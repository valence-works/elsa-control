using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class WorkspaceRolePolicyTests
{
    [Fact]
    public void PartnerProvisioner_cannot_mutate_workspace_resources_through_the_human_allow_list()
    {
        Assert.True(WorkspaceRolePolicy.Allows(WorkspaceRole.Owner, WorkspaceOperation.MutateWorkspaceResource));
        Assert.True(WorkspaceRolePolicy.Allows(WorkspaceRole.SourceAdmin, WorkspaceOperation.MutateWorkspaceResource));
        Assert.False(WorkspaceRolePolicy.Allows(WorkspaceRole.PartnerProvisioner, WorkspaceOperation.MutateWorkspaceResource));
        Assert.True(WorkspaceRolePolicy.Allows(WorkspaceRole.PartnerProvisioner, WorkspaceOperation.Read));
    }
}
