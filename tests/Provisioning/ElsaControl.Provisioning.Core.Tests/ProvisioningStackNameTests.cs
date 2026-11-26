using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningStackNameTests
{
    [Theory]
    [InlineData("acme-prod", "acme-prod")]
    [InlineData("Acme Prod", "Acme-Prod")]
    [InlineData("org/slug", "org-slug")]
    [InlineData("--alpha--", "alpha")]
    public void Canonicalizes_to_pulumi_charset(string input, string expected) =>
        Assert.Equal(expected, ProvisioningStackName.Canonicalize(input));

    [Fact]
    public void Engine_stack_name_is_the_immutable_instance_id()
    {
        var instanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackName.ForEngine(instanceId));
        Assert.StartsWith(ProvisioningStackName.EnginePrefix, ProvisioningStackName.ForEngine(instanceId));
        Assert.DoesNotContain("acme", ProvisioningStackName.ForEngine(instanceId), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Org_slash_slug_and_org_hyphen_slug_do_not_share_an_engine_stack()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal("org-slug", ProvisioningStackName.Canonicalize("org/slug"));
        Assert.Equal("org-slug", ProvisioningStackName.Canonicalize("org-slug"));
        Assert.NotEqual(ProvisioningStackName.ForEngine(first), ProvisioningStackName.ForEngine(second));
        Assert.NotEqual(ProvisioningStackName.ForEngine(first), ProvisioningStackName.Canonicalize("org/slug"));
    }

    [Fact]
    public void Empty_instance_id_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ProvisioningStackName.ForEngine(Guid.Empty));

    [Fact]
    public void Platform_stack_reference_is_fully_qualified()
    {
        Assert.Equal(
            "organization/elsa-platform/staging",
            ProvisioningStackReference.Platform("staging"));
        Assert.Equal(
            "control/elsa-platform/production",
            ProvisioningStackReference.Platform("production", "control"));
        Assert.Equal(
            "organization/elsa-engine/engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            ProvisioningStackReference.Engine("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"));
    }
}
