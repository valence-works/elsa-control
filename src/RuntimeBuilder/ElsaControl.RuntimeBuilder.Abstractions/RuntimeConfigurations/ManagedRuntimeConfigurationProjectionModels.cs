using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;

/// <summary>
/// A safe projection of a saved Builder intent for the managed instance resolver.
/// Builder export choices are intentionally reported as findings rather than being
/// allowed to cross the provider-neutral lifecycle boundary.
/// </summary>
public sealed record ManagedRuntimeConfigurationProjection(
    RuntimeBuilderIntent? BuilderIntent,
    bool CanProvision,
    IReadOnlyList<ElsaInstancePlanResolutionFinding> Findings);
