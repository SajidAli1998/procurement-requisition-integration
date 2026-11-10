using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.ScenarioTests.Infrastructure;
using Procurement.SyntheticData;

namespace Procurement.ScenarioTests.Workbook;

/// <summary>The mocks serve the client's workbook and results are compared row by row with its expected-outcome sheets.</summary>
public sealed class WorkbookPipelineTests
{
    private static readonly DataPack Pack = DataPack.Load();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The workbook schedules 14 × 503 at the start of the run; with the harness's low breaker threshold (5) a busy machine
    // can see them cluster and open the breaker, which these tests are not about (S08 covers it).
    private static SyncHarness Harness() => new(new() { ["Sync:PageSize"] = "40", ["Sync:Resilience:BreakerMinimumThroughput"] = "100" }, dataset: "Workbook");

    [Fact(DisplayName = "W01 Every row of Reconciliation_Expected matches the first run's report")]
    public async Task Matches_reconciliation_expected()
    {
        await using var h = Harness();
        var run = await h.RunAsync();

        var actual = run.Report!.Records.ToDictionary(r => r.Key);
        var mismatches = new List<string>();
        foreach (var e in Pack.ReconciliationExpected)
        {
            if (!actual.TryGetValue(e.RequisitionId, out var a)) { mismatches.Add($"{e.RequisitionId}: missing from report"); continue; }
            void Check(string what, object? expected, object? got)
            {
                if (!Equals(expected, got)) mismatches.Add($"{e.RequisitionId} {what}: expected '{expected}', got '{got}' ({a.Explanation}; {a.SapResponses})");
            }
            Check("result", e.ProcessingResult, a.Result);
            Check("remote id", e.RemoteDocumentId, a.SapNumber);
            Check("attempts", e.Attempts, a.Attempts);
            Check("category", e.FailureCategory, a.FailureCategory);
            Check("source version", e.SourceVersion, a.SourceVersion);
        }
        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches));
        actual.Count.ShouldBe(120);
        actual["REQ-7002"].SapResponses.ShouldBe("409,201");
        actual["REQ-7002"].Explanation.ShouldBe("Created after version refresh (v2 → v3) as MOCK-SAP-50002");
        actual["REQ-7003"].SapResponses.ShouldBe("429,429,201");
        actual["REQ-7004"].SapResponses.ShouldBe("503,503,201");
        actual["REQ-7001"].Explanation.ShouldBe("Already in SAP as MOCK-SAP-50001: existing document linked and not created again");
        actual["REQ-7071"].Explanation.ShouldStartWith("Total amount must be greater than zero");
        TestContext.Current.SendDiagnosticMessage(string.Join(Environment.NewLine, run.Report.Records.Take(12).Select(r => $"{r.Key} {r.Result} {r.SapNumber} {r.Attempts} {r.FailureCategory} | {r.Explanation}")));
        run.Report.Invariants.ShouldAllBe(i => i.Outcome != Procurement.Sync.Reporting.InvariantOutcome.Fail);
    }

    [Fact(DisplayName = "W02 Quarantine_Expected: every bad record is quarantined with the matching reason and field")]
    public async Task Matches_quarantine_expected()
    {
        await using var h = Harness();
        await h.RunAsync();
        await using var db = h.Db();
        var open = await db.Quarantine.Where(q => q.ResolvedAt == null).ToDictionaryAsync(q => q.SourceKey, Ct);

        // The workbook's reason codes, and the field it names, mapped to ours.
        var map = new Dictionary<string, (QuarantineReason Reason, string Field)>
        {
            ["AMOUNT_NOT_POSITIVE"] = (QuarantineReason.AmountNotPositive, "totalAmount"),
            ["REQUIRED_DATE_MISSING"] = (QuarantineReason.MissingRequiredField, "neededBy"),
        };
        open.Count.ShouldBe(Pack.QuarantineExpected.Count);
        foreach (var e in Pack.QuarantineExpected)
        {
            var entry = open[e.RequisitionId];
            var (reason, field) = map[e.ReasonCode];
            entry.Reason.ShouldBe(reason, e.RequisitionId);
            var firstField = JsonNode.Parse(entry.Details!)![0]!["field"]!.GetValue<string>();
            firstField.ToLowerInvariant().ShouldBe(field.ToLowerInvariant(), e.RequisitionId);
            entry.RawPayload.ShouldNotBeNull();
        }
    }

    [Fact(DisplayName = "W03 Mock_Remote_Status: SAP holds exactly the expected documents, versions and statuses")]
    public async Task Matches_remote_status()
    {
        await using var h = Harness();
        await h.RunAsync();

        var (total, duplicates) = await h.SapStatsAsync();
        total.ShouldBe(Pack.RemoteStatus.Count);
        duplicates.ShouldBe(0);
        foreach (var e in Pack.RemoteStatus)
        {
            var doc = (await h.SapAdmin.GetFromJsonAsync<JsonObject>($"{SyncHarness.SapBase}A_PurchaseRequisitionHeader('{e.RemoteDocumentId}')", Ct))!;
            doc["ExternalReference"]!.GetValue<string>().ShouldBe(e.RequisitionId);
            doc["ExternalVersion"]!.GetValue<long>().ShouldBe(e.RemoteVersion, e.RequisitionId);
            // The workbook's "Approved" is SAP's released status (02); "Created" is not yet in release.
            doc["ReleaseStatus"]!.GetValue<string>().ShouldBe(e.RemoteStatus == "Approved" ? "02" : "", e.RequisitionId);
        }
    }

    [Fact(DisplayName = "W04 A rerun of the workbook creates nothing and reads three pages of 40")]
    public async Task Rerun_is_idempotent()
    {
        await using var h = Harness();
        var first = await h.RunAsync();
        first.Count(Procurement.Sync.Store.RunEventKind.Extracted).ShouldBe(120);

        var second = await h.RunAsync();
        second.Count(Procurement.Sync.Store.RunEventKind.Created).ShouldBe(0);
        second.Count(Procurement.Sync.Store.RunEventKind.Amended).ShouldBe(0);
        second.Report!.Records.ShouldAllBe(r => r.Result == "Unchanged" || r.Result == "StatusUpdated");
        (await h.SapStatsAsync()).ShouldBe((65, 0));
    }

    [Fact(DisplayName = "W05 When SAP's search lags, its 409 duplicate answer is used: existing id adopted, nothing created")]
    public async Task Duplicate_409_is_adopted()
    {
        await using var h = Harness();
        await h.SapFaultsAsync(new { ReferenceSearchLag = true });
        var run = await h.RunAsync();

        foreach (var id in Pack.Failures.Where(f => f.Mode == "DUPLICATE").Select(f => f.RequisitionId))
        {
            var row = run.Report!.Records.Single(r => r.Key == id);
            row.Result.ShouldBe("Duplicate");
            row.SapResponses.ShouldBe("409");
            row.FailureCategory.ShouldBe("Idempotency");
            row.SapNumber.ShouldBe(Pack.RemoteStatus.Single(s => s.RequisitionId == id).RemoteDocumentId);
        }
        (await h.SapStatsAsync()).ShouldBe((65, 0));
    }

    [Fact(DisplayName = "W06 The workbook under extra chaos (503s, 429s, lost responses) still settles to 65 documents, no duplicates")]
    public async Task Workbook_under_chaos()
    {
        await using var h = Harness();
        await h.SapFaultsAsync(new { TransientErrorRate = 0.1, ThrottleRate = 0.05, LostResponseRate = 0.05, Seed = 11 });
        await h.RunUntilSettledAsync(maxRuns: 6);

        (await h.SapStatsAsync()).ShouldBe((65, 0));
        await using var db = h.Db();
        (await db.Requisitions.CountAsync(r => r.State == Procurement.Sync.Store.RecordState.Synced, Ct)).ShouldBe(65);
        (await db.Requisitions.CountAsync(r => r.State == Procurement.Sync.Store.RecordState.Quarantined, Ct)).ShouldBe(50);
        (await db.Requisitions.CountAsync(r => r.State == Procurement.Sync.Store.RecordState.Ignored, Ct)).ShouldBe(5);
        var linked = await db.Requisitions.Where(r => r.SapNumber != null).ToDictionaryAsync(r => r.SourceId, r => r.SapNumber, Ct);
        foreach (var e in Pack.RemoteStatus) linked[e.RequisitionId].ShouldBe(e.RemoteDocumentId);
    }
}
