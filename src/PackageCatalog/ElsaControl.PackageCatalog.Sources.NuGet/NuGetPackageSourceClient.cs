using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sources;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ElsaControl.PackageCatalog.Sources.NuGet;

public sealed class NuGetPackageSourceClient(
    PackageSourcePatternMatcher patternMatcher,
    ILogger<NuGetPackageSourceClient>? logger = null) : IPackageVersionDiscoveryClient
{
    private const int SearchPageSize = 100;
    private const int MaxSearchResultsPerPrefix = 1_000;

    public async Task<IReadOnlyList<DiscoveredPackageVersion>> FindPackageVersionsAsync(PackageSource source, CancellationToken cancellationToken = default)
    {
        var repository = Repository.Factory.GetCoreV3(source.Url);
        var packageIds = await DiscoverPackageIdsAsync(source, repository, cancellationToken);
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
        using var cache = new SourceCacheContext();
        var results = new List<DiscoveredPackageVersion>();

        foreach (var packageId in packageIds.Order(StringComparer.OrdinalIgnoreCase))
        {
            var versions = (await resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, cancellationToken)).ToList();
            var selectedVersions = SelectVersionsForPackage(source, packageId, versions, logger);
            results.AddRange(selectedVersions.Select(version => new DiscoveredPackageVersion(packageId, version.ToNormalizedString())));
        }

        return results;
    }

    public async Task<bool> PackageVersionExistsAsync(
        string serviceIndexUrl,
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceIndexUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!NuGetVersion.TryParse(version, out var nugetVersion))
            return false;

        try
        {
            var repository = Repository.Factory.GetCoreV3(serviceIndexUrl);
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
            using var cache = new SourceCacheContext
            {
                NoCache = true,
                DirectDownload = true
            };
            return await resource.DoesPackageExistAsync(
                packageId,
                nugetVersion,
                cache,
                NullLogger.Instance,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The package feed is unavailable.");
        }
    }

    private async Task<IReadOnlyCollection<string>> DiscoverPackageIdsAsync(PackageSource source, SourceRepository repository, CancellationToken cancellationToken)
    {
        var packageIds = source.IncludePatterns
            .Where(IsExactPackageId)
            .Where(packageId => patternMatcher.IsMatch(packageId, source.IncludePatterns, source.ExcludePatterns))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var searchPrefixes = source.IncludePatterns
            .Select(GetSearchPrefix)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (packageIds.Count == 0 && searchPrefixes.Count == 0)
            throw new NotSupportedException("NuGet source discovery requires at least one exact package ID include pattern, dotted prefix include pattern, or prefix wildcard include pattern, for example Elsa. or Elsa.*. Leading wildcard-only sources are not crawled.");

        if (searchPrefixes.Count == 0)
            return packageIds;

        var search = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken);
        if (search is null)
            throw new NotSupportedException("NuGet source discovery for prefix wildcard include patterns requires a feed that advertises a NuGet search service.");

        var filter = new SearchFilter(includePrerelease: true);

        foreach (var prefix in searchPrefixes)
        {
            for (var skip = 0; skip < MaxSearchResultsPerPrefix; skip += SearchPageSize)
            {
                var page = await SearchAsync(search, prefix, filter, skip, cancellationToken);

                foreach (var package in page)
                {
                    var packageId = package.Identity.Id;
                    if (patternMatcher.IsMatch(packageId, source.IncludePatterns, source.ExcludePatterns))
                        packageIds.Add(packageId);
                }

                if (page.Count < SearchPageSize)
                    break;

                if (skip + SearchPageSize >= MaxSearchResultsPerPrefix)
                    throw new InvalidOperationException($"NuGet source discovery for prefix '{prefix}' returned at least {MaxSearchResultsPerPrefix} packages. Narrow the include patterns before syncing.");
            }
        }

        return packageIds;
    }

    private static async Task<List<IPackageSearchMetadata>> SearchAsync(
        PackageSearchResource search,
        string prefix,
        SearchFilter filter,
        int skip,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await search.SearchAsync(prefix, filter, skip, SearchPageSize, NullLogger.Instance, cancellationToken)).ToList();
        }
        catch (FatalProtocolException ex) when (ex.Message.Contains("Search service", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("NuGet source discovery for prefix wildcard include patterns requires a feed that advertises a NuGet search service.", ex);
        }
    }

    internal static IEnumerable<global::NuGet.Versioning.NuGetVersion> SelectVersions(
        PackageSourceVersionDiscoveryPolicy policy,
        IEnumerable<global::NuGet.Versioning.NuGetVersion> versions) =>
        policy switch
        {
            PackageSourceVersionDiscoveryPolicy.LatestStable => versions
                .Where(version => !version.IsPrerelease)
                .OrderDescending()
                .Take(1),
            PackageSourceVersionDiscoveryPolicy.LatestIncludingPrerelease => versions
                .OrderDescending()
                .Take(1),
            PackageSourceVersionDiscoveryPolicy.LatestPreview => versions
                .Where(IsPreviewPrerelease)
                .OrderDescending()
                .Take(1),
            _ => versions
        };

    internal static IReadOnlyList<global::NuGet.Versioning.NuGetVersion> SelectVersionsForPackage(
        PackageSource source,
        string packageId,
        IReadOnlyList<global::NuGet.Versioning.NuGetVersion> versions,
        ILogger<NuGetPackageSourceClient>? logger = null)
    {
        var selectedVersions = SelectVersions(source.VersionDiscoveryPolicy, versions).ToList();
        if (source.VersionDiscoveryPolicy == PackageSourceVersionDiscoveryPolicy.LatestStable && versions.Count > 0 && selectedVersions.Count == 0)
            logger?.LogWarning(
                "Package {PackageId} from source {SourceName} has only prerelease versions and was skipped by the LatestStable version discovery policy.",
                packageId,
                source.Name);

        return selectedVersions;
    }

    private static bool IsExactPackageId(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && !PackageSourcePatternMatcher.IsDottedPrefixPattern(pattern.Trim()) && !pattern.Contains('*') && !pattern.Contains('?');

    private static bool IsPreviewPrerelease(global::NuGet.Versioning.NuGetVersion version)
    {
        if (!version.IsPrerelease)
            return false;

        return version.Release.Equals("preview", StringComparison.OrdinalIgnoreCase)
               || version.Release.StartsWith("preview.", StringComparison.OrdinalIgnoreCase)
               || version.Release.StartsWith("preview-", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSearchPrefix(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var trimmed = pattern.Trim();
        if (PackageSourcePatternMatcher.IsDottedPrefixPattern(trimmed))
            return trimmed;

        if (IsExactPackageId(trimmed))
            return null;

        var wildcardIndex = trimmed.IndexOfAny(['*', '?']);
        if (wildcardIndex <= 0)
            return null;

        return trimmed[..wildcardIndex];
    }
}
