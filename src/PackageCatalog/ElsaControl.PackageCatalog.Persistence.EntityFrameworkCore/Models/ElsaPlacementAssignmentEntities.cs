using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;

/// <summary>
/// Control-owned placement assignment used for allowance counting
/// (ADR-0017 Amendment 1). Target payload stays off this row.
/// </summary>
internal sealed class ElsaPlacementAssignmentEntity
{
    public Guid Id { get; set; }
    public Guid InstanceId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Kind { get; set; } = "";
    public string? ShardClass { get; set; }
    public string State { get; set; } = "";
    public Guid? SupersedesAssignmentId { get; set; }
    public Guid? ShardId { get; set; }
    public string? PackageSetDigest { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Eligible Shared shard used only for the post-allowance capacity check.
/// Occupancy is the number of non-Retired assignments pointing at the shard.
/// </summary>
internal sealed class ElsaSharedShardEntity
{
    public Guid Id { get; set; }
    public string RegionCode { get; set; } = "";
    public string ShardClass { get; set; } = "";
    public int Capacity { get; set; }
}

internal sealed class ElsaPlacementAssignmentConfiguration : IEntityTypeConfiguration<ElsaPlacementAssignmentEntity>
{
    public void Configure(EntityTypeBuilder<ElsaPlacementAssignmentEntity> builder)
    {
        builder.ToTable("ElsaPlacementAssignments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ShardClass).HasMaxLength(32);
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.Property(x => x.PackageSetDigest).HasMaxLength(128);
        builder.Property(x => x.CreatedAt).HasConversion(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(x => new { x.OrganizationId, x.Kind, x.ShardClass, x.State });
        builder.HasIndex(x => new { x.InstanceId, x.State })
            .IsUnique()
            .HasFilter("State = 'Active'");
        builder.HasIndex(x => x.ShardId);
        builder.HasOne<ElsaInstanceEntity>()
            .WithMany()
            .HasForeignKey(x => x.InstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ElsaSharedShardConfiguration : IEntityTypeConfiguration<ElsaSharedShardEntity>
{
    public void Configure(EntityTypeBuilder<ElsaSharedShardEntity> builder)
    {
        builder.ToTable("ElsaSharedShards");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RegionCode).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ShardClass).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Capacity).IsRequired();
        builder.HasIndex(x => new { x.RegionCode, x.ShardClass });
    }
}
