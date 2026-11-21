using System.Collections.Frozen;

namespace ElsaControl.Deployment.Core.Workspace;

public sealed class WorkspacePermissionService
{
    private readonly IWorkspacePermissionStore _store;
    private readonly IReadOnlySet<string> _allPermissions;
    private readonly IReadOnlySet<string> _ownerDefaults;

    public WorkspacePermissionService(IWorkspacePermissionStore store)
        : this(store, [])
    {
    }

    public WorkspacePermissionService(
        IWorkspacePermissionStore store,
        IEnumerable<IWorkspacePermissionContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(contributions);

        _store = store;
        var contributionList = contributions.ToList();
        _allPermissions = WorkspaceDeploymentPermissions.All
            .Concat(contributionList.SelectMany(x => x.All))
            .ToFrozenSet(StringComparer.Ordinal);
        _ownerDefaults = WorkspaceDeploymentPermissions.All
            .Concat(contributionList.SelectMany(x => x.OwnerDefaults))
            .ToFrozenSet(StringComparer.Ordinal);

        if (!_ownerDefaults.IsSubsetOf(_allPermissions))
            throw new InvalidOperationException("Workspace permission owner defaults must be included in the allowed permission set.");
    }

    public async Task<EffectiveWorkspacePermissions> BootstrapOwnerPermissionsAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (await _store.IsPartnerProvisionerAsync(workspaceId, accountId, cancellationToken))
            throw new UnauthorizedAccessException("PartnerProvisioner cannot be granted Owner.");

        var membershipCreatedAt = await _store.GetWorkspaceMembershipCreatedAtAsync(workspaceId, accountId, cancellationToken)
            ?? throw new ArgumentException("The target account is not a member of this workspace.", nameof(accountId));
        var existingPermissions = (await _store.GetPermissionGrantsAsync(workspaceId, accountId, cancellationToken))
            .Where(x => x.CreatedAt >= membershipCreatedAt)
            .Select(x => x.Permission)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var permission in _ownerDefaults.Where(permission => !existingPermissions.Contains(permission)))
        {
            await _store.GrantPermissionAsync(
                workspaceId,
                new GrantWorkspacePermissionRequest(accountId, permission, accountId),
                cancellationToken);
        }

        return await GetEffectivePermissionsAsync(workspaceId, accountId, cancellationToken);
    }

    public EffectiveWorkspacePermissions GetPartnerProvisionerPermissions(
        Guid workspaceId,
        Guid accountId,
        IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var permissions = PartnerProvisionerPermissions.FromScopes(scopes)
            .Where(_allPermissions.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return new EffectiveWorkspacePermissions(workspaceId, accountId, permissions);
    }

    public async Task<EffectiveWorkspacePermissions> GetEffectivePermissionsAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (await _store.IsPartnerProvisionerAsync(workspaceId, accountId, cancellationToken))
            return new EffectiveWorkspacePermissions(workspaceId, accountId, new HashSet<string>(StringComparer.Ordinal));

        var membershipCreatedAt = await _store.GetWorkspaceMembershipCreatedAtAsync(workspaceId, accountId, cancellationToken);
        if (!membershipCreatedAt.HasValue)
            return new EffectiveWorkspacePermissions(workspaceId, accountId, new HashSet<string>(StringComparer.Ordinal));

        var grants = await _store.GetPermissionGrantsAsync(workspaceId, accountId, cancellationToken);
        var permissions = grants
            .Where(x => x.CreatedAt >= membershipCreatedAt.Value && x.RevokedAt is null && _allPermissions.Contains(x.Permission))
            .Select(x => x.Permission)
            .ToHashSet(StringComparer.Ordinal);

        return new EffectiveWorkspacePermissions(workspaceId, accountId, permissions);
    }

    public async Task RequireAsync(
        Guid workspaceId,
        Guid accountId,
        string permission,
        CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectivePermissionsAsync(workspaceId, accountId, cancellationToken);
        if (!effective.Has(permission))
            throw new UnauthorizedAccessException($"Missing workspace permission '{permission}'.");
    }

    public WorkspacePermissionCatalog GetCatalog() => new(_allPermissions, _ownerDefaults);

    public Task<IReadOnlyList<WorkspacePermissionGrant>> ListGrantsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default) =>
        _store.ListPermissionGrantsAsync(workspaceId, accountId, cancellationToken);

    public Task<IReadOnlyList<WorkspacePermissionAuditRecord>> ListAuditRecordsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default) =>
        _store.ListPermissionAuditRecordsAsync(workspaceId, accountId, cancellationToken);

    public async Task<WorkspacePermissionGrant> GrantAsync(
        Guid workspaceId,
        GrantWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateMutation(workspaceId, request.AccountId, request.Permission, request.GrantedByAccountId, nameof(request));
        await ValidateMutationMembersAsync(workspaceId, request.AccountId, request.GrantedByAccountId, nameof(request), cancellationToken);
        if (await _store.IsPartnerProvisionerAsync(workspaceId, request.AccountId, cancellationToken))
            throw new UnauthorizedAccessException("PartnerProvisioner cannot receive additional workspace permission grants.");

        return await _store.GrantPermissionAsync(workspaceId, request, cancellationToken);
    }

    public async Task<RevokeWorkspacePermissionResult> RevokeAsync(
        Guid workspaceId,
        RevokeWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateMutation(workspaceId, request.AccountId, request.Permission, request.RevokedByAccountId, nameof(request));
        await ValidateMutationMembersAsync(workspaceId, request.AccountId, request.RevokedByAccountId, nameof(request), cancellationToken);

        return await _store.RevokePermissionAsync(workspaceId, request, cancellationToken);
    }

    private void ValidateMutation(Guid workspaceId, Guid accountId, string permission, Guid? actorAccountId, string parameterName)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", parameterName);
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account ID is required.", parameterName);
        if (actorAccountId == Guid.Empty)
            throw new ArgumentException("Actor account ID cannot be empty.", parameterName);
        if (string.IsNullOrWhiteSpace(permission) || !_allPermissions.Contains(permission))
            throw new ArgumentException($"Unknown workspace permission '{permission}'.", parameterName);
    }

    private async Task ValidateMutationMembersAsync(
        Guid workspaceId,
        Guid accountId,
        Guid? actorAccountId,
        string parameterName,
        CancellationToken cancellationToken)
    {
        if (!(await _store.GetWorkspaceMembershipCreatedAtAsync(workspaceId, accountId, cancellationToken)).HasValue)
            throw new ArgumentException("The target account is not a member of this workspace.", parameterName);

        if (actorAccountId.HasValue && actorAccountId.Value != accountId &&
            !(await _store.GetWorkspaceMembershipCreatedAtAsync(workspaceId, actorAccountId.Value, cancellationToken)).HasValue)
        {
            throw new ArgumentException("The actor account is not a member of this workspace.", parameterName);
        }
    }
}
