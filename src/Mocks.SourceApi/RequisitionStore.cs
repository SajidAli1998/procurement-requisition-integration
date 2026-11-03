using System.Text.Json.Nodes;
using Mocks.Shared;
using Procurement.SyntheticData;

namespace Mocks.SourceApi;

public sealed record RequisitionPage(JsonArray Items, string? NextCursor, bool HasMore);

public sealed record ErpStatusUpdate(string ErpReference, string ErpStatus, DateTimeOffset ErpStatusAt, string? Message);

public enum UpdateOutcome { Updated, NotFound, PreconditionMissing, PreconditionFailed, NotAllowed }

/// <summary>Which data set the source serves: the client's workbook (default) or our generated stress set.</summary>
public enum Dataset { Workbook, Generated }

/// <summary>
/// In-memory store for the internal requisition system. Records are kept as raw JSON so
/// malformed records can be served exactly as an imperfect upstream would serve them.
/// </summary>
public sealed class RequisitionStore
{
    private sealed class Entry(string key, long version, DateTime updatedAt, JsonObject body, long? listedVersion = null)
    {
        public string Key { get; } = key;
        public long Version { get; set; } = version;
        public DateTime UpdatedAt { get; set; } = updatedAt;
        public JsonObject Body { get; } = body;
        public long? ListedVersion { get; set; } = listedVersion;
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
        var data = dataset == Dataset.Workbook
            ? WorkbookSeed.Build(DataPack.Load(_workbookPath), EmployeeDirectory.Load(_directoryPath), now)
            : SeedData.Generate(seed, count, now);
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
            // Fault mode: repeat the previous page's last record, as some buggy pagers do.
            if (pageOverlap && after is not null && _entries.TryGetValue(after.Value.Key, out var previous))
            {
                items.Add(Render(previous, listed: true));
            }
            foreach (var e in page) items.Add(Render(e, listed: true));

            // The cursor is returned even on the last page: it is the durable resume point for the next run.
            var next = page.Count > 0 ? new KeysetCursor(page[^1].UpdatedAt, page[^1].Key).Encode() : after?.Encode();
            return new RequisitionPage(items, next, hasMore);
        }
    }

    public (JsonObject Body, long Version)? Get(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) return null;
            // Reading the record itself is authoritative; afterwards the list shows the current version too.
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

    /// <summary>Creates a new requisition awaiting approval (used by the assistant API).</summary>
    public (JsonObject Body, long Version) Create(JsonObject body)
    {
        lock (_gate)
        {
            // Workbook ids are REQ-7001..REQ-7120; generated ones REQ-2026-000001. New requests continue the same series.
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

    /// <summary>Withdraws a requisition that is still awaiting approval.</summary>
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
