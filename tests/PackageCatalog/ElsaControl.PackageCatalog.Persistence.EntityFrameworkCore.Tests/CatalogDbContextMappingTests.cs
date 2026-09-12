using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CatalogDbContextMappingTests
{
    [Fact]
    public async Task Can_create_schema_and_store_package_source()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        db.PackageSources.Add(new PackageSource
        {
            Name = "NuGet",
            Url = "https://api.nuget.org/v3/index.json",
            IncludePatterns = ["Elsa.*"],
            VersionDiscoveryPolicy = PackageSourceVersionDiscoveryPolicy.LatestPreview
        });
        await db.SaveChangesAsync();

        var saved = await db.PackageSources.SingleAsync();
        Assert.Equal(PackageSourceVersionDiscoveryPolicy.LatestPreview, saved.VersionDiscoveryPolicy);
    }

    [Fact]
    public async Task Tracks_in_place_package_source_pattern_changes()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        db.PackageSources.Add(new PackageSource
        {
            Name = "NuGet",
            Url = "https://api.nuget.org/v3/index.json",
            IncludePatterns = ["Elsa.*"]
        });
        await db.SaveChangesAsync();

        var source = await db.PackageSources.SingleAsync();
        source.IncludePatterns.Add("Elsa.Workflows.*");
        source.ExcludePatterns.Add("Elsa.Experimental.*");
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();

        var saved = await db.PackageSources.SingleAsync();
        Assert.Contains("Elsa.Workflows.*", saved.IncludePatterns);
        Assert.Contains("Elsa.Experimental.*", saved.ExcludePatterns);
    }

    [Fact]
    public void String_list_comparer_handles_null_values()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        using var db = new CatalogDbContext(options);
        var comparer = db.Model
            .FindEntityType(typeof(PackageSource))!
            .FindProperty(nameof(PackageSource.IncludePatterns))!
            .GetValueComparer();

        Assert.NotNull(comparer);
        Assert.True(comparer!.Equals(null, null));
        Assert.Equal(0, comparer.GetHashCode(null));
        var snapshot = () => comparer.Snapshot(null);
        Assert.Null(Record.Exception(snapshot));
    }
}
