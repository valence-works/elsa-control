using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

public sealed class ProvisioningDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProvisioningDbContext>
{
    public ProvisioningDbContext CreateDbContext(string[] args)
    {
        var sqlServer = args.Any(x => string.Equals(x, "--provider=sqlserver", StringComparison.OrdinalIgnoreCase));
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>();
        if (sqlServer)
        {
            options.UseSqlServer(
                "Server=localhost;Database=ElsaControlCatalog;Integrated Security=true;TrustServerCertificate=True",
                sql => sql.MigrationsAssembly(ProvisioningDatabaseOptions.SqlServerMigrationsAssembly));
        }
        else
        {
            options.UseSqlite(
                "Data Source=:memory:",
                sqlite => sqlite.MigrationsAssembly(ProvisioningDatabaseOptions.SqliteMigrationsAssembly));
        }

        return new ProvisioningDbContext(options.Options);
    }
}
