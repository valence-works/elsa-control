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
    public void Handoff_without_control_configuration_fails_closed()
    {
        var values = EngineTestConfig.Values();
        values["managedHandoff"] = "true";
        values["handoffConfigured"] = "false";
        var error = Assert.Throws<InvalidOperationException>(() => AzureEngineProgramPolicy.Validate(values));
        Assert.Equal("Managed handoff is declared but not configured.", error.Message);
    }
}
