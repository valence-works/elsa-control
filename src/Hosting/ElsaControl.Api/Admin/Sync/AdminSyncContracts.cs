using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;

namespace ElsaControl.Api.Admin.Sync;

public sealed record AdminSyncRunResponse(
    Guid Id,
    SyncRunTrigger Trigger,
    SyncRunMode Mode,
    SyncRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string SummaryCountersJson,
    int ItemCount,
    IReadOnlyList<AdminSyncRunSourceResponse> Sources,
    IReadOnlyList<AdminSyncRunItemResponse> Items);

public sealed record AdminSyncRunSourceResponse(
    Guid Id,
    string? Name);

public sealed record AdminSyncRunItemResponse(
    Guid Id,
    Guid? SourceId,
    string? PackageId,
    string? Version,
    SyncRunItemStatus Status,
    string? Message,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record AdminSyncRunCleanupPreviewResponse(
    DateTimeOffset CompletedBefore,
    int EligibleRunCount,
    int EligibleItemCount,
    int ExcludedRunCount,
    DateTimeOffset? OldestEligibleCompletedAt,
    DateTimeOffset? NewestEligibleCompletedAt);

public sealed record AdminSyncRunCleanupResultResponse(
    int DeletedRunCount,
    int DeletedItemCount,
    int ExcludedRunCount,
    int NotFoundRunCount,
    DateTimeOffset? CompletedBefore,
    IReadOnlyList<Guid> DeletedRunIds);
