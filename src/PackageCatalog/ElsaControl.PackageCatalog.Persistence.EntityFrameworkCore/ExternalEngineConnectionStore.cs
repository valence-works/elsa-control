using System.Data;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class EfCoreExternalEngineConnectionStore(CatalogDbContext dbContext) : IExternalEngineConnectionStore
{
    public async Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        (await dbContext.ExternalEngineConnections
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .Where(x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken))
        .Select(ToDomain)
        .ToList();

    public async Task<ExternalEngineConnection?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExternalEngineConnections
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.WorkspaceId == workspaceId
                     && x.Id == connectionId,
                cancellationToken);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<ExternalEngineConnectionCreateResult> TryCreateAsync(
        ExternalEngineConnection connection,
        string idempotencyKey,
        string requestDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var existing = await FindByIdempotencyKeyAsync(
            connection.OrganizationId, connection.WorkspaceId, idempotencyKey, cancellationToken);
        if (existing is not null)
            return string.Equals(existing.Value.RequestDigest, requestDigest, StringComparison.Ordinal)
                ? ExternalEngineConnectionCreateResult.Replay(existing.Value.Connection)
                : ExternalEngineConnectionCreateResult.Conflict();

        dbContext.ExternalEngineConnections.Add(ToEntity(connection, idempotencyKey, requestDigest));
        AddAudit(connection, "Created", connection.CreatedAt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ExternalEngineConnectionCreateResult.Created(connection);
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            existing = await FindByIdempotencyKeyAsync(
                connection.OrganizationId, connection.WorkspaceId, idempotencyKey, cancellationToken);
            return existing is not null && string.Equals(existing.Value.RequestDigest, requestDigest, StringComparison.Ordinal)
                ? ExternalEngineConnectionCreateResult.Replay(existing.Value.Connection)
                : ExternalEngineConnectionCreateResult.Conflict();
        }
    }

    public Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(
        ExternalEngineConnection expected,
        Guid challengeId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        TryUpdateAsync(expected, "PairingIssued", updatedAt, entity => entity.LastChallengeId = challengeId, cancellationToken);

    public Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(
        ExternalEngineConnection expected,
        Guid identityId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        TryUpdateAsync(expected, "IdentityEnrolled", updatedAt, entity => entity.ActiveIdentityId = identityId, cancellationToken);

    public Task<ExternalEngineConnection?> TryPrepareRepairAsync(
        ExternalEngineConnection expected,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        TryUpdateAsync(expected, "RepairStarted", updatedAt, entity =>
        {
            entity.Status = ExternalEngineConnectionStatus.Pending.ToString();
            entity.ActiveIdentityId = null;
            entity.LastChallengeId = null;
            entity.LastAuthenticatedAt = null;
            entity.LastHeartbeatSequence = null;
            entity.LastHeartbeatObservedAt = null;
            entity.ConnectorReachability = ExternalEngineConnectorReachability.Unknown.ToString();
            entity.ConnectorProtocol = null;
            entity.ConnectorVersion = null;
            entity.ObservedDistribution = null;
            entity.ObservedVersion = null;
            entity.ObservedRuntimeKind = null;
            entity.ReleaseEvidenceLevel = ExternalEngineReleaseEvidenceLevel.None.ToString();
            entity.ReleaseEvidenceReference = null;
            entity.StudioDestination = null;
            entity.StudioDestinationCandidate = null;
            entity.StudioDestinationCandidateId = null;
            entity.StudioDestinationConfirmedAt = null;
            entity.StudioDestinationConfirmedByAccountId = null;
            entity.ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Unknown.ToString();
            entity.ConnectorCompatibilityObservedAt = null;
            entity.CapabilitiesObservedAt = null;
            entity.Capabilities.Clear();
        }, cancellationToken);

    public Task<ExternalEngineConnection?> TryDisconnectAsync(
        ExternalEngineConnection expected,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default) =>
        TryUpdateAsync(expected, "Disconnected", revokedAt, entity =>
        {
            entity.Status = ExternalEngineConnectionStatus.Revoked.ToString();
            entity.RevokedAt = revokedAt.ToUniversalTime();
            entity.ConnectorReachability = ExternalEngineConnectorReachability.Unreachable.ToString();
            entity.StudioDestination = null;
            entity.StudioDestinationCandidate = null;
            entity.StudioDestinationCandidateId = null;
            entity.StudioDestinationConfirmedAt = null;
            entity.StudioDestinationConfirmedByAccountId = null;
            entity.ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Unknown.ToString();
            entity.ConnectorCompatibilityObservedAt = null;
        }, cancellationToken);

    public async Task<ExternalEngineConnection?> TryConfirmStudioDestinationAsync(
        ExternalEngineConnection expected,
        Guid candidateId,
        Guid accountId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken = default) =>
        await dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => TryConfirmStudioDestinationCoreAsync(
                expected, candidateId, accountId, confirmedAt, cancellationToken),
            async (result, attemptCancellationToken) =>
                result is null
                || await dbContext.ExternalEngineConnections.AsNoTracking().AnyAsync(
                    entity => entity.OrganizationId == expected.OrganizationId
                              && entity.WorkspaceId == expected.WorkspaceId
                              && entity.Id == expected.Id
                              && entity.StudioDestinationCandidateId == candidateId
                              && entity.StudioDestination == entity.StudioDestinationCandidate
                              && entity.StudioDestinationConfirmedByAccountId == accountId,
                    attemptCancellationToken),
            cancellationToken);

    private async Task<ExternalEngineConnection?> TryConfirmStudioDestinationCoreAsync(
        ExternalEngineConnection expected,
        Guid candidateId,
        Guid accountId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ExternalEngineConnections
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == expected.OrganizationId
                     && x.WorkspaceId == expected.WorkspaceId
                     && x.Id == expected.Id,
                cancellationToken);
        if (entity is null
            || entity.Version != expected.Version
            || entity.Status == ExternalEngineConnectionStatus.Revoked.ToString()
            || entity.ActiveIdentityId is null
            || entity.ConnectorCompatibilityStatus != ExternalEngineConnectorCompatibilityStatus.Compatible.ToString()
            || entity.StudioDestinationCandidate is null
            || entity.StudioDestinationCandidateId != candidateId
            || !entity.Capabilities.Any(x => x.Capability == ExternalEngineHeartbeatService.StudioCapability)
            || entity.LastAuthenticatedAt is null
            || confirmedAt - entity.LastAuthenticatedAt > ExternalEngineHeartbeatService.FreshnessWindow)
            return null;

        var activeIdentity = await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
            identity => identity.Id == entity.ActiveIdentityId
                        && identity.OrganizationId == entity.OrganizationId
                        && identity.WorkspaceId == entity.WorkspaceId
                        && identity.ConnectionId == entity.Id
                        && identity.RevokedAt == null,
            cancellationToken);
        if (!activeIdentity)
            return null;

        entity.StudioDestination = entity.StudioDestinationCandidate;
        entity.StudioDestinationConfirmedAt = confirmedAt.ToUniversalTime();
        entity.StudioDestinationConfirmedByAccountId = accountId;
        entity.UpdatedAt = confirmedAt.ToUniversalTime();
        entity.Version = checked(entity.Version + 1);
        AddAudit(entity, "StudioDestinationConfirmed", confirmedAt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDomain(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<ExternalEngineHeartbeatStoreResult> TryRecordUnsupportedProtocolAsync(
        ExternalEngineConnection expected,
        long sequence,
        DateTimeOffset observedAt,
        Guid identityId,
        DateTimeOffset receivedAt,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ExternalEngineConnections
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == expected.OrganizationId
                     && x.WorkspaceId == expected.WorkspaceId
                     && x.Id == expected.Id,
                cancellationToken);
        if (entity is null || entity.ActiveIdentityId != identityId)
            return new(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, entity is null ? null : ToDomain(entity));
        if (entity.Status == ExternalEngineConnectionStatus.Revoked.ToString())
            return new(ExternalEngineHeartbeatStoreStatus.Revoked, ToDomain(entity));
        if (entity.Version != expected.Version)
            return new(ExternalEngineHeartbeatStoreStatus.Concurrent, ToDomain(entity));
        var activeIdentity = await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
            identity => identity.Id == identityId
                        && identity.OrganizationId == entity.OrganizationId
                        && identity.WorkspaceId == entity.WorkspaceId
                        && identity.ConnectionId == entity.Id
                        && identity.RevokedAt == null,
            cancellationToken);
        if (!activeIdentity)
            return new(ExternalEngineHeartbeatStoreStatus.Revoked, ToDomain(entity));
        if (entity.LastHeartbeatSequence is { } lastSequence && sequence <= lastSequence)
            return new(ExternalEngineHeartbeatStoreStatus.OutOfOrder, ToDomain(entity));
        if (entity.LastAuthenticatedAt is { } lastReceived && receivedAt - lastReceived < minimumInterval)
            return new(
                ExternalEngineHeartbeatStoreStatus.RateLimited,
                ToDomain(entity),
                minimumInterval - (receivedAt - lastReceived));

        entity.ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol.ToString();
        entity.ConnectorCompatibilityObservedAt = receivedAt.ToUniversalTime();
        entity.LastAuthenticatedAt = receivedAt.ToUniversalTime();
        entity.LastHeartbeatSequence = sequence;
        entity.LastHeartbeatObservedAt = observedAt.ToUniversalTime();
        entity.ConnectorReachability = ExternalEngineConnectorReachability.Reachable.ToString();
        entity.UpdatedAt = receivedAt.ToUniversalTime();
        entity.Version = checked(entity.Version + 1);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new(ExternalEngineHeartbeatStoreStatus.Concurrent, null);
        }

        return new(ExternalEngineHeartbeatStoreStatus.Applied, ToDomain(entity));
    }

    public async Task<ExternalEngineHeartbeatStoreResult> TryApplyHeartbeatAsync(
        ExternalEngineConnection expected,
        ExternalEngineHeartbeatProjection projection,
        Guid identityId,
        DateTimeOffset receivedAt,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken = default) =>
        await dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => TryApplyHeartbeatCoreAsync(
                expected, projection, identityId, receivedAt, minimumInterval, cancellationToken),
            async (result, attemptCancellationToken) =>
                result.Status != ExternalEngineHeartbeatStoreStatus.Applied
                || await dbContext.ExternalEngineConnections.AsNoTracking().AnyAsync(
                    entity => entity.OrganizationId == expected.OrganizationId
                              && entity.WorkspaceId == expected.WorkspaceId
                              && entity.Id == expected.Id
                              && entity.ActiveIdentityId == identityId
                              && entity.LastHeartbeatSequence == projection.Sequence,
                    attemptCancellationToken),
            cancellationToken);

    private async Task<ExternalEngineHeartbeatStoreResult> TryApplyHeartbeatCoreAsync(
        ExternalEngineConnection expected,
        ExternalEngineHeartbeatProjection projection,
        Guid identityId,
        DateTimeOffset receivedAt,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ExternalEngineConnections
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == expected.OrganizationId
                     && x.WorkspaceId == expected.WorkspaceId
                     && x.Id == expected.Id,
                cancellationToken);
        if (entity is null || entity.ActiveIdentityId != identityId)
            return new(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, entity is null ? null : ToDomain(entity));
        if (entity.Status == ExternalEngineConnectionStatus.Revoked.ToString())
            return new(ExternalEngineHeartbeatStoreStatus.Revoked, ToDomain(entity));
        var activeIdentity = await dbContext.ExternalEngineConnectorIdentities.AsNoTracking().AnyAsync(
            identity => identity.Id == identityId
                        && identity.OrganizationId == entity.OrganizationId
                        && identity.WorkspaceId == entity.WorkspaceId
                        && identity.ConnectionId == entity.Id
                        && identity.RevokedAt == null,
            cancellationToken);
        if (!activeIdentity)
            return new(ExternalEngineHeartbeatStoreStatus.Revoked, ToDomain(entity));
        if (entity.Version != expected.Version)
            return new(ExternalEngineHeartbeatStoreStatus.Concurrent, ToDomain(entity));
        if (entity.LastHeartbeatSequence is not null && projection.Sequence <= entity.LastHeartbeatSequence)
            return new(ExternalEngineHeartbeatStoreStatus.OutOfOrder, ToDomain(entity));
        if (entity.LastAuthenticatedAt is { } lastReceived && receivedAt - lastReceived < minimumInterval)
            return new(
                ExternalEngineHeartbeatStoreStatus.RateLimited,
                ToDomain(entity),
                minimumInterval - (receivedAt - lastReceived));

        var previousStatus = entity.Status;
        entity.Status = projection.Status.ToString();
        entity.RuntimeHealth = projection.RuntimeHealth.ToString();
        entity.ConnectorReachability = projection.ConnectorReachability.ToString();
        entity.LastAuthenticatedAt = receivedAt.ToUniversalTime();
        entity.LastHeartbeatSequence = projection.Sequence;
        entity.LastHeartbeatObservedAt = projection.ObservedAt.ToUniversalTime();
        entity.ConnectorProtocol = projection.ConnectorProtocol;
        entity.ConnectorVersion = projection.ConnectorVersion;
        entity.ObservedDistribution = projection.ObservedDistribution;
        entity.ObservedVersion = projection.ObservedVersion;
        entity.ObservedRuntimeKind = projection.ObservedRuntimeKind;
        entity.ReleaseEvidenceLevel = projection.ReleaseEvidenceLevel.ToString();
        entity.ReleaseEvidenceReference = projection.ReleaseEvidenceReference;
        entity.ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Compatible.ToString();
        entity.ConnectorCompatibilityObservedAt = receivedAt.ToUniversalTime();
        var studioCandidate = projection.Capabilities.Contains(
                ExternalEngineHeartbeatService.StudioCapability, StringComparer.Ordinal)
            ? projection.StudioDestinationCandidate
            : null;
        if (!string.Equals(entity.StudioDestinationCandidate, studioCandidate, StringComparison.Ordinal))
        {
            entity.StudioDestinationCandidate = studioCandidate;
            entity.StudioDestinationCandidateId = studioCandidate is null ? null : Guid.NewGuid();
            entity.StudioDestination = null;
            entity.StudioDestinationConfirmedAt = null;
            entity.StudioDestinationConfirmedByAccountId = null;
        }
        entity.CapabilitiesObservedAt = receivedAt.ToUniversalTime();
        var desiredCapabilities = projection.Capabilities.ToHashSet(StringComparer.Ordinal);
        foreach (var capability in entity.Capabilities.Where(item => !desiredCapabilities.Contains(item.Capability)).ToArray())
            entity.Capabilities.Remove(capability);
        var existingCapabilities = entity.Capabilities.Select(item => item.Capability).ToHashSet(StringComparer.Ordinal);
        entity.Capabilities.AddRange(desiredCapabilities.Except(existingCapabilities, StringComparer.Ordinal).Select(capability =>
            new ExternalEngineConnectionCapabilityEntity { ConnectionId = entity.Id, Capability = capability }));
        entity.UpdatedAt = receivedAt.ToUniversalTime();
        entity.Version = checked(entity.Version + 1);

        var action = (previousStatus, entity.Status) switch
        {
            (nameof(ExternalEngineConnectionStatus.Degraded), nameof(ExternalEngineConnectionStatus.Connected)) => "HeartbeatRecovered",
            (_, nameof(ExternalEngineConnectionStatus.Connected)) when previousStatus != nameof(ExternalEngineConnectionStatus.Connected) => "HeartbeatConnected",
            (_, nameof(ExternalEngineConnectionStatus.Degraded)) when previousStatus != nameof(ExternalEngineConnectionStatus.Degraded) => "HeartbeatDegraded",
            _ => null
        };
        if (action is not null)
            AddAudit(entity, action, receivedAt);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new(ExternalEngineHeartbeatStoreStatus.Applied, ToDomain(entity));
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new(ExternalEngineHeartbeatStoreStatus.Concurrent, null);
        }
    }

    private async Task<ExternalEngineConnection?> TryUpdateAsync(
        ExternalEngineConnection expected,
        string action,
        DateTimeOffset updatedAt,
        Action<ExternalEngineConnectionEntity> mutate,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ExternalEngineConnections
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == expected.OrganizationId
                     && x.WorkspaceId == expected.WorkspaceId
                     && x.Id == expected.Id,
                cancellationToken);
        if (entity is null || entity.Version != expected.Version)
            return null;
        if (entity.Status == ExternalEngineConnectionStatus.Revoked.ToString() && action != "Disconnected")
            return null;
        if (entity.Status == ExternalEngineConnectionStatus.Revoked.ToString() && action == "Disconnected")
            return ToDomain(entity);

        mutate(entity);
        entity.UpdatedAt = updatedAt.ToUniversalTime();
        entity.Version = checked(entity.Version + 1);
        AddAudit(entity, action, updatedAt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDomain(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    private async Task<(ExternalEngineConnection Connection, string RequestDigest)?> FindByIdempotencyKeyAsync(
        Guid organizationId,
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ExternalEngineConnections
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.WorkspaceId == workspaceId
                     && x.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : (ToDomain(entity), entity.CreateRequestDigest);
    }

    private void AddAudit(ExternalEngineConnection connection, string action, DateTimeOffset occurredAt) =>
        AddAudit(connection.OrganizationId, connection.WorkspaceId, connection.Id, action, occurredAt);

    private void AddAudit(ExternalEngineConnectionEntity connection, string action, DateTimeOffset occurredAt) =>
        AddAudit(connection.OrganizationId, connection.WorkspaceId, connection.Id, action, occurredAt);

    private void AddAudit(Guid organizationId, Guid workspaceId, Guid connectionId, string action, DateTimeOffset occurredAt) =>
        dbContext.ExternalEngineConnectionAuditEvents.Add(new ExternalEngineConnectionAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ConnectionId = connectionId,
            Action = action,
            OccurredAt = occurredAt.ToUniversalTime()
        });

    private static ExternalEngineConnectionEntity ToEntity(
        ExternalEngineConnection value,
        string idempotencyKey,
        string requestDigest) =>
        new()
        {
            Id = value.Id,
            OrganizationId = value.OrganizationId,
            WorkspaceId = value.WorkspaceId,
            DisplayName = value.DisplayName,
            OwnershipMode = ExternalEngineConnection.OwnershipMode,
            Status = value.Status.ToString(),
            RuntimeHealth = value.RuntimeHealth.ToString(),
            ConnectorReachability = value.ConnectorReachability.ToString(),
            LastAuthenticatedAt = value.LastAuthenticatedAt,
            ConnectorProtocol = value.ConnectorProtocol,
            ConnectorVersion = value.ConnectorVersion,
            ObservedDistribution = value.ObservedDistribution,
            ObservedVersion = value.ObservedVersion,
            ObservedRuntimeKind = value.ObservedRuntimeKind,
            ReleaseEvidenceLevel = value.ReleaseEvidenceLevel.ToString(),
            ReleaseEvidenceReference = value.ReleaseEvidenceReference,
            StudioDestination = value.StudioDestination,
            StudioDestinationCandidate = value.StudioDestinationCandidate,
            StudioDestinationCandidateId = value.StudioDestinationCandidateId,
            StudioDestinationConfirmedAt = value.StudioDestinationConfirmedAt,
            StudioDestinationConfirmedByAccountId = value.StudioDestinationConfirmedByAccountId,
            ConnectorCompatibilityStatus = value.ConnectorCompatibilityStatus.ToString(),
            ConnectorCompatibilityObservedAt = value.ConnectorCompatibilityObservedAt,
            CapabilitiesObservedAt = value.CapabilitiesObservedAt,
            LastHeartbeatSequence = value.LastHeartbeatSequence,
            LastHeartbeatObservedAt = value.LastHeartbeatObservedAt,
            ActiveIdentityId = value.ActiveIdentityId,
            LastChallengeId = value.LastChallengeId,
            IdempotencyKey = idempotencyKey,
            CreateRequestDigest = requestDigest,
            CreatedAt = value.CreatedAt.ToUniversalTime(),
            UpdatedAt = value.UpdatedAt.ToUniversalTime(),
            RevokedAt = value.RevokedAt?.ToUniversalTime(),
            Version = value.Version,
            Capabilities = value.Capabilities
                .Select(x => new ExternalEngineConnectionCapabilityEntity { ConnectionId = value.Id, Capability = x })
                .ToList()
        };

    private static ExternalEngineConnection ToDomain(ExternalEngineConnectionEntity value) =>
        new(
            value.Id,
            value.OrganizationId,
            value.WorkspaceId,
            value.DisplayName,
            Enum.Parse<ExternalEngineConnectionStatus>(value.Status),
            Enum.Parse<ExternalEngineRuntimeHealth>(value.RuntimeHealth),
            Enum.Parse<ExternalEngineConnectorReachability>(value.ConnectorReachability),
            value.LastAuthenticatedAt,
            value.ConnectorProtocol,
            value.ConnectorVersion,
            value.ObservedDistribution,
            value.ObservedVersion,
            Enum.Parse<ExternalEngineReleaseEvidenceLevel>(value.ReleaseEvidenceLevel),
            value.StudioDestination,
            value.Capabilities.Select(x => x.Capability).Order(StringComparer.Ordinal).ToList(),
            value.CapabilitiesObservedAt,
            value.ActiveIdentityId,
            value.LastChallengeId,
            value.CreatedAt,
            value.UpdatedAt,
            value.RevokedAt,
            value.Version,
            value.ObservedRuntimeKind,
            value.ReleaseEvidenceReference,
            value.LastHeartbeatSequence,
            value.LastHeartbeatObservedAt,
            value.StudioDestinationCandidate,
            value.StudioDestinationCandidateId,
            value.StudioDestinationConfirmedAt,
            value.StudioDestinationConfirmedByAccountId,
            Enum.Parse<ExternalEngineConnectorCompatibilityStatus>(value.ConnectorCompatibilityStatus),
            value.ConnectorCompatibilityObservedAt);
}
