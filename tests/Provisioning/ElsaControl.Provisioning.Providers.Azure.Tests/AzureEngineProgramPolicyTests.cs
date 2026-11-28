using ElsaControl.Provisioning.Providers.Azure;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzureEngineProgramPolicyTests
{
    [Fact]
    public void Digest_pinned_image_and_admitted_region_pass()
    {
        var image = AzureEngineProgramPolicy.ImmutableImage(EngineTestConfig.Values());
        Assert.Equal(
            "valenceruntimeimages.azurecr.io/runtime-combined@sha256:" + new string('a', 64),
            image);
    }

    [Theory]
    [InlineData("eastus")]
    [InlineData("")]
    public void Unadmitted_location_fails_closed(string location)
    {
        var values = EngineTestConfig.Values();
        values["location"] = location;
        Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
    }

    [Fact]
    public void Tag_image_identity_fails_closed()
    {
        var values = EngineTestConfig.Values();
        values["imageRepository"] = "valenceruntimeimages.azurecr.io/runtime-combined:latest";
        Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
    }

    [Fact]
    public void Image_construction_trims_repository_and_digest()
    {
        var values = EngineTestConfig.Values();
        values["imageRepository"] = " valenceruntimeimages.azurecr.io/runtime-combined ";
        values["imageDigest"] = " " + new string('a', 64) + " ";
        var image = AzureEngineProgramPolicy.ImmutableImage(values);
        Assert.Equal(
            "valenceruntimeimages.azurecr.io/runtime-combined@sha256:" + new string('a', 64),
            image);
    }

    [Fact]
    public void Digit_leading_fingerprint_gets_a_letter_prefixed_revision_suffix()
    {
        var suffix = ElsaControl.Provisioning.Core.ProvisioningRevisionSuffix.FromFingerprint(new string('1', 64));
        Assert.Equal("r" + new string('1', 23), suffix);
        ElsaControl.Provisioning.Core.ProvisioningRevisionSuffix.Validate(suffix);
        ElsaControl.Provisioning.Core.ProvisioningRevisionSuffix.Validate("release-a");
        Assert.Throws<InvalidOperationException>(() =>
            ElsaControl.Provisioning.Core.ProvisioningRevisionSuffix.Validate("1abc"));
    }

    [Fact]
    public void Registry_login_server_and_id_must_both_be_set()
    {
        var values = EngineTestConfig.Values();
        values["registryLoginServer"] = "valenceruntimeimages.azurecr.io";
        var error = Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
        Assert.Equal("Registry login server and resource id must both be set.", error.Message);
        values["registryId"] =
            "/subscriptions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/resourceGroups/rg-valence-runtime/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages";
        AzureEngineProgramPolicy.Validate(values);
    }

    [Fact]
    public void Missing_sql_bootstrap_identity_fails_closed()
    {
        var values = EngineTestConfig.Values();
        values.Remove("sqlBootstrapObjectId");
        var error = Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
        Assert.Equal("Required engine stack config is missing.", error.Message);
    }

    [Fact]
    public void Shared_engine_seed_values_are_rejected()
    {
        var values = EngineTestConfig.Values();
        values["sqlConnectionSecretValue"] = "seeded-sql";
        var error = Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
        Assert.Equal("Shared engine seed values are not admitted.", error.Message);
    }

    [Fact]
    public void Handoff_without_control_configuration_fails_closed()
    {
        var values = EngineTestConfig.Values();
        values["managedHandoff"] = "true";
        values["handoffConfigured"] = "false";
        var error = Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
        Assert.Equal("Managed handoff is declared but not configured.", error.Message);
    }
}
