using System.Data;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Resolves the current managed Elsa identity binding from the instance aggregate.
/// A missing, deleted, unavailable or malformed binding is represented as null so
/// callers cannot accidentally construct a fallback audience or callback.
/// </summary>
public sealed class EfCoreManagedElsaInstanceIdentityStore(CatalogDbContext dbContext) : IManagedElsaInstanceIdentityStore
{
    public async Task<ManagedElsaInstanceScope?> FindScopeAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || instanceId == Guid.Empty)
            return null;
        var entity = await dbContext.ElsaInstances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.Id == instanceId,
                cancellationToken);
        return entity is null || IsUnavailable(entity)
            ? null
            : new ManagedElsaInstanceScope(entity.OrganizationId, entity.WorkspaceId, entity.Id);
    }

    public async Task<ManagedElsaInstanceIdentity?> EnsureAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(organizationId, instanceId, cancellationToken);
        if (existing is not null)
            return existing;
        if (organizationId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        dbContext.ChangeTracker.Clear();
        var projected = await dbContext.ElsaInstances
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == instanceId)
            .Select(x => new
            {
                x.WorkspaceId,
                x.CurrentDeploymentEndpointUri,
                BindingVersion = x.IdentityBinding == null ? null : (int?)x.IdentityBinding.BindingVersion,
                x.DesiredLifecycle,
                x.ObservedLifecycle,
                x.DeletedAt
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (projected is null ||
            projected.DesiredLifecycle == ElsaDesiredLifecycle.Deleting ||
            projected.ObservedLifecycle == ElsaObservedLifecycle.Deleting ||
            projected.ObservedLifecycle == ElsaObservedLifecycle.Deleted ||
            projected.DeletedAt is not null ||
            !ElsaManagedEndpointOrigin.TryCreate(projected.CurrentDeploymentEndpointUri, out var endpointOrigin))
            return null;

        var created = await BindAsync(
            organizationId,
            projected.WorkspaceId,
            instanceId,
            endpointOrigin.Value,
            projected.BindingVersion,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (created.Succeeded)
            return created.Identity;

        // A concurrent issuer may have won the unique insert.
        return await FindAsync(organizationId, instanceId, cancellationToken);
    }

    public async Task<ManagedElsaInstanceIdentity?> FindAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        dbContext.ChangeTracker.Clear();
        var entity = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.Id == instanceId,
                cancellationToken);
        return entity is null ? null : TryMap(entity);
    }

    public async Task<ManagedElsaInstanceIdentity?> FindOpenableAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || instanceId == Guid.Empty)
            return null;

        dbContext.ChangeTracker.Clear();
        var entity = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId &&
                     x.Id == instanceId &&
                     x.DeletedAt == null &&
                     x.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
                     x.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                     x.Health == ElsaInstanceHealth.Healthy,
                cancellationToken);
        return entity is null ? null : TryMap(entity);
    }

    public async Task<IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity>> FindOpenableManyAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> instanceIds,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || instanceIds.Count == 0)
            return new Dictionary<Guid, ManagedElsaInstanceIdentity>();

        dbContext.ChangeTracker.Clear();
        var entities = await dbContext.ElsaInstances
            .AsNoTracking()
            .Include(x => x.IdentityBinding)
            .Where(x => x.OrganizationId == organizationId &&
                        instanceIds.Contains(x.Id) &&
                        x.DeletedAt == null &&
                        x.DesiredLifecycle == ElsaDesiredLifecycle.Running &&
                        x.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                        x.Health == ElsaInstanceHealth.Healthy)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<Guid, ManagedElsaInstanceIdentity>();
        foreach (var entity in entities)
        {
            var identity = TryMap(entity);
            if (identity is not null)
                result[entity.Id] = identity;
        }

        return result;
    }

    /// <summary>
    /// Creates a binding when no expected version is supplied, or rotates the
    /// current binding when the supplied version matches. Both operations repeat
    /// ownership and lifecycle checks inside a serializable transaction.
    /// </summary>
    public async Task<ManagedElsaInstanceIdentityBindingWriteResult> BindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        string verifiedEndpointOrigin,
        int? expectedBindingVersion,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty || workspaceId == Guid.Empty || instanceId == Guid.Empty)
            return NotFound();
        if (expectedBindingVersion is <= 0)
            return Conflict();

        dbContext.ChangeTracker.Clear();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var entity = await dbContext.ElsaInstances
                    .Include(x => x.IdentityBinding)
                    .SingleOrDefaultAsync(
                        x => x.OrganizationId == organizationId && x.WorkspaceId == workspaceId && x.Id == instanceId,
                        cancellationToken);

                if (entity is null || IsUnavailable(entity))
                    return NotFound();

                var existing = entity.IdentityBinding;
                ElsaInstanceIdentityBinding binding;
                ManagedElsaInstanceIdentityBindingWriteOutcome outcome;
                try
                {
                    var candidate = ElsaInstanceIdentityBinding.Create(instanceId, verifiedEndpointOrigin, changedAt);
                    if (!ElsaManagedEndpointOrigin.TryCreate(entity.CurrentDeploymentEndpointUri, out var currentEndpoint) ||
                        !string.Equals(currentEndpoint.Value, candidate.VerifiedEndpointOrigin,
                            StringComparison.Ordinal))
                        return Conflict();

                    if (expectedBindingVersion is null)
                    {
                        if (existing is not null)
                            return Conflict();

                        binding = candidate;
                        entity.IdentityBinding = new ElsaInstanceIdentityBindingEntity
                        {
                            InstanceId = instanceId,
                            Audience = binding.Audience,
                            CanonicalCallbackUri = binding.CanonicalCallbackUri,
                            VerifiedEndpointOrigin = binding.VerifiedEndpointOrigin,
                            BindingVersion = binding.BindingVersion,
                            ChangedAt = binding.ChangedAt
                        };
                        outcome = ManagedElsaInstanceIdentityBindingWriteOutcome.Created;
                    }
                    else
                    {
                        if (existing is null)
                            return NotFound();
                        if (existing.BindingVersion != expectedBindingVersion)
                            return Conflict();

                        var current = ElsaInstanceIdentityBinding.Hydrate(
                            instanceId,
                            existing.VerifiedEndpointOrigin,
                            existing.BindingVersion,
                            existing.ChangedAt);
                        binding = current.Rotate(candidate.VerifiedEndpointOrigin, changedAt);
                        existing.Audience = binding.Audience;
                        existing.CanonicalCallbackUri = binding.CanonicalCallbackUri;
                        existing.VerifiedEndpointOrigin = binding.VerifiedEndpointOrigin;
                        existing.BindingVersion = binding.BindingVersion;
                        existing.ChangedAt = binding.ChangedAt;
                        outcome = ManagedElsaInstanceIdentityBindingWriteOutcome.Rotated;
                    }
                }
                catch (ArgumentException)
                {
                    return Conflict();
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return new ManagedElsaInstanceIdentityBindingWriteResult(outcome, Map(entity, binding));
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return Conflict();
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return Conflict();
        }
    }

    private static ManagedElsaInstanceIdentity? TryMap(ElsaInstanceEntity entity)
    {
        if (IsUnavailable(entity) || entity.IdentityBinding is null)
            return null;

        return ManagedElsaIdentityBindingMapper.TryMapCurrent(entity, out var binding, out var callbackUri)
            ? Map(entity, binding!, callbackUri!)
            : null;
    }

    private static ManagedElsaInstanceIdentity Map(
        ElsaInstanceEntity entity,
        ElsaInstanceIdentityBinding binding,
        Uri? callbackUri = null) =>
        new(
            entity.OrganizationId,
            entity.WorkspaceId,
            entity.Id,
            binding.Audience,
            callbackUri ?? new Uri(binding.CanonicalCallbackUri, UriKind.Absolute),
            binding.BindingVersion,
            binding.ChangedAt);

    private static bool IsUnavailable(ElsaInstanceEntity entity) =>
        entity.DeletedAt is not null ||
        entity.DesiredLifecycle == ElsaDesiredLifecycle.Deleting ||
        entity.ObservedLifecycle is ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Deleted;

    private static ManagedElsaInstanceIdentityBindingWriteResult NotFound() =>
        new(ManagedElsaInstanceIdentityBindingWriteOutcome.NotFound, null);

    private static ManagedElsaInstanceIdentityBindingWriteResult Conflict() =>
        new(ManagedElsaInstanceIdentityBindingWriteOutcome.Conflict, null);

}
