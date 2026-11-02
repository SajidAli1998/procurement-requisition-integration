using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Procurement.Contracts;
using Procurement.ScenarioTests.Infrastructure;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;

namespace Procurement.ScenarioTests.Pipeline;

/// <summary>Status synchronisation, write-back, reconciliation and operator actions.</summary>
public sealed class StatusAndReportScenarioTests
{
    private const int ExpectedInSap = 184;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> SourceErpStatusAsync(SyncHarness h, string id)
    {
        var body = (await h.SourceAdmin.GetFromJsonAsync<JsonObject>($"/api/requisitions/{id}", Ct))!;
        return body["erpSync"]?["status"]?.GetValue<string>() ?? "(none)";
    }

    private static async Task<(string Id, string SapNumber)> FirstSyncedAsync(SyncHarness h)
    {
        await using var db = h.Db();
        var record = await db.Requisitions.Where(r => r.State == RecordState.Synced).OrderBy(r => r.SourceId).FirstAsync(Ct);
        return (record.SourceId, record.SapNumber!);
    }

    [Fact(DisplayName = "S09 Status changes in SAP are synced and written back to the source")]
    public async Task Status_flows_back_to_source()
    {
        await using var h = new SyncHarness();
        var first = await h.RunAsync();
        first.Count(RunEventKind.WrittenBack).ShouldBe(ExpectedInSap, "the initial Created status is written back");
        var (id, _) = await FirstSyncedAsync(h);
        (await SourceErpStatusAsync(h, id)).ShouldBe("Created");

        await h.SapAdmin.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        var second = await h.RunAsync();

        second.Count(RunEventKind.StatusChanged).ShouldBe(ExpectedInSap);
        second.Count(RunEventKind.WrittenBack).ShouldBe(ExpectedInSap);
        (await SourceErpStatusAsync(h, id)).ShouldBe("InRelease");

        await h.SapAdmin.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        await h.SapAdmin.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        await h.RunAsync();

        await using var db = h.Db();
        var record = await db.Requisitions.SingleAsync(r => r.SourceId == id, Ct);
        record.ErpStatus.ShouldBe(ErpStatus.PoCreated);
        record.PurchaseOrder.ShouldNotBeNull().ShouldStartWith("45");
        (await SourceErpStatusAsync(h, id)).ShouldBe("PoCreated");
        (await db.StatusHistory.CountAsync(s => s.SourceId == id, Ct)).ShouldBe(3, "Created → InRelease → PoCreated as observed by polling");
    }

    [Fact(DisplayName = "S16 An out-of-order status from SAP is ignored")]
    public async Task Out_of_order_status_is_ignored()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var (id, sapNumber) = await FirstSyncedAsync(h);
        await h.SapAdmin.PostAsJsonAsync($"/_admin/requisitions/{sapNumber}/lifecycle", new { Lifecycle = "Released" }, Ct);
        await h.RunAsync();

        await h.SapAdmin.PostAsJsonAsync($"/_admin/requisitions/{sapNumber}/lifecycle", new { Lifecycle = "InRelease" }, Ct);
        var result = await h.RunAsync();

