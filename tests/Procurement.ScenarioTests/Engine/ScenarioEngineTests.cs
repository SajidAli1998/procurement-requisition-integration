using Microsoft.EntityFrameworkCore;
using Procurement.Scenarios.Catalogue;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;

namespace Procurement.ScenarioTests.Engine;

/// <summary>A broken system fails its invariants, and nothing unasserted, blocked or missing counts as passed.</summary>
public sealed class ScenarioEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScenarioDefinition Def(string id, Func<ScenarioContext, Task>? body, params string[] invariants)
    {
        var d = new ScenarioDefinition { Id = id, Category = "test", Title = id, Body = body };
        foreach (var i in invariants) d.Invariants.Add(i);
        return d;
    }

    private static Task<ScenarioResult> RunAsync(ScenarioDefinition d) => ScenarioRunner.RunOneAsync(d, new RunRequest(), null, null, Ct);

    [Fact(DisplayName = "E01 A duplicate created behind the integration's back fails INV-01 (mutation-resistant)")]
    public async Task Injected_duplicate_is_caught()
    {
        var result = await RunAsync(Def("MUT-01", async ctx =>
        {
            await ctx.Lab.PutSourceAsync("MUT-1", Req.Valid("MUT-1"));
            await ctx.RunSyncAsync();
            // Simulate a defective integration that created the document twice.
            await ctx.Lab.SapAdmin.PostAsync(LabEnvironment.SapBase + "A_PurchaseRequisitionHeader",
                System.Net.Http.Json.JsonContent.Create(new { ExternalReference = "MUT-1", ExternalVersion = 1, to_PurchaseReqnItem = new[] { new { RequestedQuantity = 1, BaseUnit = "EA", PurchaseRequisitionPrice = 1, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", DeliveryDate = Req.Today(9) } } }), Ct);
            ctx.Check("ran", true);
        }, "INV-01"));
        result.Status.ShouldBe(ScenarioStatus.Fail);
        result.Invariants.Single(i => i.Id == "INV-01").Outcome.ShouldBe(InvariantOutcome.Fail);
    }

    [Fact(DisplayName = "E02 A record marked synced without a SAP document fails INV-03")]
    public async Task Phantom_success_is_caught()
    {
        var result = await RunAsync(Def("MUT-02", async ctx =>
        {
            await ctx.Lab.PutSourceAsync("MUT-2", Req.Valid("MUT-2"));
            await ctx.RunSyncAsync();
            await using var db = ctx.Lab.Db();
            await db.Requisitions.ExecuteUpdateAsync(s => s.SetProperty(r => r.SapNumber, "0099999999"), Ct);
            ctx.Check("ran", true);
        }, "INV-03"));
        result.Invariants.Single(i => i.Id == "INV-03").Outcome.ShouldBe(InvariantOutcome.Fail);
        result.Status.ShouldBe(ScenarioStatus.Fail);
    }

    [Fact(DisplayName = "E03 A blind repeat of an uncertain create fails INV-11")]
    public async Task Unsafe_retry_is_caught()
    {
        var result = await RunAsync(Def("MUT-03", async ctx =>
        {
            // Two creates for one reference where the first got no answer and nothing was looked up in between.
            var body = System.Net.Http.Json.JsonContent.Create(new { ExternalReference = "MUT-3", ExternalVersion = 1, to_PurchaseReqnItem = new[] { new { RequestedQuantity = 1, BaseUnit = "EA", PurchaseRequisitionPrice = 1, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", DeliveryDate = Req.Today(9) } } });
            using var client = ctx.Lab.Sap.CreateDefaultClient(new CaptureHandler(ctx.Lab.Capture, "buggy→sap"));
            await ctx.Lab.ScriptSapAsync(new global::Mocks.Shared.Faults.ScriptedFault { Kind = global::Mocks.Shared.Faults.FaultKind.CommitThenRespond, Method = "POST", StatusCode = 500 });
            await client.PostAsync(LabEnvironment.SapBase + "A_PurchaseRequisitionHeader", body, Ct);
            await client.PostAsync(LabEnvironment.SapBase + "A_PurchaseRequisitionHeader", body, Ct);
            ctx.Check("ran", true);
        }, "INV-11"));
        result.Invariants.Single(i => i.Id == "INV-11").Outcome.ShouldBe(InvariantOutcome.Fail,
            result.Invariants.Single(i => i.Id == "INV-11").Detail + " | " + string.Join("; ", result.Http.Select(h => $"{h.Seq} {h.Channel} {h.Method} {h.Url} {h.Status} {h.RequestBody?.Length}")) + " | " + result.Error);
    }

    [Fact(DisplayName = "E04 No assertions means no pass; blocked and unimplemented are never passes")]
    public async Task Statuses_are_honest()
    {
        (await RunAsync(Def("EMPTY", _ => Task.CompletedTask))).Status.ShouldBe(ScenarioStatus.Fail);
        (await RunAsync(new ScenarioDefinition { Id = "B", Category = "t", Title = "B", BlockedReason = "not here" })).Status.ShouldBe(ScenarioStatus.Blocked);
        (await RunAsync(new ScenarioDefinition { Id = "N", Category = "t", Title = "N" })).Status.ShouldBe(ScenarioStatus.NotImplemented);
        (await RunAsync(Def("T", ctx => { ctx.Block("no SQL Server"); return Task.CompletedTask; }))).Status.ShouldBe(ScenarioStatus.Blocked);
    }

    [Fact(DisplayName = "E05 Catalogue: unique ids, every category populated, every blocked entry explains why")]
    public void Catalogue_is_well_formed()
    {
        var all = ScenarioCatalogue.All;
        all.Select(s => s.Id).ShouldBeUnique();
        Categories.Ordered.ShouldAllBe(c => all.Any(s => s.Category == c));
        all.Where(s => s.BlockedReason is not null).ShouldAllBe(s => s.BlockedReason!.Length > 30);
        all.Where(s => s.BlockedReason is null).ShouldAllBe(s => s.Implemented);
    }

    [Theory(DisplayName = "E06 Priority scenarios pass through the engine (reconciliation regression)")]
    [InlineData("BASE-001")]
    [InlineData("DUP-012")]
    [InlineData("DUP-013")]
    [InlineData("SAP-015")]
    [InlineData("ADV-D")]
    [InlineData("SEC-011")]
    public async Task Priority_scenarios_pass(string id)
    {
        var result = await RunAsync(ScenarioCatalogue.Find(id)!);
        result.Status.ShouldBe(ScenarioStatus.Pass, result.Actual);
    }
}
