using ElsaControl.Provisioning.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElsaControl.Provisioning.Persistence.SqliteMigrations;

public sealed class DesignTimeProvisioningDbContextFactory : IDesignTimeDbContextFactory<ProvisioningDbContext>
{
    public ProvisioningDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>();
        options.UseSqlite(
            "Data Source=:memory:",
            sqlite => sqlite.MigrationsAssembly(ProvisioningDatabaseOptions.SqliteMigrationsAssembly));
        return new ProvisioningDbContext(options.Options);
    }
}
