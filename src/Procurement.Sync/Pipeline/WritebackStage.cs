using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Procurement.Contracts;
using Procurement.Sync.Clients;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

// Guarded by If-Match. A 412 means someone edited the requisition meanwhile: re-read and send only the status again.
public sealed class WritebackStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SourceClient source,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<WritebackStage> logger)
{
    private const int MaxConflictRetries = 3;

    public async Task<string?> RunAsync(RunContext run, CancellationToken ct)
    {
        List<string> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            pending = await db.Requisitions
                .Where(r => r.WritebackPending && r.SapNumber != null && r.ErpStatus != null)
                .Select(r => r.SourceId).ToListAsync(ct);
        }

        var failed = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = options.Value.Parallelism, CancellationToken = ct },
            async (id, token) =>
            {
                if (!await WriteBackAsync(run, id, token)) Interlocked.Increment(ref failed);
            });

        if (failed > 0) logger.WritebacksPending(failed);
        return failed > 0 ? $"{failed} status write-back(s) to the source are pending and will be retried" : null;
    }

    private async Task<bool> WriteBackAsync(RunContext run, string id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.Requisitions.FindAsync([id], ct);
        if (record is null || !record.WritebackPending) return true;

        var update = new ErpStatusWriteback(record.SapNumber!, record.ErpStatus!.Value.ToString(),
            new DateTimeOffset(record.ErpStatusAt ?? time.GetUtcNow().UtcDateTime, TimeSpan.Zero),
            Describe(record));

        try
        {
            for (var attempt = 1; attempt <= MaxConflictRetries; attempt++)
            {
                var current = await source.GetAsync(id, ct);
                if (current is null)
                {
                    record.WritebackPending = false;
                    Ledger.Event(db, run, id, RunEventKind.Conflict, "Requisition no longer exists in the source; status not written back", Now);
                    break;
                }

                var result = await source.PutErpStatusAsync(id, current.Value.ETag, update, ct);
                if (result.Status == WritebackStatus.Ok)
                {
                    record.WritebackPending = false;
                    Ledger.Event(db, run, id, RunEventKind.WrittenBack, $"{update.ErpStatus} ({update.ErpReference})", Now);
                    break;
                }
                if (result.Status == WritebackStatus.PreconditionFailed)
                {
                    Ledger.Event(db, run, id, RunEventKind.Conflict, "Source requisition changed during write-back; re-reading", Now);
                    continue;
                }
                Ledger.Event(db, run, id, RunEventKind.RetryScheduled, $"Write-back failed: {result.Error}", Now);
                break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Ledger.Event(db, run, id, RunEventKind.RetryScheduled, $"Write-back failed: {ex.Message}", Now);
        }

        await db.SaveChangesAsync(ct);
        return !record.WritebackPending;
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static string Describe(RequisitionRecord r) => r.ErpStatus switch
    {
        ErpStatus.Created => $"Created in SAP as purchase requisition {r.SapNumber}",
        ErpStatus.InRelease => "Awaiting release (approval) in SAP",
        ErpStatus.Released => "Released in SAP; awaiting purchase order",
        ErpStatus.PoCreated => $"Purchase order {r.PurchaseOrder} created",
        ErpStatus.Closed => "Fully processed and closed in SAP",
        ErpStatus.Rejected => "Rejected during release in SAP",
        _ => "",
    };
}
