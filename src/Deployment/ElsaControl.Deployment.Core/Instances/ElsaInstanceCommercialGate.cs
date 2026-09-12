using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Provider-neutral commercial decision used at every managed-instance mutation
/// boundary. The decision contains only stable, value-free diagnostics.
/// </summary>
public sealed record ElsaInstanceCommercialGateDecision(bool Allowed, string Code, string Summary)
{
    public static ElsaInstanceCommercialGateDecision Allow() =>
        new(true, "commercial.allowed", "The managed-instance operation is entitled.");
}

public interface IElsaInstanceCommercialGate
{
    Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
        Guid organizationId,
        ElsaInstanceOperationAction action,
        int? activeInstanceCount = null,
        CancellationToken cancellationToken = default);
}

public static class ElsaInstanceCommercialOperation
{
    public const string EntitlementRequired = "instance.entitlement-required";
    public const string EntitlementExpired = "instance.entitlement-expired";
    public const string SubscriptionStateRequired = "instance.subscription-state-required";
    public const string LifecycleConstrained = "instance.lifecycle-constrained";
    public const string InstanceLimitReached = "instance.instance-limit-reached";
    public const string BindingRequired = "instance.binding-required";
    public const string EntitlementSafeExitSuperseded = "instance.entitlement-safe-exit-superseded";
}
