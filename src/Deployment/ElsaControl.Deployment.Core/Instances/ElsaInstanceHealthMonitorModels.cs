using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Bounds for the periodic Ready-instance health monitor. The monitor is off unless a host
/// enables it; every bound is validated before it runs so a misconfiguration fails startup
/// instead of hammering customer runtimes or never re-probing them.
/// </summary>
public sealed record ElsaInstanceHealthMonitorOptions
{
    public const string ConfigurationSection = "Deployment:ElsaInstanceHealthMonitor";
    public const int MaximumConcurrencyLimit = 32;
    public const int MaximumThreshold = 10;
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumProbeTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumProbeTimeout = TimeSpan.FromMinutes(1);

    public bool Enabled { get; init; }

    /// <summary>Cadence of one evaluation cycle over every Ready instance.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound of each instance's stable offset into a cycle.</summary>
    public TimeSpan MaxJitter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard bound of one runtime probe; a probe that does not answer in time is a failure.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum number of instances probed at the same time.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Consecutive failed probes before a healthy instance is recorded unhealthy.</summary>
    public int UnhealthyThreshold { get; init; } = 3;

    /// <summary>Consecutive healthy probes before an unhealthy instance is recorded healthy.</summary>
    public int HealthyThreshold { get; init; } = 2;

    public void Validate()
    {
        if (Interval < MinimumInterval || Interval > MaximumInterval)
            throw new ArgumentOutOfRangeException(nameof(Interval), "The health monitor interval must be between 15 seconds and one hour.");
        if (MaxJitter < TimeSpan.Zero || MaxJitter > Interval / 2)
            throw new ArgumentOutOfRangeException(nameof(MaxJitter), "The health monitor jitter must be non-negative and at most half the interval.");
        if (ProbeTimeout < MinimumProbeTimeout || ProbeTimeout > MaximumProbeTimeout || MaxJitter + ProbeTimeout >= Interval)
            throw new ArgumentOutOfRangeException(nameof(ProbeTimeout), "The health probe timeout must be between one second and one minute and end before the next interval.");
        if (MaxConcurrency is < 1 or > MaximumConcurrencyLimit)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), "The health monitor concurrency must be between 1 and 32.");
        if (UnhealthyThreshold is < 1 or > MaximumThreshold)
            throw new ArgumentOutOfRangeException(nameof(UnhealthyThreshold), "The unhealthy threshold must be between 1 and 10.");
        if (HealthyThreshold is < 1 or > MaximumThreshold)
            throw new ArgumentOutOfRangeException(nameof(HealthyThreshold), "The healthy threshold must be between 1 and 10.");
    }
}

/// <summary>A read-only runtime probe of one instance's current deployment endpoint.</summary>
public sealed record ElsaInstanceHealthProbeRequest(
    Guid WorkspaceId,
    Guid InstanceId,
    ElsaCurrentDeploymentReference CurrentDeployment,
    TimeSpan Timeout);

/// <summary>
/// Value-free outcome of one runtime probe. <see cref="ElsaInstanceHealth.Healthy"/> is only the
/// byte-exact runtime report; <see cref="ElsaInstanceHealth.Degraded"/> means the runtime answered
/// that it is not healthy; <see cref="ElsaInstanceHealth.Unreachable"/> means no successful answer
/// arrived within the bound; <see cref="ElsaInstanceHealth.Unknown"/> means Control could not
/// classify the runtime (no verified endpoint, a response outside the health contract, or an
/// unavailable probe).
/// </summary>
public sealed record ElsaInstanceHealthProbeResult
{
    public ElsaInstanceHealthProbeResult(ElsaInstanceHealth health, string diagnosticCode)
    {
        if (!Enum.IsDefined(health))
            throw new ArgumentOutOfRangeException(nameof(health), "Probe health is invalid.");
        if (!ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(diagnosticCode))
            throw new ArgumentException("Probe diagnostic code is invalid.", nameof(diagnosticCode));
        Health = health;
        DiagnosticCode = diagnosticCode;
    }

    public ElsaInstanceHealth Health { get; }

    public string DiagnosticCode { get; }
}

/// <summary>
/// One instance in the monitor's evaluated state set, captured at an exact aggregate version.
/// </summary>
public sealed record ElsaInstanceHealthMonitorTarget(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    int Version,
    ElsaDesiredLifecycle DesiredLifecycle,
    ElsaObservedLifecycle ObservedLifecycle,
    ElsaInstanceHealth Health,
    ElsaCurrentDeploymentReference CurrentDeployment);

/// <summary>
/// Compare-and-set request to change only an instance's health. It is valid only while the
/// instance still has <see cref="ExpectedVersion"/> and <see cref="ExpectedHealth"/>.
/// </summary>
public sealed record ElsaInstanceHealthTransition(
    Guid WorkspaceId,
    Guid InstanceId,
    int ExpectedVersion,
    ElsaInstanceHealth ExpectedHealth,
    ElsaInstanceHealth Health,
    string DiagnosticCode,
    DateTimeOffset ObservedAt)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || ExpectedVersion < 1 ||
            !Enum.IsDefined(ExpectedHealth) || !Enum.IsDefined(Health) || ExpectedHealth == Health ||
            !ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(DiagnosticCode) || ObservedAt == default)
            throw new InvalidOperationException("Instance health transition is invalid.");
    }
}

public enum ElsaInstanceHealthMonitorOutcome
{
    /// <summary>The instance left the evaluated state set before it was probed.</summary>
    Skipped,
    /// <summary>The probe completed and the recorded health did not change.</summary>
    Unchanged,
    /// <summary>The probe completed a streak and the new health was committed.</summary>
    Transitioned,
    /// <summary>A due change was dropped because the instance changed concurrently.</summary>
    Conflict,
    /// <summary>The evaluation failed before it could classify or record the instance.</summary>
    Error
}

/// <summary>
/// Safe result of one evaluation for host logging: opaque identifiers, enum states, a bounded
/// diagnostic code and, at most, the name of an exception type.
/// </summary>
public sealed record ElsaInstanceHealthEvaluation(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    ElsaInstanceHealthMonitorOutcome Outcome,
    ElsaInstanceHealth Observed,
    ElsaInstanceHealth PriorHealth,
    ElsaInstanceHealth Health,
    string DiagnosticCode,
    string? ExceptionType = null);

/// <summary>
/// An isolated unit of work for one evaluation. Hosts back it with a dependency-injection scope
/// so concurrent evaluations never share a persistence context.
/// </summary>
public sealed class ElsaInstanceHealthMonitorScope(
    IElsaInstanceHealthMonitorStore store,
    IElsaInstanceProviderHealthProbePort probe,
    IAsyncDisposable? lifetime = null) : IAsyncDisposable
{
    public IElsaInstanceHealthMonitorStore Store { get; } = store;

    public IElsaInstanceProviderHealthProbePort Probe { get; } = probe;

    public ValueTask DisposeAsync() => lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;
}
