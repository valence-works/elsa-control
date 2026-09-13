namespace ElsaControl.PackageCatalog.Core.Accounts;

public sealed class AccountWorkspaceService
{
    private readonly IAccountWorkspaceStore _store;
    private readonly IReadOnlyList<IWorkspaceOwnerProvisioner> _ownerProvisioners;

    public AccountWorkspaceService(IAccountWorkspaceStore store)
        : this(store, [])
    {
    }

    public AccountWorkspaceService(
        IAccountWorkspaceStore store,
        IEnumerable<IWorkspaceOwnerProvisioner> ownerProvisioners)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ownerProvisioners);
        _store = store;
        _ownerProvisioners = ownerProvisioners.ToList();
    }

    public async Task<AccountWorkspaceContext> GetOrCreateAsync(TrustedWorkspaceIdentity identity, CancellationToken cancellationToken = default)
    {
        var normalized = identity.Normalize();
        // A conflicting concurrent first sign-in has created either this identity or, for a
        // customer Entra tenant, the tenant's organization. One retry observes whichever won.
        for (var attempt = 1; ; attempt++)
        {
            var existing = await _store.FindByExternalIdentityAsync(normalized.Issuer, normalized.Subject, cancellationToken);
            if (existing is not null)
                return await RefreshAsync(existing, normalized, cancellationToken);

            try
            {
                return await CreateAccountAsync(normalized, cancellationToken);
            }
            catch (AccountWorkspaceConflictException) when (attempt == 1)
            {
            }
        }
    }

    private async Task<AccountWorkspaceContext> RefreshAsync(
        ExternalIdentityLookup existing,
        TrustedWorkspaceIdentity identity,
        CancellationToken cancellationToken)
    {
        await _store.UpdateExternalIdentitySeenAsync(existing.ExternalIdentityId, identity.DisplayName, identity.Email, cancellationToken);
        await ProvisionOwnedWorkspacesAsync(existing.Context, cancellationToken);
        return existing.Context with
        {
            Account = existing.Context.Account with
            {
                DisplayName = identity.DisplayName,
                Email = identity.Email
            }
        };
    }

    private async Task<AccountWorkspaceContext> CreateAccountAsync(TrustedWorkspaceIdentity identity, CancellationToken cancellationToken)
    {
        var account = new Account
        {
            DisplayName = identity.DisplayName,
            Email = identity.Email
        };
        account.ExternalIdentities.Add(new ExternalIdentity
        {
            Account = account,
            Issuer = identity.Issuer,
            Subject = identity.Subject,
            DisplayName = identity.DisplayName,
            Email = identity.Email
        });

        if (identity.CustomerEntraTenantId is { } tenantId &&
            await _store.FindOrganizationByEntraTenantIdAsync(tenantId, cancellationToken) is { } tenantOrganization)
            return await JoinTenantOrganizationAsync(account, tenantOrganization, cancellationToken);

        return await MintOwnedOrganizationAsync(account, identity, cancellationToken);
    }

    /// <summary>
    /// Later sign-ins from a customer tenant join its organization as members without any
    /// workspace access; owners grant workspaces through the existing membership endpoints.
    /// </summary>
    private async Task<AccountWorkspaceContext> JoinTenantOrganizationAsync(
        Account account,
        Organization organization,
        CancellationToken cancellationToken)
    {
        account.OrganizationMemberships.Add(new OrganizationMembership
        {
            Account = account,
            OrganizationId = organization.Id,
            Role = OrganizationRole.Member
        });

        await _store.AddAccountAsync(account, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        return new AccountWorkspaceContext(new AccountSummary(account.Id, account.DisplayName, account.Email), [])
        {
            Organizations = organization.Status == OrganizationStatus.Active
                ? [new OrganizationSummary(organization.Id, organization.Name, OrganizationRole.Member)]
                : []
        };
    }

    /// <summary>
    /// Mints the organization and default workspace the account owns: a personal one, or the
    /// shared one bound to the customer's Entra tenant on that tenant's first sign-in.
    /// </summary>
    private async Task<AccountWorkspaceContext> MintOwnedOrganizationAsync(
        Account account,
        TrustedWorkspaceIdentity identity,
        CancellationToken cancellationToken)
    {
        var tenantId = identity.CustomerEntraTenantId;
        var workspace = tenantId is null
            ? new Workspace
            {
                Name = string.IsNullOrWhiteSpace(identity.DisplayName) ? "Personal Workspace" : identity.DisplayName,
                Kind = WorkspaceKind.Personal
            }
            : new Workspace
            {
                Name = TenantOrganizationName(identity.Email, tenantId),
                Kind = WorkspaceKind.Shared
            };
        var organization = new Organization
        {
            Name = workspace.Name,
            CreatedByAccountId = account.Id,
            Workspaces = { workspace }
        };
        if (tenantId is not null)
            organization.IdentityBinding = new OrganizationIdentityBinding { Organization = organization, EntraTenantId = tenantId };
        var organizationMembership = new OrganizationMembership
        {
            Account = account,
            Organization = organization,
            Role = OrganizationRole.Owner
        };
        var membership = new WorkspaceMembership
        {
            Account = account,
            Workspace = workspace,
            Role = WorkspaceRole.Owner
        };

        account.OrganizationMemberships.Add(organizationMembership);
        account.Memberships.Add(membership);
        organization.Memberships.Add(organizationMembership);
        workspace.Memberships.Add(membership);

        await _store.AddAccountAsync(account, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);
        await ProvisionOwnerAsync(workspace.Id, account.Id, cancellationToken);

        return new AccountWorkspaceContext(
            new AccountSummary(account.Id, account.DisplayName, account.Email),
            [new WorkspaceSummary(workspace.Id, workspace.Name, workspace.Kind, WorkspaceRole.Owner, organization.Id, organization.Name, OrganizationRole.Owner)])
        {
            Organizations = [new OrganizationSummary(organization.Id, organization.Name, OrganizationRole.Owner)]
        };
    }

    // Entra tokens carry no tenant display name, so the binder's sign-in domain is the
    // readable default. Display claims only name the organization; the tenant id keys it.
    private static string TenantOrganizationName(string? email, string tenantId)
    {
        var domain = email?[(email.LastIndexOf('@') + 1)..];
        return string.IsNullOrWhiteSpace(domain) || domain == email ? $"Entra tenant {tenantId}" : domain;
    }

    public async Task<WorkspaceAccess?> GetWorkspaceAccessAsync(TrustedWorkspaceIdentity identity, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var normalized = identity.Normalize();
        var existing = await _store.FindByExternalIdentityAsync(normalized.Issuer, normalized.Subject, cancellationToken);
        if (existing is null)
            return null;

        var workspace = existing.Context.Workspaces.SingleOrDefault(x => x.Id == workspaceId);
        if (workspace is null)
            return null;

        await _store.UpdateExternalIdentitySeenAsync(existing.ExternalIdentityId, normalized.DisplayName, normalized.Email, cancellationToken);
        if (workspace.Role == WorkspaceRole.Owner)
            await ProvisionOwnerAsync(workspace.Id, existing.Context.Account.Id, cancellationToken);
        return new WorkspaceAccess(existing.Context.Account.Id, workspace.Id, workspace.Role, workspace.OrganizationId, workspace.OrganizationRole);
    }

    public Task<OrganizationEntitlementSnapshot?> GetLatestOrganizationEntitlementAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _store.GetLatestOrganizationEntitlementAsync(organizationId, cancellationToken);

    /// <summary>
    /// Creates an operator-owned organization and its default shared workspace through the
    /// catalog mint path. The optional owner defaults to the current trusted identity; API-key
    /// callers must therefore provide an explicit owner account id.
    /// </summary>
    public async Task<OrganizationCreateResult> CreateOrganizationAsync(
        string? name,
        Guid? ownerAccountId,
        TrustedWorkspaceIdentity? currentIdentity,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
            return OrganizationCreateResult.Denied(OrganizationCreateFailure.NameRequired);
        if (normalizedName.Length > 256)
            return OrganizationCreateResult.Denied(OrganizationCreateFailure.NameTooLong);

        ExternalIdentityLookup? currentAccount = null;
        if (currentIdentity is not null)
            currentAccount = await _store.FindByExternalIdentityAsync(
                currentIdentity.Issuer.Trim(),
                currentIdentity.Subject.Trim(),
                cancellationToken);

        var resolvedOwnerAccountId = ownerAccountId;
        if (!resolvedOwnerAccountId.HasValue)
        {
            if (currentIdentity is null)
                return OrganizationCreateResult.Denied(OrganizationCreateFailure.OwnerAccountRequired);
            if (currentAccount is null)
                return OrganizationCreateResult.Denied(OrganizationCreateFailure.OwnerAccountRequired);

            resolvedOwnerAccountId = currentAccount.Context.Account.Id;
        }

        var result = await _store.CreateOrganizationAsync(new CreateOrganizationRequest(
            normalizedName,
            resolvedOwnerAccountId.Value,
            currentAccount?.Context.Account.Id,
            operatorSubject), cancellationToken);
        if (!result.Succeeded)
            return result;

        await ProvisionOwnerAsync(result.WorkspaceId, result.OwnerAccountId, cancellationToken);
        return result;
    }

    public Task<int> ActiveWorkspaceCountAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _store.ActiveWorkspaceCountAsync(organizationId, cancellationToken);

    public Task<int> ActiveManagedInstanceCountAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        _store.ActiveManagedInstanceCountAsync(organizationId, cancellationToken);

    public async Task<OrganizationWorkspaceListResult> ListOrganizationWorkspacesAsync(TrustedWorkspaceIdentity identity, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var accountContext = await GetOrCreateAsync(identity, cancellationToken);
        var organization = accountContext.Organizations.SingleOrDefault(x => x.Id == organizationId);
        if (organization is null)
            return OrganizationWorkspaceListResult.Denied(OrganizationWorkspaceFailure.OrganizationNotAllowed);

        var canSeeAll = OrganizationRolePolicy.Allows(organization.Role, OrganizationOperation.ManageWorkspaces);
        var workspaces = await _store.ListOrganizationWorkspacesAsync(organizationId, accountContext.Account.Id, canSeeAll, cancellationToken);
        return OrganizationWorkspaceListResult.Success(workspaces);
    }

    public async Task<OrganizationWorkspaceMutationResult> CreateOrganizationWorkspaceAsync(TrustedWorkspaceIdentity identity, Guid organizationId, CreateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var access = await ResolveOrganizationAccessAsync(identity, organizationId, OrganizationOperation.CreateWorkspace, cancellationToken);
        if (!access.Succeeded)
            return OrganizationWorkspaceMutationResult.Denied(access.Failure!.Value);

        foreach (var accountId in request.InitialMembers.Select(x => x.AccountId).Where(x => x != access.AccountId!.Value).Distinct())
        {
            if (!await _store.OrganizationAccountMembershipExistsAsync(organizationId, accountId, cancellationToken))
                return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.TargetAccountNotOrganizationMember);
        }

        var result = await _store.CreateOrganizationWorkspaceAsync(organizationId, access.AccountId!.Value, request, cancellationToken);
        if (!result.Succeeded)
            return result;

        var ownerAccountIds = request.InitialMembers
            .Where(x => x.Role is WorkspaceRole.Owner)
            .Select(x => x.AccountId)
            .Append(access.AccountId.Value)
            .Distinct();
        foreach (var ownerAccountId in ownerAccountIds)
            await ProvisionOwnerAsync(result.Workspace!.Id, ownerAccountId, cancellationToken);

        return result;
    }

    public async Task<OrganizationWorkspaceMutationResult> UpdateOrganizationWorkspaceAsync(TrustedWorkspaceIdentity identity, Guid organizationId, Guid workspaceId, UpdateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        var access = await ResolveOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageWorkspaces, cancellationToken);
        if (!access.Succeeded)
            return OrganizationWorkspaceMutationResult.Denied(access.Failure!.Value);

        if (!await _store.WorkspaceBelongsToOrganizationAsync(organizationId, workspaceId, cancellationToken))
            return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.WorkspaceNotFound);

        if (await _store.OrganizationWorkspaceNameExistsAsync(organizationId, request.Name, workspaceId, cancellationToken))
            return OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.DuplicateWorkspaceName);

        var workspace = await _store.UpdateOrganizationWorkspaceAsync(organizationId, workspaceId, access.AccountId!.Value, request, cancellationToken);
        return workspace is null
            ? OrganizationWorkspaceMutationResult.Denied(OrganizationWorkspaceFailure.WorkspaceNotFound)
            : OrganizationWorkspaceMutationResult.Success(workspace);
    }

    public async Task<OrganizationWorkspaceMembershipResult> SetWorkspaceMembershipAsync(TrustedWorkspaceIdentity identity, Guid organizationId, Guid workspaceId, Guid accountId, WorkspaceRole role, CancellationToken cancellationToken = default)
    {
        var access = await ResolveOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageWorkspaceMembers, cancellationToken);
        if (!access.Succeeded)
            return OrganizationWorkspaceMembershipResult.Denied(access.Failure!.Value);

        if (!await _store.WorkspaceBelongsToOrganizationAsync(organizationId, workspaceId, cancellationToken))
            return OrganizationWorkspaceMembershipResult.Denied(OrganizationWorkspaceFailure.WorkspaceNotFound);

        if (!await _store.OrganizationAccountMembershipExistsAsync(organizationId, accountId, cancellationToken))
            return OrganizationWorkspaceMembershipResult.Denied(OrganizationWorkspaceFailure.TargetAccountNotOrganizationMember);

        var membership = await _store.SetWorkspaceMembershipAsync(organizationId, workspaceId, accountId, role, cancellationToken);
        if (role is WorkspaceRole.Owner)
            await ProvisionOwnerAsync(workspaceId, accountId, cancellationToken);
        return OrganizationWorkspaceMembershipResult.Success(membership);
    }

    public async Task<OrganizationWorkspaceMembershipResult> RemoveWorkspaceMembershipAsync(TrustedWorkspaceIdentity identity, Guid organizationId, Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var access = await ResolveOrganizationAccessAsync(identity, organizationId, OrganizationOperation.ManageWorkspaceMembers, cancellationToken);
        if (!access.Succeeded)
            return OrganizationWorkspaceMembershipResult.Denied(access.Failure!.Value);

        if (!await _store.WorkspaceBelongsToOrganizationAsync(organizationId, workspaceId, cancellationToken))
            return OrganizationWorkspaceMembershipResult.Denied(OrganizationWorkspaceFailure.WorkspaceNotFound);

        if (!await _store.CanRemoveWorkspaceMembershipAsync(workspaceId, accountId, cancellationToken))
            return OrganizationWorkspaceMembershipResult.Denied(OrganizationWorkspaceFailure.LastWorkspaceOwner);

        await _store.RemoveWorkspaceMembershipAsync(workspaceId, accountId, cancellationToken);
        return OrganizationWorkspaceMembershipResult.Removed();
    }

    public Task<OrganizationAccessResult> GetOrganizationAccessAsync(
        TrustedWorkspaceIdentity identity,
        Guid organizationId,
        OrganizationOperation operation,
        CancellationToken cancellationToken = default) =>
        ResolveOrganizationAccessAsync(identity, organizationId, operation, cancellationToken);

    private async Task<OrganizationAccessResult> ResolveOrganizationAccessAsync(TrustedWorkspaceIdentity identity, Guid organizationId, OrganizationOperation operation, CancellationToken cancellationToken)
    {
        var accountContext = await GetOrCreateAsync(identity, cancellationToken);
        var organization = accountContext.Organizations.SingleOrDefault(x => x.Id == organizationId);
        if (organization is null)
            return OrganizationAccessResult.Denied(OrganizationWorkspaceFailure.OrganizationNotAllowed);

        return OrganizationRolePolicy.Allows(organization.Role, operation)
            ? OrganizationAccessResult.Success(accountContext.Account.Id, organization.Role)
            : OrganizationAccessResult.Denied(OrganizationWorkspaceFailure.OrganizationRoleNotAllowed);
    }

    private async Task ProvisionOwnerAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken)
    {
        foreach (var provisioner in _ownerProvisioners)
            await provisioner.ProvisionAsync(workspaceId, accountId, cancellationToken);
    }

    private async Task ProvisionOwnedWorkspacesAsync(
        AccountWorkspaceContext context,
        CancellationToken cancellationToken)
    {
        foreach (var workspace in context.Workspaces.Where(x => x.Role == WorkspaceRole.Owner))
            await ProvisionOwnerAsync(workspace.Id, context.Account.Id, cancellationToken);
    }
}

