using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Procurement.Scenarios.Lab;

namespace Procurement.Scenarios.Engine;

public sealed record RunRequest
{
    public string Name { get; init; } = "Ad hoc";
    public int Parallelism { get; init; } = 4;
    /// <summary>Keep full HTTP logs and before/after snapshots (large); otherwise they are trimmed.</summary>
    public bool FullEvidence { get; init; } = true;

    /// <summary>Overrides every scenario's lab seed (data and random faults), for reproducing a run.</summary>
    public int? Seed { get; init; }
}

/// <summary>Each scenario gets a fresh lab and passes only if every assertion and applicable invariant holds.</summary>
public static class ScenarioRunner
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task<BatchResult> RunAsync(IReadOnlyList<ScenarioDefinition> scenarios, RunRequest request, Action<RunnerEvent>? progress, CancellationToken ct)
    {
        var batch = new BatchResult
        {
            Id = $"batch-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}",
            Name = request.Name,
            StartedAt = DateTimeOffset.UtcNow,
        };
        progress?.Invoke(new RunnerEvent(batch.Id, "batch-started", null, $"{scenarios.Count} scenario(s)", Tally: Tally.Of([], scenarios.Count)));

        var results = new ScenarioResult[scenarios.Count];
        var cancelled = false;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, scenarios.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, request.Parallelism), CancellationToken = ct },
                async (i, token) =>
                {
                    var s = scenarios[i];
                    progress?.Invoke(new RunnerEvent(batch.Id, "scenario-started", s.Id, s.Title));
                    var result = await RunOneAsync(s, request, e => progress?.Invoke(new RunnerEvent(batch.Id, "log", s.Id, null, Log: e)),
                        x => progress?.Invoke(new RunnerEvent(batch.Id, "http", s.Id, null, Http: x)), token);
                    results[i] = result;
                    progress?.Invoke(new RunnerEvent(batch.Id, "scenario-finished", s.Id, result.Actual, result.Status,
                        Tally.Of(results.Where(r => r is not null), scenarios.Count)));
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        // A cancelled batch keeps what it finished; the rest is recorded as not run, never as passed.
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= new ScenarioResult
            {
                Id = scenarios[i].Id, Title = scenarios[i].Title, Category = scenarios[i].Category, Priority = scenarios[i].Priority,
                Status = ScenarioStatus.NotRun, StartedAt = DateTimeOffset.UtcNow, Expected = scenarios[i].Expected.Summary,
                Actual = "Not run (batch cancelled)",
            };
        }
        if (cancelled) batch.Name += " (cancelled)";
        batch.Results.AddRange(results);
        batch.FinishedAt = DateTimeOffset.UtcNow;
        Summarise(batch, scenarios);
        progress?.Invoke(new RunnerEvent(batch.Id, "batch-finished", null, null, Tally: batch.Tally));
        return batch;
    }

    public static void Summarise(BatchResult batch, IReadOnlyList<ScenarioDefinition> scenarios)
    {
        batch.Tally = Tally.Of(batch.Results);
        batch.ByCategory = batch.Results.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => Tally.Of(g));
        var modes = scenarios.ToDictionary(s => s.Id, s => s.Modes);
        batch.ByMode = Enum.GetValues<Mode>().ToDictionary(m => m.ToString(),
            m => Tally.Of(batch.Results.Where(r => modes.TryGetValue(r.Id, out var ms) && ms.Contains(m))));
    }

    public static async Task<ScenarioResult> RunOneAsync(ScenarioDefinition s, RunRequest request, Action<LogEntry>? onLog, Action<HttpExchange>? onHttp, CancellationToken ct)
    {
        var result = new ScenarioResult
        {
            Id = s.Id,
            Title = s.Title,
            Category = s.Category,
            Priority = s.Priority,
            StartedAt = DateTimeOffset.UtcNow,
            Expected = s.Expected.Summary,
            Seed = s.Lab.Seed,
        };

        if (s.BlockedReason is not null)
        {
            result.Status = ScenarioStatus.Blocked;
            result.BlockedReason = s.BlockedReason;
            result.Actual = "Blocked: " + s.BlockedReason;
            return result;
        }
        if (!s.Implemented)
        {
            result.Status = ScenarioStatus.NotImplemented;
            result.Actual = "Not implemented";
            return result;
        }

        var clock = Stopwatch.StartNew();
        await using var lab = new LabEnvironment(request.Seed is { } seed ? s.Lab with { Seed = seed } : s.Lab);
        if (onHttp is not null) lab.Capture.Recorded += onHttp;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
        var ctx = new ScenarioContext(s, lab, result, onLog) { Cancellation = timeout.Token };
        IReadOnlyList<JsonObject> sapBefore = [];
        try
        {
            sapBefore = await lab.SapDocumentsAsync();
            await s.Body!(ctx);
            await ctx.EnsureBeforeAsync();
            if (s.Invariants.Count > 0)
            {
                ctx.Step("Check the global invariants against SAP, the database and the HTTP log");
                result.Invariants.AddRange(await Invariants.EvaluateAsync(ctx, sapBefore));
            }
            result.After = await ctx.SnapshotAsync();
        }
        catch (ScenarioBlockedException ex)
        {
            result.Status = ScenarioStatus.Blocked;
            result.BlockedReason = ex.Message;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            result.Error = $"Timed out after {s.TimeoutSeconds}s";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result.Error = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
            ctx.Warn("Scenario error: " + result.Error);
        }
        finally
        {
            if (onHttp is not null) lab.Capture.Recorded -= onHttp;
        }

        result.DurationMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
        result.Http.AddRange(request.FullEvidence ? lab.Capture.Exchanges.OrderBy(e => e.Seq) : lab.Capture.Exchanges.OrderBy(e => e.Seq).Take(200));
        if (!request.FullEvidence)
        {
            result.Before = null;
            result.After = null;
        }

        if (result.Status != ScenarioStatus.Blocked)
        {
            var failedAssertions = result.Assertions.Where(a => !a.Passed).ToList();
            var failedInvariants = result.Invariants.Where(i => i.Outcome == InvariantOutcome.Fail).ToList();
            var passed = result.Error is null && failedAssertions.Count == 0 && failedInvariants.Count == 0 && result.Assertions.Count > 0;
            result.Status = passed ? ScenarioStatus.Pass : ScenarioStatus.Fail;
            result.Actual = passed
                ? $"{result.Assertions.Count} assertion(s) and {result.Invariants.Count(i => i.Outcome == InvariantOutcome.Pass)} invariant(s) held"
                : result.Error ?? (result.Assertions.Count == 0 ? "No assertions were made" : string.Join("; ",
                    failedAssertions.Select(a => $"{a.Name}: expected {a.Expected}, got {a.Actual}").Concat(failedInvariants.Select(i => $"{i.Id} {i.Name}: {i.Detail}")).Take(4)));
        }
        else
        {
            result.Actual = "Blocked: " + result.BlockedReason;
        }
        return result;
    }
}

