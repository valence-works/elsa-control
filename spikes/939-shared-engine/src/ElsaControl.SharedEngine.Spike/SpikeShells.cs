using CShells.Configuration;
using CShells.DependencyInjection;
using CShells.FastEndpoints.Features;
using Elsa.Http.Handlers;
using Elsa.Http.ShellFeatures;
using Elsa.Identity.ShellFeatures;
using Elsa.Persistence.EFCore.Sqlite.ShellFeatures;
using Elsa.Persistence.EFCore.Sqlite.ShellFeatures.Tenants;
using Elsa.Persistence.EFCore.SqlServer.ShellFeatures;
using Elsa.Persistence.EFCore.SqlServer.ShellFeatures.Tenants;
using Elsa.Scheduling.ShellFeatures;
using Elsa.ShellFeatures;
using Elsa.Tenants.AspNetCore.ShellFeatures;
using Elsa.Tenants.ShellFeatures;
using Elsa.Workflows.Api.ShellFeatures;
using Elsa.Workflows.Management.ShellFeatures;
using Elsa.Workflows.Runtime.Distributed.ShellFeatures;
using Elsa.Workflows.Runtime.ShellFeatures;
using Elsa.Workflows.ShellFeatures;
using Medallion.Threading.FileSystem;

namespace ElsaControl.SharedEngine.Spike;

internal static class SpikeShells
{
    public static CShellsBuilder AddSpikeShells(this CShellsBuilder shells, SpikeOptions options)
    {
        return options.Mode.Trim().ToLowerInvariant() switch
        {
            "fallback" => shells.AddFallbackShells(options),
            "lookup" => shells.AddLookupShells(options),
            _ => shells.AddShardShells(options)
        };
    }

    private static CShellsBuilder AddShardShells(this CShellsBuilder shells, SpikeOptions options)
    {
        var lockDirectory = new DirectoryInfo(Path.Combine(options.DataDirectory, "locks"));
        Directory.CreateDirectory(lockDirectory.FullName);

        shells.AddShell(SpikeConstants.DefaultShell, shell =>
        {
            shell.WithFeature<SpikeProbeFeature>();
            shell.WithConfiguration("WebRouting:Path", "");
        });

        AddElsaTenantShell(
            shells,
            SpikeConstants.TenantA,
            SpikeConstants.TenantAHost,
            SpikePersistence.WorkflowConnectionString(options, "spike_939_tenant_a"),
            SpikeConstants.TenantAApiKey,
            SpikeConstants.TenantASigningKey,
            options,
            lockDirectory);

        AddElsaTenantShell(
            shells,
            SpikeConstants.TenantB,
            SpikeConstants.TenantBHost,
            SpikePersistence.WorkflowConnectionString(options, "spike_939_tenant_b"),
            SpikeConstants.TenantBApiKey,
            SpikeConstants.TenantBSigningKey,
            options,
            lockDirectory);

        return shells;
    }

    private static CShellsBuilder AddFallbackShells(this CShellsBuilder shells, SpikeOptions options)
    {
        var connectionString = SpikePersistence.WorkflowConnectionString(options, "spike_939_fallback");
        shells.AddShell(SpikeConstants.DefaultShell, shell =>
        {
            ConfigureElsaFeatures(shell, connectionString, SpikeConstants.FallbackApiKey, SpikeConstants.FallbackSigningKey, options, lockDirectory: null);
            shell.WithFeature<TenantsFeature>(feature => feature.RejectUnresolvedTenants = true);
            shell.WithFeature<TenantManagementFeature>();
            shell.WithFeature<MultitenantHttpRoutingFeature>();
            if (SpikePersistence.UsesSqlServer(options))
                shell.WithFeature<SqlServerTenantPersistenceShellFeature>(feature => feature.ConnectionString = connectionString);
            else
                shell.WithFeature<SqliteTenantPersistenceShellFeature>(feature => feature.ConnectionString = connectionString);
            shell.WithConfiguration("WebRouting:Path", "");
        });
        return shells;
    }

