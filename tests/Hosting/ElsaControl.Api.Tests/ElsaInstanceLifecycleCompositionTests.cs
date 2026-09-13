using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Api.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Collections.Concurrent;
using System.Data.Common;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Api.Tests;

public sealed class ElsaInstanceLifecycleCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"elsa-api-lifecycle-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, "restricted")]
    [InlineData("restricted", "restricted")]
    [InlineData("unrestricted", "unrestricted")]
    public void Instance_plan_egress_requires_an_explicit_server_policy_to_relax_the_default(string? configured, string expected)
    {
        var services = new ServiceCollection();
        ElsaInstancePlanResolutionComposition.AddResolver(services, Configuration(new Dictionary<string, string?>
        {
            ["RuntimeBuilder:InstancePlans:DefaultEgress"] = configured
        }));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<ElsaInstancePlanResolutionOptions>().DefaultEgress);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IElsaInstancePlanResolver));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown-policy")]
    [InlineData("unrestricted\nsecret-marker")]
    public void Unsupported_instance_plan_egress_fails_closed_without_echoing_configuration(string configured)
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ElsaInstancePlanResolutionComposition.AddResolver(services, Configuration(new Dictionary<string, string?>
            {
                ["RuntimeBuilder:InstancePlans:DefaultEgress"] = configured
            })));

        Assert.Equal("The instance plan egress policy is unsupported.", exception.Message);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IElsaInstancePlanResolver));
    }

    [Fact]
    public void Azure_provider_ports_are_not_composed_when_Azure_lifecycle_is_disabled()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Deployment:ElsaInstanceLifecycle:Enabled"] = "true",
            ["Deployment:AzureProvider:InstanceLifecycle:Enabled"] = "false"
        });

        Assert.False(AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, null));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderSubmissionPort));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderReconciliationPort));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderCleanupPort));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderRecoveryPort));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IEngineProvisioningModule));
    }

    [Fact]
    public void Azure_provider_ports_derive_the_exact_validated_runner_authority_when_enabled()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Deployment:AzureProvider:InstanceLifecycle:Enabled"] = "true"
        });
        var authority = Authority();

        Assert.True(AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, authority));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderSubmissionPort));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderReconciliationPort));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderCleanupPort));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IElsaInstanceProviderRecoveryPort));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IEngineProvisioningModule));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<AzureElsaInstanceProviderOptions>();
        Assert.Equal(authority.TemplateFingerprint, options.TemplateFingerprint);
        Assert.Equal(authority.ProviderScopeFingerprint, options.ProviderScopeFingerprint);
        Assert.Equal(authority.Scope.SubscriptionId, options.SubscriptionId);
        Assert.Equal(authority.Scope.ResourceGroupName, options.ResourceGroupNamePrefix);
        var module = Assert.Single(provider.GetServices<IEngineProvisioningModule>());
        Assert.Equal("azure", module.Id);
        Assert.Equal("Azure", module.DisplayName);
    }

    [Fact]
    public async Task Azure_delete_recovery_port_uses_a_child_scope_for_provider_dependencies()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Deployment:AzureProvider:InstanceLifecycle:Enabled"] = "true"
        });
        var authority = Authority();
        Assert.True(AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, authority));

        var contextIds = new RecordingDbContextInterceptor();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        services.AddSingleton(connection);
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlite(connection).AddInterceptors(contextIds));
        services.AddScoped<AzureProviderOperationStore>();
        services.AddScoped<IAzureProviderOperationStore>(provider =>
            provider.GetRequiredService<AzureProviderOperationStore>());
        services.AddScoped<IAzureProviderResourceAssignmentStore>(provider =>
            provider.GetRequiredService<AzureProviderOperationStore>());
        services.AddScoped<IAzureProviderRecoveryObservationStore>(provider =>
            provider.GetRequiredService<AzureProviderOperationStore>());
        services.AddScoped<IAzureProviderRunner, UnconfiguredAzureProviderRunner>();
        services.AddScoped<AzureProviderExecutor>();
        services.AddScoped<IAzureProviderOperationService, AzureProviderOperationService>();

        using var provider = services.BuildServiceProvider();
        await using (var initializationScope = provider.CreateAsyncScope())
        {
            var db = initializationScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        contextIds.Clear();
        await using var lifecycleScope = provider.CreateAsyncScope();
        var lifecycleDb = lifecycleScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var lifecycleProvider = lifecycleScope.ServiceProvider.GetRequiredService<AzureElsaInstanceProvider>();
        var cleanupPort = lifecycleScope.ServiceProvider.GetRequiredService<IElsaInstanceProviderCleanupPort>();
        var recoveryPort = lifecycleScope.ServiceProvider.GetRequiredService<IElsaInstanceProviderDeleteRecoveryPort>();

        Assert.Same(lifecycleProvider, cleanupPort);
        Assert.IsType<ScopedAzureInstanceProviderDeleteRecoveryPort>(recoveryPort);

        var request = new ElsaInstanceDeleteRecoveryRequest(
            new ElsaInstanceCleanupRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                CurrentDeployment: null,
                PlacementAssignment: null,
                Tenant: null),
            Guid.NewGuid(),
            InstanceVersion: 1,
            WorkerId: "scope-test-worker",
            LeaseToken: new string('a', 64),
            LeaseVersion: 1);
        var observation = await recoveryPort.RecoverDeleteAsync(request);

        Assert.Equal(ElsaInstanceCleanupObservationKind.Ambiguous, observation.Kind);
        var childContextIds = contextIds.ContextIds.ToArray();
        Assert.NotEmpty(childContextIds);
        Assert.All(childContextIds, contextId => Assert.NotEqual(lifecycleDb.ContextId.InstanceId, contextId));
    }

    [Fact]
    public void Enabled_Azure_lifecycle_fails_closed_without_the_concrete_runner_authority()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Deployment:AzureProvider:InstanceLifecycle:Enabled"] = "true"
        });

        Assert.Throws<InvalidOperationException>(() =>
            AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, null));
    }

    private AzureProviderRunnerAuthority Authority()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "main.bicep"), "targetScope = 'resourceGroup'");
        File.WriteAllText(Path.Combine(_root, "acr-pull-role.bicep"), "targetScope = 'resourceGroup'");
        File.WriteAllText(Path.Combine(_root, "sql-bootstrap.sql"), "SELECT 1;");
        var tool = Environment.ProcessPath ?? "/bin/sh";
        var options = new AzureProviderRunnerOptions
        {
            Enabled = true,
            AzureCliPath = tool,
            AzureCliClientId = "33333333-3333-3333-3333-333333333333",
            SqlCmdPath = tool,
            CurlPath = tool,
            TemplateRoot = _root,
            SqlBootstrapObjectId = "11111111-1111-1111-1111-111111111111",
            SqlBootstrapLogin = "bootstrap",
            SqlBootstrapIp = "203.0.113.10",
            RuntimeAdminUsername = "runtime-admin"
        };
        var scope = new AzureProviderTargetScope(
            "11111111-1111-1111-1111-111111111111",
            "proof-rg",
            "22222222-2222-2222-2222-222222222222",
            "registry-rg",
            "valenceruntimeimages",
            "westeurope");
        return new(options, scope);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingDbContextInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentBag<Guid> _contextIds = [];

        public IReadOnlyCollection<Guid> ContextIds => _contextIds;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context)
                _contextIds.Add(context.ContextId.InstanceId);
            return ValueTask.FromResult(result);
        }

        public void Clear() => _contextIds.Clear();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
