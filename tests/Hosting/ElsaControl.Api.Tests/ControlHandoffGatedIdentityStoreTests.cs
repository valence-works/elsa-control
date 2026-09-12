using ElsaControl.Api.Authentication;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ControlHandoffGatedIdentityStoreTests
{
    private static readonly ManagedElsaInstanceIdentity Identity = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "urn:elsa:instance:test",
        new Uri("https://dogfood.example/managed-elsa/handoff/callback"), 1, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Openable_identities_follow_Control_handoff_enablement(bool enabled)
    {
        var store = new ControlHandoffGatedIdentityStore(new FixedIdentityStore(), Options.Create(new ManagedElsaHandoffOptions { Enabled = enabled }));

        var single = await store.FindOpenableAsync(Identity.OrganizationId, Identity.InstanceId);
        var many = await store.FindOpenableManyAsync(Identity.OrganizationId, [Identity.InstanceId]);

        Assert.Equal(enabled ? Identity : null, single);
        Assert.Equal(enabled ? 1 : 0, many.Count);
    }

    [Fact]
    public async Task Binding_lookups_stay_available_while_Control_handoff_is_disabled()
    {
        var store = new ControlHandoffGatedIdentityStore(new FixedIdentityStore(), Options.Create(new ManagedElsaHandoffOptions { Enabled = false }));

        Assert.Equal(Identity, await store.FindAsync(Identity.OrganizationId, Identity.InstanceId));
    }

    private sealed class FixedIdentityStore : IManagedElsaInstanceIdentityStore
    {
        public Task<ManagedElsaInstanceScope?> FindScopeAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagedElsaInstanceIdentity?> EnsureAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagedElsaInstanceIdentity?> FindAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedElsaInstanceIdentity?>(Identity);

        public Task<ManagedElsaInstanceIdentity?> FindOpenableAsync(Guid organizationId, Guid instanceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ManagedElsaInstanceIdentity?>(Identity);

        public Task<IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity>> FindOpenableManyAsync(Guid organizationId, IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ManagedElsaInstanceIdentity>>(new Dictionary<Guid, ManagedElsaInstanceIdentity> { [Identity.InstanceId] = Identity });

        public Task<ManagedElsaInstanceIdentityBindingWriteResult> BindAsync(Guid organizationId, Guid workspaceId, Guid instanceId, string verifiedEndpointOrigin, int? expectedBindingVersion, DateTimeOffset changedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
