using System.Text.Json;
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
    [InlineData(AzureTransientArmFailure.QuotaExceeded)]
    [InlineData(AzureTransientArmFailure.RequestDisallowedByPolicy)]
    [InlineData(AzureTransientArmFailure.ResourceDeploymentFailure)]
    [InlineData(AzureTransientArmFailure.DeploymentFailed)]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_empty_codes_are_not_transient(string? code) =>
        Assert.False(AzureTransientArmFailure.IsTransient(code));

    [Theory]
    [InlineData(AzureTransientArmFailure.QuotaExceeded)]
    [InlineData(AzureTransientArmFailure.RequestDisallowedByPolicy)]
    [InlineData("InvalidTemplate")]
    [InlineData("ResourceGroupNotFound")]
    public void Terminal_arm_codes_are_classified_as_terminal(string code) =>
        Assert.True(AzureTransientArmFailure.IsTerminal(code));

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

    [Fact]
    public void Nested_production_aca_module_failure_is_transient()
    {
        Assert.Equal(
            AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode,
            AzureTransientArmFailure.Classify(NestedProductionAcaFailure));
    }

    [Fact]
    public void Message_only_inner_text_is_not_transient() =>
        Assert.Null(AzureTransientArmFailure.Classify(MessageOnlyDeploymentFailure));

    [Theory]
    [InlineData("""{"code":"QuotaExceeded"}""")]
    [InlineData("""{"code":"RequestDisallowedByPolicy"}""")]
    [InlineData("""{"code":"ResourceDeploymentFailure"}""")]
    [InlineData("""{"code":"DeploymentFailed"}""")]
    [InlineData("""{"code":"DeploymentFailed","details":[{"code":"ResourceDeploymentFailure"}]}""")]
    [InlineData("""{"code":"DeploymentFailed","details":[{"code":"QuotaExceeded"},{"code":"ManagedEnvironmentProvisioningError"}]}""")]
    [InlineData(WrapperOnlyProductionFoundationError)]
    public void Wrapper_or_terminal_trees_are_not_transient(string json) =>
        Assert.Null(AzureTransientArmFailure.Classify(json));

    [Fact]
    public void Production_wrapper_error_with_operations_status_message_is_transient()
    {
        Assert.Null(AzureTransientArmFailure.Classify(WrapperOnlyProductionFoundationError));
        Assert.Equal(
            AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode,
            AzureTransientArmFailure.ClassifyOperations(ProductionManagedEnvironmentOperations));
    }

    [Fact]
    public void Operations_message_text_is_not_scraped_for_unknown_codes() =>
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(MessageOnlyDeploymentOperations));

    [Theory]
    [InlineData("""[{"properties":{"provisioningState":"Failed","statusMessage":{"error":{"code":"QuotaExceeded"}}}}]""")]
    [InlineData("""[{"properties":{"provisioningState":"Failed","statusMessage":{"error":{"code":"RequestDisallowedByPolicy"}}}}]""")]
    [InlineData("""[{"properties":{"statusMessage":{"error":{"code":"QuotaExceeded","details":[{"code":"ManagedEnvironmentProvisioningError"}]}}}}]""")]
    public void Operations_terminal_or_wrapper_trees_are_not_transient(string json) =>
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(json));

    [Fact]
    public void Failed_nested_module_names_are_taken_from_structured_targets_only()
    {
        using var document = JsonDocument.Parse(ProductionFoundationNestedModuleOperations);
        var names = AzureTransientArmFailure.FailedNestedDeploymentNames(document.RootElement);
        Assert.Equal(["container-apps-environment"], names);

        using var unsafeDocument = JsonDocument.Parse(
            """[{"properties":{"targetResource":{"resourceType":"Microsoft.Resources/deployments","resourceName":"/subscriptions/1/resourceGroups/foreign-rg/providers/Microsoft.Resources/deployments/other"}}}]""");
        Assert.Empty(AzureTransientArmFailure.FailedNestedDeploymentNames(unsafeDocument.RootElement));
    }

    internal const string NestedProductionAcaFailure = """
        {
          "code": "DeploymentFailed",
          "message": "At least one resource deployment operation failed. Please list deployment operations for details. Please see https://aka.ms/arm-deployment-operations for usage details.",
          "details": [
            {
              "code": "Conflict",
              "message": "{\r\n  \"status\": \"Failed\",\r\n  \"error\": {\r\n    \"code\": \"ResourceDeploymentFailure\",\r\n    \"message\": \"The resource write operation failed to complete successfully.\"\r\n  }\r\n}",
              "details": [
                {
                  "code": "ResourceDeploymentFailure",
                  "target": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/proof-aca",
                  "message": "The resource write operation failed to complete successfully, you can check deployment operations for details.",
                  "details": [
                    {
                      "code": "ManagedEnvironmentProvisioningError",
                      "message": "Failed to provision because of an error during infrastructure setup. Check the activity log for more details."
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    internal const string MessageOnlyDeploymentFailure = """
        {
          "code": "DeploymentFailed",
          "message": "ManagedEnvironmentProvisioningError: Failed to provision the Container Apps environment.",
          "details": [
            {
              "code": "ResourceDeploymentFailure",
              "message": "The module failed with ManagedEnvironmentProvisioningError during infrastructure setup."
            }
          ]
        }
        """;

    /// <summary>
    /// Recorded production shape from #750: the top-level foundation error only
    /// wraps DeploymentFailed / ResourceDeploymentFailure. The transient ACA
    /// code is absent from details[].
    /// </summary>
    internal const string WrapperOnlyProductionFoundationError = """
        {
          "code": "DeploymentFailed",
          "message": "At least one resource deployment operation failed. Please list deployment operations for details. Please see https://aka.ms/arm-deployment-operations for usage details.",
          "details": [
            {
              "code": "ResourceDeploymentFailure",
              "message": "The resource write operation failed to complete successfully, you can check deployment operations for details."
            }
          ]
        }
        """;

    internal const string ProductionFoundationSiblingModuleOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "container-apps-environment"
              },
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "ResourceDeploymentFailure",
                  "message": "The resource write operation failed to complete successfully, you can check deployment operations for details."
                }
              }
            }
          },
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "sql"
              },
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "ResourceDeploymentFailure",
                  "message": "The resource write operation failed to complete successfully, you can check deployment operations for details."
                }
              }
            }
          }
        ]
        """;

    internal const string QuotaExceededDeploymentOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Sql/servers",
                "resourceName": "proof-sql"
              },
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "QuotaExceeded",
                  "message": "The subscription has reached its SQL server quota."
                }
              }
            }
          }
        ]
        """;

    internal const string ProductionFoundationNestedModuleOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "container-apps-environment"
              },
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "ResourceDeploymentFailure",
                  "message": "The resource write operation failed to complete successfully, you can check deployment operations for details."
                }
              }
            }
          }
        ]
        """;

    internal const string ProductionManagedEnvironmentOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "id": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/ec0139c55cfd7449-aca",
                "resourceType": "Microsoft.App/managedEnvironments",
                "resourceName": "ec0139c55cfd7449-aca"
              },
              "statusCode": "Conflict",
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "ManagedEnvironmentProvisioningError",
                  "message": "Error when initializing components on ManagedCluster"
                }
              }
            }
          }
        ]
        """;

    internal const string MessageOnlyDeploymentOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.App/managedEnvironments",
                "resourceName": "proof-aca"
              },
              "statusMessage": {
                "status": "Failed",
                "error": {
                  "code": "ResourceDeploymentFailure",
                  "message": "ManagedEnvironmentProvisioningError: Error when initializing components on ManagedCluster"
                }
              }
            }
          }
        ]
        """;
}
