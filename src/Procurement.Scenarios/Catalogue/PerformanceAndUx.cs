using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

/// <summary>The first run records a baseline; later runs fail only if throughput falls below 40% of it. No service level is invented.</summary>
internal static class PerfBaseline
{
    private static readonly Lock Gate = new();

    private static string PathOf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcurementIntegration.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "reports", "scenarios", "perf-baseline.json");
    }

    public static void Compare(ScenarioContext ctx, string key, double throughput)
    {
        lock (Gate)
        {
            var path = PathOf();
            var all = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            if (all[key]?["throughput"]?.GetValue<double>() is { } baseline)
            {
                ctx.Check($"throughput within 40% of the measured baseline ({baseline:0.0}/s)", throughput >= baseline * 0.4, $">= {baseline * 0.4:0.0}/s", $"{throughput:0.0}/s");
                return;
            }
            all[key] = new JsonObject { ["throughput"] = Math.Round(throughput, 1), ["recordedAt"] = DateTime.UtcNow.ToString("O"), ["machine"] = Environment.MachineName };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            ctx.Info($"No baseline yet for {key}: recorded {throughput:0.0}/s as the baseline (no target invented)");
        }
    }
}

internal static class PerformanceAndUx
{
    private static async Task Load(ScenarioContext ctx, int n, string prefix, int lines = 1)
    {
        await Task.WhenAll(Enumerable.Range(1, n).Chunk(50).Select(async chunk =>
        {
            foreach (var i in chunk) await ctx.Lab.PutSourceAsync($"{prefix}-{i:D6}", Req.Valid($"{prefix}-{i:D6}", lines: lines, quantity: 1, price: 10m));
        }));
    }

