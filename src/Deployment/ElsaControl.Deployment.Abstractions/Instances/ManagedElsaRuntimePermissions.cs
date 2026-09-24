using System.Collections.Frozen;

namespace ElsaControl.Deployment.Abstractions.Instances;

/// <summary>
/// Exact Elsa 3.8 runtime grants supported by managed Studio handoff. Keep this
/// list aligned with the managed runtime image; broad read or wildcard grants
/// are deliberately unsupported.
/// </summary>
public static class ManagedElsaRuntimePermissions
{
    public const string StructuredLogsRead = "read:diagnostics:structured-logs";

    public static IReadOnlyList<string> OwnerAdministrator { get; } = Array.AsReadOnly(new[]
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

    private static readonly IReadOnlySet<string> Supported =
        OwnerAdministrator.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsSupported(string? permission) =>
        permission is not null && Supported.Contains(permission);
}
