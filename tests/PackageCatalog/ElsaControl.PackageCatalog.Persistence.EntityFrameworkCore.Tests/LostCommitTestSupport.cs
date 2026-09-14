using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

internal sealed class LostCommitAcknowledgementException : Exception;

internal sealed class LostCommitAcknowledgementInterceptor(int failures = 1) : DbTransactionInterceptor
{
    private int _remainingFailures = failures;
    private int _committed;

    public int Committed => Volatile.Read(ref _committed);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _committed);
        if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            throw new LostCommitAcknowledgementException();

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }
}
