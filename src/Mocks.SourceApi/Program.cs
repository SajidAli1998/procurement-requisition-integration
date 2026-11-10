using System.Text.Json.Nodes;
using Mocks.Shared;
using Mocks.Shared.Faults;
using Mocks.SourceApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMockInfrastructure(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<RequisitionStore>();

var app = builder.Build();
app.UseMockInfrastructure();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapFaultAdmin();

var api = app.MapGroup("/api/requisitions").WithTags("Requisitions");

api.MapGet("/", (RequisitionStore store, FaultInjector faults,
        string? status, string? requester, DateTimeOffset? updatedAfter, string? cursor, int? limit) =>
    {
        var size = limit ?? 50;
        if (size is < 1 or > 100)
            return Results.Problem(statusCode: 400, title: "limit must be between 1 and 100");

        KeysetCursor? after = null;
        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(cursor, out var decoded))
                return Results.Problem(statusCode: 400, title: "Invalid cursor");
            after = decoded;
        }

        var page = store.List(status, requester, updatedAfter?.UtcDateTime, after, size, faults.Settings.PageOverlap);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor, hasMore = page.HasMore });
    })
    .WithSummary("List requisitions in change order (updatedAt, id) with a durable resume cursor.");

api.MapGet("/{id}", (string id, RequisitionStore store, HttpResponse response) =>
    {
        if (store.Get(id) is not { } found)
            return Results.Problem(statusCode: 404, title: $"Requisition {id} not found");
        response.Headers.ETag = ETags.For(found.Version);
        return Results.Ok(found.Body);
    })
    .WithSummary("Get one requisition; the ETag header carries its version.");

api.MapPut("/{id}/erp-status", (string id, ErpStatusUpdate update, RequisitionStore store, HttpRequest request, HttpResponse response) =>
    {
        if (string.IsNullOrWhiteSpace(update.ErpReference) || string.IsNullOrWhiteSpace(update.ErpStatus))
            return Results.Problem(statusCode: 400, title: "erpReference and erpStatus are required");

        var (outcome, body, version) = store.UpdateErpStatus(id, request, update);
        switch (outcome)
        {
            case UpdateOutcome.NotFound:
                return Results.Problem(statusCode: 404, title: $"Requisition {id} not found");
            case UpdateOutcome.PreconditionMissing:
                return ETags.PreconditionProblem(PreconditionResult.Missing, version, response);
            case UpdateOutcome.PreconditionFailed:
                return ETags.PreconditionProblem(PreconditionResult.Failed, version, response);
            default:
                response.Headers.ETag = ETags.For(version);
                return Results.Ok(body);
        }
    })
    .WithSummary("Write the ERP status back onto a requisition. Requires If-Match.");

api.MapPost("/", (JsonObject body, RequisitionStore store, HttpResponse response) =>
    {
        var (created, version) = store.Create(body);
        response.Headers.ETag = ETags.For(version);
        return Results.Created($"/api/requisitions/{created["id"]}", created);
    })
    .WithSummary("Create a requisition awaiting approval. The server assigns id, status and timestamps.");

api.MapPost("/{id}/withdraw", (string id, RequisitionStore store, HttpRequest request, HttpResponse response) =>
    {
        var (outcome, body, version) = store.Withdraw(id, request);
        response.Headers.ETag = ETags.For(version);
        return outcome switch
        {
            UpdateOutcome.NotFound => Results.Problem(statusCode: 404, title: $"Requisition {id} not found"),
            UpdateOutcome.PreconditionMissing => ETags.PreconditionProblem(PreconditionResult.Missing, version, response),
            UpdateOutcome.PreconditionFailed => ETags.PreconditionProblem(PreconditionResult.Failed, version, response),
            UpdateOutcome.NotAllowed => Results.Problem(statusCode: 409, title: "Only requisitions awaiting approval can be withdrawn"),
            _ => Results.Ok(body),
        };
    })
    .WithSummary("Withdraw a requisition that is still awaiting approval. Requires If-Match.");

var admin = app.MapGroup("/_admin").WithTags("Admin");
admin.MapGet("/seed-manifest", (RequisitionStore store) => Results.Ok(store.Manifest));
admin.MapPost("/reset", (RequisitionStore store, Dataset? dataset, int? seed, int? count) =>
{
    store.Reset(dataset ?? store.Dataset, seed ?? 42, count ?? 250);
    return Results.NoContent();
})
.WithSummary("Reload the data: dataset=Workbook (the client's workbook, default), Generated (seed, count) or Empty.");
admin.MapGet("/requisitions", (RequisitionStore store) => Results.Ok(store.Snapshot()))
    .WithSummary("Every record as stored (test evidence).");
admin.MapPut("/requisitions/{id}/raw", (string id, JsonObject body, RequisitionStore store, long? version) =>
        Results.Ok(store.Upsert(id, body, version)))
    .WithSummary("Store a record exactly as given, valid or not (test fixtures). version forces the version number.");
admin.MapDelete("/requisitions/{id}", (string id, RequisitionStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.Problem(statusCode: 404, title: $"Requisition {id} not found"));
admin.MapPost("/requisitions/{id}/hide-from-list", (string id, RequisitionStore store, bool? hidden) =>
    store.SetHiddenFromList(id, hidden ?? true) ? Results.NoContent() : Results.Problem(statusCode: 404, title: $"Requisition {id} not found"))
    .WithSummary("Test fault: the list skips this record (as a lagging index would); GET by id still finds it.");
admin.MapPost("/requisitions/{id}/edit", (string id, JsonObject patch, RequisitionStore store) =>
    store.Edit(id, patch) is { } body
        ? Results.Ok(body)
        : Results.Problem(statusCode: 404, title: $"Requisition {id} not found"));

app.Run();

namespace Mocks.SourceApi
{
    // Entry point type for WebApplicationFactory in tests.
    public sealed class SourceApiMarker;
}
