using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Azure;

public sealed class AzureEngineProvisioningModule : IEngineProvisioningModule
{
    public string Id => "azure";

    public string DisplayName => "Azure";

    public IReadOnlyList<ElsaInstancePlanResolutionFinding> ValidatePlan(
        ResolvedElsaApplicationPlan plan,
        string region)
    {
        // Preview has no instance ID yet, so it cannot validate a caller
        // supplied target fingerprint. Use the provider's deterministic
        // workload-name shape as a representative target; submission
        // revalidates the resolved plan against the actual generated name.
        var translation = AzureWorkloadPlanTranslator.Translate(
            plan,
            new AzureWorkloadTarget(AzureElsaInstanceProvider.WorkloadName(Guid.Empty), region));
        return translation.Findings
            .Select(finding => ElsaInstancePlanResolutionFinding.Error(finding.Code, finding.Message, finding.Scope))
            .ToArray();
    }
}
