using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// The SQLite configuration every catalog persistence test context uses. Production SQL Server
/// runs with <c>EnableRetryOnFailure</c>, whose execution strategy rejects a user-initiated
/// transaction that is not wrapped by <c>CreateExecutionStrategy().ExecuteAsync(...)</c>.
/// SQLite's default strategy allows it, so tests install <see cref="TestRetryingExecutionStrategy"/>
/// to enforce the same rule.
/// </summary>
internal static class RetryingSqliteTestOptions
{
    public static DbContextOptionsBuilder<CatalogDbContext> UseRetryingSqlite(
        this DbContextOptionsBuilder<CatalogDbContext> builder,
        string connectionString,
        Action<SqliteDbContextOptionsBuilder>? configure = null) =>
        builder.UseSqlite(connectionString, sqlite => Configure(sqlite, configure));

    public static DbContextOptionsBuilder<CatalogDbContext> UseRetryingSqlite(
        this DbContextOptionsBuilder<CatalogDbContext> builder,
        DbConnection connection,
        Action<SqliteDbContextOptionsBuilder>? configure = null,
        Func<Exception, bool>? isTransient = null) =>
        builder.UseSqlite(connection, sqlite => Configure(sqlite, configure, isTransient));

    private static void Configure(
        SqliteDbContextOptionsBuilder sqlite,
        Action<SqliteDbContextOptionsBuilder>? configure,
        Func<Exception, bool>? isTransient = null)
    {
        sqlite.ExecutionStrategy(dependencies => new TestRetryingExecutionStrategy(dependencies, isTransient));
        configure?.Invoke(sqlite);
    }
}

/// <summary>
/// Reports <see cref="ExecutionStrategy.RetriesOnFailure"/> like SQL Server's retrying strategy, so
/// EF Core enforces its "no user-initiated transaction outside the strategy" rule. It retries only
/// the failures a test explicitly classifies as transient, and never waits between attempts.
/// </summary>
internal sealed class TestRetryingExecutionStrategy(
    ExecutionStrategyDependencies dependencies,
    Func<Exception, bool>? isTransient = null)
    : ExecutionStrategy(dependencies, MaxRetries, TimeSpan.Zero)
{
    public const int MaxRetries = 3;

    protected override bool ShouldRetryOn(Exception exception) => isTransient?.Invoke(exception) ?? false;
}
