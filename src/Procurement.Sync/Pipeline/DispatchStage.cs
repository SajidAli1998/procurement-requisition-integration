using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Procurement.Contracts;
using Procurement.Sync.Clients;
using Procurement.Sync.Mapping;
using Procurement.Sync.Store;

namespace Procurement.Sync.Pipeline;

public sealed record DispatchSummary(int Attempted, bool CircuitOpen);

/// <summary>
/// Drains the outbox into SAP. Duplicate protection, in order:
/// 1) local mapping (a record with a SAP number is amended, never created again);
/// 2) a deterministic Idempotency-Key per (requisition, version);
/// 3) a lookup by ExternalReference before every create, which also works against a real SAP that ignores keys.
/// </summary>
public sealed class DispatchStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SapClient sap,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<DispatchStage> logger)
{
    private enum Flow { Continue, Stop }

    private sealed record Item(SyncDbContext Db, RunContext Run, RequisitionRecord Record, OutboxMessage Message, Requisition Requisition, bool WasInDoubt);

    private SyncOptions Options => options.Value;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<DispatchSummary> RunAsync(RunContext run, CancellationToken ct)
    {
        List<long> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var now = Now;
            var pending = (await db.Outbox.Where(m => m.ProcessedAt == null).ToListAsync(ct))
                .Where(m => m.NextAttemptAt is null || m.NextAttemptAt <= now)
                .ToList();

            // Only the newest version of each requisition is sent; older staged versions are superseded.
            var latest = pending.GroupBy(m => m.SourceId)
                .Select(g => g.OrderByDescending(m => m.SourceVersion).ThenByDescending(m => m.Id).First())
                .ToList();
            foreach (var stale in pending.Except(latest))
            {
                stale.ProcessedAt = now;
                stale.LastError = "Superseded by a newer version";
            }
            await db.SaveChangesAsync(ct);
            due = latest.OrderBy(m => m.Id).Select(m => m.Id).ToList();
        }

