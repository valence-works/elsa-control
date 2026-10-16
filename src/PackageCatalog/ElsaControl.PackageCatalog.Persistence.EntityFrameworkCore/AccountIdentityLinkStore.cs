using System.Data;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class AccountIdentityLinkStore(CatalogDbContext dbContext) : IAccountIdentityLinkStore
{
    public async Task<AccountIdentityLinkResult> LinkAsync(
        TrustedWorkspaceIdentity cloudIdentity,
        TrustedWorkspaceIdentity hubIdentity,
        CancellationToken cancellationToken = default)
    {
        var cloud = cloudIdentity.Normalize();
        var hub = hubIdentity.Normalize();
        var auditId = Guid.Empty;
        DateTimeOffset? touchedAt = null;
        try
        {
            return await dbContext.ExecuteInTransactionAsync(
                IsolationLevel.Serializable,
                () => LinkCoreAsync(cloud, hub, id => auditId = id, value => touchedAt = value, cancellationToken),
                (result, token) => VerifyLinkAsync(hub, result, auditId, touchedAt, token),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (EfCoreDatabaseExceptionPolicy.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await MapUniqueViolationAsync(cloud, hub, cancellationToken);
        }
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
            var blocked = await ClassifyHubAccountBlockAsync(hubLookup.AccountId, hub, cancellationToken);
            if (blocked is { } outcome)
                return AccountIdentityLinkResult.Denied(outcome);

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
            ActorKind = AccountIdentityLinkActorKind.Account,
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
            ActorAccountId = null,
            ActorKind = AccountIdentityLinkActorKind.Operator,
            OperatorSubject = operatorSubject,
            Action = AccountIdentityLinkAction.Unlinked
        };
        dbContext.AccountIdentityLinkAuditRecords.Add(audit);
        await dbContext.SaveChangesAsync(cancellationToken);
        auditIdSink(audit.Id);
        return AccountIdentityUnlinkResult.Success(accountId);
    }

    internal async Task<AccountIdentityLinkResult> MapUniqueViolationAsync(
        TrustedWorkspaceIdentity cloud,
        TrustedWorkspaceIdentity hub,
        CancellationToken cancellationToken)
    {
        var cloudLookup = await FindIdentityAsync(cloud.Issuer, cloud.Subject, cancellationToken);
        if (cloudLookup is null)
            return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.CloudAccountMissing);

        var hubLookup = await FindIdentityAsync(hub.Issuer, hub.Subject, cancellationToken);
        if (hubLookup is not null && hubLookup.AccountId == cloudLookup.AccountId)
            return AccountIdentityLinkResult.Success(AccountIdentityLinkOutcome.AlreadyLinked, cloudLookup.AccountId);

        if (await HasDifferentHubIdentityAsync(cloudLookup.AccountId, hub.Issuer, hub.Subject, cancellationToken))
            return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.AccountAlreadyLinked);

        if (hubLookup is not null)
        {
            return AccountIdentityLinkResult.Denied(
                await ClassifyHubAccountBlockAsync(hubLookup.AccountId, hub, cancellationToken)
                ?? AccountIdentityLinkOutcome.AccountAlreadyLinked);
        }

        return AccountIdentityLinkResult.Denied(AccountIdentityLinkOutcome.AccountAlreadyLinked);
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
                             record.Action == AccountIdentityLinkAction.Unlinked &&
                             record.ActorKind == AccountIdentityLinkActorKind.Operator &&
                             record.ActorAccountId == null,
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

    private async Task<AccountIdentityLinkOutcome?> ClassifyHubAccountBlockAsync(
        Guid accountId,
        TrustedWorkspaceIdentity hub,
        CancellationToken cancellationToken)
    {
        if (await AccountHasOtherIdentitiesAsync(accountId, hub, cancellationToken))
            return AccountIdentityLinkOutcome.AccountAlreadyLinked;
        if (await AccountHasEnginesAsync(accountId, cancellationToken))
            return AccountIdentityLinkOutcome.HubAccountHasEngines;
        if (await AccountHasBlockingStateAsync(accountId, cancellationToken))
            return AccountIdentityLinkOutcome.HubAccountHasState;
        return null;
    }

    private Task<bool> AccountHasOtherIdentitiesAsync(
        Guid accountId,
        TrustedWorkspaceIdentity hub,
        CancellationToken cancellationToken) =>
        dbContext.ExternalIdentities.AnyAsync(
            identity => identity.AccountId == accountId &&
                        (identity.Issuer != hub.Issuer || identity.Subject != hub.Subject),
            cancellationToken);

    private async Task<bool> AccountHasEnginesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var workspaceIds = dbContext.WorkspaceMemberships
            .Where(membership => membership.AccountId == accountId)
            .Select(membership => membership.WorkspaceId);
        if (await dbContext.ElsaInstances.AnyAsync(
            instance => instance.DeletedAt == null && workspaceIds.Contains(instance.WorkspaceId),
            cancellationToken))
            return true;

        var revoked = ExternalEngineConnectionStatus.Revoked.ToString();
        return await dbContext.ExternalEngineConnections.AnyAsync(
            connection => connection.Status != revoked && workspaceIds.Contains(connection.WorkspaceId),
            cancellationToken);
    }

    private async Task<bool> AccountHasBlockingStateAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var memberships = dbContext.OrganizationMemberships.Where(membership => membership.AccountId == accountId);
        if (await memberships.CountAsync(cancellationToken) > 1)
            return true;

        var organizationIds = memberships.Select(membership => membership.OrganizationId);
        return await dbContext.OrganizationSubscriptions.AnyAsync(
            subscription => organizationIds.Contains(subscription.OrganizationId) &&
                            subscription.WebhookSource == BillingWebhookSources.Hub,
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
