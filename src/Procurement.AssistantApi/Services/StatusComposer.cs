using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Procurement.AssistantApi.Model;
using Procurement.Contracts;
using Procurement.Sync.Store;

namespace Procurement.AssistantApi.Services;

// Approval state comes from the source system, transfer and ERP status from the integration store.
// The wording is meant to be relayed to the user as it is.
public sealed class StatusComposer(IDbContextFactory<SyncDbContext> dbFactory, ILogger<StatusComposer> logger)
{
    private sealed record IntegrationState(RequisitionRecord? Record, List<StatusHistoryEntry> History, QuarantineEntry? OpenIssue);

    public async Task<RequisitionView> ComposeAsync(JsonObject source, CancellationToken ct) =>
        (await ComposeManyAsync([source], ct))[0];

    public async Task<List<RequisitionView>> ComposeManyAsync(IReadOnlyList<JsonObject> sources, CancellationToken ct)
    {
        var ids = sources.Select(s => s["id"]?.GetValue<string>()).OfType<string>().ToList();
        var states = await LoadIntegrationStateAsync(ids, ct);
        return sources.Select(s =>
        {
            var id = s["id"]?.GetValue<string>() ?? "";
            return Compose(s, states.GetValueOrDefault(id) ?? new IntegrationState(null, [], null));
        }).ToList();
    }

    private async Task<Dictionary<string, IntegrationState>> LoadIntegrationStateAsync(List<string> ids, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var records = await db.Requisitions.AsNoTracking().Where(r => ids.Contains(r.SourceId)).ToListAsync(ct);
            var history = await db.StatusHistory.AsNoTracking().Where(h => ids.Contains(h.SourceId)).ToListAsync(ct);
            var issues = await db.Quarantine.AsNoTracking().Where(q => ids.Contains(q.SourceKey) && q.ResolvedAt == null).ToListAsync(ct);
            return ids.ToDictionary(id => id, id => new IntegrationState(
                records.FirstOrDefault(r => r.SourceId == id),
                history.Where(h => h.SourceId == id).OrderBy(h => h.Id).ToList(),
                issues.FirstOrDefault(q => q.SourceKey == id)));
        }
        catch (DbException ex)
        {
            // No sync has run yet, so there is no store; use the source data alone.
            logger.StoreUnavailable(ex);
            return [];
        }
    }

    private static RequisitionView Compose(JsonObject s, IntegrationState state)
    {
        var sourceStatus = Str(s, "status");
        var erpSync = s["erpSync"] as JsonObject;
        var erpStatus = state.Record?.ErpStatus
            ?? (Enum.TryParse<ErpStatus>(Str(erpSync, "status"), out var parsed) ? parsed : null);
        var erpReference = state.Record?.SapNumber ?? Str(erpSync, "reference");
        var purchaseOrder = state.Record?.PurchaseOrder;

        var (status, reason, next) = (sourceStatus, erpStatus, state.OpenIssue) switch
        {
            ("Draft" or "PendingApproval", _, _) => (RequestStatus.PendingApproval,
                "Submitted and waiting for approval by an authorised approver.",
                "An approver reviews it. The user can withdraw it until it is approved."),
            ("Rejected", _, _) => (RequestStatus.RejectedByApprover,
                "The approver rejected this request.",
                "Nothing further happens. Raise a new request if it is still needed."),
            ("Cancelled", null, _) => (RequestStatus.Withdrawn,
                "The request was withdrawn before it reached the ERP system.",
                "Nothing further happens."),
            (_, _, { } issue) => (RequestStatus.NeedsAttention,
                $"The request could not be processed automatically: {issue.Summary}",
                "The procurement team has been notified. No action is needed from the assistant; check back later."),
            (_, ErpStatus.Created or ErpStatus.InRelease, _) => (RequestStatus.TransferredToErp,
                $"Approved and created in SAP as purchase requisition {erpReference}.",
                "Purchasing releases it in SAP; a purchase order follows."),
            (_, ErpStatus.Released, _) => (RequestStatus.ReleasedInErp,
                $"Released in SAP (requisition {erpReference}).",
                "Purchasing creates a purchase order with a supplier."),
            (_, ErpStatus.PoCreated, _) => (RequestStatus.PurchaseOrderCreated,
                $"Purchase order {purchaseOrder} was created from SAP requisition {erpReference}.",
                "The supplier delivers; the requisition closes once fully processed."),
            (_, ErpStatus.Closed, _) => (RequestStatus.Closed,
                $"Fully processed in SAP (requisition {erpReference}).",
                "Nothing further happens."),
            (_, ErpStatus.Rejected, _) => (RequestStatus.RejectedInErp,
                $"Purchasing rejected SAP requisition {erpReference} during release.",
                "Contact procurement for the reason, or raise a new request."),
            _ => (RequestStatus.Approved,
                "Approved and waiting to be transferred to SAP.",
                "The integration transfers approved requests automatically, usually within minutes."),
        };

        var timeline = new List<TimelineEntry>();
        if (Date(s, "createdAt") is { } created) timeline.Add(new(created, "Submitted"));
        if (Date(s, "approvedAt") is { } approved) timeline.Add(new(approved, "Approved"));
        timeline.AddRange(state.History.Select(h => new TimelineEntry(h.ChangedAt, $"ERP status {h.ToStatus}")));

        var lines = (s["lines"] as JsonArray ?? []).OfType<JsonObject>().Select(l => new ViewLine(
            Num(l, "lineNo") is { } n ? (int)n : 0, Str(l, "materialCode"), Str(l, "description"),
            Num(l, "quantity"), Str(l, "uom"), Num(l, "unitPrice"))).ToList();

        return new RequisitionView(
            Str(s, "id") ?? "", status, reason, next, erpReference, purchaseOrder,
            Str(s, "costCenter"), Str(s, "currency"), Num(s, "totalAmount"),
            DateOnly.TryParse(Str(s, "neededBy"), CultureInfo.InvariantCulture, out var needed) ? needed : null,
            Str(s, "justification"), lines, Date(s, "updatedAt") ?? DateTime.MinValue,
            timeline.OrderBy(t => t.At).ToList());
    }

    private static string? Str(JsonObject? o, string name) => o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static decimal? Num(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : null;

    private static DateTime? Date(JsonObject o, string name) =>
        DateTime.TryParse(Str(o, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

internal static partial class StatusComposerLog
{
    [LoggerMessage(LogLevel.Debug, "Integration store unavailable")]
    public static partial void StoreUnavailable(this ILogger logger, Exception exception);
}
