using System.Reflection;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.SqlServerMigrations.Migrations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ManagedStudioGrantCapabilityMigrationTests
{
    private const string PreviousSqlServerMigration = "20260928023001_AddAzureProviderOperationStatusChangedAt";
    private const string SqlServerMigration = "20260928040001_AddManagedStudioGrantCapability";
    private const string ConnectionEnvironmentVariable = "CODEX_664_SQL_CONNECTION";

    [DisposableSqlServerTheory]
    [InlineData("fresh")]
    [InlineData("legacy")]
    [InlineData("expanded")]
    [InlineData("wrong-bit")]
    [InlineData("wrong-nullability")]
    [InlineData("wrong-default")]
    [InlineData("wrong-binding")]
    [InlineData("wrong-binding-default")]
    [InlineData("wrong-handoff")]
    public async Task Sql_server_migration_validates_and_preserves_actual_schema(string scenario)
    {
        var masterConnectionString = GetLoopbackMasterConnectionString();
        using var db = CreateSqlServerContext(masterConnectionString);
        var migration = db.GetService<IMigrationsAssembly>().CreateMigration(
            typeof(AddManagedStudioGrantCapability).GetTypeInfo(),
            "Microsoft.EntityFrameworkCore.SqlServer");
        var upCommands = db.GetService<IMigrationsSqlGenerator>()
            .Generate(migration.UpOperations)
            .Select(command => command.CommandText)
            .ToArray();

        await using var database = await SqlServerMigrationDatabase.CreateAsync(masterConnectionString);
        await database.CreateBaseTablesAsync();
        await ArrangeScenarioAsync(database.Connection, scenario, db.GetService<IMigrationsSqlGenerator>());

        if (scenario.StartsWith("wrong-", StringComparison.Ordinal))
        {
            var before = await ReadColumnCountAsync(database.Connection);
            var error = await Assert.ThrowsAsync<SqlException>(() => database.ApplyMigrationAsync(upCommands));

            Assert.Equal(51000, error.Number);
            Assert.Equal(before, await ReadColumnCountAsync(database.Connection));
            return;
        }

        await database.ApplyMigrationAsync(upCommands);
        await database.ApplyMigrationAsync(upCommands);

        Assert.Equal(3, await ReadGrantColumnCountAsync(database.Connection));

        var grants = await ReadGrantValuesAsync(database.Connection);
        if (scenario == "fresh")
        {
            Assert.False(grants.StudioGrants);
            Assert.False(grants.ManagedHandoffStudioGrants);
            Assert.Null(grants.DeploymentId);
        }
        else
        {
            Assert.True(grants.StudioGrants);
            Assert.True(grants.ManagedHandoffStudioGrants);
            Assert.Equal(scenario == "expanded" ? "preserve" : null, grants.DeploymentId);

            var falseLegacyRow = await ReadGrantValuesAsync(database.Connection, id: 2);
            Assert.False(falseLegacyRow.StudioGrants);
            Assert.False(falseLegacyRow.ManagedHandoffStudioGrants);
            Assert.Null(falseLegacyRow.DeploymentId);
        }
    }

    [Fact]
    public void Sql_server_down_script_preserves_compatibility_columns_for_legacy_rows()
    {
        using var db = CreateSqlServerContext(
            "Server=127.0.0.1,1;Database=unused;User ID=unused;Password=unused;Encrypt=False");

        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: SqlServerMigration,
            toMigration: PreviousSqlServerMigration);

        Assert.DoesNotMatch(@"\bDROP\s+COLUMN\b", script);
        Assert.DoesNotMatch(@"\bDELETE\s+FROM\s+\[?(ElsaInstances|AzureProviderOperations)\]?", script);
    }

    private static async Task ArrangeScenarioAsync(
        SqlConnection connection,
        string scenario,
        IMigrationsSqlGenerator migrationsSqlGenerator)
    {
        if (scenario is "legacy" or "expanded")
        {
            // Generate the legacy bool columns exactly as the prior EF migration did.
            var legacy = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            legacy.AddColumn<bool>(
                name: "CurrentDeploymentStudioGrants",
                table: "ElsaInstances",
                type: "bit",
                nullable: false,
                defaultValue: false);
            legacy.AddColumn<bool>(
                name: "ManagedHandoffStudioGrants",
                table: "AzureProviderOperations",
                type: "bit",
                nullable: false,
                defaultValue: false);

            await ExecuteCommandsAsync(
                connection,
                migrationsSqlGenerator.Generate(legacy.Operations).Select(command => command.CommandText));
        }
        else if (scenario.StartsWith("wrong-", StringComparison.Ordinal))
        {
            var instanceType = scenario == "wrong-bit" ? "int" : "bit";
            var instanceNullability = scenario == "wrong-nullability" ? "NULL" : "NOT NULL";
            var instanceDefault = scenario == "wrong-default" ? 1 : 0;
            var handoffType = scenario == "wrong-handoff" ? "int" : "bit";
            await ExecuteAsync(connection, $"""
                ALTER TABLE dbo.ElsaInstances
                    ADD CurrentDeploymentStudioGrants {instanceType} {instanceNullability} DEFAULT {instanceDefault};
                ALTER TABLE dbo.AzureProviderOperations
                    ADD ManagedHandoffStudioGrants {handoffType} NOT NULL DEFAULT 0;
                """);
        }

        var rowIds = scenario is "legacy" or "expanded" ? new[] { 1, 2 } : new[] { 1 };
        foreach (var id in rowIds)
            await ExecuteAsync(connection, $"INSERT dbo.ElsaInstances (Id) VALUES ({id}); INSERT dbo.AzureProviderOperations (Id) VALUES ({id});");

        if (scenario is "legacy" or "expanded")
        {
            await ExecuteAsync(connection, "UPDATE dbo.ElsaInstances SET CurrentDeploymentStudioGrants = 1 WHERE Id = 1; UPDATE dbo.AzureProviderOperations SET ManagedHandoffStudioGrants = 1 WHERE Id = 1;");
        }

        if (scenario is "expanded" or "wrong-binding" or "wrong-binding-default")
        {
            var length = scenario == "wrong-binding" ? 64 : 128;
            var defaultClause = scenario == "wrong-binding-default" ? "DEFAULT N'unknown'" : "";
            await ExecuteAsync(connection, $"ALTER TABLE dbo.ElsaInstances ADD CurrentDeploymentStudioGrantsDeploymentId nvarchar({length}) NULL {defaultClause};");
            if (scenario == "expanded")
                await ExecuteAsync(connection, "UPDATE dbo.ElsaInstances SET CurrentDeploymentStudioGrantsDeploymentId = N'preserve' WHERE Id = 1;");
        }
    }

    private static async Task ExecuteCommandsAsync(SqlConnection connection, IEnumerable<string> commands)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (var commandText in commands)
                await ExecuteAsync(connection, commandText, transaction);

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string commandText, SqlTransaction? transaction = null)
    {
        await using var command = new SqlCommand(commandText, connection, transaction) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadColumnCountAsync(SqlConnection connection) =>
        Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id IN (OBJECT_ID(N'dbo.ElsaInstances'), OBJECT_ID(N'dbo.AzureProviderOperations'))
            """));

    private static async Task<int> ReadGrantColumnCountAsync(SqlConnection connection) =>
        Convert.ToInt32(await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE (object_id = OBJECT_ID(N'dbo.ElsaInstances')
                   AND name IN (N'CurrentDeploymentStudioGrants', N'CurrentDeploymentStudioGrantsDeploymentId'))
               OR (object_id = OBJECT_ID(N'dbo.AzureProviderOperations')
                   AND name = N'ManagedHandoffStudioGrants')
            """));

    private static async Task<(bool StudioGrants, bool ManagedHandoffStudioGrants, string? DeploymentId)> ReadGrantValuesAsync(SqlConnection connection, int id = 1)
    {
        await using var command = new SqlCommand("""
            SELECT i.CurrentDeploymentStudioGrants, o.ManagedHandoffStudioGrants,
                   i.CurrentDeploymentStudioGrantsDeploymentId
            FROM dbo.ElsaInstances AS i
            CROSS JOIN dbo.AzureProviderOperations AS o
            WHERE i.Id = @id AND o.Id = @id
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string commandText)
    {
        await using var command = new SqlCommand(commandText, connection);
        return await command.ExecuteScalarAsync();
    }

    private static CatalogDbContext CreateSqlServerContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                connectionString,
                sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private static string GetLoopbackMasterConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Assert.False(string.IsNullOrWhiteSpace(value), $"{ConnectionEnvironmentVariable} must be set for the opted-in SQL Server migration tests.");

        var builder = new SqlConnectionStringBuilder(value!);
        Assert.True(IsLoopbackDataSource(builder.DataSource),
            $"{ConnectionEnvironmentVariable} must point to a loopback SQL Server; database creation was refused.");
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
        var portText = separator < 0 ? null : source[(separator + 1)..];
        if (portText is not null &&
            (portText.Length == 0 || portText.Any(character => character is < '0' or > '9') ||
             !int.TryParse(portText, out var port) || port is < 1 or > 65535))
        {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "[::1]";
    }

    private sealed class DisposableSqlServerTheoryAttribute : TheoryAttribute
    {
        public DisposableSqlServerTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
                Skip = $"Set {ConnectionEnvironmentVariable} to a disposable loopback SQL Server to run these actual-migration cases.";
        }
    }

    private sealed class SqlServerMigrationDatabase : IAsyncDisposable
    {
        private readonly SqlConnection _adminConnection;
        private bool _created;

        private SqlServerMigrationDatabase(string name, SqlConnection adminConnection, SqlConnection connection)
        {
            Name = name;
            _adminConnection = adminConnection;
            Connection = connection;
        }

        public string Name { get; }
        public SqlConnection Connection { get; }

        public static async Task<SqlServerMigrationDatabase> CreateAsync(string masterConnectionString)
        {
            var admin = new SqlConnection(masterConnectionString);
            var name = "codex664_" + Guid.NewGuid().ToString("N");
            var database = new SqlServerMigrationDatabase(name, admin, new SqlConnection(
                new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = name }.ConnectionString));

            try
            {
                await admin.OpenAsync();
                await ExecuteAsync(admin, $"CREATE DATABASE [{name}]");
                database._created = true;
                await database.Connection.OpenAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task CreateBaseTablesAsync() =>
            await ExecuteAsync(Connection, "CREATE TABLE dbo.ElsaInstances (Id int NOT NULL); CREATE TABLE dbo.AzureProviderOperations (Id int NOT NULL);");

        public Task ApplyMigrationAsync(IEnumerable<string> commands) => ExecuteCommandsAsync(Connection, commands);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync();
                if (_created)
                {
                    await ExecuteAsync(_adminConnection, $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]");
                    _created = false;
                }
            }
            finally
            {
                await _adminConnection.DisposeAsync();
            }
        }
    }
}
