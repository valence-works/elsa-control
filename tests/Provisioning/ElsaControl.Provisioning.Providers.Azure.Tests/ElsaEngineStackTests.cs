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
        Assert.Equal("dedicated-container-environment", await stack.PlatformEnvironmentKind.GetValueAsync());
        Assert.Equal("ingress", await stack.EndpointKind.GetValueAsync());
        Assert.Equal("digest-pinned", await stack.ImageIdentityKind.GetValueAsync());
        Assert.Equal("disabled", await stack.HandoffKind.GetValueAsync());
        Assert.Contains(resources, resource => resource is StackReference);
        Assert.Contains(resources, resource => resource is ResourceGroup);
        Assert.Contains(resources, resource => resource is UserAssignedIdentity);
        Assert.Contains(resources, resource => resource is Vault);
        Assert.Contains(resources, resource => resource is Secret);
        Assert.Contains(resources, resource => resource is Server);
        Assert.Contains(resources, resource => resource is Database);
        Assert.Contains(resources, resource => resource is ManagedEnvironment);
        Assert.Contains(resources, resource => resource is Pulumi.AzureNative.OperationalInsights.Workspace);
        Assert.Contains(resources, resource => resource is ContainerApp);
        Assert.Contains(resources, resource => resource is RoleAssignment);
        Assert.Equal(3, resources.OfType<Secret>().Count());
        Assert.Contains(
            resources,
            resource => resource.GetResourceType() == EngineSqlContainedUser.ResourceTypeName);
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
        var first = await Run("engine-one");
        var second = await Run("engine-two");
        Assert.Contains(first, resource => resource is ManagedEnvironment);
        Assert.Contains(second, resource => resource is ManagedEnvironment);
        Assert.Contains(first, resource => resource is Pulumi.AzureNative.OperationalInsights.Workspace);
        Assert.Contains(second, resource => resource is Pulumi.AzureNative.OperationalInsights.Workspace);
        Assert.DoesNotContain(
            first,
            resource => resource.GetResourceType().Contains("Registry", StringComparison.Ordinal)
                        && resource is not StackReference);
    }

    [Fact]
    public async Task Fresh_create_seeds_vault_secrets_before_the_container_app()
    {
        var recorded = new List<string>();
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, EngineTestConfig.Values());
        var resources = await PulumiDeployment.TestAsync<ElsaEngineStack>(
            new RecordingMocks(recorded),
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "engine-" + EngineTestConfig.InstanceId.ToString("N")
            });
        var secretIndex = recorded.FindIndex(type => type.Contains("Secret", StringComparison.Ordinal));
        var appIndex = recorded.FindIndex(type => type.Contains("ContainerApp", StringComparison.Ordinal));
        Assert.True(secretIndex >= 0);
        Assert.True(appIndex > secretIndex);
        Assert.Contains(resources, resource => resource is Secret);
        Assert.Contains(resources, resource => resource is ContainerApp);
    }

    [Fact]
    public async Task Engine_database_is_pinned_to_standard_s0()
    {
        var recorded = new List<string>();
        var mocks = new RecordingMocks(recorded);
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, EngineTestConfig.Values());
        await PulumiDeployment.TestAsync<ElsaEngineStack>(
            mocks,
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "engine-" + EngineTestConfig.InstanceId.ToString("N")
            });
        var database = mocks.Args.Single(args =>
            (args.Type ?? "").Contains("sql", StringComparison.OrdinalIgnoreCase)
            && (args.Type ?? "").Contains("Database", StringComparison.Ordinal));
        Assert.Equal("S0", SkuName(database.Inputs["sku"]));
    }

    [Fact]
    public async Task Contained_user_grant_is_skipped_during_preview()
    {
        var grant = new RecordingGrant();
        using var seam = EngineSqlContainedUserSeam.Use(grant);
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, EngineTestConfig.Values());
        var resources = await PulumiDeployment.TestAsync<ElsaEngineStack>(
            new PlatformStackReferenceMocks(),
            new TestOptions
            {
                IsPreview = true,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "engine-" + EngineTestConfig.InstanceId.ToString("N")
            });
        Assert.Empty(grant.Calls);
        Assert.Contains(
            resources,
            resource => resource.GetResourceType() == EngineSqlContainedUser.ResourceTypeName);
    }

    [Fact]
    public async Task Contained_user_grant_waits_for_the_database_output()
    {
        var grant = new RecordingGrant();
        using var seam = EngineSqlContainedUserSeam.Use(grant);
        var recorded = new List<string>();
        var mocks = new RecordingMocks(recorded);
        using var config = PulumiTestConfig.Set(PulumiProjectNames.Engine, EngineTestConfig.Values());
        var resources = await PulumiDeployment.TestAsync<ElsaEngineStack>(
            mocks,
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = "engine-" + EngineTestConfig.InstanceId.ToString("N")
            });
        Assert.NotEmpty(grant.Calls);
        Assert.Contains("Elsa", grant.DatabaseIds);
        Assert.Contains(
            resources,
            resource => resource.GetResourceType() == EngineSqlContainedUser.ResourceTypeName);
    }

    [Fact]
    public async Task Contained_user_grant_is_idempotent()
    {
        var grant = new RecordingGrant();
        using var seam = EngineSqlContainedUserSeam.Use(grant);
        EngineSqlContainedUserSeam.Active.Apply(
            "sql.example",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "engine-runtime",
            "engine-db_id");
        EngineSqlContainedUserSeam.Active.Apply(
            "sql.example",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "engine-runtime",
            "engine-db_id");
        Assert.Equal(2, grant.Calls.Count);
    }

    [Fact]
    public async Task Two_engines_get_distinct_values_for_all_three_seeds()
    {
        var first = await SeedsFor("engine-one");
        var second = await SeedsFor("engine-two");
        Assert.False(string.IsNullOrWhiteSpace(first.Inner.PasswordResult));
        Assert.False(string.IsNullOrWhiteSpace(first.Inner.BytesResult));
        Assert.False(string.IsNullOrWhiteSpace(first.Inner.SqlFqdn));
        Assert.NotEqual(first.Inner.PasswordResult, second.Inner.PasswordResult);
        Assert.NotEqual(first.Inner.BytesResult, second.Inner.BytesResult);
        Assert.NotEqual(first.Inner.SqlFqdn, second.Inner.SqlFqdn);
        var firstSecrets = MockSecretValues.From(first);
        var secondSecrets = MockSecretValues.From(second);
        if (firstSecrets.Count == 3 && secondSecrets.Count == 3)
            Assert.Empty(firstSecrets.Intersect(secondSecrets, StringComparer.Ordinal));
    }

    private static async Task<RecordingMocks> SeedsFor(string engineName)
    {
        var recorded = new List<string>();
        var mocks = new RecordingMocks(recorded, engineName);
        using var config = PulumiTestConfig.Set(
            PulumiProjectNames.Engine,
            EngineTestConfig.Values(engineName));
        await PulumiDeployment.TestAsync<ElsaEngineStack>(
            mocks,
            new TestOptions
            {
                IsPreview = false,
                ProjectName = PulumiProjectNames.Engine,
                StackName = engineName
            });
        return mocks;
    }

    private static string? SkuName(object? sku)
    {
        if (sku is System.Collections.IDictionary map)
        {
            foreach (var key in new[] { "name", "Name" })
            {
                if (map.Contains(key) && map[key] is not null)
                    return map[key]!.ToString();
            }
        }

        return sku?.ToString();
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
