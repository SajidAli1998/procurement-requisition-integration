using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Procurement.AssistantApi;
using Procurement.AssistantApi.Infrastructure;
using Procurement.AssistantApi.Model;
using Procurement.AssistantApi.Services;
using Procurement.Sync.Store;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AssistantOptions>(builder.Configuration.GetSection(AssistantOptions.Section));
var auth = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
if (auth.SigningKey.Length < 32) throw new InvalidOperationException("Auth:SigningKey must be at least 32 characters.");
builder.Services.AddSingleton(auth);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<RequestValidator>();
builder.Services.AddSyncStore(builder.Configuration);
builder.Services.AddTransient<StatusComposer>();
builder.Services.AddHttpClient<SourceRequisitionsClient>((sp, http) =>
        http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AssistantOptions>>().Value.SourceBaseUrl))
    // Never blindly repeat a create: an unknown outcome is resolved by looking the request up instead.
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Numbers must be JSON numbers; this also keeps the published schema to "type: number".
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddAssistantOpenApi();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = auth.Issuer,
        ValidAudience = auth.Audience,
        IssuerSigningKey = DevTokens.Key(auth),
        NameClaimType = "sub",
    };
    o.Events = new JwtBearerEvents
    {
        OnChallenge = async context =>
        {
            context.HandleResponse();
            await Problems.Create(401, ErrorCodes.Unauthorized, "A valid bearer token is required",
                suggestedAction: "Ask the user to sign in again; do not retry with the same token.").ExecuteAsync(context.HttpContext);
        },
        OnForbidden = context => Problems.Create(403, ErrorCodes.Forbidden, "The token lacks the scope this operation needs",
            suggestedAction: "This action is not permitted for the assistant. Tell the user to do it themselves.").ExecuteAsync(context.HttpContext),
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Scopes.Draft, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasScope(c.User, Scopes.Draft)))
    .AddPolicy(Scopes.Read, p => p.RequireAuthenticatedUser().RequireAssertion(c => HasScope(c.User, Scopes.Read)));

builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("per-user", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<IOptions<AssistantOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    o.OnRejected = async (context, _) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? (int)Math.Ceiling(wait.TotalSeconds) : 60;
        context.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Problems.Create(429, ErrorCodes.RateLimited, "Too many requests", retryable: true, retryAfterSeconds: retryAfter,
            suggestedAction: $"Wait {retryAfter} seconds before the next call.").ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).ExcludeFromDescription();

var strictJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = { new JsonStringEnumConverter() },
};
var viewJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

var v1 = app.MapGroup("/v1").RequireRateLimiting("per-user");

v1.MapPost("/requisition-requests:validate", async (HttpRequest request, RequestValidator validator) =>
    {
        var (body, _, error) = await ReadStrictAsync<CreateRequisitionRequest>(request);
        return error ?? Results.Ok(validator.Validate(body!));
    })
    .RequireAuthorization(Scopes.Draft)
    .WithMetadata(new AgentToolHints("validateRequisitionRequest", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Draft))
    .WithSummary("Check a draft request without creating anything")
    .WithDescription("Returns every problem with its field, code and a suggestion, plus the estimated total at catalogue prices. Call this before createRequisitionRequest.")
    .Accepts<CreateRequisitionRequest>("application/json")
    .Produces<ValidationResult>()
    .ProducesProblem(400);

