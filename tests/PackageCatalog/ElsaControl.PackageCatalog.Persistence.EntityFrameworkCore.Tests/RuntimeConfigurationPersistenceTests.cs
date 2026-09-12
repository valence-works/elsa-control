using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class RuntimeConfigurationPersistenceTests
{
    [Fact]
    public async Task Persists_runtime_configurations_and_versions()
    {
        await using var db = await CreateDbContextAsync();
        var workspace = new Workspace { Name = "Workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var store = new RuntimeConfigurationStore(db);
        var service = new RuntimeConfigurationService(store);

        var created = await service.CreateAsync(workspace.Id, "Runtime", null, MinimalIntent());
        var version = await service.CreateVersionAsync(workspace.Id, created.Id);

        db.ChangeTracker.Clear();
        Assert.Single((await store.ListAsync(workspace.Id)), x => x.Name == "Runtime");
        Assert.Single((await store.ListVersionsAsync(workspace.Id, created.Id)), x => x.VersionNumber == version!.VersionNumber);
    }

    [Fact]
    public async Task Migration_creates_runtime_configuration_tables()
    {
        await using var db = await CreateMigratedDbContextAsync();
        var workspace = new Workspace { Name = "Workspace" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.RuntimeConfigurations.Add(new RuntimeConfiguration
        {
            WorkspaceId = workspace.Id,
            Name = "Runtime",
            IntentJson = RuntimeConfigurationService.SerializeIntent(MinimalIntent())
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, (await db.RuntimeConfigurations.CountAsync()));
    }

    private static async Task<CatalogDbContext> CreateDbContextAsync()
    {
        var db = CreateDbContext(useMigrations: false);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<CatalogDbContext> CreateMigratedDbContextAsync()
    {
        var db = CreateDbContext(useMigrations: true);
        await db.Database.OpenConnectionAsync();
        await db.Database.MigrateAsync();
        return db;
    }

    private static CatalogDbContext CreateDbContext(bool useMigrations)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:", sqlite =>
            {
                if (useMigrations)
                    sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly);
            })
            .Options;
        return new CatalogDbContext(options);
    }

    private static RuntimeBuilderIntent MinimalIntent() =>
        new(
            new RuntimeImageSelection("elsa-pro-combined", "latest", 8080, new Dictionary<string, string>()),
            [],
            [],
            [],
            new LocalPackagesOptions(false, "packages"));
}
