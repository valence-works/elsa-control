namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Staging-only Workload-wait override so a late ARM Succeeded can auto-resume
/// with no Recover. Off by default. Billing and recovery lever flags never arm
/// this lever.
/// </summary>
public static class StagingLateSuccessLeverDefaults
{
    public const string ConfigurationSection = "Staging:LateSuccessLever";
    public const string AppliedCode = "staging.late-success-lever.applied";
    public static readonly TimeSpan DefaultWorkloadWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumWorkloadWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumWorkloadWait = TimeSpan.FromMinutes(5);
}

public sealed class StagingLateSuccessLeverOptions
{
    public const string ConfigurationSection = StagingLateSuccessLeverDefaults.ConfigurationSection;

    /// <summary>
    /// Master switch. Off by default. Staging may set this true through the
    /// deploy pipeline; committed appsettings files must keep it false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Workspace ids the lever may target. Empty by default. The lever is
    /// allowlist-only.
    /// </summary>
    public string[] AllowedWorkspaceIds { get; init; } = [];

    /// <summary>
    /// Optional Hosted smoke-owner workspace the lever must refuse even when it
    /// appears on the allowlist. Empty skips this extra deny.
    /// </summary>
    public string? SmokeOwnerWorkspaceId { get; set; }

    /// <summary>
    /// Workload-step wait used when the gate passes. Default 30 seconds; valid
    /// range 15 seconds through 5 minutes.
    /// </summary>
    public TimeSpan WorkloadWait { get; set; } = StagingLateSuccessLeverDefaults.DefaultWorkloadWait;

    public bool AllowsWorkspace(Guid workspaceId)
    {
        if (!Enabled || workspaceId == Guid.Empty || IsSmokeOwnerWorkspace(workspaceId))
            return false;

        foreach (var value in AllowedWorkspaceIds)
        {
            if (TryParseWorkspaceId(value, out var allowed) && allowed == workspaceId)
                return true;
        }

        return false;
    }

    public bool IsSmokeOwnerWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty || !TryParseWorkspaceId(SmokeOwnerWorkspaceId, out var smokeOwner))
            return false;

        return smokeOwner == workspaceId;
    }

    public IEnumerable<string> Validate()
    {
        if (WorkloadWait < StagingLateSuccessLeverDefaults.MinimumWorkloadWait ||
            WorkloadWait > StagingLateSuccessLeverDefaults.MaximumWorkloadWait)
        {
            yield return
                $"{ConfigurationSection}:WorkloadWait must be between {StagingLateSuccessLeverDefaults.MinimumWorkloadWait:c} and {StagingLateSuccessLeverDefaults.MaximumWorkloadWait:c}.";
        }

        var values = AllowedWorkspaceIds ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseWorkspaceId(values[index], out _))
            {
                yield return
                    $"{ConfigurationSection}:AllowedWorkspaceIds[{index}] must be a GUID workspace id.";
            }
        }

        if (!string.IsNullOrWhiteSpace(SmokeOwnerWorkspaceId) &&
            !TryParseWorkspaceId(SmokeOwnerWorkspaceId, out _))
        {
            yield return $"{ConfigurationSection}:SmokeOwnerWorkspaceId must be a GUID workspace id.";
        }
    }

    internal static bool TryParseWorkspaceId(string? value, out Guid workspaceId)
    {
        workspaceId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
            return false;

        return Guid.TryParse(value, out workspaceId) && workspaceId != Guid.Empty;
    }
}

/// <summary>
/// Evaluates the staging late-success Workload-wait override. Pipeline-target
/// and Stripe test-mode signals are supplied by the host so this type stays
/// free of billing configuration types.
/// </summary>
public sealed class StagingLateSuccessLever
{
    public static StagingLateSuccessLever Disabled { get; } = new(
        new StagingLateSuccessLeverOptions(),
        isPipelineTargetTest: false,
        isStripeTestMode: false);

    public StagingLateSuccessLever(
        StagingLateSuccessLeverOptions options,
        bool isPipelineTargetTest,
        bool isStripeTestMode)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        IsPipelineTargetTest = isPipelineTargetTest;
        IsStripeTestMode = isStripeTestMode;
    }

    public StagingLateSuccessLeverOptions Options { get; }
    public bool IsPipelineTargetTest { get; }
    public bool IsStripeTestMode { get; }

    public bool AppliesTo(Guid workspaceId) =>
        Options.Enabled &&
        IsPipelineTargetTest &&
        IsStripeTestMode &&
        Options.AllowsWorkspace(workspaceId);

    public TimeSpan WorkloadWaitLimit(TimeSpan configuredTimeout, Guid workspaceId)
    {
        if (configuredTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configuredTimeout), "The configured workload wait must be positive.");
        if (!AppliesTo(workspaceId))
            return configuredTimeout;

        return configuredTimeout < Options.WorkloadWait ? configuredTimeout : Options.WorkloadWait;
    }
}
