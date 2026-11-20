namespace ElsaControl.PackageCatalog.Core.Accounts;

public enum WorkspaceOperation
{
    Read,
    ManageSources,
    MutateWorkspaceResource
}

public enum WorkspaceAccessFailure
{
    MissingIdentity,
    WorkspaceNotAllowed,
    RoleNotAllowed
}

public sealed record WorkspaceAccessResult(WorkspaceAccess? Access, WorkspaceAccessFailure? Failure)
{
    public bool Succeeded => Access is not null && Failure is null;

    public static WorkspaceAccessResult Success(WorkspaceAccess access) => new(access, null);

    public static WorkspaceAccessResult Denied(WorkspaceAccessFailure failure) => new(null, failure);
}

public static class WorkspaceRolePolicy
{
    public static bool Allows(WorkspaceRole role, WorkspaceOperation operation) =>
        operation switch
        {
            WorkspaceOperation.Read => true,
            WorkspaceOperation.ManageSources => role is WorkspaceRole.Owner or WorkspaceRole.SourceAdmin,
            // Keep this separate from ManageSources so a future deployer/runtime role can mutate workspace resources without managing package sources.
            WorkspaceOperation.MutateWorkspaceResource => role is WorkspaceRole.Owner or WorkspaceRole.SourceAdmin,
            _ => false
        };
}
