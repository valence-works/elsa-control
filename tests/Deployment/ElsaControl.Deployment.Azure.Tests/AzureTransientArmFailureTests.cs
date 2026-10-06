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
    public void Historical_conflict_wrapper_is_unknown_and_blocks_retry() =>
        Assert.Null(AzureTransientArmFailure.Classify(HistoricalConflictAcaFailure));

    [Fact]
    public void Nested_known_wrappers_preserve_transient_classification() =>
        Assert.Equal(AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode,
            AzureTransientArmFailure.Classify("""{"code":"DeploymentFailed","details":[{"code":"ResourceDeploymentFailure","details":[{"code":"ManagedEnvironmentProvisioningError"}]}]}"""));

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
        var names = AzureTransientArmFailure.FailedNestedDeploymentNames(document.RootElement, out var incomplete);
        Assert.False(incomplete);
        Assert.Equal(["container-apps-environment"], names);

        using var unsafeDocument = JsonDocument.Parse(
            """[{"properties":{"targetResource":{"resourceType":"Microsoft.Resources/deployments","resourceName":"/subscriptions/1/resourceGroups/foreign-rg/providers/Microsoft.Resources/deployments/other"}}}]""");
        Assert.Empty(AzureTransientArmFailure.FailedNestedDeploymentNames(unsafeDocument.RootElement, out var unsafeTarget));
        Assert.True(unsafeTarget);
    }

    [Theory]
    [InlineData("UnknownError")]
    [InlineData("Conflict")]
    [InlineData("not-a-code")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_siblings_block_retry_in_either_order(string? unknown)
    {
        string?[] codes = [AzureTransientArmFailure.ManagedEnvironmentProvisioningError, unknown];
        foreach (var ordered in new[] { codes, codes.Reverse().ToArray() })
        {
            Assert.Null(AzureTransientArmFailure.Classify(ordered));
            Assert.Null(AzureTransientArmFailure.Classify(JsonSerializer.Serialize(new
            {
                code = AzureTransientArmFailure.DeploymentFailed,
                details = ordered.Select(code => new { code }).ToArray()
            })));
        }
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("nodes")]
    [InlineData("fanout")]
    public void Truncated_error_walk_blocks_retry(string bound) =>
        Assert.Null(AzureTransientArmFailure.Classify(IncompleteErrorTree(bound)));

    [Fact]
    public void Truncated_operations_list_blocks_retry() =>
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(OversizedOperations));

    [Theory]
    [InlineData("depth")]
    [InlineData("nodes")]
    [InlineData("fanout")]
    public void Truncated_operation_error_tree_blocks_retry(string bound) =>
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(
            "[{\"properties\":{\"statusMessage\":{\"error\":" + IncompleteErrorTree(bound) + "}}}]"));

    [Theory]
    [InlineData("UnknownError")]
    [InlineData("QuotaExceeded")]
    public void Every_structured_operation_error_path_must_be_safe(string code)
    {
        var operation = new
        {
            properties = new
            {
                statusMessage = new { error = new { code = AzureTransientArmFailure.ManagedEnvironmentProvisioningError } },
                error = new { code }
            }
        };
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(JsonSerializer.Serialize(new[] { operation })));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":{\"code\":42}}")]
    [InlineData("{\"error\":{\"code\":\"DeploymentFailed\",\"details\":{}}}")]
    public void Incomplete_operation_error_cannot_hide_behind_a_transient_sibling(string operation) =>
        Assert.Null(AzureTransientArmFailure.ClassifyOperations(
            "[{\"error\":{\"code\":\"ManagedEnvironmentProvisioningError\"}}," + operation + "]"));

    [Theory]
    [InlineData("properties.statusMessage.error")]
    [InlineData("statusMessage.error")]
    [InlineData("properties.error")]
    [InlineData("error")]
    [InlineData("properties.statusMessage")]
    [InlineData("statusMessage")]
    public void Each_supported_structured_operation_error_path_remains_transient(string path)
    {
        object error = new { code = AzureTransientArmFailure.ManagedEnvironmentProvisioningError };
        foreach (var segment in path.Split('.').Reverse())
            error = new Dictionary<string, object> { [segment] = error };
        Assert.Equal(AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode,
            AzureTransientArmFailure.ClassifyOperations(JsonSerializer.Serialize(new[] { error })));
    }

    [Theory]
    [InlineData("depth")]
    [InlineData("nodes")]
    [InlineData("fanout")]
    public void Complete_error_walk_at_limit_remains_transient(string bound)
    {
        object wrapper = new { code = AzureTransientArmFailure.DeploymentFailed };
        object[] details;
        if (bound == "depth")
        {
            for (var index = 0; index < 3; index++)
                wrapper = new { code = AzureTransientArmFailure.DeploymentFailed, details = new[] { wrapper } };
            details = [wrapper];
        }
        else if (bound == "nodes")
        {
            // Root + details array + four wrapper/array pairs + 54 leaves = 64.
            details = new[] { 14, 14, 14, 12 }.Select(count => (object)new
            {
                code = AzureTransientArmFailure.DeploymentFailed,
                details = Enumerable.Repeat(wrapper, count).ToArray()
            }).ToArray();
        }
        else
            details = Enumerable.Repeat(wrapper, 16).ToArray();

        Assert.Equal(AzureTransientArmFailure.ManagedEnvironmentProvisioningErrorCode,
            AzureTransientArmFailure.Classify(JsonSerializer.Serialize(new
            {
                code = AzureTransientArmFailure.ManagedEnvironmentProvisioningError,
                details
            })));
    }

    internal static string IncompleteErrorTree(string bound)
    {
        object terminal = new { code = AzureTransientArmFailure.QuotaExceeded };
        object nested;
        switch (bound)
        {
            case "depth":
                nested = terminal;
                for (var index = 0; index < 5; index++)
                    nested = new { code = AzureTransientArmFailure.DeploymentFailed, details = new[] { nested } };
                break;
            case "nodes":
                nested = new
                {
                    code = AzureTransientArmFailure.DeploymentFailed,
                    details = Enumerable.Range(0, 4).Select(_ => (object)new
                    {
                        code = AzureTransientArmFailure.DeploymentFailed,
                        details = Enumerable.Repeat(new { code = AzureTransientArmFailure.DeploymentFailed }, 16).ToArray()
                    }).Append(terminal).ToArray()
                };
                break;
            case "fanout":
                nested = new
                {
                    code = AzureTransientArmFailure.DeploymentFailed,
                    details = Enumerable.Repeat((object)new { code = AzureTransientArmFailure.DeploymentFailed }, 16)
                        .Append(terminal).ToArray()
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(bound));
        }

        return JsonSerializer.Serialize(new
        {
            code = AzureTransientArmFailure.ManagedEnvironmentProvisioningError,
            details = new[] { nested }
        });
    }

    internal static string OversizedOperations => JsonSerializer.Serialize(Enumerable.Range(0, 17).Select(index => new
    {
        properties = new
        {
            provisioningState = "Failed",
            statusMessage = new { error = new { code = index == 0
                ? AzureTransientArmFailure.ManagedEnvironmentProvisioningError
                : index == 16 ? AzureTransientArmFailure.QuotaExceeded : AzureTransientArmFailure.DeploymentFailed } }
        }
    }));

    internal const string HistoricalConflictAcaFailure = """
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

    internal const string FiveFailedNestedModuleOperations = """
        [
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "identity"
              },
              "statusMessage": { "error": { "code": "ResourceDeploymentFailure" } }
            }
          },
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "observability"
              },
              "statusMessage": { "error": { "code": "ResourceDeploymentFailure" } }
            }
          },
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "sql"
              },
              "statusMessage": { "error": { "code": "ResourceDeploymentFailure" } }
            }
          },
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "key-vault"
              },
              "statusMessage": { "error": { "code": "ResourceDeploymentFailure" } }
            }
          },
          {
            "properties": {
              "provisioningState": "Failed",
              "targetResource": {
                "resourceType": "Microsoft.Resources/deployments",
                "resourceName": "container-apps-environment"
              },
              "statusMessage": { "error": { "code": "ResourceDeploymentFailure" } }
            }
          }
        ]
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
