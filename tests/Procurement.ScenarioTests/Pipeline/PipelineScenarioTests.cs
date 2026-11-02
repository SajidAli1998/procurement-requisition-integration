using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Procurement.Contracts;
using Procurement.ScenarioTests.Infrastructure;
using Procurement.Sync.Store;

namespace Procurement.ScenarioTests.Pipeline;

/// <summary>End-to-end scenarios: real sync services against both mocks, numbered as in docs/test-scenarios.md.</summary>
public sealed class PipelineScenarioTests
{
    private const int Approved = 207;
    private const int Malformed = 17;
    private const int SapRejects = 6;
    private const int ExpectedInSap = Approved - Malformed - SapRejects; // 184
    private const int NotApproved = 43;

    private static readonly Dictionary<string, QuarantineReason> ExpectedReason = new()
    {
        ["missing_lines"] = QuarantineReason.MissingLines,
        ["negative_quantity"] = QuarantineReason.InvalidQuantity,
        ["unknown_currency"] = QuarantineReason.UnknownCurrency,
        ["unknown_uom"] = QuarantineReason.UnknownUnitOfMeasure,
        ["past_needed_by"] = QuarantineReason.NeededByInPast,
        ["quantity_not_number"] = QuarantineReason.SchemaInvalid,
        ["total_mismatch"] = QuarantineReason.TotalMismatch,
        ["missing_requester"] = QuarantineReason.MissingRequiredField,
        ["invalid_date"] = QuarantineReason.SchemaInvalid,
        ["plant_company_mismatch"] = QuarantineReason.SapRejectedPermanent,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> FirstCleanApprovedAsync(SyncHarness h) =>
        (await h.ManifestAsync()).First(m => m.Status == "Approved" && m.Defect is null).Key;

    [Fact(DisplayName = "S01 Happy path: every valid approved requisition reaches SAP exactly once")]
    public async Task Happy_path()
    {
        await using var h = new SyncHarness();

        var result = await h.RunAsync();

        result.Status.ShouldBe(RunStatus.Completed);
        result.Count(RunEventKind.Extracted).ShouldBe(250);
        result.Count(RunEventKind.Created).ShouldBe(ExpectedInSap);
        result.Count(RunEventKind.Ignored).ShouldBe(NotApproved);
        result.Count(RunEventKind.RetryScheduled).ShouldBe(0, "permanent SAP rejections must not be retried");
        result.OpenQuarantine.ShouldBe(Malformed + SapRejects);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));

        await using var db = h.Db();
        (await db.Requisitions.CountAsync(r => r.State == RecordState.Synced, Ct)).ShouldBe(ExpectedInSap);
        var quarantine = await db.Quarantine.Where(q => q.ResolvedAt == null).ToListAsync(Ct);
        foreach (var (key, _, defect) in (await h.ManifestAsync()).Where(m => m.Defect is not null))
        {
            if (defect == "missing_id")
            {
                quarantine.ShouldContain(q => q.SourceKey.StartsWith("NOID-") && q.Reason == QuarantineReason.MissingRequiredField);
                continue;
            }
            var entry = quarantine.SingleOrDefault(q => q.SourceKey == key);
            entry.ShouldNotBeNull($"{key} ({defect}) should be quarantined");
            entry.Reason.ShouldBe(ExpectedReason[defect!], $"{key} ({defect})");
            entry.RawPayload.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact(DisplayName = "S02 Immediate rerun creates nothing")]
    public async Task Rerun_creates_nothing()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();

        var second = await h.RunAsync();

        second.Count(RunEventKind.Created).ShouldBe(0);
        second.Count(RunEventKind.Extracted).ShouldBe(0, "the checkpoint cursor means only new changes are read");
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S03 Source fails mid-read: the next run resumes from the checkpoint")]
    public async Task Crash_mid_read_resumes()
    {
        await using var h = new SyncHarness();
        await h.ScriptSourceAsync(new { kind = "Error", method = "GET", pathContains = "/api/requisitions", skip = 3, count = 1000, statusCode = 503 });

        var first = await h.RunAsync();

        first.Status.ShouldBe(RunStatus.CompletedWithIssues);
        first.Message.ShouldNotBeNull().ShouldContain("stopped early");
        first.Count(RunEventKind.Extracted).ShouldBe(150);
        first.Count(RunEventKind.Created).ShouldBeGreaterThan(0, "pages already staged are still dispatched");

        await h.SourceAdmin.DeleteAsync("/_admin/faults", Ct);
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct); // the outage also opened the source circuit breaker
        var second = await h.RunAsync();

