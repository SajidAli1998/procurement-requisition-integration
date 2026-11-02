using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Procurement.Sync;
using Procurement.Sync.Clients;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Store;

namespace Procurement.ScenarioTests.Infrastructure;

/// <summary>
/// Hosts both mocks in-process and wires the real sync services to them. The resilience pipeline is the
/// production one; only waits are scaled down so retry and Retry-After behaviour runs in milliseconds.
/// </summary>
public sealed class SyncHarness : IAsyncDisposable
{
    public const string SapBase = "/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/";

    private readonly Dictionary<string, string?> _overrides;
    private SqliteConnection _keepAlive = null!;
    private ServiceProvider _services = null!;
    private AssistantApiFactory? _assistant;

    public string ConnectionString { get; private set; } = "";
    public string ReportDirectory { get; } = Path.Combine(Path.GetTempPath(), "procsync-tests", Guid.NewGuid().ToString("N"));

    public SourceApiFactory Source { get; } = new();
    public SapApiFactory Sap { get; } = new();
    public HttpClient SourceAdmin { get; }
    public HttpClient SapAdmin { get; }

    public SyncHarness(Dictionary<string, string?>? overrides = null)
    {
        _overrides = overrides ?? [];
        SourceAdmin = Source.CreateClient();
        SapAdmin = Sap.CreateClient();
        BuildServices();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Replaces the integration's database with an empty one, as if local state were lost.</summary>
    public async Task WipeLocalStateAsync()
    {
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
        BuildServices();
    }

    private void BuildServices()
    {
        var db = $"Data Source=sync-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        ConnectionString = db;
        _keepAlive = new SqliteConnection(db);
        _keepAlive.Open();

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:SyncStore"] = db,
            ["Sync:SourceBaseUrl"] = "http://localhost/",
            ["Sync:SapBaseUrl"] = "http://localhost" + SapBase,
            ["Sync:PageSize"] = "50",
            ["Sync:Parallelism"] = "4",
            ["Sync:MaxDispatchAttempts"] = "5",
            ["Sync:RetryPendingBaseDelay"] = "00:00:00",
            ["Sync:InDoubtRecheckDelay"] = "00:00:00",
            ["Sync:Resilience:DelayScale"] = "0.001",
            ["Sync:Resilience:PermitsPerSecond"] = "0",
            ["Sync:Resilience:AttemptTimeout"] = "00:00:02",
            ["Sync:Resilience:TotalTimeout"] = "00:00:10",
            ["Sync:Resilience:BreakerMinimumThroughput"] = "5",
            ["Sync:Resilience:BreakerSamplingDuration"] = "00:00:05",
            ["Sync:Resilience:BreakerBreakDuration"] = "00:00:01",
            ["Sync:ReportDirectory"] = ReportDirectory,
        };
        foreach (var (k, v) in _overrides) settings[k] = v;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddProcurementSync(configuration);
        services.AddHttpClient(nameof(SourceClient)).ConfigurePrimaryHttpMessageHandler(() => Source.Server.CreateHandler());
        services.AddHttpClient(nameof(SapClient)).ConfigurePrimaryHttpMessageHandler(() => Sap.Server.CreateHandler());
        _services = services.BuildServiceProvider();
    }

    public IServiceProvider Services => _services;

    /// <summary>The assistant API, wired to the same source mock and integration store.</summary>
    public AssistantApiFactory Assistant => _assistant ??= new AssistantApiFactory(Source, ConnectionString);

    public Task<RunResult> RunAsync() => _services.GetRequiredService<SyncRunner>().RunAsync("test", Ct);

    /// <summary>Runs until nothing is left waiting for retry (bounded), as a scheduler would over time.</summary>
    public async Task<List<RunResult>> RunUntilSettledAsync(int maxRuns = 5)
    {
        var results = new List<RunResult>();
        for (var i = 0; i < maxRuns; i++)
        {
            var result = await RunAsync();
            results.Add(result);
            if (result.Pending == 0) break;
            // A real scheduler runs minutes apart; give an open circuit breaker time to half-open.
            if (result.Message?.Contains("circuit breaker") == true) await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);
        }
        return results;
    }

    public SyncDbContext Db() => _services.GetRequiredService<IDbContextFactory<SyncDbContext>>().CreateDbContext();

    public async Task<(int Total, int Duplicates)> SapStatsAsync()
    {
        var stats = (await SapAdmin.GetFromJsonAsync<JsonObject>("/_admin/stats", Ct))!;
        return (stats["total"]!.GetValue<int>(), stats["duplicateExternalReferences"]!.AsArray().Count);
    }

    public async Task<List<(string Key, string Status, string? Defect)>> ManifestAsync()
    {
        var manifest = (await SourceAdmin.GetFromJsonAsync<JsonArray>("/_admin/seed-manifest", Ct))!;
        return manifest.Select(m => (m!["key"]!.GetValue<string>(), m["status"]!.GetValue<string>(), m["defect"]?.GetValue<string>())).ToList();
    }

    public Task ScriptSapAsync(params object[] faults) => SapAdmin.PostAsJsonAsync("/_admin/faults/script", faults, Ct);
    public Task ScriptSourceAsync(params object[] faults) => SourceAdmin.PostAsJsonAsync("/_admin/faults/script", faults, Ct);
    public Task SapFaultsAsync(object settings) => SapAdmin.PutAsJsonAsync("/_admin/faults", settings, Ct);
    public Task SourceFaultsAsync(object settings) => SourceAdmin.PutAsJsonAsync("/_admin/faults", settings, Ct);
    public Task ClearSapFaultsAsync() => SapAdmin.DeleteAsync("/_admin/faults", Ct);

    public async Task EditSourceAsync(string id, object patch) =>
        (await SourceAdmin.PostAsJsonAsync($"/_admin/requisitions/{id}/edit", patch, Ct)).EnsureSuccessStatusCode();

    public async ValueTask DisposeAsync()
    {
        if (_assistant is not null) await _assistant.DisposeAsync();
        if (Directory.Exists(ReportDirectory)) Directory.Delete(ReportDirectory, recursive: true);
        await _services.DisposeAsync();
        await _keepAlive.DisposeAsync();
        SourceAdmin.Dispose();
        SapAdmin.Dispose();
        await Source.DisposeAsync();
        await Sap.DisposeAsync();
    }
}
