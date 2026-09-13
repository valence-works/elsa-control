using System.Diagnostics;
using System.Diagnostics.Metrics;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Telemetry;

/// <summary>
/// The provider-neutral telemetry contract for managed instance lifecycle work.
///
/// The source and meter are deliberately defined in Core so hosts can opt in to
/// them later. This type owns the complete tag allow-list; callers cannot attach
/// operation, tenant, provider or diagnostic payload values accidentally.
/// </summary>
public static class ManagedLifecycleTelemetry
{
    public const string ActivitySourceName = "ElsaControl.ManagedLifecycle";
    public const string MeterName = "ElsaControl.ManagedLifecycle";

    public const string WorkerActivityName = "managed_lifecycle.worker";
    public const string ReconciliationActivityName = "managed_lifecycle.reconciliation";

    public const string CompletionCounterName = "managed_lifecycle.operations.completed";
    public const string ErrorCounterName = "managed_lifecycle.operations.errors";
    public const string TransitionCounterName = "managed_lifecycle.operations.transitions";
    public const string RetryCounterName = "managed_lifecycle.operations.retries";
    public const string DurationHistogramName = "managed_lifecycle.operations.duration";
    public const string EndpointHealthCounterName = "managed_lifecycle.endpoint.health.evaluations";

    public const string ActionTag = "action";
    public const string OutcomeTag = "outcome";
    public const string DesiredLifecycleTag = "desired_lifecycle";
    public const string ObservedLifecycleTag = "observed_lifecycle";
    public const string HealthTag = "health";
    public const string OperationStateTag = "operation_state";
    public const string DiagnosticCodeTag = "diagnostic_code";
    public const string OrganizationIdTag = "organization.id";
    public const string WorkspaceIdTag = "workspace.id";
    public const string InstanceIdTag = "instance.id";
    public const string OperationIdTag = "operation.id";

    private const string Unknown = "unknown";
    private const string None = "none";
    private static readonly HashSet<string> KnownDiagnosticCodes =
    [
        "lifecycle.claim.conflict",
        "lifecycle.worker.cancelled",
        "lifecycle.worker.failed",
        "provider.submission.rejected",
        "provider.submission.uncertain",
        "resolution.failed",
        "resolution.invalid",
        "resolution.input-unavailable",
        "resolution.work-item-invalid",
        "resolution.commit-invalid",
        "resolution.plan-replace-invalid",
        "resolution.unexpected",
        "catalog.unavailable",
        "release.version.mismatch",
        "release.version.lineMismatch",
        "manifest.projection.invalid",
        "plan.invalid",
        "run.reservation.conflict",
        "provider.reconciliation.ambiguous",
        "provider.reconciliation.cancelled",
        "provider.reconciliation.converged",
        "provider.reconciliation.correlation-mismatch",
        "provider.reconciliation.failed",
        "provider.reconciliation.health-failed",
        "provider.reconciliation.health-unknown",
        "provider.reconciliation.in-progress",
        "provider.reconciliation.retry-safe",
        "provider.reconciliation.unavailable",
        "provider.reconciliation.unknown"
    ];

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> CompletionCounter = Meter.CreateCounter<long>(
        CompletionCounterName,
        "{operation}",
        "Completed provider-neutral managed lifecycle operations.");
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>(
        ErrorCounterName,
        "{error}",
        "Provider-neutral managed lifecycle errors that crossed an operation boundary.");
    private static readonly Counter<long> TransitionCounter = Meter.CreateCounter<long>(
        TransitionCounterName,
        "{transition}",
        "Provider-neutral managed lifecycle operation state transitions.");
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>(
        RetryCounterName,
        "{retry}",
        "Managed lifecycle operation attempts after the first attempt.");
    private static readonly Histogram<long> DurationHistogram = Meter.CreateHistogram<long>(
        DurationHistogramName,
        "ms",
        "Managed lifecycle operation duration in milliseconds.");
    private static readonly Counter<long> EndpointHealthCounter = Meter.CreateCounter<long>(
        EndpointHealthCounterName,
        "{evaluation}",
        "Provider-neutral endpoint health evaluations.");

