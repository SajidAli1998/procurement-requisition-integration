using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static class Breaker
{
    private static ScriptedFault Fail503(int count) => new() { Kind = FaultKind.Error, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = count, StatusCode = 503 };

    private static async Task Seed(ScenarioContext ctx, int n, string prefix)
    {
        for (var i = 1; i <= n; i++) await ctx.Lab.PutSourceAsync($"{prefix}-{i:D2}", Req.Valid($"{prefix}-{i:D2}"));
    }

    private static List<double> GapsMs(ScenarioContext ctx)
    {
        var posts = ctx.Lab.Capture.Exchanges.Where(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader")).OrderBy(e => e.Seq).ToList();
        return posts.Zip(posts.Skip(1), (a, b) => (b.At - a.At).TotalMilliseconds - a.DurationMs).Select(g => Math.Round(g, 1)).ToList();
    }

    public static void Register(CatalogueBuilder c)
    {
        const string B = Categories.Breaker;

        c.Add("CB-001", B, "One transient failure", High, FailureSimulation)
            .Expect("Retried once; created; breaker stays closed").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B001");
                await ctx.Lab.ScriptSapAsync(Fail503(1));
                var run = await ctx.RunSyncAsync();
                ctx.Equal("breaker opened", 0L, run.Result?.Transport.GetValueOrDefault("sap.circuitOpened") ?? 0);
                await ctx.ExpectCreatedOnceAsync("REQ-B001-01");
            });

        c.Add("CB-002", B, "Several consecutive failures within the retry budget", High, FailureSimulation)
            .Expect("Three 503s then success, in one dispatch; created").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B002");
                await ctx.Lab.ScriptSapAsync(Fail503(3));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-B002-01");
                ctx.Equal("create attempts", 4, ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader")));
            });

        c.Add("CB-003", B, "Failure threshold reached: breaker opens", Critical, FailureSimulation)
            .Expect("sap.circuitOpened ≥ 1; run ends early with work queued").Run(async ctx =>
            {
                await Seed(ctx, 10, "REQ-B003");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                var run = await ctx.RunSyncAsync("SAP failing");
                ctx.Check("breaker opened", (run.Result?.Transport.GetValueOrDefault("sap.circuitOpened") ?? 0) >= 1, ">= 1", run.Result?.Transport.GetValueOrDefault("sap.circuitOpened"));
                ctx.Check("run message names the breaker", run.Result?.Message?.Contains("circuit breaker") == true, "circuit breaker", run.Result?.Message);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync();
            });

        c.Add("CB-004", B, "While open, requests are not sent and attempts not spent", Critical, FailureSimulation)
            .Sync(("Parallelism", "8")).Expect("Calls short-circuit locally; records keep their attempt budget").Run(async ctx =>
            {
                await Seed(ctx, 20, "REQ-B004");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("SAP failing");
                var sent = ctx.Lab.Capture.Exchanges.Count(e => e.Channel.EndsWith("→sap"));
                await using (var db = ctx.Lab.Db())
                {
                    var untouched = await db.Requisitions.CountAsync(r => r.Attempts == 0);
                    ctx.Check("most records never sent once the breaker opened", untouched >= 10, ">= 10 with 0 attempts", untouched);
                }
                ctx.Check("far fewer SAP requests than records × retries", sent < 20 * 5, "< 100", sent);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync();
            });

        c.Add("CB-005", B, "Breaker stays open during its break duration", High, FailureSimulation)
            .Sync(("Resilience:BreakerBreakDuration", "00:00:03")).Expect("A run inside the break makes no SAP writes").Run(async ctx =>
            {
                await Seed(ctx, 10, "REQ-B005");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens the breaker");
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                var before = ctx.Lab.Capture.Exchanges.Count(e => e.Channel.EndsWith("→sap") && e.Method == "POST");
                await ctx.RunSyncAsync("immediately again (breaker still open)");
                var after = ctx.Lab.Capture.Exchanges.Count(e => e.Channel.EndsWith("→sap") && e.Method == "POST");
                ctx.Equal("creates sent while open", 0, after - before);
                await Task.Delay(3200);
                await ctx.RunUntilSettledAsync("after the break");
            });

        c.Add("CB-006", B, "Cooldown expires: the next run proceeds", High, Recovery)
            .Expect("After the break duration the breaker lets traffic through; all created").Run(async ctx =>
            {
                await Seed(ctx, 8, "REQ-B006");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens");
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                ctx.Simulated("The break duration passes");
                await ctx.RunUntilSettledAsync("after cooldown");
                ctx.Equal("all 8 created", 8, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("CB-007", B, "Half-open probe succeeds and the breaker closes", High, Recovery)
            .Describe("In half-open state the breaker lets one probe through; requests sent alongside it are refused locally (no attempt spent) and wait. Once the probe succeeds the breaker is closed and the next run drains the queue.")
            .Expect("The probe gets through and succeeds; the following run sends everything without the breaker opening").Run(async ctx =>
            {
                await Seed(ctx, 8, "REQ-B007");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens");
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                var probe = await ctx.RunSyncAsync("probe run (half-open)");
                var before = (await ctx.Lab.SapStatsAsync()).Total;
                ctx.Check("the probe reached SAP and succeeded", before >= 1, ">= 1 created", before);
                var next = await ctx.RunSyncAsync("next run (closed)");
                ctx.Equal("breaker did not reopen", 0L, next.Result?.Transport.GetValueOrDefault("sap.circuitOpened") ?? 0);
                ctx.Equal("queue drained", 0, next.Result?.Pending);
                ctx.Equal("all created once", 8, (await ctx.Lab.SapStatsAsync()).Total);
                _ = probe;
            });

        c.Add("CB-008", B, "Half-open probe fails and the breaker reopens", High, FailureSimulation)
            .Expect("Still failing after the break: reopens, work stays queued, nothing lost").Run(async ctx =>
            {
                await Seed(ctx, 8, "REQ-B008");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens");
                await Task.Delay(1200);
                var again = await ctx.RunSyncAsync("probe fails");
                ctx.Check("reopened", again.Result?.Message?.Contains("circuit breaker") == true, "circuit breaker", again.Result?.Message);
                ctx.Equal("still queued", 8, again.Result?.Pending);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync();
            });

        c.Add("CB-009", B, "Breaker closes after recovery and stays closed", Medium, Recovery)
            .Expect("Two clean runs after recovery: no breaker events").Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-B009");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens");
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunSyncAsync("recovers");
                await ctx.Lab.PutSourceAsync("REQ-B009-99", Req.Valid("REQ-B009-99"));
                var later = await ctx.RunSyncAsync("later");
                ctx.Equal("breaker opened later", 0L, later.Result?.Transport.GetValueOrDefault("sap.circuitOpened") ?? 0);
                ctx.Equal("all created", 7, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("CB-010", B, "Concurrent requests while the breaker is open", High, FailureSimulation)
            .Sync(("Parallelism", "16")).Expect("No request leaks through; no attempt counted; nothing quarantined").Run(async ctx =>
            {
                await Seed(ctx, 40, "REQ-B010");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens with 16 in flight");
                ctx.Equal("quarantined", 0, (await ctx.Lab.OpenQuarantineAsync()).Count);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync();
                ctx.Equal("all 40 created", 40, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("CB-011", B, "Retry limit exceeded", High, FailureSimulation)
            .Sync(("MaxDispatchAttempts", "2"), ("Resilience:BreakerMinimumThroughput", "100"))
            .Expect("At most 5 transport tries per dispatch; after 2 dispatches quarantined RetryExhausted").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B011");
                await ctx.Lab.ScriptSapAsync(Fail503(100));
                await ctx.RunSyncAsync("dispatch 1");
                await ctx.RunSyncAsync("dispatch 2");
                var posts = ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader"));
                ctx.Equal("transport tries (2 dispatches × 5)", 10, posts);
                await ctx.ExpectQuarantinedAsync("REQ-B011-01", QuarantineReason.RetryExhausted);
            });

        c.Add("CB-012", B, "Exponential backoff between transport retries", Medium, FailureSimulation)
            .Lab(new() { FastWaits = false, Sync = new() { ["Resilience:BaseDelay"] = "00:00:00.100", ["Resilience:BreakerMinimumThroughput"] = "100" } })
            .Expect("Waits grow between attempts (base 100 ms)").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B012");
                await ctx.Lab.ScriptSapAsync(Fail503(4));
                await ctx.RunSyncAsync();
                var gaps = GapsMs(ctx);
                ctx.Evidence("gaps between attempts (ms)", gaps);
                ctx.Check("waits grow overall (last > first)", gaps.Count >= 3 && gaps[^1] > gaps[0], "increasing", string.Join(", ", gaps));
            });

        c.Add("CB-013", B, "Backoff jitter", Low, FailureSimulation)
            .Lab(new() { FastWaits = false, Sync = new() { ["Resilience:BaseDelay"] = "00:00:00.100", ["Resilience:BreakerMinimumThroughput"] = "100" } })
            .Expect("Waits are not exact powers of two (jitter spreads retries)").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B013");
                await ctx.Lab.ScriptSapAsync(Fail503(3));
                await ctx.RunSyncAsync();
                var gaps = GapsMs(ctx);
                ctx.Evidence("gaps (ms)", gaps);
                ctx.Check("not exactly 100/200/400", gaps.Count >= 2 && !(Math.Abs(gaps[0] - 100) < 1 && Math.Abs(gaps[1] - 200) < 1), "jittered", string.Join(", ", gaps));
            });

        c.Add("CB-014", B, "Retry-After in seconds is honoured", High, FailureSimulation)
            .Lab(new() { FastWaits = false }).Expect("The retry waits at least the 1 s SAP asked for").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B014");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Throttle, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", RetryAfterSeconds = 1 });
                await ctx.RunSyncAsync();
                var gaps = GapsMs(ctx);
                ctx.Check("waited ≥ 950 ms", gaps.Count == 1 && gaps[0] >= 950, ">= 950", string.Join(", ", gaps));
            });

        c.Add("CB-015", B, "Retry-After as an HTTP date is honoured", Medium, FailureSimulation)
            .Lab(new() { FastWaits = false }).Expect("The date form is understood (wait ≈ until that time)").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B015");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Throttle, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", RetryAfterSeconds = 2, RetryAfterAsDate = true });
                var run = await ctx.RunSyncAsync();
                ctx.Check("a wait was taken from the date", run.Result?.RetryWait > TimeSpan.FromMilliseconds(500), "> 0.5 s", run.Result?.RetryWait);
                await ctx.ExpectCreatedOnceAsync("REQ-B015-01");
            });

        c.Add("CB-016", B, "Timeout and retry combined", High, FailureSimulation)
            .Expect("Slow lookups time out per attempt and are retried (safe GET); total timeout bounds the call; created").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-B016");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Delay, Method = "GET", PathContains = "ExternalReference", Count = 2, DelayMs = 2500 });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-B016-01");
            });

        c.Add("CB-017", B, "Worker restart during cooldown", Medium, Recovery)
            .Describe("Breaker state lives in the process. A restarted process starts closed and probes SAP straight away; if SAP is still down it opens again quickly.")
            .Expect("No duplicates; breaker state starts fresh in each process").Run(async ctx =>
            {
                await Seed(ctx, 8, "REQ-B017");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                await ctx.RunSyncAsync("opens");
                await ctx.RestartAsync("during the cooldown");
                var after = await ctx.RunSyncAsync("new process, SAP still down");
                ctx.Check("the new process tried SAP (closed breaker)", after.Result?.Transport.GetValueOrDefault("sap.circuitOpened") >= 1, "reopened", after.Result?.Transport.GetValueOrDefault("sap.circuitOpened"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync();
                ctx.Equal("all created once", 8, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("CB-018", B, "Recovery after a prolonged outage", High, Recovery).Covered("SAP-025 (four outage runs, then recovery)")
            .Expect("Everything queued is created once after recovery").Run(async ctx =>
            {
                await Seed(ctx, 5, "REQ-B018");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                for (var i = 0; i < 3; i++) { await ctx.RunSyncAsync($"outage {i + 1}"); await Task.Delay(1100); }
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                await ctx.RunUntilSettledAsync();
                ctx.Equal("all created", 5, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("CB-019", B, "Several SAP tenants with independent breakers", Low, FailureSimulation)
            .Blocked("One integration instance talks to one SAP endpoint (one named HttpClient, one breaker). Multi-tenant SAP routing is not part of this build.");
    }
}
