using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class AccountIdentityLinkStore(CatalogDbContext dbContext) : IAccountIdentityLinkStore
{
    public Task<AccountIdentityLinkResult> LinkAsync(
        TrustedWorkspaceIdentity cloudIdentity,
        TrustedWorkspaceIdentity hubIdentity,
        CancellationToken cancellationToken = default)
    {
        var cloud = cloudIdentity.Normalize();
        var hub = hubIdentity.Normalize();
        return dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => LinkCoreAsync(cloud, hub, cancellationToken),
            cancellationToken);
    }

    public Task<AccountIdentityUnlinkResult> UnlinkAsync(
        Guid accountId,
        string? operatorSubject,
        CancellationToken cancellationToken = default) =>
        dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => UnlinkCoreAsync(accountId, operatorSubject, cancellationToken),
            cancellationToken);

    public async Task<AccountIdentityLinkStatus> GetStatusAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var linked = await dbContext.ExternalIdentities
            .AsNoTracking()
            .AnyAsync(
                identity => identity.AccountId == accountId && identity.Kind == ExternalIdentityKinds.Hub,
                cancellationToken);
        var audit = await dbContext.AccountIdentityLinkAuditRecords
            .AsNoTracking()
            .Where(record => record.AccountId == accountId)
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        return new AccountIdentityLinkStatus(accountId, linked, audit);
    }

    private async Task<AccountIdentityLinkResult> LinkCoreAsync(
        TrustedWorkspaceIdentity cloud,
        TrustedWorkspaceIdentity hub,
        CancellationToken cancellationToken)
    {
        var cloudLookup = await FindIdentityAsync(cloud.Issuer, cloud.Subject, cancellationToken);
        if (cloudLookup is null)
            return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.CloudAccountMissing);

        var cloudAccountId = cloudLookup.AccountId;
        var hubLookup = await FindIdentityAsync(hub.Issuer, hub.Subject, cancellationToken);
        if (hubLookup is not null && hubLookup.AccountId == cloudAccountId)
        {
            Touch(hubLookup, hub);
            await dbContext.SaveChangesAsync(cancellationToken);
            return AccountIdentityLinkResult.Success(AccountIdentityLinkOutcome.AlreadyLinked, cloudAccountId);
        }

        if (await HasDifferentHubIdentityAsync(cloudAccountId, hub.Issuer, hub.Subject, cancellationToken))
            return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.AccountAlreadyLinked);

        Guid? sourceAccountId = null;
        if (hubLookup is not null)
        {
            if (await AccountHasEnginesAsync(hubLookup.AccountId, cancellationToken))
                return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.HubAccountHasEngines);

            sourceAccountId = hubLookup.AccountId;
            hubLookup.AccountId = cloudAccountId;
            hubLookup.Kind = ExternalIdentityKinds.Hub;
            hubLookup.DisplayName = hub.DisplayName;
            hubLookup.Email = hub.Email;
            hubLookup.LastSeenAt = DateTimeOffset.UtcNow;
            hubLookup.UpdatedAt = hubLookup.LastSeenAt;
        }
        else
        {
            dbContext.ExternalIdentities.Add(new ExternalIdentity
            {
                AccountId = cloudAccountId,
                Issuer = hub.Issuer,
                Subject = hub.Subject,
                Kind = ExternalIdentityKinds.Hub,
                DisplayName = hub.DisplayName,
                Email = hub.Email
            });
        }

        dbContext.AccountIdentityLinkAuditRecords.Add(new AccountIdentityLinkAuditRecord
        {
            AccountId = cloudAccountId,
            SourceAccountId = sourceAccountId,
            ActorAccountId = cloudAccountId,
            Action = AccountIdentityLinkAction.Linked
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return AccountIdentityLinkResult.Success(AccountIdentityLinkOutcome.Linked, cloudAccountId);
    }

    private async Task<AccountIdentityUnlinkResult> UnlinkCoreAsync(
        Guid accountId,
        string? operatorSubject,
        CancellationToken cancellationToken)
    {
        var identities = await dbContext.ExternalIdentities
            .Where(identity => identity.AccountId == accountId && identity.Kind == ExternalIdentityKinds.Hub)
            .ToListAsync(cancellationToken);
        if (identities.Count == 0)
            return AccountIdentityUnlinkResult.Missing(accountId);

        dbContext.ExternalIdentities.RemoveRange(identities);
        dbContext.AccountIdentityLinkAuditRecords.Add(new AccountIdentityLinkAuditRecord
        {
            AccountId = accountId,
            ActorAccountId = accountId,
            OperatorSubject = operatorSubject,
            Action = AccountIdentityLinkAction.Unlinked
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return AccountIdentityUnlinkResult.Success(accountId);
    }

    private Task<ExternalIdentity?> FindIdentityAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        dbContext.ExternalIdentities.SingleOrDefaultAsync(
            identity => identity.Issuer == issuer && identity.Subject == subject,
            cancellationToken);

    private Task<bool> HasDifferentHubIdentityAsync(
        Guid accountId,
        string issuer,
        string subject,
        CancellationToken cancellationToken) =>
        dbContext.ExternalIdentities.AnyAsync(
            identity => identity.AccountId == accountId &&
                        identity.Kind == ExternalIdentityKinds.Hub &&
                        (identity.Issuer != issuer || identity.Subject != subject),
            cancellationToken);

    private async Task<bool> AccountHasEnginesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var workspaceIds = dbContext.WorkspaceMemberships
            .Where(membership => membership.AccountId == accountId)
            .Select(membership => membership.WorkspaceId);
        return await dbContext.ElsaInstances.AnyAsync(
            instance => instance.DeletedAt == null && workspaceIds.Contains(instance.WorkspaceId),
            cancellationToken);
    }

    private static void Touch(ExternalIdentity identity, TrustedWorkspaceIdentity hub)
    {
        identity.Kind = ExternalIdentityKinds.Hub;
        identity.DisplayName = hub.DisplayName;
        identity.Email = hub.Email;
        identity.LastSeenAt = DateTimeOffset.UtcNow;
        identity.UpdatedAt = identity.LastSeenAt;
    }
}
