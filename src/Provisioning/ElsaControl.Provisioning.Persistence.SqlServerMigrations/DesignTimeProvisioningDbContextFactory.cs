using ElsaControl.Provisioning.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElsaControl.Provisioning.Persistence.SqlServerMigrations;

public sealed class DesignTimeProvisioningDbContextFactory : IDesignTimeDbContextFactory<ProvisioningDbContext>
{
    public ProvisioningDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>();
        options.UseSqlServer(
            "Server=localhost;Database=ElsaControlCatalog;Integrated Security=true;TrustServerCertificate=True",
            sql => sql.MigrationsAssembly(ProvisioningDatabaseOptions.SqlServerMigrationsAssembly));
        return new ProvisioningDbContext(options.Options);
    }
}
