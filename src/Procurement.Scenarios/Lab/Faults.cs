using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Procurement.Sync.Store;

namespace Procurement.Scenarios.Lab;

public sealed class SimulatedCrashException(string point) : Exception($"Simulated process crash: {point}")
{
    public string Point { get; } = point;
}

/// <summary>Cancels the run's token right after a matching SAP response arrives, so nothing after that point is saved.</summary>
public sealed class CrashSwitch : IDisposable
{
    private readonly Lock _gate = new();
    private Func<HttpRequestMessage, HttpResponseMessage, bool>? _trigger;
    private string _point = "";
    private CancellationTokenSource _process = new();

    public CancellationToken ProcessToken
    {
        get { lock (_gate) return _process.Token; }
    }

    public string? FiredAt { get; private set; }

    public string? Armed
    {
        get { lock (_gate) return _trigger is null ? null : _point; }
    }

    public void ArmAfterSapResponse(string point, Func<HttpRequestMessage, HttpResponseMessage, bool> trigger)
    {
        lock (_gate)
        {
            _trigger = trigger;
            _point = point;
            FiredAt = null;
        }
    }

    public void Restart()
    {
        lock (_gate)
        {
            _process.Dispose();
            _process = new CancellationTokenSource();
            _trigger = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _process.Dispose();
    }

    public void Kill(string point)
    {
        lock (_gate)
        {
            FiredAt = point;
            _process.Cancel();
        }
    }

    internal bool TryFire(HttpRequestMessage request, HttpResponseMessage response)
    {
        lock (_gate)
        {
            if (_trigger is null || !_trigger(request, response)) return false;
            _trigger = null;
            FiredAt = _point;
            _process.Cancel();
            return true;
        }
    }
}

public sealed class CrashHandler(CrashSwitch crash) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (crash.TryFire(request, response))
        {
            response.Dispose();
            throw new OperationCanceledException(new SimulatedCrashException(crash.FiredAt!).Message, crash.ProcessToken);
        }
        return response;
    }
}

public enum DbFaultKind { SaveFails, DatabaseLocked, Crash }

/// <summary>A rule matches a save by what it is about to write (e.g. a record becoming Synced), so a scenario can fail exactly that write.</summary>
public sealed class DbFaults : SaveChangesInterceptor
{
    private sealed record Rule(string Name, Func<DbContext, bool> Match, DbFaultKind Kind, int Remaining);

    private readonly Lock _gate = new();
    private readonly List<Rule> _rules = [];
    private readonly CrashSwitch _crash;

    public DbFaults(CrashSwitch crash) => _crash = crash;

    public List<string> Fired { get; } = [];

    public IReadOnlyList<string> Pending
    {
        get { lock (_gate) return _rules.Select(r => $"{r.Kind}: {r.Name} ({r.Remaining} left)").ToList(); }
    }

    public void Add(string name, Func<DbContext, bool> match, DbFaultKind kind, int times = 1)
    {
        lock (_gate) _rules.Add(new Rule(name, match, kind, times));
    }

