using System.Collections.Frozen;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Maps authenticated Control workspace and organization roles to the small set of runtime
/// permissions currently supported by the managed handoff contract.
/// </summary>
public static class ManagedElsaRuntimePermissionMapping
{
    public const string StructuredLogsRead = "read:diagnostics:structured-logs";

    private static readonly IReadOnlySet<string> Empty = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StructuredLogsAdministrator =
        new[] { StructuredLogsRead }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> AllowedPermissions { get; } = [StructuredLogsRead];

    public static IReadOnlySet<string> For(WorkspaceAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);

        // SourceAdmin only governs package-source operations. Runtime log access is granted
        // to workspace owners and organization owners/admins who also have instances.open.
        return access.Role is WorkspaceRole.Owner ||
               access.OrganizationRole is OrganizationRole.Owner or OrganizationRole.Administrator
            ? StructuredLogsAdministrator
            : Empty;
    }

    public static bool IsSupported(string permission) =>
        string.Equals(permission, StructuredLogsRead, StringComparison.Ordinal);

    public static bool IsSupportedSet(IEnumerable<string> permissions) =>
        permissions is not null && permissions.All(IsSupported);
}
