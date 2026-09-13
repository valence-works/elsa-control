using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Core.Provisioning;

/// <summary>
/// Describes an engine provisioning capability contributed by an enabled provider module.
/// </summary>
public interface IEngineProvisioningModule
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// Validates a resolved provider-neutral plan against this provider's pure
    /// admission rules before a provisioning request can be accepted. The
    /// validation target is deliberately provider-neutral: no caller-supplied
    /// slug or provider target fingerprint is accepted here. The lifecycle
    /// worker derives the provider's concrete target from the instance ID when
    /// it submits the plan.
    /// Providers that do not contribute a validator fail closed.
    /// </summary>
    IReadOnlyList<ElsaInstancePlanResolutionFinding> ValidatePlan(
        ResolvedElsaApplicationPlan plan,
        string region) =>
    [
        ElsaInstancePlanResolutionFinding.Error(
            "provider.validation-unavailable",
            "The provisioning provider cannot validate the resolved plan before deployment.",
            "provider")
    ];
}
