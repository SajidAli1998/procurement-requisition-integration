using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Mapping;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;
using Procurement.Sync.Validation;

namespace Procurement.TestLab;

/// <summary>Each connection is "up", "down" or "unreliable".</summary>
public sealed record NetworkState(string Sap = "up", string Source = "up", bool SlowMotion = false);

public sealed record LiveEvent(string Type, DateTimeOffset At, object? Data);

public sealed record WorkspaceRun(string Id, string Kind, DateTimeOffset StartedAt)
{
    public DateTimeOffset? FinishedAt { get; set; }
    public string State { get; set; } = "Running";
    public string? Outcome { get; set; }
    public string? Message { get; set; }
    public double? DurationMs { get; set; }
    public Dictionary<string, int>? Events { get; set; }
    public Dictionary<string, long>? Transport { get; set; }
    public int? Pending { get; set; }
    public int? OpenQuarantine { get; set; }
    public double? RetryWaitSeconds { get; set; }
    public List<string> Steps { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public ReconciliationReport? Report { get; set; }
}

/// <summary>The long-lived lab behind the web app's operations pages. Scenarios never use it: each gets its own.</summary>
public sealed class Workspace : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<WorkspaceRun> _runs = new();
    private readonly List<Channel> _subscribers = [];
    private readonly Lock _subscribersGate = new();
    private CancellationTokenSource? _current;
    private CancellationTokenSource? _scheduler;

    public Workspace() => Lab = Create(new LabOptions { Dataset = "Workbook", Sync = Defaults(), Assistant = AssistantDefaults() });

    private static Dictionary<string, string?> AssistantDefaults() => new() { ["Assistant:RequestsPerMinute"] = "60" };

    public LabEnvironment Lab { get; private set; }
    public string Dataset => Lab.Options.Dataset;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public bool Scheduled => _scheduler is { IsCancellationRequested: false };
    public NetworkState Network { get; private set; } = new();
    public bool Paused => Lab.Worker.Services.GetRequiredService<SyncControl>().IsPaused;

    public void Pause() => Lab.Worker.Services.GetRequiredService<SyncControl>().Pause();
    public void Resume() => Lab.Worker.Services.GetRequiredService<SyncControl>().Resume();

    /// <summary>Replaces any faults set elsewhere for that system.</summary>
    public async Task SetNetworkAsync(NetworkState state)
    {
        await ApplyAsync(Lab.SapAdmin, state.Sap, state.SlowMotion ? 700 : 0);
        await ApplyAsync(Lab.SourceAdmin, state.Source, state.SlowMotion ? 150 : 0);
        Network = state;
        Publish("network", state);
    }

    private static async Task ApplyAsync(HttpClient admin, string connection, int latencyMs)
    {
        (await admin.DeleteAsync("/_admin/faults")).EnsureSuccessStatusCode();
        var flaky = connection == "unreliable";
        var settings = new Mocks.Shared.Faults.FaultSettings
        {
            LatencyMs = latencyMs, LostResponseRate = flaky ? 0.25 : 0, TransientErrorRate = flaky ? 0.15 : 0, Seed = Environment.TickCount,
        };
        (await admin.PutAsJsonAsync("/_admin/faults", settings)).EnsureSuccessStatusCode();
        if (connection == "down")
            (await admin.PostAsJsonAsync("/_admin/faults/script", new[] { new Mocks.Shared.Faults.ScriptedFault { Kind = Mocks.Shared.Faults.FaultKind.Abort, Count = 1_000_000 } })).EnsureSuccessStatusCode();
    }
    public int ScheduleSeconds { get; private set; } = 10;
    public WorkspaceRun? Active => _runs.LastOrDefault(r => r.State == "Running");
    public IReadOnlyList<WorkspaceRun> Runs => _runs.ToArray();

    private static Dictionary<string, string?> Defaults() => new()
    {
        ["PageSize"] = "40",
        // Retry-After is honoured at 1/20 speed so a demo run takes seconds, not a minute.
        ["Resilience:DelayScale"] = "0.05",
        ["Resilience:BreakerMinimumThroughput"] = "20",
        ["Resilience:BreakerBreakDuration"] = "00:00:05",
        // Retries and in-doubt re-checks after 2 s instead of 30 s/10 s, so a recovery is visible on the next click.
        ["RetryPendingBaseDelay"] = "00:00:02",
        ["InDoubtRecheckDelay"] = "00:00:02",
    };