v1.MapPost("/requisition-requests", async (HttpRequest request, HttpResponse response, ClaimsPrincipal user, RequestValidator validator,
        IdempotencyStore idempotency, SourceRequisitionsClient source, StatusComposer composer, IOptions<AssistantOptions> options, CancellationToken ct) =>
    {
        var key = request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Problems.Create(400, ErrorCodes.IdempotencyKeyRequired, "Idempotency-Key header is required (max 100 characters)",
                suggestedAction: "Generate a UUID for this user request and send it as Idempotency-Key; reuse it only to retry this same request.");

        var (body, raw, error) = await ReadStrictAsync<CreateRequisitionRequest>(request);
        if (error is not null) return error;

        var requester = user.FindFirstValue("sub")!;
        var slot = $"{requester}:{key}";
        var (claim, stored) = idempotency.TryClaim(slot, Fingerprint(raw), options.Value.IdempotencyRetention);
        switch (claim)
        {
            case IdempotencyStore.Claim.Replay:
                response.Headers["Idempotent-Replayed"] = "true";
                if (stored!.Location is not null) response.Headers.Location = stored.Location;
                if (stored.ETag is not null) response.Headers.ETag = stored.ETag;
                return Results.Content(stored.Body, "application/json", Encoding.UTF8, stored.StatusCode);
            case IdempotencyStore.Claim.Mismatch:
                return Problems.Create(422, ErrorCodes.IdempotencyKeyReused, "This Idempotency-Key was already used for a different request",
                    suggestedAction: "Use a new Idempotency-Key for a new or changed request.");
            case IdempotencyStore.Claim.InFlight:
                return Problems.Create(409, ErrorCodes.RequestInProgress, "The original request with this key is still being processed",
                    retryable: true, retryAfterSeconds: 2, suggestedAction: "Retry the same request with the same key in a few seconds.");
        }

        try
        {
            var validation = validator.Validate(body!);
            if (!validation.Valid)
            {
                idempotency.Release(slot);
                return Problems.Create(422, ErrorCodes.ValidationFailed, "The request has invalid fields", invalidFields: validation.Issues,
                    suggestedAction: "Fix each field in invalidFields (see each suggestion) and retry. Nothing was created, so the same key may be reused.");
            }

            // Look before creating: if an earlier attempt with this key reached the source but its response was lost, reuse it.
            var created = await source.FindByRequestKeyAsync(requester, key, ct)
                ?? await source.CreateAsync(ToSourceRequisition(body!, requester, key, validation), ct);
            var view = await composer.ComposeAsync(created.Body, ct);
            var location = $"/v1/requisition-requests/{view.Id}";
            var payload = JsonSerializer.Serialize(view, viewJson);
            idempotency.Complete(slot, new IdempotencyStore.StoredResponse(201, payload, location, created.ETag));
            response.Headers.Location = location;
            response.Headers.ETag = created.ETag;
            return Results.Content(payload, "application/json", Encoding.UTF8, 201);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or Polly.ExecutionRejectedException
                                   && !ct.IsCancellationRequested)
        {
            idempotency.Release(slot);
            return Problems.Create(503, ErrorCodes.UpstreamUnavailable, "The requisition system is temporarily unavailable", retryable: true,
                retryAfterSeconds: 5, suggestedAction: "Retry the same request with the same Idempotency-Key; it will not create a duplicate.");
        }
    })
    .RequireAuthorization(Scopes.Draft)
    .WithMetadata(new AgentToolHints("createRequisitionRequest", ReadOnly: false, Idempotent: true, Destructive: false, Scopes.Draft))
    .WithSummary("Raise a purchase requisition for approval")
    .WithDescription("Creates the request as PendingApproval for the signed-in user; nothing is ordered until a person approves it. Requires an Idempotency-Key header. Returns the request with its status.")
    .Accepts<CreateRequisitionRequest>("application/json")
    .Produces<RequisitionView>(201)
    .ProducesProblem(400).ProducesProblem(409).ProducesProblem(422).ProducesProblem(503);

