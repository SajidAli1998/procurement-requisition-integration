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

/// <summary>
/// Reads every change from the source in (updatedAt, id) order, validates each record on its own and
/// stages valid ones in the outbox. Each page and its checkpoint commit in one transaction, so a crash
/// resumes from the last completed page.
/// </summary>
public sealed class ExtractStage(
    IDbContextFactory<SyncDbContext> dbFactory,
    SourceClient source,
    RequisitionValidator validator,
    IOptions<SyncOptions> options,
    TimeProvider time,
    ILogger<ExtractStage> logger)
{
    public const string Stream = "source-requisitions";
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<int> RunAsync(RunContext run, CancellationToken ct)
    {
        string? cursor;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            cursor = (await db.Checkpoints.FindAsync([Stream], ct))?.Cursor;
        }

        var pages = 0;
        while (true)
        {
            var page = await source.GetChangesAsync(cursor, options.Value.PageSize, ct);
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
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("Extracted page {Page} ({Count} records)", pages, page.Items.Count);
            cursor = checkpoint.Cursor;
            if (!page.HasMore) return pages;
        }
    }

    /// <summary>
    /// Operator replay: re-reads one requisition from the source and pushes it through validation and
    /// staging again, even if this version was seen before (e.g. after SAP configuration was fixed).
    /// </summary>
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

    private async Task ProcessAsync(SyncDbContext db, RunContext run, JsonElement item, HashSet<(string, long)> seenInPage, bool force, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var envelope = SourceRecordParser.ReadEnvelope(item);
        var key = envelope.Key;
        Ledger.Event(db, run, key, RunEventKind.Extracted, $"v{envelope.Version} {envelope.Status}", now);

        var record = await db.Requisitions.FindAsync([key], ct);
        if (!force && (!seenInPage.Add((key, envelope.Version)) || (record is not null && envelope.Version <= record.SourceVersion)))
        {
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
                await Ledger.ResolveQuarantineAsync(db, run, key, $"Requisition is no longer approved ({envelope.Status})", now, ct);
                Ledger.Event(db, run, key, RunEventKind.Ignored, envelope.Status, now);
            }
            return;
        }

        var (requisition, problems) = SourceRecordParser.Parse(item, envelope);
        if (requisition is not null) problems = validator.Check(requisition);
        if (problems.Count > 0)
        {
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
            // Re-open the existing message: the unique (id, version, kind) index forbids a second one.
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
        Ledger.Event(db, run, key, RunEventKind.Staged, $"{kind} v{envelope.Version}", now);
    }
}
