using System.Buffers.Binary;
using System.Security.Cryptography;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Telemetry;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Periodically re-evaluates the endpoint health of Ready managed instances so Control's
/// <see cref="ElsaInstanceHealth"/> reflects the current runtime instead of the observation of the
/// last lifecycle operation.
/// <para>
/// Evaluated state set: a managed instance that is not tombstoned, whose desired lifecycle is
/// <see cref="ElsaDesiredLifecycle.Running"/>, whose observed lifecycle is
/// <see cref="ElsaObservedLifecycle.Ready"/>, that has a current deployment endpoint and that has no
/// blocking lifecycle operation (Accepted, WaitingForPriorOperation, Queued, EntitlementHeld,
/// Running or RecoveryRequired). Every health value is evaluated so an unhealthy instance can
/// recover. The store applies the set when listing, again immediately before each probe, and
/// once more inside the transaction that records a change.
/// </para>
/// <para>
/// Invariant: health changes only after <see cref="ElsaInstanceHealthMonitorOptions.UnhealthyThreshold"/>
/// consecutive failed probes (or <see cref="ElsaInstanceHealthMonitorOptions.HealthyThreshold"/>
/// consecutive healthy probes), all gathered against the exact instance version the change is
/// committed over. The streak is keyed by that version and is dropped whenever the instance
/// leaves the evaluated set, another writer changes it, or a commit conflicts; the store's
/// version compare-and-set rejects a commit that races a lifecycle change, so the monitor never
/// overwrites one. Only health changes: the observed lifecycle stays Ready. A probe cut short by
/// host shutdown is discarded and never counts as a failure.
/// </para>
/// </summary>
public sealed class ElsaInstanceHealthMonitor
{
    public const string ProbeTimedOutCode = "endpoint.health.timed-out";
    public const string ProbeFailedCode = "endpoint.health.probe-failed";
    public const string EvaluationFailedCode = "endpoint.health.evaluation-failed";
    public const string SkippedCode = "endpoint.health.skipped";
    private const int PageSize = 200;

    private readonly ElsaInstanceHealthMonitorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<Guid, Streak> _streaks = [];

    /// <param name="options">Validated monitor bounds.</param>
    /// <param name="timeProvider">Clock for jitter scheduling, probe timeouts and audit timestamps.</param>
    /// <param name="delay">Scheduling seam for the per-instance jitter wait; defaults to a timer delay.</param>
    public ElsaInstanceHealthMonitor(
        ElsaInstanceHealthMonitorOptions options,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? ((wait, cancellationToken) => Task.Delay(wait, _timeProvider, cancellationToken));
    }

    /// <summary>
    /// A stable offset of each instance into a cycle within [0, MaxJitter), derived from its ID so
    /// probes spread across the cycle and every instance keeps a steady cadence.
    /// </summary>
    public TimeSpan JitterFor(Guid instanceId)
    {
        if (_options.MaxJitter <= TimeSpan.Zero)
            return TimeSpan.Zero;
        Span<byte> identity = stackalloc byte[16];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        instanceId.TryWriteBytes(identity);
        SHA256.HashData(identity, hash);
        var fraction = BinaryPrimitives.ReadUInt32LittleEndian(hash) / 4294967296d;
        return TimeSpan.FromTicks((long)(_options.MaxJitter.Ticks * fraction));
    }

