using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    private const string SqlServerConnectionEnvironmentVariable = "CODEX_664_SQL_CONNECTION";

    [DisposableSqlServerFact]
    public async Task Sql_server_delete_does_not_supersede_a_create_with_submission_in_flight()
    {
        var masterConnectionString = GetLoopbackMasterConnectionString();
        await using var database = await SqlServerTestDatabase.CreateAsync(masterConnectionString);
        Guid workspaceId;
        Guid instanceId;
        Guid operationId;
        int attemptNumber;
        int instanceVersion;
        await using (var db = CreateSqlServerContext(database.ConnectionString))
        {
            await db.Database.MigrateAsync();
            var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "SQL in-flight delete");
            workspaceId = workspace.Id;
            instanceId = accepted.Instance.Id;
            operationId = accepted.Operation.Id;
            attemptNumber = accepted.Operation.AttemptNumber;
            instanceVersion = accepted.Instance.Version;
        }

        await using (var marker = CreateSqlServerContext(database.ConnectionString))
        {
            await CreateStore(marker).MarkProviderSubmissionInFlightAsync(new(
                workspaceId, instanceId, operationId, attemptNumber, Now));
        }

        await using var deleter = CreateSqlServerContext(database.ConnectionString);
        var current = Assert.IsType<ElsaInstance>(await CreateStore(deleter)
            .GetInstanceAsync(workspaceId, instanceId));
        Assert.Equal(instanceVersion, current.Version);
        var deletion = await new ElsaInstanceLifecycleService(
                CreateStore(deleter), new FixedTimeProvider(Now.AddMinutes(1)))
            .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                deleter, workspaceId, instanceId, current.Version,
                "delete-sql-inflight", Now.AddMinutes(1)));

        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, deletion.Operation.State);
        deleter.ChangeTracker.Clear();
        var predecessor = await deleter.ElsaInstanceOperations.AsNoTracking()
            .SingleAsync(x => x.Id == operationId);
        Assert.Equal(ElsaInstanceOperationState.Queued, predecessor.State);
        Assert.NotNull(predecessor.ProviderSubmissionInFlightAt);
        Assert.Null(predecessor.FailureCode);
        Assert.Null(await CreateStore(deleter).TryClaimNextDeletionAsync("deletion-worker", Now.AddMinutes(2)));
    }

    private static CatalogDbContext CreateSqlServerContext(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly);
                sqlServer.EnableRetryOnFailure();
            })
            .Options);

    private static string GetLoopbackMasterConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(SqlServerConnectionEnvironmentVariable);
        var builder = new SqlConnectionStringBuilder(value!);
        if (!IsLoopbackDataSource(builder.DataSource))
            throw new InvalidOperationException($"{SqlServerConnectionEnvironmentVariable} must point to a loopback SQL Server.");
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
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SqlServerConnectionEnvironmentVariable)))
            {
                Skip =
                    "Set CODEX_664_SQL_CONNECTION to a disposable loopback SQL Server. " +
                    "GitHub CI exports this for serializable range-lock coverage; without it " +
                    "this process cannot exercise SQL Server submission-in-flight isolation.";
            }
        }
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
            var name = "submission_inflight_" + Guid.NewGuid().ToString("N");
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
