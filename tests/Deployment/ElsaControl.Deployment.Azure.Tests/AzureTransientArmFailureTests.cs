using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureTransientArmFailureTests
{
    [Theory]
    [InlineData(AzureTransientArmFailure.ManagedEnvironmentProvisioningError)]
    [InlineData(AzureTransientArmFailure.TooManyRequests)]
    [InlineData("429")]
    [InlineData(AzureTransientArmFailure.AllocationFailed)]
    [InlineData(AzureTransientArmFailure.ServerTimeout)]
    [InlineData(AzureTransientArmFailure.InternalServerError)]
    public void Allow_listed_arm_codes_are_transient(string code) =>
        Assert.True(AzureTransientArmFailure.IsTransient(code));

    [Theory]
    [InlineData("InvalidTemplate")]
    [InlineData("ResourceGroupNotFound")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_empty_codes_are_not_transient(string? code) =>
        Assert.False(AzureTransientArmFailure.IsTransient(code));

    [Fact]
    public void Normalize_maps_429_and_rejects_unsafe_text()
    {
        Assert.Equal(AzureTransientArmFailure.TooManyRequests, AzureTransientArmFailure.Normalize("429"));
        Assert.Equal(
            AzureTransientArmFailure.ManagedEnvironmentProvisioningError,
            AzureTransientArmFailure.Normalize("ManagedEnvironmentProvisioningError"));
        Assert.Null(AzureTransientArmFailure.Normalize("not a code"));
        Assert.Null(AzureTransientArmFailure.Normalize("drop table"));
    }

    [Fact]
    public void Safe_diagnostics_are_kebab_azure_arm_codes()
    {
        Assert.Equal(
            "azure.arm.managed-environment-provisioning-error",
            AzureTransientArmFailure.ToSafeDiagnostic(AzureTransientArmFailure.ManagedEnvironmentProvisioningError));
        Assert.Equal(
            AzureTransientArmFailure.TooManyRequestsCode,
            AzureTransientArmFailure.ToSafeDiagnostic("429"));
        Assert.Null(AzureTransientArmFailure.ToSafeDiagnostic("InvalidTemplate"));
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled)]
    public void Terminal_failure_codes_are_retrying_or_needs_operator(string code) =>
        Assert.True(AzureTransientArmFailure.IsRetryingOrNeedsOperator(code));
}
