using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// The only way catalog persistence opens an explicit transaction.
/// </summary>
/// <remarks>
/// SQL Server runs the catalog with <c>EnableRetryOnFailure</c>. Its execution strategy rejects a
/// user-initiated transaction unless the whole unit of work runs inside
/// <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c>, and after a transient failure it runs that
/// whole unit again. Every attempt therefore starts from an empty change tracker and a new transaction,
/// re-reads the state it decides on, and commits only when the unit returns. An exception rolls the attempt
/// back; once the strategy stops retrying, the change tracker is cleared so no entity from a failed attempt
/// can be saved later. A unit returns its outcome; anything it records outside itself must be
/// re-initialised by the unit, because a failed attempt must leave nothing behind for the next one.
/// </remarks>
internal static class CatalogDbContextTransactionExtensions
{
    /// <summary>
    /// Runs <paramref name="unitOfWork"/> once inside a new transaction at <paramref name="isolationLevel"/>,
    /// under the catalog's execution strategy. This is a thin wrapper over the generic overload, which
    /// documents the full contract: each retry attempt clears the change tracker before opening its own
    /// transaction, the transaction commits only once the unit returns successfully, and the call refuses
    /// to start over unsaved changes rather than discard them.
    /// </summary>
    /// <param name="dbContext">The catalog context the transaction runs on.</param>
    /// <param name="isolationLevel">The isolation level for every attempt's transaction.</param>
    /// <param name="unitOfWork">The unit of work to run and, on success, commit.</param>
    /// <param name="cancellationToken">
    /// Observed before the unit starts and during the unit; cancellation never triggers a retry or a commit.
    /// </param>
    /// <returns>A task that completes once the unit's transaction has committed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="unitOfWork"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="dbContext"/> already has unsaved changes when the transaction is about to start.
    /// </exception>
    public static Task ExecuteInTransactionAsync(
        this CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task> unitOfWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return dbContext.ExecuteInTransactionAsync(isolationLevel, async () =>
        {
            await unitOfWork();
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="unitOfWork"/> inside a transaction and uses
    /// <paramref name="verifySucceeded"/> to distinguish a committed transaction from a rolled-back
    /// attempt when the commit acknowledgement is lost.
    /// </summary>
    /// <remarks>
    /// The verifier runs only after a transient failure and outside the failed attempt's transaction.
    /// It must query durable state using an operation-specific identity. Returning <see langword="true"/>
    /// returns the result produced by the committed attempt without executing the unit again.
    /// </remarks>
    public static Task ExecuteInTransactionAsync(
        this CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task> unitOfWork,
        Func<CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return dbContext.ExecuteInTransactionAsync(isolationLevel, async () =>
        {
            await unitOfWork();
            return true;
        }, (_, attemptCancellationToken) => verifySucceeded(attemptCancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="unitOfWork"/> inside a transaction at <paramref name="isolationLevel"/>, under
    /// the catalog's execution strategy, which re-runs the whole unit on a transient failure. Each attempt
    /// clears the change tracker before opening its own transaction, so it starts from a clean slate and
    /// re-reads whatever state it decides on; the transaction commits only after the unit returns
    /// successfully, and a thrown exception rolls that attempt's transaction back. Once the strategy stops
    /// retrying, the change tracker is cleared once more so no entity staged by a failed attempt can be
    /// saved by a later, unrelated operation. Because a caller's unsaved changes would otherwise be
    /// silently discarded by the first attempt's change-tracker clear, the call refuses to start while
    /// <paramref name="dbContext"/> already has them: they must be saved first or made part of the unit of
    /// work.
    /// </summary>
    /// <typeparam name="TResult">The type the unit of work returns.</typeparam>
    /// <param name="dbContext">The catalog context the transaction runs on.</param>
    /// <param name="isolationLevel">The isolation level for every attempt's transaction.</param>
    /// <param name="unitOfWork">
    /// The unit of work to run and, on success, commit. It may run more than once if the strategy retries.
    /// </param>
    /// <param name="cancellationToken">
    /// Observed before the unit starts and during an attempt; cancellation never triggers a retry or a commit.
    /// </param>
    /// <returns>The unit of work's result once its transaction has committed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dbContext"/> or <paramref name="unitOfWork"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="dbContext"/> already has unsaved changes when the transaction is about to start.
    /// </exception>
    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        this CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task<TResult>> unitOfWork,
        CancellationToken cancellationToken)
    {
        return await ExecuteInTransactionCoreAsync(
            dbContext,
            isolationLevel, unitOfWork, verifySucceeded: null, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="unitOfWork"/> inside a transaction and uses
    /// <paramref name="verifySucceeded"/> to distinguish a committed transaction from a rolled-back
    /// attempt when the commit acknowledgement is lost.
    /// </summary>
    /// <remarks>
    /// The verifier runs only after a transient failure and outside the failed attempt's transaction.
    /// It must query durable state using an operation-specific identity. Returning <see langword="true"/>
    /// returns the result produced by the committed attempt without executing the unit again.
    /// </remarks>
    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        this CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task<TResult>> unitOfWork,
        Func<TResult, CancellationToken, Task<bool>> verifySucceeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifySucceeded);
        return await ExecuteInTransactionCoreAsync(
            dbContext,
            isolationLevel, unitOfWork, verifySucceeded, cancellationToken);
    }

    private static async Task<TResult> ExecuteInTransactionCoreAsync<TResult>(
        CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task<TResult>> unitOfWork,
        Func<TResult, CancellationToken, Task<bool>>? verifySucceeded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        cancellationToken.ThrowIfCancellationRequested();

        // Refuses to start over unsaved changes; see the method's <exception> doc for why.
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException(
                "A catalog transaction cannot start while the context has unsaved changes; save them first or make them part of the unit of work.");

        try
        {
            var executionState = new TransactionExecutionState<TResult>(
                dbContext, isolationLevel, unitOfWork, verifySucceeded);
            return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
                executionState,
                static async (_, state, attemptCancellationToken) =>
                {
                    state.DbContext.ChangeTracker.Clear();
                    state.HasResult = false;
                    await using var transaction = await state.DbContext.Database.BeginTransactionAsync(
                        state.IsolationLevel, attemptCancellationToken);
                    var result = await state.UnitOfWork();
                    state.Result = result;
                    state.HasResult = true;
                    await transaction.CommitAsync(attemptCancellationToken);
                    return result;
                },
                verifySucceeded is null
                    ? null
                    : static async (_, state, attemptCancellationToken) =>
                    {
                        state.DbContext.ChangeTracker.Clear();
                        if (!state.HasResult)
                            return new ExecutionResult<TResult>(successful: false, result: default!);

                        if (!await state.VerifySucceeded!(state.Result!, attemptCancellationToken))
                            return new ExecutionResult<TResult>(successful: false, result: default!);

                        return new ExecutionResult<TResult>(successful: true, state.Result!);
                    },
                cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private sealed class TransactionExecutionState<TResult>(
        CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task<TResult>> unitOfWork,
        Func<TResult, CancellationToken, Task<bool>>? verifySucceeded)
    {
        public CatalogDbContext DbContext { get; } = dbContext;
        public IsolationLevel IsolationLevel { get; } = isolationLevel;
        public Func<Task<TResult>> UnitOfWork { get; } = unitOfWork;
        public Func<TResult, CancellationToken, Task<bool>>? VerifySucceeded { get; } = verifySucceeded;
        public bool HasResult { get; set; }
        public TResult? Result { get; set; }
    }
}
