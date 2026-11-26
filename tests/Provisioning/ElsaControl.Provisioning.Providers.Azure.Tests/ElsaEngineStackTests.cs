using ElsaControl.Provisioning.Providers.Azure;
using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Sql;
using Pulumi.Testing;
using ResourceGroup = Pulumi.AzureNative.Resources.ResourceGroup;
using PulumiDeployment = Pulumi.Deployment;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[Collection(nameof(PulumiMockTests))]
public sealed class ElsaEngineStackTests
{
    [Fact]
    public async Task Engine_stack_reads_platform_output_through_a_fully_qualified_stack_reference()
    {
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, EngineTestConfig.Values());
        var resources = await PulumiDeployment.TestAsync<ElsaEngineStack>(
            new PlatformStackReferenceMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "engine-" + EngineTestConfig.InstanceId.ToString("N")
            });

        var stack = resources.OfType<ElsaEngineStack>().Single();
        Assert.Equal(
            "engine-" + EngineTestConfig.InstanceId.ToString("N"),
            await stack.EngineName.GetValueAsync());
        Assert.Equal("shared-container-environment", await stack.PlatformEnvironmentKind.GetValueAsync());
        Assert.Equal("ingress", await stack.EndpointKind.GetValueAsync());
        Assert.Equal("digest-pinned", await stack.ImageIdentityKind.GetValueAsync());
        Assert.Equal("disabled", await stack.HandoffKind.GetValueAsync());
        Assert.Contains(resources, resource => resource is StackReference);
        Assert.Contains(resources, resource => resource is ResourceGroup);
        Assert.Contains(resources, resource => resource is UserAssignedIdentity);
        Assert.Contains(resources, resource => resource is Vault);
        Assert.Contains(resources, resource => resource is Server);
        Assert.Contains(resources, resource => resource is Database);
        Assert.Contains(resources, resource => resource is ContainerApp);
        Assert.Contains(resources, resource => resource is RoleAssignment);
        var stackReference = resources.OfType<StackReference>().Single();
        Assert.Equal("organization/elsa-platform/staging", stackReference.GetResourceName());
    }

    [Fact]
    public async Task Engine_stack_config_is_per_stack()
    {
        var first = await Run("engine-one");
        var second = await Run("engine-two");

        Assert.Equal("engine-one", await first.OfType<ElsaEngineStack>().Single().EngineName.GetValueAsync());
        Assert.Equal("engine-two", await second.OfType<ElsaEngineStack>().Single().EngineName.GetValueAsync());
    }

    [Fact]
    public async Task Second_engine_does_not_create_a_platform_environment()
    {
        var resources = await Run("engine-one");
        Assert.DoesNotContain(resources, resource => resource is ManagedEnvironment);
        Assert.DoesNotContain(
            resources,
            resource => resource.GetResourceType().Contains("Registry", StringComparison.Ordinal)
                        && resource is not StackReference);
    }

    private static async Task<System.Collections.Immutable.ImmutableArray<Pulumi.Resource>> Run(string engineName)
    {
        using var config = PulumiTestConfig.Set(
            PulumiProjectNames.Engine,
            EngineTestConfig.Values(engineName));
        return await PulumiDeployment.TestAsync<ElsaEngineStack>(
            new PlatformStackReferenceMocks(),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = engineName
            });
    }
}
