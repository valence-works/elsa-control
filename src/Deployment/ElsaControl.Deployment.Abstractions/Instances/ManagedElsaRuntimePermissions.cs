using System.Collections.Frozen;
using System.Globalization;

namespace ElsaControl.Deployment.Abstractions.Instances;

/// <summary>
/// Exact Elsa runtime grants supported by managed Studio handoff.
/// <see cref="StudioGrantsV1"/> is the <c>managed-elsa-studio-grants-v1</c> contract for Elsa 3.8 and older;
/// <see cref="StudioGrantsV2"/> is <c>managed-elsa-studio-grants-v2</c> for Elsa 3.9 and newer. Both files live
/// in <c>contracts/</c> and are shared with the runtime image. Broad read or wildcard grants are deliberately
/// unsupported. Contract selection is per runtime release line (see <see cref="UsesStudioGrantsV2"/>).
/// </summary>
public static class ManagedElsaRuntimePermissions
{
    public const string StudioGrantsV1Capability = "managed-elsa-studio-grants-v1";
    public const string StudioGrantsV2Capability = "managed-elsa-studio-grants-v2";

    public const string StructuredLogsRead = "read:diagnostics:structured-logs";
    public const string StructuredLogsReadV2 = "diagnostics/structured-logs:view";

    public static IReadOnlyList<string> DiagnosticsOnly { get; } = Array.AsReadOnly(new[] { StructuredLogsRead });

    public static IReadOnlyList<string> DiagnosticsOnlyV2 { get; } = Array.AsReadOnly(new[] { StructuredLogsReadV2 });

    public static IReadOnlyList<string> StudioGrantsV1 { get; } = Array.AsReadOnly(new[]
    {
        StructuredLogsRead,
        "read:dashboard",
        "read:workflow-definitions",
        "write:workflow-definitions",
        "publish:workflow-definitions",
        "read:activity-descriptors",
        "read:activity-descriptors-options",
        "read:expression-descriptors",
        "read:variable-descriptors",
        "read:output-converters",
        "read:commit-strategies",
        "read:incident-strategies",
        "read:log-persistence-strategies",
        "read:storage-drivers",
        "read:workflow-activation-strategies",
        "read:installed-features"
    });

    public static IReadOnlyList<string> StudioGrantsV2 { get; } = Array.AsReadOnly(new[]
    {
        StructuredLogsReadV2,
        "dashboard:view",
        "workflows/definitions:view",
        "workflows/definitions/versions:view",
        "workflows/definitions:write",
        "workflows/definitions:publish",
        "workflows/definitions/versions:revert",
        "workflows/descriptors/activities:view"
    });

    public static IReadOnlyList<string> AllSupported { get; } = Array.AsReadOnly(
        StudioGrantsV1.Concat(StudioGrantsV2).Distinct(StringComparer.Ordinal).ToArray());

    private static readonly IReadOnlySet<string> Supported =
        AllSupported.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when the runtime's release line (or catalog / manifest version) is Elsa 3.9 or newer.
    /// The release line wins when both are present. An unparseable identity keeps v1.
    /// </summary>
    public static bool UsesStudioGrantsV2(string? releaseLine, string? releaseVersion = null) =>
        TryReadMajorMinor(releaseLine, releaseVersion, out var major, out var minor) &&
        (major > 3 || (major == 3 && minor >= 9));

    public static string RequiredCapability(string? releaseLine, string? releaseVersion = null) =>
        UsesStudioGrantsV2(releaseLine, releaseVersion) ? StudioGrantsV2Capability : StudioGrantsV1Capability;

    public static IReadOnlyList<string> DiagnosticsOnlyFor(string? releaseLine, string? releaseVersion = null) =>
        UsesStudioGrantsV2(releaseLine, releaseVersion) ? DiagnosticsOnlyV2 : DiagnosticsOnly;

    public static IReadOnlyList<string> StudioGrantsFor(string? releaseLine, string? releaseVersion = null) =>
        UsesStudioGrantsV2(releaseLine, releaseVersion) ? StudioGrantsV2 : StudioGrantsV1;

    public static IReadOnlyList<string> SelectedFor(
        bool studioGrantsSupported,
        string? releaseLine,
        string? releaseVersion = null) =>
        studioGrantsSupported
            ? StudioGrantsFor(releaseLine, releaseVersion)
            : DiagnosticsOnlyFor(releaseLine, releaseVersion);

    public static bool IsSupported(string? permission) =>
        permission is not null && Supported.Contains(permission);

    internal static bool TryReadMajorMinor(
        string? releaseLine,
        string? releaseVersion,
        out int major,
        out int minor)
    {
        if (TryParseMajorMinor(releaseLine, out major, out minor))
            return true;
        return TryParseMajorMinor(releaseVersion, out major, out minor);
    }

    private static bool TryParseMajorMinor(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('.', 3, StringSplitOptions.None);
        if (parts.Length < 2)
            return false;

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
               int.TryParse(PrereleasePrefix(parts[1]), NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    private static string PrereleasePrefix(string segment)
    {
        var separator = segment.IndexOf('-');
        return separator < 0 ? segment : segment[..separator];
    }
}
