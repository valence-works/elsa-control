using System.Collections.Frozen;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Maps authenticated Control roles to exact Elsa 3.8 Studio permissions.
/// </summary>
public static class ManagedElsaRuntimePermissionMapping
{
    public const string StructuredLogsRead = ManagedElsaRuntimePermissions.StructuredLogsRead;

    private static readonly IReadOnlySet<string> Empty = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> OwnerAdministrator =
        ManagedElsaRuntimePermissions.OwnerAdministrator.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> AllowedPermissions { get; } = ManagedElsaRuntimePermissions.OwnerAdministrator;

    public static IReadOnlySet<string> For(WorkspaceAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);

        // SourceAdmin only governs package-source operations. Studio designer access is granted
        // to workspace owners and organization owners/admins who also have instances.open.
        return access.Role is WorkspaceRole.Owner ||
               access.OrganizationRole is OrganizationRole.Owner or OrganizationRole.Administrator
            ? OwnerAdministrator
            : Empty;
    }

    public static bool IsSupported(string permission) => ManagedElsaRuntimePermissions.IsSupported(permission);

    public static bool IsSupportedSet(IEnumerable<string> permissions) =>
        permissions is not null && permissions.All(IsSupported);
}
