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

    public static async Task<TResult> ExecuteInTransactionAsync<TResult>(
        this CatalogDbContext dbContext,
        IsolationLevel isolationLevel,
        Func<Task<TResult>> unitOfWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        cancellationToken.ThrowIfCancellationRequested();

        // Each attempt starts by clearing the change tracker. Refuse rather than silently discard
        // changes a caller staged but has not saved: they must be saved first or belong to the unit.
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
