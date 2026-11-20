using ElsaControl.PackageCatalog.Core.Packages;

namespace ElsaControl.PackageCatalog.Core.Packaging;

public interface IPackageVersionDiscoveryClient
{
    Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exact package-id/version lookup against a NuGet V3 service index.
    /// Implementations must not include the feed URL or credentials in exceptions.
    /// </summary>
    Task<bool> PackageVersionExistsAsync(
        string serviceIndexUrl,
        string packageId,
        string version,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Exact package-version lookup is not supported by this client.");
}

public interface IPackageArchiveDownloader
{
    Task<Stream> DownloadPackageAsync(PackageSource source, string packageId, string version, CancellationToken cancellationToken = default);
}

public interface IPackageArchiveManifestReader
{
    Task<PackageManifestReadResult> ReadAsync(Stream packageStream, CancellationToken cancellationToken = default);
}

public sealed record DiscoveredPackageVersion(
    string PackageId,
    string Version,
    DateTimeOffset? PublishedAt = null);

public sealed record PackageManifestReadResult(
    bool Exists,
    string? Path,
    string? ManifestJson,
    string? ManifestHash,
    IReadOnlyList<string> Warnings)
{
    public static PackageManifestReadResult Missing() => new(false, null, null, null, []);

    public static PackageManifestReadResult Found(string path, string manifestJson, string manifestHash, IReadOnlyList<string> warnings) =>
        new(true, path, manifestJson, manifestHash, warnings);
}
