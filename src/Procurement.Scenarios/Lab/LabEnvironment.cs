using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mocks.Shared.Faults;
using Procurement.Sync;
using Procurement.Sync.Clients;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;

namespace Procurement.Scenarios.Lab;

public sealed record LabOptions
{
    /// <summary>Workbook (the client's 120 records), Generated (the 250-record stress set) or Empty.</summary>
    public string Dataset { get; init; } = "Empty";

    public int Seed { get; init; } = 42;
    public int Count { get; init; } = 250;

    /// <summary>Keys without a section prefix go under "Sync:".</summary>
    public Dictionary<string, string?> Sync { get; init; } = [];

    public Dictionary<string, string?> Sap { get; init; } = [];

    public Dictionary<string, string?> Assistant { get; init; } = [];

    /// <summary>Shrinks every retry and Retry-After wait (x0.001) so failure scenarios run in milliseconds.</summary>
    public bool FastWaits { get; init; } = true;

    public bool CaptureHttp { get; init; } = true;
}

public enum RunOutcomeKind { Completed, CompletedWithIssues, Skipped, Crashed, Failed }

public sealed record RunOutcome(RunOutcomeKind Kind, RunResult? Result, string? Error, double DurationMs, string Worker)
{
    public int Count(RunEventKind kind) => Result?.Count(kind) ?? 0;
}

/// <summary>The whole system in memory with its own SQLite file; each scenario gets a fresh one so destructive scenarios stay contained.</summary>
public sealed class LabEnvironment : IAsyncDisposable
{
    public const string SapBase = "/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/";

    private readonly List<LabWorker> _workers = [];
    private readonly List<string> _adminSapNumbers = [];
    private AssistantHost? _assistant;

    public LabEnvironment(LabOptions? options = null)
    {
        Options = options ?? new LabOptions();
        Directory.CreateDirectory(Folder);
        DbPath = Path.Combine(Folder, "lab.db");
        Capture.Enabled = Options.CaptureHttp;
        var mockDataset = Options.Dataset == "Empty" ? "Generated" : Options.Dataset;
        Source = new SourceHost(Options.Dataset, new Dictionary<string, string?> { ["Seed:Value"] = Options.Seed.ToString(), ["Seed:Count"] = Options.Count.ToString() });
        Sap = new SapHost(mockDataset, Options.Sap);
        SourceAdmin = Source.CreateDefaultClient(new CaptureHandler(Capture, "setup→source"));
        SapAdmin = Sap.CreateDefaultClient(new CaptureHandler(Capture, "setup→sap"));
        Crash = new CrashSwitch();
        DbFaults = new DbFaults(Crash);
        Worker = AddWorker("integration");
    }

