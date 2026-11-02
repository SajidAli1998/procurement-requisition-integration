using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Mocks.SapApi;
using Mocks.Shared;
using Mocks.Shared.Faults;
using Scalar.AspNetCore;

const string EntitySet = "/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/A_PurchaseRequisitionHeader";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMockInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<SapStore>();
builder.Services.AddSingleton<IdempotencyStore>();
// SAP payloads use PascalCase property names.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);

var app = builder.Build();
app.UseMockInfrastructure();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapFaultAdmin();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = null };

app.MapPost(EntitySet, async (HttpContext context, SapStore store, IdempotencyStore idempotency, FaultInjector faults) =>
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(context.RequestAborted);

        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var useKey = faults.Settings.HonorIdempotencyKey && !string.IsNullOrWhiteSpace(key);
        if (useKey)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            var (claim, stored) = idempotency.TryClaim(key, fingerprint);
            switch (claim)
            {
                case IdempotencyStore.Claim.Replay:
                    context.Response.Headers["Idempotent-Replayed"] = "true";
                    if (stored!.ETag is not null) context.Response.Headers.ETag = stored.ETag;
                    if (stored.Location is not null) context.Response.Headers.Location = stored.Location;
                    return Results.Content(stored.Body, "application/json", Encoding.UTF8, stored.StatusCode);
                case IdempotencyStore.Claim.Mismatch:
                    return ODataError.Create(422, "IDEMPOTENCY/MISMATCH", "Idempotency-Key was already used with a different request body");
                case IdempotencyStore.Claim.InFlight:
                    return ODataError.Create(409, "IDEMPOTENCY/IN_PROGRESS", "A request with this Idempotency-Key is still being processed");
            }
        }

        PurchaseRequisitionInput? input;
        try
        {
            input = JsonSerializer.Deserialize<PurchaseRequisitionInput>(raw, json);
        }
        catch (JsonException ex)
        {
            if (useKey) idempotency.Release(key);
            return ODataError.Create(400, "CX_SXML_PARSE_ERROR", "Malformed request payload", [ex.Message]);
        }

        var errors = input is null ? ["Request body is empty"] : SapStore.Validate(input, isCreate: true);
        if (errors.Count > 0)
        {
            if (useKey) idempotency.Release(key);
            return ODataError.Create(400, "06/402", errors[0], errors);
        }

        var created = store.Create(input!);
        var location = $"{EntitySet}('{created.PurchaseRequisition}')";
        var etag = ETags.For(created.Version);
        // Serialize once so an idempotent replay is byte-for-byte the original response.
        var payload = JsonSerializer.Serialize(created, json);
        if (useKey)
        {
            idempotency.Complete(key, new IdempotencyStore.StoredResponse(201, payload, etag, location));
        }
        context.Response.Headers.ETag = etag;
        context.Response.Headers.Location = location;
        return Results.Content(payload, "application/json", Encoding.UTF8, 201);
    })
    .WithTags("PurchaseRequisition")
    .WithSummary("Create a purchase requisition (deep insert with items). Honors Idempotency-Key unless disabled.");

app.MapGet(EntitySet, (SapStore store, HttpRequest request,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "$skiptoken")] string? skipToken) =>
    {
        var size = top ?? 50;
        if (size is < 1 or > 200) return ODataError.Create(400, "/IWBEP/CM_MGW_RT/022", "$top must be between 1 and 200");

        if (filter is not null && Filters.ExternalReference().Match(filter) is { Success: true } byRef)
        {
            return Results.Json(new Dictionary<string, object> { ["value"] = store.FindByExternalReference(byRef.Groups[1].Value) });
        }

        DateTime? since = null;
        if (filter is not null)
        {
            var m = Filters.ChangedSince().Match(filter);
            if (!m.Success || !DateTimeOffset.TryParse(m.Groups[1].Value, out var parsed))
                return ODataError.Create(400, "/IWBEP/CM_MGW_RT/021",
                    "Only \"ExternalReference eq '...'\" and \"LastChangeDateTime gt <ISO-8601>\" filters are supported by this mock");
            since = parsed.UtcDateTime;
        }

        KeysetCursor? after = null;
        if (skipToken is not null)
        {
            if (!KeysetCursor.TryDecode(skipToken, out var decoded)) return ODataError.Create(400, "/IWBEP/CM_MGW_RT/023", "Invalid $skiptoken");
            after = decoded;
        }

        var (items, next) = store.ChangedSince(since, after, size);
        var body = new Dictionary<string, object> { ["value"] = items };
        if (next is not null)
        {
            var query = new List<string>();
            if (filter is not null) query.Add("$filter=" + Uri.EscapeDataString(filter));
            query.Add($"$top={size}");
            query.Add("$skiptoken=" + next.Value.Encode());
            body["@odata.nextLink"] = $"{EntitySet}?{string.Join('&', query)}";
        }
        return Results.Json(body);
    })
    .WithTags("PurchaseRequisition")
    .WithSummary("Query requisitions by ExternalReference, or list changes since a timestamp (paged via @odata.nextLink).");

