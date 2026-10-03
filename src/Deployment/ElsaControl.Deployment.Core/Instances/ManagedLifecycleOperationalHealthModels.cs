using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Workspace;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Provider-neutral operational status for a managed Elsa lifecycle projection.
/// This is deliberately separate from the lifecycle state machine: it describes
/// the current projection and does not introduce any transitions.
/// </summary>
public enum ManagedLifecycleOperationalHealthStatus
{
    Healthy,
    Degraded,
    Failed,
    Unknown,
    Stale,
    RecoveryRequired
}

/// <summary>Stable, safe diagnostic codes emitted by the evaluator.</summary>
public static class ManagedLifecycleOperationalHealthDiagnosticCodes
{
    public const string Healthy = "managed.lifecycle.healthy";
    public const string Degraded = "managed.lifecycle.degraded";
    public const string Failed = "managed.lifecycle.failed";
    public const string Unknown = "managed.lifecycle.unknown";
    public const string ProviderUnknown = "managed.lifecycle.provider-unknown";
    public const string Stale = "managed.lifecycle.stale";
    public const string StaleWork = "managed.lifecycle.stale-work";
    public const string ReconciliationUnknown = "managed.lifecycle.reconciliation-unknown";
    public const string ReconciliationStale = "managed.lifecycle.reconciliation-stale";
    public const string UnhealthyEndpoint = "managed.lifecycle.unhealthy-endpoint";
    public const string RecoveryRequired = "managed.lifecycle.recovery-required";
    public const string OperationFailed = "managed.lifecycle.operation-failed";
    public const string RunFailed = "managed.lifecycle.run-failed";
    public const string WorkActive = "managed.lifecycle.work-active";
    public const string RetryExhausted = "managed.lifecycle.retry-exhausted";

    public static bool IsSafe(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '.' or '-');
}

/// <summary>
/// The bounded operation projection consumed by the health evaluator. It carries
/// no request payload, provider data, credentials, names, or free-form diagnostics.
/// </summary>
public sealed record ManagedLifecycleOperationSnapshot
{
    public ManagedLifecycleOperationSnapshot(
        Guid id,
        ElsaInstanceOperationState state,
        int attemptNumber,
        DateTimeOffset acceptedAt,
        DateTimeOffset? startedAt = null,
        string? diagnosticCode = null,
        DateTimeOffset? heartbeatAt = null,
        DateTimeOffset? lastProgressAt = null,
        DateTimeOffset? requiresHumanAt = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Operation ID is required.", nameof(id));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), "Operation state is invalid.");
        if (attemptNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Operation attempt number must be positive.");
        if (acceptedAt == default)
            throw new ArgumentException("Operation acceptance timestamp is required.", nameof(acceptedAt));

        var acceptedAtUtc = acceptedAt.ToUniversalTime();
        var startedAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            startedAt, acceptedAtUtc, nameof(startedAt));
        var heartbeatAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            heartbeatAt, acceptedAtUtc, nameof(heartbeatAt));
        var lastProgressAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            lastProgressAt, acceptedAtUtc, nameof(lastProgressAt));

        Id = id;
        State = state;
        AttemptNumber = attemptNumber;
        AcceptedAt = acceptedAtUtc;
        StartedAt = startedAtUtc;
        HeartbeatAt = heartbeatAtUtc;
        LastProgressAt = lastProgressAtUtc;
        DiagnosticCode = ManagedLifecycleOperationalHealthValue.OptionalDiagnosticCode(diagnosticCode, nameof(diagnosticCode));
        RequiresHumanAt = requiresHumanAt?.ToUniversalTime();
    }

    public Guid Id { get; }

    public ElsaInstanceOperationState State { get; }

    public int AttemptNumber { get; }

    public DateTimeOffset AcceptedAt { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? HeartbeatAt { get; }

    public DateTimeOffset? LastProgressAt { get; }

    public string? DiagnosticCode { get; }

    public DateTimeOffset? RequiresHumanAt { get; }
}

