using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;

namespace Procurement.ScenarioTests.Properties;

/// <summary>
/// Every requisition ends with one SAP document holding its latest valid content, over many seeded random cases.
/// A failure names its seed so the case can be replayed.
/// </summary>
public sealed class PropertyTests
{
    private const int Cases = 12;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task CheckAsync(Func<int, Task> property)
    {
        var failures = new List<string>();
        for (var seed = 1; seed <= Cases; seed++)
        {
            try { await property(seed); }
            catch (Exception ex) when (ex is ShouldAssertException or InvalidOperationException) { failures.Add($"seed {seed}: {ex.Message.Split('\n')[0]}"); }
        }
        failures.ShouldBeEmpty("Replay a failing case with its seed:\n" + string.Join("\n", failures));
    }

    private static async Task<List<string>> SeedRecordsAsync(LabEnvironment lab, Random random, string prefix)
    {
        var ids = Enumerable.Range(1, random.Next(3, 12)).Select(i => $"{prefix}-{i:D2}").ToList();
        foreach (var id in ids) await lab.PutSourceAsync(id, Req.Valid(id, lines: random.Next(1, 4), quantity: random.Next(1, 9), price: random.Next(1, 500)));
        return ids;
    }

    private static async Task AssertOnePerIdAsync(LabEnvironment lab, IEnumerable<string> ids, int seed)
    {
        var docs = await lab.SapDocumentsAsync();
        foreach (var id in ids)
            docs.Count(d => d["ExternalReference"]!.GetValue<string>() == id).ShouldBe(1, $"seed {seed}: {id}");
        (await lab.SapStatsAsync()).DuplicateReferences.ShouldBe(0, $"seed {seed}");
    }

    [Fact(DisplayName = "P1 Repeating input any number of times never duplicates")]
    public Task Duplicate_input_repetition() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment(new LabOptions { Sync = new() { ["PageSize"] = random.Next(1, 6).ToString() } });
        var ids = await SeedRecordsAsync(lab, random, "P1");
        await lab.SourceFaultsAsync(new FaultSettings { PageOverlap = random.Next(2) == 0 });
        for (var run = 0; run < random.Next(2, 5); run++)
        {
            foreach (var id in ids.Where(_ => random.Next(3) == 0)) await lab.EditSourceAsync(id, new { status = "Approved" });
            await lab.RunAsync("p1", Ct);
        }
        await AssertOnePerIdAsync(lab, ids, seed);
    });

    [Fact(DisplayName = "P2 The order records arrive in does not change the result")]
    public Task Request_ordering() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment(new LabOptions { Sync = new() { ["PageSize"] = "3" } });
        var ids = Enumerable.Range(1, 8).Select(i => $"P2-{i:D2}").OrderBy(_ => random.Next()).ToList();
        foreach (var id in ids) await lab.PutSourceAsync(id, Req.Valid(id));
        await lab.RunUntilSettledAsync(ct: Ct);
        await AssertOnePerIdAsync(lab, ids, seed);
    });

    [Fact(DisplayName = "P3 Random SAP failures settle to exactly one document each")]
    public Task Random_failures() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment(new LabOptions { Sync = new() { ["MaxDispatchAttempts"] = "50", ["Resilience:BreakerMinimumThroughput"] = "1000" } });
        var ids = await SeedRecordsAsync(lab, random, "P3");
        await lab.SapFaultsAsync(new FaultSettings
        {
            TransientErrorRate = random.NextDouble() * 0.4, ThrottleRate = random.NextDouble() * 0.2, LostResponseRate = random.NextDouble() * 0.3,
            HonorIdempotencyKey = random.Next(2) == 0, Seed = seed,
        });
        await lab.RunUntilSettledAsync(12, Ct);
        await lab.SapFaultsAsync(new FaultSettings());
        await lab.RunUntilSettledAsync(ct: Ct);
        await AssertOnePerIdAsync(lab, ids, seed);
    });

    [Fact(DisplayName = "P4 Random mutations between runs: SAP ends with the latest valid content")]
    public Task Random_mutations() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment();
        var ids = await SeedRecordsAsync(lab, random, "P4");
        var expected = new Dictionary<string, decimal>();
        for (var round = 0; round < 3; round++)
        {
            foreach (var id in ids.Where(_ => random.Next(2) == 0))
            {
                var qty = random.Next(1, 20);
                var v = Req.Valid(id, quantity: qty);
                await lab.EditSourceAsync(id, new { lines = v["lines"], totalAmount = v["totalAmount"] });
                expected[id] = qty;
            }
            await lab.RunUntilSettledAsync(ct: Ct);
        }
        await AssertOnePerIdAsync(lab, ids, seed);
        foreach (var (id, qty) in expected)
        {
            var doc = (await lab.SapDocumentsForAsync(id)).Single();
            doc["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>().ShouldBe(qty, $"seed {seed}: {id}");
        }
    });

    [Fact(DisplayName = "P5 A crash at a random checkpoint or SAP response is always recovered")]
    public Task Random_interruption() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment(new LabOptions { Sync = new() { ["PageSize"] = random.Next(1, 4).ToString() } });
        var ids = await SeedRecordsAsync(lab, random, "P5");
        var target = random.Next(1, 4);
        if (random.Next(2) == 0)
        {
            var saves = 0;
            lab.DbFaults.Add("random checkpoint crash", db => DbFaults.SavesCheckpoint(db) && ++saves == target, DbFaultKind.Crash);
        }
        else
        {
            var creates = 0;
            lab.Crash.ArmAfterSapResponse("random SAP response", (rq, _) => LabHooks.IsSapCreate(rq) && Interlocked.Increment(ref creates) == target);
        }
        await lab.RunAsync("may crash", Ct);
        await lab.RestartAsync();
        await lab.RunUntilSettledAsync(ct: Ct);
        await using var db = lab.Db();
        (await db.Requisitions.CountAsync(r => r.State == RecordState.Sending, Ct)).ShouldBe(0, $"seed {seed}");
        await AssertOnePerIdAsync(lab, ids, seed);
    });

    [Fact(DisplayName = "P6 Any number of repeated replays creates one document")]
    public Task Repeated_retries() => CheckAsync(async seed =>
    {
        var random = new Random(seed);
        await using var lab = new LabEnvironment();
        await lab.PutSourceAsync("P6-01", Req.Valid("P6-01"));
        await lab.RunAsync("first", Ct);
        for (var i = 0; i < random.Next(1, 8); i++) await lab.Worker.ReplayAsync("P6-01", Ct);
        await AssertOnePerIdAsync(lab, ["P6-01"], seed);
    });
}
