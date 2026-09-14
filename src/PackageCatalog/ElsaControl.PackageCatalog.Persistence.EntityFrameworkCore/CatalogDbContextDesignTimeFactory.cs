using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

// Used only by `dotnet ef` in migration projects. The API host requires a full runtime
// composition that is intentionally unavailable during design-time model construction.
public sealed class CatalogDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var sqlServer = args.Any(x => string.Equals(x, "--provider=sqlserver", StringComparison.OrdinalIgnoreCase));
        var options = new DbContextOptionsBuilder<CatalogDbContext>();
        if (sqlServer)
            options.UseSqlServer("Server=localhost;Database=ElsaControlCatalog;Integrated Security=true;TrustServerCertificate=True", sql => sql.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly));
        else
            options.UseSqlite("Data Source=:memory:", sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly));
        return new CatalogDbContext(options.Options);
    }
}
