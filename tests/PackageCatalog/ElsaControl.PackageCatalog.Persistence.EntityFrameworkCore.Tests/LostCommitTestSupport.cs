using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

internal sealed class LostCommitAcknowledgementException : Exception;

internal sealed class LostCommitAcknowledgementInterceptor(int failures = 1) : DbTransactionInterceptor
{
    private int _remainingFailures = failures;

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            throw new LostCommitAcknowledgementException();

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }
}
