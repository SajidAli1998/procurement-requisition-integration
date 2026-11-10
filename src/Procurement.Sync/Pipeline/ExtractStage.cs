using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Procurement.Contracts;
using Procurement.Sync.Clients;
using Procurement.Sync.Mapping;
using Procurement.Sync.Store;
using Procurement.Sync.Validation;

namespace Procurement.Sync.Pipeline;

// Each page and its checkpoint commit in one transaction, so a crash resumes from the last completed page.
public sealed class ExtractStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SourceClient source,
    RequisitionValidator validator,
    SyncControl control,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<ExtractStage> logger)
{
    public const string Stream = "source-requisitions";

    public const string FullScanStream = "source-full-scan";
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(RunContext run, CancellationToken ct)
    {
        string? cursor;
        var fullScan = false;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            cursor = (await db.Checkpoints.FindAsync([Stream], ct))?.Cursor;
            // A record the source left out of an earlier page (a lagging index, a skipped page) stays behind the cursor
            // for ever; only a read from the start finds it. Re-reads are harmless.
            var lastFull = (await db.Checkpoints.FindAsync([FullScanStream], ct))?.UpdatedAt;
            if (cursor is not null && options.Value.FullScanInterval is { } every
                && (lastFull is null || time.GetUtcNow().UtcDateTime - lastFull.Value >= every))
            {
                logger.FullScanStarted();
                cursor = null;
                fullScan = true;
            }
        }

        fullScan |= cursor is null;
        var pages = 0;
        var resetOnce = false;
        while (true)
        {
            await control.WaitWhilePausedAsync(ct);
            SourcePage page;
            try
            {
                page = await source.GetChangesAsync(cursor, options.Value.PageSize, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest && cursor is not null && !resetOnce)
            {
                // A corrupt or expired cursor: start over. Records already processed are recognised as unchanged,
                // so nothing is sent twice.
                logger.CursorRejected(ex);
                await using (var resetDb = await dbFactory.CreateDbContextAsync(ct))
                {
                    Ledger.Event(resetDb, run, Stream, RunEventKind.Conflict, "Saved cursor rejected by the source (corrupt or expired); re-reading from the start", time.GetUtcNow().UtcDateTime);
                    await resetDb.SaveChangesAsync(ct);
                }
                cursor = null;
                resetOnce = true;
                fullScan = true;
                continue;
            }
            pages++;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var seenInPage = new HashSet<(string, long)>();
            foreach (var item in page.Items)
            {
                await ProcessAsync(db, run, item, seenInPage, force: false, ct);
            }

            var checkpoint = await db.Checkpoints.FindAsync([Stream], ct);
            if (checkpoint is null)
            {
                checkpoint = new Checkpoint { Stream = Stream };
                db.Checkpoints.Add(checkpoint);
            }
            checkpoint.Cursor = page.NextCursor ?? cursor;
            checkpoint.RunId = run.RunId;
            checkpoint.UpdatedAt = time.GetUtcNow().UtcDateTime;
            // Recorded when the scan begins, so an interrupted scan is finished from the cursor next run.
            if (pages == 1 && fullScan)
            {
                var marker = await db.Checkpoints.FindAsync([FullScanStream], ct);
                if (marker is null)
                {
                    marker = new Checkpoint { Stream = FullScanStream };
                    db.Checkpoints.Add(marker);
                }
                marker.RunId = run.RunId;
                marker.UpdatedAt = time.GetUtcNow().UtcDateTime;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.PageExtracted(pages, page.Items.Count);
            cursor = checkpoint.Cursor;
            if (!page.HasMore) return pages;
        }
    }

    // Operator replay: runs even if this version was seen before, for example after the SAP configuration was fixed.
    public async Task ReprocessAsync(RunContext run, string id, CancellationToken ct)
    {
        var fetched = await source.GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Requisition {id} was not found in the source system");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ProcessAsync(db, run, fetched.Body, [], force: true, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // A version already processed came back with different content. Treating it as a repeat would silently drop
    // the change (or send whichever copy arrived first), so it goes to quarantine for the source owner.
    private static async Task<bool> QuarantineIfConflictingAsync(SyncDbContext db, RunContext run, RequisitionRecord record, JsonElement item,
        SourceEnvelope envelope, DateTime now, CancellationToken ct)
    {
        if (envelope.Status != "Approved" || record.ContentHash is null || record.State is RecordState.Ignored or RecordState.Dismissed) return false;
        var (requisition, problems) = SourceRecordParser.Parse(item, envelope);
        if (requisition is null || problems.Count > 0 || Fingerprints.ContentHash(requisition) == record.ContentHash) return false;

        var detail = $"v{envelope.Version} was served again with different content";
        Ledger.Event(db, run, record.SourceId, RunEventKind.Conflict, detail, now);
        await Ledger.QuarantineAsync(db, run, record.SourceId, envelope.Version, PipelineStage.Extract, QuarantineReason.ConflictingSourceContent,
            $"{detail}; neither copy is sent until the source issues a new version" +
            (record.SapNumber is null ? "" : $" (SAP PR {record.SapNumber} keeps the earlier content)"), envelope.Raw, now, ct);

        // We no longer know which content is right, so a create not yet sent is withdrawn.
        var staged = db.Outbox.Local.Where(m => m.SourceId == record.SourceId && m.ProcessedAt == null).ToList();
        staged.AddRange(await db.Outbox.Where(m => m.SourceId == record.SourceId && m.ProcessedAt == null).ToListAsync(ct));
        foreach (var m in staged.Distinct())
        {
            m.ProcessedAt = now;
            m.LastError = "Withdrawn: conflicting content for the same version";
        }
        if (record.SapNumber is null) record.State = RecordState.Quarantined;
        record.ContentHash = null;
        record.LastError = detail;
        return true;
    }

    private async Task ProcessAsync(SyncDbContext db, RunContext run, JsonElement item, HashSet<(string, long)> seenInPage, bool force, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var envelope = SourceRecordParser.ReadEnvelope(item);
        var key = envelope.Key;
        Ledger.Event(db, run, key, RunEventKind.Extracted, $"v{envelope.Version} {envelope.Status}", now);

        var record = await db.Requisitions.FindAsync([key], ct);
        var repeatedInPage = !seenInPage.Add((key, envelope.Version));
        if (!force && (repeatedInPage || (record is not null && envelope.Version <= record.SourceVersion)))
        {
            if (record is not null && envelope.Version == record.SourceVersion
                && await QuarantineIfConflictingAsync(db, run, record, item, envelope, now, ct)) return;
            Ledger.Event(db, run, key, RunEventKind.Unchanged, $"v{envelope.Version} already processed", now);
            return;
        }

        if (record is null)
        {
            record = new RequisitionRecord { SourceId = key, FirstSeenAt = now };
            db.Requisitions.Add(record);
        }
        record.SourceVersion = envelope.Version;
        record.SourceStatus = envelope.Status;
        record.UpdatedAt = now;

        if (envelope.Status != "Approved")
        {
            if (record.SapNumber is not null)
            {
                await Ledger.QuarantineAsync(db, run, key, envelope.Version, PipelineStage.Extract,
                    QuarantineReason.SourceChangedAfterTransfer,
                    $"Source status changed to {envelope.Status ?? "(none)"} after transfer to SAP PR {record.SapNumber}; review the SAP requisition",
                    envelope.Raw, now, ct);
                Ledger.Event(db, run, key, RunEventKind.SourceChangedAfterTransfer, envelope.Status, now);
            }
            else
            {
                record.State = RecordState.Ignored;
                record.ContentHash = null;
                // Anything staged while it was approved (for example waiting for SAP to come back) must not be sent now.
                var withdrawn = db.Outbox.Local.Where(m => m.SourceId == key && m.ProcessedAt == null).ToList();
                withdrawn.AddRange(await db.Outbox.Where(m => m.SourceId == key && m.ProcessedAt == null).ToListAsync(ct));
                foreach (var m in withdrawn.Distinct())
                {
                    m.ProcessedAt = now;
                    m.LastError = $"Withdrawn: no longer approved ({envelope.Status ?? "no status"})";
                }
                await Ledger.ResolveQuarantineAsync(db, run, key, $"Requisition is no longer approved ({envelope.Status})", now, ct);
                Ledger.Event(db, run, key, RunEventKind.Ignored, envelope.Status, now);
            }
            return;
        }

        var (requisition, problems) = SourceRecordParser.Parse(item, envelope);
        if (requisition is not null) problems = validator.Check(requisition);
        if (problems.Count > 0)
        {
            // The newest version is the requester's intent. If it is invalid, an older version still queued must not
            // be sent in its place.
            if (record.SapNumber is null)
            {
                var older = db.Outbox.Local.Where(m => m.SourceId == key && m.ProcessedAt == null).ToList();
                older.AddRange(await db.Outbox.Where(m => m.SourceId == key && m.ProcessedAt == null).ToListAsync(ct));
                foreach (var m in older.Distinct())
                {
                    m.ProcessedAt = now;
                    m.LastError = $"Withdrawn: superseded by invalid v{envelope.Version}";
                }
            }
            // A bad new version of an already-synced requisition leaves SAP's copy in place.
            record.State = record.SapNumber is null ? RecordState.Quarantined : RecordState.Synced;
            record.LastError = problems[0].Message;
            await Ledger.QuarantineAsync(db, run, key, envelope.Version, PipelineStage.Validate, problems, envelope.Raw, now, ct);
            return;
        }

        var hash = Fingerprints.ContentHash(requisition!);
        if (!force && record.ContentHash == hash && record.State is not (RecordState.Ignored or RecordState.Dismissed))
        {
            Ledger.Event(db, run, key, RunEventKind.Unchanged, $"v{envelope.Version} has no content change", now);
            return;
        }

        await Ledger.ResolveQuarantineAsync(db, run, key,
            force ? $"Replayed by an operator (v{envelope.Version})" : $"Superseded by valid version {envelope.Version}", now, ct);

        var kind = record.SapNumber is null ? OutboxKind.Create : OutboxKind.Amend;
        var staged = db.Outbox.Local.FirstOrDefault(m => m.SourceId == key && m.SourceVersion == envelope.Version && m.Kind == kind)
            ?? await db.Outbox.FirstOrDefaultAsync(m => m.SourceId == key && m.SourceVersion == envelope.Version && m.Kind == kind, ct);
        if (staged is not null && force)
        {
            // The unique (id, version, kind) index forbids a second message, so the existing one is reopened. Taking
            // the payload just read also repairs a stored payload that had become unreadable.
            staged.Payload = JsonSerializer.Serialize(requisition, PayloadJson);
            staged.ProcessedAt = null;
            staged.Attempts = 0;
            staged.NextAttemptAt = null;
            staged.LastError = null;
        }
        else if (staged is null)
        {
            db.Outbox.Add(new OutboxMessage
            {
                SourceId = key,
                SourceVersion = envelope.Version,
                Kind = kind,
                Payload = JsonSerializer.Serialize(requisition, PayloadJson),
                CreatedAt = now,
            });
        }

        record.ContentHash = hash;
        record.CanonicalJson = JsonSerializer.Serialize(requisition, PayloadJson);
        record.State = RecordState.Queued;
        record.Attempts = 0;
        record.NextAttemptAt = null;
        record.LastError = null;
        var truncated = requisition!.Lines.Count(l => l.Description.Length > Mapping.SapMapper.TextLimit);
        Ledger.Event(db, run, key, RunEventKind.Staged, $"{kind} v{envelope.Version}" +
            (truncated > 0 ? $" ({truncated} line description(s) longer than SAP's {Mapping.SapMapper.TextLimit}-character item text; SAP gets the first {Mapping.SapMapper.TextLimit}, the source keeps the full text)" : ""), now);
    }
}