    public LabOptions Options { get; }
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "procurement-lab", Guid.NewGuid().ToString("N"));
    public string DbPath { get; }
    // Keeps the production default busy timeout (30 s): the lab must not be more fragile than the real thing.
    public string ConnectionString => $"Data Source={DbPath}";
    public string ReportDirectory => Path.Combine(Folder, "reports");

    public SourceHost Source { get; }
    public SapHost Sap { get; }
    public HttpClient SourceAdmin { get; }
    public HttpClient SapAdmin { get; }
    public HttpCapture Capture { get; } = new();
    public CrashSwitch Crash { get; }
    public DbFaults DbFaults { get; }
    public LabHooks Hooks { get; } = new();

    /// <summary>The integration process; scenarios with two workers add more with <see cref="AddWorker"/>.</summary>
    public LabWorker Worker { get; private set; }

    public IReadOnlyList<LabWorker> Workers => _workers;

    /// <summary>Documents a scenario created directly in SAP, not through the integration.</summary>
    public IReadOnlyList<string> AdminSapNumbers => _adminSapNumbers;

    public AssistantHost Assistant => _assistant ??= new AssistantHost(Source, ConnectionString, Options.Dataset == "Empty" ? "Generated" : Options.Dataset, Options.Assistant, Capture);

    public LabWorker AddWorker(string name, Dictionary<string, string?>? overrides = null)
    {
        var worker = new LabWorker(this, name, overrides ?? []);
        _workers.Add(worker);
        return worker;
    }

    public Task<RunOutcome> RunAsync(string trigger = "lab", CancellationToken ct = default) => Worker.RunAsync(trigger, ct);

    /// <summary>Runs until nothing is waiting for retry (bounded), as a scheduler would over time.</summary>
    public async Task<List<RunOutcome>> RunUntilSettledAsync(int maxRuns = 6, CancellationToken ct = default)
    {
        var outcomes = new List<RunOutcome>();
        for (var i = 0; i < maxRuns; i++)
        {
            var outcome = await RunAsync($"lab settle {i + 1}", ct);
            outcomes.Add(outcome);
            if (outcome.Kind is RunOutcomeKind.Crashed or RunOutcomeKind.Failed) break;
            if (outcome.Result is { Pending: 0 }) break;
            // A real scheduler runs minutes apart; give an open circuit breaker time to half-open.
            if (outcome.Result?.Message?.Contains("circuit breaker") == true) await Task.Delay(TimeSpan.FromSeconds(1.2), ct);
            else await Task.Delay(50, ct);
        }
        return outcomes;
    }

    public async Task RestartAsync()
    {
        Crash.Restart();
        var name = Worker.Name;
        var overrides = Worker.Overrides;
        _workers.Remove(Worker);
        await Worker.DisposeAsync();
        Worker = AddWorker(name, overrides);
    }

    /// <summary>Deletes the integration database, as after a failed disk or a bad restore.</summary>
    public async Task WipeLocalStateAsync()
    {
        foreach (var w in _workers) await w.DisposeAsync();
        _workers.Clear();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
        Crash.Restart();
        Worker = AddWorker("integration");
    }

    public SyncDbContext Db() => Worker.Db();

    public async Task ResetSourceAsync(string dataset, int? seed = null, int? count = null) =>
        (await SourceAdmin.PostAsync($"/_admin/reset?dataset={dataset}&seed={seed ?? Options.Seed}&count={count ?? Options.Count}", null)).EnsureSuccessStatusCode();

    /// <summary>Stores the record exactly as given, valid or not; a version forces its version number.</summary>
    public async Task<JsonObject> PutSourceAsync(string id, JsonObject body, long? version = null)
    {
        var response = await SourceAdmin.PutAsJsonAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}/raw{(version is null ? "" : $"?version={version}")}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    public async Task EditSourceAsync(string id, object mergePatch) =>
        (await SourceAdmin.PostAsJsonAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}/edit", mergePatch)).EnsureSuccessStatusCode();

    public async Task DeleteSourceAsync(string id) =>
        (await SourceAdmin.DeleteAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}")).EnsureSuccessStatusCode();

    public async Task HideFromListAsync(string id, bool hidden = true) =>
        (await SourceAdmin.PostAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}/hide-from-list?hidden={hidden}", null)).EnsureSuccessStatusCode();

    public async Task<JsonArray> SourceSnapshotAsync() => (await SourceAdmin.GetFromJsonAsync<JsonArray>("/_admin/requisitions"))!;

    public async Task<JsonObject?> SourceRecordAsync(string id)
    {
        var response = await SourceAdmin.GetAsync($"/api/requisitions/{Uri.EscapeDataString(id)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonObject>() : null;
    }

    public Task ScriptSourceAsync(params ScriptedFault[] faults) => PostOk(SourceAdmin, "/_admin/faults/script", faults);
    public Task SourceFaultsAsync(FaultSettings settings) => PutOk(SourceAdmin, "/_admin/faults", settings);

    public Task ScriptSapAsync(params ScriptedFault[] faults) => PostOk(SapAdmin, "/_admin/faults/script", faults);
    public Task SapFaultsAsync(FaultSettings settings) => PutOk(SapAdmin, "/_admin/faults", settings);

    public async Task ClearFaultsAsync()
    {
        (await SapAdmin.DeleteAsync("/_admin/faults")).EnsureSuccessStatusCode();
        (await SourceAdmin.DeleteAsync("/_admin/faults")).EnsureSuccessStatusCode();
        DbFaults.Clear();
    }

    public async Task<JsonObject> SapFaultStateAsync() => (await SapAdmin.GetFromJsonAsync<JsonObject>("/_admin/faults"))!;

    /// <summary>Creates a document directly in SAP, as a buyer in the SAP GUI would.</summary>
    public async Task<string> InsertSapAsync(string reference, long version = 1, string? number = null, string? lifecycle = null,
        decimal quantity = 1, decimal price = 100, string plant = "1100", string company = "1000", string currency = "USD")
    {
        var query = new List<string>();
        if (number is not null) query.Add("number=" + number);
        if (lifecycle is not null) query.Add("lifecycle=" + lifecycle);
        var input = new Dictionary<string, object?>
        {
            ["PurReqnDescription"] = $"Created in SAP for {reference}",
            ["ExternalReference"] = reference,
            ["ExternalVersion"] = version,
            ["to_PurchaseReqnItem"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["PurchaseRequisitionItem"] = "00010", ["PurchaseRequisitionItemText"] = "Item", ["RequestedQuantity"] = quantity,
                    ["BaseUnit"] = "EA", ["PurchaseRequisitionPrice"] = price, ["PurReqnItemCurrency"] = currency, ["Plant"] = plant,
                    ["CompanyCode"] = company, ["PurchasingGroup"] = "001", ["DeliveryDate"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
                },
            },
        };
        var response = await SapAdmin.PostAsJsonAsync("/_admin/requisitions" + (query.Count > 0 ? "?" + string.Join('&', query) : ""), input);
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var made = created["PurchaseRequisition"]!.GetValue<string>();
        _adminSapNumbers.Add(made);
        return made;
    }

    public async Task SetSapLifecycleAsync(string number, string lifecycle) =>
        (await SapAdmin.PostAsJsonAsync($"/_admin/requisitions/{number}/lifecycle", new { Lifecycle = lifecycle })).EnsureSuccessStatusCode();

    public async Task SetSapRawStatusAsync(string number, string? processing, string? release) =>
        (await SapAdmin.PostAsJsonAsync($"/_admin/requisitions/{number}/raw-status", new { ProcessingStatus = processing, ReleaseStatus = release })).EnsureSuccessStatusCode();

    public async Task DeleteSapAsync(string number) => (await SapAdmin.DeleteAsync($"/_admin/requisitions/{number}")).EnsureSuccessStatusCode();
    public async Task TouchSapAsync(string number) => (await SapAdmin.PostAsync($"/_admin/requisitions/{number}/touch", null)).EnsureSuccessStatusCode();
    public async Task AdvanceSapAsync(int rejectEvery = 7) => (await SapAdmin.PostAsync($"/_admin/advance?rejectEvery={rejectEvery}", null)).EnsureSuccessStatusCode();
    public async Task EvictSapIdempotencyAsync() => (await SapAdmin.PostAsync("/_admin/idempotency/evict", null)).EnsureSuccessStatusCode();

    public async Task<List<JsonObject>> SapDocumentsAsync() =>
        (await SapAdmin.GetFromJsonAsync<JsonArray>("/_admin/requisitions"))!.Select(n => n!.AsObject()).ToList();

    public async Task<List<JsonObject>> SapDocumentsForAsync(string reference) =>
        (await SapDocumentsAsync()).Where(d => d["ExternalReference"]?.GetValue<string>() == reference).ToList();

    public async Task<(int Total, int DuplicateReferences)> SapStatsAsync()
    {
        var stats = (await SapAdmin.GetFromJsonAsync<JsonObject>("/_admin/stats"))!;
        return (stats["total"]!.GetValue<int>(), stats["duplicateExternalReferences"]!.AsArray().Count);
    }

    public async Task<RequisitionRecord?> RecordAsync(string id)
    {
        await using var db = Db();
        return await db.Requisitions.AsNoTracking().SingleOrDefaultAsync(r => r.SourceId == id);
    }

    public async Task<List<QuarantineEntry>> OpenQuarantineAsync(string? id = null)
    {
        await using var db = Db();
        return await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null && (id == null || q.SourceKey == id)).ToListAsync();
    }

    public async Task<List<RunEvent>> EventsAsync(string? id = null)
    {
        await using var db = Db();
        return await db.RunEvents.AsNoTracking().Where(e => id == null || e.SourceKey == id).OrderBy(e => e.Id).ToListAsync();
    }

    public async Task<ReconciliationReport> ReconcileAsync()
    {
        await using var scope = Worker.Services.CreateAsyncScope();
        var builder = scope.ServiceProvider.GetRequiredService<ReportBuilder>();
        return await builder.BuildAsync("lab-reconcile-" + Guid.NewGuid().ToString("N")[..6], "reconcile", DateTime.UtcNow, "Reconcile", null, CancellationToken.None);
    }

    public async Task<object> DbSnapshotAsync()
    {
        await using var db = Db();
        try
        {
            var records = await db.Requisitions.AsNoTracking().OrderBy(r => r.SourceId)
                .Select(r => new { r.SourceId, State = r.State.ToString(), r.SapNumber, r.SourceVersion, r.SyncedSourceVersion, ErpStatus = r.ErpStatus.ToString(), r.Attempts, r.LastError })
                .ToListAsync();
            var quarantine = await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null)
                .Select(q => new { q.SourceKey, Reason = q.Reason.ToString(), q.Summary }).ToListAsync();
            var outbox = await db.Outbox.AsNoTracking().CountAsync(m => m.ProcessedAt == null);
            var checkpoint = await db.Checkpoints.AsNoTracking().Select(c => new { c.Stream, c.Cursor, c.UpdatedAt }).ToListAsync();
            var runs = await db.Runs.AsNoTracking().OrderBy(r => r.StartedAt).Select(r => new { r.Id, Status = r.Status.ToString(), r.Message }).ToListAsync();
            var leases = await db.Leases.AsNoTracking().Select(l => new { l.Name, l.Holder, l.ExpiresAt }).ToListAsync();
            return new { records, openQuarantine = quarantine, pendingOutbox = outbox, checkpoint, runs, leases };
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            return new { unavailable = ex.Message };
        }
    }

    private static async Task PostOk(HttpClient client, string url, object body) => (await client.PostAsJsonAsync(url, body)).EnsureSuccessStatusCode();
    private static async Task PutOk(HttpClient client, string url, object body) => (await client.PutAsJsonAsync(url, body)).EnsureSuccessStatusCode();

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _workers) await w.DisposeAsync();
        if (_assistant is not null) await _assistant.DisposeAsync();
        Crash.Dispose();
        SourceAdmin.Dispose();
        SapAdmin.Dispose();
        await Source.DisposeAsync();
        await Sap.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class LabIsolationException(string message) : Exception(message);

