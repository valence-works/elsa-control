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
        var auditId = Guid.Empty;
        DateTimeOffset? touchedAt = null;
        return dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => LinkCoreAsync(cloud, hub, id => auditId = id, value => touchedAt = value, cancellationToken),
            (result, token) => VerifyLinkAsync(hub, result, auditId, touchedAt, token),
            cancellationToken);
    }

    public Task<AccountIdentityUnlinkResult> UnlinkAsync(
        Guid accountId,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        var auditId = Guid.Empty;
        return dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => UnlinkCoreAsync(accountId, operatorSubject, id => auditId = id, cancellationToken),
            (result, token) => VerifyUnlinkAsync(accountId, result, auditId, token),
            cancellationToken);
    }

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
        Action<Guid> auditIdSink,
        Action<DateTimeOffset> touchedAtSink,
        CancellationToken cancellationToken)
    {
        var cloudLookup = await FindIdentityAsync(cloud.Issuer, cloud.Subject, cancellationToken);
        if (cloudLookup is null)
            return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.CloudAccountMissing);

        var cloudAccountId = cloudLookup.AccountId;
        var hubLookup = await FindIdentityAsync(hub.Issuer, hub.Subject, cancellationToken);
        if (hubLookup is not null && hubLookup.AccountId == cloudAccountId)
        {
            touchedAtSink(Touch(hubLookup, hub));
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

        var audit = new AccountIdentityLinkAuditRecord
        {
            AccountId = cloudAccountId,
            SourceAccountId = sourceAccountId,
            ActorAccountId = cloudAccountId,
            Action = AccountIdentityLinkAction.Linked
        };
        dbContext.AccountIdentityLinkAuditRecords.Add(audit);
        await dbContext.SaveChangesAsync(cancellationToken);
        auditIdSink(audit.Id);
        return AccountIdentityLinkResult.Success(AccountIdentityLinkOutcome.Linked, cloudAccountId);
    }

    private async Task<AccountIdentityUnlinkResult> UnlinkCoreAsync(
        Guid accountId,
        string? operatorSubject,
        Action<Guid> auditIdSink,
        CancellationToken cancellationToken)
    {
        var identities = await dbContext.ExternalIdentities
            .Where(identity => identity.AccountId == accountId && identity.Kind == ExternalIdentityKinds.Hub)
            .ToListAsync(cancellationToken);
        if (identities.Count == 0)
            return AccountIdentityUnlinkResult.Missing(accountId);

        dbContext.ExternalIdentities.RemoveRange(identities);
        var audit = new AccountIdentityLinkAuditRecord
        {
            AccountId = accountId,
            ActorAccountId = accountId,
            OperatorSubject = operatorSubject,
            Action = AccountIdentityLinkAction.Unlinked
        };
        dbContext.AccountIdentityLinkAuditRecords.Add(audit);
        await dbContext.SaveChangesAsync(cancellationToken);
        auditIdSink(audit.Id);
        return AccountIdentityUnlinkResult.Success(accountId);
    }

    private async Task<bool> VerifyLinkAsync(
        TrustedWorkspaceIdentity hub,
        AccountIdentityLinkResult result,
        Guid auditId,
        DateTimeOffset? touchedAt,
        CancellationToken cancellationToken)
    {
        if (result.Outcome == AccountIdentityLinkOutcome.AlreadyLinked)
            return result.AccountId is { } alreadyLinkedAccountId &&
                   touchedAt is { } expectedTouchedAt &&
                   await dbContext.ExternalIdentities.AsNoTracking().AnyAsync(
                       identity => identity.AccountId == alreadyLinkedAccountId &&
                                   identity.Kind == ExternalIdentityKinds.Hub &&
                                   identity.Issuer == hub.Issuer &&
                                   identity.Subject == hub.Subject &&
                                   identity.DisplayName == hub.DisplayName &&
                                   identity.Email == hub.Email &&
                                   identity.LastSeenAt >= expectedTouchedAt &&
                                   identity.UpdatedAt >= expectedTouchedAt,
                       cancellationToken);

        if (result.Outcome != AccountIdentityLinkOutcome.Linked || result.AccountId is not { } accountId)
            return true;

        return auditId != Guid.Empty &&
               await dbContext.AccountIdentityLinkAuditRecords.AsNoTracking().AnyAsync(
                   record => record.Id == auditId &&
                             record.AccountId == accountId &&
                             record.Action == AccountIdentityLinkAction.Linked,
                   cancellationToken) &&
               await HubLinkedToAccountAsync(hub, accountId, cancellationToken);
    }

    private async Task<bool> VerifyUnlinkAsync(
        Guid accountId,
        AccountIdentityUnlinkResult result,
        Guid auditId,
        CancellationToken cancellationToken)
    {
        if (result.Outcome != AccountIdentityUnlinkOutcome.Unlinked)
            return true;

        return auditId != Guid.Empty &&
               await dbContext.AccountIdentityLinkAuditRecords.AsNoTracking().AnyAsync(
                   record => record.Id == auditId &&
                             record.AccountId == accountId &&
                             record.Action == AccountIdentityLinkAction.Unlinked,
                   cancellationToken) &&
               !await dbContext.ExternalIdentities.AsNoTracking().AnyAsync(
                   identity => identity.AccountId == accountId && identity.Kind == ExternalIdentityKinds.Hub,
                   cancellationToken);
    }

    private Task<bool> HubLinkedToAccountAsync(
        TrustedWorkspaceIdentity hub,
        Guid accountId,
        CancellationToken cancellationToken) =>
        dbContext.ExternalIdentities.AsNoTracking().AnyAsync(
            identity => identity.AccountId == accountId &&
                        identity.Kind == ExternalIdentityKinds.Hub &&
                        identity.Issuer == hub.Issuer &&
                        identity.Subject == hub.Subject,
            cancellationToken);

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

    private static DateTimeOffset Touch(ExternalIdentity identity, TrustedWorkspaceIdentity hub)
    {
        var now = DateTimeOffset.UtcNow;
        identity.Kind = ExternalIdentityKinds.Hub;
        identity.DisplayName = hub.DisplayName;
        identity.Email = hub.Email;
        identity.LastSeenAt = now;
        identity.UpdatedAt = now;
        return now;
    }
}
