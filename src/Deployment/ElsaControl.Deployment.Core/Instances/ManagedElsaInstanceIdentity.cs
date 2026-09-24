namespace ElsaControl.Deployment.Core.Instances;

public sealed record ManagedElsaInstanceIdentity(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    string Audience,
    Uri CallbackUri,
    int BindingVersion,
    DateTimeOffset ChangedAt,
    bool StudioGrantsSupported = false);

public sealed record ManagedElsaInstanceScope(Guid OrganizationId, Guid WorkspaceId, Guid InstanceId);

public enum ManagedElsaInstanceIdentityBindingWriteOutcome
{
    Created,
    Rotated,
    NotFound,
    Conflict
}

public sealed record ManagedElsaInstanceIdentityBindingWriteResult(
    ManagedElsaInstanceIdentityBindingWriteOutcome Outcome,
    ManagedElsaInstanceIdentity? Identity)
{
    public bool Succeeded => Identity is not null &&
                             Outcome is ManagedElsaInstanceIdentityBindingWriteOutcome.Created or
                                 ManagedElsaInstanceIdentityBindingWriteOutcome.Rotated;
}

public interface IManagedElsaInstanceIdentityStore
{
    Task<ManagedElsaInstanceScope?> FindScopeAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<ManagedElsaInstanceIdentity?> EnsureAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<ManagedElsaInstanceIdentity?> FindAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    Task<ManagedElsaInstanceIdentity?> FindOpenableAsync(
        Guid organizationId,
        Guid instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the current openable identity bindings for many instances in a
    /// single query, keyed by instance id. Instances that are missing, deleted,
    /// unhealthy or unbound are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity>> FindOpenableManyAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> instanceIds,
        CancellationToken cancellationToken = default);

    Task<ManagedElsaInstanceIdentityBindingWriteResult> BindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        string verifiedEndpointOrigin,
        int? expectedBindingVersion,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);
}