    /// <summary>
    /// Evaluates every instance in the evaluated state set once: each waits for its jitter offset
    /// and a free slot (at most <see cref="ElsaInstanceHealthMonitorOptions.MaxConcurrency"/>), then
    /// runs in its own scope. Started evaluations always finish before the cycle returns.
    /// </summary>
    public async Task<IReadOnlyList<ElsaInstanceHealthEvaluation>> RunCycleAsync(
        IElsaInstanceHealthMonitorStore targets,
        Func<ElsaInstanceHealthMonitorScope> openEvaluationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(openEvaluationScope);
        var candidates = await ListAsync(targets, cancellationToken);
        Retain(candidates);

        var started = _timeProvider.GetUtcNow();
        using var slots = new SemaphoreSlim(_options.MaxConcurrency);
        var evaluations = new List<Task<ElsaInstanceHealthEvaluation>>(candidates.Count);
        try
        {
            foreach (var candidate in candidates.OrderBy(x => JitterFor(x.InstanceId)).ThenBy(x => x.InstanceId))
            {
                var wait = started + JitterFor(candidate.InstanceId) - _timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                    await _delay(wait, cancellationToken);
                await slots.WaitAsync(cancellationToken);
                evaluations.Add(EvaluateInScopeAsync(candidate, openEvaluationScope, slots, cancellationToken));
            }
        }
        finally
        {
            await ((Task)Task.WhenAll(evaluations)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return await Task.WhenAll(evaluations);
    }

    /// <summary>
    /// Re-reads one listed instance, probes it if it is still in the evaluated state set, applies
    /// the hysteresis and records a health change only when a streak completes. Every evaluation
    /// that reaches the probe emits one endpoint health evaluation measurement.
    /// </summary>
    public async Task<ElsaInstanceHealthEvaluation> EvaluateAsync(
        ElsaInstanceHealthMonitorTarget candidate,
        IElsaInstanceHealthMonitorStore store,
        IElsaInstanceProviderHealthProbePort probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(probe);
        ElsaInstanceHealthMonitorTarget? target = null;
        ElsaInstanceHealthProbeResult? observation = null;
        try
        {
            target = await store.GetHealthMonitorTargetAsync(candidate.WorkspaceId, candidate.InstanceId, cancellationToken);
            if (target is null)
            {
                Forget(candidate.InstanceId);
                return Evaluation(candidate, ElsaInstanceHealthMonitorOutcome.Skipped, candidate.Health, candidate.Health, SkippedCode);
            }

            (observation, var probeException) = await ProbeAsync(probe, target, cancellationToken);
            if (Observe(target, observation.Health) is not { } health)
                return Record(target, ElsaInstanceHealthMonitorOutcome.Unchanged, observation, target.Health, probeException);

            try
            {
                var version = await store.CommitHealthTransitionAsync(new(
                    target.WorkspaceId,
                    target.InstanceId,
                    target.Version,
                    target.Health,
                    health,
                    observation.DiagnosticCode,
                    _timeProvider.GetUtcNow()), cancellationToken);
                Committed(target.InstanceId, version);
                return Record(target, ElsaInstanceHealthMonitorOutcome.Transitioned, observation, health, probeException);
            }
            catch (ElsaInstanceLifecycleConflictException)
            {
                Forget(target.InstanceId);
                return Record(target, ElsaInstanceHealthMonitorOutcome.Conflict, observation, target.Health, probeException);
            }
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw Interrupted(exception, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(target ?? candidate, observation, exception);
        }
    }

    private async Task<ElsaInstanceHealthEvaluation> EvaluateInScopeAsync(
        ElsaInstanceHealthMonitorTarget candidate,
        Func<ElsaInstanceHealthMonitorScope> openEvaluationScope,
        SemaphoreSlim slots,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = openEvaluationScope();
            return await EvaluateAsync(candidate, scope.Store, scope.Probe, cancellationToken);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            throw Interrupted(exception, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(candidate, null, exception);
        }
        finally
        {
            slots.Release();
        }
    }

    // Work interrupted by host shutdown is neither evidence nor an evaluation error.
    private static OperationCanceledException Interrupted(Exception exception, CancellationToken cancellationToken) =>
        new("The health evaluation was interrupted by shutdown.", exception, cancellationToken);

    private async Task<(ElsaInstanceHealthProbeResult Result, Exception? Exception)> ProbeAsync(
        IElsaInstanceProviderHealthProbePort probe,
        ElsaInstanceHealthMonitorTarget target,
        CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(_options.ProbeTimeout, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        (ElsaInstanceHealthProbeResult, Exception?) outcome;
        try
        {
            var result = await probe.ProbeAsync(
                new(target.WorkspaceId, target.InstanceId, target.CurrentDeployment, _options.ProbeTimeout),
                bounded.Token);
            outcome = (result ?? new(ElsaInstanceHealth.Unknown, ProbeFailedCode), null);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            outcome = (new(ElsaInstanceHealth.Unreachable, ProbeTimedOutCode), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            outcome = (new(ElsaInstanceHealth.Unknown, ProbeFailedCode), exception);
        }

        // A probe that returns while the host stops may have been cut short; it is never evidence.
        stoppingToken.ThrowIfCancellationRequested();
        return outcome;
    }

    private ElsaInstanceHealth? Observe(ElsaInstanceHealthMonitorTarget target, ElsaInstanceHealth observed)
    {
        lock (_streaks)
        {
            var streak = _streaks.TryGetValue(target.InstanceId, out var current) && current.Version == target.Version
                ? current
                : new Streak(target.Version);
            streak = observed == ElsaInstanceHealth.Healthy
                ? streak with { Successes = streak.Successes + 1, Failures = 0, SameKindFailures = 0 }
                : streak with
                {
                    Successes = 0,
                    Failures = streak.Failures + 1,
                    SameKindFailures = streak.Failures > 0 && streak.FailureHealth == observed ? streak.SameKindFailures + 1 : 1,
                    FailureHealth = observed
                };
            _streaks[target.InstanceId] = streak;

            if (observed == ElsaInstanceHealth.Healthy)
                return target.Health != ElsaInstanceHealth.Healthy && streak.Successes >= _options.HealthyThreshold
                    ? ElsaInstanceHealth.Healthy
                    : null;
            if (target.Health == ElsaInstanceHealth.Healthy)
                return streak.Failures >= _options.UnhealthyThreshold ? observed : null;
            return target.Health != observed && streak.SameKindFailures >= _options.UnhealthyThreshold ? observed : null;
        }
    }

    private void Committed(Guid instanceId, int version)
    {
        lock (_streaks)
        {
            if (_streaks.TryGetValue(instanceId, out var streak))
                _streaks[instanceId] = streak with { Version = version };
        }
    }

    private void Forget(Guid instanceId)
    {
        lock (_streaks)
            _streaks.Remove(instanceId);
    }

    private void Retain(IReadOnlyCollection<ElsaInstanceHealthMonitorTarget> candidates)
    {
        var retained = candidates.Select(x => x.InstanceId).ToHashSet();
        lock (_streaks)
        {
            foreach (var instanceId in _streaks.Keys.Where(x => !retained.Contains(x)).ToArray())
                _streaks.Remove(instanceId);
        }
    }

    private ElsaInstanceHealthEvaluation Failed(
        ElsaInstanceHealthMonitorTarget target,
        ElsaInstanceHealthProbeResult? observation,
        Exception exception)
    {
        Forget(target.InstanceId);
        return Record(
            target,
            ElsaInstanceHealthMonitorOutcome.Error,
            observation ?? new(ElsaInstanceHealth.Unknown, EvaluationFailedCode),
            target.Health,
            exception);
    }

    private static ElsaInstanceHealthEvaluation Record(
        ElsaInstanceHealthMonitorTarget target,
        ElsaInstanceHealthMonitorOutcome outcome,
        ElsaInstanceHealthProbeResult observation,
        ElsaInstanceHealth health,
        Exception? exception)
    {
        ManagedLifecycleTelemetry.RecordEndpointHealthEvaluation(
            outcome switch
            {
                ElsaInstanceHealthMonitorOutcome.Transitioned => "transition",
                ElsaInstanceHealthMonitorOutcome.Conflict => "conflict",
                ElsaInstanceHealthMonitorOutcome.Error => "error",
                _ => "succeeded"
            },
            target.DesiredLifecycle,
            target.ObservedLifecycle,
            observation.Health);
        return Evaluation(target, outcome, observation.Health, health, observation.DiagnosticCode, exception?.GetType().Name);
    }

    private static ElsaInstanceHealthEvaluation Evaluation(
        ElsaInstanceHealthMonitorTarget target,
        ElsaInstanceHealthMonitorOutcome outcome,
        ElsaInstanceHealth observed,
        ElsaInstanceHealth health,
        string diagnosticCode,
        string? exceptionType = null) =>
        new(target.OrganizationId, target.WorkspaceId, target.InstanceId, outcome, observed, target.Health, health,
            diagnosticCode, exceptionType);

    private static async Task<IReadOnlyList<ElsaInstanceHealthMonitorTarget>> ListAsync(
        IElsaInstanceHealthMonitorStore store,
        CancellationToken cancellationToken)
    {
        var targets = new Dictionary<Guid, ElsaInstanceHealthMonitorTarget>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await store.ListHealthMonitorTargetsAsync(offset, PageSize, cancellationToken);
            var added = page.Count(target => targets.TryAdd(target.InstanceId, target));
            // A short page ends the listing; a page with nothing new cannot make progress either.
            if (page.Count < PageSize || added == 0)
                return targets.Values.ToArray();
        }
    }

    private sealed record Streak(
        int Version,
        int Successes = 0,
        int Failures = 0,
        int SameKindFailures = 0,
        ElsaInstanceHealth FailureHealth = ElsaInstanceHealth.Unknown);
}