    public static ManagedLifecycleTelemetryOperation StartOperation(
        string activityName,
        ElsaInstanceOperationAction action,
        ElsaDesiredLifecycle? desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        ElsaInstanceOperationState? operationState,
        Guid? organizationId = null,
        Guid? workspaceId = null,
        Guid? instanceId = null,
        Guid? operationId = null,
        int attemptNumber = 1)
    {
        var activity = ActivitySource.StartActivity(activityName, ActivityKind.Internal);
        ApplyCorrelation(activity, organizationId, workspaceId, instanceId, operationId);
        var operation = new ManagedLifecycleTelemetryOperation(
            activity,
            action,
            activityName == ReconciliationActivityName);
        operation.SetInitialState(desiredLifecycle, observedLifecycle, health, operationState);
        if (attemptNumber > 1)
            operation.RecordRetry(desiredLifecycle, observedLifecycle, health, operationState);
        return operation;
    }

    internal static TagList Tags(
        ElsaInstanceOperationAction action,
        string outcome,
        ElsaDesiredLifecycle? desiredLifecycle,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        ElsaInstanceOperationState? operationState,
        string? diagnosticCode) =>
        new()
        {
            { ActionTag, EnumValue(action) },
            { OutcomeTag, OutcomeValue(outcome) },
            { DesiredLifecycleTag, desiredLifecycle is { } desired ? EnumValue(desired) : Unknown },
            { ObservedLifecycleTag, EnumValue(observedLifecycle) },
            { HealthTag, EnumValue(health) },
            { OperationStateTag, operationState is { } state ? EnumValue(state) : Unknown },
            { DiagnosticCodeTag, DiagnosticValue(diagnosticCode) }
        };

    private static TagList ToMetricTags(in TagList tags)
    {
        var metricTags = new TagList();
        foreach (var tag in tags)
        {
            if (tag.Key != DiagnosticCodeTag)
                metricTags.Add(tag.Key, tag.Value);
        }

        return metricTags;
    }

    private static void Apply(Activity? activity, in TagList tags)
    {
        if (activity is null)
            return;

        foreach (var tag in tags)
            activity.SetTag(tag.Key, tag.Value);
    }

    private static void ApplyCorrelation(
        Activity? activity,
        Guid? organizationId,
        Guid? workspaceId,
        Guid? instanceId,
        Guid? operationId)
    {
        if (activity is null)
            return;

        SetOpaqueId(activity, OrganizationIdTag, organizationId);
        SetOpaqueId(activity, WorkspaceIdTag, workspaceId);
        SetOpaqueId(activity, InstanceIdTag, instanceId);
        SetOpaqueId(activity, OperationIdTag, operationId);
    }

    private static void SetOpaqueId(Activity activity, string key, Guid? value)
    {
        if (value is { } id && id != Guid.Empty)
            activity.SetTag(key, id.ToString("D"));
    }

    private static string EnumValue<T>(T value) where T : struct, Enum =>
        !Enum.IsDefined(typeof(T), value)
            ? Unknown
            : value.ToString() switch
        {
            { Length: 0 } => Unknown,
            var text => ToSnakeCase(text)
        };

    private static string OutcomeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Unknown;

