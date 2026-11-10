using System.Text.Json.Serialization;
using Procurement.Scenarios.Lab;

namespace Procurement.Scenarios.Engine;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Priority { Critical, High, Medium, Low }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioStatus { NotRun, Pass, Fail, Blocked, NotImplemented }

/// <summary>Full Regression runs every mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Mode { Baseline, Functional, Negative, DuplicateStress, FailureSimulation, Recovery, Security, Performance }

/// <summary>Free text for people to read: the assertions are what decide the result.</summary>
public sealed record Expectation
{
    public string? Http { get; init; }
    public string? Source { get; init; }
    public string? Sap { get; init; }
    public string? Database { get; init; }
    public string? Reconciliation { get; init; }
    public string Summary { get; init; } = "";
}

public sealed class ScenarioDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public string Description { get; set; } = "";
    public Priority Priority { get; set; } = Priority.Medium;
    public string Severity { get; set; } = "Major";
    public List<Mode> Modes { get; } = [];
    public string Preconditions { get; set; } = "Fresh, isolated lab: empty source, empty SAP, empty integration database.";
    public string Fixture { get; set; } = "";
    public string Faults { get; set; } = "None";
    public List<string> Steps { get; } = [];
    public Expectation Expected { get; set; } = new();
    public HashSet<string> Invariants { get; } = [];
    public LabOptions Lab { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Why the scenario cannot run here; it is reported as Blocked, never as passed.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>Where else the behaviour is covered, e.g. an xUnit test or the Playwright suite.</summary>
    public string? CoveredBy { get; set; }

    [JsonIgnore] public Func<ScenarioContext, Task>? Body { get; set; }

    public bool Implemented => Body is not null;
}

public sealed record Assertion(string Name, bool Passed, string? Expected, string? Actual, string? Invariant = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogKind { Step, Info, Run, Warn, Error }

/// <summary>Simulated marks a step that stands for time passing or an event the lab only imitates.</summary>
public sealed record LogEntry(DateTimeOffset At, LogKind Kind, string Text, bool Simulated = false);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvariantOutcome { Pass, Fail, NotApplicable }

public sealed record InvariantResult(string Id, string Name, InvariantOutcome Outcome, string Detail);

public sealed record RunSummary(string Label, string Worker, string Kind, string? Status, string? Message, double DurationMs,
    IReadOnlyDictionary<string, int>? Events, IReadOnlyDictionary<string, long>? Transport, int? OpenQuarantine, int? Pending);

public sealed record StateSnapshot(object? Source, object? Sap, object? Database);

public sealed record ScenarioResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public Priority Priority { get; init; }
    public ScenarioStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public double DurationMs { get; set; }
    public string Expected { get; init; } = "";
    public string Actual { get; set; } = "";
    public string? Error { get; set; }
    public string? BlockedReason { get; set; }
    public List<LogEntry> Log { get; init; } = [];
    public List<Assertion> Assertions { get; init; } = [];
    public List<InvariantResult> Invariants { get; init; } = [];
    public List<RunSummary> Runs { get; init; } = [];
    public List<HttpExchange> Http { get; init; } = [];
    public Dictionary<string, object?> Evidence { get; init; } = [];
    public StateSnapshot? Before { get; set; }
    public StateSnapshot? After { get; set; }
    public int Seed { get; init; }
}

public sealed record Tally(int Total, int Executed, int Passed, int Failed, int Blocked, int NotImplemented, int NotRun)
{
    public static Tally Of(IEnumerable<ScenarioResult> results, int? total = null)
    {
        var list = results.ToList();
        var t = total ?? list.Count;
        var pass = list.Count(r => r.Status == ScenarioStatus.Pass);
        var fail = list.Count(r => r.Status == ScenarioStatus.Fail);
        var blocked = list.Count(r => r.Status == ScenarioStatus.Blocked);
        var ni = list.Count(r => r.Status == ScenarioStatus.NotImplemented);
        return new Tally(t, pass + fail, pass, fail, blocked, ni, t - list.Count + list.Count(r => r.Status == ScenarioStatus.NotRun));
    }
}

public sealed record BatchResult
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public Tally Tally { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
    public Dictionary<string, Tally> ByCategory { get; set; } = [];
    public Dictionary<string, Tally> ByMode { get; set; } = [];
    public RunEnvironment Environment { get; init; } = RunEnvironment.Current();
    public List<ScenarioResult> Results { get; init; } = [];
}

public sealed record RunEnvironment(string AppVersion, string GitCommit, string Runtime, string Os, string Machine)
{
    public static RunEnvironment Current()
    {
        var version = typeof(RunEnvironment).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return new RunEnvironment(version, GitInfo.Commit, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription, Environment.MachineName);
    }
}

internal static class GitInfo
{
    private static readonly Lazy<string> Lazy = new(() =>
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
            if (dir is null) return "unknown";
            var head = File.ReadAllText(Path.Combine(dir.FullName, ".git", "HEAD")).Trim();
            if (!head.StartsWith("ref: ")) return head[..Math.Min(12, head.Length)];
            var refPath = Path.Combine(dir.FullName, ".git", head[5..]);
            var commit = File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "unknown";
            return $"{head[16..]}@{commit[..Math.Min(12, commit.Length)]}";
        }
        catch (IOException)
        {
            return "unknown";
        }
    });

    public static string Commit => Lazy.Value;
}

public sealed record RunnerEvent(string BatchId, string Type, string? ScenarioId, string? Text, ScenarioStatus? Status = null, Tally? Tally = null,
    LogEntry? Log = null, HttpExchange? Http = null);
