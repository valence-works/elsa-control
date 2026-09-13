using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Core.Tests;

/// <summary>
/// In-memory monitor store with the relational store's rules: only eligible (not busy) rows are
/// read, and a commit is a compare-and-set on version and health that bumps the version.
/// </summary>
internal sealed class FakeHealthMonitorStore : IElsaInstanceHealthMonitorStore
{
    private readonly object _gate = new();
    private readonly SortedDictionary<Guid, ElsaInstanceHealthMonitorTarget> _targets = [];

    public List<ElsaInstanceHealthTransition> Commits { get; } = [];

    public HashSet<Guid> Busy { get; } = [];

    public Exception? ReadFailure { get; set; }

    public bool ConflictOnCommit { get; set; }

    public ElsaInstanceHealthMonitorTarget Add(ElsaInstanceHealth health = ElsaInstanceHealth.Healthy, Guid? instanceId = null)
    {
        var id = instanceId ?? Guid.NewGuid();
        var target = new ElsaInstanceHealthMonitorTarget(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            id,
            1,
            ElsaDesiredLifecycle.Running,
            ElsaObservedLifecycle.Ready,
            health,
            new ElsaCurrentDeploymentReference("deployment-1", "attempt-1", "https://runtime.example.test"));
        lock (_gate)
            _targets[id] = target;
        return target;
    }

    public ElsaInstanceHealthMonitorTarget this[Guid instanceId]
    {
        get
        {
            lock (_gate)
                return _targets[instanceId];
        }
    }

    /// <summary>Simulates another writer changing the instance (for example a rename).</summary>
    public void Touch(Guid instanceId)
    {
        lock (_gate)
            _targets[instanceId] = _targets[instanceId] with { Version = _targets[instanceId].Version + 1 };
    }

    public Task<IReadOnlyList<ElsaInstanceHealthMonitorTarget>> ListHealthMonitorTargetsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ElsaInstanceHealthMonitorTarget>>(
                _targets.Values.Where(x => !Busy.Contains(x.InstanceId)).Skip(offset).Take(limit).ToArray());
    }

    public Task<ElsaInstanceHealthMonitorTarget?> GetHealthMonitorTargetAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (ReadFailure is { } failure)
            throw failure;
        lock (_gate)
            return Task.FromResult(_targets.TryGetValue(instanceId, out var target) && !Busy.Contains(instanceId) &&
                                   target.WorkspaceId == workspaceId
                ? target
                : null);
    }

    public Task<int> CommitHealthTransitionAsync(
        ElsaInstanceHealthTransition transition,
        CancellationToken cancellationToken = default)
    {
        transition.Validate();
        lock (_gate)
        {
            var current = _targets[transition.InstanceId];
            if (ConflictOnCommit || Busy.Contains(transition.InstanceId) ||
                current.Version != transition.ExpectedVersion || current.Health != transition.ExpectedHealth)
                throw new ElsaInstanceLifecycleConflictException("Instance health changed concurrently.");
            _targets[transition.InstanceId] = current with { Health = transition.Health, Version = current.Version + 1 };
            Commits.Add(transition);
            return Task.FromResult(current.Version + 1);
        }
    }
}

/// <summary>A probe that answers from a script, then with <see cref="Default"/>.</summary>
internal sealed class ScriptedHealthProbe : IElsaInstanceProviderHealthProbePort
{
    private readonly Queue<Func<ElsaInstanceHealthProbeRequest, CancellationToken, Task<ElsaInstanceHealthProbeResult>>> _script = new();
    private readonly List<ElsaInstanceHealthProbeRequest> _requests = [];

    public Func<ElsaInstanceHealthProbeRequest, CancellationToken, Task<ElsaInstanceHealthProbeResult>> Default { get; set; } =
        (_, _) => Task.FromResult(Result(ElsaInstanceHealth.Healthy));

    public IReadOnlyList<ElsaInstanceHealthProbeRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToArray();
        }
    }

    public static ElsaInstanceHealthProbeResult Result(ElsaInstanceHealth health) =>
        new(health, $"test.health.{health.ToString().ToLowerInvariant()}");

    public void Returns(params ElsaInstanceHealth[] results)
    {
        foreach (var health in results)
            Runs((_, _) => Task.FromResult(Result(health)));
    }

    public void Throws(Exception exception) => Runs((_, _) => Task.FromException<ElsaInstanceHealthProbeResult>(exception));

    public void Runs(Func<ElsaInstanceHealthProbeRequest, CancellationToken, Task<ElsaInstanceHealthProbeResult>> step)
    {
        lock (_script)
            _script.Enqueue(step);
    }

    public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
        ElsaInstanceHealthProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        lock (_requests)
            _requests.Add(request);
        Func<ElsaInstanceHealthProbeRequest, CancellationToken, Task<ElsaInstanceHealthProbeResult>> step;
        lock (_script)
            step = _script.TryDequeue(out var scripted) ? scripted : Default;
        return step(request, cancellationToken);
    }
}
