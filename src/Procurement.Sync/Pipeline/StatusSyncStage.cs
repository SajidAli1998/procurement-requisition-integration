using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Procurement.Sync.Clients;
using Procurement.Sync.Mapping;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

/// <summary>
/// Polls SAP's change feed since the last watermark and applies procurement statuses to local records.
/// SAP owns status, so this only ever moves a status forward; it never touches requisition content.
/// </summary>
public sealed class StatusSyncStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SapClient sap,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<StatusSyncStage> logger)
{
    public const string Stream = "sap-status";

    /// <returns>An issue description if polling stopped early, otherwise null.</returns>
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
                logger.LogWarning("Status polling stopped: {Status} {Error}", page.Status, page.Error);
                return $"SAP status polling stopped early ({page.Status}); it resumes from the last watermark next run";
            }

            var (items, link) = page.Value;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var numbers = items.Select(i => i.PurchaseRequisition).ToList();
            var records = await db.Requisitions.Where(r => r.SapNumber != null && numbers.Contains(r.SapNumber)).ToDictionaryAsync(r => r.SapNumber!, ct);
            var now = time.GetUtcNow().UtcDateTime;
            foreach (var pr in items)
            {
                if (records.TryGetValue(pr.PurchaseRequisition, out var record))
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

        logger.LogInformation("Status sync applied {Count} status change(s)", applied);
        return null;
    }
}