/// <summary>One integration process with its own services, sharing the lab's database file.</summary>
public sealed class LabWorker : IAsyncDisposable
{
    private readonly LabEnvironment _lab;
    private readonly ServiceProvider _services;

    internal LabWorker(LabEnvironment lab, string name, Dictionary<string, string?> overrides)
    {
        _lab = lab;
        Name = name;
        Overrides = overrides;
        var fast = lab.Options.FastWaits;
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SyncStore"] = lab.ConnectionString,
            ["Sync:SourceBaseUrl"] = "http://source.lab/",
            ["Sync:SapBaseUrl"] = "http://sap.lab" + LabEnvironment.SapBase,
            ["Sync:ClientId"] = "procsync-" + name,
            ["Sync:PageSize"] = "50",
            ["Sync:Parallelism"] = "4",
            ["Sync:MaxDispatchAttempts"] = "5",
            ["Sync:RetryPendingBaseDelay"] = "00:00:00",
            ["Sync:InDoubtRecheckDelay"] = "00:00:00",
            ["Sync:Resilience:DelayScale"] = fast ? "0.001" : "1",
            ["Sync:Resilience:PermitsPerSecond"] = "0",
            ["Sync:Resilience:AttemptTimeout"] = "00:00:02",
            ["Sync:Resilience:TotalTimeout"] = "00:00:10",
            ["Sync:Resilience:BreakerMinimumThroughput"] = "5",
            ["Sync:Resilience:BreakerSamplingDuration"] = "00:00:05",
            ["Sync:Resilience:BreakerBreakDuration"] = "00:00:01",
            ["Sync:ReportDirectory"] = lab.ReportDirectory,
        };
        foreach (var (k, v) in lab.Options.Sync) settings[k.StartsWith("Sync:") || k.StartsWith("ConnectionStrings:") || k.StartsWith("Store:") ? k : "Sync:" + k] = v;
        foreach (var (k, v) in overrides) settings[k.StartsWith("Sync:") || k.StartsWith("ConnectionStrings:") || k.StartsWith("Store:") ? k : "Sync:" + k] = v;

