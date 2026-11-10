using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
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

internal static class StateAndConcurrency
{
    private static async Task Seed(ScenarioContext ctx, int n, string prefix)
    {
        for (var i = 1; i <= n; i++) await ctx.Lab.PutSourceAsync($"{prefix}-{i:D2}", Req.Valid($"{prefix}-{i:D2}"));
    }

    private static async Task AllOnce(ScenarioContext ctx, int n, string prefix)
    {
        for (var i = 1; i <= n; i++) ctx.Equal($"{prefix}-{i:D2} in SAP once", 1, await ctx.SapCountAsync($"{prefix}-{i:D2}"), "INV-01");
    }

    public static void Register(CatalogueBuilder c)
    {
        const string C = Categories.Concurrency;

        c.Add("CON-001", C, "Two workers start at the same time", Critical, DuplicateStress)
            .Expect("One runs, the other is skipped by the lease; each requisition once").Run(async ctx =>
            {
                await Seed(ctx, 8, "REQ-C001");
                var w2 = ctx.Lab.AddWorker("worker-2");
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("w1"), w2.RunAsync("w2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                await ctx.RunSyncAsync("settle");
                await AllOnce(ctx, 8, "REQ-C001");
            });

        c.AddApi("CON-002", C, "Two users submit the same request simultaneously (same Idempotency-Key)", Critical, DuplicateStress)
            .Expect("One draft; the other request replays it or is told it is in progress (409)").Run(async ctx =>
            {
                using var client = ctx.Lab.Assistant.ClientFor("E1003");
                var r = await Assist.ReferenceAsync(client);
                var draft = Assist.Draft(r);
                var token = await Assist.TokenAsync(client, draft);
                var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Assist.CreateAsync(client, draft, "same-key-C002", token)));
                var statuses = results.Select(x => (int)x.Response.StatusCode).ToList();
                ctx.Evidence("statuses", statuses);
                ctx.Check("only 201s and 409 in-progress", statuses.All(s => s is 201 or 409), "201/409", string.Join(",", statuses), "INV-08");
                var ids = results.Where(x => x.Response.StatusCode == HttpStatusCode.Created).Select(x => x.Body?["id"]?.GetValue<string>()).Distinct().ToList();
                ctx.Equal("distinct drafts created", 1, ids.Count, "INV-09");
                ctx.Equal("drafts in the source", 1, (await ctx.Lab.SourceSnapshotAsync()).Count);
            });

        c.Add("CON-003", C, "Overlapping scheduled runs", High, DuplicateStress)
            .Expect("While one run holds the lease, three more are skipped").Run(async ctx =>
            {
                await Seed(ctx, 30, "REQ-C003");
                var extra = Enumerable.Range(2, 3).Select(i => ctx.Lab.AddWorker($"scheduler-{i}")).ToList();
                var runs = await Task.WhenAll(new[] { ctx.Lab.Worker.RunAsync("s1") }.Concat(extra.Select(w => w.RunAsync(w.Name))));
                for (var i = 0; i < runs.Length; i++) ctx.RecordRun($"scheduler {i + 1}", runs[i]);
                ctx.Check("at least one overlapping run skipped", runs.Any(r => r.Kind == RunOutcomeKind.Skipped), ">= 1 skipped", string.Join(",", runs.Select(r => r.Kind)));
                await ctx.RunSyncAsync("settle");
                await AllOnce(ctx, 30, "REQ-C003");
            });

