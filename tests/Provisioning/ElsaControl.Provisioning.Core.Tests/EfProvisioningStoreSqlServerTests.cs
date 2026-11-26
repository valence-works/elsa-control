using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Core.Stores;
using ElsaControl.Provisioning.Persistence.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class EfProvisioningStoreSqlServerTests
{
    private const string ConnectionEnvironmentVariable = "CODEX_664_SQL_CONNECTION";

    [Fact]
    public void Sql_server_lease_commands_keep_the_locks_table_as_an_identifier()
    {
        Assert.Equal("provisioning", ProvisioningDbContext.SchemaName);
        AssertNoParameterizedIdentifier(ProvisioningLeaseSql.StampExpiry());
        AssertNoParameterizedIdentifier(ProvisioningLeaseSql.ListExpired());
        AssertNoParameterizedIdentifier(ProvisioningLeaseSql.LeaseExpired());

        var options = new DbContextOptionsBuilder<ProvisioningDbContext>()
            .UseSqlServer("Server=127.0.0.1;Database=unused;TrustServerCertificate=True")
            .Options;
        using var db = new ProvisioningDbContext(options);
        var builder = db.GetService<IRawSqlCommandBuilder>();

        AssertNoParameterizedIdentifier(
            db.Database.SqlQueryRaw<Guid>(ProvisioningLeaseSql.ListExpired()).ToQueryString());
        AssertNoParameterizedIdentifier(
            db.Database.SqlQueryRaw<int>(
                ProvisioningLeaseSql.LeaseExpired(),
                "platform/sql-id",
                Guid.NewGuid()).ToQueryString());
        AssertNoParameterizedIdentifier(
            builder.Build(
                ProvisioningLeaseSql.StampExpiry(),
                [30, "platform/sql-id", "owner", 1L]).RelationalCommand.CommandText);
        AssertNoParameterizedIdentifier(
            builder.Build(
                ProvisioningLeaseSql.LeaseExpired(),
                ["platform/sql-id", Guid.NewGuid()]).RelationalCommand.CommandText);
    }

    [DisposableSqlServerFact]
    public async Task Sql_server_claim_renew_and_reclaim_use_the_locks_table()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetLoopbackMasterConnectionString());
        var options = new DbContextOptionsBuilder<ProvisioningDbContext>()
            .UseSqlServer(database.ConnectionString)
            .Options;
        await using (var created = new ProvisioningDbContext(options))
            await created.Database.EnsureCreatedAsync();
        var factory = new TestFactory(options);

        var live = new EfProvisioningStore(
            factory,
            new ProvisioningReplicaIdentity("sql-live", TimeSpan.FromMinutes(5)));
        var request = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("sql-lease"),
            ProvisioningFixtures.PlatformSpec("sql-lease"),
            "sql-lease");
        var enqueued = await live.CreateOrGetAsync(
            request,
            ProvisioningRequestHash.Compute(request),
            DateTimeOffset.UtcNow);
        var claimed = await live.ClaimAsync(enqueued.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, claimed!.State);
        Assert.True(await live.RenewLeaseAsync(claimed.Id, DateTimeOffset.UtcNow));
        Assert.Empty(await live.FailInterruptedAsync(DateTimeOffset.UtcNow));

        var shortLived = new EfProvisioningStore(
            factory,
            new ProvisioningReplicaIdentity("sql-dead", TimeSpan.FromMilliseconds(1)));
        var expiredRequest = ProvisioningFixtures.Request(
            ProvisioningCommand.Provision,
            ProvisioningFixtures.PlatformTarget("sql-reclaim"),
            ProvisioningFixtures.PlatformSpec("sql-reclaim"),
            "sql-reclaim");
        var expiredEnqueue = await shortLived.CreateOrGetAsync(
            expiredRequest,
            ProvisioningRequestHash.Compute(expiredRequest),
            DateTimeOffset.UtcNow);
        var expiredClaim = await shortLived.ClaimAsync(expiredEnqueue.OperationId, DateTimeOffset.UtcNow);
        Assert.Equal(ProvisioningOperationState.Running, expiredClaim!.State);
        await Task.Delay(1500);
        var reclaim = new EfProvisioningStore(
            factory,
            new ProvisioningReplicaIdentity("sql-reclaim", TimeSpan.FromMinutes(5)));
        var interrupted = await reclaim.FailInterruptedAsync(DateTimeOffset.UtcNow);
        Assert.Contains(interrupted, operation => operation.Id == expiredClaim.Id);
        Assert.Equal(expiredClaim.Id, await reclaim.GetHolderAsync(ProvisioningStackKind.Platform, "sql-reclaim"));
        Assert.True(await reclaim.MarkStateLockClearedAsync(expiredClaim.Id));
        Assert.Null(await reclaim.GetHolderAsync(ProvisioningStackKind.Platform, "sql-reclaim"));
    }

    private static void AssertNoParameterizedIdentifier(string sql)
    {
        Assert.Contains("[provisioning].[Locks]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[@", sql, StringComparison.Ordinal);
    }

    private static string GetLoopbackMasterConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        var builder = new SqlConnectionStringBuilder(value!);
        if (!IsLoopbackDataSource(builder.DataSource))
        {
            throw new InvalidOperationException(
                $"{ConnectionEnvironmentVariable} must point to a loopback SQL Server.");
        }

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
                    $"Set {ConnectionEnvironmentVariable} to a disposable loopback SQL Server. " +
                    "GitHub CI exports this for the Full .NET test job.";
            }
        }
    }

    private sealed class TestFactory(DbContextOptions<ProvisioningDbContext> options)
        : IDbContextFactory<ProvisioningDbContext>
    {
        public ProvisioningDbContext CreateDbContext() => new(options);
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
            var name = "provisioning_lease_" + Guid.NewGuid().ToString("N");
            var admin = new SqlConnection(masterConnectionString);
            var database = new SqlServerTestDatabase(
                new SqlConnectionStringBuilder(masterConnectionString)
                {
                    InitialCatalog = name,
                    Pooling = false
                }.ConnectionString,
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
                    var catalog = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
                    await using var drop = new SqlCommand(
                        $"ALTER DATABASE [{catalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{catalog}];",
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