app.MapGet(EntitySet + "('{number}')", (string number, SapStore store, HttpResponse response) =>
    {
        if (store.Get(number) is not { } pr) return ODataError.Create(404, "/IWBEP/CM_MGW_RT/020", $"Purchase requisition {number} not found");
        response.Headers.ETag = ETags.For(pr.Version);
        return Results.Json(pr);
    })
    .WithTags("PurchaseRequisition");

app.MapMethods(EntitySet + "('{number}')", ["PATCH"], async (string number, HttpRequest request, HttpResponse response, SapStore store) =>
    {
        PurchaseRequisitionInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<PurchaseRequisitionInput>(request.Body, json, request.HttpContext.RequestAborted);
        }
        catch (JsonException ex)
        {
            return ODataError.Create(400, "CX_SXML_PARSE_ERROR", "Malformed request payload", [ex.Message]);
        }
        if (input is null) return ODataError.Create(400, "CX_SXML_PARSE_ERROR", "Request body is empty");

        var errors = SapStore.Validate(input, isCreate: false);
        if (errors.Count > 0) return ODataError.Create(400, "06/402", errors[0], errors);

        var (outcome, _, version) = store.Patch(number, request, input);
        response.Headers.ETag = ETags.For(version);
        return outcome switch
        {
            SapStore.PatchOutcome.NotFound => ODataError.Create(404, "/IWBEP/CM_MGW_RT/020", $"Purchase requisition {number} not found"),
            SapStore.PatchOutcome.PreconditionMissing => ODataError.Create(428, "/IWBEP/CM_MGW_RT/025", "If-Match header is required"),
            SapStore.PatchOutcome.PreconditionFailed => ODataError.Create(412, "/IWBEP/CM_MGW_RT/024", "The entity was changed by another user; reload and retry"),
            SapStore.PatchOutcome.Locked => ODataError.Create(409, "06/RELEASED", $"Purchase requisition {number} is released; content changes are not allowed"),
            _ => Results.NoContent(),
        };
    })
    .WithTags("PurchaseRequisition")
    .WithSummary("Update header fields or replace items. Requires If-Match; rejected once released.");

var admin = app.MapGroup("/_admin").WithTags("Admin");
admin.MapPost("/advance", (SapStore store, int? rejectEvery) => Results.Ok(new { moved = store.AdvanceAll(rejectEvery ?? 7) }));
admin.MapPost("/requisitions/{number}/lifecycle", (string number, LifecycleChange change, SapStore store) =>
    store.SetLifecycle(number, change.Lifecycle) is { } pr ? Results.Json(pr) : Results.NotFound());
admin.MapPost("/requisitions/{number}/touch", (string number, SapStore store) =>
    store.Touch(number) is { } pr ? Results.Json(pr) : Results.NotFound());
admin.MapGet("/stats", (SapStore store) => Results.Ok(store.Stats()));
admin.MapPost("/reset", (SapStore store, IdempotencyStore idempotency) =>
{
    store.Reset();
    idempotency.Reset();
    return Results.NoContent();
});

app.Run();

namespace Mocks.SapApi
{
    public sealed record LifecycleChange(SapLifecycle Lifecycle);

    internal static partial class Filters
    {
        [GeneratedRegex(@"^\s*ExternalReference\s+eq\s+'([^']+)'\s*$")]
        public static partial Regex ExternalReference();

        [GeneratedRegex(@"^\s*LastChangeDateTime\s+gt\s+(?:datetimeoffset')?([^'\s]+)'?\s*$")]
        public static partial Regex ChangedSince();
    }

    /// <summary>Marker type for WebApplicationFactory in tests.</summary>
    public sealed class SapApiMarker;
}
