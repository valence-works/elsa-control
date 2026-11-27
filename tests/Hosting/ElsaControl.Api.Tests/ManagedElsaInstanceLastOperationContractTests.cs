using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Pins Hub-facing list/detail <c>lastOperation</c> JSON so an accidental
/// contract change fails CI here rather than on the Hub.
/// </summary>
public sealed class ManagedElsaInstanceLastOperationContractTests
{
    public const string RelativeDirectory = "contracts/managed-elsa-instance-last-operation";
    public const string RegenerateEnvironmentVariable =
        "ELSA_CONTROL_REGENERATE_MANAGED_ELSA_INSTANCE_LAST_OPERATION_CONTRACTS";

    private static readonly DateTimeOffset CompletedAt = DateTimeOffset.Parse("2026-11-27T12:00:00Z");
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static TheoryData<string, ManagedElsaInstanceOverviewLastOperationResponse> LastOperationScenarios() => new()
    {
        { "failed-create.json", FailedCreate() },
        { "failed-retry.json", FailedRetry() },
        { "failed-after-ready.json", FailedAfterReady() }
    };

    [Theory]
    [MemberData(nameof(LastOperationScenarios))]
    public void Last_operation_response_matches_the_committed_golden_file(
        string fileName,
        ManagedElsaInstanceOverviewLastOperationResponse response)
    {
        AssertCommitted(fileName, response);
    }

    [Fact]
    public void Projection_matches_the_failed_create_golden()
    {
        var lastOperation = new ElsaInstanceOperationSummary(
            Guid.Parse("40000000-0000-0000-0000-000000000002"),
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.Failed,
            1, 1, CompletedAt, CompletedAt, CompletedAt, null, null, null,
            "azure.step.failed",
            ElsaObservedLifecycle.Failed,
            ElsaInstanceHealth.Unreachable,
            ReasonCode: ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop);

        AssertCommitted("failed-create.json", ManagedElsaInstanceOverviewProjection.ToLastOperation(lastOperation)!);
    }

    [Fact]
    public void Golden_files_do_not_embed_provider_identifiers_or_secrets()
    {
        foreach (var file in Directory.GetFiles(CommittedDirectory(), "*.json"))
        {
            var json = File.ReadAllText(file);
            Assert.DoesNotContain("azure.", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("providerId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("serializedPlan", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", json, StringComparison.Ordinal);
        }
    }

    private static ManagedElsaInstanceOverviewLastOperationResponse FailedCreate() =>
        new(
            ElsaInstanceOperationAction.Create,
            ElsaInstanceOperationState.Failed,
            CompletedAt,
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed)
        {
            ReasonCode = ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop
        };

    private static ManagedElsaInstanceOverviewLastOperationResponse FailedRetry() =>
        new(
            ElsaInstanceOperationAction.Retry,
            ElsaInstanceOperationState.Failed,
            CompletedAt,
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed)
        {
            ReasonCode = ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop
        };

    private static ManagedElsaInstanceOverviewLastOperationResponse FailedAfterReady() =>
        new(
            ElsaInstanceOperationAction.Restart,
            ElsaInstanceOperationState.Failed,
            CompletedAt,
            ManagedLifecycleOperationalHealthDiagnosticCodes.OperationFailed);

    private static void AssertCommitted(string fileName, ManagedElsaInstanceOverviewLastOperationResponse response)
    {
        var actual = CanonicalJson(response);
        var file = new FileInfo(Path.Combine(CommittedDirectory(), fileName));
        var regenerate = IsTruthy(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable));
        if (regenerate && (IsTruthy(Environment.GetEnvironmentVariable("CI")) || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
        {
            Assert.Fail(
                "Managed-instance lastOperation contract regeneration is disabled when CI or GITHUB_ACTIONS is set. " +
                $"Unset {RegenerateEnvironmentVariable}.");
        }

        if (regenerate)
        {
            Directory.CreateDirectory(file.DirectoryName!);
            File.WriteAllText(file.FullName, actual + Environment.NewLine);
        }

        Assert.True(file.Exists, $"Committed golden file is missing at {RelativeDirectory}/{fileName}.");
        Assert.Equal(CanonicalJson(File.ReadAllText(file.FullName)), actual);
    }

    private static string CanonicalJson<T>(T value) =>
        CanonicalJson(JsonSerializer.Serialize(value, JsonOptions));

    private static string CanonicalJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON was empty.");
        return node.ToJsonString(JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static string CommittedDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Could not locate repository root.");
        return Path.Combine(directory.FullName, RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
