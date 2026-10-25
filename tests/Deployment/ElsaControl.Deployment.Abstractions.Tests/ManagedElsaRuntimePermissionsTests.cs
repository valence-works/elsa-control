using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Abstractions.Tests;

public sealed class ManagedElsaRuntimePermissionsTests
{
    [Theory]
    [InlineData("3.7", null, false)]
    [InlineData("3.8", null, false)]
    [InlineData("3.8", "3.8.1", false)]
    [InlineData("3.8", "3.8.0-preview.5413", false)]
    [InlineData(null, "3.8.1", false)]
    [InlineData(null, "3.8.0-preview.1", false)]
    [InlineData("2.14", "2.14.0", false)]
    [InlineData("3.9", null, true)]
    [InlineData("3.9", "3.9.0", true)]
    [InlineData("3.9", "3.9.0-preview.1", true)]
    [InlineData(null, "3.9.0", true)]
    [InlineData(null, "3.9.0-preview.1", true)]
    [InlineData("3.10", "3.10.4", true)]
    [InlineData("4.1", "4.1.0", true)]
    [InlineData("5.0", "5.0.0", true)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("not-a-line", "also-not", false)]
    public void Release_line_selects_v2_from_3_9_onward(string? releaseLine, string? releaseVersion, bool usesV2)
    {
        Assert.Equal(usesV2, ManagedElsaRuntimePermissions.UsesStudioGrantsV2(releaseLine, releaseVersion));
        Assert.Equal(
            usesV2
                ? ManagedElsaRuntimePermissions.StudioGrantsV2Capability
                : ManagedElsaRuntimePermissions.StudioGrantsV1Capability,
            ManagedElsaRuntimePermissions.RequiredCapability(releaseLine, releaseVersion));
        Assert.Equal(
            usesV2
                ? ManagedElsaRuntimePermissions.StudioGrantsV2
                : ManagedElsaRuntimePermissions.StudioGrantsV1,
            ManagedElsaRuntimePermissions.StudioGrantsFor(releaseLine, releaseVersion));
        Assert.Equal(
            usesV2
                ? ManagedElsaRuntimePermissions.DiagnosticsOnlyV2
                : ManagedElsaRuntimePermissions.DiagnosticsOnly,
            ManagedElsaRuntimePermissions.DiagnosticsOnlyFor(releaseLine, releaseVersion));
    }

    [Fact]
    public void Release_line_wins_over_a_conflicting_catalog_version()
    {
        Assert.False(ManagedElsaRuntimePermissions.UsesStudioGrantsV2("3.8", "3.9.0"));
        Assert.True(ManagedElsaRuntimePermissions.UsesStudioGrantsV2("3.9", "3.8.1"));
    }

    [Fact]
    public void Selected_grants_follow_the_image_capability_and_the_release_line()
    {
        Assert.Equal(
            ManagedElsaRuntimePermissions.StudioGrantsV1,
            ManagedElsaRuntimePermissions.SelectedFor(true, "3.8", "3.8.1"));
        Assert.Equal(
            ManagedElsaRuntimePermissions.DiagnosticsOnly,
            ManagedElsaRuntimePermissions.SelectedFor(false, "3.8", "3.8.1"));
        Assert.Equal(
            ManagedElsaRuntimePermissions.StudioGrantsV2,
            ManagedElsaRuntimePermissions.SelectedFor(true, "3.9", "3.9.0"));
        Assert.Equal(
            ManagedElsaRuntimePermissions.DiagnosticsOnlyV2,
            ManagedElsaRuntimePermissions.SelectedFor(false, "3.10", "3.10.4"));
    }

    [Fact]
    public void Supported_set_is_the_union_of_both_contracts()
    {
        Assert.All(ManagedElsaRuntimePermissions.StudioGrantsV1, permission =>
            Assert.True(ManagedElsaRuntimePermissions.IsSupported(permission)));
        Assert.All(ManagedElsaRuntimePermissions.StudioGrantsV2, permission =>
            Assert.True(ManagedElsaRuntimePermissions.IsSupported(permission)));
        Assert.False(ManagedElsaRuntimePermissions.IsSupported("read:*"));
        Assert.False(ManagedElsaRuntimePermissions.IsSupported("workflows/*:view"));
        Assert.False(ManagedElsaRuntimePermissions.IsSupported("system/features:view"));
    }
}
