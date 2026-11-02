using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Procurement.Sync.Store;

public sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<RequisitionRecord> Requisitions => Set<RequisitionRecord>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<QuarantineEntry> Quarantine => Set<QuarantineEntry>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<SyncRun> Runs => Set<SyncRun>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<StatusHistoryEntry> StatusHistory => Set<StatusHistoryEntry>();
    public DbSet<Lease> Leases => Set<Lease>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Store every DateTime as UTC and read it back as UTC (SQLite drops DateTimeKind).
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RequisitionRecord>(e =>
        {
            e.ToTable("Requisitions");
            e.HasKey(x => x.SourceId);
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.Property(x => x.SapNumber).HasMaxLength(10);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.SapNumber);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("Outbox");
            e.Property(x => x.SourceId).HasMaxLength(64);
            // Staging the same version twice is a no-op, which keeps re-extraction idempotent.
            e.HasIndex(x => new { x.SourceId, x.SourceVersion, x.Kind }).IsUnique();
            e.HasIndex(x => x.ProcessedAt);
        });

        b.Entity<QuarantineEntry>(e =>
        {
            e.ToTable("Quarantine");
            e.Property(x => x.SourceKey).HasMaxLength(64);
            e.HasIndex(x => new { x.SourceKey, x.ResolvedAt });
        });

        b.Entity<Checkpoint>(e =>
        {
            e.ToTable("Checkpoints");
            e.HasKey(x => x.Stream);
            e.Property(x => x.Stream).HasMaxLength(64);
        });

        b.Entity<SyncRun>(e =>
        {
            e.ToTable("Runs");
            e.Property(x => x.Id).HasMaxLength(40);
        });

        b.Entity<RunEvent>(e =>
        {
            e.ToTable("RunEvents");
            e.Property(x => x.RunId).HasMaxLength(40);
            e.Property(x => x.SourceKey).HasMaxLength(64);
            e.HasIndex(x => new { x.RunId, x.Kind });
        });

        b.Entity<StatusHistoryEntry>(e =>
        {
            e.ToTable("StatusHistory");
            e.Property(x => x.SourceId).HasMaxLength(64);
            e.HasIndex(x => x.SourceId);
        });

        b.Entity<Lease>(e =>
        {
            e.ToTable("Leases");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Holder).HasMaxLength(100);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<RequisitionRecord>())
        {
            if (entry.State is EntityState.Modified or EntityState.Added) entry.Entity.Revision++;
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