public interface IAccountWorkspaceStore
{
    Task<ExternalIdentityLookup?> FindByExternalIdentityAsync(string issuer, string subject, CancellationToken cancellationToken = default);
    Task<Organization?> FindOrganizationByEntraTenantIdAsync(string tenantId, CancellationToken cancellationToken = default);
    Task<OrganizationCreateResult> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default);
    Task AddAccountAsync(Account account, CancellationToken cancellationToken = default);
    Task UpdateExternalIdentitySeenAsync(Guid externalIdentityId, string? displayName, string? email, CancellationToken cancellationToken = default);
    Task<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<OrganizationEntitlementSnapshot?> GetLatestOrganizationEntitlementAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<int> ActiveWorkspaceCountAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<int> ActiveManagedInstanceCountAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<bool> OrganizationWorkspaceNameExistsAsync(Guid organizationId, string name, Guid? excludingWorkspaceId, CancellationToken cancellationToken = default);
    Task<bool> WorkspaceBelongsToOrganizationAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default);
    Task<bool> OrganizationAccountMembershipExistsAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceSummary>> ListOrganizationWorkspacesAsync(Guid organizationId, Guid accountId, bool includeAllOrganizationWorkspaces, CancellationToken cancellationToken = default);
    Task<OrganizationWorkspaceMutationResult> CreateOrganizationWorkspaceAsync(Guid organizationId, Guid creatorAccountId, CreateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceSummary?> UpdateOrganizationWorkspaceAsync(Guid organizationId, Guid workspaceId, Guid accountId, UpdateOrganizationWorkspaceRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceSummary> SetWorkspaceMembershipAsync(Guid organizationId, Guid workspaceId, Guid accountId, WorkspaceRole role, CancellationToken cancellationToken = default);
    Task<bool> CanRemoveWorkspaceMembershipAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default);
    Task RemoveWorkspaceMembershipAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken = default);
    Task<WorkspaceEntitlementSnapshot?> GetLatestEntitlementAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<WorkspaceEntitlementSnapshot> SaveEntitlementAsync(WorkspaceEntitlementSnapshot entitlement, CancellationToken cancellationToken = default);
    Task<WorkspaceSourceAddResult> TryAddWorkspaceSourceAsync(Packages.PackageSource source, int maxSources, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Packages.PackageSource>> ListVisibleSourcesAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, int>> GetPackageCountsAsync(IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record TrustedWorkspaceIdentity(string Issuer, string Subject, string? DisplayName, string? Email)
{
    /// <summary>
    /// The customer Microsoft Entra tenant (<c>tid</c>) of a validated multi-tenant sign-in.
    /// A new account from such a tenant joins, or on first sign-in mints, the organization
    /// bound to it. Null for every other identity, including Valence dogfood tenants.
    /// </summary>
    public string? CustomerEntraTenantId { get; init; }

    public TrustedWorkspaceIdentity Normalize() =>
        new(Issuer.Trim(), Subject.Trim(), NormalizeBlank(DisplayName), NormalizeBlank(Email))
        {
            CustomerEntraTenantId = NormalizeBlank(CustomerEntraTenantId)
        };

    private static string? NormalizeBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ExternalIdentityLookup(Guid ExternalIdentityId, AccountWorkspaceContext Context);

public sealed class AccountWorkspaceConflictException(string message, Exception innerException) : Exception(message, innerException);

public sealed partial record AccountWorkspaceContext(AccountSummary Account, IReadOnlyList<WorkspaceSummary> Workspaces);

public sealed partial record AccountWorkspaceContext
{
    public IReadOnlyList<OrganizationSummary> Organizations { get; init; } = [];
}

public sealed record AccountSummary(Guid Id, string? DisplayName, string? Email);

public sealed record OrganizationSummary(Guid Id, string Name, OrganizationRole Role);

public sealed record WorkspaceSummary(
    Guid Id,
    string Name,
    WorkspaceKind Kind,
    WorkspaceRole Role,
    Guid OrganizationId = default,
    string OrganizationName = "",
    OrganizationRole OrganizationRole = ElsaControl.PackageCatalog.Core.Accounts.OrganizationRole.Member);

public sealed record WorkspaceAccess(
    Guid AccountId,
    Guid WorkspaceId,
    WorkspaceRole Role,
    Guid OrganizationId = default,
    OrganizationRole OrganizationRole = ElsaControl.PackageCatalog.Core.Accounts.OrganizationRole.Member)
{
    public bool CanAdministerSources => Role is WorkspaceRole.Owner or WorkspaceRole.SourceAdmin;
}

public sealed record CreateOrganizationWorkspaceRequest(string Name, IReadOnlyList<InitialWorkspaceMember> InitialMembers);

public sealed record CreateOrganizationRequest(
    string Name,
    Guid OwnerAccountId,
    Guid? ActorAccountId,
    string? OperatorSubject);

public enum OrganizationCreateFailure
{
    NameRequired,
    NameTooLong,
    OwnerAccountRequired,
    OwnerAccountNotFound
}

public sealed record OrganizationCreateResult(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid OwnerAccountId,
    OrganizationCreateFailure? Failure = null)
{
    public bool Succeeded => Failure is null && OrganizationId != Guid.Empty && WorkspaceId != Guid.Empty;

    public static OrganizationCreateResult Denied(OrganizationCreateFailure failure) => new(Guid.Empty, Guid.Empty, Guid.Empty, failure);
}

public sealed record InitialWorkspaceMember(Guid AccountId, WorkspaceRole Role);

public sealed record UpdateOrganizationWorkspaceRequest(string Name, WorkspaceLifecycleStatus Status);

public enum WorkspaceLifecycleStatus
{
    Active,
    Archived
}

public enum OrganizationOperation
{
    ViewOrganization,
    CreateWorkspace,
    ManageWorkspaces,
    ManageWorkspaceMembers,
    ManageBilling
}

public enum OrganizationWorkspaceFailure
{
    OrganizationNotAllowed,
    OrganizationRoleNotAllowed,
    EntitlementRequired,
    WorkspaceLimitReached,
    DuplicateWorkspaceName,
    WorkspaceNotFound,
    TargetAccountNotOrganizationMember,
    LastWorkspaceOwner
}

public sealed record OrganizationAccessResult(Guid? AccountId, OrganizationRole? Role, OrganizationWorkspaceFailure? Failure)
{
    public bool Succeeded => AccountId.HasValue && Role.HasValue && Failure is null;

    public static OrganizationAccessResult Success(Guid accountId, OrganizationRole role) => new(accountId, role, null);

    public static OrganizationAccessResult Denied(OrganizationWorkspaceFailure failure) => new(null, null, failure);
}

public sealed record OrganizationWorkspaceListResult(IReadOnlyList<WorkspaceSummary> Workspaces, OrganizationWorkspaceFailure? Failure)
{
    public bool Succeeded => Failure is null;

    public static OrganizationWorkspaceListResult Success(IReadOnlyList<WorkspaceSummary> workspaces) => new(workspaces, null);

    public static OrganizationWorkspaceListResult Denied(OrganizationWorkspaceFailure failure) => new([], failure);
}

public sealed record OrganizationWorkspaceMutationResult(WorkspaceSummary? Workspace, OrganizationWorkspaceFailure? Failure)
{
    public bool Succeeded => Workspace is not null && Failure is null;

    public static OrganizationWorkspaceMutationResult Success(WorkspaceSummary workspace) => new(workspace, null);

    public static OrganizationWorkspaceMutationResult Denied(OrganizationWorkspaceFailure failure) => new(null, failure);
}

public sealed record OrganizationWorkspaceMembershipResult(WorkspaceSummary? Workspace, bool WasRemoved, OrganizationWorkspaceFailure? Failure)
{
    public bool Succeeded => Failure is null;

    public static OrganizationWorkspaceMembershipResult Success(WorkspaceSummary workspace) => new(workspace, false, null);

    public static OrganizationWorkspaceMembershipResult Removed() => new(null, true, null);

    public static OrganizationWorkspaceMembershipResult Denied(OrganizationWorkspaceFailure failure) => new(null, false, failure);
}

public static class OrganizationRolePolicy
{
    public static bool Allows(OrganizationRole role, OrganizationOperation operation) =>
        operation switch
        {
            OrganizationOperation.ViewOrganization => true,
            OrganizationOperation.CreateWorkspace => role is OrganizationRole.Owner or OrganizationRole.Administrator or OrganizationRole.WorkspaceCreator,
            OrganizationOperation.ManageWorkspaces => role is OrganizationRole.Owner or OrganizationRole.Administrator,
            OrganizationOperation.ManageWorkspaceMembers => role is OrganizationRole.Owner or OrganizationRole.Administrator,
            OrganizationOperation.ManageBilling => role is OrganizationRole.Owner or OrganizationRole.Administrator or OrganizationRole.BillingAdmin,
            _ => false
        };
}

public sealed record WorkspaceSourceAddResult(WorkspaceSourceAddStatus Status);

public enum WorkspaceSourceAddStatus
{
    Created,
    LimitReached,
    DuplicateUrl
}