/// <summary>Every batch as JSON, plus latest.json and a small index for the history views.</summary>
public sealed class ResultStore(string directory)
{
    public string Directory { get; } = directory;

    private static readonly Lock IndexGate = new();

    /// <summary>The batch without its heavy evidence (HTTP logs, snapshots, timelines): what lists and dashboards need.</summary>
    public static BatchResult Summary(BatchResult b) => b with
    {
        Results = b.Results.Select(r => r with
        {
            Http = [], Log = [], Evidence = [], Before = null, After = null, Runs = [],
            Assertions = r.Assertions.Where(a => !a.Passed).ToList(),
        }).ToList(),
    };

    public async Task<string> SaveAsync(BatchResult batch)
    {
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "batches"));
        var path = Path.Combine(Directory, "batches", batch.Id + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(batch, ScenarioRunner.Json));
        await File.WriteAllTextAsync(Path.Combine(Directory, "batches", batch.Id + ".summary.json"), JsonSerializer.Serialize(Summary(batch), ScenarioRunner.Json));
        File.Copy(path, Path.Combine(Directory, "latest.json"), overwrite: true);
        UpdateIndex(batch);
        return path;
    }

    /// <summary>Latest result for every scenario across all batches (scenarios not in a batch keep their earlier result).</summary>
    public Dictionary<string, LatestResult> LatestResults()
    {
        var path = Path.Combine(Directory, "scenario-index.json");
        if (!File.Exists(path)) Backfill(path);
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, LatestResult>>(File.ReadAllText(path), ScenarioRunner.Json) ?? [];
    }

    /// <summary>Builds the index once from batches saved before it existed, oldest first, so the newest result wins.</summary>
    private void Backfill(string path)
    {
        lock (IndexGate)
        {
            if (File.Exists(path)) return;
            var index = new Dictionary<string, LatestResult>(StringComparer.Ordinal);
            foreach (var summary in History().OrderBy(b => b.StartedAt))
            {
                BatchResult? batch;
                try { batch = Load(summary.Id, summary: true); }
                catch (Exception ex) when (ex is JsonException or IOException) { continue; }
                foreach (var r in batch?.Results.Where(r => r.Status != ScenarioStatus.NotRun) ?? [])
                    index[r.Id] = new LatestResult(batch!.Id, batch.StartedAt, r.Status, r.Actual, r.DurationMs);
            }
            if (index.Count > 0) File.WriteAllText(path, JsonSerializer.Serialize(index, ScenarioRunner.Json));
        }
    }

    private void UpdateIndex(BatchResult batch)
    {
        var index = LatestResults();
        lock (IndexGate)
        {
            foreach (var r in batch.Results.Where(r => r.Status != ScenarioStatus.NotRun))
                index[r.Id] = new LatestResult(batch.Id, batch.StartedAt, r.Status, r.Actual, r.DurationMs);
            File.WriteAllText(Path.Combine(Directory, "scenario-index.json"), JsonSerializer.Serialize(index, ScenarioRunner.Json));
        }
    }

    public IReadOnlyList<BatchSummary> History()
    {
        var folder = Path.Combine(Directory, "batches");
        if (!System.IO.Directory.Exists(folder)) return [];
        var list = new List<BatchSummary>();
        var summaries = System.IO.Directory.GetFiles(folder, "*.summary.json").ToHashSet();
        // Prefer the small summary file; fall back to the full file only for batches saved before summaries existed.
        var files = summaries.Concat(System.IO.Directory.GetFiles(folder, "*.json")
            .Where(f => !f.EndsWith(".summary.json", StringComparison.Ordinal) && !summaries.Contains(f[..^5] + ".summary.json")));
        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                list.Add(new BatchSummary(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!,
                    root.GetProperty("startedAt").GetDateTimeOffset(),
                    root.TryGetProperty("finishedAt", out var f) && f.ValueKind == JsonValueKind.String ? f.GetDateTimeOffset() : null,
                    root.GetProperty("tally").Deserialize<Tally>(ScenarioRunner.Json)!));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
            {
            }
        }
        return list.OrderByDescending(b => b.StartedAt).ToList();
    }

    public async Task<BatchResult?> LoadAsync(string id, bool summary = false)
    {
        var path = PathFor(id, summary);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BatchResult>(stream, ScenarioRunner.Json);
    }

    private BatchResult? Load(string id, bool summary)
    {
        var path = PathFor(id, summary);
        return File.Exists(path) ? JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(path), ScenarioRunner.Json) : null;
    }

    private string PathFor(string id, bool summary)
    {
        if (id == "latest") id = History() is [var newest, ..] ? newest.Id : id;
        var folder = Path.Combine(Directory, "batches");
        var name = Path.GetFileName(id);
        return summary && File.Exists(Path.Combine(folder, name + ".summary.json")) ? Path.Combine(folder, name + ".summary.json")
            : id == "latest" ? Path.Combine(Directory, "latest.json") : Path.Combine(folder, name + ".json");
    }
}

public sealed record BatchSummary(string Id, string Name, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, Tally Tally);

public sealed record LatestResult(string BatchId, DateTimeOffset At, ScenarioStatus Status, string Actual, double DurationMs);
