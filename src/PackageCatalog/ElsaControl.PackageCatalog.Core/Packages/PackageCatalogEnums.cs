namespace ElsaControl.PackageCatalog.Core.Packages;

public enum PackageSourceType
{
    NuGetFeed
}

public enum PackageSourceApprovalPolicy
{
    AutoApprove,
    Manual
}

public enum PackageSourceVersionDiscoveryPolicy
{
    AllVersions,
    LatestStable,
    LatestIncludingPrerelease,
    LatestPreview
}

public enum PackageSourceStatus
{
    Healthy,
    Warning,
    Error
}

public enum PackageApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

public enum ValidationStatus
{
    NotValidated,
    Valid,
    Invalid,
    UnsupportedSchema,
    Suspicious
}

public enum SyncRunTrigger
{
    Scheduled,
    ManualAll,
    ManualSource,
    ManualPackage
}

/// <remarks>
/// Persisted as its numeric value. <see cref="Verification"/> must stay 0: the column default classifies runs
/// recorded before modes existed, and runs written by older binaries, as verification runs, which is what they were.
/// </remarks>
public enum SyncRunMode
{
    /// <summary>Downloads and reads every discovered version, so stored manifests are re-checked for suspicious republishes.</summary>
    Verification,

    /// <summary>
    /// Downloads and reads only versions that are not stored yet and that the last verification run did not find without
    /// elsa-package.json. Stored versions are recorded as unchanged, and versions found without a manifest as invalid, without being re-read.
    /// </summary>
    NewVersionsOnly
}

public enum SyncRunStatus
{
    Running,
    Completed,
    Failed,
    CompletedWithErrors,
    Canceled
}

public enum SyncRunItemStatus
{
    Discovered,
    Skipped,
    Downloaded,
    Indexed,
    Unchanged,
    Invalid,
    Failed,
    Suspicious
}

public enum ApprovalTargetType
{
    Package,
    PackageVersion
}
