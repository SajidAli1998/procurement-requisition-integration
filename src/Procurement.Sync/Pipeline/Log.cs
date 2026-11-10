using Microsoft.Extensions.Logging;
using Procurement.Sync.Clients;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

internal static partial class Log
{
    [LoggerMessage(LogLevel.Warning, "Another run holds the lease; skipping")]
    public static partial void LeaseHeld(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Run {RunId} started ({Trigger})")]
    public static partial void RunStarted(this ILogger logger, string runId, string trigger);

    [LoggerMessage(LogLevel.Information, "Run {RunId} finished: {Status}")]
    public static partial void RunFinished(this ILogger logger, string runId, RunStatus status);

    [LoggerMessage(LogLevel.Warning, "Extract stopped early")]
    public static partial void ExtractStopped(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Could not write the reconciliation report for {RunId}")]
    public static partial void ReportFailed(this ILogger logger, Exception exception, string runId);

    [LoggerMessage(LogLevel.Information, "Reading the source from the start (periodic completeness scan)")]
    public static partial void FullScanStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Warning, "The source rejected the saved cursor; reading from the start")]
    public static partial void CursorRejected(this ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Information, "Extracted page {Page} ({Count} records)")]
    public static partial void PageExtracted(this ILogger logger, int page, int count);

    [LoggerMessage(LogLevel.Warning, "Dispatch stopped early; remaining outbox messages stay queued")]
    public static partial void DispatchStopped(this ILogger logger);

    [LoggerMessage(LogLevel.Error, "Stored payload for {Id} is unreadable")]
    public static partial void PayloadUnreadable(this ILogger logger, Exception exception, string id);

    [LoggerMessage(LogLevel.Warning, "Status polling stopped: {Status} {Error}")]
    public static partial void StatusPollingStopped(this ILogger logger, SapCallStatus status, string? error);

    [LoggerMessage(LogLevel.Information, "Status sync applied {Count} status change(s)")]
    public static partial void StatusChangesApplied(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Warning, "{Count} status write-back(s) left pending")]
    public static partial void WritebacksPending(this ILogger logger, int count);
}
