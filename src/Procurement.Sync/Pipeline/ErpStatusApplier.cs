using Procurement.Contracts;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

internal static class ErpStatusApplier
{
    /// <summary>
    /// Applies an ERP status if it moves the record forward; stale or duplicate statuses are ignored.
    /// Returns true when the status changed (and therefore needs writing back to the source).
    /// </summary>
    public static bool Apply(SyncDbContext db, RunContext run, RequisitionRecord record, ErpStatus status,
        DateTime changedAt, string? purchaseOrder, DateTime now)
    {
        if (record.ErpStatus is { } current && !current.CanTransitionTo(status))
        {
            if (current != status)
            {
                Ledger.Event(db, run, record.SourceId, RunEventKind.StatusIgnored, $"{status} arrived after {current}; ignored as stale", now);
            }
            return false;
        }

        db.StatusHistory.Add(new StatusHistoryEntry
        {
            SourceId = record.SourceId,
            SapNumber = record.SapNumber!,
            FromStatus = record.ErpStatus,
            ToStatus = status,
            ChangedAt = changedAt,
            RecordedAt = now,
            RunId = run.RunId,
        });
        if (record.ErpStatus is not null)
        {
            Ledger.Event(db, run, record.SourceId, RunEventKind.StatusChanged, $"{record.ErpStatus} → {status}", now);
        }
        record.ErpStatus = status;
        record.ErpStatusAt = changedAt;
        record.PurchaseOrder = purchaseOrder ?? record.PurchaseOrder;
        record.WritebackPending = true;
        return true;
    }
}
