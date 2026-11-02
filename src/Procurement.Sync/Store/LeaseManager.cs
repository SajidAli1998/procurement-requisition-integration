using Microsoft.EntityFrameworkCore;

namespace Procurement.Sync.Store;

/// <summary>
/// Row-based lease that works on SQLite and SQL Server alike: a conditional UPDATE takes the lease
/// only if it is free or expired, so two overlapping runs cannot both hold it.
/// </summary>
public sealed class LeaseManager(IDbContextFactory<SyncDbContext> dbFactory, TimeProvider time)
{
    public async Task<bool> TryAcquireAsync(string name, string holder, TimeSpan duration, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Leases.AnyAsync(l => l.Name == name, ct))
        {
            db.Leases.Add(new Lease { Name = name, Holder = holder, ExpiresAt = now + duration });
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                // Another runner inserted it first; fall through to the conditional update.
                db.ChangeTracker.Clear();
            }
        }

        var taken = await db.Leases
            .Where(l => l.Name == name && (l.ExpiresAt < now || l.Holder == holder))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Holder, holder).SetProperty(l => l.ExpiresAt, now + duration), ct);
        return taken == 1;
    }

    public async Task ReleaseAsync(string name, string holder, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Leases.Where(l => l.Name == name && l.Holder == holder).ExecuteDeleteAsync(ct);
    }
}
