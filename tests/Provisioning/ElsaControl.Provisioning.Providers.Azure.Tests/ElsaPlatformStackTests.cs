using ElsaControl.Provisioning.Providers.Azure;
using ElsaControl.Provisioning.Providers.Azure.Pulumi;
using Pulumi;
using Pulumi.Testing;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[Collection(nameof(PulumiMockTests))]
public sealed class ElsaPlatformStackTests
{
    [Fact]
    public async Task Platform_stack_exports_capability_kinds()
    {
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Platform, new Dictionary<string, string>
        {
            ["environmentName"] = "staging"
        });
        var resources = await Deployment.TestAsync<ElsaPlatformStack>(
            new EmptyMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Platform,
                StackName = "staging"
            });

        var stack = resources.OfType<ElsaPlatformStack>().Single();
        Assert.Equal("staging", await stack.EnvironmentName.GetValueAsync());
        Assert.Equal("shared-container-environment", await stack.ContainerEnvironmentKind.GetValueAsync());
        Assert.Equal("shared-registry", await stack.RegistryKind.GetValueAsync());
    }
}
