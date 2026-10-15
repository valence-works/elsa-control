using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Persistence-neutral handoff metadata accepted by the EF adapter. The API
/// project maps its handoff contract to this record without making persistence
/// depend on the hosting layer.
/// </summary>
public sealed record ManagedElsaHandoffAuditRecord(
    string Action,
    string Jti,
    Guid? AccountId,
    Guid? OrganizationId,
    Guid? InstanceId,
    string? Audience,
    int? BindingVersion,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

/// <summary>
/// Relational storage for one-time handoff consumption and safe audit metadata.
/// </summary>
public sealed class EfCoreManagedElsaHandoffStore(
    CatalogDbContext dbContext,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ReplayRetentionAfterExpiry = TimeSpan.FromHours(24);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Consumes a JTI exactly once. The unique primary key insert is the atomic
    /// cross-process boundary; a duplicate key means another consumer won.
    /// </summary>
    public async ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        var normalizedExpiry = expiresAt.ToUniversalTime();
        var consumedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var retentionCutoff = consumedAt.Subtract(ReplayRetentionAfterExpiry);
        _ = await dbContext.ManagedElsaHandoffReplays
            .Where(x => x.ExpiresAt < retentionCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        if (consumedAt >= normalizedExpiry)
            consumedAt = normalizedExpiry.AddTicks(-1);

        var entity = new ManagedElsaHandoffReplayEntity
        {
            Jti = jti,
            ExpiresAt = normalizedExpiry,
            ConsumedAt = consumedAt
        };
        dbContext.ManagedElsaHandoffReplays.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            // Keep unrelated scoped work intact when a concurrent consumer wins.
            dbContext.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async ValueTask RecordAsync(
        ManagedElsaHandoffAuditRecord audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        dbContext.ManagedElsaHandoffAuditEvents.Add(new ManagedElsaHandoffAuditEventEntity
        {
            Id = Guid.NewGuid(),
            Action = audit.Action,
            Jti = audit.Jti,
            AccountId = audit.AccountId,
            OrganizationId = audit.OrganizationId,
            InstanceId = audit.InstanceId,
            Audience = audit.Audience,
            BindingVersion = audit.BindingVersion,
            CorrelationId = audit.CorrelationId,
            OccurredAt = audit.OccurredAt.ToUniversalTime()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Returns actions from the latest loaded key-ring snapshot: the most recent
    /// row whose action starts with <paramref name="activePrefix"/>, plus every
    /// row at that timestamp whose action starts with that prefix or
    /// <paramref name="previousPrefix"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLatestKeyRingSnapshotActionsAsync(
        string activePrefix,
        string previousPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPrefix);

        var latest = await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.Action.StartsWith(activePrefix))
            .OrderByDescending(x => x.OccurredAt)
            .Select(x => (DateTimeOffset?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
            return [];

        return await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.OccurredAt == latest.Value &&
                        (x.Action.StartsWith(activePrefix) || x.Action.StartsWith(previousPrefix)))
            .OrderBy(x => x.Action)
            .Select(x => x.Action)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListActionsStartingWithAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.Action.StartsWith(prefix))
            .OrderBy(x => x.OccurredAt)
            .Select(x => x.Action)
            .ToListAsync(cancellationToken);
    }

}

internal static class EfCoreDatabaseExceptionPolicy
{
    internal static bool IsSqlServerLifecycleReservationConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlServer &&
                IsSqlServerLifecycleReservationConflictNumber(sqlServer.Number))
                return true;
        }

        return false;
    }

    internal static bool IsSqlServerLifecycleReservationConflictNumber(int number) =>
        number is 1205 or 2601 or 2627 or 3960;

    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 })
                return true;
            if (current is SqlException { Number: 2601 or 2627 })
                return true;
        }

        return false;
    }

    internal static bool IsElsaInstanceSlugUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 } sqlite &&
                sqlite.Message.Contains(
                    "ElsaInstances.WorkspaceId, ElsaInstances.Slug",
                    StringComparison.Ordinal))
                return true;

            if (current is SqlException { Number: 2601 or 2627 } sqlServer &&
                sqlServer.Message.Contains(
                    "IX_ElsaInstances_WorkspaceId_Slug",
                    StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
