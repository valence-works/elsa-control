namespace ElsaControl.PackageCatalog.Core.Accounts;

public sealed class AccountIdentityLinkService(IAccountIdentityLinkStore store)
{
    public Task<AccountIdentityLinkResult> LinkAsync(
        TrustedWorkspaceIdentity cloudIdentity,
        TrustedWorkspaceIdentity hubIdentity,
        CancellationToken cancellationToken = default)
    {
        var cloud = RequireCloud(cloudIdentity);
        var hub = RequireHub(hubIdentity);
        return store.LinkAsync(cloud, hub, cancellationToken);
    }

    public Task<AccountIdentityUnlinkResult> UnlinkAsync(
        Guid accountId,
        string? operatorSubject,
        CancellationToken cancellationToken = default) =>
        store.UnlinkAsync(accountId, OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(operatorSubject), cancellationToken);

    public Task<AccountIdentityLinkStatus> GetStatusAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        store.GetStatusAsync(accountId, cancellationToken);

    private static TrustedWorkspaceIdentity RequireCloud(TrustedWorkspaceIdentity identity)
    {
        var normalized = identity.Normalize();
        if (string.Equals(normalized.Kind, ExternalIdentityKinds.Hub, StringComparison.Ordinal))
            throw new ArgumentException("A Cloud account session is required.", nameof(identity));
        return normalized;
    }

    private static TrustedWorkspaceIdentity RequireHub(TrustedWorkspaceIdentity identity)
    {
        var normalized = identity.Normalize();
        if (!string.Equals(normalized.Kind, ExternalIdentityKinds.Hub, StringComparison.Ordinal))
            throw new ArgumentException("A Hub account session is required.", nameof(identity));
        return normalized;
    }
}

public interface IAccountIdentityLinkStore
{
    Task<AccountIdentityLinkResult> LinkAsync(
        TrustedWorkspaceIdentity cloudIdentity,
        TrustedWorkspaceIdentity hubIdentity,
        CancellationToken cancellationToken = default);

    Task<AccountIdentityUnlinkResult> UnlinkAsync(
        Guid accountId,
        string? operatorSubject,
        CancellationToken cancellationToken = default);

    Task<AccountIdentityLinkStatus> GetStatusAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);
}

public enum AccountIdentityLinkOutcome
{
    Linked,
    AlreadyLinked,
    CloudAccountMissing,
    AccountAlreadyLinked,
    HubAccountHasEngines,
    HubAccountHasState
}

public enum AccountIdentityUnlinkOutcome
{
    Unlinked,
    NotFound
}

public sealed record AccountIdentityLinkResult(AccountIdentityLinkOutcome Outcome, Guid? AccountId = null)
{
    public bool Succeeded => Outcome is AccountIdentityLinkOutcome.Linked or AccountIdentityLinkOutcome.AlreadyLinked;

    public static AccountIdentityLinkResult Success(AccountIdentityLinkOutcome outcome, Guid accountId) =>
        new(outcome, accountId);

    public static AccountIdentityLinkResult Denied(AccountIdentityLinkOutcome outcome) => new(outcome);
}

public sealed record AccountIdentityUnlinkResult(AccountIdentityUnlinkOutcome Outcome, Guid AccountId)
{
    public bool Succeeded => Outcome is AccountIdentityUnlinkOutcome.Unlinked;

    public static AccountIdentityUnlinkResult Success(Guid accountId) =>
        new(AccountIdentityUnlinkOutcome.Unlinked, accountId);

    public static AccountIdentityUnlinkResult Missing(Guid accountId) =>
        new(AccountIdentityUnlinkOutcome.NotFound, accountId);
}

public sealed record AccountIdentityLinkStatus(
    Guid AccountId,
    bool Linked,
    IReadOnlyList<AccountIdentityLinkAuditRecord> Audit);
