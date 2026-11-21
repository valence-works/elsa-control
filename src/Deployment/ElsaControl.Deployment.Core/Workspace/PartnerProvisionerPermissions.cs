namespace ElsaControl.Deployment.Core.Workspace;

public static class PartnerProvisionerPermissions
{
    public const string RoleName = "PartnerProvisioner";

    public static IReadOnlySet<string> FromScopes(IEnumerable<string> scopes)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            switch (scope)
            {
                case "engines:read":
                    permissions.Add(WorkspaceDeploymentPermissions.Read);
                    permissions.Add(ManagedElsaInstancePermissions.Open);
                    break;
                case "engines:write":
                    permissions.Add(WorkspaceDeploymentPermissions.Read);
                    permissions.Add(WorkspaceDeploymentPermissions.ManageSetup);
                    permissions.Add(WorkspaceDeploymentPermissions.ManageDesiredState);
                    permissions.Add(WorkspaceDeploymentPermissions.ExecuteDeployment);
                    permissions.Add(WorkspaceDeploymentPermissions.ExecuteControls);
                    permissions.Add(ManagedElsaInstancePermissions.Open);
                    break;
                case "engines:delete":
                    permissions.Add(WorkspaceDeploymentPermissions.Read);
                    permissions.Add(ManagedElsaInstancePermissions.Delete);
                    break;
                case "releases:apply":
                    permissions.Add(WorkspaceDeploymentPermissions.Read);
                    permissions.Add(WorkspaceDeploymentPermissions.PreviewPromotion);
                    permissions.Add(WorkspaceDeploymentPermissions.ExecuteDeployment);
                    break;
                case "studio:sessions":
                    permissions.Add(ManagedElsaInstancePermissions.Open);
                    break;
                case "webhooks:manage":
                    break;
            }
        }

        return permissions;
    }
}
