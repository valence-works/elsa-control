using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

namespace ElsaControl.RuntimeBuilder.Core.Tests;

public sealed class ManagedElsaReleasePickerTests
{
    [Fact]
    public void NuGet_semver_orders_stable_ahead_of_older_prerelease_and_unparseable()
    {
        var ordered = new[] { "3.8.0-preview.5413", "3.8.x", "3.10.2" }
            .OrderByDescending(version => version, NuGetReleaseVersionComparer.Instance)
            .ToArray();

        Assert.Equal(["3.10.2", "3.8.0-preview.5413", "3.8.x"], ordered);
    }

    [Fact]
    public void Newest_supported_by_nuget_semver_is_first_ahead_of_prerelease_and_string_sort()
    {
        var selected = ManagedElsaReleasePicker.SelectUniqueNewestFirst(
            ManagedElsaReleasePicker.WhereStableReleaseVersion(
            [
                Entry("3.8.0-preview.5413", "3.8"),
                Entry("3.8.x", "3.8.x"),
                Entry("3.10.2", "3.10")
            ]));

        Assert.Equal("3.10.2", Assert.Single(selected).Distribution.ReleaseVersion);
    }

    [Fact]
    public void Cap_keeps_the_newest_max_choices_in_semver_order()
    {
        var newestFirst = ManagedElsaReleasePicker.SelectUniqueNewestFirst(
        [
            Entry("3.8.4", "3.8"),
            Entry("3.9.0", "3.9"),
            Entry("3.10.1", "3.10"),
            Entry("3.10.2", "3.10"),
            Entry("3.11.0", "3.11")
        ]);
        var capped = ManagedElsaReleasePicker.Cap(newestFirst, 3);

        Assert.Equal(5, newestFirst.Count);
        Assert.Equal(["3.11.0", "3.10.2", "3.10.1"], capped.Select(x => x.Distribution.ReleaseVersion));
    }

    [Fact]
    public void Partner_selector_is_the_same_unique_newest_first_list_without_preview()
    {
        var supported = ManagedElsaReleasePicker.SelectUniqueNewestFirst(
        [
            Entry("3.10.2", "3.10"),
            Entry("3.8.4", "3.8"),
            Entry("3.9.0", "3.9")
        ]);
        var preview = ManagedElsaReleasePicker.SelectUniqueNewestFirst(
        [
            Entry("3.8.0-preview.5413", "3.8", catalogLifecycle: "preview")
        ]);
        var partner = ManagedElsaReleasePicker.Cap(supported, 3);

        Assert.Equal(["3.10.2", "3.9.0", "3.8.4"], partner.Select(x => x.Distribution.ReleaseVersion));
        Assert.DoesNotContain(partner, entry => entry.Distribution.ReleaseVersion.Contains("preview", StringComparison.OrdinalIgnoreCase));
        Assert.Single(preview);
    }

    [Fact]
    public void Ambiguous_identities_are_dropped()
    {
        var selected = ManagedElsaReleasePicker.SelectUniqueNewestFirst(
        [
            Entry("3.10.2", "3.10", digestMarker: 'a'),
            Entry("3.10.2", "3.10", digestMarker: 'b'),
            Entry("3.9.0", "3.9")
        ]);

        Assert.Equal("3.9.0", Assert.Single(selected).Distribution.ReleaseVersion);
    }

    [Fact]
    public void Stable_release_versions_are_required_for_supported()
    {
        Assert.True(GovernedReleaseCatalogLifecycle.CanBeSupported("supported", "3.10.2"));
        Assert.False(GovernedReleaseCatalogLifecycle.CanBeSupported("supported", "3.8.0-preview.5413"));
        Assert.False(GovernedReleaseCatalogLifecycle.CanBeSupported("supported", "3.8.x"));
        Assert.True(GovernedReleaseCatalogLifecycle.CanBeSupported("preview", "3.8.0-preview.5413"));
    }

    private static GovernedReleaseCatalogEntry Entry(
        string version,
        string releaseLine,
        string catalogLifecycle = "supported",
        char digestMarker = 'a') =>
        new(
            "1.0",
            $"oci://registry.example.test/releases/manifest@sha256:{new string(digestMarker, 64)}",
            $"sha256:{new string(digestMarker, 64)}",
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "https://evidence.example.test/signatures/manifest",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            "paid",
            new GovernedReleaseDistribution(
                "valence-runtime", "3", releaseLine, version, "stable", "supported", null,
                "https://github.com/valence-works/elsa", "0123456789abcdef", "run-1"),
            new GovernedReleaseTopology("combined", "1.0", ["server"], [], [], [], []),
            catalogLifecycle,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
}
