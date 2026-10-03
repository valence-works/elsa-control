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

    public async Task<int> DispatchPendingAsync(
        int limit = 32,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        dbContext.ChangeTracker.Clear();
        var due = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .Where(row =>
                row.SentAt == null &&
                (row.NextAttemptAt == null || row.NextAttemptAt <= now))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var delivered = 0;
        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _sender.Send(new RecoveryRequiredAlertDispatch(
                    row.WorkspaceId,
                    row.InstanceId,
                    row.OperationId,
                    row.AttemptNumber));
                row.SentAt = now;
                row.DeliveryAttempts += 1;
                row.NextAttemptAt = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                dbContext.ChangeTracker.Clear();
                var retry = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == row.Id && candidate.SentAt == null,
                        cancellationToken);
                if (retry is null)
                    continue;
                retry.DeliveryAttempts += 1;
                retry.NextAttemptAt = now + RecoveryRequiredAlertBackoff.Delay(retry.DeliveryAttempts);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return delivered;
    }
}