    private static async Task Measure(ScenarioContext ctx, string key, int records, int maxRuns = 6)
    {
        GC.Collect();
        var memBefore = GC.GetTotalMemory(true);
        var clock = Stopwatch.StartNew();
        var runs = await ctx.RunUntilSettledAsync($"{records} records", maxRuns);
        clock.Stop();
        var sapCalls = ctx.Lab.Capture.Exchanges.Where(e => e.Channel.EndsWith("→sap", StringComparison.Ordinal)).ToList();
        var lat = sapCalls.Where(e => e.Status is not null).Select(e => e.DurationMs).Order().ToList();
        double P(double q) => lat.Count == 0 ? 0 : Math.Round(lat[(int)Math.Min(lat.Count - 1, Math.Floor(q * lat.Count))], 2);
        var stats = await ctx.Lab.SapStatsAsync();
        var throughput = stats.Total / Math.Max(0.001, clock.Elapsed.TotalSeconds);
        var metrics = new
        {
            records, created = stats.Total, seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), throughputPerSecond = Math.Round(throughput, 1),
            sapLatencyMs = ctx.Lab.Options.CaptureHttp ? new { p50 = P(0.5), p95 = P(0.95), p99 = P(0.99) } : null,
            retryAmplification = ctx.Lab.Options.CaptureHttp && records > 0 ? Math.Round(sapCalls.Count / (double)records, 2) : (double?)null,
            managedMemoryMb = Math.Round((GC.GetTotalMemory(false) - memBefore) / 1024.0 / 1024.0, 1),
            workingSetMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0, 0),
            runs = runs.Count,
        };
        ctx.Evidence("metrics", metrics);
        ctx.Info($"{records} records: {metrics.seconds}s, {metrics.throughputPerSecond}/s");
        ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
        PerfBaseline.Compare(ctx, key, throughput);
    }

    private static LabOptions Perf(int capture = 1, Dictionary<string, string?>? sync = null, Dictionary<string, string?>? sap = null) => new()
    {
        CaptureHttp = capture == 1,
        Sync = sync ?? new() { ["PageSize"] = "100", ["Parallelism"] = "8" },
        Sap = sap ?? [],
    };

    public static void Register(CatalogueBuilder c)
    {
        const string P = Categories.Performance;

        foreach (var (id, n, title) in new[] { ("PERF-001", 1, "1 record"), ("PERF-002", 10, "10 records"), ("PERF-004", 1000, "1,000 records") })
        {
            c.Add(id, P, $"Throughput: {title}", n >= 1000 ? High : Medium, Performance).Lab(Perf()).Timeout(300)
                .Expect("All created once; throughput, latency, retry amplification and memory recorded").Run(async ctx =>
                {
                    await Load(ctx, n, id);
                    await Measure(ctx, id, n);
                    ctx.Equal("all created", n, (await ctx.Lab.SapStatsAsync()).Total);
                });
        }

        c.Add("PERF-003", P, "Throughput: the client's 120-record workbook", High, Performance, Baseline).Lab(BaselineAndValid.Workbook)
            .Expect("Settles to 65 documents; metrics recorded").Run(async ctx =>
            {
                await Measure(ctx, "PERF-003", 120);
                ctx.Equal("SAP documents", 65, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("PERF-005", P, "Throughput: 10,000 records", High, Performance)
            .Lab(new() { Dataset = "Generated", Count = 10_000, Seed = 11, CaptureHttp = false, Sync = new() { ["PageSize"] = "100", ["Parallelism"] = "16" } })
            .Timeout(900).Expect("Every record accounted for; no duplicates; metrics recorded").Run(async ctx =>
            {
                await Measure(ctx, "PERF-005", 10_000);
                await using var db = ctx.Lab.Db();
                ctx.Equal("records tracked", 10_000, await db.Requisitions.CountAsync());
            });

        c.Add("PERF-006", P, "Throughput: 100,000 records", Low, Performance)
            .Blocked("Not practical in the lab's in-memory hosts on a developer machine (the mocks hold every record in one process). PERF-005 measures 10,000; a 100,000-record run belongs on SQL Server with the outbox on Service Bus (see the production plan).");

        c.Add("PERF-007", P, "Slow source API (50 ms per call)", Medium, Performance).Lab(Perf()).Timeout(300)
            .Expect("Correct under latency; throughput recorded").Run(async ctx =>
            {
                await Load(ctx, 200, "PERF-007");
                await ctx.Lab.SourceFaultsAsync(new FaultSettings { LatencyMs = 50 });
                await Measure(ctx, "PERF-007", 200);
            });

        c.Add("PERF-008", P, "Slow SAP API (50 ms per call)", Medium, Performance).Lab(Perf()).Timeout(300)
            .Expect("Correct under latency; latency percentiles reflect it").Run(async ctx =>
            {
                await Load(ctx, 200, "PERF-008");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { LatencyMs = 50 });
                await Measure(ctx, "PERF-008", 200);
            });

        c.Add("PERF-009", P, "High network latency on both sides (30 ms)", Medium, Performance).Lab(Perf()).Timeout(300)
            .Expect("Correct; recorded").Run(async ctx =>
            {
                await Load(ctx, 200, "PERF-009");
                await ctx.Lab.SourceFaultsAsync(new FaultSettings { LatencyMs = 30 });
                await ctx.Lab.SapFaultsAsync(new FaultSettings { LatencyMs = 30 });
                await Measure(ctx, "PERF-009", 200);
            });

        c.Add("PERF-010", P, "Intermittent failures (10% 503, 5% 429, 3% lost responses)", High, Performance, FailureSimulation).Lab(Perf()).Timeout(300)
            .Expect("Settles with every record once; retry amplification recorded").Run(async ctx =>
            {
                await Load(ctx, 400, "PERF-010");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 0.10, ThrottleRate = 0.05, LostResponseRate = 0.03, Seed = 99 });
                await Measure(ctx, "PERF-010", 400, maxRuns: 10);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await ctx.RunUntilSettledAsync("drain");
                ctx.Equal("all created once", 400, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("PERF-011", P, "High retry volume (30% 503)", Medium, Performance, FailureSimulation).Lab(Perf(sync: new() { ["PageSize"] = "100", ["Parallelism"] = "8", ["Resilience:BreakerFailureRatio"] = "0.9" })).Timeout(300)
            .Expect("No unsafe retries, no duplicates; amplification measured").Run(async ctx =>
            {
                await Load(ctx, 300, "PERF-011");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 0.30, Seed = 5 });
                await Measure(ctx, "PERF-011", 300, maxRuns: 10);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await ctx.RunUntilSettledAsync("drain");
                ctx.Equal("all created once", 300, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("PERF-012", P, "Concurrent workers (4) on 600 records", High, Performance, DuplicateStress)
            .Lab(Perf(sync: new() { ["PageSize"] = "100", ["Parallelism"] = "8", ["LeaseDuration"] = "00:00:00" })).Timeout(300)
            .Expect("No duplicates under contention; all created").Run(async ctx =>
            {
                await Load(ctx, 600, "PERF-012");
                var workers = Enumerable.Range(2, 3).Select(i => ctx.Lab.AddWorker($"worker-{i}", new() { ["LeaseDuration"] = "00:00:00" })).ToList();
                var clock = Stopwatch.StartNew();
                var runs = await Task.WhenAll(new[] { ctx.Lab.Worker.RunAsync("w1") }.Concat(workers.Select(w => w.RunAsync(w.Name))));
                for (var i = 0; i < runs.Length; i++) ctx.RecordRun($"worker {i + 1}", runs[i]);
                await ctx.RunUntilSettledAsync("settle");
                var stats = await ctx.Lab.SapStatsAsync();
                ctx.Equal("duplicates", 0, stats.DuplicateReferences, "INV-01");
                ctx.Equal("all created", 600, stats.Total);
                ctx.Evidence("metrics", new { seconds = clock.Elapsed.TotalSeconds, perSecond = 600 / clock.Elapsed.TotalSeconds, failedRuns = runs.Count(r => r.Kind == RunOutcomeKind.Failed) });
            });

        c.Add("PERF-013", P, "Large line-item collections (100 requisitions × 50 lines)", Medium, Performance).Lab(Perf()).Timeout(300)
            .Expect("All lines mapped; recorded").Run(async ctx =>
            {
                await Load(ctx, 100, "PERF-013", lines: 50);
                await Measure(ctx, "PERF-013", 100);
                var sample = (await ctx.Lab.SapDocumentsAsync()).First();
                ctx.Equal("lines per document", 50, sample["to_PurchaseReqnItem"]!.AsArray().Count);
            });

        c.Add("PERF-014", P, "Database contention (32 parallel dispatchers)", Medium, Performance)
            .Lab(Perf(sync: new() { ["PageSize"] = "100", ["Parallelism"] = "32" })).Timeout(300)
            .Expect("SQLite copes (WAL, 30 s busy timeout); no duplicates; recorded").Run(async ctx =>
            {
                await Load(ctx, 800, "PERF-014");
                await Measure(ctx, "PERF-014", 800);
            });

        c.Add("PERF-015", P, "Very large reconciliation report (5,000 synced)", Medium, Performance)
            .Lab(new() { Dataset = "Generated", Count = 5_000, Seed = 3, CaptureHttp = false, Sync = new() { ["PageSize"] = "100", ["Parallelism"] = "16" } })
            .Timeout(600).Expect("Report built against a full SAP scan; time recorded").Run(async ctx =>
            {
                await ctx.RunUntilSettledAsync();
                var clock = Stopwatch.StartNew();
                var report = await ctx.Lab.ReconcileAsync();
                clock.Stop();
                ctx.Evidence("report", new { seconds = clock.Elapsed.TotalSeconds, invariants = report.Invariants.Count, states = report.StateTotals });
                ctx.Check("no invariant fails", report.Invariants.All(i => i.Outcome != Procurement.Sync.Reporting.InvariantOutcome.Fail), "none", string.Join(",", report.Invariants.Where(i => i.Outcome == Procurement.Sync.Reporting.InvariantOutcome.Fail).Select(i => i.Name)));
                PerfBaseline.Compare(ctx, "PERF-015", 5000 / Math.Max(0.001, clock.Elapsed.TotalSeconds));
            });

        c.Add("PERF-016", P, "Prolonged SAP outage under load", High, Performance, Recovery).Covered("SAP-025")
            .Lab(Perf()).Timeout(300).Expect("300 records queued through a 3-run outage, then all created once").Run(async ctx =>
            {
                await Load(ctx, 300, "PERF-016");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                for (var i = 0; i < 3; i++) { await ctx.RunSyncAsync($"outage {i + 1}"); await Task.Delay(1100); }
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Measure(ctx, "PERF-016", 300);
                ctx.Equal("all created", 300, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("PERF-017", P, "System restart under load", Critical, Performance, Recovery).Lab(Perf()).Timeout(300)
            .Expect("A crash after ~200 creates, restart, and the rest: every record once").Run(async ctx =>
            {
                await Load(ctx, 1000, "PERF-017");
                var creates = 0;
                ctx.Lab.Crash.ArmAfterSapResponse("after ~200 creates", (rq, rs) => LabHooks.IsSapCreate(rq) && Interlocked.Increment(ref creates) >= 200);
                var crashed = await ctx.RunSyncAsync("load, then crash");
                ctx.Equal("crashed", RunOutcomeKind.Crashed, crashed.Kind);
                await ctx.RestartAsync();
                await Measure(ctx, "PERF-017", 1000);
                ctx.Equal("all created once", 1000, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("PERF-018", P, "Memory pressure: repeated runs do not grow memory", Medium, Performance).Lab(Perf(capture: 0)).Timeout(300)
            .Expect("Managed memory after the 5th rerun within 2× of the 1st (no leak per run)").Run(async ctx =>
            {
                await Load(ctx, 1000, "PERF-018");
                await ctx.RunUntilSettledAsync();
                var samples = new List<double>();
                for (var i = 0; i < 5; i++)
                {
                    await ctx.Lab.RunAsync($"rerun {i + 1}");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    samples.Add(GC.GetTotalMemory(true) / 1024.0 / 1024.0);
                }
                ctx.Evidence("managed MB after each rerun", samples.Select(s => Math.Round(s, 1)));
                ctx.Check("no growth across reruns", samples[^1] <= samples[0] * 2 + 50, $"<= {samples[0] * 2 + 50:0} MB", $"{samples[^1]:0.0} MB");
            });

        c.Add("PERF-019", P, "Slow browser rendering of large tables", Medium, Performance).Covered("web/e2e UX-013")
            .Inv().Run(ctx => Frontend.FromPlaywright(ctx, "UX-013"));

        // The UX scenarios run in Playwright; their results are read back here.
        foreach (var (id, title) in new[]
        {
            ("UX-001", "Empty dashboard"), ("UX-002", "Loading dashboard"), ("UX-003", "API unavailable"), ("UX-004", "Expired user session"),
            ("UX-005", "Slow network"), ("UX-006", "Broken network connection"), ("UX-007", "Browser refresh during an active run"),
            ("UX-008", "Two tabs controlling one run"), ("UX-009", "Browser back/forward navigation"), ("UX-010", "Invalid form inputs"),
            ("UX-011", "Duplicate submit clicks"), ("UX-012", "Repeated retry clicks"), ("UX-013", "Large data table"),
            ("UX-014", "Filter combined with pagination"), ("UX-015", "Export after filtering"), ("UX-016", "Form with unsaved changes"),
            ("UX-017", "Incomplete requisition form"), ("UX-018", "API returns an unexpected schema"), ("UX-019", "Long validation messages"),
            ("UX-020", "Keyboard navigation"), ("UX-021", "Screen-reader accessibility (axe)"), ("UX-022", "Mobile and tablet layout"),
            ("UX-023", "Dark and light themes"), ("UX-024", "Timezone differences"), ("UX-025", "Locale-specific formatting"),
            ("UX-026", "Browser says success, backend fails (advanced scenario N)"),
        })
        {
            c.Add(id, Categories.Frontend, title, id is "UX-011" or "UX-026" or "UX-003" ? High : Medium, Functional)
                .Describe("Executed by the Playwright suite in web/e2e; this entry reads its result from reports/playwright/results.json (never assumed).")
                .Covered($"web/e2e (test titled '{id} …')").Inv()
                .Expect("The Playwright test with this ID passed in the latest e2e run").Run(ctx => Frontend.FromPlaywright(ctx, id));
        }
    }
}

internal static class Frontend
{
    public static Task FromPlaywright(ScenarioContext ctx, string id)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcurementIntegration.slnx"))) dir = dir.Parent;
        var path = Path.Combine(dir?.FullName ?? ".", "reports", "playwright", "results.json");
        if (!File.Exists(path)) ctx.Block("No Playwright results yet: run `npm run test:e2e` in web/ (with the Test Lab API running) to produce reports/playwright/results.json.");

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var matches = new List<(string Title, bool Ok, string Status, double Ms)>();
        void Walk(JsonNode? suite)
        {
            foreach (var spec in suite?["specs"]?.AsArray() ?? [])
            {
                var title = spec!["title"]?.GetValue<string>() ?? "";
                if (!title.StartsWith(id, StringComparison.Ordinal)) continue;
                var results = spec["tests"]?.AsArray().SelectMany(t => t!["results"]?.AsArray() ?? []).ToList() ?? [];
                var last = results.LastOrDefault();
                matches.Add((title, spec["ok"]?.GetValue<bool>() == true, last?["status"]?.GetValue<string>() ?? "unknown", last?["duration"]?.GetValue<double>() ?? 0));
            }
            foreach (var child in suite?["suites"]?.AsArray() ?? []) Walk(child);
        }
        foreach (var s in root["suites"]?.AsArray() ?? []) Walk(s);
        if (matches.Count == 0) ctx.Block($"The latest Playwright run has no test titled '{id} …'.");
        var started = root["stats"]?["startTime"]?.GetValue<string>();
        ctx.Evidence("playwright", new { file = path, runStarted = started, tests = matches.Select(m => new { m.Title, m.Ok, m.Status, m.Ms }) });
        foreach (var m in matches) ctx.Check($"Playwright: {m.Title}", m.Ok, "passed", m.Status);
        return Task.CompletedTask;
    }
}
