using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static class Failures
{
    private static ScriptedFault SourceList(FaultKind kind, int count = 1, int status = 503, int skip = 0, string? body = null, int delay = 0) => new()
    {
        Kind = kind, Method = "GET", PathContains = "/api/requisitions?", Count = count, Skip = skip, StatusCode = status, Body = body, DelayMs = delay,
    };

    private static ScriptedFault Sap(FaultKind kind, string method = "POST", int count = 1, int status = 503, string? path = "A_PurchaseRequisitionHeader") => new()
    {
        Kind = kind, Method = method, PathContains = path, Count = count, StatusCode = status,
    };

    private static int Creates(ScenarioContext ctx, string? reference = null) => ctx.Lab.Capture.Exchanges.Count(e =>
        e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader", StringComparison.Ordinal) && (reference is null || (e.RequestBody?.Contains($"\"{reference}\"") ?? false)));

    private static async Task Seed(ScenarioContext ctx, int n, string prefix)
    {
        for (var i = 1; i <= n; i++) await ctx.Lab.PutSourceAsync($"{prefix}-{i:D2}", Req.Valid($"{prefix}-{i:D2}"));
    }

    private static async Task ExpectAll(ScenarioContext ctx, int n, string prefix)
    {
        var stats = await ctx.Lab.SapStatsAsync();
        ctx.Equal($"all {n} in SAP", n, (await ctx.Lab.SapDocumentsAsync()).Count(d => d["ExternalReference"]!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal)));
        ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
    }

    public static void Register(CatalogueBuilder c)
    {
        const string S = Categories.Source;

        c.Add("SRC-001", S, "Source returns HTTP 500 on a page", High, FailureSimulation)
            .Sync(("PageSize", "5")).Faults("Second page answers 500 (more times than the retries allow)")
            .Expect("Run reports 'stopped early'; pages already read are sent; next run resumes from the checkpoint; all 15 created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 15, "REQ-S001");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Error, count: 20, status: 500, skip: 1));
                var first = await ctx.RunSyncAsync("source fails on page 2");
                ctx.Check("run says the read stopped early", first.Result?.Message?.Contains("stopped early") == true, "stopped early", first.Result?.Message);
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync("source back");
                await ExpectAll(ctx, 15, "REQ-S001");
            });

        c.Add("SRC-002", S, "Source returns 503 briefly (retried within the run)", High, FailureSimulation)
            .Faults("First list request answers 503 twice").Expect("Transport retries succeed; everything created in the same run")
            .Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-S002");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Error, count: 2, status: 503));
                var run = await ctx.RunSyncAsync();
                ctx.Equal("run outcome", RunOutcomeKind.Completed, run.Kind);
                await ExpectAll(ctx, 3, "REQ-S002");
            });

        c.Add("SRC-003", S, "Source times out", High, FailureSimulation)
            .Faults("First list request takes 3 s (attempt timeout 2 s)").Expect("Attempt times out, is retried (reads are safe to repeat), run completes")
            .Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-S003");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Delay, delay: 3000));
                await ctx.RunSyncAsync();
                await ExpectAll(ctx, 2, "REQ-S003");
            });

        c.Add("SRC-004", S, "Source returns malformed JSON", High, FailureSimulation)
            .Faults("First list response is '{\"items\": [ {' (truncated)").Expect("Treated as an outage: page not processed, checkpoint unchanged, run completes with an issue; next run reads everything")
            .Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-S004");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Respond, status: 200, body: "{\"items\": [ {"));
                var first = await ctx.RunSyncAsync("malformed page");
                ctx.Check("run completed with an issue (not a crash)", first.Kind == RunOutcomeKind.CompletedWithIssues, "CompletedWithIssues", first.Kind);
                ctx.Check("issue names the unreadable page", first.Result?.Message?.Contains("unreadable") == true, "unreadable page", first.Result?.Message);
                await ctx.RunSyncAsync("next run");
                await ExpectAll(ctx, 3, "REQ-S004");
            });

        c.Add("SRC-005", S, "Source returns an empty page", Medium, FailureSimulation)
            .Faults("First list response: no items, no cursor").Expect("Nothing processed, checkpoint not moved; next run reads all")
            .Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-S005");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Respond, status: 200, body: "{\"items\":[],\"nextCursor\":null,\"hasMore\":false}"));
                await ctx.RunSyncAsync("empty page");
                ctx.Equal("nothing created yet", 0, (await ctx.Lab.SapStatsAsync()).Total);
                await ctx.RunSyncAsync("next run");
                await ExpectAll(ctx, 3, "REQ-S005");
            });

        c.Add("SRC-006", S, "Source repeats a whole page", High, DuplicateStress, FailureSimulation)
            .Sync(("PageSize", "3")).Faults("The second list call returns page 1 again (same cursor)").Expect("Repeats recognised as unchanged; every record created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 7, "REQ-S006");
                var firstPage = await ctx.Lab.SourceAdmin.GetStringAsync("/api/requisitions?limit=3");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Respond, status: 200, skip: 1, body: firstPage));
                await ctx.RunSyncAsync();
                await ExpectAll(ctx, 7, "REQ-S006");
            });

        c.Add("SRC-007", S, "Source skips records (lagging index): completeness scan finds them", Critical, FailureSimulation)
            .Describe("Two records are missing from the list while their neighbours are read, so the cursor moves past them. A cursor alone can never find them again; the periodic full scan (FullScanInterval) does. This scenario proves both halves.")
            .Sync(("FullScanInterval", "00:00:01")).Expect("Run 1 misses them (detected: approved in the source, unknown to the integration); after the scan interval they are found and created")
            .Without("INV-02")
            .Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-S007");
                await ctx.Lab.HideFromListAsync("REQ-S007-02");
                await ctx.Lab.HideFromListAsync("REQ-S007-04");
                await ctx.RunSyncAsync("index lags: two records missing from the list");
                ctx.Equal("missed records unknown to the integration", null, (await ctx.Lab.RecordAsync("REQ-S007-02"))?.State.ToString());
                await ctx.Lab.HideFromListAsync("REQ-S007-02", false);
                await ctx.Lab.HideFromListAsync("REQ-S007-04", false);
                ctx.Step("The index catches up, but both records are now behind the saved cursor");
                await Task.Delay(1100);
                await ctx.RunSyncAsync("next run: periodic full scan");
                await ExpectAll(ctx, 6, "REQ-S007");
            });

        c.Add("SRC-008", S, "Records change while paging (moved behind the cursor)", High, FailureSimulation)
            .Sync(("PageSize", "3")).Faults("Before page 2 is read, a page-1 record is edited")
            .Expect("The edited record reappears later in the same read (its update time moved) and SAP ends with its latest version")
            .Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-S008");
                var calls = 0;
                ctx.Lab.Hooks.Before("source", r => LabHooks.IsSourceList(r) && ++calls == 2, async () =>
                {
                    var v2 = Req.Valid("REQ-S008-01").Set("lines[0].quantity", 7).Recalculate();
                    await ctx.Lab.EditSourceAsync("REQ-S008-01", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                });
                await ctx.RunUntilSettledAsync();
                await ExpectAll(ctx, 6, "REQ-S008");
                ctx.Equal("SAP has the edited quantity", 7m, (await ctx.Lab.SapDocumentsForAsync("REQ-S008-01"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });

        c.Add("SRC-009", S, "Records inserted while paging", High, FailureSimulation)
            .Sync(("PageSize", "3")).Expect("The new record (newest update time) is read in the same run; all created")
            .Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-S009");
                var calls = 0;
                ctx.Lab.Hooks.Before("source", r => LabHooks.IsSourceList(r) && ++calls == 2, () => ctx.Lab.PutSourceAsync("REQ-S009-99", Req.Valid("REQ-S009-99")));
                await ctx.RunSyncAsync();
                await ExpectAll(ctx, 7, "REQ-S009");
            });

        c.Add("SRC-010", S, "Records deleted while paging", Medium, FailureSimulation)
            .Sync(("PageSize", "3")).Expect("A record deleted before it was read is never sent; the rest are; accounting matches the source as it is now")
            .Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-S010");
                var calls = 0;
                ctx.Lab.Hooks.Before("source", r => LabHooks.IsSourceList(r) && ++calls == 2, () => ctx.Lab.DeleteSourceAsync("REQ-S010-06"));
                await ctx.RunSyncAsync();
                ctx.Equal("deleted record not in SAP", 0, await ctx.SapCountAsync("REQ-S010-06"));
                ctx.Equal("the other five in SAP", 5, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("SRC-011", S, "Source reports an incorrect total count", Low, FailureSimulation)
            .Blocked("The source API has no total count (cursor paging with hasMore only), so there is nothing to misreport. Completeness is checked by the periodic full scan and invariant 2 instead (SRC-007).");

        c.Add("SRC-012", S, "Bookmark corrupted", Critical, Recovery)
            .Expect("Source rejects the cursor (400); the integration re-reads from the start; nothing sent twice")
            .Run(async ctx =>
            {
                await Seed(ctx, 4, "REQ-S012");
                await ctx.RunSyncAsync("first run");
                await using (var db = ctx.Lab.Db())
                    await db.Checkpoints.Where(cp => cp.Stream == "source-requisitions").ExecuteUpdateAsync(s => s.SetProperty(cp => cp.Cursor, "%%garbage%%"));
                ctx.Step("Corrupt the saved cursor in the database");
                await ctx.Lab.PutSourceAsync("REQ-S012-05", Req.Valid("REQ-S012-05"));
                var run = await ctx.RunSyncAsync("run with a corrupt cursor");
                ctx.Check("run completed (no endless 'stopped early')", run.Kind is RunOutcomeKind.Completed or RunOutcomeKind.CompletedWithIssues, "Completed", run.Kind);
                ctx.Equal("re-read records recognised as unchanged (no creates for them)", 1, run.Count(RunEventKind.Created));
                await ExpectAll(ctx, 5, "REQ-S012");
            });

        c.Add("SRC-013", S, "Bookmark cannot be saved", High, Recovery)
            .Sync(("PageSize", "3")).Faults("The checkpoint write for the first page fails")
            .Expect("The page's staging rolls back with it; next run re-reads the page; all created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 5, "REQ-S013");
                ctx.Lab.DbFaults.Add("checkpoint save", DbFaults.SavesCheckpoint, DbFaultKind.SaveFails);
                var first = await ctx.RunSyncAsync("checkpoint save fails");
                ctx.Check("first run failed cleanly", first.Kind is RunOutcomeKind.Failed, "Failed", first.Kind);
                ctx.Equal("nothing sent from the rolled-back page", 0, (await ctx.Lab.SapStatsAsync()).Total);
                await ctx.RunUntilSettledAsync("next run");
                await ExpectAll(ctx, 5, "REQ-S013");
            });

        c.Add("SRC-014", S, "Bookmark points to an expired cursor", Medium, Recovery)
            .Faults("The source answers 400 'Invalid cursor' for the saved cursor").Expect("Same recovery as a corrupt cursor: read from the start")
            .Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-S014");
                await ctx.RunSyncAsync("first run");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Respond, Method = "GET", PathContains = "cursor=", StatusCode = 400, Body = "{\"title\":\"Invalid cursor\"}" });
                var run = await ctx.RunSyncAsync("cursor expired");
                ctx.Check("recovered by reading from the start", run.Kind is RunOutcomeKind.Completed or RunOutcomeKind.CompletedWithIssues, "Completed", run.Kind);
                await ExpectAll(ctx, 3, "REQ-S014");
            });

        c.Add("SRC-015", S, "Source authentication expires (401)", Medium, FailureSimulation)
            .Faults("List answers 401").Expect("Run reports the problem; nothing lost; next run proceeds")
            .Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-S015");
                await ctx.Lab.ScriptSourceAsync(SourceList(FaultKind.Respond, status: 401, body: "{\"title\":\"Token expired\"}"));
                var first = await ctx.RunSyncAsync("credentials rejected");
                ctx.Check("problem reported, run not crashed", first.Kind == RunOutcomeKind.CompletedWithIssues, "CompletedWithIssues", first.Kind);
                await ctx.RunSyncAsync("credentials renewed");
                await ExpectAll(ctx, 2, "REQ-S015");
            });

        c.Add("SRC-016", S, "Source temporarily unavailable for a whole run", High, FailureSimulation)
            .Faults("Every list call answers 503 during run 1").Expect("Run 1 reads nothing and says so; run 2 completes")
            .Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-S016");
                await ctx.Lab.SourceFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                var first = await ctx.RunSyncAsync("source down");
                ctx.Check("issue reported", first.Kind == RunOutcomeKind.CompletedWithIssues, "CompletedWithIssues", first.Kind);
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync("source back");
                await ExpectAll(ctx, 3, "REQ-S016");
            });

        FieldsAndFormats.Quarantine(c, "SRC-017", S, "Source returns a partial record (no lines, no total)", High,
            r => r.Drop("lines").Drop("totalAmount"), QuarantineReason.MissingRequiredField, "lines");

        c.Add("SRC-018", S, "Source has 10,000+ records", Medium, Performance)
            .Lab(new() { Dataset = "Generated", Count = 10_000, Seed = 7, CaptureHttp = false, Sync = new() { ["PageSize"] = "100", ["Parallelism"] = "16" } })
            .Timeout(600).Expect("Every approved, valid record created once; every record accounted for")
            .Run(async ctx =>
            {
                var runs = await ctx.RunUntilSettledAsync("10,000 records", maxRuns: 4);
                var source = await ctx.Lab.SourceSnapshotAsync();
                await using var db = ctx.Lab.Db();
                var tracked = await db.Requisitions.CountAsync();
                ctx.Equal("records tracked", source.Count, tracked);
                var stats = await ctx.Lab.SapStatsAsync();
                ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
                ctx.Equal("synced = SAP documents", await db.Requisitions.CountAsync(r => r.State == RecordState.Synced), stats.Total);
                var seconds = runs.Sum(r => r.DurationMs) / 1000;
                ctx.Evidence("throughput", new { records = source.Count, seconds = Math.Round(seconds, 1), perSecond = Math.Round(source.Count / seconds, 1) });
                ctx.Info($"{source.Count} records in {seconds:0.0}s");
            });

        c.Add("SRC-019", S, "Source unavailable after the SAP creation (write-back fails)", High, Recovery)
            .Faults("Status write-back PUTs answer 503 throughout run 1").Expect("SAP document kept; write-back pending and retried next run; source shows the SAP reference")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-S019");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "PUT", PathContains = "erp-status", Count = 20, StatusCode = 503 });
                await ctx.RunSyncAsync("write-back fails");
                ctx.Equal("write-back pending", true, (await ctx.Lab.RecordAsync("REQ-S019-01"))?.WritebackPending);
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync("source back");
                ctx.Equal("write-back done", false, (await ctx.Lab.RecordAsync("REQ-S019-01"))?.WritebackPending);
                ctx.Check("source shows the SAP reference", (await ctx.Lab.SourceRecordAsync("REQ-S019-01"))?["erpSync"]?["reference"] is not null, "erpSync.reference", "");
                await ctx.ExpectCreatedOnceAsync("REQ-S019-01");
            });

        c.Add("SRC-020", S, "Worker crashes halfway through a page", Critical, Recovery)
            .Sync(("PageSize", "3")).Faults("Process dies while committing the second page").Expect("Restart resumes from the last committed page; nothing lost, nothing twice")
            .Run(async ctx =>
            {
                await Seed(ctx, 7, "REQ-S020");
                var saves = 0;
                ctx.Lab.DbFaults.Add("crash on the 2nd checkpoint", db => DbFaults.SavesCheckpoint(db) && ++saves == 2, DbFaultKind.Crash);
                var crashed = await ctx.RunSyncAsync("crashes mid-read");
                ctx.Equal("run outcome", RunOutcomeKind.Crashed, crashed.Kind);
                await ctx.RestartAsync();
                await ctx.RunUntilSettledAsync("after restart");
                await ExpectAll(ctx, 7, "REQ-S020");
            });

        c.Add("SRC-021", S, "Status write-back rejected (500) then accepted", Medium, Recovery)
            .Faults("Two write-backs answer 500").Expect("Retried on a later run; source eventually shows the status")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-S021");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "PUT", PathContains = "erp-status", Count = 2, StatusCode = 500 });
                await ctx.RunUntilSettledAsync();
                await ctx.RunSyncAsync("later run");
                ctx.Equal("write-back done", false, (await ctx.Lab.RecordAsync("REQ-S021-01"))?.WritebackPending);
            });
        const string P = Categories.Sap;

        c.Add("SAP-001", P, "429 with Retry-After", Critical, FailureSimulation)
            .Faults("Two creates answer 429 Retry-After: 2").Expect("Waits as asked (RetryWait > 0), then created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-P001");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Throttle, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = 2, RetryAfterSeconds = 2 });
                var run = await ctx.RunSyncAsync();
                ctx.Check("Retry-After honoured (scaled wait recorded)", run.Result?.RetryWait > TimeSpan.Zero, "> 0", run.Result?.RetryWait);
                await ctx.ExpectCreatedOnceAsync("REQ-P001-01");
            });

        c.Add("SAP-002", P, "429 without Retry-After", High, FailureSimulation)
            .Faults("Two creates answer 429 with no Retry-After header").Expect("Bounded exponential backoff, then created")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-P002");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Throttle, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = 2, OmitRetryAfter = true });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-P002-01");
                ctx.Equal("create attempts", 3, Creates(ctx));
            });

        c.Add("SAP-003", P, "503 (refused before processing)", High, FailureSimulation)
            .Faults("Two creates answer 503").Expect("Retried by the transport; created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-P003");
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Error, count: 2, status: 503));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-P003-01");
            });

        foreach (var (id, status, title) in new[] { ("SAP-004", 500, "500 on create"), ("SAP-005", 502, "502 gateway error on create") })
        {
            c.Add(id, P, title + ": not blindly repeated", Critical, FailureSimulation)
                .Faults($"First create answers {status} (before processing)").Expect("Transport does not repeat a POST after 500/502; the next attempt looks in SAP first; created once")
                .Run(async ctx =>
                {
                    var rid = $"REQ-{id}";
                    await ctx.Lab.PutSourceAsync(rid, Req.Valid(rid));
                    await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Error, status: status));
                    var first = await ctx.RunSyncAsync($"create answers {status}");
                    ctx.Equal("creates in the first run (no transport retry)", 1, Creates(ctx));
                    await ctx.RunUntilSettledAsync();
                    await ctx.ExpectCreatedOnceAsync(rid);
                });
        }

        c.Add("SAP-006", P, "504 after SAP committed: ambiguous outcome resolved", Critical, FailureSimulation)
            .Faults("SAP creates the document, then answers 504").Expect("Not repeated blindly; next attempt finds the document by lookup and links it; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-006", Req.Valid("REQ-SAP-006"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenRespond, status: 504));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-006");
            });

        c.Add("SAP-007", P, "400 Bad Request: not retried", High, Negative)
            .Faults("Create answers 400 with an SAP error").Expect("One attempt; quarantined SapRejectedPermanent with SAP's message")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-007", Req.Valid("REQ-SAP-007"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, status: 400) with { Body = "{\"error\":{\"code\":\"06/402\",\"message\":{\"lang\":\"en\",\"value\":\"Field MATKL is mandatory\"}}}" });
                await ctx.RunSyncAsync();
                ctx.Equal("create attempts", 1, Creates(ctx));
                await ctx.ExpectQuarantinedAsync("REQ-SAP-007", QuarantineReason.SapRejectedPermanent);
            });

        foreach (var (id, status) in new[] { ("SAP-008", 401), ("SAP-009", 403) })
        {
            c.Add(id, P, $"{status} from SAP: a credentials problem, not bad data", Critical, FailureSimulation, Security)
                .Faults($"Creates answer {status} during run 1").Expect("Run stops and says SAP refused the credentials; nothing quarantined; nothing retried as transient; next run (fixed credentials) creates all")
                .Run(async ctx =>
                {
                    await Seed(ctx, 4, $"REQ-{id}");
                    await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, count: 50, status: status) with { Body = "{\"error\":{\"code\":\"/IWFND/CM_CONSUMER/101\",\"message\":{\"value\":\"Not authorized\"}}}" });
                    var first = await ctx.RunSyncAsync("credentials refused");
                    ctx.Check("run message names the credentials", first.Result?.Message?.Contains("credentials") == true, "credentials", first.Result?.Message);
                    ctx.Equal("quarantined", 0, (await ctx.Lab.OpenQuarantineAsync()).Count);
                    var perRecord = ctx.Lab.Capture.Exchanges.Where(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader"))
                        .GroupBy(e => e.RequestBody).Max(g => g.Count());
                    ctx.Equal("tries per requisition (no transport retry of a 401/403)", 1, perRecord);
                    ctx.Check("dispatch stopped: at most one parallel batch was sent", Creates(ctx) <= 4, "<= 4 (parallelism)", Creates(ctx));
                    await ctx.Lab.ClearFaultsAsync();
                    await ctx.RunUntilSettledAsync("credentials fixed");
                    await ExpectAll(ctx, 4, $"REQ-{id}");
                });
        }

        c.Add("SAP-010", P, "404 on the lookup: never read as 'no match'", Critical, FailureSimulation)
            .Faults("The lookup answers 404 (entity set unavailable) for the first run").Expect("No create without a successful lookup; deferred; created later")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-010", Req.Valid("REQ-SAP-010"));
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Respond, Method = "GET", PathContains = "ExternalReference", Count = 1, StatusCode = 404, Body = "{}" });
                await ctx.RunSyncAsync("lookup 404");
                ctx.Equal("creates while the lookup failed", 0, Creates(ctx));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-010");
            });

        c.Add("SAP-011", P, "409 duplicate: verified before linking", Critical, DuplicateStress).Covered("DUP-018 runs the full case")
            .Expect("Linked only to a document whose reference matches")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-011", Req.Valid("REQ-SAP-011"));
                var existing = await ctx.Lab.InsertSapAsync("REQ-SAP-011", quantity: 2, price: 1200m);
                await ctx.Lab.SapFaultsAsync(new FaultSettings { ReferenceSearchLag = true });
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, status: 409) with
                {
                    Body = $"{{\"error\":{{\"code\":\"DUPLICATE_REFERENCE\",\"message\":{{\"value\":\"exists\"}},\"innererror\":{{\"existingDocument\":\"{existing}\"}}}}}}",
                });
                await ctx.RunSyncAsync();
                ctx.Equal("linked to the verified document", existing, (await ctx.Lab.RecordAsync("REQ-SAP-011"))?.SapNumber);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
            });

        c.Add("SAP-012", P, "409 version conflict: re-read and resend the current version", Critical, FailureSimulation)
            .Faults("SAP says it knows a newer version; meanwhile the source has v2").Expect("Re-reads the source, sends v2 once; one document holding v2")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-012", Req.Valid("REQ-SAP-012"));
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, async () =>
                {
                    var v2 = Req.Valid("REQ-SAP-012").Set("lines[0].quantity", 6).Recalculate();
                    await ctx.Lab.EditSourceAsync("REQ-SAP-012", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                });
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, status: 409) with
                {
                    Body = "{\"error\":{\"code\":\"VERSION_CONFLICT\",\"message\":{\"value\":\"Version 1 is out of date; the current version is 2\"},\"innererror\":{\"currentVersion\":2}}}",
                });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-012");
                var doc = (await ctx.Lab.SapDocumentsForAsync("REQ-SAP-012"))[0];
                ctx.Equal("SAP holds v2's quantity", 6m, doc["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
                ctx.Check("refresh recorded", (await ctx.Lab.EventsAsync("REQ-SAP-012")).Any(e => e.Kind == RunEventKind.VersionRefreshed), "VersionRefreshed", "");
            });

        c.Add("SAP-013", P, "422 validation error from SAP", High, Negative)
            .Expect("Quarantined SapRejectedPermanent with SAP's reason; not retried")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-013", Req.Valid("REQ-SAP-013"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, status: 422) with { Body = "{\"error\":{\"code\":\"ME/083\",\"message\":{\"value\":\"Cost center CC-1000-10 is blocked\"}}}" });
                await ctx.RunSyncAsync();
                await ctx.ExpectQuarantinedAsync("REQ-SAP-013", QuarantineReason.SapRejectedPermanent);
                ctx.Check("SAP's reason kept", (await ctx.Lab.OpenQuarantineAsync("REQ-SAP-013"))[0].Summary.Contains("blocked"), "Cost center … is blocked", "");
            });

        c.Add("SAP-014", P, "200/201 with a malformed body", Critical, FailureSimulation)
            .Faults("SAP creates the document, then sends half a JSON body").Expect("Outcome treated as unknown; lookup finds the document; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-014", Req.Valid("REQ-SAP-014"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenTransform) with { Transform = BodyTransform.Truncate });
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-014");
            });

        c.Add("SAP-015", P, "201 without a SAP document number (phantom success)", Critical, FailureSimulation)
            .Describe("Advanced scenario K. SAP answers 201 but the body has no document number; nothing was actually created.")
            .Faults("Create answered 201 '{}' without processing").Expect("Never counted as success: unresolved, lookup finds nothing, created properly later; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-015", Req.Valid("REQ-SAP-015"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Respond, status: 201) with { Body = "{}" });
                var first = await ctx.RunSyncAsync("phantom 201");
                var record = await ctx.Lab.RecordAsync("REQ-SAP-015");
                ctx.Check("not counted as synchronised after the phantom 201", record?.State != RecordState.Synced, "not Synced", record?.State, "INV-03");
                ctx.Equal("created events in that run", 0, first.Count(RunEventKind.Created));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-015");
            });

        c.Add("SAP-016", P, "Connection closed after the request (outcome unknown)", Critical, FailureSimulation)
            .Faults("Connection dropped before SAP processed the create").Expect("Verified by lookup before resending; created once")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-016", Req.Valid("REQ-SAP-016"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.Abort));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-016");
            });

        c.Add("SAP-017", P, "Slow response beyond the timeout (SAP commits late)", Critical, FailureSimulation)
            .Faults("Create delayed 3 s (attempt timeout 2 s); SAP then commits it anyway").Expect("Timed-out POST not repeated; lookup later finds the late document; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-017", Req.Valid("REQ-SAP-017"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Delay, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", DelayMs = 3000 });
                await ctx.RunSyncAsync("create times out");
                await Task.Delay(1500);
                ctx.Simulated("Time passes before the next scheduled run");
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-017");
            });

        c.Add("SAP-018", P, "SAP creates the record but answers 500", Critical, FailureSimulation)
            .Expect("POST not repeated by the transport; lookup finds the document; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-018", Req.Valid("REQ-SAP-018"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenRespond, status: 500));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-018");
            });

        c.Add("SAP-019", P, "SAP creates the record but answers 503 (keys honoured)", Critical, FailureSimulation)
            .Expect("The transport retry carries the same Idempotency-Key and is replayed; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-019", Req.Valid("REQ-SAP-019"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenRespond, status: 503));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-019");
            });

        c.Add("SAP-019B", P, "SAP creates the record but answers 503 (keys ignored, 503 not trusted)", Critical, FailureSimulation)
            .Describe("Against a real SAP that ignores Idempotency-Key, set Resilience:TrustServiceUnavailable=false: a 503 on a create is then handed back to the pipeline, which looks in SAP before resending.")
            .Sync(("Resilience:TrustServiceUnavailable", "false")).Expect("No blind repeat; lookup finds the document; one document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-019B", Req.Valid("REQ-SAP-019B"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenRespond, status: 503));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-019B");
                ctx.Equal("create requests", 1, Creates(ctx));
            });

        c.Add("SAP-020", P, "SAP returns the wrong document number", Critical, FailureSimulation)
            .Faults("SAP creates document N but the body names 9999999999 (Location still names N)").Expect("Mismatch detected, outcome treated as unknown, lookup links the real document N")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-020", Req.Valid("REQ-SAP-020"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenTransform) with { Transform = BodyTransform.SetField, Field = "PurchaseRequisition", Value = "\"9999999999\"" });
                await ctx.RunUntilSettledAsync();
                var real = await ctx.ExpectCreatedOnceAsync("REQ-SAP-020");
                ctx.Check("linked to the real document, not the wrong number", real != "9999999999" && (await ctx.Lab.RecordAsync("REQ-SAP-020"))?.SapNumber == real, real, (await ctx.Lab.RecordAsync("REQ-SAP-020"))?.SapNumber, "INV-03");
            });

        c.Add("SAP-021", P, "SAP response contains unexpected fields", Medium, Functional)
            .Expect("Tolerated; created once")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-021", Req.Valid("REQ-SAP-021"));
                await ctx.Lab.ScriptSapAsync(Sap(FaultKind.CommitThenTransform) with { Transform = BodyTransform.SetField, Field = "ZZ_CustomerExtension", Value = "{\"x\":1}" });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-021");
            });

        c.Add("SAP-022", P, "SAP lookup temporarily unavailable", Critical, FailureSimulation)
            .Faults("Lookups answer 503 beyond the retries").Expect("No create without a lookup (0 POSTs); created on a later run")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-022", Req.Valid("REQ-SAP-022"));
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "GET", PathContains = "ExternalReference", Count = 5, StatusCode = 503 });
                await ctx.RunSyncAsync("lookups failing");
                ctx.Equal("creates without a lookup", 0, Creates(ctx));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-SAP-022");
            });

        c.Add("SAP-023", P, "SAP reports an inconsistent status", High, FailureSimulation)
            .Faults("Status codes set to processing 'Q', release 'ZZ' (unknown)").Expect("Detected and reported (StatusIgnored with the codes); last known status kept")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-SAP-023", Req.Valid("REQ-SAP-023"));
                await ctx.RunSyncAsync();
                var number = (await ctx.Lab.RecordAsync("REQ-SAP-023"))!.SapNumber!;
                await ctx.Lab.SetSapRawStatusAsync(number, "Q", "ZZ");
                await ctx.RunSyncAsync("status poll sees unknown codes");
                var ev = (await ctx.Lab.EventsAsync("REQ-SAP-023")).LastOrDefault(e => e.Kind == RunEventKind.StatusIgnored);
                ctx.Check("unknown codes reported", ev?.Detail?.Contains("unrecognised status codes") == true, "unrecognised status codes", ev?.Detail);
                ctx.Equal("status kept", "Created", (await ctx.Lab.RecordAsync("REQ-SAP-023"))?.ErpStatus.ToString());
            });

        c.Add("SAP-024", P, "SAP rate-limits per client: each worker throttled on its own budget", High, FailureSimulation, Performance)
            .Lab(new() { FastWaits = false, Sap = new() { ["RateLimit:PermitsPerSecond"] = "5", ["RateLimit:Burst"] = "5" },
                Sync = new() { ["LeaseDuration"] = "00:00:00", ["Resilience:AttemptTimeout"] = "00:00:05", ["Resilience:TotalTimeout"] = "00:00:30", ["Resilience:BreakerSamplingDuration"] = "00:00:30" } })
            .Timeout(180).Expect("429s are per client id (X-Client-Id) and honoured with real Retry-After waits; both workers finish; no duplicates")
            .Run(async ctx =>
            {
                await Seed(ctx, 30, "REQ-P024");
                var second = ctx.Lab.AddWorker("worker-2", new() { ["LeaseDuration"] = "00:00:00" });
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("w1"), second.RunAsync("w2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                await ctx.RunUntilSettledAsync("settle");
                var throttled = ctx.Lab.Capture.Exchanges.Where(e => e.Status == 429).GroupBy(e => e.Channel).ToDictionary(g => g.Key, g => g.Count());
                ctx.Evidence("throttled channels", throttled);
                ctx.Check("SAP throttled the integration", ctx.Lab.Capture.Exchanges.Any(e => e.Status == 429), "some 429s", ctx.Lab.Capture.Exchanges.Count(e => e.Status == 429));
                await ExpectAll(ctx, 30, "REQ-P024");
            });

        c.Add("SAP-025", P, "SAP unavailable for hours", Critical, FailureSimulation, Recovery)
            .Describe("Advanced scenario F. Four scheduled runs while SAP answers 503 to everything (standing for hours of outage), then SAP recovers.")
            .Expect("Breaker opens; work stays queued (not lost, not quarantined); after recovery everything is created once")
            .Run(async ctx =>
            {
                await Seed(ctx, 12, "REQ-P025");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
                for (var i = 1; i <= 4; i++)
                {
                    ctx.Simulated($"Scheduled run {i} during the outage (an hour apart in real life)");
                    await ctx.RunSyncAsync($"outage run {i}");
                    await Task.Delay(1100);
                }
                var pending = (await ctx.Lab.DbSnapshotAsync());
                await using (var db = ctx.Lab.Db())
                {
                    ctx.Equal("nothing quarantined during the outage", 0, await db.Quarantine.CountAsync(q => q.ResolvedAt == null));
                    ctx.Equal("all 12 still queued", 12, await db.Requisitions.CountAsync(r => r.State == RecordState.Queued || r.State == RecordState.RetryPending || r.State == RecordState.InDoubt));
                }
                ctx.Evidence("state during outage", pending);
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
                ctx.Step("SAP recovers");
                await ctx.RunUntilSettledAsync("after recovery");
                await ExpectAll(ctx, 12, "REQ-P025");
            });

        Breaker.Register(c);
    }
}