        var normalized = ToSnakeCase(value.Trim());
        return normalized is
            "queued" or "failed" or "already_completed" or "waiting_for_prior_operation" or
            "conflict" or "deleted" or "converged" or "recovery_required" or
            "health_gate_failed" or "succeeded" or "transition" or "error" or "retry" or "unknown"
            ? normalized
            : Unknown;
    }

    private static string DiagnosticValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return None;

        var normalized = value.Trim();
        return KnownDiagnosticCodes.Contains(normalized) ? normalized : Unknown;
    }

    private static string ToSnakeCase(string value)
    {
        if (value.Length == 0)
            return Unknown;

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character) || character is '-' or '.')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
                continue;
            }

            if (character is >= 'A' and <= 'Z')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (char.IsAsciiLetterOrDigit(character) || character == '_')
                builder.Append(char.ToLowerInvariant(character));
            else
                return Unknown;
        }

        return builder.Length == 0 ? Unknown : builder.ToString();
    }

    public sealed class ManagedLifecycleTelemetryOperation : IDisposable
    {
        private readonly Activity? _activity;
        private readonly ElsaInstanceOperationAction _action;
        private readonly bool _recordsEndpointHealth;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private TagList _lastTags;
        private bool _errorRecorded;
        private bool _durationRecorded;
        private bool _disposed;

        internal ManagedLifecycleTelemetryOperation(
            Activity? activity,
            ElsaInstanceOperationAction action,
            bool recordsEndpointHealth)
        {
            _activity = activity;
            _action = action;
            _recordsEndpointHealth = recordsEndpointHealth;
        }

        internal void SetInitialState(
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState)
        {
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                Unknown,
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                null);
            Apply(_activity, tags);
            _lastTags = ManagedLifecycleTelemetry.ToMetricTags(tags);
        }

        public void Complete(
            string outcome,
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState,
            string? diagnosticCode = null)
        {
            EnsureActive();
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                outcome,
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                diagnosticCode);
            Apply(_activity, tags);
            var metricTags = ManagedLifecycleTelemetry.ToMetricTags(tags);
            CompletionCounter.Add(1, metricTags);
            if (_recordsEndpointHealth)
                EndpointHealthCounter.Add(1, metricTags);
            RecordDuration(metricTags);
            _activity?.SetStatus(_errorRecorded ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        }

        public void CompleteReplay(
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState,
            string? diagnosticCode = null)
        {
            EnsureActive();
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                "already_completed",
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                diagnosticCode);
            Apply(_activity, tags);
            _lastTags = ManagedLifecycleTelemetry.ToMetricTags(tags);
            _durationRecorded = true;
            _activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public void RecordError(
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState,
            string? diagnosticCode = null)
        {
            EnsureActive();
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                "error",
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                diagnosticCode);
            Apply(_activity, tags);
            var metricTags = ManagedLifecycleTelemetry.ToMetricTags(tags);
            ErrorCounter.Add(1, metricTags);
            _lastTags = metricTags;
            _errorRecorded = true;
            _activity?.SetStatus(ActivityStatusCode.Error);
        }

        public void RecordTransition(
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState,
            string? diagnosticCode = null)
        {
            EnsureActive();
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                "transition",
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                diagnosticCode);
            var metricTags = ManagedLifecycleTelemetry.ToMetricTags(tags);
            TransitionCounter.Add(1, metricTags);
            _lastTags = metricTags;
        }

        public void SetCorrelation(
            Guid? organizationId,
            Guid? workspaceId,
            Guid? instanceId,
            Guid? operationId) =>
            ApplyCorrelation(_activity, organizationId, workspaceId, instanceId, operationId);

        internal void RecordRetry(
            ElsaDesiredLifecycle? desiredLifecycle,
            ElsaObservedLifecycle observedLifecycle,
            ElsaInstanceHealth health,
            ElsaInstanceOperationState? operationState)
        {
            var tags = ManagedLifecycleTelemetry.Tags(
                _action,
                "retry",
                desiredLifecycle,
                observedLifecycle,
                health,
                operationState,
                null);
            RetryCounter.Add(1, ManagedLifecycleTelemetry.ToMetricTags(tags));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            RecordDuration(_lastTags);
            _activity?.Stop();
        }

        private void RecordDuration(in TagList tags)
        {
            if (_durationRecorded)
                return;

            _durationRecorded = true;
            DurationHistogram.Record(
                Math.Max(0, (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds),
                tags);
        }

        private void EnsureActive()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ManagedLifecycleTelemetryOperation));
        }
    }
}
