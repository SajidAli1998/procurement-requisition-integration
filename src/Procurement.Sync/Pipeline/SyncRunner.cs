using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Procurement.Sync.Http;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

public sealed class SyncRunner(
    IDbContextFactory<SyncDbContext> dbFactory,
    LeaseManager leases,
    ExtractStage extract,
    DispatchStage dispatch,
    StatusSyncStage statusSync,
    WritebackStage writeback,
    ReportBuilder reports,
    ReportWriter reportWriter,
    RunMetrics metrics,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<SyncRunner> logger)
{
    public const string LeaseName = "procsync";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public Task<RunResult> RunAsync(string trigger, CancellationToken ct) =>
        ExecuteAsync(trigger, extract.RunAsync, ct);

    public Task<RunResult> ReplayAsync(string sourceId, CancellationToken ct) =>
        ExecuteAsync($"replay {sourceId}", (run, token) => extract.ReprocessAsync(run, sourceId, token), ct);

    private async Task<RunResult> ExecuteAsync(string trigger, Func<RunContext, CancellationToken, Task> extractStep, CancellationToken ct)
    {
        await dbFactory.EnsureSyncStoreAsync(ct);
        var runId = $"run-{Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..29];
        if (!await leases.TryAcquireAsync(LeaseName, runId, options.Value.LeaseDuration, ct))
        {
            logger.LeaseHeld();
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
            logger.RunStarted(runId, trigger);

            await RecoverInterruptedAsync(run, ct);

            var issues = new List<string>();
            try
            {
                await extractStep(run, ct);
            }
            catch (Exception ex) when (IsDownstreamFailure(ex) && !ct.IsCancellationRequested)
            {
                logger.ExtractStopped(ex);
                issues.Add($"Source read stopped early ({ex.Message}); progress up to the last checkpoint is kept");
            }

            // Dispatch still runs: everything staged before a source failure is valid and should be sent.
            var dispatched = await dispatch.RunAsync(run, ct);
            if (dispatched.CircuitOpen) issues.Add(dispatched.StopReason ?? "SAP circuit breaker opened; remaining records stay queued for the next run");

            if (await statusSync.RunAsync(run, ct) is { } statusIssue) issues.Add(statusIssue);
            if (await writeback.RunAsync(run, ct) is { } writebackIssue) issues.Add(writebackIssue);

            return await FinishAsync(run, trigger, issues, ct);
        }
        finally
        {
            await leases.ReleaseAsync(LeaseName, runId, CancellationToken.None);
        }
    }

    // Records left in Sending by a crashed run may or may not have reached SAP.
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

    private async Task<RunResult> FinishAsync(RunContext run, string trigger, List<string> issues, CancellationToken ct)
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

        ReconciliationReport? report = null;
        string? reportPath = null;
        try
        {
            report = await reports.BuildAsync(run.RunId, trigger, run.StartedAt, status.ToString(), message, ct);
            reportPath = await reportWriter.WriteAsync(report, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The run's work is already committed; a reporting failure must not undo or hide it.
            logger.ReportFailed(ex, run.RunId);
        }

        logger.RunFinished(run.RunId, status);
        return new RunResult(run.RunId, status, message, events, metrics.Snapshot(), metrics.TotalRetryWait, openQuarantine, pending)
        {
            Report = report,
            ReportPath = reportPath,
        };
    }

    private static bool IsDownstreamFailure(Exception ex) =>
        ex is HttpRequestException or ExecutionRejectedException or TaskCanceledException or IOException or System.Text.Json.JsonException;
}
