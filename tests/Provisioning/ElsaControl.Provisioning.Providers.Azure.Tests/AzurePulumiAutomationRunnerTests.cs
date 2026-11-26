using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Providers.Azure.Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzurePulumiAutomationRunnerTests
{
    [Fact]
    public void Workspace_uses_blob_backend_with_locking_and_key_vault_secrets()
    {
        var runner = new AzurePulumiAutomationRunner();
        var target = AzureTarget("acme-prod", ProvisioningStackKind.Engine, "staging");
        var workspace = runner.CreateWorkspace(
            target,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-prod" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AZURE_CLIENT_ID"] = "workload-identity-client" });

        Assert.Equal(PulumiProjectNames.Engine, workspace.ProjectName);
        Assert.Equal("acme-prod", workspace.StackName);
        Assert.Equal("azblob://pulumi-state", workspace.BackendUrl);
        Assert.Equal("azurekeyvault://kv-elsa-platform.vault.azure.net/keys/pulumi-secrets", workspace.SecretsProviderUrl);
        Assert.Equal("stelsaplatform", workspace.EnvironmentVariables["AZURE_STORAGE_ACCOUNT"]);
        Assert.Equal("workload-identity-client", workspace.EnvironmentVariables["AZURE_CLIENT_ID"]);
        Assert.DoesNotContain(workspace.EnvironmentVariables.Keys, key =>
            key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            || key.Contains("STORAGE_KEY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("elsa-platform/staging", workspace.StackConfig["platformStack"]);
        Assert.True(AzureBlobStateBackend.From(target.StateBackend).LockingEnabled);

        var options = workspace.ToLocalWorkspaceOptions(runner.CreateProgram(ProvisioningStackKind.Engine));
        Assert.Equal("azblob://pulumi-state", options.ProjectSettings!.Backend!.Url);
        Assert.Equal(workspace.SecretsProviderUrl, options.SecretsProvider);
        Assert.NotNull(options.Program);
    }

    [Fact]
    public void Engine_stack_config_is_independent_per_stack()
    {
        var runner = new AzurePulumiAutomationRunner();
        var one = runner.CreateWorkspace(
            AzureTarget("acme-one", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-one" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var two = runner.CreateWorkspace(
            AzureTarget("acme-two", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-two" },
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(PulumiProjectNames.Engine, one.ProjectName);
        Assert.Equal(PulumiProjectNames.Engine, two.ProjectName);
        Assert.NotEqual(one.StackName, two.StackName);
        Assert.Equal("acme-one", one.StackConfig["engineName"]);
        Assert.Equal("acme-two", two.StackConfig["engineName"]);
        Assert.Equal("elsa-platform/staging", one.StackConfig["platformStack"]);
    }

    [Fact]
    public void Platform_and_engine_programs_are_the_checked_in_stacks()
    {
        var runner = new AzurePulumiAutomationRunner();
        Assert.Equal(PulumiProjectNames.Platform, runner.ProjectName(ProvisioningStackKind.Platform));
        Assert.Equal(PulumiProjectNames.Engine, runner.ProjectName(ProvisioningStackKind.Engine));
        Assert.NotNull(runner.CreateProgram(ProvisioningStackKind.Platform));
        Assert.NotNull(runner.CreateProgram(ProvisioningStackKind.Engine));
        Assert.Equal(nameof(ElsaPlatformStack), typeof(ElsaPlatformStack).Name);
        Assert.Equal(nameof(ElsaEngineStack), typeof(ElsaEngineStack).Name);
    }

    [Fact]
    public void Secret_environment_keys_are_rejected()
    {
        var runner = new AzurePulumiAutomationRunner();
        var workspace = runner.CreateWorkspace(
            AzureTarget("staging", ProvisioningStackKind.Platform),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AZURE_STORAGE_KEY"] = "must-not-appear" });
        var error = Assert.Throws<InvalidOperationException>(() =>
            workspace.ToLocalWorkspaceOptions(runner.CreateProgram(ProvisioningStackKind.Platform)));
        Assert.Equal("Provisioning environment must not carry secret values.", error.Message);
    }

    [Fact]
    public async Task Azure_provider_emits_sanitised_events_without_running_the_bicep_runner()
    {
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials());
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Preview,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "acme-prod",
            "azure-preview",
            "azure-preview-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Preview,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal)
                {
                    ProvisioningCapability.ContainerRuntime,
                    ProvisioningCapability.ManagedSql
                }),
            AzureTarget("acme-prod", ProvisioningStackKind.Engine, "staging"),
            "azure-preview",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "acme-prod" });

        var result = await provider.PreviewAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Events);
        foreach (var value in result.Events)
        {
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.ResourceKind));
        }
    }

    private static ProvisioningTarget AzureTarget(
        string stackName,
        ProvisioningStackKind kind,
        string? platformStackName = null) =>
        new(
            AzurePulumiAutomationRunner.ProviderId,
            kind,
            stackName,
            new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity-client"),
            new StateBackendReference(StateBackendReference.AzureBlobKind, "stelsaplatform/pulumi-state"),
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, "kv-elsa-platform/pulumi-secrets"),
            platformStackName);
}
