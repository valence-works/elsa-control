using System.Data;
using Microsoft.EntityFrameworkCore;

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
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        cancellationToken.ThrowIfCancellationRequested();

        // Refuses to start over unsaved changes; see the method's <exception> doc for why.
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException(
                "A catalog transaction cannot start while the context has unsaved changes; save them first or make them part of the unit of work.");

        try
        {
            return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
                (dbContext, isolationLevel, unitOfWork),
                static async (_, state, attemptCancellationToken) =>
                {
                    state.dbContext.ChangeTracker.Clear();
                    await using var transaction = await state.dbContext.Database.BeginTransactionAsync(
                        state.isolationLevel, attemptCancellationToken);
                    var result = await state.unitOfWork();
                    await transaction.CommitAsync(attemptCancellationToken);
                    return result;
                },
                verifySucceeded: null,
                cancellationToken);
        }
        catch
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }
}
