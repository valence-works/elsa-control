using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class EfCoreRecoveryRequiredAlertOutboxDispatcher(
    CatalogDbContext dbContext,
    IRecoveryRequiredAlertSender sender,
    TimeProvider? timeProvider = null,
    ILogger<EfCoreRecoveryRequiredAlertOutboxDispatcher>? logger = null) : IRecoveryRequiredAlertOutboxDispatcher
{
    private readonly IRecoveryRequiredAlertSender _sender =
        sender ?? throw new ArgumentNullException(nameof(sender));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<EfCoreRecoveryRequiredAlertOutboxDispatcher>.Instance;
    private readonly string _owner = Guid.NewGuid().ToString("N");

    public async Task<int> DispatchPendingAsync(
        int limit = 32,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        dbContext.ChangeTracker.Clear();
        var scanNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var dueIds = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .AsNoTracking()
            .Where(row =>
                row.SentAt == null &&
                (row.NextAttemptAt == null || row.NextAttemptAt <= scanNow) &&
                (row.LeasedUntil == null || row.LeasedUntil < scanNow))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .Select(row => row.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var delivered = 0;
        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var leaseUntil = now + RecoveryRequiredAlertBackoff.LeaseDuration;
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
                .AsNoTracking()
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

                if (!await TryMarkSentAsync(id, cancellationToken))
                    continue;
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

    private async Task<bool> TryMarkSentAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        dbContext.ChangeTracker.Clear();
        var marked = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .Where(row =>
                row.Id == id &&
                row.SentAt == null &&
                row.LeasedBy == _owner &&
                row.LeasedUntil != null &&
                row.LeasedUntil >= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.SentAt, now)
                    .SetProperty(row => row.DeliveryAttempts, row => row.DeliveryAttempts + 1)
                    .SetProperty(row => row.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(row => row.LeasedUntil, (DateTimeOffset?)null)
                    .SetProperty(row => row.LeasedBy, (string?)null),
                cancellationToken);
        if (marked == 1)
            return true;

        _logger.LogWarning(
            "RecoveryRequired alert {AlertId} was not marked sent because the lease owner or expiry did not match.",
            id);
        return false;
    }

    private async Task RecordDeliveryFailureAsync(
        Guid id,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var snapshot = await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .AsNoTracking()
            .Where(candidate =>
                candidate.Id == id &&
                candidate.SentAt == null &&
                candidate.LeasedBy == _owner)
            .Select(candidate => new { candidate.DeliveryAttempts })
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null)
            return;

        var attempts = snapshot.DeliveryAttempts + 1;
        var nextAttemptAt = now + RecoveryRequiredAlertBackoff.Delay(attempts);
        await dbContext.ElsaInstanceRecoveryRequiredAlertOutbox
            .Where(row =>
                row.Id == id &&
                row.SentAt == null &&
                row.LeasedBy == _owner)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.DeliveryAttempts, attempts)
                    .SetProperty(row => row.NextAttemptAt, nextAttemptAt)
                    .SetProperty(row => row.LeasedUntil, (DateTimeOffset?)null)
                    .SetProperty(row => row.LeasedBy, (string?)null),
                cancellationToken);
    }
}
