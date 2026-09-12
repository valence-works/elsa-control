namespace ElsaControl.Deployment.Azure;

/// <summary>
/// One CPU and memory pair the Azure Container Apps consumption profile accepts for a replica,
/// in plan units and in the template representation, with the ephemeral storage the platform
/// allocates for that CPU. Consumption storage is derived from CPU and cannot be set.
/// </summary>
public sealed record AzureContainerAppsConsumptionSize(
    int CpuMillicores,
    int MemoryMiB,
    string Cpu,
    string Memory,
    int EphemeralStorageMiB);

/// <summary>
/// Maps governed workload capacity to the Azure Container Apps consumption profile exactly.
/// Capacity outside these sizes or replica bounds has no mapping: callers fail closed instead
/// of rounding to a neighbouring size or falling back to template defaults.
/// </summary>
public static class AzureContainerAppsCapacity
{
    /// <summary>
    /// Conservative replica ceiling. The platform limit has been raised over time; this is the
    /// value every documented revision accepts, and the template enforces the same bound.
    /// </summary>
    public const int MaximumReplicas = 300;

    /// <summary>
    /// The CPU/memory pairs valid in both consumption-only and workload-profile environments.
    /// Larger consumption sizes exist only in workload-profile environments and are excluded.
    /// </summary>
    public static IReadOnlyList<AzureContainerAppsConsumptionSize> ConsumptionSizes { get; } = Array.AsReadOnly(
    [
        new AzureContainerAppsConsumptionSize(250, 512, "0.25", "0.5Gi", 1024),
        new AzureContainerAppsConsumptionSize(500, 1024, "0.5", "1Gi", 2048),
        new AzureContainerAppsConsumptionSize(750, 1536, "0.75", "1.5Gi", 4096),
        new AzureContainerAppsConsumptionSize(1000, 2048, "1", "2Gi", 4096),
        new AzureContainerAppsConsumptionSize(1250, 2560, "1.25", "2.5Gi", 8192),
        new AzureContainerAppsConsumptionSize(1500, 3072, "1.5", "3Gi", 8192),
        new AzureContainerAppsConsumptionSize(1750, 3584, "1.75", "3.5Gi", 8192),
        new AzureContainerAppsConsumptionSize(2000, 4096, "2", "4Gi", 8192)
    ]);

    /// <summary>
    /// Returns the exact consumption size for <paramref name="capacity"/>, or <see langword="null"/>
    /// when its CPU/memory pair or replica bounds have no Container Apps mapping.
    /// </summary>
    public static AzureContainerAppsConsumptionSize? Map(AzureWorkloadCapacity? capacity) =>
        capacity is { MinReplicas: >= 0, MaxReplicas: >= 1 and <= MaximumReplicas } &&
        capacity.MinReplicas <= capacity.MaxReplicas
            ? ConsumptionSizes.SingleOrDefault(size =>
                size.CpuMillicores == capacity.CpuMillicores && size.MemoryMiB == capacity.MemoryMiB)
            : null;
}
