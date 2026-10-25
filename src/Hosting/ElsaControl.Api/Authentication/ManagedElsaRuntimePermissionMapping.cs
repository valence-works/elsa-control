using System.Collections.Frozen;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Maps authenticated Control roles to exact Elsa Studio permissions for the runtime's grant contract.
/// The workspace role sets the ceiling; the provider-confirmed deployment release selects v1 or v2
/// (see <see cref="ManagedElsaRuntimePermissions.UsesStudioGrantsV2"/>). A missing confirmed release
/// keeps v1:
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
    public const string StructuredLogsReadV2 = ManagedElsaRuntimePermissions.StructuredLogsReadV2;
    public const string StudioAccessFull = "full";
    public const string StudioAccessRoleLimited = "role-limited";
    public const string StudioAccessDeploymentLimited = "deployment-limited";

    private static readonly IReadOnlySet<string> Empty = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StructuredLogsV1 =
        ManagedElsaRuntimePermissions.DiagnosticsOnly.ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StructuredLogsV2 =
        ManagedElsaRuntimePermissions.DiagnosticsOnlyV2.ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StudioV1 =
        ManagedElsaRuntimePermissions.StudioGrantsV1.ToFrozenSet(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> StudioV2 =
        ManagedElsaRuntimePermissions.StudioGrantsV2.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Union of every permission Control may ever issue. The deployed image receives one contract's set.</summary>
    public static IReadOnlyList<string> AllowedPermissions { get; } = ManagedElsaRuntimePermissions.AllSupported;

    public static IReadOnlySet<string> For(
        WorkspaceAccess access,
        bool studioGrantsSupported = false,
        string? releaseLine = null,
        string? releaseVersion = null)
    {
        ArgumentNullException.ThrowIfNull(access);

        var v2 = ManagedElsaRuntimePermissions.UsesStudioGrantsV2(releaseLine, releaseVersion);
        if (access.Role is WorkspaceRole.Owner)
            return studioGrantsSupported
                ? (v2 ? StudioV2 : StudioV1)
                : (v2 ? StructuredLogsV2 : StructuredLogsV1);

        // SourceAdmin only governs package-source operations, and a Reader only views. Organization owners and
        // administrators keep the Structured Logs read they have always had, and nothing more.
        return access.OrganizationRole is OrganizationRole.Owner or OrganizationRole.Administrator
            ? (v2 ? StructuredLogsV2 : StructuredLogsV1)
            : Empty;
    }

    public static string StudioAccessFor(WorkspaceRole role, bool studioGrantsSupported) =>
        role is not WorkspaceRole.Owner
            ? StudioAccessRoleLimited
            : studioGrantsSupported
                ? StudioAccessFull
                : StudioAccessDeploymentLimited;

    public static bool IsSupported(string permission) => ManagedElsaRuntimePermissions.IsSupported(permission);

    public static bool IsSupportedSet(IEnumerable<string> permissions) =>
        permissions is not null && permissions.All(IsSupported);
}