        // The lab refuses to point the integration anywhere but its own in-memory hosts (invariant 12).
        foreach (var key in new[] { "Sync:SourceBaseUrl", "Sync:SapBaseUrl" })
        {
            if (!Uri.TryCreate(settings[key], UriKind.Absolute, out var url) || url.Host is not ("source.lab" or "sap.lab"))
                throw new LabIsolationException($"{key}={settings[key]} is outside the lab; only http://source.lab/ and http://sap.lab/ are allowed");
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddProcurementSync(configuration);
        services.ConfigureDbContext<SyncDbContext>(o => o.AddInterceptors(lab.DbFaults));
        services.AddHttpClient(nameof(SourceClient))
            .ConfigurePrimaryHttpMessageHandler(() => lab.Source.Server.CreateHandler())
            .AddHttpMessageHandler(() => new HookHandler(lab.Hooks, "source"))
            .AddHttpMessageHandler(() => new CaptureHandler(lab.Capture, $"{name}→source"));
        services.AddHttpClient(nameof(SapClient))
            .ConfigurePrimaryHttpMessageHandler(() => lab.Sap.Server.CreateHandler())
            .AddHttpMessageHandler(() => new HookHandler(lab.Hooks, "sap"))
            .AddHttpMessageHandler(() => new CrashHandler(lab.Crash))
            .AddHttpMessageHandler(() => new CaptureHandler(lab.Capture, $"{name}→sap"));
        _services = services.BuildServiceProvider();
    }

