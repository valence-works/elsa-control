using System.Data;
using System.Data.Common;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CatalogDbContextTransactionExtensionsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TransactionRecorder _transactions = new();
    private CatalogDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = CreateContext();
        await _db.Database.EnsureCreatedAsync();
        _transactions.Clear();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Test_contexts_reject_a_user_transaction_outside_the_execution_strategy()
    {
        // The rule SQL Server's EnableRetryOnFailure enforces in production. Every store test relies on
        // the test strategy enforcing it too; without this, the suite would pass vacuously.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Organizations.CountAsync());

        Assert.Contains("does not support user-initiated transactions", exception.Message);
    }

    [Fact]
    public async Task Commits_the_unit_and_returns_its_result()
    {
        var result = await _db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            _db.Organizations.Add(new Organization { Name = "Committed" });
            await _db.SaveChangesAsync();
            return "unit result";
        }, CancellationToken.None);

        Assert.Equal("unit result", result);
        Assert.Null(_db.Database.CurrentTransaction);
        Assert.Equal(["Committed"], await PersistedOrganizationNamesAsync());
        Assert.Equal(1, _transactions.Committed);
    }

    [Theory]
    [InlineData(IsolationLevel.Serializable)]
    [InlineData(IsolationLevel.Unspecified)]
    public async Task Begins_the_transaction_with_the_requested_isolation_level(IsolationLevel isolationLevel)
    {
        await _db.ExecuteInTransactionAsync(isolationLevel, () => Task.CompletedTask, CancellationToken.None);

        Assert.Equal([isolationLevel], _transactions.Started);
    }

    [Fact]
    public async Task Rolls_back_and_rethrows_when_the_unit_fails()
    {
        var failure = new InvalidOperationException("The unit failed after saving.");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                _db.Organizations.Add(new Organization { Name = "Rolled back" });
                await _db.SaveChangesAsync();
                throw failure;
            }, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.Null(_db.Database.CurrentTransaction);
        Assert.Empty(_db.ChangeTracker.Entries());
        Assert.Empty(await PersistedOrganizationNamesAsync());
        Assert.Equal(0, _transactions.Committed);
    }

    [Fact]
    public async Task Transient_failure_reruns_the_whole_unit_from_a_cleared_change_tracker()
    {
        await using var db = CreateContext(isTransient: exception => exception is TransientTestException);
        var attempts = 0;
        var trackedAtAttemptStart = new List<int>();

        var committed = await db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            trackedAtAttemptStart.Add(db.ChangeTracker.Entries().Count());
            attempts++;
            var organization = new Organization { Name = $"Attempt {attempts}" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
            if (attempts == 1)
            {
                // The saved row must roll back with the attempt, and the pending one must not be
                // saved by the next attempt.
                db.Organizations.Add(new Organization { Name = "Pending from attempt 1" });
                throw new TransientTestException();
            }

            return organization.Name;
        }, CancellationToken.None);

        Assert.Equal("Attempt 2", committed);
        Assert.Equal([0, 0], trackedAtAttemptStart);
        Assert.Equal(["Attempt 2"], await PersistedOrganizationNamesAsync());
        Assert.Equal(2, _transactions.Started.Count);
        Assert.Equal(1, _transactions.Committed);
    }

    [Fact]
    public async Task Exhausted_transient_retries_fail_loudly_and_commit_nothing()
    {
        await using var db = CreateContext(isTransient: exception => exception is TransientTestException);
        var attempts = 0;

        var exception = await Assert.ThrowsAsync<RetryLimitExceededException>(() =>
            db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                attempts++;
                db.Organizations.Add(new Organization { Name = $"Attempt {attempts}" });
                await db.SaveChangesAsync();
                throw new TransientTestException();
            }, CancellationToken.None));

        Assert.IsType<TransientTestException>(exception.InnerException);
        Assert.Equal(TestRetryingExecutionStrategy.MaxRetries + 1, attempts);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await PersistedOrganizationNamesAsync());
        Assert.Equal(0, _transactions.Committed);
    }

    [Fact]
    public async Task A_cancelled_token_stops_before_the_unit_runs()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _db.ExecuteInTransactionAsync(IsolationLevel.Serializable, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.False(ran);
        Assert.Empty(_transactions.Started);
    }

    [Fact]
    public async Task Cancellation_during_the_unit_propagates_without_a_retry_or_a_commit()
    {
        // Even a strategy that treats every failure as transient must not turn cancellation into a
        // retry or into a result.
        await using var db = CreateContext(isTransient: _ => true);
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                attempts++;
                db.Organizations.Add(new Organization { Name = "Cancelled" });
                await db.SaveChangesAsync(cancellation.Token);
                await cancellation.CancelAsync();
                cancellation.Token.ThrowIfCancellationRequested();
            }, cancellation.Token));

        Assert.Equal(1, attempts);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await PersistedOrganizationNamesAsync());
        Assert.Equal(0, _transactions.Committed);
    }

    [Fact]
    public async Task Refuses_to_start_instead_of_discarding_unsaved_changes()
    {
        var pending = new Organization { Name = "Unsaved" };
        _db.Organizations.Add(pending);
        var ran = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _db.ExecuteInTransactionAsync(IsolationLevel.Serializable, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Contains("unsaved changes", exception.Message);
        Assert.False(ran);
        Assert.Equal(EntityState.Added, _db.Entry(pending).State);
        Assert.Empty(_transactions.Started);
    }

    private CatalogDbContext CreateContext(Func<Exception, bool>? isTransient = null) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection, isTransient: isTransient)
            .AddInterceptors(_transactions)
            .Options);

    private async Task<string[]> PersistedOrganizationNamesAsync()
    {
        await using var reader = CreateContext();
        return await reader.Organizations.AsNoTracking().Select(x => x.Name).OrderBy(x => x).ToArrayAsync();
    }

    private sealed class TransientTestException : Exception;

    private sealed class TransactionRecorder : DbTransactionInterceptor
    {
        public List<IsolationLevel> Started { get; } = [];
        public int Committed { get; private set; }

        public void Clear()
        {
            Started.Clear();
            Committed = 0;
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Started.Add(eventData.IsolationLevel);
            return base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Committed++;
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}
