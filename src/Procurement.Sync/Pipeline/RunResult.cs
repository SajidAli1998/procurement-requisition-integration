using System.Text;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

public sealed record RunResult(
    string RunId,
    RunStatus Status,
    string? Message,
    IReadOnlyDictionary<RunEventKind, int> Events,
    IReadOnlyDictionary<string, long> Transport,
    TimeSpan RetryWait,
    int OpenQuarantine,
    int Pending,
    bool Skipped = false)
{
    public int Count(RunEventKind kind) => Events.GetValueOrDefault(kind);

    public static RunResult SkippedRun(string runId, string reason) =>
        new(runId, RunStatus.Aborted, reason, new Dictionary<RunEventKind, int>(), new Dictionary<string, long>(), TimeSpan.Zero, 0, 0, Skipped: true);

    public string ToConsoleSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run {RunId}: {Status}{(Message is null ? "" : $" - {Message}")}");
        foreach (var (kind, count) in Events.OrderBy(e => e.Key)) sb.AppendLine($"  {kind,-26}{count,6}");
        foreach (var (name, value) in Transport) sb.AppendLine($"  {name,-26}{value,6}");
        sb.AppendLine($"  {"retry wait",-26}{RetryWait.TotalSeconds,6:0.0}s");
        sb.AppendLine($"  {"open quarantine",-26}{OpenQuarantine,6}");
        sb.Append($"  {"pending (queued/retry)",-26}{Pending,6}");
        return sb.ToString();
    }
}
