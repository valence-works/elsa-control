using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// The capacity outcome the plan resolver emits for the default standard-small profile. Azure
/// admission requires one for the workload component, as every resolved managed plan carries.
/// </summary>
internal static class GovernedTestCapacity
{
    public static IReadOnlyList<ResolvedComponentCapacity> StandardSmall(string componentId)
    {
        var profile = ElsaInstancePlanResolutionOptions.Default.EffectiveCapacityProfiles["standard-small"];
        return [new(componentId, profile.MinReplicas, profile.MaxReplicas, profile.CpuMillicores, profile.MemoryMiB, profile.EphemeralStorageMiB)];
    }
}
