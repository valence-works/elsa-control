using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CShells;
using CShells.Features;
using CShells.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
namespace ElsaControl.SharedShellAc1;

public sealed class ActivityDescriptorRecord
{
    public string TypeName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string PackageRevision { get; init; } = "";
    public string AssemblyQualifiedName { get; init; } = "";
}

public interface IVerifiedActivityCatalog
{
    IReadOnlyList<ActivityDescriptorRecord> Descriptors { get; }
    object Create(string typeName);
    string ExecuteFirstWorkflow(string input);
}

public sealed class VerifiedActivityCatalog : IVerifiedActivityCatalog
{
    private readonly ConcurrentDictionary<string, Type> _types = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json;
    private readonly IMemoryCache _cache;
    private List<ActivityDescriptorRecord> _descriptors = [];

    public VerifiedActivityCatalog(JsonSerializerOptions json, IMemoryCache cache)
    {
        _json = json;
        _cache = cache;
    }

    public IReadOnlyList<ActivityDescriptorRecord> Descriptors => _descriptors;

    public void LoadFrom(Assembly packageAssembly, string packageRevision)
    {
        var activityInterface = packageAssembly.GetTypes().First(t => t.Name == "IVerifiedActivity" && t.IsInterface);
        var activities = packageAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && activityInterface.IsAssignableFrom(t))
            .ToArray();

        var descriptors = new List<ActivityDescriptorRecord>(activities.Length);
        foreach (var type in activities)
        {
            var instance = Activator.CreateInstance(type)!;
            var typeName = (string)type.GetProperty("TypeName")!.GetValue(instance)!;
            _types[typeName] = type;
            descriptors.Add(new ActivityDescriptorRecord
            {
                TypeName = typeName,
                DisplayName = type.Name,
                PackageRevision = packageRevision,
                AssemblyQualifiedName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name,
            });
            _cache.Set($"descriptor:{typeName}", descriptors[^1], TimeSpan.FromMinutes(10));
        }

        _descriptors = descriptors;
        _ = JsonSerializer.Serialize(descriptors, _json);
    }

    public void Clear()
    {
        _types.Clear();
        _descriptors = [];
    }

    public object Create(string typeName)
    {
        if (!_types.TryGetValue(typeName, out var type))
            throw new InvalidOperationException($"Activity '{typeName}' is not in this shell catalog.");
        return Activator.CreateInstance(type)!;
    }

    public string ExecuteFirstWorkflow(string input)
    {
        var activity = Create("Verified.Ready");
        var execute = activity.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Verified activity is missing Execute.");
        return (string)execute.Invoke(activity, [input])!;
    }
}

public sealed class ShellCatalogEntity
{
    public int Id { get; set; }
    public required string TypeName { get; set; }
    public required string DisplayName { get; set; }
    public required string PackageRevision { get; set; }
    public required string AssemblyQualifiedName { get; set; }
}

public sealed class ShellTypeAliasEntity
{
    public int Id { get; set; }
    public required string Alias { get; set; }
    public required string ClrName { get; set; }
}

public sealed class ShellBookmarkMetadataEntity
{
    public int Id { get; set; }
    public required string ResumeTargetId { get; set; }
    public required string PackageRevision { get; set; }
}

public sealed class ShellCatalogDbContext : DbContext
{
    public ShellCatalogDbContext(DbContextOptions<ShellCatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<ShellCatalogEntity> Activities => Set<ShellCatalogEntity>();
    public DbSet<ShellTypeAliasEntity> TypeAliases => Set<ShellTypeAliasEntity>();
    public DbSet<ShellBookmarkMetadataEntity> Bookmarks => Set<ShellBookmarkMetadataEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShellCatalogEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TypeName).IsUnique();
            entity.Property(e => e.TypeName).HasMaxLength(200);
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.PackageRevision).HasMaxLength(128);
            entity.Property(e => e.AssemblyQualifiedName).HasMaxLength(512);
        });
        modelBuilder.Entity<ShellTypeAliasEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Alias).IsUnique();
            entity.Property(e => e.Alias).HasMaxLength(200);
            entity.Property(e => e.ClrName).HasMaxLength(512);
        });
        modelBuilder.Entity<ShellBookmarkMetadataEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ResumeTargetId);
            entity.Property(e => e.ResumeTargetId).HasMaxLength(128);
            entity.Property(e => e.PackageRevision).HasMaxLength(128);
        });
    }
}

internal sealed class AllowlistDelegatingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";
        if (!host.Equals("example.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Host '{host}' is not on the verified-package allowlist.");
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class CatalogPrepareInitializer(
    ShellCatalogDbContext db,
    VerifiedActivityCatalog catalog,
    PackageRevisionLoadContext package) : IShellInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        catalog.LoadFrom(package.Assembly, package.RevisionHash);

        if (!await db.Activities.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Activities.AddRange(catalog.Descriptors.Select(d => new ShellCatalogEntity
            {
                TypeName = d.TypeName,
                DisplayName = d.DisplayName,
                PackageRevision = d.PackageRevision,
                AssemblyQualifiedName = d.AssemblyQualifiedName,
            }));
            db.TypeAliases.AddRange(catalog.Descriptors.Select(d => new ShellTypeAliasEntity
            {
                Alias = d.TypeName,
                ClrName = d.AssemblyQualifiedName,
            }));
            db.Bookmarks.Add(new ShellBookmarkMetadataEntity
            {
                ResumeTargetId = "ac1-first-workflow",
                PackageRevision = package.RevisionHash,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

[ShellFeature("TenantGroupRuntime", DisplayName = "Tenant group runtime")]
public sealed class TenantGroupRuntimeFeature : IShellFeature
{
    private readonly ShellSettings _settings;
    private readonly PackageRevisionLoadContext _package;
    private readonly string _dataDirectory;

    public TenantGroupRuntimeFeature(ShellSettings settings, PackageRevisionLoadContext package, SharedShellAc1Options options)
    {
        _settings = settings;
        _package = package;
        _dataDirectory = options.DataDirectory;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        _package.AddRef();
        var shellName = _settings.Id.Name;
        var dbPath = Path.Combine(_dataDirectory, $"{shellName}.sqlite");

        services.AddMemoryCache();
        services.AddSingleton(_ => new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        });
        services.AddSingleton<VerifiedActivityCatalog>();
        services.AddSingleton<IVerifiedActivityCatalog>(sp => sp.GetRequiredService<VerifiedActivityCatalog>());
        services.AddDbContext<ShellCatalogDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddHttpClient("Elsa.Integrations.Http")
            .AddHttpMessageHandler(() => new AllowlistDelegatingHandler());
        services.AddShellInitializer<CatalogPrepareInitializer>(LifecyclePhase.Prepare, order: 100);
        services.AddShellTerminator<CatalogReleaseTerminator>(LifecyclePhase.Prepare, order: 100);
    }
}

public sealed class CatalogReleaseTerminator(
    VerifiedActivityCatalog catalog,
    PackageRevisionLoadContext package) : IShellTerminator
{
    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        catalog.Clear();
        package.Release();
        return Task.CompletedTask;
    }
}

public sealed class SharedShellAc1Options
{
    public required string DataDirectory { get; init; }
    public required string PackagePath { get; init; }
}