        c.Add("CON-004", C, "Manual retry overlaps the automatic retry", Critical, DuplicateStress)
            .Describe("Advanced scenario H.").Sync(("Resilience:BreakerMinimumThroughput", "100"))
            .Expect("The lease admits one; one logical creation").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C004");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = 5, StatusCode = 503 });
                await ctx.RunSyncAsync("fails: waits for retry");
                await ctx.Lab.ClearFaultsAsync();
                ctx.Step("Operator clicks replay while the scheduler retries");
                var replay = Task.Run(async () => { try { await ctx.Lab.Worker.ReplayAsync("REQ-C004-01"); } catch (Exception) { } });
                var w2 = ctx.Lab.AddWorker("scheduler");
                var auto = await w2.RunAsync("automatic retry");
                await replay;
                ctx.RecordRun("automatic retry", auto);
                await ctx.RunSyncAsync("settle");
                await ctx.ExpectCreatedOnceAsync("REQ-C004-01");
            });

        c.Add("CON-005", C, "Source changes during the SAP creation", High, DuplicateStress)
            .Expect("v1 created; v2 amends the same document on the next run").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C005");
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, async () =>
                {
                    var v2 = Req.Valid("REQ-C005-01").Set("lines[0].quantity", 9).Recalculate();
                    await ctx.Lab.EditSourceAsync("REQ-C005-01", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                });
                await ctx.RunSyncAsync("create races an edit");
                await ctx.RunSyncAsync("next run");
                await ctx.ExpectCreatedOnceAsync("REQ-C005-01");
                ctx.Equal("SAP holds the edit", 9m, (await ctx.Lab.SapDocumentsForAsync("REQ-C005-01"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });

        c.Add("CON-006", C, "Approval changes during synchronisation", Critical, Negative).Covered("APR-007 and APR-007B")
            .Sync(("ApprovalRecheck", "Always")).Expect("With re-check: withdrawn approval stops the create").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C006");
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapLookup, () => ctx.Lab.EditSourceAsync("REQ-C006-01", new { status = "Rejected", approvedAt = (string?)null }));
                await ctx.RunSyncAsync();
                await ctx.ExpectSkippedAsync("REQ-C006-01", "Rejected");
            });

        c.Add("CON-007", C, "Mapping saved concurrently by two workers (lease expired)", Critical, DuplicateStress)
            .Sync(("LeaseDuration", "00:00:00")).Expect("Optimistic concurrency lets one save win; never two documents").Run(async ctx =>
            {
                await Seed(ctx, 12, "REQ-C007");
                var w2 = ctx.Lab.AddWorker("worker-2", new() { ["LeaseDuration"] = "00:00:00" });
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("w1"), w2.RunAsync("w2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                await ctx.RunUntilSettledAsync("settle");
                await AllOnce(ctx, 12, "REQ-C007");
            });

        c.Add("CON-008", C, "Database deadlock / busy", High, Recovery)
            .Describe("SQLite reports SQLITE_BUSY ('database is locked') rather than deadlocks; SQL Server deadlocks are retried by EnableRetryOnFailure.")
            .Expect("The run fails cleanly at the busy write; the next run completes; no duplicates").Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-C008");
                ctx.Lab.DbFaults.Add("busy on the first send", DbFaults.SavesSending, DbFaultKind.DatabaseLocked);
                var failed = await ctx.RunSyncAsync("database busy");
                ctx.Check("run failed at the busy write", failed.Kind == RunOutcomeKind.Failed, "Failed", failed.Kind);
                await ctx.RunUntilSettledAsync("next run");
                await AllOnce(ctx, 3, "REQ-C008");
            });

        c.Add("CON-009", C, "Optimistic concurrency conflict after SAP success", Critical, Recovery)
            .Faults("Someone else updates the record row while SAP creates it").Expect("Save rejected (row changed); record re-checked in SAP next run and linked; one document")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C009");
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, async () =>
                {
                    await using var db = ctx.Lab.Db();
                    await db.Requisitions.Where(r => r.SourceId == "REQ-C009-01").ExecuteUpdateAsync(s => s.SetProperty(r => r.Revision, r => r.Revision + 100));
                });
                var first = await ctx.RunSyncAsync("conflicting update");
                ctx.Check("the save was refused", first.Kind == RunOutcomeKind.Failed, "Failed (DbUpdateConcurrencyException)", first.Kind + " " + first.Error);
                await ctx.RunUntilSettledAsync("recovery");
                await ctx.ExpectCreatedOnceAsync("REQ-C009-01");
            });

        c.Add("CON-010", C, "Stale ETag when amending", High, FailureSimulation)
            .Expect("SAP answers 412, the integration re-reads and amends; one document").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C010");
                await ctx.RunSyncAsync();
                var number = (await ctx.Lab.RecordAsync("REQ-C010-01"))!.SapNumber!;
                await ctx.Lab.TouchSapAsync(number);
                ctx.Step("A buyer edits the document in SAP (its ETag changes)");
                var v2 = Req.Valid("REQ-C010-01").Set("lines[0].quantity", 4).Recalculate();
                await ctx.Lab.EditSourceAsync("REQ-C010-01", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                var run = await ctx.RunSyncAsync();
                ctx.Equal("amended", 1, run.Count(RunEventKind.Amended));
                await ctx.ExpectCreatedOnceAsync("REQ-C010-01");
            });

        c.Add("CON-011", C, "Same Idempotency-Key sent in parallel to SAP", Critical, DuplicateStress).Inv("INV-12")
            .Expect("One document; the others replay it or get 409 in progress").Run(async ctx =>
            {
                var body = new { ExternalReference = "REQ-C011", ExternalVersion = 1, to_PurchaseReqnItem = new[] { new { PurchaseRequisitionItem = "00010", RequestedQuantity = 1, BaseUnit = "EA", PurchaseRequisitionPrice = 10, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", PurchasingGroup = "001", DeliveryDate = Req.Today(30) } } };
                var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, LabEnvironment.SapBase + "A_PurchaseRequisitionHeader") { Content = JsonContent.Create(body) };
                    req.Headers.Add("Idempotency-Key", "parallel-C011");
                    return (int)(await ctx.Lab.SapAdmin.SendAsync(req)).StatusCode;
                }));
                ctx.Evidence("statuses", responses);
                ctx.Equal("documents", 1, await ctx.SapCountAsync("REQ-C011"));
                ctx.Check("only 201 or 409", responses.All(s => s is 201 or 409), "201/409", string.Join(",", responses));
            });

        c.Add("CON-012", C, "Worker shutdown during a database transaction", High, Recovery).Covered("SRC-020")
            .Expect("The transaction rolls back as a whole; restart resumes cleanly").Run(async ctx =>
            {
                await Seed(ctx, 4, "REQ-C012");
                ctx.Lab.DbFaults.Add("crash in the page transaction", DbFaults.SavesCheckpoint, DbFaultKind.Crash);
                await ctx.RunSyncAsync("crash");
                await using (var db = ctx.Lab.Db())
                    ctx.Equal("nothing half-written (page rolled back)", 0, await db.Requisitions.CountAsync());
                await ctx.RestartAsync();
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 4, "REQ-C012");
            });

        c.Add("CON-013", C, "Worker shutdown during the SAP request", Critical, Recovery)
            .Faults("The process dies after sending the create, before any answer").Expect("Restart marks it in doubt, checks SAP, creates or links once")
            .Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C013");
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, () => { ctx.Lab.Crash.Kill("during the SAP create"); return Task.CompletedTask; });
                var crashed = await ctx.RunSyncAsync("dies mid-request");
                ctx.Equal("run outcome", RunOutcomeKind.Crashed, crashed.Kind);
                await ctx.RestartAsync();
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-C013-01");
                ctx.Check("resolved through the in-doubt path", (await ctx.Lab.EventsAsync("REQ-C013-01")).Any(e => e.Kind == RunEventKind.InDoubt), "InDoubt event", "");
            });

        c.Add("CON-014", C, "SAP status changes during the source write-back", High, FailureSimulation)
            .Expect("Statuses only move forward; the newer status is written back on the next run").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-C014");
                await ctx.RunSyncAsync();
                var number = (await ctx.Lab.RecordAsync("REQ-C014-01"))!.SapNumber!;
                await ctx.Lab.SetSapLifecycleAsync(number, "InRelease");
                ctx.Lab.Hooks.Before("source", LabHooks.IsWriteback, () => ctx.Lab.SetSapLifecycleAsync(number, "Released"));
                await ctx.RunSyncAsync("write-back races a release");
                await ctx.RunSyncAsync("next run");
                ctx.Equal("record status", "Released", (await ctx.Lab.RecordAsync("REQ-C014-01"))?.ErpStatus.ToString());
                ctx.Equal("source shows Released", "Released", (await ctx.Lab.SourceRecordAsync("REQ-C014-01"))?["erpSync"]?["status"]?.GetValue<string>());
            });

        c.Add("CON-015", C, "Reconciliation while a sync is running", Medium, Functional)
            .Expect("Both complete; the report is consistent (no exception, no false duplicate)").Run(async ctx =>
            {
                await Seed(ctx, 40, "REQ-C015");
                var run = ctx.Lab.RunAsync("sync");
                var report = await ctx.Lab.ReconcileAsync();
                ctx.RecordRun("sync", await run);
                ctx.Check("report built during the run", report.Invariants.Count > 0, "invariants", report.Invariants.Count);
                ctx.Check("no false duplicate", report.Invariants.First(i => i.Name.StartsWith("No duplicate", StringComparison.Ordinal)).Outcome != Procurement.Sync.Reporting.InvariantOutcome.Fail, "not Fail", "");
            });

        c.Add("CON-016", C, "Two operators repair the same quarantined item", High, DuplicateStress)
            .Expect("Both edits land as versions; overlapping replays are serialised by the lease; one document").Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-C016", Req.Valid("REQ-C016").Set("totalAmount", 0));
                await ctx.RunSyncAsync();
                await ctx.ExpectQuarantinedAsync("REQ-C016", QuarantineReason.AmountNotPositive);
                await Task.WhenAll(ctx.Lab.EditSourceAsync("REQ-C016", new { totalAmount = 2400 }), ctx.Lab.EditSourceAsync("REQ-C016", new { totalAmount = 2400 }));
                await Task.WhenAll(Task.Run(async () => { try { await ctx.Lab.Worker.ReplayAsync("REQ-C016"); } catch (InvalidOperationException) { } }),
                    Task.Run(async () => { try { await ctx.Lab.Worker.ReplayAsync("REQ-C016"); } catch (InvalidOperationException) { } }));
                await ctx.RunSyncAsync("settle");
                await ctx.ExpectCreatedOnceAsync("REQ-C016");
            });

        c.Add("CON-017", C, "Pause mid-sync, then resume", High, Recovery)
            .Expect("While paused nothing new reaches SAP; after resuming the same run finishes; every requisition once")
            .Run(async ctx =>
            {
                await Seed(ctx, 12, "REQ-C017");
                await ctx.Lab.SapFaultsAsync(new FaultSettings { LatencyMs = 150 });
                var control = ctx.Lab.Worker.Services.GetRequiredService<Procurement.Sync.Pipeline.SyncControl>();
                var run = ctx.Lab.RunAsync("paused part-way");
                await Task.Delay(700);
                control.Pause();
                ctx.Step("Pause pressed");
                await Task.Delay(600);
                var atPause = (await ctx.Lab.SapStatsAsync()).Total;
                await Task.Delay(1500);
                var stillPaused = (await ctx.Lab.SapStatsAsync()).Total;
                ctx.Equal("no new SAP documents while paused", atPause, stillPaused);
                ctx.Check("the run was part-way when paused", atPause < 12, "< 12", atPause);
                control.Resume();
                ctx.Step("Resume pressed");
                ctx.RecordRun("paused part-way", await run);
                await AllOnce(ctx, 12, "REQ-C017");
            });
        const string D = Categories.Database;

        c.Add("DB-001", D, "Integration database unavailable", Critical, FailureSimulation)
            .Expect("The run fails before touching SAP; nothing sent; recovers when the database is back").Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-DB001");
                var broken = ctx.Lab.AddWorker("no-database", new() { ["ConnectionStrings:SyncStore"] = "Data Source=/nonexistent-dir/x/lab.db;Mode=ReadWrite" });
                var failed = await broken.RunAsync("database unavailable");
                ctx.RecordRun("database unavailable", failed);
                ctx.Equal("run outcome", RunOutcomeKind.Failed, failed.Kind);
                ctx.Equal("SAP requests sent", 0, ctx.Lab.Capture.Exchanges.Count(e => e.Channel.StartsWith("no-database") && e.Channel.EndsWith("→sap")));
                await ctx.RunSyncAsync("database back");
                await AllOnce(ctx, 2, "REQ-DB001");
            });

        c.Add("DB-002", D, "Connection timeout (database held by another process)", High, FailureSimulation)
            .Expect("Times out after the busy timeout, fails cleanly; succeeds once released").Timeout(60).Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-DB002");
                await ctx.RunSyncAsync("create the schema");
                await ctx.Lab.PutSourceAsync("REQ-DB002-02", Req.Valid("REQ-DB002-02"));
                SqliteConnection.ClearAllPools();
                await using (await DatabaseLock.AcquireAsync(ctx.Lab.DbPath))
                {
                    ctx.Step("Another process holds an exclusive lock on the database file");
                    var impatient = ctx.Lab.AddWorker("short-timeout", new() { ["ConnectionStrings:SyncStore"] = $"Data Source={ctx.Lab.DbPath};Default Timeout=2" });
                    var blocked = await impatient.RunAsync("database locked");
                    ctx.RecordRun("database locked (2 s busy timeout)", blocked);
                    ctx.Check("run did not succeed while locked", blocked.Kind is RunOutcomeKind.Failed, "Failed", blocked.Kind + " " + blocked.Error);
                }
                SqliteConnection.ClearAllPools();
                await ctx.RunUntilSettledAsync("lock released");
                await AllOnce(ctx, 1, "REQ-DB002");
                ctx.Equal("REQ-DB002-02 in SAP once", 1, await ctx.SapCountAsync("REQ-DB002-02"));
            });

        c.Add("DB-003", D, "Database locked on one write", High, Recovery).Covered("CON-008")
            .Expect("Fails at that write; next run completes; one document each").Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-DB003");
                ctx.Lab.DbFaults.Add("locked on mapping save", DbFaults.SavesMapping, DbFaultKind.DatabaseLocked);
                await ctx.RunSyncAsync("locked");
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 2, "REQ-DB003");
            });

        c.Add("DB-004", D, "Transaction rolled back", High, Recovery).Covered("SRC-013")
            .Expect("Staging and checkpoint roll back together; re-read next run").Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-DB004");
                ctx.Lab.DbFaults.Add("page rollback", DbFaults.SavesCheckpoint, DbFaultKind.SaveFails);
                await ctx.RunSyncAsync("rolled back");
                await using (var db = ctx.Lab.Db()) ctx.Equal("no partial staging", 0, await db.Outbox.CountAsync());
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 3, "REQ-DB004");
            });

        c.Add("DB-005", D, "Unique constraint violation (two workers insert the same new record)", High, Recovery)
            .Sync(("LeaseDuration", "00:00:00")).Expect("One insert wins, the other run fails cleanly; next run completes").Run(async ctx =>
            {
                await Seed(ctx, 20, "REQ-DB005");
                var w2 = ctx.Lab.AddWorker("worker-2", new() { ["LeaseDuration"] = "00:00:00" });
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("w1"), w2.RunAsync("w2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                await ctx.RunUntilSettledAsync("settle");
                await AllOnce(ctx, 20, "REQ-DB005");
            });

        c.Add("DB-006", D, "Mapping insert fails after SAP success", Critical, Recovery).Covered("DUP-014")
            .Expect("Recovered from SAP identity; one document").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-DB006");
                ctx.Lab.DbFaults.Add("mapping save", DbFaults.SavesMapping, DbFaultKind.SaveFails);
                await ctx.RunSyncAsync("mapping fails");
                await ctx.RunSyncAsync("recovery");
                await ctx.ExpectCreatedOnceAsync("REQ-DB006-01");
            });

        c.Add("DB-007", D, "Checkpoint save fails", High, Recovery).Covered("SRC-013")
            .Expect("No data loss; page re-read").Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-DB007");
                ctx.Lab.DbFaults.Add("checkpoint", DbFaults.SavesCheckpoint, DbFaultKind.SaveFails);
                await ctx.RunSyncAsync("fails");
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 2, "REQ-DB007");
            });

        c.Add("DB-008", D, "Disk full", High, Recovery)
            .Expect("A write fails with SQLITE_FULL; the run stops cleanly; nothing half-sent; next run completes").Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-DB008");
                ctx.Lab.DbFaults.Add("disk full on the first send", DbFaults.SavesSending, DbFaultKind.SaveFails);
                ctx.Simulated("The disk fills up (a write is refused with 'database or disk is full')");
                await ctx.RunSyncAsync("disk full");
                await ctx.RunUntilSettledAsync("space freed");
                await AllOnce(ctx, 2, "REQ-DB008");
            });

        c.Add("DB-009", D, "Corrupted stored JSON (poison message)", Critical, Recovery)
            .Expect("The unreadable outbox payload is quarantined (SchemaInvalid) without blocking others; replay re-reads the source and creates it").Run(async ctx =>
            {
                await Seed(ctx, 3, "REQ-DB009");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "GET", PathContains = "ExternalReference", Count = 30, StatusCode = 503 });
                await ctx.RunSyncAsync("staged, SAP lookups failing");
                await using (var db = ctx.Lab.Db())
                    await db.Outbox.Where(m => m.SourceId == "REQ-DB009-02").ExecuteUpdateAsync(s => s.SetProperty(m => m.Payload, "{ this is not json"));
                ctx.Step("The stored payload of REQ-DB009-02 is corrupted");
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunUntilSettledAsync("SAP back");
                ctx.Equal("REQ-DB009-01 created", 1, await ctx.SapCountAsync("REQ-DB009-01"));
                ctx.Equal("REQ-DB009-03 created", 1, await ctx.SapCountAsync("REQ-DB009-03"));
                await ctx.ExpectQuarantinedAsync("REQ-DB009-02", QuarantineReason.SchemaInvalid);
                await ctx.Lab.Worker.ReplayAsync("REQ-DB009-02");
                ctx.Step("Operator replays it: the integration re-reads the source");
                await ctx.ExpectCreatedOnceAsync("REQ-DB009-02");
            });

        c.Add("DB-010", D, "Duplicate mapping entries (two records, one SAP number)", Critical, Recovery).Covered("DUP-025")
            .Expect("Detected by reconciliation; status polling keeps working; the wrong link is removed when the record is next processed").Run(async ctx =>
            {
                await Seed(ctx, 2, "REQ-DB010");
                await ctx.RunSyncAsync();
                var a = (await ctx.Lab.RecordAsync("REQ-DB010-01"))!.SapNumber!;
                await using (var db = ctx.Lab.Db())
                    await db.Requisitions.Where(r => r.SourceId == "REQ-DB010-02").ExecuteUpdateAsync(s => s.SetProperty(r => r.SapNumber, a));
                var report = await ctx.Lab.ReconcileAsync();
                ctx.Equal("reconciliation flags it", "Fail", report.Invariants.First(i => i.Name.StartsWith("Every synced", StringComparison.Ordinal)).Outcome.ToString(), "INV-10");
                await ctx.Lab.SetSapLifecycleAsync(a, "InRelease");
                var poll = await ctx.RunSyncAsync("status poll with a duplicated mapping");
                ctx.Check("status polling did not crash", poll.Kind != RunOutcomeKind.Failed, "not Failed", poll.Kind + " " + poll.Error);
                await ctx.Lab.EditSourceAsync("REQ-DB010-02", new { justification = "changed" });
                await ctx.RunSyncAsync("B processed again");
                ctx.Equal("wrong link removed", null, (await ctx.Lab.RecordAsync("REQ-DB010-02"))?.SapNumber);
            });

        c.Add("DB-011", D, "Database restarts mid-run", High, Recovery)
            .Expect("The interrupted run fails; the next run recovers in-flight records via SAP; one document each").Run(async ctx =>
            {
                await Seed(ctx, 5, "REQ-DB011");
                var saves = 0;
                ctx.Lab.DbFaults.Add("connection lost mid-dispatch", db => DbFaults.SavesMapping(db) && ++saves == 3, DbFaultKind.SaveFails);
                await ctx.RunSyncAsync("database drops");
                SqliteConnection.ClearAllPools();
                ctx.Simulated("The database server restarts");
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 5, "REQ-DB011");
            });

        c.Add("DB-012", D, "Stale record version (optimistic concurrency)", High, Recovery).Covered("CON-009")
            .Expect("Stale write refused; recovered next run").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-DB012");
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, async () =>
                {
                    await using var db = ctx.Lab.Db();
                    await db.Requisitions.Where(r => r.SourceId == "REQ-DB012-01").ExecuteUpdateAsync(s => s.SetProperty(r => r.Revision, r => r.Revision + 1));
                });
                await ctx.RunSyncAsync("stale");
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-DB012-01");
            });

        c.Add("DB-013", D, "Partial batch transaction failure", High, Recovery)
            .Sync(("PageSize", "4")).Expect("A failed page leaves no partial staging; earlier pages are kept; all created once later").Run(async ctx =>
            {
                await Seed(ctx, 10, "REQ-DB013");
                var saves = 0;
                ctx.Lab.DbFaults.Add("second page fails", db => DbFaults.SavesCheckpoint(db) && ++saves == 2, DbFaultKind.SaveFails);
                await ctx.RunSyncAsync("page 2 fails");
                await using (var db = ctx.Lab.Db()) ctx.Equal("only page 1 staged", 4, await db.Requisitions.CountAsync());
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 10, "REQ-DB013");
            });

        c.Add("DB-014", D, "Recovery after a process restart", Critical, Recovery).Covered("DUP-013, SRC-020, CON-013")
            .Expect("Restart continues from durable state").Run(async ctx =>
            {
                await Seed(ctx, 6, "REQ-DB014");
                ctx.Lab.Crash.ArmAfterSapResponse("after the 3rd create", (rq, rs) => LabHooks.IsSapCreate(rq) && ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader")) >= 3);
                await ctx.RunSyncAsync("crash");
                await ctx.RestartAsync();
                await ctx.RunUntilSettledAsync();
                await AllOnce(ctx, 6, "REQ-DB014");
            });

        c.Add("DB-015", D, "SQLite and SQL Server behave the same", Medium, Recovery)
            .Run(async ctx =>
            {
                var sql = Environment.GetEnvironmentVariable("PROCUREMENT_LAB_SQLSERVER");
                if (string.IsNullOrWhiteSpace(sql)) ctx.Block("No SQL Server is available to the lab. Set PROCUREMENT_LAB_SQLSERVER to a connection string to run this scenario on SQL Server (the model is provider-neutral; Store:Provider=SqlServer).");
                var w = ctx.Lab.AddWorker("sqlserver", new() { ["Store:Provider"] = "SqlServer", ["ConnectionStrings:SyncStore"] = sql });
                await ctx.Lab.PutSourceAsync("REQ-DB015", Req.Valid("REQ-DB015"));
                var run = await w.RunAsync("on SQL Server");
                ctx.RecordRun("on SQL Server", run);
                ctx.Equal("created on SQL Server", 1, await ctx.SapCountAsync("REQ-DB015"));
            })
            .Inv("INV-12");

        c.Add("DB-016", D, "Database connection lost right after SAP success", Critical, Recovery).Covered("DUP-014")
            .Expect("No untraceable SAP creation: next run links it").Run(async ctx =>
            {
                await Seed(ctx, 1, "REQ-DB016");
                ctx.Lab.DbFaults.Add("connection lost", DbFaults.SavesMapping, DbFaultKind.DatabaseLocked);
                await ctx.RunSyncAsync("connection lost");
                await ctx.RunSyncAsync("reconnected");
                await ctx.ExpectCreatedOnceAsync("REQ-DB016-01");
            });
        const string St = Categories.Status;

        async Task<string> Synced(ScenarioContext ctx, string id)
        {
            await ctx.Lab.PutSourceAsync(id, Req.Valid(id));
            await ctx.RunSyncAsync("create");
            return (await ctx.Lab.RecordAsync(id))!.SapNumber!;
        }

        c.Add("STS-001", St, "SAP status moves forward", Critical, Functional)
            .Expect("Released captured and written back to the source").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST001");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.RunSyncAsync("status poll");
                ctx.Equal("record status", "Released", (await ctx.Lab.RecordAsync("REQ-ST001"))?.ErpStatus.ToString());
                ctx.Equal("source status", "Released", (await ctx.Lab.SourceRecordAsync("REQ-ST001"))?["erpSync"]?["status"]?.GetValue<string>());
            });

        c.Add("STS-002", St, "SAP status unchanged", Medium, Functional)
            .Expect("No status change event, no extra write-back").Run(async ctx =>
            {
                await Synced(ctx, "REQ-ST002");
                var run = await ctx.RunSyncAsync("poll with no change");
                ctx.Equal("status changes", 0, run.Count(RunEventKind.StatusChanged));
                ctx.Equal("write-backs", 0, run.Count(RunEventKind.WrittenBack));
            });

        c.Add("STS-003", St, "SAP sends an older status", Critical, Negative)
            .Expect("Ignored and logged; the source keeps Released").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST003");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.RunSyncAsync("released");
                await ctx.Lab.SetSapLifecycleAsync(n, "InRelease");
                var run = await ctx.RunSyncAsync("older status arrives");
                ctx.Check("ignored", run.Count(RunEventKind.StatusIgnored) >= 1, ">= 1 StatusIgnored", run.Count(RunEventKind.StatusIgnored));
                ctx.Equal("still Released", "Released", (await ctx.Lab.RecordAsync("REQ-ST003"))?.ErpStatus.ToString());
            });

        c.Add("STS-004", St, "Unknown SAP status codes", High, Negative).Covered("SAP-023")
            .Expect("Reported, never guessed").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST004");
                await ctx.Lab.SetSapRawStatusAsync(n, "X", "77");
                await ctx.RunSyncAsync("unknown codes");
                ctx.Check("reported", (await ctx.Lab.EventsAsync("REQ-ST004")).Any(e => e.Kind == RunEventKind.StatusIgnored && e.Detail!.Contains("unrecognised")), "StatusIgnored", "");
            });

        c.Add("STS-005", St, "SAP status response missing (feed fails)", High, FailureSimulation)
            .Expect("Polling stops early with an issue; the watermark is kept; next run catches up").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST005");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "GET", PathContains = "$top=100", Count = 10, StatusCode = 503 });
                var run = await ctx.RunSyncAsync("feed fails");
                ctx.Check("issue reported", run.Result?.Message?.Contains("status polling stopped") == true, "status polling stopped early", run.Result?.Message);
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync("feed back");
                ctx.Equal("caught up", "Released", (await ctx.Lab.RecordAsync("REQ-ST005"))?.ErpStatus.ToString());
            });

        c.Add("STS-006", St, "Status refresh times out", Medium, FailureSimulation)
            .Expect("Timeout handled like an outage; next run catches up").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST006");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Delay, Method = "GET", PathContains = "$top=100", Count = 6, DelayMs = 2500 });
                await ctx.RunSyncAsync("feed slow");
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync("feed normal");
                ctx.Equal("caught up", "Released", (await ctx.Lab.RecordAsync("REQ-ST006"))?.ErpStatus.ToString());
            });

        c.Add("STS-007", St, "Source status update fails", High, Recovery).Covered("SRC-019")
            .Expect("Write-back pending, retried").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST007");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "PUT", PathContains = "erp-status", Count = 10, StatusCode = 500 });
                await ctx.RunSyncAsync("write-back fails");
                ctx.Equal("pending", true, (await ctx.Lab.RecordAsync("REQ-ST007"))?.WritebackPending);
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync();
                ctx.Equal("source updated", "Released", (await ctx.Lab.SourceRecordAsync("REQ-ST007"))?["erpSync"]?["status"]?.GetValue<string>());
            });

        c.Add("STS-008", St, "Source and SAP status differ: reported", High, Functional)
            .Expect("Reconciliation warns 'write-back pending' while they differ").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST008");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Error, Method = "PUT", PathContains = "erp-status", Count = 10, StatusCode = 500 });
                await ctx.RunSyncAsync();
                var report = await ctx.Lab.ReconcileAsync();
                var inv = report.Invariants.First(i => i.Name.StartsWith("Source shows", StringComparison.Ordinal));
                ctx.Equal("difference reported", "Warn", inv.Outcome.ToString(), "INV-10");
                await ctx.Lab.ClearFaultsAsync();
                await Task.Delay(1200);
                await ctx.RunSyncAsync("settle");
            });

        c.Add("STS-009", St, "Status already final", Medium, Functional)
            .Expect("Closed is terminal: later SAP changes do not move it").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST009");
                await ctx.Lab.SetSapLifecycleAsync(n, "Closed");
                await ctx.RunSyncAsync();
                await ctx.Lab.TouchSapAsync(n);
                await ctx.RunSyncAsync();
                ctx.Equal("still Closed", "Closed", (await ctx.Lab.RecordAsync("REQ-ST009"))?.ErpStatus.ToString());
            });

        c.Add("STS-010", St, "Final status appears to regress", High, Negative)
            .Expect("A move from Closed back to Released is ignored; the source keeps Closed").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST010");
                await ctx.Lab.SetSapLifecycleAsync(n, "Closed");
                await ctx.RunSyncAsync();
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.RunSyncAsync();
                ctx.Equal("still Closed", "Closed", (await ctx.Lab.RecordAsync("REQ-ST010"))?.ErpStatus.ToString());
            });

        c.Add("STS-011", St, "SAP document cancelled (rejected)", High, Functional)
            .Expect("A legitimate business reversal is reported explicitly: status Rejected written back").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST011");
                await ctx.Lab.SetSapLifecycleAsync(n, "Rejected");
                await ctx.RunSyncAsync();
                ctx.Equal("record status", "Rejected", (await ctx.Lab.RecordAsync("REQ-ST011"))?.ErpStatus.ToString());
                ctx.Equal("source status", "Rejected", (await ctx.Lab.SourceRecordAsync("REQ-ST011"))?["erpSync"]?["status"]?.GetValue<string>());
            });

        c.Add("STS-012", St, "SAP document deleted", High, Negative)
            .Describe("The status feed has no deletions. Reconciliation is what notices: 'Every synced requisition exists in SAP' fails, naming it. The global no-phantom invariant is expected to flag the same record, so it is checked explicitly here instead.")
            .Without("INV-03", "INV-07")
            .Expect("Reported by reconciliation (Fail naming the requisition), not silently ignored").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST012");
                await ctx.Lab.DeleteSapAsync(n);
                await ctx.RunSyncAsync();
                var report = await ctx.Lab.ReconcileAsync();
                var inv = report.Invariants.First(i => i.Name.StartsWith("Every synced", StringComparison.Ordinal));
                ctx.Equal("reconciliation fails", "Fail", inv.Outcome.ToString(), "INV-10");
                ctx.Check("names the requisition", inv.Detail.Contains("REQ-ST012"), "REQ-ST012", inv.Detail);
            });

        c.Add("STS-013", St, "Several status changes within one poll (out of order)", Medium, Functional)
            .Expect("Ends at the latest forward status; history never goes backwards").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST013");
                await ctx.Lab.SetSapLifecycleAsync(n, "InRelease");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.AdvanceSapAsync(rejectEvery: 0);
                await ctx.RunSyncAsync();
                var status = (await ctx.Lab.RecordAsync("REQ-ST013"))?.ErpStatus;
                ctx.Check("at or beyond Released", status is ErpStatus.Released or ErpStatus.PoCreated or ErpStatus.Closed, ">= Released", status);
            });

        c.Add("STS-014", St, "Duplicate status notifications (overlapping polls)", Medium, Functional)
            .Expect("Re-reading the same change adds no history and no write-back").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST014");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.RunSyncAsync();
                await ctx.RunSyncAsync();
                await ctx.RunSyncAsync();
                await using var db = ctx.Lab.Db();
                ctx.Equal("history rows to Released", 1, await db.StatusHistory.CountAsync(h => h.SourceId == "REQ-ST014" && h.ToStatus == ErpStatus.Released));
            });

        c.Add("STS-015", St, "Status write-back retried after 412", High, Recovery)
            .Expect("Re-reads the source's ETag and writes again; succeeds").Run(async ctx =>
            {
                var n = await Synced(ctx, "REQ-ST015");
                await ctx.Lab.SetSapLifecycleAsync(n, "Released");
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Respond, Method = "PUT", PathContains = "erp-status", Count = 3, StatusCode = 412, Body = "{\"title\":\"Precondition failed\"}" });
                await ctx.RunUntilSettledAsync();
                await ctx.RunSyncAsync();
                ctx.Equal("source updated", "Released", (await ctx.Lab.SourceRecordAsync("REQ-ST015"))?["erpSync"]?["status"]?.GetValue<string>());
            });
    }
}
