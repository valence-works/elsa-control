using System.Collections.Frozen;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Maps authenticated Control roles to exact Elsa 3.8 Studio permissions. The workspace role sets the ceiling:
/// <list type="table">
/// <listheader><term>Workspace role</term><description>Runtime grants (with <c>instances.open</c>)</description></listheader>
/// <item><term>Owner</term><description>The Studio grant set on an image that supports it, otherwise Structured Logs read</description></item>
/// <item><term>Reader or SourceAdmin, organization Owner or Administrator</term><description>Structured Logs read</description></item>
/// <item><term>Reader or SourceAdmin, organization Member</term><description>None</description></item>
/// </list>
/// An organization role never raises grants above Structured Logs read. An organization administrator who needs the
/// designer makes themselves workspace Owner, which is an explicit, audited step.
/// </summary>
public static class ManagedElsaRuntimePermissionMapping
{
    public const string StructuredLogsRead = ManagedElsaRuntimePermissions.StructuredLogsRead;

    private static readonly IReadOnlySet<string> Empty = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StructuredLogs =
        ManagedElsaRuntimePermissions.DiagnosticsOnly.ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Studio =
        ManagedElsaRuntimePermissions.StudioGrantsV1.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> AllowedPermissions { get; } = ManagedElsaRuntimePermissions.StudioGrantsV1;

    public static IReadOnlySet<string> For(WorkspaceAccess access, bool studioGrantsSupported = false)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (access.Role is WorkspaceRole.Owner)
            return studioGrantsSupported ? Studio : StructuredLogs;

        // SourceAdmin only governs package-source operations, and a Reader only views. Organization owners and
        // administrators keep the Structured Logs read they have always had, and nothing more.
        return access.OrganizationRole is OrganizationRole.Owner or OrganizationRole.Administrator
            ? StructuredLogs
            : Empty;
    }

    public static bool IsSupported(string permission) => ManagedElsaRuntimePermissions.IsSupported(permission);

    public static bool IsSupportedSet(IEnumerable<string> permissions) =>
        permissions is not null && permissions.All(IsSupported);
}
