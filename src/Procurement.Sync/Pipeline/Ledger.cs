using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.Sync.Store;
using Procurement.Sync.Validation;

namespace Procurement.Sync.Pipeline;

/// <summary>Writes run events and quarantine entries in the caller's unit of work, so they commit with the state change.</summary>
internal static class Ledger
{
    public static void Event(SyncDbContext db, RunContext run, string key, RunEventKind kind, string? detail, DateTime now) =>
        db.RunEvents.Add(new RunEvent { RunId = run.RunId, SourceKey = key, Kind = kind, Detail = detail, At = now });

    public static async Task QuarantineAsync(SyncDbContext db, RunContext run, string key, long? version, PipelineStage stage,
        IReadOnlyList<ValidationProblem> problems, string? raw, DateTime now, CancellationToken ct)
    {
        var first = problems[0];
        var details = JsonSerializer.Serialize(problems.Select(p => new { reason = p.Reason.ToString(), field = p.Field, message = p.Message }));
        var entry = await FindOpenAsync(db, key, ct);
        if (entry is null)
        {
            entry = new QuarantineEntry { SourceKey = key, Summary = first.Message, FirstSeenAt = now };
            db.Quarantine.Add(entry);
        }
        else
        {
            entry.Occurrences++;
        }
        entry.SourceVersion = version;
        entry.Stage = stage;
        entry.Reason = first.Reason;
        entry.Summary = first.Message;
        entry.Details = details;
        entry.RawPayload = raw;
        entry.LastSeenAt = now;
        Event(db, run, key, RunEventKind.Quarantined, $"{first.Reason}: {first.Message}", now);
    }

    public static Task QuarantineAsync(SyncDbContext db, RunContext run, string key, long? version, PipelineStage stage,
        QuarantineReason reason, string summary, string? raw, DateTime now, CancellationToken ct) =>
        QuarantineAsync(db, run, key, version, stage, [new ValidationProblem(reason, "", summary)], raw, now, ct);

    public static async Task ResolveQuarantineAsync(SyncDbContext db, RunContext run, string key, string resolution, DateTime now, CancellationToken ct)
    {
        var entry = await FindOpenAsync(db, key, ct);
        if (entry is null) return;
        entry.ResolvedAt = now;
        entry.Resolution = resolution;
        Event(db, run, key, RunEventKind.QuarantineResolved, $"{entry.Reason} resolved: {resolution}", now);
    }

    private static async Task<QuarantineEntry?> FindOpenAsync(SyncDbContext db, string key, CancellationToken ct) =>
        db.Quarantine.Local.FirstOrDefault(q => q.SourceKey == key && q.ResolvedAt == null)
        ?? await db.Quarantine.FirstOrDefaultAsync(q => q.SourceKey == key && q.ResolvedAt == null, ct);
}
