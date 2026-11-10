using System.Text.Json.Nodes;
using Procurement.Contracts;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;

namespace Procurement.Scenarios.Engine;

public sealed class ScenarioBlockedException(string reason) : Exception(reason);

/// <summary>Assertions never throw: every check is recorded, and the scenario passes only if all of them and the global invariants hold.</summary>
public sealed class ScenarioContext(ScenarioDefinition definition, LabEnvironment lab, ScenarioResult result, Action<LogEntry>? onLog)
{
    public ScenarioDefinition Definition { get; } = definition;
    public LabEnvironment Lab { get; } = lab;
    public ScenarioResult Result { get; } = result;
    public CancellationToken Cancellation { get; init; }

    // Security scenarios judge invariants 8 and 9 themselves.
    internal HashSet<string> JudgedInvariants { get; } = [];

    public void Step(string text) => Add(LogKind.Step, text);
    public void Info(string text) => Add(LogKind.Info, text);
    public void Warn(string text) => Add(LogKind.Warn, text);

    /// <summary>A step the lab imitates rather than performs, e.g. "three hours pass".</summary>
    public void Simulated(string text) => Add(LogKind.Step, text, simulated: true);

    private void Add(LogKind kind, string text, bool simulated = false)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, kind, text, simulated);
        lock (Result.Log) Result.Log.Add(entry);
        onLog?.Invoke(entry);
    }

    public void Evidence(string key, object? value)
    {
        lock (Result.Evidence) Result.Evidence[key] = value;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Called as ctx.Block(...) from scenario bodies")]
    public void Block(string reason) => throw new ScenarioBlockedException(reason);

    public bool Check(string name, bool passed, object? expected = null, object? actual = null, string? invariant = null)
    {
        lock (Result.Assertions) Result.Assertions.Add(new Assertion(name, passed, expected?.ToString(), actual?.ToString(), invariant));
        if (invariant is not null) JudgedInvariants.Add(invariant);
        Add(passed ? LogKind.Info : LogKind.Error, $"{(passed ? "✓" : "✗")} {name}" + (passed ? "" : $" (expected {expected}, got {actual})"));
        return passed;
    }

    public bool Equal<T>(string name, T expected, T actual, string? invariant = null) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual), expected, actual, invariant);

    public async Task<RunOutcome> RunSyncAsync(string label = "sync run", LabWorker? worker = null)
    {
        await EnsureBeforeAsync();
        Add(LogKind.Run, $"▶ {label}{(worker is null ? "" : $" on {worker.Name}")}");
        var outcome = await (worker ?? Lab.Worker).RunAsync(label, Cancellation);
        RecordRun(label, outcome);
        return outcome;
    }

    public async Task<List<RunOutcome>> RunUntilSettledAsync(string label = "sync until settled", int maxRuns = 6)
    {
        await EnsureBeforeAsync();
        Add(LogKind.Run, $"▶ {label}");
        var outcomes = await Lab.RunUntilSettledAsync(maxRuns, Cancellation);
        for (var i = 0; i < outcomes.Count; i++) RecordRun($"{label} #{i + 1}", outcomes[i]);
        return outcomes;
    }

    public void RecordRun(string label, RunOutcome outcome)
    {
        var r = outcome.Result;
        var summary = new RunSummary(label, outcome.Worker, outcome.Kind.ToString(), r?.Status.ToString(), outcome.Error, Math.Round(outcome.DurationMs, 1),
            r?.Events.ToDictionary(e => e.Key.ToString(), e => e.Value), r?.Transport.ToDictionary(e => e.Key, e => e.Value), r?.OpenQuarantine, r?.Pending);
        lock (Result.Runs) Result.Runs.Add(summary);
        Add(outcome.Kind is RunOutcomeKind.Crashed or RunOutcomeKind.Failed ? LogKind.Warn : LogKind.Run,
            $"■ {label}: {outcome.Kind}" + (r is null ? $" ({outcome.Error})" : $", {Describe(r.Events)}{(r.Pending > 0 ? $", {r.Pending} pending" : "")}"));
    }

    private static string Describe(IReadOnlyDictionary<RunEventKind, int> events)
    {
        var shown = new[] { RunEventKind.Created, RunEventKind.AlreadyInSap, RunEventKind.Amended, RunEventKind.InDoubtResolved, RunEventKind.Quarantined,
            RunEventKind.Ignored, RunEventKind.RetryScheduled, RunEventKind.Conflict };
        var parts = shown.Where(k => events.GetValueOrDefault(k) > 0).Select(k => $"{events[k]} {k}");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "no changes" : text;
    }

    public async Task RestartAsync(string why = "process restarted")
    {
        Step($"Restart the integration process ({why}); the database file is kept");
        await Lab.RestartAsync();
    }

    public async Task EnsureBeforeAsync()
    {
        if (Result.Before is null) Result.Before = await SnapshotAsync();
    }

    public async Task<StateSnapshot> SnapshotAsync() =>
        new(await Lab.SourceSnapshotAsync(), await Lab.SapDocumentsAsync(), await Lab.DbSnapshotAsync());

    public async Task<JsonObject> PutAsync(JsonObject record, long? version = null)
    {
        var id = record["id"]?.GetValue<string>() ?? "NOID-" + Guid.NewGuid().ToString("N")[..8];
        return await Lab.PutSourceAsync(id, record, version);
    }

    public async Task PutAllAsync(IEnumerable<JsonObject> records)
    {
        foreach (var r in records) await PutAsync(r);
    }

    public async Task<int> SapCountAsync(string reference) => (await Lab.SapDocumentsForAsync(reference)).Count;

    public async Task<string?> ExpectCreatedOnceAsync(string id, string? label = null)
    {
        var docs = await Lab.SapDocumentsForAsync(id);
        var record = await Lab.RecordAsync(id);
        Equal($"{label ?? id}: exactly one SAP requisition", 1, docs.Count, "INV-01");
        Equal($"{label ?? id}: record state", "Synced", record?.State.ToString());
        var number = docs.FirstOrDefault()?["PurchaseRequisition"]?.GetValue<string>();
        Equal($"{label ?? id}: local link is that SAP document", number, record?.SapNumber, "INV-03");
        return number;
    }

    public async Task ExpectQuarantinedAsync(string id, QuarantineReason reason, string? field = null)
    {
        var record = await Lab.RecordAsync(id);
        var entries = await Lab.OpenQuarantineAsync(id);
        Equal($"{id}: record state", "Quarantined", record?.State.ToString());
        Check($"{id}: quarantined as {reason}", entries.Any(e => e.Reason == reason), reason, string.Join(", ", entries.Select(e => e.Reason)));
        if (field is not null)
        {
            var details = string.Join(" | ", entries.Select(e => e.Details ?? e.Summary));
            Check($"{id}: problem names field '{field}'", details.Contains(field, StringComparison.OrdinalIgnoreCase), field, Shorten(details));
        }
        Check($"{id}: raw payload kept for repair", entries.All(e => !string.IsNullOrEmpty(e.RawPayload)), "raw payload", entries.Count == 0 ? "no entry" : "kept");
        Equal($"{id}: nothing created in SAP", 0, await SapCountAsync(id), "INV-03");
    }

    public async Task ExpectSkippedAsync(string id, string? status = null)
    {
        var record = await Lab.RecordAsync(id);
        Equal($"{id}: record state", "Ignored", record?.State.ToString());
        if (status is not null) Equal($"{id}: source status recorded", status, record?.SourceStatus);
        Equal($"{id}: nothing created in SAP", 0, await SapCountAsync(id), "INV-03");
    }

    public async Task ExpectNotSyncedAsync(string id)
    {
        var record = await Lab.RecordAsync(id);
        Check($"{id}: not reported as synchronised", record?.State != RecordState.Synced, "not Synced", record?.State.ToString() ?? "(no record)", "INV-03");
        Equal($"{id}: nothing created in SAP", 0, await SapCountAsync(id));
    }

    internal static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
