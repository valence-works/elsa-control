using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Providers.Azure.Pulumi;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Builds Automation API workspace options for <c>elsa-platform</c> and
/// <c>elsa-engine</c>. Preview / Up / Refresh / Destroy against a live backend
/// are #1014; this skeleton exposes the runner configuration and program
/// selection.
/// </summary>
public sealed class AzurePulumiAutomationRunner
{
    public const string ProviderId = "azure";

    public PulumiFn CreateProgram(ProvisioningStackKind stackKind) =>
        stackKind switch
        {
            ProvisioningStackKind.Platform => PulumiFn.Create<ElsaPlatformStack>(),
            ProvisioningStackKind.Engine => PulumiFn.Create<ElsaEngineStack>(),
            _ => throw new ArgumentOutOfRangeException(nameof(stackKind))
        };

    public string ProjectName(ProvisioningStackKind stackKind) =>
        stackKind switch
        {
            ProvisioningStackKind.Platform => PulumiProjectNames.Platform,
            ProvisioningStackKind.Engine => PulumiProjectNames.Engine,
            _ => throw new ArgumentOutOfRangeException(nameof(stackKind))
        };

    public PulumiWorkspaceConfiguration CreateWorkspace(
        ProvisioningTarget target,
        IReadOnlyDictionary<string, string> stackConfig,
        IReadOnlyDictionary<string, string> credentialEnvironment)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(stackConfig);
        ArgumentNullException.ThrowIfNull(credentialEnvironment);
        if (!string.Equals(target.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("Azure provider required.", nameof(target));

        var backend = AzureBlobStateBackend.From(target.StateBackend);
        var secrets = AzureKeyVaultSecretsProvider.From(target.SecretsProvider);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in backend.EnvironmentVariables)
            environment[pair.Key] = pair.Value;
        foreach (var pair in credentialEnvironment)
            environment[pair.Key] = pair.Value;

        var config = new Dictionary<string, string>(stackConfig, StringComparer.Ordinal);
        if (target.StackKind == ProvisioningStackKind.Engine
            && !config.ContainsKey("platformStack")
            && target.PlatformStackName is not null)
        {
            config["platformStack"] = $"{PulumiProjectNames.Platform}/{target.PlatformStackName}";
        }

        if (target.StackKind == ProvisioningStackKind.Engine && !config.ContainsKey("engineName"))
            config["engineName"] = target.StackName;
        if (target.StackKind == ProvisioningStackKind.Platform && !config.ContainsKey("environmentName"))
            config["environmentName"] = target.StackName;

        return new PulumiWorkspaceConfiguration(
            ProjectName(target.StackKind),
            target.StackName,
            backend.BackendUrl,
            secrets.SecretsProviderUrl,
            environment,
            config);
    }
}
