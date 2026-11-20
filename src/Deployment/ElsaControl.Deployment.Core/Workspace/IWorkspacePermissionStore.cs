namespace ElsaControl.Deployment.Core.Workspace;

public interface IWorkspacePermissionStore
{
    Task<DateTimeOffset?> GetWorkspaceMembershipCreatedAtAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default);

    Task<bool> IsPartnerProvisionerAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspacePermissionGrant>> GetPermissionGrantsAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspacePermissionGrant>> ListPermissionGrantsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspacePermissionAuditRecord>> ListPermissionAuditRecordsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default);

    Task<WorkspacePermissionGrant> GrantPermissionAsync(
        Guid workspaceId,
        GrantWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default);

    Task<RevokeWorkspacePermissionResult> RevokePermissionAsync(
        Guid workspaceId,
        RevokeWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default);
}
