using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Procurement.Sync.Clients;
using Procurement.Sync.Mapping;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

public sealed class StatusSyncStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SapClient sap,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<StatusSyncStage> logger)
{
    public const string Stream = "sap-status";

    // Returns an issue description when polling stopped early.
    public async Task<string?> RunAsync(RunContext run, CancellationToken ct)
    {
        DateTime? watermark;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            watermark = (await db.Checkpoints.FindAsync([Stream], ct))?.Watermark;
        }
        var since = watermark - options.Value.StatusSyncOverlap;
        string? nextLink = null;
        var applied = 0;

        do
        {
            var page = await sap.GetChangesAsync(since, nextLink, 100, ct);
            if (!page.IsSuccess)
            {
                logger.StatusPollingStopped(page.Status, page.Error);
                return $"SAP status polling stopped early ({page.Status}); it resumes from the last watermark next run";
            }

            var (items, link) = page.Value;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var numbers = items.Select(i => i.PurchaseRequisition).ToList();
            var linked = await db.Requisitions.Where(r => r.SapNumber != null && numbers.Contains(r.SapNumber)).ToListAsync(ct);
            // A corrupted mapping can link two records to one SAP number. Only the one SAP names as its reference gets
            // the status; the others are reported instead of breaking every status poll.
            var records = new Dictionary<string, RequisitionRecord>(StringComparer.Ordinal);
            foreach (var group in linked.GroupBy(r => r.SapNumber!))
            {
                var pr = items.First(i => i.PurchaseRequisition == group.Key);
                var owner = group.FirstOrDefault(r => r.SourceId == pr.ExternalReference) ?? (group.Count() == 1 ? group.First() : null);
                if (owner is not null) records[group.Key] = owner;
                foreach (var other in group.Where(r => r != owner))
                    Ledger.Event(db, run, other.SourceId, RunEventKind.Conflict,
                        $"Linked to SAP PR {group.Key}, which belongs to '{pr.ExternalReference}'; status not applied (mapping integrity)", time.GetUtcNow().UtcDateTime);
            }
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var pr in items)
            {
                if (records.TryGetValue(pr.PurchaseRequisition, out var record) && !SapMapper.IsKnownStatus(pr))
                {
                    Ledger.Event(db, run, record.SourceId, RunEventKind.StatusIgnored,
                        $"SAP PR {pr.PurchaseRequisition} has unrecognised status codes (processing '{pr.ProcessingStatus}', release '{pr.ReleaseStatus}'); kept {record.ErpStatus}", now);
                }
                else if (records.TryGetValue(pr.PurchaseRequisition, out record))
                {
                    record.SapETag = pr.ETag ?? record.SapETag;
                    if (ErpStatusApplier.Apply(db, run, record, SapMapper.ToErpStatus(pr), pr.LastChangeDateTime, SapMapper.PurchaseOrderOf(pr), now))
                    {
                        applied++;
                    }
                }
                if (watermark is null || pr.LastChangeDateTime > watermark) watermark = pr.LastChangeDateTime;
            }

            var checkpoint = await db.Checkpoints.FindAsync([Stream], ct);
            if (checkpoint is null)
            {
                checkpoint = new Checkpoint { Stream = Stream };
                db.Checkpoints.Add(checkpoint);
            }
            checkpoint.Watermark = watermark;
            checkpoint.RunId = run.RunId;
            checkpoint.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            nextLink = link;
        }
        while (nextLink is not null);

        logger.StatusChangesApplied(applied);
        return null;
    }
}
