using ElsaControl.Provisioning.Core.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Provisioning.Persistence.EntityFrameworkCore;

public static class ProvisioningPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Binds the provisioning queue, lock, and event tables to Control's
    /// catalog SQL database. Replaces the in-memory store. There is no
    /// silent in-memory fallback here.
    /// </summary>
    public static IServiceCollection AddProvisioningPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!TryGetCatalogStore(configuration, out var provider, out var connectionString))
        {
            throw new InvalidOperationException(
                "Provisioning durable store requires ConnectionStrings:Catalog.");
        }

        services.AddDbContextFactory<ProvisioningDbContext>(options =>
            ConfigureProvider(options, provider, connectionString));
        services.RemoveAll<IProvisioningOperationStore>();
        services.RemoveAll<IProvisioningEventStore>();
        services.RemoveAll<IProvisioningStackLockStore>();
        services.RemoveAll<InMemoryProvisioningStore>();
        services.AddSingleton<EfProvisioningStore>();
        services.AddSingleton<IProvisioningOperationStore>(sp => sp.GetRequiredService<EfProvisioningStore>());
        services.AddSingleton<IProvisioningEventStore>(sp => sp.GetRequiredService<EfProvisioningStore>());
        services.AddSingleton<IProvisioningStackLockStore>(sp => sp.GetRequiredService<EfProvisioningStore>());
        services.AddSingleton<IDurableProvisioningStore>(sp => sp.GetRequiredService<EfProvisioningStore>());
        return services;
    }

    public static bool TryGetCatalogStore(
        IConfiguration configuration,
        out string provider,
        out string connectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        provider = configuration["Database:Provider"] ?? "";
        connectionString = configuration.GetConnectionString("Catalog") ?? "";
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        if (string.IsNullOrWhiteSpace(provider))
            provider = "Sqlite";
        return string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
               || string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureProvider(
        DbContextOptionsBuilder options,
        string provider,
        string connectionString)
    {
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlServer(connectionString, builder =>
            {
                builder.MigrationsAssembly(ProvisioningDatabaseOptions.SqlServerMigrationsAssembly);
                builder.EnableRetryOnFailure();
            });
            return;
        }

        if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(connectionString, builder =>
                builder.MigrationsAssembly(ProvisioningDatabaseOptions.SqliteMigrationsAssembly));
            return;
        }

        throw new InvalidOperationException($"Unsupported catalog database provider '{provider}'.");
    }
}