        var stop = 0;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Options.Parallelism, CancellationToken = ct },
            async (id, token) =>
            {
                // After the breaker opens, leave the rest queued rather than cancelling requests already in flight.
                if (Volatile.Read(ref stop) == 1) return;
                if (await ProcessAsync(id, run, token) == Flow.Stop) Interlocked.Exchange(ref stop, 1);
            });

        if (stop == 1) logger.LogWarning("SAP circuit breaker is open; remaining outbox messages stay queued");
        return new DispatchSummary(due.Count, stop == 1);
    }

    private async Task<Flow> ProcessAsync(long messageId, RunContext run, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var message = await db.Outbox.FindAsync([messageId], ct);
        if (message is null || message.ProcessedAt is not null) return Flow.Continue;
        var record = await db.Requisitions.FindAsync([message.SourceId], ct);
        if (record is null) return Flow.Continue;
        var requisition = JsonSerializer.Deserialize<Requisition>(message.Payload, ExtractStage.PayloadJson)!;

        // Persist "Sending" before calling SAP: if the process dies mid-call, the next run treats it as in doubt.
        var wasInDoubt = record.State == RecordState.InDoubt;
        record.State = RecordState.Sending;
        record.Attempts++;
        message.Attempts++;
        record.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);

        var item = new Item(db, run, record, message, requisition, wasInDoubt);
        // Layer 1: a requisition already linked to a SAP number is only ever amended.
        var flow = record.SapNumber is null ? await CreateAsync(item, ct) : await AmendAsync(item, null, ct);
        await db.SaveChangesAsync(ct);
        return flow;
    }

    private async Task<Flow> CreateAsync(Item item, CancellationToken ct)
    {
        var req = item.Requisition;

        // Layer 3: look before creating. Cheap insurance against lost responses and lost local state.
        var lookup = await sap.FindByExternalReferenceAsync(req.Id, ct);
        if (!lookup.IsSuccess) return await DeferAsync(item, lookup.Status, $"lookup failed: {lookup.Error}", ct);
        if (lookup.Value!.Count > 0) return await AdoptExistingAsync(item, lookup.Value, ct);

        // Layer 2: deterministic Idempotency-Key, so a retried create is replayed, not duplicated.
        var created = await sap.CreateAsync(SapMapper.ToSap(req), Fingerprints.CreateKey(req.Id, req.Version), ct);
        switch (created.Status)
        {
            case SapCallStatus.Created:
                Adopt(item, created.Value!, created.ETag, RunEventKind.Created, $"SAP PR {created.Value!.PurchaseRequisition}");
                return Flow.Continue;

            case SapCallStatus.Replayed:
                Adopt(item, created.Value!, created.ETag, RunEventKind.AlreadyInSap, $"SAP PR {created.Value!.PurchaseRequisition} (idempotent replay)");
                return Flow.Continue;

            case SapCallStatus.Rejected:
                await QuarantineAsync(item, QuarantineReason.SapRejectedPermanent, $"SAP rejected the requisition: {created.Error}", ct);
                return Flow.Continue;

            case SapCallStatus.Ambiguous:
                Ledger.Event(item.Db, item.Run, req.Id, RunEventKind.InDoubt, created.Error, Now);
                var recheck = await sap.FindByExternalReferenceAsync(req.Id, ct);
                if (recheck.IsSuccess && recheck.Value!.Count > 0)
                {
                    var found = recheck.Value.OrderBy(p => p.PurchaseRequisition, StringComparer.Ordinal).First();
                    Adopt(item, found, found.ETag, RunEventKind.InDoubtResolved, $"Response was lost; SAP PR {found.PurchaseRequisition} found on re-check");
                    return Flow.Continue;
                }
                // Not visible (yet). Wait before checking again rather than risk a second create.
                item.Record.State = RecordState.InDoubt;
                item.Record.LastError = created.Error;
                item.Record.NextAttemptAt = item.Message.NextAttemptAt = Now + Options.InDoubtRecheckDelay;
                return Flow.Continue;

            default:
                return await DeferAsync(item, created.Status, created.Error, ct);
        }
    }

    private async Task<Flow> AdoptExistingAsync(Item item, IReadOnlyList<SapRequisitionDto> existing, CancellationToken ct)
    {
        var ordered = existing.OrderBy(p => p.PurchaseRequisition, StringComparer.Ordinal).ToList();
        var pr = ordered[0];
        if (ordered.Count > 1)
        {
            Ledger.Event(item.Db, item.Run, item.Record.SourceId, RunEventKind.Conflict,
                $"SAP already holds {ordered.Count} requisitions for this reference ({string.Join(", ", ordered.Select(p => p.PurchaseRequisition))}); using {pr.PurchaseRequisition}", Now);
        }

        if (pr.ExternalVersion >= item.Requisition.Version)
        {
            var kind = item.WasInDoubt ? RunEventKind.InDoubtResolved : RunEventKind.AlreadyInSap;
            Adopt(item, pr, pr.ETag, kind, $"SAP PR {pr.PurchaseRequisition} already holds v{pr.ExternalVersion}");
            return Flow.Continue;
        }

        // SAP has an older version of this requisition: link to it and amend instead of creating.
        item.Record.SapNumber = pr.PurchaseRequisition;
        item.Record.SyncedSourceVersion = pr.ExternalVersion;
        return await AmendAsync(item, pr, ct);
    }

    private async Task<Flow> AmendAsync(Item item, SapRequisitionDto? current, CancellationToken ct)
    {
        var record = item.Record;
        var req = item.Requisition;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (current?.ETag is null)
            {
                var fetched = await sap.GetAsync(record.SapNumber!, ct);
                if (fetched.Status == SapCallStatus.NotFound)
                {
                    await QuarantineAsync(item, QuarantineReason.VersionConflictUnresolved, $"SAP PR {record.SapNumber} no longer exists", ct);
                    return Flow.Continue;
                }
                if (!fetched.IsSuccess) return await DeferAsync(item, fetched.Status, fetched.Error, ct);
                current = fetched.Value! with { ETag = fetched.ETag ?? fetched.Value!.ETag };
            }

            if (current.ExternalVersion >= req.Version)
            {
                Adopt(item, current, current.ETag, RunEventKind.AlreadyInSap, $"SAP PR {current.PurchaseRequisition} already holds v{current.ExternalVersion}");
                return Flow.Continue;
            }

            var status = SapMapper.ToErpStatus(current);
            if (!SapMapper.IsAmendable(status))
            {
                ErpStatusApplier.Apply(item.Db, item.Run, record, status, current.LastChangeDateTime, SapMapper.PurchaseOrderOf(current), Now);
                Ledger.Event(item.Db, item.Run, record.SourceId, RunEventKind.Conflict, $"Amendment v{req.Version} refused: SAP PR is {status}", Now);
                await QuarantineAsync(item, QuarantineReason.VersionConflictUnresolved,
                    $"Source v{req.Version} changed after SAP PR {record.SapNumber} reached {status}; SAP no longer accepts content changes. " +
                    "Apply the change in SAP manually or raise a new requisition.", ct);
                return Flow.Continue;
            }

            var patched = await sap.PatchAsync(record.SapNumber!, current.ETag!, SapMapper.ToPatch(req), ct);
            switch (patched.Status)
            {
                case SapCallStatus.Ok:
                    Adopt(item, current with { ExternalVersion = req.Version }, patched.ETag, RunEventKind.Amended,
                        $"SAP PR {record.SapNumber} updated to v{req.Version}");
                    return Flow.Continue;

                case SapCallStatus.PreconditionFailed:
                case SapCallStatus.Ambiguous:
                    // Someone changed it in SAP, or we don't know if our PATCH landed: re-read and decide again.
                    Ledger.Event(item.Db, item.Run, record.SourceId, RunEventKind.Conflict,
                        patched.Status == SapCallStatus.PreconditionFailed ? "ETag changed in SAP; re-reading" : "PATCH outcome unknown; re-reading", Now);
                    current = null;
                    continue;

                case SapCallStatus.Locked:
                    await QuarantineAsync(item, QuarantineReason.VersionConflictUnresolved, $"SAP refused the change: {patched.Error}", ct);
                    return Flow.Continue;

                case SapCallStatus.Rejected:
                    await QuarantineAsync(item, QuarantineReason.SapRejectedPermanent, $"SAP rejected the amendment: {patched.Error}", ct);
                    return Flow.Continue;

                default:
                    return await DeferAsync(item, patched.Status, patched.Error, ct);
            }
        }
        return await DeferAsync(item, SapCallStatus.Transient, "SAP requisition kept changing during the update", ct);
    }

    private void Adopt(Item item, SapRequisitionDto pr, string? etag, RunEventKind kind, string detail)
    {
        var record = item.Record;
        var now = Now;
        record.SapNumber = pr.PurchaseRequisition;
        record.SapETag = etag ?? pr.ETag;
        record.SyncedSourceVersion = pr.ExternalVersion == 0 ? item.Requisition.Version : pr.ExternalVersion;
        ErpStatusApplier.Apply(item.Db, item.Run, record, SapMapper.ToErpStatus(pr), pr.LastChangeDateTime, SapMapper.PurchaseOrderOf(pr), now);
        record.State = RecordState.Synced;
        record.Attempts = 0;
        record.NextAttemptAt = null;
        record.LastError = null;
        item.Message.ProcessedAt = now;
        item.Message.LastError = null;
        Ledger.Event(item.Db, item.Run, record.SourceId, kind, detail, now);
    }

    private async Task QuarantineAsync(Item item, QuarantineReason reason, string summary, CancellationToken ct)
    {
        var record = item.Record;
        var now = Now;
        await Ledger.QuarantineAsync(item.Db, item.Run, record.SourceId, item.Message.SourceVersion, PipelineStage.Load,
            reason, summary, item.Message.Payload, now, ct);
        record.State = record.SapNumber is null ? RecordState.Quarantined : RecordState.Synced;
        record.NextAttemptAt = null;
        record.LastError = summary;
        item.Message.ProcessedAt = now;
        item.Message.LastError = summary;
    }

    private async Task<Flow> DeferAsync(Item item, SapCallStatus status, string? error, CancellationToken ct)
    {
        var record = item.Record;
        var now = Now;
        var keepState = item.WasInDoubt ? RecordState.InDoubt : RecordState.RetryPending;

        if (status == SapCallStatus.CircuitOpen)
        {
            // The call never reached SAP, so it does not count as an attempt.
            record.Attempts--;
            item.Message.Attempts--;
            record.State = keepState;
            record.LastError = "SAP circuit breaker open";
            Ledger.Event(item.Db, item.Run, record.SourceId, RunEventKind.RetryScheduled, "circuit breaker open", now);
            return Flow.Stop;
        }

        if (record.Attempts >= Options.MaxDispatchAttempts)
        {
            Ledger.Event(item.Db, item.Run, record.SourceId, RunEventKind.RetryExhausted, $"{status}: {error}", now);
            await QuarantineAsync(item, QuarantineReason.RetryExhausted, $"Gave up after {record.Attempts} attempts; last error {status}: {error}", ct);
            return Flow.Continue;
        }

        var delay = Options.RetryPendingBaseDelay * Math.Pow(2, Math.Max(0, record.Attempts - 1));
        record.State = keepState;
        record.LastError = $"{status}: {error}";
        record.NextAttemptAt = item.Message.NextAttemptAt = now + delay;
        item.Message.LastError = record.LastError;
        Ledger.Event(item.Db, item.Run, record.SourceId, RunEventKind.RetryScheduled, $"{status}: {error}", now);
        return Flow.Continue;
    }
}
