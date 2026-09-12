using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Azure placement facts supplied below the provider-neutral resolved-plan boundary.
/// </summary>
public sealed record AzureWorkloadTarget(string WorkloadName, string Location);

/// <summary>
/// Deterministic, secret-safe intent consumed by the Azure Bicep lifecycle adapter.
/// It deliberately describes inputs rather than Azure resource shape.
/// <see cref="ManagedHandoff"/> is true when this deployment configures the runtime's managed Elsa handoff:
/// the admitted release declares <c>managed-elsa-handoff-v1</c> and the workload runs as exactly one replica,
/// which the runtime's in-process handoff state and session store require.
/// </summary>
public sealed record AzureWorkloadPlan(
    string WorkloadName,
    string Location,
    string ElsaVersion,
    string ReleaseLine,
    string Topology,
    string Isolation,
    string ImageRepository,
    string ImageDigest,
    string ReleaseManifestReference,
    string ReleaseManifestDigest,
    string ReleaseManifestSignatureReference,
    string ReleaseManifestSignatureDigest,
    IReadOnlyDictionary<string, string> SecretReferences,
    string Fingerprint,
    string? SqlWorkflowPackageVersion = null,
    string? SqlQuartzPackageVersion = null,
    AzureWorkloadCapacity? Capacity = null,
    bool ManagedHandoff = false);

/// <summary>
/// Governed sizing of the single workload container, in the resolved plan's own units.
/// <see cref="AzureContainerAppsCapacity"/> owns the exact Container Apps representation;
/// a capacity without one is never approximated.
/// </summary>
public sealed record AzureWorkloadCapacity(int MinReplicas, int MaxReplicas, int CpuMillicores, int MemoryMiB);

public sealed record AzureWorkloadPlanTranslation(
    AzureWorkloadPlan? Plan,
    IReadOnlyList<ResolvedPlanValidationFinding> Findings)
{
    public bool IsAccepted => Plan is not null && Findings.Count == 0;
}
