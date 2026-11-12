using Elsa.Common.Multitenancy;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Tenants;
using Elsa.Tenants.Options;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ElsaControl.SharedEngine.Spike.Tests;

[Collection("FallbackHost")]
public sealed class TenantFilterTests
{
    private readonly FallbackHostFixture _fixture;

    public TenantFilterTests(FallbackHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task G1_tenant_filter_applies_in_shell_mode()
    {
        var shell = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.DefaultShell);
        await using var scope = shell.BeginScope();
        var services = scope.ServiceProvider;

        var tenantsOptions = services.GetRequiredService<IOptions<TenantsOptions>>().Value;
        Assert.True(tenantsOptions.IsEnabled);

        await using var dbContext = await services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>().CreateDbContextAsync();
        Assert.True(dbContext.IsTenantFilteringEnabled);

        var tenantStore = services.GetRequiredService<ITenantStore>();
        var tenantA = new Tenant { Id = "filter-a", Name = "filter-a" };
        var tenantB = new Tenant { Id = "filter-b", Name = "filter-b" };
        await tenantStore.AddAsync(tenantA);
        await tenantStore.AddAsync(tenantB);

        var accessor = services.GetRequiredService<ITenantAccessor>();
        var publisher = services.GetRequiredService<IWorkflowDefinitionPublisher>();
        var store = services.GetRequiredService<IWorkflowDefinitionStore>();

        using (accessor.PushContext(tenantA))
        {
            var definition = await publisher.NewAsync(new Sequence { Activities = { new WriteLine("a") } });
            definition.DefinitionId = SpikeConstants.SharedDefinitionId;
            definition.Name = "filter-a";
            await publisher.SaveDraftAsync(definition);
        }

        using (accessor.PushContext(tenantB))
        {
            var definition = await publisher.NewAsync(new Sequence { Activities = { new WriteLine("b") } });
            definition.DefinitionId = SpikeConstants.SharedDefinitionId;
            definition.Name = "filter-b";
            await publisher.SaveDraftAsync(definition);
        }

        IEnumerable<Elsa.Workflows.Management.Entities.WorkflowDefinition> seenByA;
        using (accessor.PushContext(tenantA))
            seenByA = await store.FindManyAsync(new WorkflowDefinitionFilter { DefinitionId = SpikeConstants.SharedDefinitionId });

        IEnumerable<Elsa.Workflows.Management.Entities.WorkflowDefinition> seenByB;
        using (accessor.PushContext(tenantB))
            seenByB = await store.FindManyAsync(new WorkflowDefinitionFilter { DefinitionId = SpikeConstants.SharedDefinitionId });

        Assert.Single(seenByA);
        Assert.Equal("filter-a", seenByA.Single().Name);
        Assert.Single(seenByB);
        Assert.Equal("filter-b", seenByB.Single().Name);
    }

    [Fact]
    public async Task G2_definition_uniqueness_includes_tenant()
    {
        var shell = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.DefaultShell);
        await using var scope = shell.BeginScope();
        var services = scope.ServiceProvider;
        var accessor = services.GetRequiredService<ITenantAccessor>();
        var publisher = services.GetRequiredService<IWorkflowDefinitionPublisher>();
        var tenantStore = services.GetRequiredService<ITenantStore>();
        await tenantStore.AddAsync(new Tenant { Id = "unique-a", Name = "unique-a" });
        await tenantStore.AddAsync(new Tenant { Id = "unique-b", Name = "unique-b" });

        using (accessor.PushContext(new Tenant { Id = "unique-a", Name = "unique-a" }))
        {
            var definition = await publisher.NewAsync(new Sequence());
            definition.DefinitionId = "g2-shared";
            definition.Name = "unique-a";
            await publisher.SaveDraftAsync(definition);
        }

        using (accessor.PushContext(new Tenant { Id = "unique-b", Name = "unique-b" }))
        {
            var definition = await publisher.NewAsync(new Sequence());
            definition.DefinitionId = "g2-shared";
            definition.Name = "unique-b";
            await publisher.SaveDraftAsync(definition);
        }

        await using var dbContext = await services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>().CreateDbContextAsync();
        var rows = await dbContext.WorkflowDefinitions
            .IgnoreQueryFilters()
            .Where(definition => definition.DefinitionId == "g2-shared")
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.TenantId == "unique-a");
        Assert.Contains(rows, row => row.TenantId == "unique-b");

        var source = File.ReadAllText(Path.Combine(
            Environment.GetEnvironmentVariable("ElsaCoreRoot") ?? FindElsaCoreRoot(),
            "core/src/modules/Elsa.Persistence.EFCore/Modules/Management/Configurations.cs"));
        Assert.Contains("x.DefinitionId, x.TenantId, x.Version", source);
    }

    private static string FindElsaCoreRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".deps", "elsa-core");
            if (Directory.Exists(Path.Combine(candidate, "core", "src")))
                return candidate;
            var spike = Path.Combine(directory.FullName, "spikes", "939-shared-engine", ".deps", "elsa-core");
            if (Directory.Exists(Path.Combine(spike, "core", "src")))
                return spike;
            directory = directory.Parent;
        }

        var env = Environment.GetEnvironmentVariable("ElsaCoreRoot");
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        throw new DirectoryNotFoundException("Pinned elsa-core checkout not found.");
    }
}
