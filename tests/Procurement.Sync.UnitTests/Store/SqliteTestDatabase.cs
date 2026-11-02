using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Procurement.Sync.Store;

namespace Procurement.Sync.UnitTests.Store;

/// <summary>Shared in-memory SQLite database that lives as long as the test.</summary>
public sealed class SqliteTestDatabase : IDbContextFactory<SyncDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<SyncDbContext> _options;

    public SqliteTestDatabase()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<SyncDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public SyncDbContext CreateDbContext() => new(_options);

    public SqliteConnection Connection => _connection;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