    public string Name { get; }
    internal Dictionary<string, string?> Overrides { get; }
    public IServiceProvider Services => _services;

    public SyncDbContext Db() => _services.GetRequiredService<IDbContextFactory<SyncDbContext>>().CreateDbContext();

    public async Task<RunOutcome> RunAsync(string trigger = "lab", CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lab.Crash.ProcessToken);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await _services.GetRequiredService<SyncRunner>().RunAsync(trigger, linked.Token);
            var kind = result.Skipped ? RunOutcomeKind.Skipped
                : result.Status == Procurement.Sync.Store.RunStatus.Completed ? RunOutcomeKind.Completed : RunOutcomeKind.CompletedWithIssues;
            return new RunOutcome(kind, result, result.Message, clock.Elapsed.TotalMilliseconds, Name);
        }
        catch (OperationCanceledException) when (_lab.Crash.ProcessToken.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new RunOutcome(RunOutcomeKind.Crashed, null, $"Process crashed: {_lab.Crash.FiredAt ?? "database fault"}", clock.Elapsed.TotalMilliseconds, Name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new RunOutcome(RunOutcomeKind.Failed, null, $"{ex.GetType().Name}: {ex.GetBaseException().Message}", clock.Elapsed.TotalMilliseconds, Name);
        }
    }

    public async Task<RunResult> ReplayAsync(string id, CancellationToken ct = default) =>
        await _services.GetRequiredService<SyncRunner>().ReplayAsync(id, ct);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
