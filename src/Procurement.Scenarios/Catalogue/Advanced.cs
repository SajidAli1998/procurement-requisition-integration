using Procurement.Contracts;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.SyntheticData;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

/// <summary>Advanced scenarios A to P. Where one is the same test as a catalogue entry, it runs that body.</summary>
internal static class Advanced
{
    public static void Register(CatalogueBuilder c)
    {
        const string V = Categories.Advanced;
        ScenarioDefinition Of(string id) => c.All.Single(s => s.Id == id);

        ScenarioBuilder Same(string id, string title, string source, Priority p = Critical, params Mode[] modes)
        {
            var original = Of(source);
            var b = c.Add(id, V, title, p, modes.Length == 0 ? [.. original.Modes] : modes)
                .Describe($"Same test as {source}: {original.Title}. {original.Description}").Covered(source)
                .Pre(original.Preconditions).Faults(original.Faults).Lab(original.Lab).Timeout(original.TimeoutSeconds)
                .Expect(original.Expected.Summary, original.Expected.Http, original.Expected.Source, original.Expected.Sap, original.Expected.Database, original.Expected.Reconciliation)
                .Inv([.. original.Invariants]);
            if (original.BlockedReason is not null) b.Blocked(original.BlockedReason);
            else if (original.Body is not null) b.Run(original.Body);
            return b;
        }

        Same("ADV-A", "Ambiguous SAP creation: committed, answer lost", "DUP-012");
        Same("ADV-B", "Crash between the SAP creation and the database save", "DUP-013");

        c.Add("ADV-C", V, "Approval revoked during processing (before and after the commit)", Critical, Negative)
            .Sync(("ApprovalRecheck", "Always"))
            .Describe("Both halves in one lab. Revoked before the create: nothing is created. Revoked in the instant after SAP committed: the document is kept and the race is reported for a person (compensation policy).")
            .Expect("A: not created. B: one document, quarantined SourceChangedAfterTransfer").Covered("APR-007, APR-007B")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-ADVC-A", Req.Valid("REQ-ADVC-A"));
                await ctx.Lab.PutSourceAsync("REQ-ADVC-B", Req.Valid("REQ-ADVC-B"));
                ctx.Lab.Hooks.Before("sap", r => LabHooks.IsSapLookup(r) && r.RequestUri!.Query.Contains("ADVC-A"), () => ctx.Lab.EditSourceAsync("REQ-ADVC-A", new { status = "PendingApproval", approvedAt = (string?)null }));
                ctx.Lab.Hooks.Before("sap", r => LabHooks.IsSapCreate(r), () => ctx.Lab.EditSourceAsync("REQ-ADVC-B", new { status = "Cancelled" }));
                await ctx.RunSyncAsync("both races");
                await ctx.RunSyncAsync("next run");
                await ctx.ExpectSkippedAsync("REQ-ADVC-A");
                ctx.Equal("B's document kept (not denied)", 1, await ctx.SapCountAsync("REQ-ADVC-B"));
                ctx.Check("B's race reported", (await ctx.Lab.OpenQuarantineAsync("REQ-ADVC-B")).Any(q => q.Reason == QuarantineReason.SourceChangedAfterTransfer), "SourceChangedAfterTransfer", "");
            });

        c.Add("ADV-D", V, "Same ID, different business data", Critical, DuplicateStress, Negative)
            .Describe("The source serves REQ-ADVD again under the same version number with a different amount (a versioning bug upstream), and REQ-ADVD2 twice on one page with different quantities.")
            .Expect("Detected as a conflict, never treated as a harmless repeat: quarantined ConflictingSourceContent; SAP keeps the first content; nothing sent for the in-page conflict")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-ADVD", Req.Valid("REQ-ADVD"), version: 1);
                await ctx.RunSyncAsync("v1 transferred");
                // The version the integration processed (our own status write-back has since moved the source on).
                var v = (await ctx.Lab.RecordAsync("REQ-ADVD"))!.SourceVersion;
                await ctx.Lab.PutSourceAsync("REQ-ADVD", Req.Valid("REQ-ADVD").Set("lines[0].unitPrice", 99_000m).Recalculate(), version: v);
                ctx.Step($"The source re-serves version {v}, already processed, with a different price");
                await ctx.RunSyncAsync("same version, different content");
                var q = await ctx.Lab.OpenQuarantineAsync("REQ-ADVD");
                ctx.Check("quarantined ConflictingSourceContent", q.Any(e => e.Reason == QuarantineReason.ConflictingSourceContent), "ConflictingSourceContent", string.Join(",", q.Select(e => e.Reason)));
                ctx.Equal("SAP keeps the first price", 1200m, (await ctx.Lab.SapDocumentsForAsync("REQ-ADVD"))[0]["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionPrice"]!.GetValue<decimal>());

                var a = Req.Valid("REQ-ADVD2");
                var b = Req.Valid("REQ-ADVD2", quantity: 7);
                a["version"] = 1; a["updatedAt"] = DateTime.UtcNow.ToString("O");
                b["version"] = 1; b["updatedAt"] = DateTime.UtcNow.ToString("O");
                var page = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonArray(a, b), ["nextCursor"] = null, ["hasMore"] = false };
                await ctx.Lab.ScriptSourceAsync(new Mocks.Shared.Faults.ScriptedFault { Kind = Mocks.Shared.Faults.FaultKind.Respond, Method = "GET", PathContains = "/api/requisitions", StatusCode = 200, Body = page.ToJsonString() });
                await ctx.RunSyncAsync("same id twice on one page, different quantities");
                ctx.Equal("nothing sent for the in-page conflict", 0, await ctx.SapCountAsync("REQ-ADVD2"));
                ctx.Check("in-page conflict quarantined", (await ctx.Lab.OpenQuarantineAsync("REQ-ADVD2")).Any(e => e.Reason == QuarantineReason.ConflictingSourceContent), "ConflictingSourceContent", "");
            })
            .Without("INV-02");

        c.Add("ADV-E", V, "One invalid line among ten", Critical, Negative)
            .Describe("Policy: whole-document rejection. A requisition is approved as a whole, so it is never sent with a line missing.")
            .Expect("Quarantined naming line 70; nothing sent; the source keeps all ten lines").Run(async ctx =>
            {
                var r = Req.Valid("REQ-ADVE", lines: 10);
                r.Set("lines[6].quantity", 0).Recalculate();
                await ctx.Lab.PutSourceAsync("REQ-ADVE", r);
                await ctx.RunSyncAsync();
                await ctx.ExpectQuarantinedAsync("REQ-ADVE", QuarantineReason.InvalidQuantity, "Line 70");
                ctx.Equal("no line dropped in the source", 10, (await ctx.Lab.SourceRecordAsync("REQ-ADVE"))?["lines"]?.AsArray().Count);
            });

        Same("ADV-F", "SAP outage for several hours", "SAP-025");

        c.Add("ADV-G", V, "Source changes while the sync is paging (modify, delete, insert)", Critical, FailureSimulation)
            .Sync(("PageSize", "4"))
            .Expect("Modified record ends with its latest version; deleted (unread) record never sent; inserted record read in the same run; all accounted for")
            .Run(async ctx =>
            {
                for (var i = 1; i <= 10; i++) await ctx.Lab.PutSourceAsync($"REQ-ADVG-{i:D2}", Req.Valid($"REQ-ADVG-{i:D2}"));
                var calls = 0;
                ctx.Lab.Hooks.Before("source", r => LabHooks.IsSourceList(r) && ++calls == 2, async () =>
                {
                    var v2 = Req.Valid("REQ-ADVG-01").Set("lines[0].quantity", 5).Recalculate();
                    await ctx.Lab.EditSourceAsync("REQ-ADVG-01", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                    await ctx.Lab.DeleteSourceAsync("REQ-ADVG-10");
                    await ctx.Lab.PutSourceAsync("REQ-ADVG-99", Req.Valid("REQ-ADVG-99"));
                });
                await ctx.RunUntilSettledAsync();
                ctx.Equal("modified: latest quantity in SAP", 5m, (await ctx.Lab.SapDocumentsForAsync("REQ-ADVG-01"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
                ctx.Equal("deleted before read: not in SAP", 0, await ctx.SapCountAsync("REQ-ADVG-10"));
                ctx.Equal("inserted mid-read: in SAP", 1, await ctx.SapCountAsync("REQ-ADVG-99"));
            });

        Same("ADV-H", "Manual and automatic retry collide", "CON-004");
        c.Add("ADV-I", V, "Cross-tenant identity collision", Medium, DuplicateStress)
            .Blocked("Single-tenant, single-source build: identity is the source requisition id. Namespacing ids per tenant/source (in the SAP external reference, the mapping key, the idempotency key and the report) would be a design change.");

        c.Add("ADV-J", V, "Contradictory reconciliation data (expected 'created', SAP has nothing)", Critical, Baseline)
            .Lab(BaselineAndValid.Workbook).Without("INV-03", "INV-07")
            .Expect("The discrepancy is reported; the expected values are never altered to make it pass").Run(async ctx =>
            {
                await ctx.RunSyncAsync("workbook run");
                var number = (await ctx.Lab.RecordAsync("REQ-7003"))!.SapNumber!;
                await ctx.Lab.DeleteSapAsync(number);
                ctx.Step($"Someone deletes {number} (REQ-7003) in SAP");
                var expected = DataPack.Load().ReconciliationExpected.Single(e => e.RequisitionId == "REQ-7003");
                ctx.Equal("expected sheet still says Created (unchanged)", "Created", expected.ProcessingResult);
                var report = await ctx.Lab.ReconcileAsync();
                var inv = report.Invariants.First(i => i.Name.StartsWith("Every synced", StringComparison.Ordinal));
                ctx.Equal("reconciliation fails", "Fail", inv.Outcome.ToString(), "INV-10");
                ctx.Check("names REQ-7003", inv.Detail.Contains("REQ-7003"), "REQ-7003", inv.Detail);
            });

        Same("ADV-K", "Phantom success (2xx without a verifiable document)", "SAP-015");
        Same("ADV-L", "Bulk import replay (five times)", "BASE-005");
        Same("ADV-M", "Reused confirmation token after modification", "AI-011");
        Same("ADV-N", "Browser says success, backend fails", "UX-026", High);
        Same("ADV-O", "Status race (newer then stale status)", "STS-003");
        Same("ADV-P", "Mapping collision", "DUP-025");
    }
}
