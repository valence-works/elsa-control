using ElsaControl.Provisioning.Providers.Azure;
using ElsaControl.Provisioning.Providers.Azure.Pulumi;
using Pulumi;
using Pulumi.Testing;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[Collection(nameof(PulumiMockTests))]
public sealed class ElsaEngineStackTests
{
    [Fact]
    public async Task Engine_stack_reads_platform_output_through_stack_reference()
    {
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, new Dictionary<string, string>
        {
            ["engineName"] = "acme-prod",
            ["platformStack"] = "elsa-platform/staging"
        });
        var resources = await Deployment.TestAsync<ElsaEngineStack>(
            new PlatformStackReferenceMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "acme-prod"
            });

        var stack = resources.OfType<ElsaEngineStack>().Single();
        Assert.Equal("acme-prod", await stack.EngineName.GetValueAsync());
        Assert.Equal("shared-container-environment", await stack.PlatformEnvironmentKind.GetValueAsync());
        Assert.Equal("https-ingress", await stack.EndpointKind.GetValueAsync());
        Assert.Contains(resources, resource => resource is StackReference);
    }

    [Fact]
    public async Task Engine_stack_config_is_per_stack()
    {
        var first = await Run("acme-one");
        var second = await Run("acme-two");

        Assert.Equal("acme-one", await first.OfType<ElsaEngineStack>().Single().EngineName.GetValueAsync());
        Assert.Equal("acme-two", await second.OfType<ElsaEngineStack>().Single().EngineName.GetValueAsync());
    }

    private static async Task<System.Collections.Immutable.ImmutableArray<Resource>> Run(string engineName)
    {
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, new Dictionary<string, string>
        {
            ["engineName"] = engineName,
            ["platformStack"] = "elsa-platform/staging"
        });
        return await Deployment.TestAsync<ElsaEngineStack>(
            new PlatformStackReferenceMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = engineName
            });
    }
}
