using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Reads the customer-safe provisioning projection for one visible managed instance.
/// Implementations resolve workspace and instance correlation server-side.
/// </summary>
public interface IManagedElsaProvisioningProgressReader
{
    Task<ManagedElsaProvisioningProgress?> ReadAsync(
        Guid workspaceId,
        Guid instanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The allowlisted, provider-neutral view of managed Create progress.
/// </summary>
public sealed record ManagedElsaProvisioningProgress(
    string State,
    string? Provider,
    string? CurrentStage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUpdatedAt,
    DateTimeOffset? CompletedAt,
    string? DiagnosticCode,
    IReadOnlyList<ManagedElsaProvisioningStage> Stages,
    IReadOnlyList<ManagedElsaProvisioningActivity> Activity);

/// <summary>A stable customer-facing provisioning stage.</summary>
public sealed record ManagedElsaProvisioningStage(
    string Code,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A stable customer-facing activity entry.</summary>
public sealed record ManagedElsaProvisioningActivity(
    long Sequence,
    string Stage,
    string Status,
    string MessageCode,
    DateTimeOffset OccurredAt);

/// <summary>Stable overall states exposed by the progress contract.</summary>
public static class ManagedElsaProvisioningProgressStates
{
    public const string Queued = "queued";
    public const string Active = "active";
    public const string Stale = "stale";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

/// <summary>Stable ordered stage codes exposed by the progress contract.</summary>
public static class ManagedElsaProvisioningProgressStages
{
    public const string RequestAccepted = "request-accepted";
    public const string HostingFoundation = "hosting-foundation";
    public const string Configuration = "configuration";
    public const string RuntimeDeployment = "runtime-deployment";
    public const string HealthVerification = "health-verification";
    public const string TrafficRouting = "traffic-routing";
    public const string Ready = "ready";

    public static IReadOnlyList<string> Ordered { get; } =
    [
        RequestAccepted,
        HostingFoundation,
        Configuration,
        RuntimeDeployment,
        HealthVerification,
        TrafficRouting,
        Ready
    ];
}

/// <summary>Stable stage states exposed by the progress contract.</summary>
public static class ManagedElsaProvisioningProgressStageStatuses
{
    public const string Pending = "pending";
    public const string Current = "current";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Unknown = "unknown";
}

/// <summary>Stable activity states exposed by the progress contract.</summary>
public static class ManagedElsaProvisioningProgressActivityStatuses
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Ready = "ready";
}

/// <summary>Stable diagnostics exposed by the progress contract.</summary>
public static class ManagedElsaProvisioningProgressDiagnostics
{
    public const string RequiresAttention = "provisioning.requires-attention";
    public const string Failed = "provisioning.failed";
    public const string Cancelled = "provisioning.cancelled";
    public const string HistoryUnavailable = "provisioning.history-unavailable";
}
