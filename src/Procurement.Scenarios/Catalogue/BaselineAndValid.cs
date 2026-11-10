using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;
using Procurement.SyntheticData;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static partial class BaselineAndValid
{
    internal static readonly LabOptions Workbook = new() { Dataset = "Workbook", Sync = new() { ["PageSize"] = "40", ["Resilience:BreakerMinimumThroughput"] = "100" } };

    public static void Register(CatalogueBuilder c)
    {
        const string B = Categories.Baseline;

        c.Add("BASE-001", B, "Workbook first run matches Reconciliation_Expected row by row", Critical, Baseline)
            .Describe("The client's 120-record workbook through the real pipeline, with its scheduled 429/503/409 failures, compared with every row of Reconciliation_Expected.")
            .Lab(Workbook).Pre("Source and SAP loaded from the client's workbook; 7 requisitions already in SAP.")
            .Fixture("Sajid_Procurement_Integration_Synthetic_Data_Expanded.xlsx (unchanged)").Faults("The workbook's Failure_Injection sheet: 14 × 429, 14 × 503, 7 × 409 version conflict, 7 pre-existing")
            .Steps("Run one sync", "Compare result, SAP document, attempts, failure category and source version for all 120 rows")
            .Expect("120/120 rows match; 58 created, 7 linked, 50 quarantined, 5 skipped, 0 duplicates", http: "429/503 retried after Retry-After; 409 resolved", sap: "65 documents, no duplicate references", recon: "No invariant fails")
            .Run(async ctx =>
            {
                var run = await ctx.RunSyncAsync("first run over the workbook");
                var report = run.Result?.Report;
                ctx.Check("run completed", report is not null, "a report", run.Kind);
                if (report is null) return;
                var actual = report.Records.ToDictionary(r => r.Key);
                var mismatches = new List<string>();
                foreach (var e in DataPack.Load().ReconciliationExpected)
                {
                    if (!actual.TryGetValue(e.RequisitionId, out var a)) { mismatches.Add($"{e.RequisitionId}: missing"); continue; }
                    if (e.ProcessingResult != a.Result) mismatches.Add($"{e.RequisitionId} result {e.ProcessingResult}≠{a.Result}");
                    if (e.RemoteDocumentId != a.SapNumber) mismatches.Add($"{e.RequisitionId} document {e.RemoteDocumentId}≠{a.SapNumber}");
                    if (e.Attempts != a.Attempts) mismatches.Add($"{e.RequisitionId} attempts {e.Attempts}≠{a.Attempts}");
                    if (e.FailureCategory != a.FailureCategory) mismatches.Add($"{e.RequisitionId} category {e.FailureCategory}≠{a.FailureCategory}");
                    if (e.SourceVersion != a.SourceVersion) mismatches.Add($"{e.RequisitionId} version {e.SourceVersion}≠{a.SourceVersion}");
                }
                ctx.Evidence("mismatches", mismatches);
                ctx.Equal("rows matching Reconciliation_Expected", "120/120", $"{120 - mismatches.Count}/120");
                var results = report.Records.GroupBy(r => r.Result).ToDictionary(g => g.Key, g => g.Count());
                ctx.Evidence("results", results);
                ctx.Equal("created", 58, results.GetValueOrDefault("Created"));
                ctx.Equal("existing SAP documents linked", 7, results.GetValueOrDefault("Duplicate"));
                ctx.Equal("quarantined", 50, results.GetValueOrDefault("Quarantined"));
                ctx.Equal("skipped (not approved)", 5, results.GetValueOrDefault("Skipped"));
                ctx.Equal("newly created duplicates in SAP", 0, (await ctx.Lab.SapStatsAsync()).DuplicateReferences, "INV-01");
            });

        c.Add("BASE-002", B, "Workbook quarantine matches Quarantine_Expected", Critical, Baseline)
            .Lab(Workbook).Fixture("Client workbook").Expect("All 50 quarantined with the expected reason and field; raw payload kept")
            .Run(async ctx =>
            {
                await ctx.RunSyncAsync();
                var open = await ctx.Lab.OpenQuarantineAsync();
                var map = new Dictionary<string, string> { ["AMOUNT_NOT_POSITIVE"] = "AmountNotPositive", ["REQUIRED_DATE_MISSING"] = "MissingRequiredField" };
                var wrong = new List<string>();
                foreach (var q in DataPack.Load().QuarantineExpected)
                {
                    var entry = open.FirstOrDefault(e => e.SourceKey == q.RequisitionId);
                    if (entry is null) { wrong.Add($"{q.RequisitionId}: not quarantined"); continue; }
                    if (map.TryGetValue(q.ReasonCode, out var reason) && entry.Reason.ToString() != reason) wrong.Add($"{q.RequisitionId}: {entry.Reason}≠{reason}");
                    if (string.IsNullOrEmpty(entry.RawPayload)) wrong.Add($"{q.RequisitionId}: no raw payload");
                }
                ctx.Evidence("differences", wrong);
                ctx.Equal("quarantine rows matching Quarantine_Expected", $"{DataPack.Load().QuarantineExpected.Count}/{DataPack.Load().QuarantineExpected.Count}",
                    $"{DataPack.Load().QuarantineExpected.Count - wrong.Count}/{DataPack.Load().QuarantineExpected.Count}");
                ctx.Equal("open quarantine entries", 50, open.Count);
            });

        c.Add("BASE-003", B, "SAP afterwards matches Mock_Remote_Status", Critical, Baseline)
            .Lab(Workbook).Expect("65 SAP documents with the expected numbers; no duplicate references")
            .Run(async ctx =>
            {
                await ctx.RunSyncAsync();
                var docs = await ctx.Lab.SapDocumentsAsync();
                var byRef = docs.ToDictionary(d => d["ExternalReference"]!.GetValue<string>(), d => d["PurchaseRequisition"]!.GetValue<string>());
                var wrong = DataPack.Load().RemoteStatus.Where(r => !string.IsNullOrEmpty(r.RemoteDocumentId) && byRef.GetValueOrDefault(r.RequisitionId) != r.RemoteDocumentId)
                    .Select(r => $"{r.RequisitionId}: expected {r.RemoteDocumentId}, SAP has {byRef.GetValueOrDefault(r.RequisitionId) ?? "nothing"}").ToList();
                ctx.Evidence("differences", wrong);
                ctx.Equal("SAP documents", 65, docs.Count);
                ctx.Equal("documents with the expected number", 0, wrong.Count);
            });

        c.Add("BASE-004", B, "Rerunning the workbook creates and changes nothing", Critical, Baseline, DuplicateStress)
            .Lab(Workbook).Expect("Second run: 0 created, 0 amended, still 65 documents")
            .Run(async ctx =>
            {
                await ctx.RunSyncAsync("first run");
                var second = await ctx.RunSyncAsync("second run");
                ctx.Equal("created on rerun", 0, second.Count(RunEventKind.Created));
                ctx.Equal("amended on rerun", 0, second.Count(RunEventKind.Amended));
                ctx.Equal("SAP documents", 65, (await ctx.Lab.SapStatsAsync()).Total);
            });

        c.Add("BASE-005", B, "Bulk replay: the workbook five times gives stable outcomes", High, Baseline, DuplicateStress)
            .Describe("Advanced scenario L. The source is reloaded from the workbook five times (as five re-imports would) and synced each time.")
            .Lab(Workbook).Expect("Identical outcomes every time; 65 documents; zero new duplicates")
            .Run(async ctx =>
            {
                await ctx.RunSyncAsync("import 1");
                for (var i = 2; i <= 5; i++)
                {
                    await ctx.Lab.ResetSourceAsync("Workbook");
                    ctx.Step($"Re-import the workbook (import {i}): every record reappears with its original version");
                    await ctx.RunSyncAsync($"import {i}");
                }
                var stats = await ctx.Lab.SapStatsAsync();
                ctx.Equal("SAP documents after five imports", 65, stats.Total);
                ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
                var open = await ctx.Lab.OpenQuarantineAsync();
                ctx.Equal("quarantine entries (unchanged)", 50, open.Count);
            });

        c.AddApi("BASE-006", B, "All 60 AI_API_Contract_Tests rows return the expected status", Critical, Baseline, Security)
            .Lab(Workbook).Expect("60/60 expected HTTP statuses; every successful create is refused without confirmation first")
            .Run(async ctx =>
            {
                await ctx.RunSyncAsync("transfer so status reads include SAP");
                var pack = DataPack.Load();
                var directory = EmployeeDirectory.Load();
                var failures = new List<string>();
                foreach (var test in pack.ApiTests)
                {
                    using var client = ctx.Lab.Assistant.ClientFor(test.EmployeeId);
                    int status;
                    if (test.Operation == "GET status")
                    {
                        var id = RequisitionId().Match(test.RequestSummary).Value;
                        status = (int)(await client.GetAsync($"/v1/requisition-requests/{id}")).StatusCode;
                    }
                    else
                    {
                        var employee = directory.Find(test.EmployeeId);
                        var purpose = test.RequestSummary.Replace("Create synthetic draft for ", "", StringComparison.Ordinal);
                        var item = pack.Requisitions.Where(r => r.Purpose == purpose).SelectMany(r => r.Lines).OrderBy(l => l.UnitPrice).First();
                        var draft = new JsonObject
                        {
                            ["costCenter"] = employee?.CostCentre ?? item.CostCentre, ["plant"] = DataPack.Plant, ["neededBy"] = Req.Today(30),
                            ["justification"] = $"{purpose} requested through the assistant ({test.TestId})",
                            ["lines"] = new JsonArray(new JsonObject { ["materialCode"] = item.ItemCode, ["quantity"] = 1 }), ["assistantSessionRef"] = test.TestId,
                        };
                        if (test.ConfirmationRequired && test.ExpectedHttpStatus == 201)
                        {
                            var unconfirmed = await Assist.CreateAsync(client, draft, test.TestId + "-unconfirmed", null);
                            if (unconfirmed.Response.StatusCode != HttpStatusCode.PreconditionRequired)
                                failures.Add($"{test.TestId}: without confirmation expected 428, got {(int)unconfirmed.Response.StatusCode}");
                        }
                        status = (int)(await Assist.ConfirmedCreateAsync(client, draft, test.TestId)).Response.StatusCode;
                    }
                    if (status != test.ExpectedHttpStatus) failures.Add($"{test.TestId} {test.Operation} as {test.EmployeeId}: expected {test.ExpectedHttpStatus}, got {status}");
                }
                ctx.Evidence("failures", failures);
                ctx.Equal("contract rows passing", $"{pack.ApiTests.Count}/{pack.ApiTests.Count}", $"{pack.ApiTests.Count - failures.Count}/{pack.ApiTests.Count}", "INV-08");
                ctx.Check("no create succeeded without the user's confirmation", !failures.Any(f => f.Contains("428")), "428 every time", string.Join("; ", failures.Where(f => f.Contains("428"))), "INV-09");
            });
        const string V = Categories.Valid;

        c.Add("VAL-001", V, "Single valid approved requisition", Critical, Functional)
            .Fixture("One approved requisition, one line").Expect("Created exactly once; linked; ERP status written back", sap: "1 document", db: "Synced with SAP number")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V001"));
                var run = await ctx.RunSyncAsync();
                ctx.Equal("created events", 1, run.Count(RunEventKind.Created));
                await ctx.ExpectCreatedOnceAsync("REQ-V001");
                var source = await ctx.Lab.SourceRecordAsync("REQ-V001");
                ctx.Check("source shows the SAP reference", source?["erpSync"]?["reference"] is not null, "erpSync.reference set", source?["erpSync"]?.ToJsonString());
            });

        c.Add("VAL-002", V, "Multiple valid requisitions", High, Functional)
            .Fixture("25 approved requisitions").Expect("All 25 created once each")
            .Run(async ctx =>
            {
                await ctx.PutAllAsync(Enumerable.Range(1, 25).Select(i => Req.Valid($"REQ-V002-{i:D2}")));
                await ctx.RunSyncAsync();
                var docs = await ctx.Lab.SapDocumentsAsync();
                ctx.Equal("SAP documents", 25, docs.Count);
                ctx.Equal("distinct references", 25, docs.Select(d => d["ExternalReference"]!.GetValue<string>()).Distinct().Count());
            });

        c.Add("VAL-003", V, "Requisition with multiple line items", High, Functional)
            .Fixture("One requisition, 7 lines with different materials, quantities and prices").Expect("All 7 lines mapped with item numbers 00010 to 00070")
            .Run(async ctx =>
            {
                var r = Req.Valid("REQ-V003", lines: 7);
                for (var i = 0; i < 7; i++) r.Set($"lines[{i}].quantity", i + 1).Set($"lines[{i}].unitPrice", 10.5m * (i + 1));
                await ctx.PutAsync(r.Recalculate());
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-V003");
                var items = (await ctx.Lab.SapDocumentsForAsync("REQ-V003"))[0]["to_PurchaseReqnItem"]!.AsArray();
                ctx.Equal("SAP items", 7, items.Count);
                ctx.Equal("item numbers", "00010,00020,00030,00040,00050,00060,00070", string.Join(',', items.Select(i => i!["PurchaseRequisitionItem"]!.GetValue<string>())));
                ctx.Equal("line 7 quantity", 7m, items[6]!["RequestedQuantity"]!.GetValue<decimal>());
                ctx.Equal("line 7 price", 73.5m, items[6]!["PurchaseRequisitionPrice"]!.GetValue<decimal>());
            });

        c.Add("VAL-004", V, "Valid high-value requisition", High, Functional)
            .Fixture("Approved requisition of 2,450,000.00 USD").Expect("Processed: the integration transfers what the source approved; amount preserved exactly")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V004", quantity: 7, price: 350000m));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-V004");
                var item = (await ctx.Lab.SapDocumentsForAsync("REQ-V004"))[0]["to_PurchaseReqnItem"]![0]!;
                ctx.Equal("amount in SAP", 2_450_000m, item["RequestedQuantity"]!.GetValue<decimal>() * item["PurchaseRequisitionPrice"]!.GetValue<decimal>());
            });

        c.Add("VAL-005", V, "Valid low-value requisition", Medium, Functional)
            .Fixture("One line of 1 × 0.01 USD").Expect("Processed normally")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V005", quantity: 1, price: 0.01m));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-V005");
            });

        c.Add("VAL-006", V, "Multiple currencies: supported accepted, unsupported refused", High, Functional, Negative)
            .Fixture("EUR (company 1000), AED, GBP; and one in XYZ").Expect("Supported currencies created with their currency; XYZ quarantined UnknownCurrency")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V006-EUR", currency: "EUR"));
                await ctx.PutAsync(Req.Valid("REQ-V006-GBP", currency: "GBP"));
                await ctx.PutAsync(Req.Valid("REQ-V006-XYZ", currency: "XYZ"));
                await ctx.RunSyncAsync();
                foreach (var (id, cur) in new[] { ("REQ-V006-EUR", "EUR"), ("REQ-V006-GBP", "GBP") })
                {
                    await ctx.ExpectCreatedOnceAsync(id);
                    var doc = (await ctx.Lab.SapDocumentsForAsync(id))[0];
                    ctx.Equal($"{id}: SAP currency", cur, doc["to_PurchaseReqnItem"]![0]!["PurReqnItemCurrency"]!.GetValue<string>());
                }
                await ctx.ExpectQuarantinedAsync("REQ-V006-XYZ", Procurement.Contracts.QuarantineReason.UnknownCurrency, "Currency");
            });

        c.Add("VAL-007", V, "Unicode descriptions are preserved", High, Functional)
            .Fixture("Descriptions in Arabic, Japanese and with emoji (within SAP's 40-character text limit)").Expect("SAP item texts equal the source text, byte for byte")
            .Run(async ctx =>
            {
                string[] texts = ["طابعة مكتبية ملونة", "東京オフィス用ノートPC", "Café crème ☕, naïve", "Ünïcödé 💡 lamp"];
                var r = Req.Valid("REQ-V007", lines: texts.Length);
                for (var i = 0; i < texts.Length; i++) r.Set($"lines[{i}].description", texts[i]);
                await ctx.PutAsync(r);
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-V007");
                var items = (await ctx.Lab.SapDocumentsForAsync("REQ-V007"))[0]["to_PurchaseReqnItem"]!.AsArray();
                for (var i = 0; i < texts.Length; i++) ctx.Equal($"text {i + 1}", texts[i], items[i]!["PurchaseRequisitionItemText"]!.GetValue<string>());
            });

        c.Add("VAL-008", V, "Large batch of valid records: no data loss", High, Functional, Performance)
            .Fixture("600 approved requisitions over 12 pages").Expect("All 600 created once; nothing missing").Timeout(300)
            .Lab(new LabOptions { CaptureHttp = true })
            .Run(async ctx =>
            {
                await ctx.PutAllAsync(Enumerable.Range(1, 600).Select(i => Req.Valid($"REQ-V008-{i:D4}")));
                await ctx.RunUntilSettledAsync();
                var stats = await ctx.Lab.SapStatsAsync();
                ctx.Equal("SAP documents", 600, stats.Total);
                ctx.Equal("duplicate references", 0, stats.DuplicateReferences, "INV-01");
            });

        c.Add("VAL-009", V, "Already synchronised requisition reuses its mapping", Critical, Functional, DuplicateStress)
            .Expect("Second run sends nothing; the same SAP number stays linked")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V009"));
                await ctx.RunSyncAsync("first run");
                var number = (await ctx.Lab.RecordAsync("REQ-V009"))?.SapNumber;
                var before = ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Channel.EndsWith("→sap") && e.Url.EndsWith("A_PurchaseRequisitionHeader"));
                var second = await ctx.RunSyncAsync("second run");
                var after = ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Channel.EndsWith("→sap") && e.Url.EndsWith("A_PurchaseRequisitionHeader"));
                ctx.Equal("SAP creates sent on the second run", 0, after - before);
                ctx.Equal("same SAP number", number, (await ctx.Lab.RecordAsync("REQ-V009"))?.SapNumber);
                ctx.Equal("created on second run", 0, second.Count(RunEventKind.Created));
            });

        c.Add("VAL-010", V, "Requisition with many line items (100)", Medium, Functional)
            .Describe("The integration sets no maximum; SAP item numbers run from 00010 to 01000. The assistant API separately limits AI drafts to 20 lines.")
            .Expect("All 100 lines mapped, amounts equal")
            .Run(async ctx =>
            {
                await ctx.PutAsync(Req.Valid("REQ-V010", lines: 100, quantity: 1, price: 9.99m));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-V010");
                var items = (await ctx.Lab.SapDocumentsForAsync("REQ-V010"))[0]["to_PurchaseReqnItem"]!.AsArray();
                ctx.Equal("SAP items", 100, items.Count);
                ctx.Equal("last item number", "01000", items[^1]!["PurchaseRequisitionItem"]!.GetValue<string>());
            });
    }

    [GeneratedRegex(@"REQ-\d+")]
    private static partial Regex RequisitionId();
}
