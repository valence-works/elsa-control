using System.Data.Common;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CompatibilityQueriesTests
{
    [Fact]
    public async Task Loads_exact_selected_versions_with_one_database_command()
    {
        var commandCounter = new CommandCounterInterceptor();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .AddInterceptors(commandCounter)
            .Options;

        await using var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var source = PublicCatalogSeedData.CreatePackageSource();
        var firstPackage = PublicCatalogSeedData.CreatePackage(source, "Elsa.First");
        PublicCatalogSeedData.AddVersion(firstPackage, "1.0.0");
        PublicCatalogSeedData.AddVersion(firstPackage, "2.0.0");
        var secondPackage = PublicCatalogSeedData.CreatePackage(source, "Elsa.Second");
        PublicCatalogSeedData.AddVersion(secondPackage, "1.0.0");
        PublicCatalogSeedData.AddVersion(secondPackage, "2.0.0");
        db.PackageSources.Add(source);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commandCounter.Reset();

        var result = await new CompatibilityQueries(db).GetPackageVersionsAsync(
            null,
            [
                new SelectedPackageVersion(source.Id, "Elsa.First", "1.0.0"),
                new SelectedPackageVersion(source.Id, "Elsa.Second", "2.0.0")
            ]);

        Assert.Equal(1, commandCounter.ReaderCommandCount);
        Assert.Collection(
            result.OrderBy(version => version.Package!.PackageId),
            version =>
            {
                Assert.Equal("Elsa.First", version.Package!.PackageId);
                Assert.Equal("1.0.0", version.Version);
            },
            version =>
            {
                Assert.Equal("Elsa.Second", version.Package!.PackageId);
                Assert.Equal("2.0.0", version.Version);
            });
    }

    private sealed class CommandCounterInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }

        public void Reset() => ReaderCommandCount = 0;
    }
}
