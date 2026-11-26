using System.Collections.Concurrent;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Staging-only Health-step inject so QA can prove crash-loop and health-timeout
/// reasons without a hand Azure edit. Off by default. Billing, recovery, and
/// late-success lever flags never arm this lever.
/// </summary>
public static class StagingProvisioningHealthLeverDefaults
{
    public const string ConfigurationSection = "Staging:ProvisioningHealthLever";
    public const string AppliedCode = "staging.provisioning-health-lever.applied";
    public const string DisabledCode = "staging.provisioning-health-lever.disabled";
    public const string InstanceNotAllowedCode = "staging.provisioning-health-lever.instance-not-allowed";
    public const string CrashLoopMode = "crash-loop";
    public const string TimeoutMode = "health-timeout";
    public const string CrashLoopCode = "staging.provisioning-health-lever.crash-loop";
    public const string TimeoutCode = "staging.provisioning-health-lever.health-timeout";
    public const string ResetCode = "staging.provisioning-health-lever.reset";
}

public enum StagingProvisioningHealthFailureMode
{
    None = 0,
    CrashLoop = 1,
    Timeout = 2
}

public enum StagingProvisioningHealthLeverOutcome
{
    Armed,
    Reset,
    Disabled,
    InstanceNotAllowed
}

public sealed class StagingProvisioningHealthLeverOptions
{
    public const string ConfigurationSection = StagingProvisioningHealthLeverDefaults.ConfigurationSection;

    /// <summary>
    /// Master switch. Off by default. Staging may set this true through the
    /// deploy pipeline; committed appsettings files must keep it false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Instance ids the lever may target. Empty by default. The lever is
    /// allowlist-only; there is no synthetic-marker fallback.
    /// </summary>
    public string[] AllowedInstanceIds { get; init; } = [];

    /// <summary>
    /// Optional Hosted smoke-owner instance the lever must refuse even when it
    /// appears on the allowlist. Empty skips this extra deny.
    /// </summary>
    public string? SmokeOwnerInstanceId { get; set; }

    public bool AllowsInstance(Guid instanceId)
    {
        if (!Enabled || instanceId == Guid.Empty || IsSmokeOwnerInstance(instanceId))
            return false;

        foreach (var value in AllowedInstanceIds)
        {
            if (TryParseInstanceId(value, out var allowed) && allowed == instanceId)
                return true;
        }

        return false;
    }

    public bool IsSmokeOwnerInstance(Guid instanceId)
    {
        if (instanceId == Guid.Empty || !TryParseInstanceId(SmokeOwnerInstanceId, out var smokeOwner))
            return false;

        return smokeOwner == instanceId;
    }

    public IEnumerable<string> Validate()
    {
        var values = AllowedInstanceIds ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseInstanceId(values[index], out _))
            {
                yield return
                    $"{ConfigurationSection}:AllowedInstanceIds[{index}] must be a GUID instance id.";
            }
        }

        if (!string.IsNullOrWhiteSpace(SmokeOwnerInstanceId) &&
            !TryParseInstanceId(SmokeOwnerInstanceId, out _))
        {
            yield return $"{ConfigurationSection}:SmokeOwnerInstanceId must be a GUID instance id.";
        }
    }

    internal static bool TryParseInstanceId(string? value, out Guid instanceId)
    {
        instanceId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
            return false;

        return Guid.TryParse(value, out instanceId) && instanceId != Guid.Empty;
    }
}

/// <summary>
/// Evaluates the staging Health-step inject. Stripe test-mode is supplied by
/// the host so this type stays free of billing configuration types. Armed
/// modes are process-local so a POST can select crash-loop vs timeout without
/// another deploy.
/// </summary>
public sealed class StagingProvisioningHealthLever
{
    public static StagingProvisioningHealthLever Disabled { get; } = new(
        new StagingProvisioningHealthLeverOptions(),
        isStripeTestMode: false);

    private readonly ConcurrentDictionary<Guid, StagingProvisioningHealthFailureMode> _armed = new();

    public StagingProvisioningHealthLever(
        StagingProvisioningHealthLeverOptions options,
        bool isStripeTestMode)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        IsStripeTestMode = isStripeTestMode;
    }

    public StagingProvisioningHealthLeverOptions Options { get; }
    public bool IsStripeTestMode { get; }

    public bool AppliesTo(Guid instanceId) =>
        Options.Enabled &&
        IsStripeTestMode &&
        Options.AllowsInstance(instanceId);

    public bool TryGetArmedFailure(Guid instanceId, out StagingProvisioningHealthFailureMode mode)
    {
        mode = StagingProvisioningHealthFailureMode.None;
        if (!AppliesTo(instanceId))
            return false;
        if (!_armed.TryGetValue(instanceId, out var armed) ||
            armed is not (StagingProvisioningHealthFailureMode.CrashLoop or StagingProvisioningHealthFailureMode.Timeout))
            return false;

        mode = armed;
        return true;
    }

    public StagingProvisioningHealthLeverOutcome Arm(
        Guid instanceId,
        StagingProvisioningHealthFailureMode mode)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (mode is not (StagingProvisioningHealthFailureMode.CrashLoop or StagingProvisioningHealthFailureMode.Timeout))
            throw new ArgumentOutOfRangeException(nameof(mode), "The staging provisioning health lever mode is not allowed.");

        if (!Options.Enabled || !IsStripeTestMode)
            return StagingProvisioningHealthLeverOutcome.Disabled;
        if (!Options.AllowsInstance(instanceId))
            return StagingProvisioningHealthLeverOutcome.InstanceNotAllowed;

        _armed[instanceId] = mode;
        return StagingProvisioningHealthLeverOutcome.Armed;
    }

    public StagingProvisioningHealthLeverOutcome Reset(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));

        if (!Options.Enabled || !IsStripeTestMode)
            return StagingProvisioningHealthLeverOutcome.Disabled;
        if (!Options.AllowsInstance(instanceId))
            return StagingProvisioningHealthLeverOutcome.InstanceNotAllowed;

        _armed.TryRemove(instanceId, out _);
        return StagingProvisioningHealthLeverOutcome.Reset;
    }

    public static string ModeName(StagingProvisioningHealthFailureMode mode) => mode switch
    {
        StagingProvisioningHealthFailureMode.CrashLoop => StagingProvisioningHealthLeverDefaults.CrashLoopMode,
        StagingProvisioningHealthFailureMode.Timeout => StagingProvisioningHealthLeverDefaults.TimeoutMode,
        _ => "none"
    };

    public static string OutcomeCode(
        StagingProvisioningHealthLeverOutcome outcome,
        StagingProvisioningHealthFailureMode mode = StagingProvisioningHealthFailureMode.None) =>
        outcome switch
        {
            StagingProvisioningHealthLeverOutcome.Armed when mode == StagingProvisioningHealthFailureMode.CrashLoop =>
                StagingProvisioningHealthLeverDefaults.CrashLoopCode,
            StagingProvisioningHealthLeverOutcome.Armed when mode == StagingProvisioningHealthFailureMode.Timeout =>
                StagingProvisioningHealthLeverDefaults.TimeoutCode,
            StagingProvisioningHealthLeverOutcome.Reset => StagingProvisioningHealthLeverDefaults.ResetCode,
            StagingProvisioningHealthLeverOutcome.Disabled => StagingProvisioningHealthLeverDefaults.DisabledCode,
            StagingProvisioningHealthLeverOutcome.InstanceNotAllowed =>
                StagingProvisioningHealthLeverDefaults.InstanceNotAllowedCode,
            _ => throw new InvalidOperationException("Unsupported staging provisioning health lever outcome.")
        };
}
