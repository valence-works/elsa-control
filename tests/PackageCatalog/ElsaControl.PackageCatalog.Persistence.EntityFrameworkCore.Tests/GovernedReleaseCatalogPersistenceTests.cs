using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class GovernedReleaseCatalogPersistenceTests
{
    [Fact]
    public async Task Stores_all_topologies_as_one_typed_release_aggregate()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var db = database.Context;
        var entries = CreateEntries();

        var result = await database.Store.StoreAsync(entries);

        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, result.Status);
        Assert.Single(await db.GovernedReleaseCatalog.ToListAsync());
        var release = await db.GovernedReleaseCatalog
            .Include(x => x.PackageDeclarations)
            .Include(x => x.Topologies)
            .ThenInclude(x => x.RuntimeKinds)
            .Include(x => x.Topologies)
            .ThenInclude(x => x.Capabilities)
            .Include(x => x.Topologies)
            .ThenInclude(x => x.Components)
            .ThenInclude(x => x.PlatformDigests)
            .SingleAsync();

        Assert.Equal(2, release.Topologies.Count);
        Assert.Equal(2, release.Topologies.SelectMany(x => x.Components).Count());
        Assert.Equal(2, release.Topologies.SelectMany(x => x.RuntimeKinds).Count());
        Assert.Equal("preview", release.CatalogLifecycle);
        Assert.Equal("central-package-declarations-v1", release.ComponentDeclarationsFormat);
        Assert.Equal(Digest('4'), release.ComponentDeclarationsDigest);
        Assert.Equal(
            ["Elsa.Persistence.EFCore.SqlServer@3.8.0-preview.5413", "Elsa.Scheduling.Quartz.EFCore.SqlServer@3.8.0-preview.5413"],
            release.PackageDeclarations
                .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(x => $"{x.PackageId}@{x.Version}")
                .ToArray());
        Assert.All(release.Topologies.SelectMany(x => x.Components), component =>
            Assert.StartsWith("sha256:", component.ImageDigest, StringComparison.OrdinalIgnoreCase));
        var queried = await database.Store.QueryAsync(new GovernedReleaseCatalogQuery(TopologyId: "server"));
        var declarations = Assert.Single(queried).ComponentDeclarations;
        Assert.NotNull(declarations);
        Assert.Equal("central-package-declarations-v1", declarations!.Format);
        Assert.Equal(Digest('4'), declarations.Digest);
        Assert.Equal(
            ["Elsa.Persistence.EFCore.SqlServer@3.8.0-preview.5413", "Elsa.Scheduling.Quartz.EFCore.SqlServer@3.8.0-preview.5413"],
            declarations.Packages.Select(package => $"{package.Id}@{package.Version}").ToArray());
    }

    [Fact]
    public async Task Replaying_same_immutable_release_with_a_new_admission_time_is_unchanged()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var db = database.Context;
        var store = database.Store;
        var original = CreateEntries(DateTimeOffset.UtcNow);

        var first = await store.StoreAsync(original);
        var replay = await store.StoreAsync(original
            .Select(x => x with { AdmittedAt = x.AdmittedAt.AddHours(1) })
            .ToArray());

        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, first.Status);
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Unchanged, replay.Status);
        Assert.Equal(1, await db.GovernedReleaseCatalog.CountAsync());
        Assert.Equal(2, await db.GovernedReleaseCatalogTopologies.CountAsync());
    }

    [Fact]
    public async Task Projection_conflict_does_not_mutate_the_existing_aggregate()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var db = database.Context;
        var store = database.Store;
        var original = CreateEntries();
        await store.StoreAsync(original);

        var conflicting = original
            .Select(entry => entry.Topology.Id == "server"
                ? entry with
                {
                    Topology = entry.Topology with
                    {
                        Components = entry.Topology.Components
                            .Select(component => component with
                            {
                                ImageReference = $"oci://images/{component.Id}@{Digest('f')}",
                                ImageDigest = Digest('f')
                            })
                            .ToArray()
                    }
                }
                : entry)
            .ToArray();

        var result = await store.StoreAsync(conflicting);

        Assert.Equal(GovernedReleaseCatalogWriteStatus.Conflict, result.Status);
        Assert.Equal(2, await db.GovernedReleaseCatalogComponents.CountAsync());
        Assert.DoesNotContain(await db.GovernedReleaseCatalogComponents.Select(x => x.ImageDigest).ToListAsync(), x => x == Digest('f'));
    }

    [Fact]
    public async Task Changed_release_package_declarations_conflict_without_mutating_the_aggregate()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var original = CreateEntries();
        await database.Store.StoreAsync(original);

        var conflicting = original.Select(entry => entry with
        {
            ComponentDeclarations = entry.ComponentDeclarations! with
            {
                Packages = entry.ComponentDeclarations.Packages
                    .Select(package => package.Id == "Elsa.Persistence.EFCore.SqlServer"
                        ? package with { Version = "5.0.0" }
                        : package)
                    .ToArray()
            }
        }).ToArray();

        var result = await database.Store.StoreAsync(conflicting);

        Assert.Equal(GovernedReleaseCatalogWriteStatus.Conflict, result.Status);
        Assert.DoesNotContain(
            await database.Context.GovernedReleaseCatalogPackageDeclarations.Select(x => x.Version).ToArrayAsync(),
            version => version == "5.0.0");
    }

    [Fact]
    public async Task Query_filters_runtime_kind_capability_and_lifecycle_per_topology()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var store = database.Store;
        await store.StoreAsync(CreateEntries());

        var server = await store.QueryAsync(new GovernedReleaseCatalogQuery(RuntimeKind: "elsa.server"));
        var capability = await store.QueryAsync(new GovernedReleaseCatalogQuery(Capability: "studio-ui"));
        var lifecycle = await store.QueryAsync(new GovernedReleaseCatalogQuery(CatalogLifecycle: "PREVIEW"));

        Assert.Equal("server", Assert.Single(server).Topology.Id);
        Assert.Equal("studio", Assert.Single(capability).Topology.Id);
        Assert.Equal(2, lifecycle.Count);
        Assert.Equal(new[] { "server", "studio" }, lifecycle.Select(x => x.Topology.Id).ToArray());
    }

    [Fact]
    public async Task Catalog_rows_are_immutable_through_the_db_context()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var db = database.Context;
        await database.Store.StoreAsync(CreateEntries());

        var release = await db.GovernedReleaseCatalog.SingleAsync();
        release.ReleaseLine = "5.0";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("3.8", await db.GovernedReleaseCatalog.Select(x => x.ReleaseLine).SingleAsync());
    }

    [Fact]
    public async Task Mutable_image_reference_is_rejected_before_any_catalog_row_is_written()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var db = database.Context;
        var entries = CreateEntries()
            .Select(entry => entry with
            {
                Topology = entry.Topology with
                {
                    Components = entry.Topology.Components
                        .Select(component => component with
                        {
                            ImageReference = $"oci://images/{component.Id}:latest@{component.ImageDigest}"
                        })
                        .ToArray()
                }
            })
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.StoreAsync(entries));

        Assert.Empty(await db.GovernedReleaseCatalog.ToListAsync());
    }

    [Fact]
    public async Task Mixed_schema_versions_are_rejected_before_any_catalog_row_is_written()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var entries = CreateEntries().ToArray();
        entries[1] = entries[1] with { SchemaVersion = entries[0].SchemaVersion + 1 };

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.StoreAsync(entries));

        Assert.Empty(await database.Context.GovernedReleaseCatalog.ToListAsync());
    }

    [Fact]
    public async Task Values_exceeding_shared_storage_limits_are_rejected_for_every_provider()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var entries = CreateEntries()
            .Select(entry => entry with
            {
                Distribution = entry.Distribution with
                {
                    ReleaseLine = new string('x', GovernedReleaseCatalogFieldLimits.ReleaseLine + 1)
                }
            })
            .ToArray();

        await Assert.ThrowsAsync<GovernedReleaseCatalogStorageValidationException>(() =>
            database.Store.StoreAsync(entries));

        Assert.Empty(await database.Context.GovernedReleaseCatalog.ToListAsync());
    }

    [Fact]
    public async Task Arbitrary_release_lines_share_the_same_durable_query_path()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var store = database.Store;
        var releases = new[]
        {
            (Line: "3.8", Version: "3.8.0", Digit: 'a'),
            (Line: "3.9", Version: "3.9.0", Digit: 'b'),
            (Line: "4.1", Version: "4.1.0", Digit: 'c'),
            (Line: "5.0", Version: "5.0.0", Digit: 'd')
        };

        foreach (var release in releases)
            Assert.Equal(
                GovernedReleaseCatalogWriteStatus.Stored,
                (await store.StoreAsync(CreateEntries(
                    releaseLine: release.Line,
                    releaseVersion: release.Version,
                    manifestDigit: release.Digit))).Status);

        var all = await store.QueryAsync(new GovernedReleaseCatalogQuery());
        var selected = await store.QueryAsync(new GovernedReleaseCatalogQuery(ReleaseLine: "4.1"));

        Assert.Equal(8, all.Count);
        Assert.Equal(
            new[] { "3.8", "3.8", "3.9", "3.9", "4.1", "4.1", "5.0", "5.0" },
            all.Select(x => x.Distribution.ReleaseLine).ToArray());
        Assert.All(selected, entry => Assert.Equal("4.1.0", entry.Distribution.ReleaseVersion));
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public async Task Query_uses_invariant_normalized_identity_columns_under_turkish_culture()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var entries = CreateEntries()
            .Select((entry, index) => entry with
            {
                CatalogLifecycle = "IDENTITY",
                Distribution = entry.Distribution with
                {
                    Id = "IDENTITY",
                    Generation = "IDENTITY",
                    Channel = "IDENTITY",
                    ProducerLifecycle = "IDENTITY"
                },
                Topology = entry.Topology with
                {
                    Id = $"IDENTITY-{index}",
                    RuntimeKinds = ["IDENTITY"],
                    Capabilities = ["IDENTITY"],
                    Components = entry.Topology.Components
                        .Select(component => component with { Capabilities = ["IDENTITY"] })
                        .ToArray()
                }
            })
            .ToArray();
        var write = await database.Store.StoreAsync(entries);

        Assert.All(write.Entries, entry =>
        {
            Assert.Equal(entry.Distribution.Id.ToLowerInvariant(), entry.Distribution.Id);
            Assert.Equal(entry.Topology.Id.ToLowerInvariant(), entry.Topology.Id);
        });
        Assert.Equal(
            GovernedReleaseCatalogWriteStatus.Unchanged,
            (await database.Store.StoreAsync(write.Entries)).Status);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var result = await database.Store.QueryAsync(new GovernedReleaseCatalogQuery(
                DistributionId: "IDENTITY",
                Channel: "IDENTITY",
                ProducerLifecycle: "IDENTITY",
                CatalogLifecycle: "IDENTITY",
                TopologyId: "IDENTITY-0",
                RuntimeKind: "IDENTITY",
                Capability: "IDENTITY"));

            Assert.Single(result);
            Assert.Equal("identity", result[0].Distribution.Id);
            Assert.Equal("identity-0", result[0].Topology.Id);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task Whitespace_only_topology_filters_have_the_same_no_filter_semantics()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        await database.Store.StoreAsync(CreateEntries());

        var result = await database.Store.QueryAsync(new GovernedReleaseCatalogQuery(
            TopologyId: "  ",
            RuntimeKind: "\t",
            Capability: "\r\n"));

        Assert.Equal(2, result.Count);
        Assert.Equal(["server", "studio"], result.Select(x => x.Topology.Id).ToArray());
    }

    private static async Task<TestCatalogDatabase> CreateMigratedDatabaseAsync()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite($"Data Source=release-catalog-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
            .Options;
        var db = new CatalogDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        return new TestCatalogDatabase(db, new GovernedReleaseCatalogStore(options));
    }

    private sealed record TestCatalogDatabase(
        CatalogDbContext Context,
        GovernedReleaseCatalogStore Store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static IReadOnlyList<GovernedReleaseCatalogEntry> CreateEntries(
        DateTimeOffset? admittedAt = null,
        string releaseLine = "3.8",
        string releaseVersion = "3.8.0-preview.5413",
        char manifestDigit = 'a')
    {
        var distribution = new GovernedReleaseDistribution(
            "valence-runtime",
            $"build-{releaseVersion}",
            releaseLine,
            releaseVersion,
            "preview",
            "Preview",
            "commercial",
            "https://github.com/valence-works/elsa-production-image",
            new string('a', 40),
            "79");

        return
        [
            CreateEntry(distribution, "server", ["elsa.server"], ["workflow"], "server", admittedAt ?? DateTimeOffset.UnixEpoch, manifestDigit),
            CreateEntry(distribution, "studio", ["elsa.studio"], ["studio-ui"], "studio", admittedAt ?? DateTimeOffset.UnixEpoch, manifestDigit)
        ];
    }

    private static GovernedReleaseCatalogEntry CreateEntry(
        GovernedReleaseDistribution distribution,
        string topologyId,
        IReadOnlyList<string> runtimeKinds,
        IReadOnlyList<string> topologyCapabilities,
        string componentId,
        DateTimeOffset admittedAt,
        char manifestDigit) => new(
        "2.0.0",
        $"oci://manifests.example/releases/release@{Digest(manifestDigit)}",
        Digest(manifestDigit),
        Digest('b'),
        $"https://sigstore.example/signature@{Digest('c')}",
        Digest('c'),
        "paid",
        distribution,
        new(
            topologyId,
            "producer-2.0.0",
            runtimeKinds,
            topologyCapabilities,
            [new(componentId, distribution.ReleaseVersion)],
            [new(
                componentId,
                $"oci://images/{componentId}@{Digest(topologyId == "server" ? 'd' : 'e')}",
                Digest(topologyId == "server" ? 'd' : 'e'),
                new Dictionary<string, string> { ["linux/amd64"] = Digest('f') },
                [componentId == "server" ? "api" : "ui"],
                topologyCapabilities,
                [new("https", "https", 443, "public", true, $"/{componentId}")],
                null)],
            [new("sbom", $"https://evidence.example/sbom@{Digest('1')}", Digest('1')),
             new("provenance", $"https://evidence.example/provenance@{Digest('2')}", Digest('2')),
             new("vulnerability-scan", $"https://evidence.example/scan@{Digest('3')}", Digest('3'))]),
        "preview",
        admittedAt,
        new(
            "central-package-declarations-v1",
            Digest('4'),
            [new("Elsa.Persistence.EFCore.SqlServer", distribution.ReleaseVersion),
             new("Elsa.Scheduling.Quartz.EFCore.SqlServer", distribution.ReleaseVersion)]));

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
}