v1.MapGet("/requisition-requests/{id}", async (string id, ClaimsPrincipal user, HttpResponse response, SourceRequisitionsClient source,
        StatusComposer composer, CancellationToken ct) =>
    {
        var found = await source.GetAsync(id, ct);
        if (found is null || found.Value.Body["requester"]?.GetValue<string>() != user.FindFirstValue("sub"))
            return NotFound(id);
        response.Headers.ETag = found.Value.ETag;
        return Results.Json(await composer.ComposeAsync(found.Value.Body, ct), viewJson);
    })
    .RequireAuthorization(Scopes.Read)
    .WithMetadata(new AgentToolHints("getRequisitionRequest", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Read))
    .WithSummary("Get the status of one of the user's requests")
    .WithDescription("Returns status, a plain-language statusReason, nextExpectedStep, the SAP reference once transferred and a timeline. The ETag header is needed to withdraw.")
    .Produces<RequisitionView>()
    .ProducesProblem(404);

v1.MapGet("/requisition-requests", async (ClaimsPrincipal user, SourceRequisitionsClient source, StatusComposer composer,
        string? cursor, int? limit, CancellationToken ct) =>
    {
        var size = Math.Clamp(limit ?? 20, 1, 50);
        var (items, next, hasMore) = await source.ListByRequesterAsync(user.FindFirstValue("sub")!, cursor, size, ct);
        var views = await composer.ComposeManyAsync(items, ct);
        return Results.Json(new RequisitionPage(views, hasMore ? next : null), viewJson);
    })
    .RequireAuthorization(Scopes.Read)
    .WithMetadata(new AgentToolHints("listRequisitionRequests", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Read))
    .WithSummary("List the user's requests, least recently changed first")
    .WithDescription("Only the signed-in user's requests are returned. Page with cursor; limit is 1-50 (default 20).")
    .Produces<RequisitionPage>();

v1.MapPost("/requisition-requests/{id}:withdraw", async (string id, HttpRequest request, HttpResponse response, ClaimsPrincipal user,
        SourceRequisitionsClient source, StatusComposer composer, CancellationToken ct) =>
    {
        var etag = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            return Problems.Create(428, ErrorCodes.PreconditionRequired, "If-Match header is required",
                suggestedAction: "Get the request first and send its ETag in If-Match, after the user confirms the withdrawal.");

        var current = await source.GetAsync(id, ct);
        if (current is null || current.Value.Body["requester"]?.GetValue<string>() != user.FindFirstValue("sub")) return NotFound(id);

        var (outcome, body, newEtag) = await source.WithdrawAsync(id, etag, ct);
        return outcome switch
        {
            WithdrawOutcome.NotFound => NotFound(id),
            WithdrawOutcome.PreconditionFailed => Problems.Create(412, ErrorCodes.VersionConflict, "The request changed since it was read",
                suggestedAction: "Get the request again, show the user its current status, and confirm before retrying."),
            WithdrawOutcome.NotAllowed => Problems.Create(409, ErrorCodes.NotWithdrawable, "Only requests awaiting approval can be withdrawn",
                suggestedAction: "Tell the user the request is already past approval; they must contact procurement to cancel it."),
            _ => WithEtag(response, newEtag, Results.Json(await composer.ComposeAsync(body!, ct), viewJson)),
        };
    })
    .RequireAuthorization(Scopes.Draft)
    .WithMetadata(new AgentToolHints("withdrawRequisitionRequest", ReadOnly: false, Idempotent: true, Destructive: true, Scopes.Draft))
    .WithSummary("Withdraw one of the user's requests that is still awaiting approval")
    .WithDescription("Requires the user's explicit confirmation and If-Match with the ETag from the latest read. Requests already approved cannot be withdrawn.")
    .Produces<RequisitionView>()
    .ProducesProblem(404).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);

var reference = v1.MapGroup("/reference").RequireAuthorization(Scopes.Read);
reference.MapGet("/materials", (string? search, int? limit) =>
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Results.Ok(ReferenceCatalogue.Materials
            .Where(m => words.All(w => m.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Code.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Clamp(limit ?? 20, 1, 50)));
    })
    .WithMetadata(new AgentToolHints("searchMaterials", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Read))
    .WithSummary("Search the material and service catalogue")
    .WithDescription("Matches all words against code and description. Use the returned code in request lines; prices shown are what will be charged.")
    .Produces<IEnumerable<MaterialInfo>>();
