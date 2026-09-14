using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Persistence of the periodic Ready-instance health monitor. It changes only an instance's
/// health (plus the version and timestamp every aggregate write carries) and appends one audit
/// event, under the same serializable compare-and-set boundary as every other lifecycle write.
/// </summary>
public sealed partial class EfCoreElsaInstanceLifecycleStore
{
    internal const string HealthChangedEventType = "lifecycle.health-changed";

    public async Task<IReadOnlyList<ElsaInstanceHealthMonitorTarget>> ListHealthMonitorTargetsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var entities = await HealthMonitorCandidates(dbContext.ElsaInstances.AsNoTracking())
            .OrderBy(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.Select(TryMapHealthMonitorTarget).OfType<ElsaInstanceHealthMonitorTarget>().ToArray();
    }

    public async Task<ElsaInstanceHealthMonitorTarget?> GetHealthMonitorTargetAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty)
            return null;
        var entity = await HealthMonitorCandidates(dbContext.ElsaInstances.AsNoTracking())
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == instanceId, cancellationToken);
        return entity is null ? null : TryMapHealthMonitorTarget(entity);
    }

    public async Task<int> CommitHealthTransitionAsync(
        ElsaInstanceHealthTransition transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        transition.Validate();
        dbContext.ChangeTracker.Clear();
        var observedAt = transition.ObservedAt.ToUniversalTime();
        var transitionFingerprint = HealthTransitionFingerprint(transition, observedAt);
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                async () =>
            {
                // Re-read inside the transaction: the instance must still be in the evaluated set,
                // at the version and health the probe streak was gathered against. The version is
                // also the row's concurrency token, so a writer that commits after this read
                // still makes the save below fail instead of being overwritten.
                var instance = await HealthMonitorCandidates(dbContext.ElsaInstances)
                    .SingleOrDefaultAsync(x => x.WorkspaceId == transition.WorkspaceId && x.Id == transition.InstanceId,
                        cancellationToken)
                    ?? throw Conflict("The instance is no longer evaluated for endpoint health.");
                if (instance.Version != transition.ExpectedVersion || instance.Health != transition.ExpectedHealth)
                    throw Conflict("Instance health changed concurrently.", ElsaInstanceLifecycleConflictReason.VersionConflict);

                var priorHealth = instance.Health;
                instance.Health = transition.Health;
                instance.UpdatedAt = observedAt;
                await dbContext.ElsaInstanceAuditEvents.AddAsync(new ElsaInstanceAuditEventEntity
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = instance.OrganizationId,
                    WorkspaceId = instance.WorkspaceId,
                    InstanceId = instance.Id,
                    Sequence = await NextAuditSequenceAsync(instance.Id, cancellationToken),
                    EventType = HealthChangedEventType,
                    PriorState = priorHealth.ToString(),
                    NewState = transition.Health.ToString(),
                    DesiredStateRevisionId = instance.DesiredStateRevisionId,
                    DiagnosticCode = transition.DiagnosticCode,
                    RequestKeyHash = transitionFingerprint,
                    OccurredAt = observedAt
                }, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return instance.Version;
            },
                async (version, verificationCancellationToken) =>
                {
                    if (version != transition.ExpectedVersion + 1)
                        return false;
                    var persisted = await dbContext.ElsaInstances.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.WorkspaceId == transition.WorkspaceId &&
                                                   x.Id == transition.InstanceId,
                            verificationCancellationToken);
                    if (persisted is null || persisted.Version != version || persisted.Health != transition.Health ||
                        persisted.UpdatedAt != observedAt)
                        return false;
                    return await dbContext.ElsaInstanceAuditEvents.AsNoTracking().AnyAsync(x =>
                        x.InstanceId == transition.InstanceId && x.EventType == HealthChangedEventType &&
                        x.PriorState == transition.ExpectedHealth.ToString() &&
                        x.NewState == transition.Health.ToString() &&
                        x.DiagnosticCode == transition.DiagnosticCode &&
                        x.RequestKeyHash == transitionFingerprint && x.OccurredAt == observedAt,
                        verificationCancellationToken);
                },
                cancellationToken);
        }
        catch (ElsaInstanceLifecycleConflictException)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or DbUpdateException or DbException)
        {
            dbContext.ChangeTracker.Clear();
            throw Conflict("Instance health conflicted with a concurrent change.", ElsaInstanceLifecycleConflictReason.VersionConflict);
        }
    }

    private static string HealthTransitionFingerprint(
        ElsaInstanceHealthTransition transition, DateTimeOffset observedAt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\u001f',
            transition.WorkspaceId,
            transition.InstanceId,
            transition.ExpectedVersion,
            transition.ExpectedHealth,
            transition.Health,
            transition.DiagnosticCode ?? string.Empty,
            observedAt.Ticks))));

    /// <summary>
    /// The monitor's evaluated state set: a live managed instance, desired Running and observed
    /// Ready, with a current deployment endpoint and no blocking lifecycle operation.
    /// </summary>
    private static IQueryable<ElsaInstanceEntity> HealthMonitorCandidates(IQueryable<ElsaInstanceEntity> instances) =>
        instances.Where(x =>
            x.DeletedAt == null &&
            x.TargetMode == "managed" &&
            x.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
            x.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
            x.CurrentDeploymentId != null &&
            x.CurrentDeploymentEndpointUri != null &&
            !x.Operations.Any(operation =>
                operation.State == ElsaInstanceOperationState.Accepted ||
                operation.State == ElsaInstanceOperationState.WaitingForPriorOperation ||
                operation.State == ElsaInstanceOperationState.Queued ||
                operation.State == ElsaInstanceOperationState.EntitlementHeld ||
                operation.State == ElsaInstanceOperationState.Running ||
                operation.State == ElsaInstanceOperationState.RecoveryRequired));

    private static ElsaInstanceHealthMonitorTarget? TryMapHealthMonitorTarget(ElsaInstanceEntity entity)
    {
        try
        {
            return MapDeployment(entity) is { } deployment
                ? new(entity.OrganizationId, entity.WorkspaceId, entity.Id, entity.Version, entity.DesiredLifecycle,
                    entity.ObservedLifecycle, entity.Health, deployment)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A corrupt deployment projection cannot be probed; the instance API hides the same row.
            return null;
        }
    }
}
