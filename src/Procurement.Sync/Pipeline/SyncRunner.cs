using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Procurement.Sync.Http;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

/// <summary>One sync cycle: extract → dispatch. Guarded by a lease so overlapping runs cannot double-process.</summary>
public sealed class SyncRunner(
    IDbContextFactory<SyncDbContext> dbFactory,
    LeaseManager leases,
    ExtractStage extract,
    DispatchStage dispatch,
    RunMetrics metrics,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<SyncRunner> logger)
{
    public const string LeaseName = "procsync";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<RunResult> RunAsync(string trigger, CancellationToken ct)
    {
        await dbFactory.EnsureSyncStoreAsync(ct);
        var runId = $"run-{Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..29];
        if (!await leases.TryAcquireAsync(LeaseName, runId, options.Value.LeaseDuration, ct))
        {
            logger.LogWarning("Another run holds the lease; skipping");
            return RunResult.SkippedRun(runId, "Another run is in progress");
        }

        try
        {
            metrics.Reset();
            var run = new RunContext(runId, Now);
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                db.Runs.Add(new SyncRun { Id = runId, StartedAt = run.StartedAt, Status = RunStatus.Running, Trigger = trigger });
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Run {RunId} started ({Trigger})", runId, trigger);

            await RecoverInterruptedAsync(run, ct);

            var issues = new List<string>();
            try
            {
                await extract.RunAsync(run, ct);
            }
            catch (Exception ex) when (IsDownstreamFailure(ex) && !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Extract stopped early");
                issues.Add($"Source read stopped early ({ex.Message}); progress up to the last checkpoint is kept");
            }

            // Dispatch still runs: everything staged before a source failure is valid and should be sent.
            var dispatched = await dispatch.RunAsync(run, ct);
            if (dispatched.CircuitOpen) issues.Add("SAP circuit breaker opened; remaining records stay queued for the next run");

            return await FinishAsync(run, issues, ct);
        }
        finally
        {
            await leases.ReleaseAsync(LeaseName, runId, CancellationToken.None);
        }
    }

    /// <summary>Records left in Sending by a crashed run may or may not have reached SAP.</summary>
    private async Task RecoverInterruptedAsync(RunContext run, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var interrupted = await db.Requisitions.Where(r => r.State == RecordState.Sending).ToListAsync(ct);
        foreach (var record in interrupted)
        {
            record.State = RecordState.InDoubt;
            record.NextAttemptAt = null;
            Ledger.Event(db, run, record.SourceId, RunEventKind.InDoubt, "Interrupted while sending in a previous run", Now);
        }
        await db.Outbox.Where(m => m.ProcessedAt == null && interrupted.Select(r => r.SourceId).Contains(m.SourceId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, (DateTime?)null), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<RunResult> FinishAsync(RunContext run, List<string> issues, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var events = await db.RunEvents.Where(e => e.RunId == run.RunId)
            .GroupBy(e => e.Kind).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var openQuarantine = await db.Quarantine.CountAsync(q => q.ResolvedAt == null, ct);
        var pending = await db.Requisitions.CountAsync(r =>
            r.State == RecordState.Queued || r.State == RecordState.RetryPending || r.State == RecordState.InDoubt || r.State == RecordState.Sending, ct);

        var status = issues.Count > 0 || pending > 0 ? RunStatus.CompletedWithIssues : RunStatus.Completed;
        var message = issues.Count > 0 ? string.Join("; ", issues) : pending > 0 ? $"{pending} record(s) waiting for retry" : null;

        var row = await db.Runs.FindAsync([run.RunId], ct);
        row!.FinishedAt = Now;
        row.Status = status;
        row.Message = message;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Run {RunId} finished: {Status}", run.RunId, status);
        return new RunResult(run.RunId, status, message, events, metrics.Snapshot(), metrics.TotalRetryWait, openQuarantine, pending);
    }

    private static bool IsDownstreamFailure(Exception ex) =>
        ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException or IOException;
}
