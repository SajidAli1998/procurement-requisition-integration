using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Procurement.Contracts;
using Procurement.Sync.Clients;
using Procurement.Sync.Http;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Store;

namespace Procurement.Sync.Reporting;

/// <summary>
/// Builds the reconciliation report from local state and, when enabled, a full scan of SAP.
/// The invariants are the evidence for "nothing was lost and nothing was duplicated".
/// </summary>
public sealed class ReportBuilder(
    IDbContextFactory<SyncDbContext> dbFactory,
    SapClient sap,
    RunMetrics metrics,
    IOptions<SyncOptions> options,
    TimeProvider time)
{
    private static readonly RunEventKind[] Notable =
    [
        RunEventKind.Quarantined, RunEventKind.QuarantineResolved, RunEventKind.Amended, RunEventKind.InDoubt,
        RunEventKind.InDoubtResolved, RunEventKind.Conflict, RunEventKind.SourceChangedAfterTransfer,
        RunEventKind.RetryExhausted, RunEventKind.RetryScheduled, RunEventKind.StatusIgnored,
    ];

    private static readonly RecordState[] Accounted =
    [
        RecordState.Synced, RecordState.Quarantined, RecordState.Dismissed, RecordState.Queued, RecordState.RetryPending,
        RecordState.InDoubt, RecordState.Sending,
    ];

    public async Task<ReconciliationReport> BuildAsync(string runId, string trigger, DateTime startedAt, string status, string? message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var records = await db.Requisitions.AsNoTracking().ToListAsync(ct);
        var open = await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null).OrderBy(q => q.Reason).ThenBy(q => q.SourceKey).ToListAsync(ct);
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == runId).OrderBy(e => e.Id).ToListAsync(ct);

        var invariants = new List<InvariantResult> { CheckApprovedAccountedFor(records, open) };
        var (sapInvariants, totals, excluded) = await CheckAgainstSapAsync(records, ct);
        invariants.AddRange(sapInvariants);
        var writebackPending = records.Count(r => r.WritebackPending);
        invariants.Add(writebackPending == 0
            ? new("Source shows the current SAP status", InvariantOutcome.Pass, "All ERP statuses are written back to the source.")
            : new("Source shows the current SAP status", InvariantOutcome.Warn, $"{writebackPending} status write-back(s) pending; retried next run."));

        return new ReconciliationReport
        {
            RunId = runId,
            Trigger = trigger,
            StartedAt = startedAt,
            GeneratedAt = time.GetUtcNow().UtcDateTime,
            Status = status,
            Message = message,
            RunCounts = events.GroupBy(e => e.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            StateTotals = records.GroupBy(r => r.State).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            Transport = metrics.Snapshot(),
            RetryWaitSeconds = Math.Round(metrics.TotalRetryWait.TotalSeconds, 2),
            Invariants = invariants,
            Totals = totals,
            ExcludedFromTotals = excluded,
            OpenFailures = open.Select(q => new FailureRow(q.SourceKey, q.SourceVersion, q.Stage.ToString(), q.Reason.ToString(), q.Summary,
                SuggestedActions.For(q.Reason, q.SourceKey), q.FirstSeenAt, q.LastSeenAt, q.Occurrences)).ToList(),
            NotableChanges = events.Where(e => Notable.Contains(e.Kind)).Select(e => new ChangeRow(e.SourceKey, e.Kind.ToString(), e.Detail)).ToList(),
            Records = RecordOutcomes.Build(events, records.ToDictionary(r => r.SourceId)),
            Pending = records.Where(r => r.State is RecordState.Queued or RecordState.RetryPending or RecordState.InDoubt or RecordState.Sending)
                .OrderBy(r => r.SourceId).Select(r => new PendingRow(r.SourceId, r.State.ToString(), r.Attempts, r.NextAttemptAt, r.LastError)).ToList(),
        };
    }

    private static InvariantResult CheckApprovedAccountedFor(List<RequisitionRecord> records, List<QuarantineEntry> open)
    {
        const string name = "Every approved requisition is accounted for";
        var approved = records.Where(r => r.SourceStatus == "Approved" || r.SapNumber is not null).ToList();
        var openKeys = open.Select(q => q.SourceKey).ToHashSet();
        var lost = approved.Where(r => !Accounted.Contains(r.State)).Select(r => r.SourceId).ToList();
        var unexplained = approved.Where(r => r.State == RecordState.Quarantined && !openKeys.Contains(r.SourceId)).Select(r => r.SourceId).ToList();
        var breakdown = string.Join(", ", approved.GroupBy(r => r.State).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));

        if (lost.Count > 0 || unexplained.Count > 0)
        {
            return new(name, InvariantOutcome.Fail,
                $"Unaccounted: {string.Join(", ", lost.Concat(unexplained).Take(20))}. Breakdown: {breakdown}.");
        }
        return new(name, InvariantOutcome.Pass, $"{approved.Count} approved: {breakdown}.");
    }

    private async Task<(List<InvariantResult>, List<CurrencyTotal>, int)> CheckAgainstSapAsync(List<RequisitionRecord> records, CancellationToken ct)
    {
        string[] names =
        [
            "No duplicate requisitions in SAP",
            "Every synced requisition exists in SAP",
            "Every SAP requisition is linked to a source requisition",
            "Amounts match between source and SAP",
        ];
        if (!options.Value.VerifyAgainstSap)
        {
            return (names.Select(n => new InvariantResult(n, InvariantOutcome.Skipped, "SAP verification is disabled.")).ToList(), [], 0);
        }

        var sapRows = new List<SapRequisitionDto>();
        string? nextLink = null;
        do
        {
            var page = await sap.GetChangesAsync(null, nextLink, 200, ct);
            if (!page.IsSuccess)
            {
                var why = $"SAP could not be scanned ({page.Status}); re-run reconciliation when SAP is available.";
                return (names.Select(n => new InvariantResult(n, InvariantOutcome.Skipped, why)).ToList(), [], 0);
            }
            sapRows.AddRange(page.Value.Items);
            nextLink = page.Value.NextLink;
        }
        while (nextLink is not null);

        var results = new List<InvariantResult>();

        var duplicates = sapRows.GroupBy(p => p.ExternalReference).Where(g => g.Count() > 1).ToList();
        results.Add(duplicates.Count == 0
            ? new(names[0], InvariantOutcome.Pass, $"{sapRows.Count} SAP requisitions, each with a unique external reference.")
            : new(names[0], InvariantOutcome.Fail, string.Join("; ", duplicates.Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => p.PurchaseRequisition))}"))));

        var sapNumbers = sapRows.Select(p => p.PurchaseRequisition).ToHashSet();
        var synced = records.Where(r => r.SapNumber is not null).ToList();
        var missing = synced.Where(r => !sapNumbers.Contains(r.SapNumber!)).Select(r => $"{r.SourceId}→{r.SapNumber}").ToList();
        results.Add(missing.Count == 0
            ? new(names[1], InvariantOutcome.Pass, $"All {synced.Count} linked SAP numbers found.")
            : new(names[1], InvariantOutcome.Fail, $"Missing in SAP: {string.Join(", ", missing.Take(20))}"));

        var linked = synced.Select(r => r.SapNumber!).ToHashSet();
        var orphans = sapRows.Where(p => !linked.Contains(p.PurchaseRequisition)).Select(p => $"{p.PurchaseRequisition} ({p.ExternalReference})").ToList();
        results.Add(orphans.Count == 0
            ? new(names[2], InvariantOutcome.Pass, "No unlinked SAP requisitions.")
            : new(names[2], InvariantOutcome.Warn, $"Not linked locally (adopted on the next run if the source still has them): {string.Join(", ", orphans.Take(20))}"));

        // Compare like with like: only requisitions whose current content is the version SAP holds.
        var compared = new List<(string SapNumber, string Currency, decimal Amount)>();
        foreach (var r in synced)
        {
            var req = r.CanonicalJson is null ? null : JsonSerializer.Deserialize<Requisition>(r.CanonicalJson, ExtractStage.PayloadJson);
            if (req is not null && req.Version == r.SyncedSourceVersion)
            {
                compared.Add((r.SapNumber!, req.Currency, req.Lines.Sum(l => l.Quantity * l.UnitPrice)));
            }
        }
        var excluded = synced.Count - compared.Count;
        var comparedNumbers = compared.Select(c => c.SapNumber).ToHashSet();
        var sapCompared = sapRows.Where(p => comparedNumbers.Contains(p.PurchaseRequisition))
            .Select(p => (Currency: p.Items.FirstOrDefault()?.PurReqnItemCurrency ?? "", Amount: p.Items.Sum(i => i.RequestedQuantity * i.PurchaseRequisitionPrice)))
            .ToList();

        var totals = compared.Select(l => l.Currency).Concat(sapCompared.Select(s => s.Currency)).Distinct().OrderBy(c => c)
            .Select(c => new CurrencyTotal(c,
                compared.Count(l => l.Currency == c), Math.Round(compared.Where(l => l.Currency == c).Sum(l => l.Amount), 2),
                sapCompared.Count(s => s.Currency == c), Math.Round(sapCompared.Where(s => s.Currency == c).Sum(s => s.Amount), 2)))
            .ToList();
        var mismatched = totals.Where(t => !t.Matches).ToList();
        var note = excluded > 0 ? $" {excluded} requisition(s) excluded because SAP holds an earlier version (see open failures)." : "";
        results.Add(mismatched.Count == 0
            ? new(names[3], InvariantOutcome.Pass, string.Join("; ", totals.Select(t => $"{t.Currency} {t.LocalAmount:N2} in {t.LocalCount}")) + "." + note)
            : new(names[3], InvariantOutcome.Fail, string.Join("; ", mismatched.Select(t => $"{t.Currency}: source {t.LocalAmount:N2} ({t.LocalCount}) vs SAP {t.SapAmount:N2} ({t.SapCount})")) + note));

        return (results, totals, excluded);
    }
}
