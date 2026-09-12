using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// The catalog migrations install guard triggers that EF Core does not manage. EF must know about
/// every one of them: on SQL Server an undeclared trigger makes EF emit <c>OUTPUT</c> without
/// <c>INTO</c>, which SQL Server rejects with error 334 for any table that has enabled triggers.
/// </summary>
public sealed partial class CatalogTableTriggerDeclarationTests
{
    private const string UnusedSqlServerConnectionString =
        "Server=tcp:127.0.0.1,1;Initial Catalog=ElsaControlTriggerDeclarationTests;User ID=unused;Password=unused;Encrypt=False";

    [Fact]
    public void Sql_server_model_declares_exactly_the_triggers_its_migrations_create()
    {
        using var db = CreateSqlServerContext();
        var script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Equal(Describe(TriggersCreatedBy(script)), Describe(DeclaredTriggers(db.Model)));
        Assert.False(db.Database.HasPendingModelChanges(), "The SQL Server model snapshot is out of date.");
    }

    [Fact]
    public async Task Sqlite_model_declares_exactly_the_triggers_its_migrations_create()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
        await db.Database.MigrateAsync();

        var created = await db.Database
            .SqlQueryRaw<SqliteTrigger>("SELECT tbl_name AS \"Table\", name AS \"Name\" FROM sqlite_master WHERE type = 'trigger'")
            .ToListAsync();

        Assert.Equal(Describe(created.Select(x => (x.Table, x.Name))), Describe(DeclaredTriggers(db.Model)));
    }

    // Tables whose rows pass the context's save guards without domain setup; the parity tests
    // above cover the declaration for every trigger-guarded table.
    [Theory]
    [InlineData("DeploymentRuns")]
    [InlineData("DeploymentEnvironments")]
    public async Task Sql_server_updates_on_trigger_guarded_tables_do_not_use_a_bare_output_clause(string table)
    {
        var capture = new CommandCapture();
        await using var db = CreateSqlServerContext(capture);
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        var entityType = db.Model.GetEntityTypes().Single(x => x.GetTableName() == table && x.BaseType is null);
        var entity = RuntimeHelpers.GetUninitializedObject(entityType.ClrType);
        var entry = db.Entry(entity);
        foreach (var key in entityType.FindPrimaryKey()!.Properties)
            entry.Property(key.Name).CurrentValue = SampleKeyValue(key.ClrType);
        foreach (var ownership in entityType.GetProperties().Where(x => x.ClrType == typeof(Guid) && !x.IsKey() && !x.IsShadowProperty()))
            entry.Property(ownership.Name).CurrentValue = Guid.NewGuid();
        entry.State = EntityState.Unchanged;
        var updated = entityType.GetProperties().First(x => !x.IsKey() && !x.IsConcurrencyToken && !x.IsShadowProperty() && x.ValueGenerated == ValueGenerated.Never);
        entry.Property(updated.Name).IsModified = true;

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<CommandCapturedException>(failure.InnerException);

        Assert.Contains($"UPDATE [{table}]", capture.CommandText, StringComparison.Ordinal);
        Assert.DoesNotMatch(BareOutputClause(), capture.CommandText);
    }

    private static CatalogDbContext CreateSqlServerContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(UnusedSqlServerConnectionString, sqlServer => sqlServer.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .AddInterceptors(interceptors)
            .Options);

    private static IEnumerable<(string Table, string Name)> TriggersCreatedBy(string script)
    {
        var triggers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in TriggerDdl().Matches(script))
        {
            if (match.Groups["created"].Success)
                triggers[match.Groups["created"].Value] = match.Groups["table"].Value;
            else
                triggers.Remove(match.Groups["dropped"].Value);
        }

        return triggers.Select(x => (x.Value, x.Key));
    }

    private static IEnumerable<(string Table, string Name)> DeclaredTriggers(IModel model) =>
        model.GetEntityTypes()
            .Where(x => x.GetTableName() is not null)
            .SelectMany(x => x.GetDeclaredTriggers().Select(trigger => (x.GetTableName()!, trigger.GetDatabaseName()!)));

    private static string[] Describe(IEnumerable<(string Table, string Name)> triggers) =>
        triggers.Select(x => $"{x.Table}:{x.Name}").Distinct().Order(StringComparer.Ordinal).ToArray();

    private static object SampleKeyValue(Type type) => (Nullable.GetUnderlyingType(type) ?? type) switch
    {
        var t when t == typeof(Guid) => Guid.NewGuid(),
        var t when t == typeof(string) => "trigger-declaration",
        var t when t == typeof(long) => 1L,
        var t when t == typeof(int) => 1,
        var t => throw new NotSupportedException($"No sample key value for {t}."),
    };

    [GeneratedRegex(@"\bOUTPUT\b(?![^;]*\bINTO\b)", RegexOptions.IgnoreCase)]
    private static partial Regex BareOutputClause();

    [GeneratedRegex(
        @"CREATE\s+(?:OR\s+ALTER\s+)?TRIGGER\s+(?:\[?dbo\]?\.)?\[?(?<created>\w+)\]?\s+ON\s+(?:\[?dbo\]?\.)?\[?(?<table>\w+)\]?|DROP\s+TRIGGER\s+(?:IF\s+EXISTS\s+)?(?:\[?dbo\]?\.)?\[?(?<dropped>\w+)\]?",
        RegexOptions.IgnoreCase)]
    private static partial Regex TriggerDdl();

    private sealed record SqliteTrigger(string Table, string Name);

    private sealed class CommandCapturedException : Exception;

    /// <summary>
    /// Keeps SaveChanges away from any server: validation queries read no rows, and the first
    /// write command is recorded and aborted before it would execute.
    /// </summary>
    private sealed class CommandCapture : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public string CommandText { get; private set; } = "";

        public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) =>
            Intercept(command);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Intercept(command));

        private InterceptionResult<DbDataReader> Intercept(DbCommand command)
        {
            if (!WriteCommand().IsMatch(command.CommandText))
                return InterceptionResult<DbDataReader>.SuppressWithResult(new System.Data.DataTable().CreateDataReader());

            CommandText = command.CommandText;
            throw new CommandCapturedException();
        }
    }

    [GeneratedRegex(@"^\s*(?:SET\s+[^;]+;\s*)*(?:UPDATE|INSERT|DELETE|MERGE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WriteCommand();
}