        second.Count(RunEventKind.Extracted).ShouldBe(100);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Theory(DisplayName = "S04 Lost responses are resolved by lookup, never duplicated")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_responses_are_resolved(bool sapHonoursIdempotencyKeys)
    {
        await using var h = new SyncHarness();
        await h.SapFaultsAsync(new { HonorIdempotencyKey = sapHonoursIdempotencyKeys });
        await h.ScriptSapAsync(new { kind = "LostResponse", method = "POST", count = 5 });

        var runs = await h.RunUntilSettledAsync();

        runs.Sum(r => r.Count(RunEventKind.InDoubt)).ShouldBe(5);
        runs[^1].Pending.ShouldBe(0);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));

        // Every request whose response was lost ends in SAP exactly once, or (if SAP had rejected it
        // before the connection dropped) is resent on a later run and quarantined. Which 5 requests are
        // hit depends on parallel timing, so assert the guarantee rather than a fixed split.
        await using var db = h.Db();
        var inDoubt = await db.RunEvents.Where(e => e.Kind == RunEventKind.InDoubt).Select(e => e.SourceKey).ToListAsync(Ct);
        foreach (var key in inDoubt)
        {
            var record = await db.Requisitions.SingleAsync(r => r.SourceId == key, Ct);
            if (record.State == RecordState.Synced) continue;
            record.State.ShouldBe(RecordState.Quarantined);
            (await db.Quarantine.SingleAsync(q => q.SourceKey == key, Ct)).Reason.ShouldBe(QuarantineReason.SapRejectedPermanent);
        }
        runs.Sum(r => r.Count(RunEventKind.InDoubtResolved)).ShouldBe(
            inDoubt.Count(k => db.Requisitions.Single(r => r.SourceId == k).State == RecordState.Synced));
    }

    [Fact(DisplayName = "S05 Local state lost: rerun adopts what SAP already has")]
    public async Task Local_state_loss_adopts_existing()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        await h.WipeLocalStateAsync();

        var result = await h.RunAsync();

        result.Count(RunEventKind.Created).ShouldBe(0);
        result.Count(RunEventKind.AlreadyInSap).ShouldBe(ExpectedInSap);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S06 Throttling: 429s are absorbed and Retry-After is honoured")]
    public async Task Throttling_is_absorbed()
    {
        await using var h = new SyncHarness();
        await h.ScriptSapAsync(new { kind = "Throttle", method = "POST", count = 8, retryAfterSeconds = 2 });

        var runs = await h.RunUntilSettledAsync();

        runs[0].Transport.GetValueOrDefault("sap.throttled").ShouldBeGreaterThan(0);
        runs[0].RetryWait.ShouldBeGreaterThan(TimeSpan.Zero);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S07 Random transient failures are retried until everything is synced")]
    public async Task Random_transient_failures()
    {
        await using var h = new SyncHarness();
        await h.SapFaultsAsync(new { TransientErrorRate = 0.2, Seed = 7 });

        var runs = await h.RunUntilSettledAsync(maxRuns: 8);

        runs.Sum(r => r.Transport.GetValueOrDefault("sap.retries")).ShouldBeGreaterThan(0);
        runs[^1].Pending.ShouldBe(0);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S08 SAP outage opens the breaker; the next run completes")]
    public async Task Sap_outage_opens_breaker()
    {
        await using var h = new SyncHarness();
        await h.ScriptSapAsync(new { kind = "Error", statusCode = 503, count = 100_000 });

        var first = await h.RunAsync();

        first.Status.ShouldBe(RunStatus.CompletedWithIssues);
        first.Message.ShouldNotBeNull().ShouldContain("circuit breaker");
        first.Transport.GetValueOrDefault("sap.circuitOpened").ShouldBeGreaterThan(0);
        first.Pending.ShouldBe(ExpectedInSap + SapRejects);
        (await h.SapStatsAsync()).Total.ShouldBe(0);

        await h.ClearSapFaultsAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.2), Ct); // let the breaker half-open
        var runs = await h.RunUntilSettledAsync();

        runs[^1].Pending.ShouldBe(0);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S10 Fixing a quarantined record in the source releases it")]
    public async Task Fixed_record_is_released_from_quarantine()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var badCurrency = (await h.ManifestAsync()).First(m => m.Defect == "unknown_currency").Key;

        await h.EditSourceAsync(badCurrency, new { currency = "USD" });
        var result = await h.RunAsync();

        result.Count(RunEventKind.QuarantineResolved).ShouldBe(1);
        result.Count(RunEventKind.Created).ShouldBe(1);
        result.OpenQuarantine.ShouldBe(Malformed + SapRejects - 1);
        await using var db = h.Db();
        (await db.Quarantine.SingleAsync(q => q.SourceKey == badCurrency, Ct)).ResolvedAt.ShouldNotBeNull();
    }

    [Fact(DisplayName = "S11 Source amendment of an open requisition updates SAP in place")]
    public async Task Amendment_updates_sap()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var id = await FirstCleanApprovedAsync(h);

        await h.EditSourceAsync(id, new { justification = "Scope increased" });
        var result = await h.RunAsync();

        result.Count(RunEventKind.Amended).ShouldBe(1);
        result.Count(RunEventKind.Created).ShouldBe(0);
        await using var db = h.Db();
        var record = await db.Requisitions.SingleAsync(r => r.SourceId == id, Ct);
        record.SyncedSourceVersion.ShouldBe(record.SourceVersion);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S12 Overlapping pages do not cause double processing")]
    public async Task Overlapping_pages()
    {
        await using var h = new SyncHarness();
        await h.SourceFaultsAsync(new { pageOverlap = true });

        var result = await h.RunAsync();

        result.Count(RunEventKind.Unchanged).ShouldBe(4, "pages 2-5 each repeat one record");
        result.Count(RunEventKind.Created).ShouldBe(ExpectedInSap);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S13 Amendment after SAP release is quarantined as a version conflict")]
    public async Task Amendment_after_release_conflicts()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var id = await FirstCleanApprovedAsync(h);
        string sapNumber;
        await using (var db = h.Db()) sapNumber = (await db.Requisitions.SingleAsync(r => r.SourceId == id, Ct)).SapNumber!;
        await h.SapAdmin.PostAsJsonAsync($"/_admin/requisitions/{sapNumber}/lifecycle", new { Lifecycle = "Released" }, Ct);

        await h.EditSourceAsync(id, new { justification = "Too late to change" });
        var result = await h.RunAsync();

        result.Count(RunEventKind.Conflict).ShouldBeGreaterThan(0);
        await using var check = h.Db();
        var entry = await check.Quarantine.SingleAsync(q => q.SourceKey == id && q.ResolvedAt == null, Ct);
        entry.Reason.ShouldBe(QuarantineReason.VersionConflictUnresolved);
        var record = await check.Requisitions.SingleAsync(r => r.SourceId == id, Ct);
        record.State.ShouldBe(RecordState.Synced, "SAP still holds the earlier, valid version");
        record.ErpStatus.ShouldBe(ErpStatus.Released);
    }

    [Fact(DisplayName = "S14 Cancellation after transfer is flagged for review")]
    public async Task Cancellation_after_transfer()
    {
        await using var h = new SyncHarness();
        await h.RunAsync();
        var id = await FirstCleanApprovedAsync(h);

        await h.EditSourceAsync(id, new { status = "Cancelled" });
        var result = await h.RunAsync();

        result.Count(RunEventKind.SourceChangedAfterTransfer).ShouldBe(1);
        await using var db = h.Db();
        (await db.Quarantine.SingleAsync(q => q.SourceKey == id && q.ResolvedAt == null, Ct)).Reason
            .ShouldBe(QuarantineReason.SourceChangedAfterTransfer);
    }

    [Fact(DisplayName = "S15 A timed-out create is re-checked before anything is resent")]
    public async Task Timed_out_create_is_rechecked()
    {
        await using var h = new SyncHarness();
        await h.ScriptSapAsync(new { kind = "Delay", method = "POST", delayMs = 3000, count = 1 });

        var first = await h.RunAsync();

        first.Count(RunEventKind.InDoubt).ShouldBe(1);
        first.Pending.ShouldBe(1, "the timed-out request was not found on re-check, so it waits instead of being resent");

        var second = await h.RunAsync();

        second.Pending.ShouldBe(0);
        (first.Count(RunEventKind.Created) + second.Count(RunEventKind.Created)).ShouldBe(ExpectedInSap);
        (await h.SapStatsAsync()).ShouldBe((ExpectedInSap, 0));
    }

    [Fact(DisplayName = "S17 A second runner is skipped while the lease is held")]
    public async Task Concurrent_run_is_skipped()
    {
        await using var h = new SyncHarness();
        await h.RunAsync(); // creates the schema
        var leases = h.Services.GetRequiredService<LeaseManager>();
        (await leases.TryAcquireAsync("procsync", "other-runner", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();

        var result = await h.RunAsync();

        result.Skipped.ShouldBeTrue();
    }
}
