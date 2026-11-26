using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

/// <summary>
/// Module-owned tables on Control's catalog SQL database, schema
/// <c>provisioning</c>. Not shared with catalog entities.
/// </summary>
public sealed class ProvisioningDbContext(DbContextOptions<ProvisioningDbContext> options) : DbContext(options)
{
    public const string SchemaName = "provisioning";

    internal DbSet<ProvisioningOperationEntity> Operations => Set<ProvisioningOperationEntity>();
    internal DbSet<ProvisioningEventEntity> Events => Set<ProvisioningEventEntity>();
    internal DbSet<ProvisioningLockEntity> Locks => Set<ProvisioningLockEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!Database.IsSqlite())
            modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<ProvisioningOperationEntity>(builder =>
        {
            builder.ToTable("Operations");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Command).HasMaxLength(32).IsRequired();
            builder.Property(x => x.State).HasMaxLength(32).IsRequired();
            builder.Property(x => x.ProviderId).HasMaxLength(64).IsRequired();
            builder.Property(x => x.StackKind).HasMaxLength(32).IsRequired();
            builder.Property(x => x.StackName).HasMaxLength(128).IsRequired();
            builder.Property(x => x.LockKey).HasMaxLength(192).IsRequired();
            builder.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            builder.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
            builder.Property(x => x.FailureCode).HasMaxLength(128);
            builder.Property(x => x.RequestJson).IsRequired();
            builder.HasIndex(x => x.IdempotencyKey).IsUnique();
            builder.HasIndex(x => new { x.State, x.AcceptedAt });
            builder.HasIndex(x => new { x.LockKey, x.State });
        });

        modelBuilder.Entity<ProvisioningEventEntity>(builder =>
        {
            builder.ToTable("Events");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).ValueGeneratedOnAdd();
            builder.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            builder.Property(x => x.ResourceKind).HasMaxLength(128).IsRequired();
            builder.Property(x => x.Message).HasMaxLength(512).IsRequired();
            builder.HasIndex(x => x.OperationId);
        });

        modelBuilder.Entity<ProvisioningLockEntity>(builder =>
        {
            builder.ToTable("Locks");
            builder.HasKey(x => x.LockKey);
            builder.Property(x => x.LockKey).HasMaxLength(192);
        });
    }
}
