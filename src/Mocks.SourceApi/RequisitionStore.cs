using System.Text.Json.Nodes;
using Mocks.Shared;

namespace Mocks.SourceApi;

public sealed record RequisitionPage(JsonArray Items, string? NextCursor, bool HasMore);

public sealed record ErpStatusUpdate(string ErpReference, string ErpStatus, DateTimeOffset ErpStatusAt, string? Message);

public enum UpdateOutcome { Updated, NotFound, PreconditionMissing, PreconditionFailed }

/// <summary>
/// In-memory store for the internal requisition system. Records are kept as raw JSON so
/// malformed records can be served exactly as an imperfect upstream would serve them.
/// </summary>
public sealed class RequisitionStore
{
    private sealed class Entry(string key, long version, DateTime updatedAt, JsonObject body)
    {
        public string Key { get; } = key;
        public long Version { get; set; } = version;
        public DateTime UpdatedAt { get; set; } = updatedAt;
        public JsonObject Body { get; } = body;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly MonotonicClock _clock;
    private readonly TimeProvider _time;
    private IReadOnlyList<SeedManifestEntry> _manifest = [];

    public RequisitionStore(MonotonicClock clock, TimeProvider time, IConfiguration configuration)
    {
        _clock = clock;
        _time = time;
        Reset(configuration.GetValue("Seed:Value", 42), configuration.GetValue("Seed:Count", 250));
    }

    public IReadOnlyList<SeedManifestEntry> Manifest
    {
        get { lock (_gate) return _manifest; }
    }

    public void Reset(int seed, int count)
    {
        var generated = SeedData.Generate(seed, count, _time.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            _entries.Clear();
            foreach (var r in generated.Records)
            {
                _entries[r.Key] = new Entry(r.Key, r.Version, r.UpdatedAt, r.Body);
            }
            _manifest = generated.Manifest;
        }
    }

    public RequisitionPage List(string? status, DateTime? updatedAfter, KeysetCursor? after, int limit, bool pageOverlap)
    {
        lock (_gate)
        {
            var matches = _entries.Values
                .Where(e => after is null || after.Value.IsBefore(e.UpdatedAt, e.Key))
                .Where(e => updatedAfter is null || e.UpdatedAt > updatedAfter)
                .Where(e => status is null || StatusOf(e.Body) == status)
                .OrderBy(e => e.UpdatedAt).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToList();

            var hasMore = matches.Count > limit;
            var page = matches.Take(limit).ToList();

            var items = new JsonArray();
            // Fault mode: repeat the previous page's last record, as some buggy pagers do.
            if (pageOverlap && after is not null && _entries.TryGetValue(after.Value.Key, out var previous))
            {
                items.Add(Render(previous));
            }
            foreach (var e in page) items.Add(Render(e));

            // The cursor is returned even on the last page: it is the durable resume point for the next run.
            var next = page.Count > 0 ? new KeysetCursor(page[^1].UpdatedAt, page[^1].Key).Encode() : after?.Encode();
            return new RequisitionPage(items, next, hasMore);
        }
    }

    public (JsonObject Body, long Version)? Get(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var e) ? (Render(e), e.Version) : null;
        }
    }

    public (UpdateOutcome Outcome, JsonObject? Body, long Version) UpdateErpStatus(string id, HttpRequest request, ErpStatusUpdate update)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return (UpdateOutcome.NotFound, null, 0);

            switch (ETags.CheckIfMatch(request, e.Version))
            {
                case PreconditionResult.Missing: return (UpdateOutcome.PreconditionMissing, null, e.Version);
                case PreconditionResult.Failed: return (UpdateOutcome.PreconditionFailed, null, e.Version);
            }

            e.Body["erpSync"] = new JsonObject
            {
                ["reference"] = update.ErpReference,
                ["status"] = update.ErpStatus,
                ["statusAt"] = update.ErpStatusAt.UtcDateTime.ToString("O"),
                ["message"] = update.Message,
            };
            Touch(e);
            return (UpdateOutcome.Updated, Render(e), e.Version);
        }
    }

    /// <summary>Simulates a user editing the requisition in the source system (admin only).</summary>
    public JsonObject? Edit(string id, JsonObject mergePatch)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return null;
            mergePatch.Remove("id");
            JsonMergePatch.Apply(e.Body, mergePatch);
            Touch(e);
            return Render(e);
        }
    }

    private void Touch(Entry e)
    {
        e.Version++;
        e.UpdatedAt = _clock.UtcNow;
    }

    private static string? StatusOf(JsonObject body) =>
        body["status"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject Render(Entry e)
    {
        var copy = e.Body.DeepClone().AsObject();
        copy["version"] = e.Version;
        copy["updatedAt"] = e.UpdatedAt.ToString("O");
        return copy;
    }
}