reference.MapGet("/cost-centers", () => Results.Ok(ReferenceCatalogue.CostCenters))
    .WithMetadata(new AgentToolHints("listCostCenters", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Read))
    .WithSummary("List cost centers with their company and currency")
    .Produces<IEnumerable<CostCenterInfo>>();
reference.MapGet("/plants", (string? companyCode) =>
        Results.Ok(ReferenceCatalogue.Plants.Where(p => companyCode is null || p.CompanyCode == companyCode)))
    .WithMetadata(new AgentToolHints("listPlants", ReadOnly: true, Idempotent: true, Destructive: false, Scopes.Read))
    .WithSummary("List receiving plants, optionally for one company")
    .Produces<IEnumerable<PlantInfo>>();

if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/token", (DevTokenRequest r, AuthOptions options) =>
            Results.Ok(new { access_token = DevTokens.Issue(options, r.User, r.Scopes ?? [Scopes.Read, Scopes.Draft]), token_type = "Bearer" }))
        .ExcludeFromDescription();
}

app.Run();

static bool HasScope(ClaimsPrincipal user, string scope) =>
    user.FindFirstValue("scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope) == true;

static string Fingerprint(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

static IResult NotFound(string id) => Problems.Create(404, ErrorCodes.NotFound, $"No request {id} for this user",
    suggestedAction: "List the user's requests to find the right id.");

static IResult WithEtag(HttpResponse response, string? etag, IResult result)
{
    if (etag is not null) response.Headers.ETag = etag;
    return result;
}

async Task<(T? Body, string Raw, IResult? Error)> ReadStrictAsync<T>(HttpRequest request) where T : class
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
    try
    {
        var body = JsonSerializer.Deserialize<T>(raw, strictJson);
        return body is null
            ? (null, raw, Problems.Create(400, ErrorCodes.InvalidRequestBody, "Request body is empty"))
            : (body, raw, null);
    }
    catch (JsonException ex)
    {
        return (null, raw, Problems.Create(400, ErrorCodes.InvalidRequestBody, "Request body does not match the schema", ex.Message,
            suggestedAction: "Send only the documented fields with the documented types; see the OpenAPI schema."));
    }
}

static JsonObject ToSourceRequisition(CreateRequisitionRequest r, string requester, string key, ValidationResult validation)
{
    var costCenter = ReferenceCatalogue.CostCenter(r.CostCenter)!;
    var lines = new JsonArray();
    for (var i = 0; i < r.Lines.Count; i++)
    {
        var m = ReferenceCatalogue.Material(r.Lines[i].MaterialCode)!;
        lines.Add(new JsonObject
        {
            ["lineNo"] = (i + 1) * 10,
            ["materialCode"] = m.Code,
            ["description"] = m.Description,
            ["materialGroup"] = m.MaterialGroup,
            ["quantity"] = r.Lines[i].Quantity,
            ["uom"] = m.Uom,
            ["unitPrice"] = m.UnitPrice,
            ["plant"] = r.Plant,
        });
    }
    return new JsonObject
    {
        ["requester"] = requester,
        ["costCenter"] = costCenter.Code,
        ["companyCode"] = costCenter.CompanyCode,
        ["currency"] = costCenter.Currency,
        ["neededBy"] = r.NeededBy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["justification"] = r.Justification.Trim(),
        ["totalAmount"] = validation.EstimatedTotal,
        ["lines"] = lines,
        ["channel"] = "AiAssistant",
        ["assistantSessionRef"] = r.AssistantSessionRef,
        ["assistantRequestKey"] = key,
        ["createdBy"] = requester,
    };
}

namespace Procurement.AssistantApi
{
    public sealed record DevTokenRequest(string User, string[]? Scopes);

    /// <summary>Marker type for WebApplicationFactory in tests.</summary>
    public sealed class AssistantApiMarker;
}
