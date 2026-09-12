using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Packages;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class AccountWorkspaceStore(CatalogDbContext dbContext) : IAccountWorkspaceStore
{
    public async Task<ExternalIdentityLookup?> FindByExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken = default)
    {
        var identity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .Include(x => x.Account)
            .ThenInclude(x => x!.OrganizationMemberships)
            .ThenInclude(x => x.Organization)
            .Include(x => x.Account)
            .ThenInclude(x => x!.Memberships)
            .ThenInclude(x => x.Workspace)
            .ThenInclude(x => x!.Organization)
            .SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, cancellationToken);

        if (identity?.Account is null)
            return null;

        return new ExternalIdentityLookup(
            identity.Id,
            new AccountWorkspaceContext(
                new AccountSummary(identity.Account.Id, identity.Account.DisplayName, identity.Account.Email),
                identity.Account.Memberships
                    .Where(x => x.Workspace is { SoftDeletedAt: null, Organization: { Status: OrganizationStatus.Active } })
                    .Where(x => identity.Account.OrganizationMemberships.Any(membership =>
                        membership.OrganizationId == x.Workspace!.OrganizationId &&
                        membership.DisabledAt == null))
                    .Select(x => new WorkspaceSummary(
                        x.Workspace!.Id,
                        x.Workspace.Name,
                        x.Workspace.Kind,
                        x.Role,
                        x.Workspace.OrganizationId,
                        x.Workspace.Organization!.Name,
                        OrganizationRoleFor(identity.Account.OrganizationMemberships, x.Workspace.OrganizationId)))
                    .ToList())
            {
                Organizations = identity.Account.OrganizationMemberships
                    .Where(x => x is { DisabledAt: null, Organization: { Status: OrganizationStatus.Active } })
                    .Select(x => new OrganizationSummary(x.Organization!.Id, x.Organization.Name, x.Role))
                    .OrderBy(x => x.Name)
                    .ToList()
            });
    }

    public async Task AddAccountAsync(Account account, CancellationToken cancellationToken = default) =>
        await dbContext.Accounts.AddAsync(account, cancellationToken);

    public async Task UpdateExternalIdentitySeenAsync(Guid externalIdentityId, string? displayName, string? email, CancellationToken cancellationToken = default)
    {
        await dbContext.ExecuteInTransactionAsync(IsolationLevel.Unspecified, async () =>
        {
            var now = DateTimeOffset.UtcNow;
            await dbContext.ExternalIdentities
                .Where(x => x.Id == externalIdentityId)
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(x => x.DisplayName, displayName)
                    .SetProperty(x => x.Email, email)
                    .SetProperty(x => x.LastSeenAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);

            await dbContext.Accounts
                .Where(x => x.ExternalIdentities.Any(identity => identity.Id == externalIdentityId))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(x => x.DisplayName, displayName)
                    .SetProperty(x => x.Email, email)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }, cancellationToken);
    }

    public async Task<WorkspaceEntitlementSnapshot?> GetLatestEntitlementAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspaceEntitlementSnapshots
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.SyncedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        dbContext.Workspaces
            .AsNoTracking()
            .AnyAsync(x => x.Id == workspaceId && x.SoftDeletedAt == null, cancellationToken);

    public async Task<OrganizationEntitlementSnapshot?> GetLatestOrganizationEntitlementAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await dbContext.OrganizationEntitlementSnapshots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.SyncedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<int> ActiveWorkspaceCountAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.Workspaces
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.SoftDeletedAt == null, cancellationToken);

    public Task<int> ActiveManagedInstanceCountAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.ElsaInstances
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.TargetMode == "managed" && x.DeletedAt == null, cancellationToken);

    public Task<bool> OrganizationWorkspaceNameExistsAsync(Guid organizationId, string name, Guid? excludingWorkspaceId, CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        return dbContext.Workspaces
            .AsNoTracking()
            .AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.SoftDeletedAt == null &&
                x.Name == normalizedName &&
                (!excludingWorkspaceId.HasValue || x.Id != excludingWorkspaceId.Value), cancellationToken);
    }

    public Task<bool> WorkspaceBelongsToOrganizationAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        dbContext.Workspaces
            .AsNoTracking()
            .AnyAsync(x =>
                x.Id == workspaceId &&
                x.OrganizationId == organizationId &&
                x.Organization!.Status == OrganizationStatus.Active &&
                x.SoftDeletedAt == null, cancellationToken);

    public Task<bool> OrganizationAccountMembershipExistsAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.AccountId == accountId &&
                x.DisabledAt == null &&
                x.Organization!.Status == OrganizationStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<WorkspaceSummary>> ListOrganizationWorkspacesAsync(Guid organizationId, Guid accountId, bool includeAllOrganizationWorkspaces, CancellationToken cancellationToken = default)
    {
        var organizationRole = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AccountId == accountId && x.DisabledAt == null)
            .Select(x => x.Role)
            .SingleAsync(cancellationToken);

        var workspaces = await dbContext.Workspaces
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.Memberships)
            .Where(x => x.OrganizationId == organizationId && x.SoftDeletedAt == null)
            .Where(x => includeAllOrganizationWorkspaces || x.Memberships.Any(membership => membership.AccountId == accountId))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return workspaces
            .Select(x => new WorkspaceSummary(
                x.Id,
                x.Name,
                x.Kind,
                x.Memberships
                    .Where(membership => membership.AccountId == accountId)
                    .Select(membership => membership.Role)
                    .DefaultIfEmpty(WorkspaceRole.Reader)
                    .First(),
                x.OrganizationId,
                x.Organization!.Name,
                organizationRole))
            .ToList();
    }

    public Task<OrganizationWorkspaceMutationResult> CreateOrganizationWorkspaceAsync(Guid organizationId, Guid creatorAccountId, CreateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default) =>
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var workspaceName = request.Name.Trim();

            var entitlement = await dbContext.OrganizationEntitlementSnapshots
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId)
                .OrderByDescending(x => x.SyncedAt)
                .ThenByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (entitlement is null)
                return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.EntitlementRequired);

            var activeWorkspaceCount = await dbContext.Workspaces
                .AsNoTracking()
                .CountAsync(x => x.OrganizationId == organizationId && x.SoftDeletedAt == null, cancellationToken);
            if (activeWorkspaceCount >= entitlement.MaxWorkspaces)
                return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.WorkspaceLimitReached);

            var duplicateName = await dbContext.Workspaces
                .AsNoTracking()
                .AnyAsync(x =>
                    x.OrganizationId == organizationId &&
                    x.SoftDeletedAt == null &&
                    x.Name == workspaceName, cancellationToken);
            if (duplicateName)
                return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.DuplicateWorkspaceName);

            var workspace = new Workspace
            {
                OrganizationId = organizationId,
                Name = workspaceName,
                Kind = WorkspaceKind.Shared,
                CreatedAt = now,
                UpdatedAt = now
            };

            workspace.Memberships.Add(new WorkspaceMembership
            {
                AccountId = creatorAccountId,
                Workspace = workspace,
                Role = WorkspaceRole.Owner,
                CreatedAt = now,
                UpdatedAt = now
            });

            foreach (var member in request.InitialMembers.Where(x => x.AccountId != creatorAccountId).GroupBy(x => x.AccountId).Select(x => x.Last()))
            {
                workspace.Memberships.Add(new WorkspaceMembership
                {
                    AccountId = member.AccountId,
                    Workspace = workspace,
                    Role = member.Role,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            dbContext.Workspaces.Add(workspace);
            await dbContext.SaveChangesAsync(cancellationToken);
            return OrganizationWorkspaceMutationResult.Success((await WorkspaceSummaryAsync(organizationId, workspace.Id, creatorAccountId, cancellationToken))!);
        }, cancellationToken);

    public async Task<WorkspaceSummary?> UpdateOrganizationWorkspaceAsync(Guid organizationId, Guid workspaceId, Guid accountId, UpdateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var workspace = await dbContext.Workspaces
            .SingleOrDefaultAsync(x => x.Id == workspaceId && x.OrganizationId == organizationId && x.SoftDeletedAt == null, cancellationToken);
        if (workspace is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        workspace.Name = request.Name.Trim();
        workspace.UpdatedAt = now;
        workspace.SoftDeletedAt = request.Status is WorkspaceLifecycleStatus.Archived ? now : null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await WorkspaceSummaryAsync(organizationId, workspaceId, accountId, cancellationToken);
    }

    public async Task<WorkspaceSummary> SetWorkspaceMembershipAsync(Guid organizationId, Guid workspaceId, Guid accountId, WorkspaceRole role, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var membership = await dbContext.WorkspaceMemberships
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.AccountId == accountId, cancellationToken);
        if (membership is null)
        {
            membership = new WorkspaceMembership
            {
                WorkspaceId = workspaceId,
                AccountId = accountId,
                Role = role,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.WorkspaceMemberships.Add(membership);
        }
        else
        {
            membership.Role = role;
            membership.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (await WorkspaceSummaryAsync(organizationId, workspaceId, accountId, cancellationToken))!;
    }

    public async Task<bool> CanRemoveWorkspaceMembershipAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var membership = await dbContext.WorkspaceMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.AccountId == accountId, cancellationToken);
        if (membership is null || membership.Role is not WorkspaceRole.Owner)
            return true;

        var ownerCount = await dbContext.WorkspaceMemberships
            .AsNoTracking()
            .CountAsync(x => x.WorkspaceId == workspaceId && x.Role == WorkspaceRole.Owner, cancellationToken);
        return ownerCount > 1;
    }

    public async Task RemoveWorkspaceMembershipAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await dbContext.WorkspaceMemberships
            .Where(x => x.WorkspaceId == workspaceId && x.AccountId == accountId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<WorkspaceEntitlementSnapshot> SaveEntitlementAsync(WorkspaceEntitlementSnapshot entitlement, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await dbContext.WorkspaceEntitlementSnapshots
            .SingleOrDefaultAsync(x => x.WorkspaceId == entitlement.WorkspaceId, cancellationToken);

        if (existing is null)
        {
            entitlement.SyncedAt = now;
            entitlement.CreatedAt = now;
            entitlement.UpdatedAt = now;
            await dbContext.WorkspaceEntitlementSnapshots.AddAsync(entitlement, cancellationToken);
            existing = entitlement;
        }
        else
        {
            existing.CanCreateCustomSources = entitlement.CanCreateCustomSources;
            existing.MaxSources = entitlement.MaxSources;
            existing.MaxPackagesIndexed = entitlement.MaxPackagesIndexed;
            existing.MaxVersionsPerPackage = entitlement.MaxVersionsPerPackage;
            existing.MaxSyncsPerDay = entitlement.MaxSyncsPerDay;
            existing.PrivateFeedsEnabled = entitlement.PrivateFeedsEnabled;
            existing.SyncedAt = now;
            existing.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public Task<WorkspaceSourceAddResult> TryAddWorkspaceSourceAsync(PackageSource source, int maxSources, CancellationToken cancellationToken = default) =>
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var currentSourceCount = await dbContext.PackageSources.CountAsync(x =>
                x.OwnerWorkspaceId == source.OwnerWorkspaceId &&
                x.Visibility == PackageSourceVisibility.Workspace &&
                x.SoftDeletedAt == null,
                cancellationToken);
            if (currentSourceCount >= maxSources)
                return new WorkspaceSourceAddResult(WorkspaceSourceAddStatus.LimitReached);

            var urlExists = await dbContext.PackageSources.AnyAsync(x =>
                x.OwnerWorkspaceId == source.OwnerWorkspaceId &&
                x.Visibility == PackageSourceVisibility.Workspace &&
                x.SoftDeletedAt == null &&
                x.Url == source.Url,
                cancellationToken);
            if (urlExists)
                return new WorkspaceSourceAddResult(WorkspaceSourceAddStatus.DuplicateUrl);

            await dbContext.PackageSources.AddAsync(source, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WorkspaceSourceAddResult(WorkspaceSourceAddStatus.Created);
        }, cancellationToken);

    public async Task<IReadOnlyList<PackageSource>> ListVisibleSourcesAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
        await dbContext.PackageSources
            .AsNoTracking()
            .Where(x => x.Enabled && x.Browseable && x.SoftDeletedAt == null)
            .Where(x =>
                (x.Visibility == PackageSourceVisibility.Public && x.OwnerWorkspaceId == null) ||
                (x.Visibility == PackageSourceVisibility.Workspace && x.OwnerWorkspaceId == workspaceId && x.OwnerWorkspace!.SoftDeletedAt == null))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> GetPackageCountsAsync(IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken = default) =>
        await dbContext.Packages
            .AsNoTracking()
            .Where(x => sourceIds.Contains(x.SourceId))
            .GroupBy(x => x.SourceId)
            .Select(x => new { SourceId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            dbContext.ChangeTracker.Clear();
            throw new AccountWorkspaceConflictException("A concurrent account workspace update conflicted with this operation.", ex);
        }
    }

    private async Task<WorkspaceSummary?> WorkspaceSummaryAsync(Guid organizationId, Guid workspaceId, Guid accountId, CancellationToken cancellationToken)
    {
        var workspace = await dbContext.Workspaces
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.Memberships)
            .SingleOrDefaultAsync(x => x.Id == workspaceId && x.OrganizationId == organizationId, cancellationToken);
        if (workspace is null)
            return null;

        var organizationRole = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AccountId == accountId && x.DisabledAt == null)
            .Select(x => (OrganizationRole?)x.Role)
            .FirstOrDefaultAsync(cancellationToken) ?? OrganizationRole.Member;

        return new WorkspaceSummary(
            workspace.Id,
            workspace.Name,
            workspace.Kind,
            workspace.Memberships
                .Where(x => x.AccountId == accountId)
                .Select(x => x.Role)
                .DefaultIfEmpty(WorkspaceRole.Reader)
                .First(),
            workspace.OrganizationId,
            workspace.Organization!.Name,
            organizationRole);
    }

    private static OrganizationRole OrganizationRoleFor(IEnumerable<OrganizationMembership> memberships, Guid organizationId) =>
        memberships
            .Where(x => x.OrganizationId == organizationId && x.DisabledAt == null)
            .Select(x => x.Role)
            .DefaultIfEmpty(OrganizationRole.Member)
            .First();
}
