using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Procurement.Contracts;
using Procurement.Sync.Store;

namespace Procurement.Sync.UnitTests.Store;

public sealed class SyncDbContextTests : IAsyncLifetime
{
    private readonly SqliteTestDatabase _db = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enums_are_stored_as_text_and_datetimes_come_back_as_utc()
    {
        var seen = new DateTime(2026, 11, 2, 9, 30, 0, DateTimeKind.Utc);
        await using (var db = _db.CreateDbContext())
        {
            db.Requisitions.Add(new RequisitionRecord
            {
                SourceId = "REQ-1", State = RecordState.Synced, ErpStatus = ErpStatus.Released, FirstSeenAt = seen, UpdatedAt = seen,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT State, ErpStatus FROM Requisitions";
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetString(0).ShouldBe("Synced");
            reader.GetString(1).ShouldBe("Released");
        }

        await using var read = _db.CreateDbContext();
        var record = await read.Requisitions.SingleAsync(Ct);
        record.FirstSeenAt.Kind.ShouldBe(DateTimeKind.Utc);
        record.FirstSeenAt.ShouldBe(seen);
    }

    [Fact]
    public async Task Concurrent_updates_to_the_same_record_are_detected()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.Requisitions.Add(new RequisitionRecord { SourceId = "REQ-2", State = RecordState.Queued });
            await db.SaveChangesAsync(Ct);
        }

        await using var first = _db.CreateDbContext();
        await using var second = _db.CreateDbContext();
        var a = await first.Requisitions.SingleAsync(Ct);
        var b = await second.Requisitions.SingleAsync(Ct);

        a.State = RecordState.Sending;
        await first.SaveChangesAsync(Ct);

        b.State = RecordState.Sending;
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Staging_the_same_outbox_message_twice_violates_the_unique_index()
    {
        await using var db = _db.CreateDbContext();
        db.Outbox.Add(new OutboxMessage { SourceId = "REQ-3", SourceVersion = 1, Kind = OutboxKind.Create, Payload = "{}" });
        await db.SaveChangesAsync(Ct);

        db.Outbox.Add(new OutboxMessage { SourceId = "REQ-3", SourceVersion = 1, Kind = OutboxKind.Create, Payload = "{}" });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Lease_is_exclusive_until_released_or_expired()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var leases = new LeaseManager(_db, clock);

        (await leases.TryAcquireAsync("sync", "runner-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        (await leases.TryAcquireAsync("sync", "runner-b", TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
        (await leases.TryAcquireAsync("sync", "runner-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue("the holder may renew");

        clock.Advance(TimeSpan.FromMinutes(6));
        (await leases.TryAcquireAsync("sync", "runner-b", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue("an expired lease can be taken over");

        await leases.ReleaseAsync("sync", "runner-b", Ct);
        (await leases.TryAcquireAsync("sync", "runner-a", TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
    }
}
