using System.CommandLine;
using System.Text.Json;
using Procurement.Scenarios.Catalogue;
using Procurement.Scenarios.Engine;

namespace Procurement.TestLab;

public static class Cli
{
    public static readonly string[] Commands = ["list", "run", "show", "history"];

    public static async Task<int> RunAsync(string[] args)
    {
        var root = new RootCommand("testlab: run the integration's real-world test scenarios in an isolated lab.");

        var category = new Option<string?>("--category", "-c") { Description = "Only scenarios whose category contains this text" };
        var mode = new Option<Mode?>("--mode", "-m") { Description = "Only one execution mode (Baseline, Functional, Negative, DuplicateStress, FailureSimulation, Recovery, Security, Performance)" };
        var priority = new Option<Priority?>("--priority", "-p") { Description = "Only one priority (Critical, High, Medium, Low)" };
        var search = new Option<string?>("--search", "-s") { Description = "Text in the id, title or description" };
        var json = new Option<bool>("--json") { Description = "Machine-readable output" };

        var list = new Command("list", "List the scenario catalogue.") { category, mode, priority, search, json };
        list.SetAction(r =>
        {
            var selected = ScenarioCatalogue.Select(null, r.GetValue(category), r.GetValue(mode), r.GetValue(priority), r.GetValue(search));
            if (r.GetValue(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(selected, ScenarioRunner.Json));
                return 0;
            }
            foreach (var group in selected.GroupBy(s => s.Category))
            {
                Console.WriteLine();
                Console.WriteLine($"\u001b[1m{group.Key}\u001b[0m ({group.Count()})");
                foreach (var s in group)
                {
                    var state = s.BlockedReason is not null ? "\u001b[33mblocked\u001b[0m" : s.Implemented ? "" : "\u001b[90mnot implemented\u001b[0m";
                    Console.WriteLine($"  {s.Id,-10} {Pri(s.Priority)} {s.Title} {state}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"{selected.Count} scenario(s); {selected.Count(s => s.Implemented && s.BlockedReason is null)} executable, {selected.Count(s => s.BlockedReason is not null)} blocked.");
            return 0;
        });

        var ids = new Argument<string[]>("ids") { Description = "Scenario ids (DUP-012) or prefixes (DUP-*); empty with --all runs everything", Arity = ArgumentArity.ZeroOrMore };
        var all = new Option<bool>("--all") { Description = "Run the full regression (every scenario)" };
        var parallel = new Option<int>("--parallel") { Description = "Scenarios at a time", DefaultValueFactory = _ => 4 };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Stream each scenario's timeline" };
        var name = new Option<string?>("--name") { Description = "Name for this batch in the history" };
        var run = new Command("run", "Run scenarios; exit code 0 when none failed.") { ids, all, category, mode, priority, search, parallel, verbose, name };
        run.SetAction(async (r, ct) =>
        {
            var chosen = r.GetValue(ids) ?? [];
            if (chosen.Length == 0 && !r.GetValue(all) && r.GetValue(category) is null && r.GetValue(mode) is null && r.GetValue(priority) is null && r.GetValue(search) is null)
            {
                Console.Error.WriteLine("Name scenarios (e.g. DUP-012 or 'DUP-*'), a filter (--category, --mode, --priority, --search) or --all.");
                return 2;
            }
            var selected = ScenarioCatalogue.Select(chosen, r.GetValue(category), r.GetValue(mode), r.GetValue(priority), r.GetValue(search));
            if (selected.Count == 0)
            {
                Console.Error.WriteLine("No scenario matches.");
                return 2;
            }
            var batchName = r.GetValue(name) ?? (r.GetValue(all) ? "Full regression" : r.GetValue(mode) is { } m ? $"{m} mode" : string.Join(' ', chosen.DefaultIfEmpty("Filtered run")));
            return await RunBatchAsync(selected, batchName, r.GetValue(parallel), r.GetValue(verbose) || selected.Count == 1, ct);
        });

        var showId = new Argument<string>("id");
        var show = new Command("show", "Show a scenario's definition and its latest result.") { showId };
        show.SetAction(async (r, ct) =>
        {
            var s = ScenarioCatalogue.Find(r.GetValue(showId)!);
            if (s is null) { Console.Error.WriteLine("Unknown scenario."); return 2; }
            Console.WriteLine($"\u001b[1m{s.Id} {s.Title}\u001b[0m");
            Console.WriteLine($"  Category:      {s.Category}");
            Console.WriteLine($"  Priority:      {s.Priority} ({s.Severity})   Modes: {string.Join(", ", s.Modes)}");
            if (s.Description.Length > 0) Console.WriteLine($"  Description:   {s.Description}");
            Console.WriteLine($"  Preconditions: {s.Preconditions}");
            if (s.Fixture.Length > 0) Console.WriteLine($"  Fixture:       {s.Fixture}");
            Console.WriteLine($"  Faults:        {s.Faults}");
            Console.WriteLine($"  Expected:      {s.Expected.Summary}");
            Console.WriteLine($"  Invariants:    {string.Join(", ", s.Invariants.Order())}");
            if (s.BlockedReason is not null) Console.WriteLine($"  \u001b[33mBlocked:\u001b[0m       {s.BlockedReason}");
            var latest = await Store().LoadAsync("latest");
            var result = latest?.Results.FirstOrDefault(x => x.Id == s.Id);
            if (result is not null) { Console.WriteLine(); PrintResult(result, detailed: true); }
            return 0;
        });

        var history = new Command("history", "List earlier batches.");
        history.SetAction(_ =>
        {
            foreach (var b in Store().History().Take(30))
                Console.WriteLine($"{b.StartedAt:yyyy-MM-dd HH:mm}  {b.Id}  {b.Name,-30} pass {b.Tally.Passed}, fail {b.Tally.Failed}, blocked {b.Tally.Blocked}, not implemented {b.Tally.NotImplemented}");
            return 0;
        });

        root.Subcommands.Add(list);
        root.Subcommands.Add(run);
        root.Subcommands.Add(show);
        root.Subcommands.Add(history);
        return await root.Parse(args).InvokeAsync();
    }

    public static ResultStore Store() => new(Environment.GetEnvironmentVariable("TESTLAB_RESULTS_DIR") is { Length: > 0 } d ? d : Path.Combine(Paths.Root, "reports", "scenarios"));

    private static async Task<int> RunBatchAsync(IReadOnlyList<ScenarioDefinition> selected, string name, int parallel, bool verbose, CancellationToken ct)
    {
        Console.WriteLine($"\u001b[1mRunning {selected.Count} scenario(s): {name}\u001b[0m  (each in its own isolated lab)");
        var gate = new Lock();
        var batch = await ScenarioRunner.RunAsync(selected, new RunRequest { Name = name, Parallelism = parallel }, e =>
        {
            lock (gate)
            {
                if (e.Type == "log" && verbose && e.Log is { } log)
                    Console.WriteLine($"\u001b[90m  {log.At:HH:mm:ss.fff}{(log.Simulated ? " (simulated)" : "")} [{e.ScenarioId}]\u001b[0m {Colour(log)}");
                if (e.Type == "scenario-finished")
                {
                    var s = selected.First(x => x.Id == e.ScenarioId);
                    Console.WriteLine($"{Icon(e.Status!.Value)} {s.Id,-10} {s.Title}{(e.Status is ScenarioStatus.Pass ? "" : $"\n      \u001b[90m{e.Text}\u001b[0m")}");
                }
            }
        }, ct);

        var path = await Store().SaveAsync(batch);
        if (verbose) foreach (var r in batch.Results.Where(r => r.Status is ScenarioStatus.Pass or ScenarioStatus.Fail)) { Console.WriteLine(); PrintResult(r, detailed: true); }
        var t = batch.Tally;
        Console.WriteLine();
        Console.WriteLine($"\u001b[1m{t.Total} scenario(s), {t.Executed} executed: \u001b[32m{t.Passed} passed\u001b[0m\u001b[1m, \u001b[31m{t.Failed} failed\u001b[0m\u001b[1m, \u001b[33m{t.Blocked} blocked\u001b[0m\u001b[1m, {t.NotImplemented} not implemented\u001b[0m  ({(batch.FinishedAt - batch.StartedAt)?.TotalSeconds:0.0}s)");
        Console.WriteLine($"Results: {path}");
        return t.Failed == 0 ? 0 : 1;
    }

    private static void PrintResult(ScenarioResult r, bool detailed)
    {
        Console.WriteLine($"{Icon(r.Status)} \u001b[1m{r.Id} {r.Title}\u001b[0m  ({r.DurationMs / 1000:0.00}s)");
        Console.WriteLine($"  Expected: {r.Expected}");
        Console.WriteLine($"  Actual:   {r.Actual}");
        if (!detailed) return;
        foreach (var a in r.Assertions) Console.WriteLine($"    {(a.Passed ? "\u001b[32m✓\u001b[0m" : "\u001b[31m✗\u001b[0m")} {a.Name}{(a.Passed ? "" : $" (expected {a.Expected}, got {a.Actual})")}");
        foreach (var i in r.Invariants) Console.WriteLine($"    {(i.Outcome == InvariantOutcome.Pass ? "\u001b[32m✓\u001b[0m" : i.Outcome == InvariantOutcome.Fail ? "\u001b[31m✗\u001b[0m" : "-")} {i.Id} {i.Name}: \u001b[90m{i.Detail}\u001b[0m");
    }

    private static string Colour(LogEntry log) => log.Kind switch
    {
        LogKind.Step => $"\u001b[1m{log.Text}\u001b[0m",
        LogKind.Run => $"\u001b[36m{log.Text}\u001b[0m",
        LogKind.Warn => $"\u001b[33m{log.Text}\u001b[0m",
        LogKind.Error => $"\u001b[31m{log.Text}\u001b[0m",
        _ => log.Text,
    };

    private static string Icon(ScenarioStatus s) => s switch
    {
        ScenarioStatus.Pass => "\u001b[32mPASS\u001b[0m",
        ScenarioStatus.Fail => "\u001b[31mFAIL\u001b[0m",
        ScenarioStatus.Blocked => "\u001b[33mBLKD\u001b[0m",
        ScenarioStatus.NotImplemented => "\u001b[90mN/I \u001b[0m",
        _ => "    ",
    };

    private static string Pri(Priority p) => p switch
    {
        Priority.Critical => "\u001b[31mP0\u001b[0m",
        Priority.High => "\u001b[33mP1\u001b[0m",
        Priority.Medium => "P2",
        _ => "\u001b[90mP3\u001b[0m",
    };
}

public static class Paths
{
    public static readonly string Root = Find();

    private static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcurementIntegration.slnx"))) dir = dir.Parent;
            if (dir is not null) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