/// <summary>
/// The bounded deployment-run projection consumed by the health evaluator. It
/// mirrors only the existing run state contract and safe operational metadata.
/// </summary>
public sealed record ManagedLifecycleRunSnapshot
{
    public ManagedLifecycleRunSnapshot(
        Guid id,
        WorkspaceDeploymentRunStatus status,
        int attemptNumber,
        DateTimeOffset queuedAt,
        DateTimeOffset? startedAt = null,
        string? diagnosticCode = null,
        DateTimeOffset? heartbeatAt = null,
        DateTimeOffset? lastProgressAt = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Run ID is required.", nameof(id));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), "Deployment run status is invalid.");
        if (attemptNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Deployment run attempt number must be positive.");
        if (queuedAt == default)
            throw new ArgumentException("Run queued timestamp is required.", nameof(queuedAt));

        var queuedAtUtc = queuedAt.ToUniversalTime();
        var startedAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            startedAt, queuedAtUtc, nameof(startedAt));
        var heartbeatAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            heartbeatAt, queuedAtUtc, nameof(heartbeatAt));
        var lastProgressAtUtc = ManagedLifecycleOperationalHealthValue.NormalizeTimestamp(
            lastProgressAt, queuedAtUtc, nameof(lastProgressAt));

        Id = id;
        Status = status;
        AttemptNumber = attemptNumber;
        QueuedAt = queuedAtUtc;
        StartedAt = startedAtUtc;
        HeartbeatAt = heartbeatAtUtc;
        LastProgressAt = lastProgressAtUtc;
        DiagnosticCode = ManagedLifecycleOperationalHealthValue.OptionalDiagnosticCode(diagnosticCode, nameof(diagnosticCode));
    }

    public Guid Id { get; }

    public WorkspaceDeploymentRunStatus Status { get; }

    public int AttemptNumber { get; }

    public DateTimeOffset QueuedAt { get; }

    public DateTimeOffset? StartedAt { get; }

    public DateTimeOffset? HeartbeatAt { get; }

    public DateTimeOffset? LastProgressAt { get; }

    public string? DiagnosticCode { get; }
}

/// <summary>
/// Safe input to <see cref="ManagedLifecycleOperationalHealthEvaluator"/>. IDs are
/// opaque control-plane identities; all other values are existing enum/state values,
/// timestamps, or bounded stable diagnostic codes.
/// </summary>
public sealed record ManagedLifecycleOperationalHealthSnapshot
{
    public ManagedLifecycleOperationalHealthSnapshot(
        Guid workspaceId,
        Guid instanceId,
        ElsaDesiredLifecycle desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        ElsaInstanceProviderObservationKind? providerObservationKind = null,
        ManagedLifecycleOperationSnapshot? operation = null,
        ManagedLifecycleRunSnapshot? run = null,
        string? providerDiagnosticCode = null,
        DateTimeOffset? reconciledAt = null)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (!Enum.IsDefined(desiredLifecycle))
            throw new ArgumentOutOfRangeException(nameof(desiredLifecycle), "Desired lifecycle is invalid.");
        if (!Enum.IsDefined(observedLifecycle))
            throw new ArgumentOutOfRangeException(nameof(observedLifecycle), "Observed lifecycle is invalid.");
        if (!Enum.IsDefined(health))
            throw new ArgumentOutOfRangeException(nameof(health), "Instance health is invalid.");
        if (providerObservationKind is not null && !Enum.IsDefined(providerObservationKind.Value))
            throw new ArgumentOutOfRangeException(nameof(providerObservationKind), "Provider observation kind is invalid.");

        WorkspaceId = workspaceId;
        InstanceId = instanceId;
        DesiredLifecycle = desiredLifecycle;
        ObservedLifecycle = observedLifecycle;
        Health = health;
        ProviderObservationKind = providerObservationKind;
        Operation = operation;
        Run = run;
        ProviderDiagnosticCode = ManagedLifecycleOperationalHealthValue.OptionalDiagnosticCode(providerDiagnosticCode, nameof(providerDiagnosticCode));
        ReconciledAt = reconciledAt?.ToUniversalTime();
    }

    public Guid WorkspaceId { get; }

    public Guid InstanceId { get; }

    public ElsaDesiredLifecycle DesiredLifecycle { get; }

    public ElsaObservedLifecycle ObservedLifecycle { get; }

    public ElsaInstanceHealth Health { get; }

    public ElsaInstanceProviderObservationKind? ProviderObservationKind { get; }

    public ManagedLifecycleOperationSnapshot? Operation { get; }

    public ManagedLifecycleRunSnapshot? Run { get; }

    public string? ProviderDiagnosticCode { get; }

    public DateTimeOffset? ReconciledAt { get; }
}

