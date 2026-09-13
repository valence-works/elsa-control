using ElsaControl.Deployment.Azure;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureEngineProvisioningModuleTests
{
    private readonly AzureEngineProvisioningModule module = new();

    [Fact]
    public void Accepts_a_supported_resolved_plan()
    {
        var findings = module.ValidatePlan(
            AzureWorkloadPlanTranslatorTests.CreatePlan(),
            "westeurope");

        Assert.Empty(findings);
    }

    [Fact]
    public void Rejects_restricted_egress_before_provisioning()
    {
        var plan = AzureWorkloadPlanTranslatorTests.CreatePlan();
        var findings = module.ValidatePlan(
            plan with { Network = plan.Network with { Egress = "restricted" } },
            "westeurope");

        Assert.Contains(findings, finding => finding.Code == "azure.network.unsupported");
        Assert.All(findings, finding => Assert.Equal("error", finding.Severity));
    }

    [Fact]
    public void Rejects_unsupported_release_evidence_before_provisioning()
    {
        var plan = AzureWorkloadPlanTranslatorTests.CreatePlan();
        var findings = module.ValidatePlan(
            plan with
            {
                Evidence = plan.Evidence
                    .Select(evidence => evidence.Kind == ReleaseManifestEvidenceKinds.Manifest
                        ? evidence with { Reference = "oci://other-release/manifest" }
                        : evidence)
                    .ToArray()
            },
            "westeurope");

        Assert.Contains(findings, finding => finding.Code == "azure.releaseManifestEvidence.mismatch");
    }

    [Fact]
    public void Rejects_unsupported_provider_settings_before_provisioning()
    {
        var plan = AzureWorkloadPlanTranslatorTests.CreatePlan();
        var findings = module.ValidatePlan(
            plan with { Isolation = "Shared" },
            "westeurope");

        Assert.Contains(findings, finding => finding.Code == "azure.isolation.unsupported");
    }
}
