using System.Linq.Expressions;
using ElsaControl.Deployment.Core.Telemetry;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Shared RecoveryRequired clock-scan selection. The EF and in-memory stores
/// must use the same due predicate, ordering, candidate cap, and failure log.
/// </summary>
public static class RecoveryRequiredHumanClockScan
{
    public const string FailureLogMessage =
        "Managed Elsa RecoveryRequired clock scan failed for operation {OperationId}.";

    public static DateTimeOffset DueBefore(DateTimeOffset now) =>
        now.ToUniversalTime() - ManagedElsaReasonCodeCatalog.HumanRequiredAfter;

    public static bool IsDueCandidate(
        DateTimeOffset? requiresHumanAt,
        DateTimeOffset? reasonEnteredAt,
        DateTimeOffset now)
    {
        if (requiresHumanAt is not null)
            return false;
        return reasonEnteredAt is null || reasonEnteredAt <= DueBefore(now);
    }

    public static IEnumerable<T> SelectCandidates<T>(
        IEnumerable<T> source,
        Func<T, DateTimeOffset?> requiresHumanAt,
        Func<T, DateTimeOffset?> reasonEnteredAt,
        Func<T, Guid> id,
        DateTimeOffset now,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requiresHumanAt);
        ArgumentNullException.ThrowIfNull(reasonEnteredAt);
        ArgumentNullException.ThrowIfNull(id);
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        now = now.ToUniversalTime();
        return source
            .Where(item => IsDueCandidate(requiresHumanAt(item), reasonEnteredAt(item), now))
            .OrderBy(item => reasonEnteredAt(item))
            .ThenBy(item => id(item))
            .Take(limit);
    }

    public static IQueryable<T> ApplyCandidateOrderAndLimit<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTimeOffset?>> reasonEnteredAt,
        Expression<Func<T, Guid>> id,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reasonEnteredAt);
        ArgumentNullException.ThrowIfNull(id);
        if (limit is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return source.OrderBy(reasonEnteredAt).ThenBy(id).Take(limit);
    }

    public static void RecordFailure(
        Guid operationId,
        Exception exception,
        Action<Exception, Guid>? log = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        log?.Invoke(exception, operationId);
        ManagedLifecycleTelemetry.RecordRecoveryRequiredClockScanFailure();
    }
}
