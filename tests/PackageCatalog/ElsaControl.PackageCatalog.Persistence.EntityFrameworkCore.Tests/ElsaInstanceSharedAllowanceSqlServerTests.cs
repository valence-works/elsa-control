using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ElsaInstanceSharedAllowanceSqlServerTests
{
    private const string ConnectionEnvironmentVariable = "CODEX_664_SQL_CONNECTION";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T10:00:00Z");

    [DisposableSqlServerFact]
    public async Task Parallel_same_kind_moves_at_L_admit_exactly_one_on_sql_server()
    {

        var masterConnectionString = GetLoopbackMasterConnectionString();
        await using var database = await SqlServerTestDatabase.CreateAsync(masterConnectionString);
        Guid organizationId;
        Guid workspaceId;
        Guid firstId;
        Guid secondId;
        Guid shardId;
        await using (var setup = CreateContext(database.ConnectionString))
        {
            await setup.Database.MigrateAsync();
            var workspace = await CreateWorkspaceAsync(setup, maxInstances: 2, maxSharedTenants: 2);
            var shard = new ElsaSharedShardEntity
            {
                Id = Guid.NewGuid(),
                RegionCode = "westeurope",
                ShardClass = ElsaShardClasses.Standard,
                Capacity = 8
            };
            setup.ElsaSharedShards.Add(shard);
            await setup.SaveChangesAsync();
            organizationId = workspace.OrganizationId;
            workspaceId = workspace.Id;
            shardId = shard.Id;
            var service = CreateLifecycleService(setup);
            firstId = (await service.CreateAsync(CreateRequest(workspace, "Shared one", "shared-one"))).Instance.Id;
            secondId = (await service.CreateAsync(CreateRequest(workspace, "Shared two", "shared-two"))).Instance.Id;
        }

        var outcomes = await Task.WhenAll(
            AdmitSameKindSharedMoveAsync(database.ConnectionString, organizationId, workspaceId, firstId, shardId, "first"),
            AdmitSameKindSharedMoveAsync(database.ConnectionString, organizationId, workspaceId, secondId, shardId, "second"));

        Assert.Equal(1, outcomes.Count(x => x.Error is null));
        var loser = Assert.Single(outcomes, x => x.Error is not null);
        var conflict = Assert.IsType<ElsaInstanceLifecycleConflictException>(loser.Error);
        Assert.Equal(ElsaInstanceCommercialOperation.SharedAllowanceMoveInProgress, conflict.CommercialCode);
        await using var verify = CreateContext(database.ConnectionString);
        Assert.Equal(3, await ElsaInstanceAllowanceAccounting.CountSharedStandardAsync(verify, organizationId));
    }

    private static async Task<(bool Succeeded, Exception? Error)> AdmitSameKindSharedMoveAsync(
        string connectionString,
        Guid organizationId,
        Guid workspaceId,
        Guid instanceId,
        Guid shardId,
        string digest)
    {
        try
        {
            await using var db = CreateContext(connectionString);
            await CreateLifecycleService(db).MovePlacementAsync(new ElsaInstancePlacementMoveRequest(
                organizationId,
                workspaceId,
                instanceId,
                ElsaPlacementKinds.SharedShard,
                ElsaShardClasses.Standard,
                PackageSetDigest: digest,
                TargetShardId: shardId));
            return (true, null);
        }
        catch (Exception error)
        {
            return (false, error);
        }
    }

    private static async Task<Workspace> CreateWorkspaceAsync(
        CatalogDbContext db,
        int maxInstances,
        int? maxSharedTenants)
    {
        var organization = new Organization { Name = "Shared allowance sql org " + Guid.NewGuid().ToString("N") };
        db.Organizations.Add(organization);
        var workspace = new Workspace
        {
            Name = "Shared allowance sql " + Guid.NewGuid().ToString("N"),
            OrganizationId = organization.Id,
            Organization = organization
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = maxInstances,
            MaxSharedTenants = maxSharedTenants,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        return workspace;
    }

    private static ElsaInstanceCreateRequest CreateRequest(Workspace workspace, string name, string slug) =>
        new(
            workspace.OrganizationId,
            workspace.Id,
            name,
            slug,
            new ElsaInstanceIntent(
                new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.0", "stable"),
                new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>()),
                new ElsaPlacementIntent("managed", "westeurope", "shared", "standard-small", "public", "managed")),
            $"create-{slug}");

    private static ElsaInstanceLifecycleService CreateLifecycleService(CatalogDbContext db) =>
        new(
            new EfCoreElsaInstanceLifecycleStore(db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(Now)),
            new FixedTimeProvider(Now));

    private static CatalogDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly);
                sqlServer.EnableRetryOnFailure();
            })
            .Options);

    private static string GetLoopbackMasterConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        var builder = new SqlConnectionStringBuilder(value!);
        if (!IsLoopbackDataSource(builder.DataSource))
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} must point to a loopback SQL Server.");
        builder.InitialCatalog = "master";
        builder.Pooling = false;
        return builder.ConnectionString;
    }

    private static bool IsLoopbackDataSource(string dataSource)
    {
        var source = dataSource.Trim();
        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            source = source[4..];

        var separator = source.LastIndexOf(',');
        var host = separator < 0 ? source : source[..separator];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "[::1]";
    }

    private sealed class DisposableSqlServerFactAttribute : FactAttribute
    {
        public DisposableSqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
            {
                Skip =
                    "Set CODEX_664_SQL_CONNECTION to a disposable loopback SQL Server. " +
                    "GitHub CI exports this for serializable range-lock coverage; without it " +
                    "this process cannot exercise SQL Server 1205 deadlock retry.";
            }
        }
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SqlServerTestDatabase : IAsyncDisposable
    {
        private readonly SqlConnection _admin;
        private bool _created;

        private SqlServerTestDatabase(string connectionString, SqlConnection admin)
        {
            ConnectionString = connectionString;
            _admin = admin;
        }

        public string ConnectionString { get; }

        public static async Task<SqlServerTestDatabase> CreateAsync(string masterConnectionString)
        {
            var name = "shared_allowance_" + Guid.NewGuid().ToString("N");
            var admin = new SqlConnection(masterConnectionString);
            var database = new SqlServerTestDatabase(
                new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = name, Pooling = false }.ConnectionString,
                admin);
            try
            {
                await admin.OpenAsync();
                await using var create = new SqlCommand($"CREATE DATABASE [{name}]", admin);
                await create.ExecuteNonQueryAsync();
                database._created = true;
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_created)
            {
                try
                {
                    await using var drop = new SqlCommand(
                        $"ALTER DATABASE [{new SqlConnectionStringBuilder(ConnectionString).InitialCatalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{new SqlConnectionStringBuilder(ConnectionString).InitialCatalog}];",
                        _admin);
                    await drop.ExecuteNonQueryAsync();
                }
                catch (SqlException)
                {
                }
            }

            await _admin.DisposeAsync();
        }
    }
}
