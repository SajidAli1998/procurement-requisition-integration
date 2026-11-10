using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Procurement.Sync.Store;

public static class StoreRegistration
{
    public static IServiceCollection AddSyncStore(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Store:Provider"] ?? "Sqlite";
        var connection = configuration.GetConnectionString("SyncStore") ?? "Data Source=procsync.db";
        services.AddDbContextFactory<SyncDbContext>(options =>
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
                options.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
            else
                options.UseSqlite(connection);
        });
        return services;
    }

    // A production deployment would ship EF migration bundles per provider instead of EnsureCreated.
    public static async Task EnsureSyncStoreAsync(this IDbContextFactory<SyncDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        if (db.Database.IsSqlite())
        {
            // WAL lets the status report read while a run is writing.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
    }
}