    private static CShellsBuilder AddLookupShells(this CShellsBuilder shells, SpikeOptions options)
    {
        shells.AddShell(SpikeConstants.DefaultShell, shell =>
        {
            shell.WithFeature<SpikeProbeFeature>();
            shell.WithConfiguration("WebRouting:Path", "");
        });

        for (var i = 0; i < options.ProbeShellCount; i++)
        {
            var name = $"{options.ProbeHostPrefix}-{i}";
            shells.AddShell(name, shell =>
            {
                shell.WithFeature<SpikeProbeFeature>();
                shell.WithConfiguration("WebRouting:Host", $"{name}.local");
                shell.WithConfiguration("WebRouting:Path", "");
            });
        }

        return shells;
    }

    private static void AddElsaTenantShell(
        CShellsBuilder shells,
        string name,
        string host,
        string connectionString,
        string apiKey,
        string signingKey,
        SpikeOptions options,
        DirectoryInfo lockDirectory)
    {
        shells.AddShell(name, shell =>
        {
            ConfigureElsaFeatures(shell, connectionString, apiKey, signingKey, options, lockDirectory);
            shell.WithFeature<SpikeProbeFeature>();
            shell.WithConfiguration("WebRouting:Host", host);
            shell.WithConfiguration("WebRouting:Path", "");
        });
    }

    private static void ConfigureElsaFeatures(
        ShellBuilder shell,
        string connectionString,
        string apiKey,
        string signingKey,
        SpikeOptions options,
        DirectoryInfo? lockDirectory)
    {
        // Diagnostics modules stay disabled on shared engines. Proto.Actor is not enabled.
        shell.WithFeatures(
            typeof(ElsaFeature),
            typeof(WorkflowManagementFeature),
            typeof(WorkflowInstancesFeature),
            typeof(WorkflowRuntimeFeature),
            typeof(WorkflowsFeature),
            typeof(DistributedRuntimeFeature),
            typeof(WorkflowsApiFeature),
            typeof(IdentityFeature),
            typeof(DefaultAuthenticationFeature),
            typeof(DefaultAdminUserFeature),
            typeof(FastEndpointsFeature),
            typeof(HttpFeature),
            typeof(SchedulingFeature));

        shell.WithFeature<DefaultAuthenticationFeature>(feature => feature.AdminApiKey = apiKey);
        shell.WithFeature<DefaultAdminUserFeature>(feature =>
        {
            feature.AdminUserName = SpikeConstants.AdminUserName;
            feature.AdminPassword = SpikeConstants.AdminPassword;
        });
        shell.WithFeature<FastEndpointsFeature>(feature => feature.EndpointRoutePrefix = "elsa/api");
        shell.WithFeature<HttpFeature>(feature =>
        {
            feature.HttpEndpointAuthorizationHandler = sp => sp.GetRequiredService<AllowAnonymousHttpEndpointAuthorizationHandler>();
            feature.HttpActivityOptions.BasePath = "/workflows";
            feature.HttpActivityOptions.BaseUrl = new Uri("http://localhost");
        });
        if (lockDirectory is not null)
        {
            shell.WithFeature<WorkflowRuntimeFeature>(feature =>
            {
                feature.DistributedLockProvider = _ => new FileDistributedSynchronizationProvider(lockDirectory);
            });
        }

        if (SpikePersistence.UsesSqlServer(options))
            shell.WithFeature<SqlServerWorkflowPersistenceShellFeature>(feature => feature.ConnectionString = connectionString);
        else
            shell.WithFeature<SqliteWorkflowPersistenceShellFeature>(feature => feature.ConnectionString = connectionString);

        shell.WithConfiguration("Identity:SigningKey", signingKey);
        shell.WithConfiguration("Identity:AccessTokenLifetime", "01:00:00");
        shell.WithConfiguration("Identity:RefreshTokenLifetime", "1.00:00:00");
    }
}
