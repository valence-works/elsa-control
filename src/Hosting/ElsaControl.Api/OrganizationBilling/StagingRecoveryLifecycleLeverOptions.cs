using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.OrganizationBilling;

public static class StagingRecoveryLifecycleLeverDefaults
{
    public const string ConfigurationSection = "Staging:RecoveryLifecycleLever";
    public const string DisabledCode = "staging.recovery-lifecycle-lever.disabled";
    public const string InstanceNotAllowedCode = "staging.recovery-lifecycle-lever.instance-not-allowed";
    public const string TransitionCode = StagingRecoveryLifecycleLeverStoreDefaults.TransitionCode;
    public const string FiredEventType = StagingRecoveryLifecycleLeverStoreDefaults.FiredEventType;
    public const string RecoveryRequiredEventType = StagingRecoveryLifecycleLeverStoreDefaults.RecoveryRequiredEventType;
}

public sealed class StagingRecoveryLifecycleLeverOptions
{
    public const string ConfigurationSection = StagingRecoveryLifecycleLeverDefaults.ConfigurationSection;

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

    internal IEnumerable<string> Validate()
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

public sealed class StagingRecoveryLifecycleLeverConfigurationValidator(
    IOptions<StagingRecoveryLifecycleLeverOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.CompletedTask;

        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException(
                $"Staging recovery lifecycle lever configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
