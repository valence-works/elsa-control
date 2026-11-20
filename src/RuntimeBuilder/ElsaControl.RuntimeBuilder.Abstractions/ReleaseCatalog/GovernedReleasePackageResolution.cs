namespace ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

/// <summary>
/// Admission and promotion package-resolution rules. Declared packages must
/// resolve from the image inventory when the manifest carries one, otherwise
/// from the configured feed. The current producer schema does not publish an
/// <c>/app/packages</c> inventory, so Control checks the feed only.
/// </summary>
public static class GovernedReleasePackageResolution
{
    public const string UnresolvableCode = "catalog.packages.unresolvable";
    public const string FeedUnavailableCode = "catalog.packages.feed.unavailable";

    public static string UnresolvableMessage(IEnumerable<string> packageIds)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        var ids = packageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ids.Length == 0
            ? "Declared packages could not be resolved."
            : $"Declared packages could not be resolved: {string.Join(", ", ids)}.";
    }
}

/// <summary>
/// Exact package identity used by the admission feed lookup. Comparison is
/// ordinal-ignore-case on both id and version.
/// </summary>
public sealed record ReleasePackageIdentity(string Id, string Version);

public sealed class ReleasePackageIdentityComparer : IEqualityComparer<ReleasePackageIdentity>
{
    public static ReleasePackageIdentityComparer Instance { get; } = new();

    public bool Equals(ReleasePackageIdentity? left, ReleasePackageIdentity? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(ReleasePackageIdentity value) =>
        HashCode.Combine(
            value.Id.ToUpperInvariant(),
            value.Version.ToUpperInvariant());
}

/// <summary>
/// Looks up exact package identities on the configured release feed.
/// Implementations must not include the feed URL or credentials in exceptions.
/// </summary>
public interface IReleasePackageFeed
{
    Task<IReadOnlySet<ReleasePackageIdentity>> FindExistingAsync(
        IReadOnlyList<ReleasePackageIdentity> packages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed feed outage. Callers map this to
/// <see cref="GovernedReleasePackageResolution.FeedUnavailableCode"/>.
/// </summary>
public sealed class ReleasePackageFeedUnavailableException : Exception
{
    public ReleasePackageFeedUnavailableException()
        : base("The configured package feed is unavailable.")
    {
    }

    public ReleasePackageFeedUnavailableException(Exception innerException)
        : base("The configured package feed is unavailable.", innerException)
    {
    }
}
