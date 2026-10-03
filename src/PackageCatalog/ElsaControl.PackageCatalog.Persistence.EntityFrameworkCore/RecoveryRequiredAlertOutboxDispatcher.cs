using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class EfCoreRecoveryRequiredAlertOutboxDispatcher(
    CatalogDbContext dbContext,
    IRecoveryRequiredAlertSender sender,
    TimeProvider? timeProvider = null) : IRecoveryRequiredAlertOutboxDispatcher
{
    private readonly IRecoveryRequiredAlertSender _sender =
        sender ?? throw new ArgumentNullException(nameof(sender));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public async Task<int> DispatchPendingAsync(
        int limit = 32,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var leaseUntil = now + RecoveryRequiredAlertBackoff.Lease;
        dbContext.ChangeTracker.Clear();
        var dueIds = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .AsNoTracking()
            .Where(row =>
                row.SentAt == null &&
                (row.NextAttemptAt == null || row.NextAttemptAt <= now) &&
                (row.LeasedUntil == null || row.LeasedUntil < now))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var delivered = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claimed = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
                .Where(row =>
                    row.Id == id &&
                    row.SentAt == null &&
                    (row.NextAttemptAt == null || row.NextAttemptAt <= now) &&
                    (row.LeasedUntil == null || row.LeasedUntil < now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.LeasedUntil, leaseUntil)
                        .SetProperty(row => row.LeasedBy, _owner),
                    cancellationToken);
            if (claimed != 1)
                continue;

            dbContext.ChangeTracker.Clear();
            var row = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == id &&
                                 candidate.SentAt == null &&
                                 candidate.LeasedBy == _owner,
                    cancellationToken);
            if (row is null)
                continue;

            try
            {
                var acked = _sender.Send(new RecoveryRequiredAlertDispatch(
                    row.WorkspaceId,
                    row.InstanceId,
                    row.OperationId,
                    row.AttemptNumber,
                    row.RunId,
                    row.DedupeIdentity));
                if (!acked)
                {
                    await RecordDeliveryFailureAsync(id, now, cancellationToken);
                    continue;
                }

                row.SentAt = now;
                row.DeliveryAttempts += 1;
                row.NextAttemptAt = null;
                row.LeasedUntil = null;
                row.LeasedBy = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await RecordDeliveryFailureAsync(id, now, cancellationToken);
            }
        }

        return delivered;
    }

    private async Task RecordDeliveryFailureAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var retry = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .SingleOrDefaultAsync(
                candidate => candidate.Id == id && candidate.SentAt == null,
                cancellationToken);
        if (retry is null)
            return;
        retry.DeliveryAttempts += 1;
        retry.NextAttemptAt = now + RecoveryRequiredAlertBackoff.Delay(retry.DeliveryAttempts);
        retry.LeasedUntil = null;
        retry.LeasedBy = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
