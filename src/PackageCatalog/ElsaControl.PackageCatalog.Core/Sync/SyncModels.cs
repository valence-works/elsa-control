using ElsaControl.PackageCatalog.Core.Packages;

namespace ElsaControl.PackageCatalog.Core.Sync;

public sealed class ManifestValidationResultRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackageVersionId { get; set; }
    public PackageVersion? PackageVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public ValidationStatus Status { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public string WarningsJson { get; set; } = "[]";
    public DateTimeOffset ValidatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ValidatorVersion { get; set; }
}

public sealed class ApprovalRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ApprovalTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }
    public PackageApprovalStatus Status { get; set; }
    public string? Reason { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SyncRunTrigger Trigger { get; set; }
    public SyncRunMode Mode { get; set; } = SyncRunMode.Verification;
    public SyncRunStatus Status { get; set; } = SyncRunStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string SummaryCountersJson { get; set; } = "{}";
    public List<SyncRunItem> Items { get; set; } = [];
}

public sealed class SyncRunItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncRunId { get; set; }
    public SyncRun? SyncRun { get; set; }
    public Guid? SourceId { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public Guid? PackageVersionId { get; set; }
    public PackageVersion? PackageVersion { get; set; }
    public SyncRunItemStatus Status { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? WarningsJson { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
