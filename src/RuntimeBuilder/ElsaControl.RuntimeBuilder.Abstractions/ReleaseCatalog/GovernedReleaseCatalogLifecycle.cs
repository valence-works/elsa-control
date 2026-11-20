using NuGet.Versioning;

namespace ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

/// <summary>
/// Catalog-lifecycle rules that are independent of storage. A row may be
/// <c>supported</c> only when its version has no NuGet semver prerelease label.
/// A prerelease is admitted as <c>preview</c> or not at all.
/// </summary>
public static class GovernedReleaseCatalogLifecycle
{
    public const string Supported = "supported";
    public const string Preview = "preview";
    public const string SupportedPrereleaseCode = "catalog.lifecycle.supported.prerelease";

    public static bool IsSupported(string? catalogLifecycle) =>
        string.Equals(catalogLifecycle, Supported, StringComparison.OrdinalIgnoreCase);

    public static bool IsPreview(string? catalogLifecycle) =>
        string.Equals(catalogLifecycle, Preview, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A version is stable when NuGet semver parses it and it has no prerelease label.
    /// Unparseable versions are not stable, so they cannot become <c>supported</c>.
    /// </summary>
    public static bool IsStableReleaseVersion(string? version) =>
        NuGetVersion.TryParse(version, out var parsed) && !parsed.IsPrerelease;

    public static bool CanBeSupported(string? catalogLifecycle, string? version) =>
        !IsSupported(catalogLifecycle) || IsStableReleaseVersion(version);
}
