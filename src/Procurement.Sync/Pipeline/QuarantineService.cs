using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

public sealed record RequisitionStatusView(RequisitionRecord Record, IReadOnlyList<StatusHistoryEntry> History, IReadOnlyList<QuarantineEntry> Quarantine);

/// <summary>Operator queries and actions on quarantined records.</summary>
public sealed class QuarantineService(IDbContextFactory<SyncDbContext> dbFactory, TimeProvider time)
{
    public async Task<List<QuarantineEntry>> ListAsync(QuarantineReason? reason, bool includeResolved, CancellationToken ct)
    {
        await dbFactory.EnsureSyncStoreAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Quarantine.AsNoTracking().Where(q => includeResolved || q.ResolvedAt == null);
        if (reason is not null) query = query.Where(q => q.Reason == reason);
        return (await query.ToListAsync(ct)).OrderBy(q => q.Reason).ThenBy(q => q.SourceKey).ToList();
    }

    public async Task<RequisitionStatusView?> GetAsync(string key, CancellationToken ct)
    {
        await dbFactory.EnsureSyncStoreAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var record = await db.Requisitions.AsNoTracking().FirstOrDefaultAsync(r => r.SourceId == key, ct);
        if (record is null) return null;
        var history = (await db.StatusHistory.AsNoTracking().Where(h => h.SourceId == key).ToListAsync(ct)).OrderBy(h => h.Id).ToList();
        var quarantine = (await db.Quarantine.AsNoTracking().Where(q => q.SourceKey == key).ToListAsync(ct)).OrderBy(q => q.Id).ToList();
        return new RequisitionStatusView(record, history, quarantine);
    }

    /// <summary>Closes the open entry after the problem was handled outside the integration (e.g. fixed directly in SAP).</summary>
    public async Task<bool> ResolveAsync(string key, string note, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.Quarantine.FirstOrDefaultAsync(q => q.SourceKey == key && q.ResolvedAt == null, ct);
        if (entry is null) return false;

        var now = time.GetUtcNow().UtcDateTime;
        entry.ResolvedAt = now;
        entry.Resolution = $"Resolved by operator: {note}";
        var record = await db.Requisitions.FindAsync([key], ct);
        if (record is { State: RecordState.Quarantined })
        {
            record.State = RecordState.Dismissed;
            record.LastError = entry.Resolution;
            record.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
