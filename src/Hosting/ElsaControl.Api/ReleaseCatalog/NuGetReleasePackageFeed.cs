using ElsaControl.PackageCatalog.Core.Packaging;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.ReleaseCatalog;

/// <summary>
/// Resolves declared packages against the configured release feed using the
/// existing NuGet source client. Findings and exceptions never include the
/// feed URL or credentials.
/// </summary>
public sealed class NuGetReleasePackageFeed(
    IPackageVersionDiscoveryClient client,
    IOptions<ReleaseCatalogAdmissionOptions> options) : IReleasePackageFeed
{
    public async Task<IReadOnlySet<ReleasePackageIdentity>> FindExistingAsync(
        IReadOnlyList<ReleasePackageIdentity> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        string feedUrl;
        try
        {
            feedUrl = options.Value.NormalizeReleaseFeedServiceIndex();
        }
        catch (ArgumentException ex)
        {
            throw new ReleasePackageFeedUnavailableException(ex);
        }

        var found = new HashSet<ReleasePackageIdentity>(ReleasePackageIdentityComparer.Instance);
        try
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await client.PackageVersionExistsAsync(
                        feedUrl,
                        package.Id,
                        package.Version,
                        cancellationToken))
                    found.Add(package);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReleasePackageFeedUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ReleasePackageFeedUnavailableException(ex);
        }

        return found;
    }
}
