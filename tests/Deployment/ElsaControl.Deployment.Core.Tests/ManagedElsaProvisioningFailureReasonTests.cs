using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaProvisioningFailureReasonTests
{
    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProvisioningTimeoutFoundation)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProvisioningTimeoutWorkload)]
    public void Customer_codes_pass_through(string code)
    {
        Assert.True(ManagedElsaProvisioningFailureReason.IsCustomerCode(code));
        Assert.True(ManagedElsaProvisioningFailureReason.TryMap(code, out var mapped));
        Assert.Equal(code, mapped);
    }

    [Theory]
    [InlineData(ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop, ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop)]
    [InlineData(ManagedElsaProvisioningFailureReason.AzureHealthTimeout, ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth)]
    [InlineData(ManagedElsaProvisioningFailureReason.AzureHealthUnhealthy, ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth)]
    public void Internal_health_codes_map_to_customer_codes(string providerCode, string customerCode)
    {
        Assert.True(ManagedElsaProvisioningFailureReason.TryMap(providerCode, out var mapped));
        Assert.Equal(customerCode, mapped);
        Assert.False(mapped!.Contains("subscriptions/", StringComparison.OrdinalIgnoreCase));
        Assert.False(mapped.Contains("resourceGroups", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ManagedElsaProvisioningProgressStages.HostingFoundation, ManagedElsaReasonCodeCatalog.ProvisioningTimeoutFoundation)]
    [InlineData(ManagedElsaProvisioningProgressStages.RuntimeDeployment, ManagedElsaReasonCodeCatalog.ProvisioningTimeoutWorkload)]
    [InlineData(ManagedElsaProvisioningProgressStages.HealthVerification, ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth)]
    public void Wait_exceeded_maps_to_the_stage_timeout(string stage, string expected)
    {
        Assert.True(ManagedElsaProvisioningFailureReason.TryMap(
            ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
            stage,
            out var mapped));
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void Wait_exceeded_without_a_stage_is_not_guessed()
    {
        Assert.False(ManagedElsaProvisioningFailureReason.TryMap(
            ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded,
            out _));
    }

    [Fact]
    public void Unknown_and_unsafe_codes_are_not_mapped()
    {
        Assert.False(ManagedElsaProvisioningFailureReason.TryMap("azure.step.failed", out _));
        Assert.False(ManagedElsaProvisioningFailureReason.TryMap(
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg",
            out _));
        Assert.False(ManagedElsaProvisioningFailureReason.TryMap("Container terminated with exit code 139", out _));
    }

    [Fact]
    public void Resolve_prefers_a_crash_loop_over_a_generic_failure()
    {
        var mapped = ManagedElsaProvisioningFailureReason.Resolve(
            reconciliationDiagnosticCode: "azure.step.failed",
            failureCode: "azure.step.failed",
            lastObservationReasonCode: ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop,
            diagnosticCodes: [ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop],
            currentStage: ManagedElsaProvisioningProgressStages.HealthVerification);

        Assert.Equal(ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop, mapped);
    }

    [Fact]
    public void Resolve_keeps_a_translator_finding_when_no_failure_class_matches()
    {
        var mapped = ManagedElsaProvisioningFailureReason.Resolve(
            "azure.imageRegistry.unsupported",
            "provider.submission.rejected",
            lastObservationReasonCode: null,
            diagnosticCodes: null,
            currentStage: ManagedElsaProvisioningProgressStages.RequestAccepted);

        Assert.Equal("azure.imageRegistry.unsupported", mapped);
    }
}