        result.Count(RunEventKind.StatusIgnored).ShouldBe(1);
        await using var db = h.Db();
        (await db.Requisitions.SingleAsync(r => r.SourceId == id, Ct)).ErpStatus.ShouldBe(ErpStatus.Released);
        (await SourceErpStatusAsync(h, id)).ShouldBe("Released");
    }

    [Fact(DisplayName = "S18 A write-back version conflict re-reads the source and retries")]
    public async Task Writeback_conflict_retries()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        await h.SapAdmin.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        await h.ScriptSourceAsync(new { kind = "Error", method = "PUT", pathContains = "/erp-status", statusCode = 412, count = 3 });

        var result = await h.RunAsync();

        result.Count(RunEventKind.Conflict).ShouldBe(3);
        result.Count(RunEventKind.WrittenBack).ShouldBe(ExpectedInSap);
        result.Report!.Invariants.Single(i => i.Name.StartsWith("Source shows")).Outcome.ShouldBe(InvariantOutcome.Pass);
    }

    [Fact(DisplayName = "S19 The reconciliation report proves the outcome and explains every failure")]
    public async Task Report_explains_outcome()
    {
        await using var h = new SyncHarness();

        var result = await h.RunAsync();

        var report = result.Report.ShouldNotBeNull();
        report.Invariants.Count.ShouldBe(6);
        report.Invariants.ShouldAllBe(i => i.Outcome == InvariantOutcome.Pass);
        report.Totals.ShouldAllBe(t => t.Matches);
        report.Totals.Sum(t => t.SapCount).ShouldBe(ExpectedInSap);
        report.OpenFailures.Count.ShouldBe(23);
        report.OpenFailures.ShouldAllBe(f => !string.IsNullOrWhiteSpace(f.SuggestedAction) && !string.IsNullOrWhiteSpace(f.Summary));
        foreach (var extension in new[] { "json", "md", "html" })
        {
            File.Exists(Path.Combine(h.ReportDirectory, $"{result.RunId}.{extension}")).ShouldBeTrue();
            File.Exists(Path.Combine(h.ReportDirectory, $"latest.{extension}")).ShouldBeTrue();
        }
        (await File.ReadAllTextAsync(result.ReportPath!, Ct)).ShouldContain("REQ-2026-000045");
    }

    [Fact(DisplayName = "S20 The report detects a duplicate created outside the integration")]
    public async Task Report_detects_external_duplicate()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var (id, _) = await FirstSyncedAsync(h);
        await h.SapAdmin.PostAsJsonAsync(SyncHarness.SapBase + "A_PurchaseRequisitionHeader", new
        {
            ExternalReference = id,
            to_PurchaseReqnItem = new[] { new { RequestedQuantity = 1, BaseUnit = "EA", PurchaseRequisitionPrice = 1, PurReqnItemCurrency = "USD", Plant = "1100", CompanyCode = "1000", DeliveryDate = "2026-12-31" } },
        }, Ct);

        var result = await h.RunAsync();

        var invariants = result.Report!.Invariants.ToDictionary(i => i.Name);
        invariants["No duplicate requisitions in SAP"].Outcome.ShouldBe(InvariantOutcome.Fail);
        invariants["No duplicate requisitions in SAP"].Detail.ShouldContain(id);
        invariants["Every SAP requisition is linked to a source requisition"].Outcome.ShouldBe(InvariantOutcome.Warn);
    }

    [Fact(DisplayName = "S21 A record that exhausted its retries can be replayed by an operator")]
    public async Task Replay_after_retry_exhaustion()
    {
        // One request at a time so all five scripted 503s hit the same create, and a breaker threshold high
        // enough that it exhausts its retries rather than tripping the circuit (that path is S08).
        await using var h = new SyncHarness(new()
        {
            ["Sync:MaxDispatchAttempts"] = "1",
            ["Sync:Parallelism"] = "1",
            ["Sync:Resilience:BreakerMinimumThroughput"] = "100",
        });
        await h.ScriptSapAsync(new { kind = "Error", method = "POST", statusCode = 503, count = 5 });
        var first = await h.RunAsync();

        first.Count(RunEventKind.RetryExhausted).ShouldBe(1);
        string id;
        await using (var db = h.Db())
        {
            id = (await db.Quarantine.SingleAsync(q => q.Reason == QuarantineReason.RetryExhausted, Ct)).SourceKey;
        }

        var replay = await h.Services.GetRequiredService<SyncRunner>().ReplayAsync(id, Ct);

        replay.Count(RunEventKind.QuarantineResolved).ShouldBe(1);
        replay.Count(RunEventKind.Created).ShouldBe(1);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S22 An operator can resolve a quarantine entry handled outside the integration")]
    public async Task Operator_resolves_quarantine()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        string key;
        await using (var db = h.Db())
        {
            key = (await db.Quarantine.FirstAsync(q => q.SourceKey.StartsWith("NOID-"), Ct)).SourceKey;
        }

        var resolved = await h.Services.GetRequiredService<QuarantineService>().ResolveAsync(key, "Raised with source system owner", Ct);
        var result = await h.RunAsync();

        resolved.ShouldBeTrue();
        result.OpenQuarantine.ShouldBe(22);
        result.Report!.Invariants[0].Outcome.ShouldBe(InvariantOutcome.Pass);
        await using var check = h.Db();
        (await check.Requisitions.SingleAsync(r => r.SourceId == key, Ct)).State.ShouldBe(RecordState.Dismissed);
    }
}
