using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class AccountIdentityLinkServiceTests
{
    [Fact]
    public async Task Link_attaches_the_hub_identity_to_the_cloud_account()
    {
        var store = new FakeStore();
        var service = new AccountIdentityLinkService(store);
        var cloud = Cloud("cloud-user");
        var hub = Hub("hub-user");

        var result = await service.LinkAsync(cloud, hub);

        Assert.Equal(AccountIdentityLinkOutcome.Linked, result.Outcome);
        Assert.Equal((cloud.Issuer, cloud.Subject, hub.Issuer, hub.Subject), store.LastLink);
    }

    [Fact]
    public async Task Unlink_forwards_the_fingerprinted_operator_subject()
    {
        var store = new FakeStore();
        var service = new AccountIdentityLinkService(store);
        var accountId = Guid.NewGuid();

        var result = await service.UnlinkAsync(accountId, "support-operator");

        Assert.True(result.Succeeded);
        Assert.Equal(accountId, store.LastUnlinkAccountId);
        Assert.Equal(
            OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject("support-operator"),
            store.LastUnlinkOperatorSubject);
        Assert.DoesNotContain("support-operator", store.LastUnlinkOperatorSubject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_rejects_a_hub_session_used_as_the_cloud_identity()
    {
        var service = new AccountIdentityLinkService(new FakeStore());

        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(Hub("hub-user"), Hub("other-hub")));
    }

    [Fact]
    public async Task Link_rejects_a_cloud_session_used_as_the_hub_identity()
    {
        var service = new AccountIdentityLinkService(new FakeStore());

        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(Cloud("cloud-user"), Cloud("other-cloud")));
    }

    private static TrustedWorkspaceIdentity Cloud(string subject) =>
        new("https://cloud-account.test/auth/v1", subject, "Cloud", $"{subject}@example.test");

    private static TrustedWorkspaceIdentity Hub(string subject) =>
        new("https://hub-account.test/auth/v1", subject, "Hub", $"{subject}@example.test")
        {
            Kind = ExternalIdentityKinds.Hub
        };

    private sealed class FakeStore : IAccountIdentityLinkStore
    {
        public (string CloudIssuer, string CloudSubject, string HubIssuer, string HubSubject)? LastLink { get; private set; }
        public Guid? LastUnlinkAccountId { get; private set; }
        public string? LastUnlinkOperatorSubject { get; private set; }

        public Task<AccountIdentityLinkResult> LinkAsync(
            TrustedWorkspaceIdentity cloudIdentity,
            TrustedWorkspaceIdentity hubIdentity,
            CancellationToken cancellationToken = default)
        {
            LastLink = (cloudIdentity.Issuer, cloudIdentity.Subject, hubIdentity.Issuer, hubIdentity.Subject);
            return Task.FromResult(AccountIdentityLinkResult.Success(AccountIdentityLinkOutcome.Linked, Guid.NewGuid()));
        }

        public Task<AccountIdentityUnlinkResult> UnlinkAsync(
            Guid accountId,
            string? operatorSubject,
            CancellationToken cancellationToken = default)
        {
            LastUnlinkAccountId = accountId;
            LastUnlinkOperatorSubject = operatorSubject;
            return Task.FromResult(AccountIdentityUnlinkResult.Success(accountId));
        }

        public Task<AccountIdentityLinkStatus> GetStatusAsync(
            Guid accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountIdentityLinkStatus(accountId, false, []));
    }
}