    public void Clear()
    {
        lock (_gate) _rules.Clear();
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db) Check(db);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db) Check(db);
        return base.SavingChanges(eventData, result);
    }

    private void Check(DbContext db)
    {
        Rule? hit = null;
        lock (_gate)
        {
            for (var i = 0; i < _rules.Count; i++)
            {
                if (!_rules[i].Match(db)) continue;
                hit = _rules[i];
                if (hit.Remaining <= 1) _rules.RemoveAt(i);
                else _rules[i] = hit with { Remaining = hit.Remaining - 1 };
                break;
            }
            if (hit is not null) Fired.Add(hit.Name);
        }
        if (hit is null) return;
        switch (hit.Kind)
        {
            case DbFaultKind.DatabaseLocked:
                throw new DbUpdateException($"Injected: {hit.Name}", new SqliteException("database is locked", 5));
            case DbFaultKind.Crash:
                _crash.Kill(hit.Name);
                throw new OperationCanceledException($"Simulated process crash: {hit.Name}", _crash.ProcessToken);
            default:
                throw new DbUpdateException($"Injected: {hit.Name}");
        }
    }

    /// <summary>The mapping write after SAP success: a requisition becoming Synced with a SAP number.</summary>
    public static bool SavesMapping(DbContext db) =>
        db.ChangeTracker.Entries<RequisitionRecord>().Any(e => e.State == EntityState.Modified && e.Entity.State == RecordState.Synced
            && e.Property(r => r.SapNumber).IsModified && e.Entity.SapNumber is not null);

    public static bool SavesCheckpoint(DbContext db) =>
        db.ChangeTracker.Entries<Checkpoint>().Any(e => e.State is EntityState.Added or EntityState.Modified);

    public static bool SavesSending(DbContext db) =>
        db.ChangeTracker.Entries<RequisitionRecord>().Any(e => e.State == EntityState.Modified && e.Entity.State == RecordState.Sending);
}

/// <summary>Holds an exclusive lock on the SQLite file, as another process stuck in a long transaction would.</summary>
public sealed class DatabaseLock : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbTransaction _tx;

    private DatabaseLock(SqliteConnection connection, DbTransaction tx)
    {
        _connection = connection;
        _tx = tx;
    }

    public static async Task<DatabaseLock> AcquireAsync(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Default Timeout=1");
        await connection.OpenAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            await cmd.ExecuteNonQueryAsync();
        }
        var tx = await connection.BeginTransactionAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "PRAGMA locking_mode=EXCLUSIVE; CREATE TABLE IF NOT EXISTS __lab_lock (x INTEGER); INSERT INTO __lab_lock VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        }
        return new DatabaseLock(connection, tx);
    }

    public async ValueTask DisposeAsync()
    {
        await _tx.RollbackAsync();
        await _tx.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>Runs an action at an exact moment inside a run (e.g. just before the SAP create goes out), which makes races deterministic.</summary>
public sealed class LabHooks
{
    private sealed record Hook(string Target, Func<HttpRequestMessage, bool> Match, Func<Task> Action, int Remaining);

    private readonly Lock _gate = new();
    private readonly List<Hook> _hooks = [];

    public List<string> Fired { get; } = [];

    /// <summary>Target is "sap" or "source": the outgoing calls to watch.</summary>
    public void Before(string target, Func<HttpRequestMessage, bool> match, Func<Task> action, int times = 1)
    {
        lock (_gate) _hooks.Add(new Hook(target, match, action, times));
    }

    public static bool IsSapCreate(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("A_PurchaseRequisitionHeader", StringComparison.Ordinal);
    public static bool IsSapLookup(HttpRequestMessage r) => r.Method == HttpMethod.Get && r.RequestUri!.Query.Contains("ExternalReference", StringComparison.Ordinal);
    public static bool IsSourceList(HttpRequestMessage r) => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/api/requisitions", StringComparison.Ordinal);
    public static bool IsWriteback(HttpRequestMessage r) => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.EndsWith("/erp-status", StringComparison.Ordinal);

    internal async Task RunAsync(string target, HttpRequestMessage request)
    {
        Hook? hit = null;
        lock (_gate)
        {
            for (var i = 0; i < _hooks.Count; i++)
            {
                if (_hooks[i].Target != target || !_hooks[i].Match(request)) continue;
                hit = _hooks[i];
                if (hit.Remaining <= 1) _hooks.RemoveAt(i);
                else _hooks[i] = hit with { Remaining = hit.Remaining - 1 };
                Fired.Add($"{target}: {request.Method} {request.RequestUri?.PathAndQuery}");
                break;
            }
        }
        if (hit is not null) await hit.Action();
    }
}

public sealed class HookHandler(LabHooks hooks, string target) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await hooks.RunAsync(target, request);
        return await base.SendAsync(request, cancellationToken);
    }
}
