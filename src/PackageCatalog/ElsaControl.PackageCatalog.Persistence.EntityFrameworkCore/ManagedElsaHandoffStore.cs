using System.Data;
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
    DateTimeOffset OccurredAt,
    Guid? KeyRingSnapshotId = null,
    Guid? KeyRingPreviousSnapshotId = null);

/// <summary>
/// One complete key-ring transition: retired diffs plus the currently loaded
/// active and previous key ids, written under a shared snapshot identity.
/// </summary>
public sealed record ManagedElsaHandoffKeyRingSnapshotWrite(
    string ActiveKeyId,
    IReadOnlyList<string> PreviousKeyIds,
    DateTimeOffset OccurredAt,
    string ActivePrefix,
    string PreviousPrefix,
    string RetiredPrefix,
    string RetiredRevokedSuffix);

/// <summary>
/// Relational storage for one-time handoff consumption and safe audit metadata.
/// </summary>
public sealed class EfCoreManagedElsaHandoffStore(
    CatalogDbContext dbContext,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ReplayRetentionAfterExpiry = TimeSpan.FromHours(24);
    private const int KeyRingSnapshotConflictAttempts = 8;
    /// <summary>
    /// Previous-snapshot sentinel for the first snapshot-id row, including the
    /// first new-style snapshot after legacy timestamp-grouped rows.
    /// </summary>
    public static readonly Guid KeyRingGenesisPreviousSnapshotId = Guid.Empty;
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
        dbContext.ManagedElsaHandoffAuditEvents.Add(ToEntity(audit));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Writes retired, active, and previous rows for one key-ring transition in
    /// a single transaction under a shared snapshot id. The diff is against the
    /// latest snapshot id (or the legacy timestamp-grouped set when no snapshot
    /// id exists). Concurrent writers serialize on the previous-snapshot unique
    /// index so a removed key id gets exactly one retired row.
    /// </summary>
    public async Task RecordKeyRingSnapshotAsync(
        ManagedElsaHandoffKeyRingSnapshotWrite snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ActiveKeyId);
        ArgumentNullException.ThrowIfNull(snapshot.PreviousKeyIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ActivePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.PreviousPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RetiredPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RetiredRevokedSuffix);

        DbUpdateException? lastConflict = null;
        for (var attempt = 0; attempt < KeyRingSnapshotConflictAttempts; attempt++)
        {
            try
            {
                await dbContext.ExecuteInTransactionAsync(
                    IsolationLevel.Serializable,
                    () => PersistKeyRingSnapshotAttemptAsync(snapshot, cancellationToken),
                    cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
            {
                lastConflict = exception;
            }
        }

        throw lastConflict!;
    }

    /// <summary>
    /// Returns actions from the latest loaded key-ring snapshot. Snapshot-id
    /// rows are grouped by snapshot identity (the chain tip). Legacy rows
    /// without a snapshot id still group by the latest active timestamp.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLatestKeyRingSnapshotActionsAsync(
        string activePrefix,
        string previousPrefix,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadLatestKeyRingSnapshotAsync(activePrefix, previousPrefix, cancellationToken);
        return snapshot.Actions;
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
            .ThenBy(x => x.Action)
            .Select(x => x.Action)
            .ToListAsync(cancellationToken);
    }

    private async Task PersistKeyRingSnapshotAttemptAsync(
        ManagedElsaHandoffKeyRingSnapshotWrite snapshot,
        CancellationToken cancellationToken)
    {
        var latest = await ReadLatestKeyRingSnapshotAsync(
            snapshot.ActivePrefix, snapshot.PreviousPrefix, cancellationToken);
        var previous = ParseTrustedSnapshot(latest.Actions, snapshot.ActivePrefix, snapshot.PreviousPrefix);
        var previousKeyIds = snapshot.PreviousKeyIds
            .Where(keyId => !string.IsNullOrWhiteSpace(keyId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var currentTrusted = previousKeyIds
            .Append(snapshot.ActiveKeyId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (string.Equals(previous.ActiveKeyId, snapshot.ActiveKeyId, StringComparison.Ordinal) &&
            previous.TrustedKeyIds.ToHashSet(StringComparer.Ordinal).SetEquals(currentTrusted))
            return;

        var snapshotId = Guid.NewGuid();
        var previousSnapshotId = latest.SnapshotId ?? KeyRingGenesisPreviousSnapshotId;
        var occurredAt = snapshot.OccurredAt.ToUniversalTime();
        foreach (var keyId in previous.TrustedKeyIds.Where(keyId =>
                     !currentTrusted.Contains(keyId, StringComparer.Ordinal)))
        {
            var signerRevoked = string.Equals(keyId, previous.ActiveKeyId, StringComparison.Ordinal);
            dbContext.ManagedElsaHandoffAuditEvents.Add(new ManagedElsaHandoffAuditEventEntity
            {
                Id = Guid.NewGuid(),
                Action = signerRevoked
                    ? snapshot.RetiredPrefix + keyId + snapshot.RetiredRevokedSuffix
                    : snapshot.RetiredPrefix + keyId,
                Jti = "",
                KeyRingSnapshotId = snapshotId,
                OccurredAt = occurredAt
            });
        }

        dbContext.ManagedElsaHandoffAuditEvents.Add(new ManagedElsaHandoffAuditEventEntity
        {
            Id = Guid.NewGuid(),
            Action = snapshot.ActivePrefix + snapshot.ActiveKeyId,
            Jti = "",
            KeyRingSnapshotId = snapshotId,
            KeyRingPreviousSnapshotId = previousSnapshotId,
            OccurredAt = occurredAt
        });
        foreach (var keyId in previousKeyIds)
        {
            dbContext.ManagedElsaHandoffAuditEvents.Add(new ManagedElsaHandoffAuditEventEntity
            {
                Id = Guid.NewGuid(),
                Action = snapshot.PreviousPrefix + keyId,
                Jti = "",
                KeyRingSnapshotId = snapshotId,
                OccurredAt = occurredAt
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(Guid? SnapshotId, IReadOnlyList<string> Actions)> ReadLatestKeyRingSnapshotAsync(
        string activePrefix,
        string previousPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPrefix);

        var snapshotRows = await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.KeyRingSnapshotId != null &&
                        (x.Action.StartsWith(activePrefix) || x.Action.StartsWith(previousPrefix)))
            .Select(x => new { x.KeyRingSnapshotId, x.KeyRingPreviousSnapshotId, x.Action, x.OccurredAt })
            .ToListAsync(cancellationToken);
        if (snapshotRows.Count > 0)
        {
            var referenced = snapshotRows
                .Where(x => x.KeyRingPreviousSnapshotId is not null)
                .Select(x => x.KeyRingPreviousSnapshotId!.Value)
                .ToHashSet();
            var activeRows = snapshotRows
                .Where(x => x.Action.StartsWith(activePrefix, StringComparison.Ordinal))
                .ToArray();
            var tip = activeRows
                .Where(x => !referenced.Contains(x.KeyRingSnapshotId!.Value))
                .OrderByDescending(x => x.OccurredAt)
                .ThenByDescending(x => x.KeyRingSnapshotId)
                .FirstOrDefault()
                ?? activeRows
                    .OrderByDescending(x => x.OccurredAt)
                    .ThenByDescending(x => x.KeyRingSnapshotId)
                    .First();
            var actions = snapshotRows
                .Where(x => x.KeyRingSnapshotId == tip.KeyRingSnapshotId)
                .Select(x => x.Action)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return (tip.KeyRingSnapshotId, actions);
        }

        var latest = await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.Action.StartsWith(activePrefix))
            .OrderByDescending(x => x.OccurredAt)
            .Select(x => (DateTimeOffset?)x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
            return (null, []);

        var legacyActions = await dbContext.ManagedElsaHandoffAuditEvents
            .AsNoTracking()
            .Where(x => x.OccurredAt == latest.Value &&
                        (x.Action.StartsWith(activePrefix) || x.Action.StartsWith(previousPrefix)))
            .OrderBy(x => x.Action)
            .Select(x => x.Action)
            .ToListAsync(cancellationToken);
        return (null, legacyActions);
    }

    private static (string? ActiveKeyId, IReadOnlyList<string> TrustedKeyIds) ParseTrustedSnapshot(
        IReadOnlyList<string> actions,
        string activePrefix,
        string previousPrefix)
    {
        string? activeKeyId = null;
        var trusted = new List<string>();
        foreach (var action in actions)
        {
            if (action.StartsWith(activePrefix, StringComparison.Ordinal))
            {
                var keyId = action[activePrefix.Length..];
                if (keyId.Length == 0)
                    continue;
                activeKeyId = keyId;
                if (!trusted.Contains(keyId, StringComparer.Ordinal))
                    trusted.Add(keyId);
            }
            else if (action.StartsWith(previousPrefix, StringComparison.Ordinal))
            {
                var keyId = action[previousPrefix.Length..];
                if (keyId.Length == 0 || trusted.Contains(keyId, StringComparer.Ordinal))
                    continue;
                trusted.Add(keyId);
            }
        }

        return (activeKeyId, trusted);
    }

    private static ManagedElsaHandoffAuditEventEntity ToEntity(ManagedElsaHandoffAuditRecord audit) =>
        new()
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
            KeyRingSnapshotId = audit.KeyRingSnapshotId,
            KeyRingPreviousSnapshotId = audit.KeyRingPreviousSnapshotId,
            OccurredAt = audit.OccurredAt.ToUniversalTime()
        };

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
