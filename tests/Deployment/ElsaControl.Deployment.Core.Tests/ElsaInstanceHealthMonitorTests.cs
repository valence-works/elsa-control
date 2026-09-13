using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Xunit;
using static ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceHealth;
using static ElsaControl.Deployment.Core.Instances.ElsaInstanceHealthMonitorOutcome;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ElsaInstanceHealthMonitorTests
{
    private static readonly ElsaInstanceHealthMonitorOptions Defaults = new() { Enabled = true };
    private readonly FakeHealthMonitorStore _store = new();
    private readonly ScriptedHealthProbe _probe = new();
    private readonly ElsaInstanceHealthMonitor _monitor = new(Defaults);

    [Fact]
    public async Task Healthy_instance_is_recorded_unhealthy_only_after_three_consecutive_failures()
    {
        var target = _store.Add(Healthy);

        var evaluations = await EvaluateAsync(target, Unreachable, Unreachable, Unreachable);

        Assert.Equal(new[] { Unchanged, Unchanged, Transitioned }, Outcomes(evaluations));
        var commit = Assert.Single(_store.Commits);
        Assert.Equal((1, Healthy, Unreachable, "test.health.unreachable"),
            (commit.ExpectedVersion, commit.ExpectedHealth, commit.Health, commit.DiagnosticCode));
        Assert.Equal((Unreachable, 2), (_store[target.InstanceId].Health, _store[target.InstanceId].Version));
        Assert.Equal((Healthy, Unreachable), (evaluations[^1].PriorHealth, evaluations[^1].Health));
    }

    [Fact]
    public async Task A_healthy_probe_resets_the_failure_streak()
    {
        var target = _store.Add(Healthy);

        await EvaluateAsync(target, Degraded, Degraded, Healthy, Degraded, Degraded);
        Assert.Empty(_store.Commits);

        await EvaluateAsync(target, Degraded);
        Assert.Equal(Degraded, Assert.Single(_store.Commits).Health);
    }

    [Fact]
    public async Task Unhealthy_instance_recovers_only_after_two_consecutive_successes()
    {
        var target = _store.Add(Unreachable);

        await EvaluateAsync(target, Healthy, Unreachable, Healthy);
        Assert.Empty(_store.Commits);

        var evaluations = await EvaluateAsync(target, Healthy);
        Assert.Equal(new[] { Transitioned }, Outcomes(evaluations));
        Assert.Equal((Unreachable, Healthy), (Assert.Single(_store.Commits).ExpectedHealth, _store[target.InstanceId].Health));
    }

    [Fact]
    public async Task Steady_health_is_never_written()
    {
        var healthy = _store.Add(Healthy);
        var unreachable = _store.Add(Unreachable);

        await EvaluateAsync(healthy, Enumerable.Repeat(Healthy, 10).ToArray());
        await EvaluateAsync(unreachable, Enumerable.Repeat(Unreachable, 10).ToArray());

        Assert.Empty(_store.Commits);
    }

    [Fact]
    public async Task Unknown_probes_count_as_failures_and_are_recorded_as_unknown()
    {
        var target = _store.Add(Healthy);

        await EvaluateAsync(target, Unknown, Unknown, Unknown);

        Assert.Equal(Unknown, Assert.Single(_store.Commits).Health);
    }

    [Fact]
    public async Task A_healthy_instance_takes_the_classification_of_the_failure_that_completes_the_streak()
    {
        var target = _store.Add(Healthy);

        await EvaluateAsync(target, Unreachable, Unreachable, Degraded);

        Assert.Equal(Degraded, Assert.Single(_store.Commits).Health);
    }

    [Fact]
    public async Task An_unhealthy_instance_changes_its_failure_kind_only_after_three_probes_of_the_new_kind()
    {
        var target = _store.Add(Unreachable);

        await EvaluateAsync(target, Degraded, Degraded, Unreachable, Degraded, Degraded);
        Assert.Empty(_store.Commits);

        await EvaluateAsync(target, Degraded);
        Assert.Equal((Unreachable, Degraded), (Assert.Single(_store.Commits).ExpectedHealth, _store[target.InstanceId].Health));
    }

    [Fact]
    public async Task A_probe_timeout_is_an_unreachable_failure()
    {
        var target = _store.Add(Healthy);
        for (var attempt = 0; attempt < 3; attempt++)
            _probe.Throws(new OperationCanceledException());

        var evaluations = await EvaluateAsync(target, 3);

        Assert.All(evaluations, x => Assert.Equal((Unreachable, ElsaInstanceHealthMonitor.ProbeTimedOutCode), (x.Observed, x.DiagnosticCode)));
        Assert.Equal(Unreachable, Assert.Single(_store.Commits).Health);
    }

    [Fact]
    public async Task A_probe_is_cancelled_at_the_configured_timeout()
    {
        var monitor = new ElsaInstanceHealthMonitor(Defaults with { ProbeTimeout = TimeSpan.FromSeconds(1) });
        var target = _store.Add(Healthy);
        _probe.Runs(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ScriptedHealthProbe.Result(Healthy);
        });

        var evaluation = await monitor.EvaluateAsync(target, _store, _probe);

        Assert.Equal((Unreachable, ElsaInstanceHealthMonitor.ProbeTimedOutCode), (evaluation.Observed, evaluation.DiagnosticCode));
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(_probe.Requests).Timeout);
    }

    [Fact]
    public async Task A_probe_cut_short_by_host_shutdown_is_discarded_and_never_counts_as_a_failure()
    {
        var target = _store.Add(Healthy);
        await EvaluateAsync(target, Unreachable, Unreachable);
        using var stopping = new CancellationTokenSource();
        _probe.Runs((_, _) =>
        {
            stopping.Cancel();
            return Task.FromResult(ScriptedHealthProbe.Result(Unreachable));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _monitor.EvaluateAsync(target, _store, _probe, stopping.Token));

        // Counted, this would have been the third failure and a recorded Unreachable.
        Assert.Empty(_store.Commits);
        Assert.Equal(Healthy, _store[target.InstanceId].Health);
    }

    [Fact]
    public async Task A_store_failure_during_shutdown_is_an_interruption_not_an_evaluation_error()
    {
        var target = _store.Add(Healthy);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _store.ReadFailure = new InvalidOperationException("The connection was closed by shutdown.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _monitor.EvaluateAsync(target, _store, _probe, stopping.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _monitor.RunCycleAsync(_store, OpenScope, stopping.Token));
    }

    [Fact]
    public async Task A_busy_instance_is_skipped_without_a_probe_and_restarts_its_streak()
    {
        var target = _store.Add(Healthy);
        await EvaluateAsync(target, Unreachable, Unreachable);
        _store.Busy.Add(target.InstanceId);

        var skipped = await _monitor.EvaluateAsync(target, _store, _probe);

        Assert.Equal(Skipped, skipped.Outcome);
        Assert.Equal(2, _probe.Requests.Count);
        _store.Busy.Remove(target.InstanceId);
        await EvaluateAsync(target, Unreachable, Unreachable);
        Assert.Empty(_store.Commits);
        await EvaluateAsync(target, Unreachable);
        Assert.Single(_store.Commits);
    }

    [Fact]
    public async Task A_change_by_another_writer_restarts_the_streak()
    {
        var target = _store.Add(Healthy);
        await EvaluateAsync(target, Unreachable, Unreachable);

        _store.Touch(target.InstanceId);
        await EvaluateAsync(target, Unreachable, Unreachable);
        Assert.Empty(_store.Commits);

        await EvaluateAsync(target, Unreachable);
        Assert.Equal(2, Assert.Single(_store.Commits).ExpectedVersion);
    }

    [Fact]
    public async Task A_conflicting_commit_is_dropped_and_restarts_the_streak()
    {
        var target = _store.Add(Healthy);
        _store.ConflictOnCommit = true;

        var evaluations = await EvaluateAsync(target, Unreachable, Unreachable, Unreachable);

        Assert.Equal(new[] { Unchanged, Unchanged, Conflict }, Outcomes(evaluations));
        Assert.Equal(Healthy, _store[target.InstanceId].Health);
        _store.ConflictOnCommit = false;
        await EvaluateAsync(target, Unreachable, Unreachable);
        Assert.Empty(_store.Commits);
        await EvaluateAsync(target, Unreachable);
        Assert.Single(_store.Commits);
    }

    [Fact]
    public async Task Failures_surface_only_an_exception_type_and_a_fixed_code()
    {
        const string sensitive = "GET https://tenant.example.test/health?token=secret-value failed";
        var target = _store.Add(Healthy);
        for (var attempt = 0; attempt < 3; attempt++)
            _probe.Throws(new HttpRequestException(sensitive));

        var probeFailures = await EvaluateAsync(target, 3);
        _store.ReadFailure = new InvalidOperationException(sensitive);
        var readFailure = await _monitor.EvaluateAsync(target, _store, _probe);

        Assert.All(probeFailures, x => Assert.Equal(
            (Unknown, ElsaInstanceHealthMonitor.ProbeFailedCode, nameof(HttpRequestException)),
            (x.Observed, x.DiagnosticCode, x.ExceptionType)));
        Assert.Equal((Error, ElsaInstanceHealthMonitor.EvaluationFailedCode, nameof(InvalidOperationException)),
            (readFailure.Outcome, readFailure.DiagnosticCode, readFailure.ExceptionType));
        Assert.Equal((Unknown, ElsaInstanceHealthMonitor.ProbeFailedCode),
            (Assert.Single(_store.Commits).Health, _store.Commits[0].DiagnosticCode));
        Assert.Equal(3, _probe.Requests.Count);
        var serialized = JsonSerializer.Serialize(new object[] { probeFailures, readFailure, _store.Commits });
        Assert.DoesNotContain("tenant.example.test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime.example.test", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Jitter_is_a_stable_per_instance_offset_bounded_by_the_maximum()
    {
        var instanceIds = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();

        var offsets = instanceIds.Select(_monitor.JitterFor).ToArray();

        Assert.All(offsets, offset => Assert.InRange(offset, TimeSpan.Zero, Defaults.MaxJitter - TimeSpan.FromTicks(1)));
        Assert.Equal(offsets, instanceIds.Select(_monitor.JitterFor));
        Assert.True(offsets.Distinct().Count() > 900, "offsets must spread instances across the jitter window");
        Assert.Equal(TimeSpan.Zero, new ElsaInstanceHealthMonitor(Defaults with { MaxJitter = TimeSpan.Zero }).JitterFor(instanceIds[0]));
    }

    [Fact]
    public async Task A_cycle_waits_for_each_instances_own_jitter_offset()
    {
        var targets = Enumerable.Range(0, 20).Select(_ => _store.Add()).ToArray();
        var waits = new List<TimeSpan>();
        var monitor = new ElsaInstanceHealthMonitor(Defaults, new FrozenClock(), (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });

        await monitor.RunCycleAsync(_store, OpenScope);

        Assert.Equal(targets.Select(x => monitor.JitterFor(x.InstanceId)).Where(x => x > TimeSpan.Zero).Order(), waits);
        Assert.All(waits, wait => Assert.InRange(wait, TimeSpan.Zero, Defaults.MaxJitter));
    }

    [Fact]
    public async Task A_cycle_never_probes_more_instances_at_once_than_the_concurrency_cap()
    {
        for (var index = 0; index < 12; index++)
            _store.Add();
        var monitor = new ElsaInstanceHealthMonitor(Defaults with { MaxJitter = TimeSpan.Zero, MaxConcurrency = 3 });
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        int active = 0, peak = 0;
        _probe.Default = async (_, cancellationToken) =>
        {
            lock (gate)
                peak = Math.Max(peak, ++active);
            if (peak == 3)
                saturated.TrySetResult();
            await saturated.Task.WaitAsync(cancellationToken);
            await Task.Delay(5, cancellationToken);
            lock (gate)
                active--;
            return ScriptedHealthProbe.Result(Healthy);
        };

        var evaluations = await monitor.RunCycleAsync(_store, OpenScope);

        Assert.Equal(12, evaluations.Count);
        Assert.All(evaluations, x => Assert.Equal(Unchanged, x.Outcome));
        Assert.Equal(3, peak);
    }

    [Fact]
    public async Task A_cycle_evaluates_every_listed_instance_once_in_its_own_disposed_scope()
    {
        var targets = Enumerable.Range(0, 450).Select(_ => _store.Add()).ToArray();
        _store.Busy.Add(targets[0].InstanceId);
        var monitor = new ElsaInstanceHealthMonitor(Defaults with { MaxJitter = TimeSpan.Zero });
        var lifetime = new CountingLifetime();

        var evaluations = await monitor.RunCycleAsync(_store, () =>
        {
            lifetime.Opened();
            return new(_store, _probe, lifetime);
        });

        Assert.Equal(targets.Skip(1).Select(x => x.InstanceId).Order(), evaluations.Select(x => x.InstanceId).Order());
        Assert.Equal((449, 449), (lifetime.OpenCount, lifetime.DisposeCount));
    }

    [Fact]
    public void Defaults_are_off_and_within_bounds()
    {
        var options = new ElsaInstanceHealthMonitorOptions();

        options.Validate();
        Assert.False(options.Enabled);
        Assert.Equal(
            (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), 4, 3, 2),
            (options.Interval, options.MaxJitter, options.ProbeTimeout, options.MaxConcurrency, options.UnhealthyThreshold, options.HealthyThreshold));
    }

    [Theory]
    [MemberData(nameof(OutOfBoundsOptions))]
    public void Options_outside_their_bounds_are_rejected(ElsaInstanceHealthMonitorOptions options)
    {
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElsaInstanceHealthMonitor(options));
    }

    public static TheoryData<ElsaInstanceHealthMonitorOptions> OutOfBoundsOptions => new()
    {
        Defaults with { Interval = TimeSpan.FromSeconds(14) },
        Defaults with { Interval = TimeSpan.FromMinutes(61) },
        Defaults with { MaxJitter = TimeSpan.FromSeconds(-1) },
        Defaults with { MaxJitter = TimeSpan.FromSeconds(31) },
        Defaults with { ProbeTimeout = TimeSpan.FromMilliseconds(999) },
        Defaults with { ProbeTimeout = TimeSpan.FromSeconds(61) },
        Defaults with { Interval = TimeSpan.FromSeconds(20), MaxJitter = TimeSpan.FromSeconds(10), ProbeTimeout = TimeSpan.FromSeconds(10) },
        Defaults with { MaxConcurrency = 0 },
        Defaults with { MaxConcurrency = 33 },
        Defaults with { UnhealthyThreshold = 0 },
        Defaults with { UnhealthyThreshold = 11 },
        Defaults with { HealthyThreshold = 0 },
        Defaults with { HealthyThreshold = 11 }
    };

    private ElsaInstanceHealthMonitorScope OpenScope() => new(_store, _probe);

    private Task<ElsaInstanceHealthEvaluation[]> EvaluateAsync(
        ElsaInstanceHealthMonitorTarget target,
        params ElsaInstanceHealth[] probes)
    {
        _probe.Returns(probes);
        return EvaluateAsync(target, probes.Length);
    }

    private async Task<ElsaInstanceHealthEvaluation[]> EvaluateAsync(ElsaInstanceHealthMonitorTarget target, int times)
    {
        var evaluations = new ElsaInstanceHealthEvaluation[times];
        for (var index = 0; index < times; index++)
            evaluations[index] = await _monitor.EvaluateAsync(target, _store, _probe);
        return evaluations;
    }

    private static ElsaInstanceHealthMonitorOutcome[] Outcomes(IEnumerable<ElsaInstanceHealthEvaluation> evaluations) =>
        evaluations.Select(x => x.Outcome).ToArray();

    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CountingLifetime : IAsyncDisposable
    {
        private int _opened;
        private int _disposed;

        public int OpenCount => Volatile.Read(ref _opened);

        public int DisposeCount => Volatile.Read(ref _disposed);

        public void Opened() => Interlocked.Increment(ref _opened);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }
}
