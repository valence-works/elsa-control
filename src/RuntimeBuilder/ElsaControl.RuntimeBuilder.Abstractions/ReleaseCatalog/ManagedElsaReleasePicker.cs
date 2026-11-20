using NuGet.Versioning;

namespace ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

/// <summary>
/// Shared Supported-release selector for onboarding-options and Partner API
/// <c>GET /releases</c>. Sort is NuGet semver descending; the cap is a picker
/// rule, not a security rule.
/// </summary>
public static class ManagedElsaReleasePicker
{
    public const int DefaultMaxChoices = 3;
    public const int MaximumMaxChoices = 100;

    public static int NormalizeMaxChoices(int maxChoices) =>
        maxChoices < 1 ? DefaultMaxChoices : Math.Min(maxChoices, MaximumMaxChoices);

    /// <summary>
    /// Supported-list filter: drop prerelease and unparseable versions even when a
    /// catalog row is still labelled <c>supported</c>. Preview selection must not
    /// call this.
    /// </summary>
    public static IEnumerable<GovernedReleaseCatalogEntry> WhereStableReleaseVersion(
        IEnumerable<GovernedReleaseCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(entry =>
            GovernedReleaseCatalogLifecycle.IsStableReleaseVersion(entry.Distribution.ReleaseVersion));
    }

    /// <summary>
    /// Drops ambiguous identities (more than one immutable admission) and returns
    /// the remaining rows newest-first by NuGet semver.
    /// </summary>
    public static IReadOnlyList<GovernedReleaseCatalogEntry> SelectUniqueNewestFirst(
        IEnumerable<GovernedReleaseCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .GroupBy(ReleaseIdentity, ReleaseIdentityComparer.Instance)
            .Where(group => group.Take(2).Count() == 1)
            .Select(group => group.Single())
            .OrderByDescending(entry => entry.Distribution.ReleaseVersion, NuGetReleaseVersionComparer.Instance)
            .ThenBy(entry => entry.Distribution.ReleaseLine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Distribution.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Topology.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<GovernedReleaseCatalogEntry> Cap(
        IReadOnlyList<GovernedReleaseCatalogEntry> newestFirst,
        int maxChoices)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var cap = NormalizeMaxChoices(maxChoices);
        return newestFirst.Count <= cap ? newestFirst : newestFirst.Take(cap).ToArray();
    }

    public static (string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId)
        ReleaseIdentity(GovernedReleaseCatalogEntry entry) =>
        (
            entry.Distribution.Id.Trim().ToUpperInvariant(),
            entry.Distribution.ReleaseLine.Trim().ToUpperInvariant(),
            entry.Distribution.ReleaseVersion.Trim().ToUpperInvariant(),
            entry.Distribution.Channel.Trim().ToUpperInvariant(),
            entry.Topology.Id.Trim().ToUpperInvariant());

    private sealed class ReleaseIdentityComparer :
        IEqualityComparer<(string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId)>
    {
        public static ReleaseIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId) left,
            (string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId) right) =>
            left.DistributionId == right.DistributionId &&
            left.ReleaseLine == right.ReleaseLine &&
            left.Version == right.Version &&
            left.Channel == right.Channel &&
            left.TopologyId == right.TopologyId;

        public int GetHashCode(
            (string DistributionId, string ReleaseLine, string Version, string Channel, string TopologyId) value) =>
            HashCode.Combine(value.DistributionId, value.ReleaseLine, value.Version, value.Channel, value.TopologyId);
    }
}

/// <summary>
/// NuGet semver comparison. Parseable versions sort by <see cref="NuGetVersion"/>;
/// unparseable values sort below every parseable version so they cannot become
/// the recommended default when a stable semver row exists.
/// </summary>
public sealed class NuGetReleaseVersionComparer : IComparer<string>
{
    public static NuGetReleaseVersionComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var leftParsed = NuGetVersion.TryParse(left, out var leftVersion);
        var rightParsed = NuGetVersion.TryParse(right, out var rightVersion);
        if (leftParsed && rightParsed)
            return leftVersion!.CompareTo(rightVersion);
        if (leftParsed)
            return 1;
        if (rightParsed)
            return -1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