    private LabEnvironment Create(LabOptions options)
    {
        var lab = new LabEnvironment(options with { FastWaits = false });
        lab.Capture.Recorded += e => Publish("http", e);
        return lab;
    }

    public async Task ResetAsync(string dataset, int seed, int count)
    {
        await _gate.WaitAsync();
        try
        {
            StopSchedule();
            _current?.Cancel();
            await Lab.DisposeAsync();
            Lab = Create(new LabOptions { Dataset = dataset, Seed = seed, Count = count, Sync = Defaults(), Assistant = AssistantDefaults() });
            CreatedAt = DateTimeOffset.UtcNow;
            Network = new NetworkState();
            while (_runs.TryDequeue(out _)) { }
        }
        finally
        {
            _gate.Release();
        }
        Publish("workspace-reset", new { dataset, seed, count });
    }

    /// <summary>Starts a run in the background; null when one is already running.</summary>
    public WorkspaceRun? Start(string kind, Func<LabEnvironment, CancellationToken, Task<RunOutcome>> work)
    {
        if (!_gate.Wait(0)) return null;
        var run = new WorkspaceRun($"ws-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}", kind, DateTimeOffset.UtcNow);
        _runs.Enqueue(run);
        while (_runs.Count > 200) _runs.TryDequeue(out _);
        _current = new CancellationTokenSource();
        var token = _current.Token;
        Publish("run-started", run);
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await work(Lab, token);
                Complete(run, outcome);
            }
            catch (Exception ex)
            {
                run.State = "Finished";
                run.Outcome = token.IsCancellationRequested ? "Stopped" : "Failed";
                run.Message = token.IsCancellationRequested ? "Stopped by the operator; records mid-send are re-checked in SAP on the next run" : ex.GetBaseException().Message;
                run.FinishedAt = DateTimeOffset.UtcNow;
            }
            finally
            {
                _current = null;
                _gate.Release();
                Publish("run-finished", run);
            }
        });
        return run;
    }

    private static void Complete(WorkspaceRun run, RunOutcome outcome)
    {
        var r = outcome.Result;
        run.State = "Finished";
        run.Outcome = outcome.Kind.ToString();
        run.Message = outcome.Error ?? r?.Message;
        run.DurationMs = Math.Round(outcome.DurationMs, 1);
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.Events = r?.Events.ToDictionary(e => e.Key.ToString(), e => e.Value);
        run.Transport = r?.Transport.ToDictionary(e => e.Key, e => e.Value);
        run.Pending = r?.Pending;
        run.OpenQuarantine = r?.OpenQuarantine;
        run.RetryWaitSeconds = r?.RetryWait.TotalSeconds;
        run.Report = r?.Report;
    }

    /// <summary>Null when a run is in progress.</summary>
    public async Task<T?> ExclusiveAsync<T>(Func<LabEnvironment, Task<T>> work) where T : class
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2))) return null;
        try { return await work(Lab); }
        finally { _gate.Release(); }
    }

    /// <summary>Records a run made outside Start, such as a what-if situation.</summary>
    public void Record(string kind, RunOutcome outcome)
    {
        var run = new WorkspaceRun($"ws-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}", kind, DateTimeOffset.UtcNow.AddMilliseconds(-outcome.DurationMs));
        Complete(run, outcome);
        _runs.Enqueue(run);
        while (_runs.Count > 200) _runs.TryDequeue(out _);
        Publish("run-finished", run);
    }

    public WorkspaceRun? StartSync(string trigger = "web") => Start("sync", (lab, ct) => lab.RunAsync(trigger, ct));

    public WorkspaceRun? StartReplay(IReadOnlyList<string> ids) => Start(ids.Count == 1 ? $"replay {ids[0]}" : $"replay {ids.Count}", async (lab, ct) =>
    {
        RunOutcome? last = null;
        foreach (var id in ids)
        {
            var started = DateTime.UtcNow;
            try
            {
                var result = await lab.Worker.ReplayAsync(id, ct);
                last = new RunOutcome(result.Skipped ? RunOutcomeKind.Skipped : result.Status == RunStatus.Completed ? RunOutcomeKind.Completed : RunOutcomeKind.CompletedWithIssues,
                    result, result.Message, (DateTime.UtcNow - started).TotalMilliseconds, lab.Worker.Name);
            }
            catch (InvalidOperationException ex)
            {
                last = new RunOutcome(RunOutcomeKind.Failed, null, ex.Message, 0, lab.Worker.Name);
            }
        }
        return last!;
    });

    /// <summary>Cancels like a killed process would; the next run re-checks anything mid-send.</summary>
    public bool Stop()
    {
        StopSchedule();
        if (_current is null) return false;
        _current.Cancel();
        return true;
    }

    public void StartSchedule(int seconds)
    {
        StopSchedule();
        ScheduleSeconds = Math.Clamp(seconds, 2, 3600);
        var cts = _scheduler = new CancellationTokenSource();
        Publish("schedule", new { enabled = true, seconds = ScheduleSeconds });
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                StartSync("schedule");
                try { await Task.Delay(TimeSpan.FromSeconds(ScheduleSeconds), cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void StopSchedule()
    {
        if (_scheduler is null) return;
        _scheduler.Cancel();
        _scheduler = null;
        Publish("schedule", new { enabled = false, seconds = ScheduleSeconds });
    }

    public async Task RestartAsync()
    {
        Stop();
        await _gate.WaitAsync();
        try { await Lab.RestartAsync(); }
        finally { _gate.Release(); }
        Publish("restart", null);
    }

    public sealed class Channel
    {
        public System.Threading.Channels.Channel<LiveEvent> Events { get; } = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(
            new System.Threading.Channels.BoundedChannelOptions(2000) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    }

    public Channel Subscribe()
    {
        var c = new Channel();
        lock (_subscribersGate) _subscribers.Add(c);
        return c;
    }

    public void Unsubscribe(Channel c)
    {
        lock (_subscribersGate) _subscribers.Remove(c);
    }

    public void Publish(string type, object? data)
    {
        var e = new LiveEvent(type, DateTimeOffset.UtcNow, data);
        lock (_subscribersGate) foreach (var s in _subscribers) s.Events.Writer.TryWrite(e);
    }

    public async Task<List<JsonObject>> RequisitionRowsAsync()
    {
        var source = await Lab.SourceSnapshotAsync();
        var sap = (await Lab.SapDocumentsAsync()).GroupBy(d => d["PurchaseRequisition"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.First());
        await using var db = Lab.Db();
        await db.Database.EnsureCreatedAsync();
        var records = await db.Requisitions.AsNoTracking().ToDictionaryAsync(r => r.SourceId);
        var quarantine = (await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null).ToListAsync()).GroupBy(q => q.SourceKey).ToDictionary(g => g.Key, g => g.First());
        var lastEvents = (await db.RunEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(20000).ToListAsync())
            .GroupBy(e => e.SourceKey).ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<JsonObject>();
        foreach (var node in source)
        {
            var s = node!.AsObject();
            var id = s["id"]?.GetValue<string>() ?? "(no id)";
            records.TryGetValue(id, out var r);
            quarantine.TryGetValue(id, out var q);
            lastEvents.TryGetValue(id, out var ev);
            var lines = s["lines"] as JsonArray;
            var first = lines?.FirstOrDefault() as JsonObject;
            var doc = r?.SapNumber is { } n && sap.TryGetValue(n, out var d) ? d : null;
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["sourceSystem"] = "Internal requisitions (lab)",
                ["requester"] = s["requester"]?.DeepClone(),
                ["department"] = s["department"]?.DeepClone() ?? s["costCenter"]?.DeepClone(),
                ["approvalStatus"] = s["status"]?.DeepClone(),
                ["sourceVersion"] = s["version"]?.DeepClone(),
                ["itemCount"] = lines?.Count ?? 0,
                ["materialCode"] = first?["materialCode"]?.DeepClone(),
                ["description"] = first?["description"]?.DeepClone(),
                ["quantity"] = first?["quantity"]?.DeepClone(),
                ["unitOfMeasure"] = first?["uom"]?.DeepClone(),
                ["unitPrice"] = first?["unitPrice"]?.DeepClone(),
                ["currency"] = s["currency"]?.DeepClone(),
                ["totalAmount"] = s["totalAmount"]?.DeepClone(),
                ["costCenter"] = s["costCenter"]?.DeepClone(),
                ["companyCode"] = s["companyCode"]?.DeepClone(),
                ["plant"] = first?["plant"]?.DeepClone(),
                ["purchasingGroup"] = doc?["to_PurchaseReqnItem"]?[0]?["PurchasingGroup"]?.DeepClone(),
                ["requiredDelivery"] = s["neededBy"]?.DeepClone(),
                ["sapNumber"] = r?.SapNumber,
                ["sapStatus"] = r?.ErpStatus?.ToString(),
                ["state"] = r?.State.ToString() ?? "NotSeen",
                ["outcome"] = OutcomeOf(r, ev),
                ["lastAttempt"] = ev?.FirstOrDefault(e => e.SapResponses is not null)?.At.ToString("O"),
                ["retryCount"] = ev?.Count(e => e.Kind == RunEventKind.RetryScheduled) ?? 0,
                ["validation"] = q is null ? (r is null ? null : "Valid") : "Invalid",
                ["quarantineReason"] = q?.Reason.ToString(),
                ["quarantineSummary"] = q?.Summary,
                ["reconciliation"] = r is null ? "Not processed" : r.State switch
                {
                    RecordState.Synced => doc is null ? "Missing in SAP" : doc["ExternalReference"]?.GetValue<string>() == id ? "Matched" : "Linked to another reference",
                    RecordState.Quarantined => "Explained (quarantined)",
                    RecordState.Ignored => "Explained (not approved)",
                    RecordState.Dismissed => "Explained (resolved by operator)",
                    _ => "Pending",
                },
                ["lastError"] = r?.LastError,
            });
        }
        return rows;
    }

    private static string OutcomeOf(RequisitionRecord? r, List<RunEvent>? events)
    {
        if (r is null) return "Not processed";
        return r.State switch
        {
            RecordState.Synced when events?.Any(e => e.Kind is RunEventKind.AlreadyInSap or RunEventKind.InDoubtResolved) == true
                                  && events.All(e => e.Kind != RunEventKind.Created) => "Linked",
            RecordState.Synced when events?.Any(e => e.Kind == RunEventKind.Amended) == true => "Updated",
            RecordState.Synced => "Created",
            RecordState.Quarantined => "Quarantined",
            RecordState.Ignored => "Skipped",
            RecordState.Dismissed => "Resolved by operator",
            RecordState.RetryPending => "Retrying",
            RecordState.InDoubt => "Unresolved (verifying in SAP)",
            RecordState.Sending => "Unresolved (mid-send)",
            _ => "Pending",
        };
    }

    public async Task<JsonObject?> RequisitionDetailAsync(string id)
    {
        var source = await Lab.SourceRecordAsync(id);
        await using var db = Lab.Db();
        await db.Database.EnsureCreatedAsync();
        var record = await db.Requisitions.AsNoTracking().SingleOrDefaultAsync(r => r.SourceId == id);
        if (source is null && record is null) return null;
        var events = await db.RunEvents.AsNoTracking().Where(e => e.SourceKey == id).OrderBy(e => e.Id).ToListAsync();
        var quarantine = await db.Quarantine.AsNoTracking().Where(q => q.SourceKey == id).OrderBy(q => q.Id).ToListAsync();
        var history = await db.StatusHistory.AsNoTracking().Where(h => h.SourceId == id).OrderBy(h => h.Id).ToListAsync();
        var sapDocs = await Lab.SapDocumentsForAsync(id);

        var validation = source is null ? null : Validate(source);
        Requisition? canonical = null;
        if (record?.CanonicalJson is { } cj) canonical = JsonSerializer.Deserialize<Requisition>(cj, Json);
        var sapPayload = canonical is null ? null : JsonSerializer.SerializeToNode(SapMapper.ToSap(canonical), Procurement.Sync.Clients.SapJson.Options);
        var http = Lab.Capture.Exchanges.Where(e => e.Url.Contains(id, StringComparison.Ordinal) || Uri.UnescapeDataString(e.Url).Contains(id, StringComparison.Ordinal)
                                                    || (e.RequestBody?.Contains($"\"{id}\"", StringComparison.Ordinal) ?? false))
            .OrderBy(e => e.Seq).TakeLast(200).ToList();

        return new JsonObject
        {
            ["id"] = id,
            ["source"] = source?.DeepClone(),
            ["canonical"] = canonical is null ? null : JsonSerializer.SerializeToNode(canonical, Json),
            ["sapPayload"] = sapPayload,
            ["sapDocuments"] = new JsonArray(sapDocs.Select(d => (JsonNode)d.DeepClone()).ToArray()),
            ["record"] = record is null ? null : JsonSerializer.SerializeToNode(record, Json),
            ["validation"] = validation,
            ["duplicateDecision"] = DuplicateDecision(events, record),
            ["timeline"] = new JsonArray(events.Select(e => (JsonNode)new JsonObject
            {
                ["at"] = e.At.ToString("O"), ["run"] = e.RunId, ["kind"] = e.Kind.ToString(), ["detail"] = e.Detail, ["sapResponses"] = e.SapResponses,
            }).ToArray()),
            ["quarantine"] = JsonSerializer.SerializeToNode(quarantine, Json),
            ["statusHistory"] = JsonSerializer.SerializeToNode(history, Json),
            ["http"] = JsonSerializer.SerializeToNode(http, Json),
        };
    }

    /// <summary>Which of the three duplicate protections decided this record's fate.</summary>
    private static JsonObject DuplicateDecision(List<RunEvent> events, RequisitionRecord? record)
    {
        string layer, detail;
        if (events.Any(e => e.Kind == RunEventKind.Conflict && e.Detail?.Contains("none linked") == true))
            (layer, detail) = ("Lookup (layer 3): ambiguous", "SAP held several documents for this reference: none linked, nothing created");
        else if (events.LastOrDefault(e => e.Kind == RunEventKind.AlreadyInSap) is { } already && already.Detail?.Contains("idempotent replay") == true)
            (layer, detail) = ("Idempotency-Key (layer 2)", already.Detail!);
        else if (events.LastOrDefault(e => e.Kind is RunEventKind.AlreadyInSap or RunEventKind.InDoubtResolved) is { } found)
            (layer, detail) = ("Lookup before create (layer 3)", found.Detail ?? "existing SAP document linked");
        else if (events.Any(e => e.Kind == RunEventKind.Amended))
            (layer, detail) = ("Local mapping (layer 1)", "Already linked: changes were sent as an update, never a second create");
        else if (events.Any(e => e.Kind == RunEventKind.Created))
            (layer, detail) = ("No existing document", "Mapping empty, lookup found nothing: created once with a deterministic Idempotency-Key");
        else
            (layer, detail) = ("Not reached", record?.State.ToString() ?? "not processed");
        return new JsonObject { ["layer"] = layer, ["detail"] = detail, ["created"] = events.Count(e => e.Kind == RunEventKind.Created) };
    }

    /// <summary>Runs the real parser and business rules on a record without storing anything.</summary>
    public JsonObject Validate(JsonNode record)
    {
        using var doc = JsonDocument.Parse(record.ToJsonString());
        var envelope = SourceRecordParser.ReadEnvelope(doc.RootElement);
        var (requisition, problems) = SourceRecordParser.Parse(doc.RootElement, envelope);
        if (requisition is not null) problems = Lab.Worker.Services.GetRequiredService<RequisitionValidator>().Check(requisition);
        return new JsonObject
        {
            ["approved"] = envelope.Status == "Approved",
            ["status"] = envelope.Status,
            ["valid"] = problems.Count == 0,
            ["eligible"] = problems.Count == 0 && envelope.Status == "Approved",
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode)new JsonObject
            {
                ["reason"] = p.Reason.ToString(), ["field"] = p.Field, ["message"] = p.Message, ["value"] = ValueAt(record, p.Field),
            }).ToArray()),
            ["sapPayload"] = requisition is null ? null : JsonSerializer.SerializeToNode(SapMapper.ToSap(requisition), Procurement.Sync.Clients.SapJson.Options),
        };
    }

    public static JsonNode? ValueAt(JsonNode? root, string field)
    {
        if (root is null || string.IsNullOrEmpty(field) || field == "$") return null;
        JsonNode? current = root;
        foreach (var part in field.Replace("]", "").Split('.'))
        {
            var bracket = part.IndexOf('[');
            var name = bracket >= 0 ? part[..bracket] : part;
            if (current is JsonObject o)
            {
                var match = o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                current = match.Value;
            }
            else return null;
            if (bracket >= 0 && current is JsonArray a && int.TryParse(part[(bracket + 1)..], out var i)) current = i < a.Count ? a[i] : null;
        }
        return current?.DeepClone();
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask DisposeAsync()
    {
        StopSchedule();
        _current?.Cancel();
        await Lab.DisposeAsync();
    }
}
