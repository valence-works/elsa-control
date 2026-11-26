using System.Reflection;
using ElsaControl.Provisioning.Providers.Azure;
using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.Monitor;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.Testing;
using ResourceGroup = Pulumi.AzureNative.Resources.ResourceGroup;
using PulumiDeployment = Pulumi.Deployment;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[Collection(nameof(PulumiMockTests))]
public sealed class ElsaPlatformStackTests
{
    [Fact]
    public async Task Platform_stack_exports_capability_kinds_and_shared_environment()
    {
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Platform, EngineTestConfig.Platform());
        var resources = await PulumiDeployment.TestAsync<ElsaPlatformStack>(
            new EmptyMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Platform,
                StackName = "staging"
            });

        var stack = resources.OfType<ElsaPlatformStack>().Single();
        Assert.Equal("staging", await stack.EnvironmentName.GetValueAsync());
        Assert.Equal("shared-environment", await stack.ContainerEnvironmentKind.GetValueAsync());
        Assert.Equal("shared-registry", await stack.RegistryKind.GetValueAsync());
        Assert.Equal("runtimeimages", await stack.RegistryName.GetValueAsync());
        Assert.Equal("example.azurecontainerapps.io", await stack.ContainerEnvironmentDefaultDomain.GetValueAsync());
        Assert.Contains(resources, resource => resource is ResourceGroup);
        Assert.Contains(resources, resource => resource is Workspace);
        Assert.Contains(resources, resource => resource is ManagedEnvironment);
        Assert.Contains(resources, resource => resource is DiagnosticSetting);
        Assert.DoesNotContain(
            resources,
            resource => resource.GetResourceType().Contains("Registry", StringComparison.OrdinalIgnoreCase)
                        && resource is not DiagnosticSetting);
    }

    [Fact]
    public void Platform_outputs_are_registered_under_camelCase_keys()
    {
        foreach (var property in typeof(ElsaPlatformStack).GetProperties())
        {
            var output = property.GetCustomAttribute<OutputAttribute>();
            if (output is null)
                continue;
            var expected = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            Assert.Equal(expected, output.Name);
        }

        Assert.Equal(
            PulumiStackOutputs.ContainerEnvironmentId,
            typeof(ElsaPlatformStack).GetProperty(nameof(ElsaPlatformStack.ContainerEnvironmentId))!
                .GetCustomAttribute<OutputAttribute>()!.Name);
    }
}