/// <summary>Clock and policy bounds used by the pure evaluator.</summary>
public sealed record ManagedLifecycleOperationalHealthOptions
{
    public const string ConfigurationSection = "Deployment:ManagedLifecycleOperationalHealth";

    public TimeSpan OperationDeadline { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RunDeadline { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan ReconciliationDeadline { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxAttempts { get; init; } = 3;

    public void Validate()
    {
        if (OperationDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(OperationDeadline), "Operation deadline must be positive.");
        if (RunDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RunDeadline), "Run deadline must be positive.");
        if (ReconciliationDeadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReconciliationDeadline), "Reconciliation deadline must be positive.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "Maximum attempts must be positive.");
    }
}

/// <summary>A stable alert identity containing no provider or customer content.</summary>
public enum ManagedLifecycleOperationalHealthAlertSeverity
{
    Warning,
    Critical
}

public sealed record ManagedLifecycleOperationalHealthAlert
{
    internal ManagedLifecycleOperationalHealthAlert(
        string code,
        ManagedLifecycleOperationalHealthAlertSeverity severity,
        string dedupeIdentity)
    {
        Code = ManagedLifecycleOperationalHealthValue.DiagnosticCode(code, nameof(code));
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), "Alert severity is invalid.");
        if (string.IsNullOrWhiteSpace(dedupeIdentity) || dedupeIdentity.Length != 64 ||
            dedupeIdentity.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("Alert dedupe identity is invalid.", nameof(dedupeIdentity));
        Severity = severity;
        DedupeIdentity = dedupeIdentity;
    }

    public string Code { get; }

    public ManagedLifecycleOperationalHealthAlertSeverity Severity { get; }

    public string DedupeIdentity { get; }
}

/// <summary>Pure evaluation output; it contains only safe status and alert metadata.</summary>
public sealed record ManagedLifecycleOperationalHealthResult(
    ManagedLifecycleOperationalHealthStatus Status,
    string DiagnosticCode,
    string DedupeIdentity,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<ManagedLifecycleOperationalHealthAlert> Alerts);

internal static class ManagedLifecycleOperationalHealthValue
{
    public static string DiagnosticCode(string value, string parameterName)
    {
        if (!ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(value))
            throw new ArgumentException("Diagnostic code is invalid.", parameterName);
        return value;
    }

    public static string? OptionalDiagnosticCode(string? value, string parameterName) =>
        value is null ? null : DiagnosticCode(value, parameterName);

    public static DateTimeOffset? NormalizeTimestamp(
        DateTimeOffset? value,
        DateTimeOffset baseTimestamp,
        string parameterName)
    {
        if (value is not { } timestamp)
            return null;

        var utc = timestamp.ToUniversalTime();
        if (utc < baseTimestamp)
            throw new ArgumentException("Timestamp cannot precede its base timestamp.", parameterName);
        return utc;
    }
}

internal static class ManagedLifecycleOperationalHealthIdentity
{
    public static string Compute(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        ManagedLifecycleOperationalHealthStatus status,
        string diagnosticCode,
        string? alertCode = null)
    {
        var canonical = string.Join('\n',
            snapshot.WorkspaceId.ToString("D"),
            snapshot.InstanceId.ToString("D"),
            snapshot.Operation?.Id.ToString("D") ?? string.Empty,
            snapshot.Run?.Id.ToString("D") ?? string.Empty,
            status.ToString(),
            diagnosticCode,
            alertCode ?? string.Empty);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeAlert(
        ManagedLifecycleOperationalHealthSnapshot snapshot,
        string alertCode)
    {
        var canonical = string.Join('\n',
            snapshot.WorkspaceId.ToString("D"),
            snapshot.InstanceId.ToString("D"),
            snapshot.Operation?.Id.ToString("D") ?? string.Empty,
            snapshot.Run?.Id.ToString("D") ?? string.Empty,
            alertCode);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
