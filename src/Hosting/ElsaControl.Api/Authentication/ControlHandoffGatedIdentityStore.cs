using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Reports no openable identity while Control's own managed handoff is disabled. Issue and redeem already
/// refuse in that state, so without this gate the console would offer Open for an instance whose sign-in
/// then fails. Binding and scope lookups are unaffected, so re-enabling the handoff needs no redeploy.
/// </summary>
public sealed class ControlHandoffGatedIdentityStore(
    IManagedElsaInstanceIdentityStore inner,
    IOptions<ManagedElsaHandoffOptions> options) : IManagedElsaInstanceIdentityStore
{
    private static readonly IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity> None = new Dictionary<Guid, ManagedElsaInstanceIdentity>();

    public Task<ManagedElsaInstanceScope?> FindScopeAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
        inner.FindScopeAsync(organizationId, instanceId, cancellationToken);

    public Task<ManagedElsaInstanceIdentity?> EnsureAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
        inner.EnsureAsync(organizationId, instanceId, cancellationToken);

    public Task<ManagedElsaInstanceIdentity?> FindAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
        inner.FindAsync(organizationId, instanceId, cancellationToken);

    public Task<ManagedElsaInstanceIdentity?> FindOpenableAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
        options.Value.Enabled
            ? inner.FindOpenableAsync(organizationId, instanceId, cancellationToken)
            : Task.FromResult<ManagedElsaInstanceIdentity?>(null);

    public Task<IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity>> FindOpenableManyAsync(Guid organizationId, IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default) =>
        options.Value.Enabled
            ? inner.FindOpenableManyAsync(organizationId, instanceIds, cancellationToken)
            : Task.FromResult(None);

    public Task<ManagedElsaInstanceIdentityBindingWriteResult> BindAsync(Guid organizationId, Guid workspaceId, Guid instanceId, string verifiedEndpointOrigin, int? expectedBindingVersion, DateTimeOffset changedAt, CancellationToken cancellationToken = default) =>
        inner.BindAsync(organizationId, workspaceId, instanceId, verifiedEndpointOrigin, expectedBindingVersion, changedAt, cancellationToken);
}
