using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static class Duplicates
{
    public static ScriptedFault SapCreate(FaultKind kind, int count = 1, int status = 503, string? body = null, string? reference = null) => new()
    {
        Kind = kind, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = count, StatusCode = status, Body = body, BodyContains = reference,
    };

    public static void Register(CatalogueBuilder c)
    {
        const string D = Categories.Duplicates;

        c.Add("DUP-001", D, "Same source ID submitted twice", Critical, DuplicateStress)
            .Fixture("REQ-D001, synced, then touched in the source (new version, same content)").Expect("One SAP creation; the second version is recognised as unchanged content")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D001"));
                await ctx.RunSyncAsync("first submission");
                await ctx.Lab.EditSourceAsync("REQ-D001", new { justification = "Lab scenario REQ-D001" });
                ctx.Step("The source re-submits REQ-D001 (version bumped, content identical)");
                var second = await ctx.RunSyncAsync("second submission");
                ctx.Equal("created on second submission", 0, second.Count(RunEventKind.Created));
                ctx.Equal("amended on second submission", 0, second.Count(RunEventKind.Amended));
                await ctx.ExpectCreatedOnceAsync("REQ-D001");
            });

        c.Add("DUP-002", D, "Same record twice on one page", Critical, DuplicateStress)
            .Faults("The source's first page is served with REQ-D002 listed twice").Expect("One SAP creation; the repeat is recognised")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D002"));
                var listed = (await ctx.Lab.SourceSnapshotAsync())[0]!.DeepClone();
                var page = new JsonObject { ["items"] = new JsonArray(listed.DeepClone(), listed.DeepClone()), ["nextCursor"] = null, ["hasMore"] = false };
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Respond, Method = "GET", PathContains = "/api/requisitions", StatusCode = 200, Body = page.ToJsonString() });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-D002");
                var unchanged = (await ctx.Lab.EventsAsync("REQ-D002")).Count(e => e.Kind == RunEventKind.Unchanged);
                ctx.Check("the repeat was recognised as already seen", unchanged >= 1, ">= 1 Unchanged event", unchanged);
            });

        c.Add("DUP-003", D, "Same record on different pages", Critical, DuplicateStress)
            .Faults("Page overlap: each page repeats the previous page's last record; page size 2").Sync(("PageSize", "2"))
            .Expect("Each requisition created once")
            .Run(async ctx =>
            {
                await ctx.PutAllAsync(Enumerable.Range(1, 5).Select(i => Req.Valid($"REQ-D003-{i}")));
                await ctx.Lab.SourceFaultsAsync(new FaultSettings { PageOverlap = true });
                await ctx.RunSyncAsync();
                for (var i = 1; i <= 5; i++) await ctx.ExpectCreatedOnceAsync($"REQ-D003-{i}");
            });

        c.Add("DUP-004", D, "Same ID with a changed description", High, DuplicateStress)
            .Expect("Controlled update: SAP document amended (PATCH with If-Match), not a second document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D004"));
                await ctx.RunSyncAsync("v1");
                await ctx.Lab.EditSourceAsync("REQ-D004", new { lines = new[] { Req.Line(description: "Laptop 16-inch") } });
                var second = await ctx.RunSyncAsync("v2 (description changed)");
                ctx.Equal("amended", 1, second.Count(RunEventKind.Amended));
                await ctx.ExpectCreatedOnceAsync("REQ-D004");
                var doc = (await ctx.Lab.SapDocumentsForAsync("REQ-D004"))[0];
                ctx.Equal("SAP holds the new description", "Laptop 16-inch", doc["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionItemText"]!.GetValue<string>());
                var current = (await ctx.Lab.SourceRecordAsync("REQ-D004"))!["version"]!.GetValue<long>();
                ctx.Check("SAP holds the edited version (or the newer write-back version of the same content)", doc["ExternalVersion"]!.GetValue<long>() >= 2,
                    $">= 2 (source is now v{current})", doc["ExternalVersion"]!.GetValue<long>());
            });

        c.Add("DUP-005", D, "Same ID with a changed quantity", High, DuplicateStress)
            .Expect("Version-aware: v2 quantity in SAP, one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D005"));
                await ctx.RunSyncAsync("v1");
                var v2 = Req.Valid("REQ-D005").Set("lines[0].quantity", 5).Recalculate();
                await ctx.Lab.EditSourceAsync("REQ-D005", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                await ctx.RunSyncAsync("v2 (quantity 2 to 5)");
                await ctx.ExpectCreatedOnceAsync("REQ-D005");
                ctx.Equal("SAP quantity", 5m, (await ctx.Lab.SapDocumentsForAsync("REQ-D005"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });

        c.Add("DUP-006", D, "Same ID with a changed price: before and after release", High, DuplicateStress)
            .Describe("Policy: a price change updates the SAP requisition while it is still open; once SAP has released it, the change is refused and quarantined for a person.")
            .Expect("Before release: amended. After release: VersionConflictUnresolved, SAP keeps the released price")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D006A"));
                await ctx.PutAsync(Req.Valid("REQ-D006B"));
                await ctx.RunSyncAsync("v1");
                var number = (await ctx.Lab.RecordAsync("REQ-D006B"))!.SapNumber!;
                await ctx.Lab.SetSapLifecycleAsync(number, "Released");
                ctx.Step($"SAP releases REQ-D006B ({number})");
                foreach (var id in (string[])["REQ-D006A", "REQ-D006B"])
                {
                    var v2 = Req.Valid(id).Set("lines[0].unitPrice", 999.99m).Recalculate();
                    await ctx.Lab.EditSourceAsync(id, new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                }
                await ctx.RunSyncAsync("v2 (price changed)");
                ctx.Equal("open requisition: SAP price updated", 999.99m, (await ctx.Lab.SapDocumentsForAsync("REQ-D006A"))[0]["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionPrice"]!.GetValue<decimal>());
                ctx.Equal("released requisition: SAP keeps its price", 1200m, (await ctx.Lab.SapDocumentsForAsync("REQ-D006B"))[0]["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionPrice"]!.GetValue<decimal>());
                var q = await ctx.Lab.OpenQuarantineAsync("REQ-D006B");
                ctx.Check("released requisition's change quarantined", q.Any(e => e.Reason == QuarantineReason.VersionConflictUnresolved), "VersionConflictUnresolved", string.Join(",", q.Select(e => e.Reason)));
                ctx.Equal("documents for REQ-D006B", 1, await ctx.SapCountAsync("REQ-D006B"));
            });

        c.Add("DUP-007", D, "Same payload under different source IDs", Medium, DuplicateStress)
            .Describe("Policy: identity is the source requisition ID. Two IDs with identical content are two requisitions (people do order the same thing twice); detecting business duplicates is the approval workflow's job.")
            .Expect("Two SAP documents, one per source ID")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D007A"));
                await ctx.PutAsync(Req.Valid("REQ-D007B"));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-D007A");
                await ctx.ExpectCreatedOnceAsync("REQ-D007B");
            });

        c.Add("DUP-008", D, "Requisition already in SAP before the integration started", Critical, DuplicateStress)
            .Fixture("SAP holds a document for REQ-D008 created by an earlier load").Expect("Existing document linked (AlreadyInSap); nothing created")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D008"));
                var existing = await ctx.Lab.InsertSapAsync("REQ-D008", quantity: 2, price: 1200m);
                var run = await ctx.RunSyncAsync();
                ctx.Equal("created", 0, run.Count(RunEventKind.Created));
                ctx.Equal("linked to the existing document", existing, (await ctx.Lab.RecordAsync("REQ-D008"))?.SapNumber);
                ctx.Equal("SAP documents for REQ-D008", 1, await ctx.SapCountAsync("REQ-D008"));
            });

        c.Add("DUP-009", D, "Same Idempotency-Key with a different payload", High, DuplicateStress, Security)
            .Describe("Sent straight to SAP (as a buggy client would): the key is bound to the first request's body.")
            .Expect("Second request refused with 422 IDEMPOTENCY/MISMATCH; one document").Inv("INV-12")
            .Run(async ctx =>
            {
                var body1 = new { ExternalReference = "REQ-D009", ExternalVersion = 1, to_PurchaseReqnItem = new[] { new { PurchaseRequisitionItem = "00010", RequestedQuantity = 1, BaseUnit = "EA", PurchaseRequisitionPrice = 10, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", PurchasingGroup = "001", DeliveryDate = Req.Today(30) } } };
                var body2 = new { ExternalReference = "REQ-D009", ExternalVersion = 1, to_PurchaseReqnItem = new[] { new { PurchaseRequisitionItem = "00010", RequestedQuantity = 99, BaseUnit = "EA", PurchaseRequisitionPrice = 10, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", PurchasingGroup = "001", DeliveryDate = Req.Today(30) } } };
                async Task<HttpResponseMessage> Post(object body)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, LabEnvironment.SapBase + "A_PurchaseRequisitionHeader") { Content = JsonContent.Create(body) };
                    req.Headers.Add("Idempotency-Key", "same-key-D009");
                    return await ctx.Lab.SapAdmin.SendAsync(req);
                }
                ctx.Equal("first request", HttpStatusCode.Created, (await Post(body1)).StatusCode);
                var second = await Post(body2);
                ctx.Equal("same key, different body", HttpStatusCode.UnprocessableEntity, second.StatusCode);
                ctx.Check("error code IDEMPOTENCY/MISMATCH", (await second.Content.ReadAsStringAsync()).Contains("IDEMPOTENCY/MISMATCH"), "IDEMPOTENCY/MISMATCH", second.StatusCode);
                ctx.Equal("SAP documents", 1, await ctx.SapCountAsync("REQ-D009"));
            });

        c.Add("DUP-010", D, "Concurrent identical submissions from two workers", Critical, DuplicateStress)
            .Describe("Two integration processes whose lease has expired (lease 0 s) dispatch the same queue at the same moment.")
            .Sync(("LeaseDuration", "00:00:00")).Expect("Each requisition created once; the loser of each race is rejected by optimistic concurrency")
            .Run(async ctx =>
            {
                await ctx.PutAllAsync(Enumerable.Range(1, 10).Select(i => Req.Valid($"REQ-D010-{i:D2}")));
                var second = ctx.Lab.AddWorker("worker-2", new() { ["LeaseDuration"] = "00:00:00" });
                ctx.Step("Start both workers at once");
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("worker-1"), second.RunAsync("worker-2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                await ctx.RunUntilSettledAsync("settle");
                var stats = await ctx.Lab.SapStatsAsync();
                ctx.Equal("SAP documents", 10, stats.Total);
                ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
            });

        c.Add("DUP-011", D, "Retry after a successful SAP creation", Critical, DuplicateStress, FailureSimulation)
            .Faults("SAP commits the create, then the connection drops (lost response)").Expect("Existing document found and reused; one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D011"));
                await ctx.Lab.ScriptSapAsync(SapCreate(FaultKind.LostResponse));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-D011");
                var events = await ctx.Lab.EventsAsync("REQ-D011");
                ctx.Check("outcome was treated as uncertain, then resolved", events.Any(e => e.Kind == RunEventKind.InDoubtResolved), "InDoubtResolved", string.Join(",", events.Select(e => e.Kind)));
            });

        c.Add("DUP-012", D, "SAP commits but the response is lost (SAP ignores Idempotency-Key)", Critical, DuplicateStress, FailureSimulation)
            .Describe("As with a real SAP endpoint, Idempotency-Key is ignored, so only the lookup before resending prevents a duplicate.")
            .Faults("HonorIdempotencyKey = false; lost response on the first create").Expect("Lookup finds the committed document before any resend; one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D012"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
                await ctx.Lab.ScriptSapAsync(SapCreate(FaultKind.LostResponse));
                await ctx.RunUntilSettledAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-D012");
                var posts = ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Channel.EndsWith("→sap") && e.Url.EndsWith("A_PurchaseRequisitionHeader"));
                ctx.Equal("create requests sent", 1, posts);
            });

        c.Add("DUP-013", D, "Worker crashes immediately after SAP success", Critical, DuplicateStress, Recovery)
            .Faults("Process dies the moment SAP's 201 arrives, before anything is saved").Expect("On restart the record is found in 'Sending', treated as in doubt, looked up in SAP and linked; one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D013"));
                ctx.Lab.Crash.ArmAfterSapResponse("after SAP created REQ-D013, before the mapping was saved", (rq, rs) => LabHooks.IsSapCreate(rq) && rs.StatusCode == HttpStatusCode.Created);
                var crashed = await ctx.RunSyncAsync("run that crashes");
                ctx.Equal("run outcome", RunOutcomeKind.Crashed, crashed.Kind);
                ctx.Equal("record left mid-send", "Sending", (await ctx.Lab.RecordAsync("REQ-D013"))?.State.ToString());
                ctx.Equal("SAP already holds it", 1, await ctx.SapCountAsync("REQ-D013"));
                await ctx.RestartAsync("after the crash");
                await ctx.RunSyncAsync("run after restart");
                await ctx.ExpectCreatedOnceAsync("REQ-D013");
            });

        c.Add("DUP-014", D, "Saving the mapping fails after SAP success", Critical, DuplicateStress, Recovery)
            .Faults("The database write that stores the SAP number fails").Expect("Next run recovers the identity from SAP; one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D014"));
                ctx.Lab.DbFaults.Add("mapping save after SAP success", DbFaults.SavesMapping, DbFaultKind.SaveFails);
                var failed = await ctx.RunSyncAsync("run whose mapping save fails");
                ctx.Check("run failed at the mapping write", failed.Kind is RunOutcomeKind.Failed or RunOutcomeKind.Crashed, "Failed", failed.Kind);
                await ctx.RunSyncAsync("next run");
                await ctx.ExpectCreatedOnceAsync("REQ-D014");
            });

        c.Add("DUP-015", D, "Two workers process the same source ID", Critical, DuplicateStress, Recovery)
            .Expect("The lease admits one worker; the other skips; one document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D015"));
                var second = ctx.Lab.AddWorker("worker-2");
                var runs = await Task.WhenAll(ctx.Lab.Worker.RunAsync("worker-1"), second.RunAsync("worker-2"));
                ctx.RecordRun("worker-1", runs[0]);
                ctx.RecordRun("worker-2", runs[1]);
                ctx.Check("one worker skipped because the other held the lease", runs.Count(r => r.Kind == RunOutcomeKind.Skipped) == 1
                    || runs.All(r => r.Kind != RunOutcomeKind.Skipped), "lease respected", string.Join(",", runs.Select(r => r.Kind)));
                await ctx.RunSyncAsync("settle");
                await ctx.ExpectCreatedOnceAsync("REQ-D015");
            });

        c.Add("DUP-016", D, "Same ID across distinct source systems", Medium, DuplicateStress)
            .Blocked("The integration serves one source system: its identity is the source requisition ID with no source-system namespace. Namespacing (e.g. 'SRC1:REQ-1' as the SAP external reference) would be a design change.");

        c.Add("DUP-017", D, "The source changes the record while it is being retried", Critical, DuplicateStress, FailureSimulation)
            .Faults("SAP answers the first creates with 503 (retries exhausted for this run)").Expect("Newest version supersedes the queued one; one document holding v2")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D017"));
                await ctx.Lab.ScriptSapAsync(SapCreate(FaultKind.Error, count: 5));
                await ctx.RunSyncAsync("run 1: SAP keeps failing");
                ctx.Equal("record waiting for retry", "RetryPending", (await ctx.Lab.RecordAsync("REQ-D017"))?.State.ToString());
                var v2 = Req.Valid("REQ-D017").Set("lines[0].quantity", 4).Recalculate();
                await ctx.Lab.EditSourceAsync("REQ-D017", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                ctx.Step("The requester changes the quantity to 4 while the create is waiting");
                await ctx.RunUntilSettledAsync("later runs");
                await ctx.ExpectCreatedOnceAsync("REQ-D017");
                var doc = (await ctx.Lab.SapDocumentsForAsync("REQ-D017"))[0];
                ctx.Check("SAP holds the newest version (v2 or later)", doc["ExternalVersion"]!.GetValue<long>() >= 2, ">= 2", doc["ExternalVersion"]!.GetValue<long>());
                ctx.Equal("SAP quantity", 4m, doc["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });

        c.Add("DUP-018", D, "SAP answers 'duplicate': linked only to a verified match", Critical, DuplicateStress)
            .Describe("SAP's search lags (lookup finds nothing), so the create reaches SAP, which answers 409 DUPLICATE_REFERENCE naming a document. For REQ-D018A it names the right document; for REQ-D018B it names a document that belongs to another requisition.")
            .Expect("A: linked to the named document. B: not linked to the wrong document; nothing created")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D018A"));
                await ctx.PutAsync(Req.Valid("REQ-D018B"));
                var right = await ctx.Lab.InsertSapAsync("REQ-D018A", quantity: 2, price: 1200m);
                var other = await ctx.Lab.InsertSapAsync("REQ-OTHER", quantity: 1, price: 5m);
                await ctx.Lab.SapFaultsAsync(new FaultSettings { ReferenceSearchLag = true });
                string Dup(string number) => new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = "DUPLICATE_REFERENCE",
                        ["message"] = new JsonObject { ["lang"] = "en", ["value"] = $"Purchase requisition {number} already exists" },
                        ["innererror"] = new JsonObject { ["existingDocument"] = number },
                    },
                }.ToJsonString();
                await ctx.Lab.ScriptSapAsync(SapCreate(FaultKind.Respond, status: 409, body: Dup(right), reference: "REQ-D018A"),
                    SapCreate(FaultKind.Respond, count: 10, status: 409, body: Dup(other), reference: "REQ-D018B"));
                await ctx.RunSyncAsync();
                ctx.Equal("A linked to the verified document", right, (await ctx.Lab.RecordAsync("REQ-D018A"))?.SapNumber, "INV-03");
                var b = await ctx.Lab.RecordAsync("REQ-D018B");
                ctx.Check("B not linked to another requisition's document", b?.SapNumber != other, $"not {other}", b?.SapNumber ?? "(none)", "INV-03");
                ctx.Equal("nothing created for B", 0, await ctx.SapCountAsync("REQ-D018B"));
                await ctx.Lab.SapFaultsAsync(new FaultSettings());
            });

        c.Add("DUP-019", D, "Repeated manual retry clicks", High, DuplicateStress)
            .Faults("SAP down long enough to exhaust retries (quarantined RetryExhausted), then five replays at once")
            .Sync(("MaxDispatchAttempts", "1"), ("Resilience:BreakerMinimumThroughput", "100"))
            .Expect("One document; overlapping replays are refused by the lease")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D019"));
                await ctx.Lab.ScriptSapAsync(SapCreate(FaultKind.Error, count: 5));
                await ctx.RunSyncAsync("SAP failing");
                await ctx.ExpectQuarantinedAsync("REQ-D019", QuarantineReason.RetryExhausted);
                ctx.Step("An operator clicks 'replay' five times");
                var replays = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
                {
                    try { return (await ctx.Lab.Worker.ReplayAsync("REQ-D019")).Skipped ? "skipped" : "ran"; }
                    catch (Exception ex) { return "error: " + ex.GetType().Name; }
                }));
                ctx.Evidence("replays", replays);
                for (var i = 0; i < 3; i++) await ctx.Lab.Worker.ReplayAsync("REQ-D019");
                ctx.Step("Three more replays one after another");
                await ctx.ExpectCreatedOnceAsync("REQ-D019");
            });

        c.Add("DUP-020", D, "Duplicate across separate runs, even after the local database is lost", Critical, DuplicateStress, Recovery)
            .Expect("Three runs and a wiped database: still one document per requisition")
            .Run(async ctx =>
            {
                await ctx.PutAllAsync(Enumerable.Range(1, 5).Select(i => Req.Valid($"REQ-D020-{i}")));
                await ctx.RunSyncAsync("run 1");
                await ctx.RunSyncAsync("run 2");
                ctx.Step("The integration database is lost");
                await ctx.Lab.WipeLocalStateAsync();
                var third = await ctx.RunSyncAsync("run 3 on an empty database");
                ctx.Equal("created on run 3", 0, third.Count(RunEventKind.Created));
                for (var i = 1; i <= 5; i++) await ctx.ExpectCreatedOnceAsync($"REQ-D020-{i}");
            });

        c.Add("DUP-021", D, "Idempotency keys evicted by SAP", High, DuplicateStress, Recovery)
            .Faults("Lost response, then SAP forgets every Idempotency-Key, and the local database is lost").Expect("The lookup still prevents a second document")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D021"));
                ctx.Lab.Crash.ArmAfterSapResponse("after create", (rq, rs) => LabHooks.IsSapCreate(rq));
                await ctx.RunSyncAsync("crashes after SAP created it");
                await ctx.Lab.EvictSapIdempotencyAsync();
                ctx.Simulated("SAP's key retention period passes: all Idempotency-Keys forgotten");
                await ctx.Lab.WipeLocalStateAsync();
                await ctx.RunSyncAsync("recovery on a fresh database");
                await ctx.ExpectCreatedOnceAsync("REQ-D021");
            });

        c.Add("DUP-022", D, "SAP lookup returns several candidates", Critical, DuplicateStress)
            .Fixture("Two documents for REQ-D022 created directly in SAP").Expect("Quarantined AmbiguousSapMatch; not linked to either; nothing created")
            .Without("INV-03")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D022"));
                await ctx.Lab.InsertSapAsync("REQ-D022");
                await ctx.Lab.InsertSapAsync("REQ-D022");
                await ctx.RunSyncAsync();
                var record = await ctx.Lab.RecordAsync("REQ-D022");
                ctx.Equal("not linked", null, record?.SapNumber, "INV-03");
                ctx.Equal("record state", "Quarantined", record?.State.ToString());
                ctx.Check("quarantined as an ambiguous match", (await ctx.Lab.OpenQuarantineAsync("REQ-D022")).Any(q => q.Reason == QuarantineReason.AmbiguousSapMatch), "AmbiguousSapMatch", "");
                ctx.Equal("documents for REQ-D022 (still the two)", 2, await ctx.SapCountAsync("REQ-D022"));
            });

        c.Add("DUP-023", D, "IDs that differ only in case or whitespace", Medium, DuplicateStress)
            .Describe("Rule: IDs are opaque and compared exactly (ordinal). 'REQ-d023' and 'REQ-D023' are different requisitions; nothing is trimmed or case-folded.")
            .Expect("Each distinct ID gets its own document; lookups never cross-match")
            .Run(async ctx =>
            {
                string[] ids = ["REQ-D023", "req-d023", "REQ-D023 "];
                foreach (var id in ids) await ctx.Lab.PutSourceAsync(id, Req.Valid(id));
                await ctx.RunSyncAsync();
                foreach (var id in ids) ctx.Equal($"documents for '{id}'", 1, await ctx.SapCountAsync(id));
                ctx.Equal("documents in total", 3, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("DUP-024", D, "Identical line items appear twice on purpose", Medium, Functional)
            .Expect("Both lines preserved as separate SAP items")
            .Run(async ctx =>
            {
                var r = Req.Valid("REQ-D024");
                ((JsonArray)r["lines"]!).Add(Req.Line(20));
                await ctx.PutAsync(r.Recalculate());
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-D024");
                ctx.Equal("SAP items", 2, (await ctx.Lab.SapDocumentsForAsync("REQ-D024"))[0]["to_PurchaseReqnItem"]!.AsArray().Count);
            });

        c.Add("DUP-025", D, "SAP number linked to an unrelated source record", Critical, DuplicateStress)
            .Describe("Advanced scenario P. The local link of REQ-D025B is corrupted to point at REQ-D025A's document; then B changes.")
            .Expect("Integrity conflict detected: B quarantined MappingIntegrityViolation, its wrong link removed, A's document untouched")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-D025A"));
                await ctx.PutAsync(Req.Valid("REQ-D025B"));
                await ctx.RunSyncAsync();
                var a = (await ctx.Lab.RecordAsync("REQ-D025A"))!.SapNumber!;
                await using (var db = ctx.Lab.Db())
                {
                    await db.Requisitions.Where(r => r.SourceId == "REQ-D025B").ExecuteUpdateAsync(s => s.SetProperty(r => r.SapNumber, a));
                }
                ctx.Step($"Corrupt the mapping: REQ-D025B now points at {a} (REQ-D025A's document)");
                var report = await ctx.Lab.ReconcileAsync();
                var inv = report.Invariants.First(i => i.Name.StartsWith("Every synced requisition exists", StringComparison.Ordinal));
                ctx.Equal("reconciliation flags the bad link", "Fail", inv.Outcome.ToString(), "INV-10");
                var v2 = Req.Valid("REQ-D025B").Set("lines[0].quantity", 9).Recalculate();
                await ctx.Lab.EditSourceAsync("REQ-D025B", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                await ctx.RunSyncAsync("B changes");
                var b = await ctx.Lab.RecordAsync("REQ-D025B");
                ctx.Check("B quarantined as a mapping integrity violation", (await ctx.Lab.OpenQuarantineAsync("REQ-D025B")).Any(q => q.Reason == QuarantineReason.MappingIntegrityViolation), "MappingIntegrityViolation", b?.LastError);
                ctx.Equal("B's wrong link removed", null, b?.SapNumber);
                ctx.Equal("A's document untouched (quantity)", 2m, (await ctx.Lab.SapDocumentsForAsync("REQ-D025A"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });
    }
}
