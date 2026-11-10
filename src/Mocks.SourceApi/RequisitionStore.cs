using System.Text.Json.Nodes;
using Mocks.Shared;
using Procurement.SyntheticData;

namespace Mocks.SourceApi;

public sealed record RequisitionPage(JsonArray Items, string? NextCursor, bool HasMore);

public sealed record ErpStatusUpdate(string ErpReference, string ErpStatus, DateTimeOffset ErpStatusAt, string? Message);

public enum UpdateOutcome { Updated, NotFound, PreconditionMissing, PreconditionFailed, NotAllowed }

public enum Dataset { Workbook, Generated, Empty }

// Records stay raw JSON so malformed ones are served exactly as an imperfect upstream would serve them.
public sealed class RequisitionStore
{
    private sealed class Entry(string key, long version, DateTime updatedAt, JsonObject body, long? listedVersion = null)
    {
        public string Key { get; } = key;
        public long Version { get; set; } = version;
        public DateTime UpdatedAt { get; set; } = updatedAt;
        public JsonObject Body { get; } = body;
        public long? ListedVersion { get; set; } = listedVersion;
        public bool HiddenFromList { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly MonotonicClock _clock;
    private readonly TimeProvider _time;
    private readonly string? _workbookPath;
    private readonly string? _directoryPath;
    private IReadOnlyList<SeedManifestEntry> _manifest = [];

    public RequisitionStore(MonotonicClock clock, TimeProvider time, IConfiguration configuration)
    {
        _clock = clock;
        _time = time;
        _workbookPath = configuration["Dataset:WorkbookPath"];
        _directoryPath = configuration["Dataset:DirectoryPath"];
        Reset(configuration.GetValue("Dataset:Source", Dataset.Workbook),
            configuration.GetValue("Seed:Value", 42), configuration.GetValue("Seed:Count", 250));
    }

    public Dataset Dataset { get; private set; }

    public IReadOnlyList<SeedManifestEntry> Manifest
    {
        get { lock (_gate) return _manifest; }
    }

    public void Reset(Dataset dataset, int seed, int count)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var data = dataset switch
        {
            Dataset.Workbook => WorkbookSeed.Build(DataPack.Load(_workbookPath), EmployeeDirectory.Load(_directoryPath), now),
            Dataset.Empty => new SeedResult([], []),
            _ => SeedData.Generate(seed, count, now),
        };
        lock (_gate)
        {
            Dataset = dataset;
            _entries.Clear();
            foreach (var r in data.Records)
            {
                _entries[r.Key] = new Entry(r.Key, r.Version, r.UpdatedAt, r.Body, r.ListedVersion);
            }
            _manifest = data.Manifest;
        }
    }

    public RequisitionPage List(string? status, string? requester, DateTime? updatedAfter, KeysetCursor? after, int limit, bool pageOverlap)
    {
        lock (_gate)
        {
            var matches = _entries.Values
                .Where(e => !e.HiddenFromList)
                .Where(e => after is null || after.Value.IsBefore(e.UpdatedAt, e.Key))
                .Where(e => updatedAfter is null || e.UpdatedAt > updatedAfter)
                .Where(e => status is null || StatusOf(e.Body) == status)
                .Where(e => requester is null || StringOf(e.Body, "requester") == requester)
                .OrderBy(e => e.UpdatedAt).ThenBy(e => e.Key, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToList();

            var hasMore = matches.Count > limit;
            var page = matches.Take(limit).ToList();

            var items = new JsonArray();
            if (pageOverlap && after is not null && _entries.TryGetValue(after.Value.Key, out var previous))
            {
                items.Add(Render(previous, listed: true));
            }
            foreach (var e in page) items.Add(Render(e, listed: true));

            // Returned even on the last page, because it is where the next run resumes.
            var next = page.Count > 0 ? new KeysetCursor(page[^1].UpdatedAt, page[^1].Key).Encode() : after?.Encode();
            return new RequisitionPage(items, next, hasMore);
        }
    }

    public (JsonObject Body, long Version)? Get(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return null;
            // Once the record itself has been read, the list catches up to the current version too.
            e.ListedVersion = null;
            return (Render(e), e.Version);
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

    public (JsonObject Body, long Version) Create(JsonObject body)
    {
        lock (_gate)
        {
            // Continue the current series: REQ-7001 style for the workbook, REQ-2026-000001 style for generated data.
            var prefix = Dataset == Dataset.Workbook ? "REQ-" : "REQ-2026-";
            var next = _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => int.TryParse(k.AsSpan(prefix.Length), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            var id = Dataset == Dataset.Workbook ? $"REQ-{next}" : $"REQ-2026-{next:D6}";
            var now = _clock.UtcNow;
            body["id"] = id;
            body["status"] = "PendingApproval";
            body["createdAt"] = now.ToString("O");
            body["approvedAt"] = null;
            body["erpSync"] = null;
            var entry = new Entry(id, 1, now, body);
            _entries[id] = entry;
            return (Render(entry), entry.Version);
        }
    }

    public (UpdateOutcome Outcome, JsonObject? Body, long Version) Withdraw(string id, HttpRequest request)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return (UpdateOutcome.NotFound, null, 0);
            switch (ETags.CheckIfMatch(request, e.Version))
            {
                case PreconditionResult.Missing: return (UpdateOutcome.PreconditionMissing, null, e.Version);
                case PreconditionResult.Failed: return (UpdateOutcome.PreconditionFailed, null, e.Version);
            }
            if (StatusOf(e.Body) is not ("PendingApproval" or "Draft")) return (UpdateOutcome.NotAllowed, Render(e), e.Version);
            e.Body["status"] = "Cancelled";
            Touch(e);
            return (UpdateOutcome.Updated, Render(e), e.Version);
        }
    }

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

    // A forced version simulates a buggy source that changes content without bumping the version.
    public JsonObject Upsert(string key, JsonObject body, long? version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                var replaced = new Entry(key, version ?? existing.Version + 1, _clock.UtcNow, body);
                _entries[key] = replaced;
                return Render(replaced);
            }
            var entry = new Entry(key, version ?? 1, _clock.UtcNow, body);
            _entries[key] = entry;
            return Render(entry);
        }
    }

    public bool Delete(string id)
    {
        lock (_gate) return _entries.Remove(id);
    }

    public bool SetHiddenFromList(string id, bool hidden)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return false;
            e.HiddenFromList = hidden;
            return true;
        }
    }

    public JsonArray Snapshot()
    {
        lock (_gate)
        {
            var all = new JsonArray();
            foreach (var e in _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal)) all.Add(Render(e));
            return all;
        }
    }

    private void Touch(Entry e)
    {
        e.ListedVersion = null;
        e.Version++;
        e.UpdatedAt = _clock.UtcNow;
    }

    private static string? StatusOf(JsonObject body) => StringOf(body, "status");

    private static string? StringOf(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject Render(Entry e, bool listed = false)
    {
        var copy = e.Body.DeepClone().AsObject();
        copy["version"] = listed && e.ListedVersion is { } stale ? stale : e.Version;
        copy["updatedAt"] = e.UpdatedAt.ToString("O");
        return copy;
    }
}
