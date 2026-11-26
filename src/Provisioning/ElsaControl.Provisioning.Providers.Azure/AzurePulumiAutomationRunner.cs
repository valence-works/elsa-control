using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Builds Automation API workspace options for <c>elsa-platform</c> and
/// <c>elsa-engine</c> and selects the checked-in programs.
/// </summary>
public sealed class AzurePulumiAutomationRunner
{
    public const string ProviderId = "azure";

    public AzurePulumiAutomationRunner(bool allowFileBackend = false)
    {
        AllowFileBackend = allowFileBackend;
    }

    public bool AllowFileBackend { get; }

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
        IReadOnlyDictionary<string, string> credentialEnvironment,
        IReadOnlyDictionary<string, string>? secretStackConfig = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(stackConfig);
        ArgumentNullException.ThrowIfNull(credentialEnvironment);
        if (!string.Equals(target.ProviderId, ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("Azure provider required.", nameof(target));

        var (backendUrl, backendEnvironment, secretsUrl) = ResolveBackend(target);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in backendEnvironment)
            environment[pair.Key] = pair.Value;
        foreach (var pair in credentialEnvironment)
            environment[pair.Key] = pair.Value;

        var config = new Dictionary<string, string>(stackConfig, StringComparer.Ordinal);
        if (target.StackKind == ProvisioningStackKind.Engine
            && !config.ContainsKey("platformStack")
            && target.PlatformStackName is not null)
        {
            config["platformStack"] = PulumiStackReferenceName.Platform(
                target.PlatformStackName,
                ProvisioningStackReference.DefaultOrganization);
        }

        if (target.StackKind == ProvisioningStackKind.Engine && !config.ContainsKey("engineName"))
            config["engineName"] = target.StackName;
        if (target.StackKind == ProvisioningStackKind.Platform && !config.ContainsKey("environmentName"))
            config["environmentName"] = target.StackName;

        return new PulumiWorkspaceConfiguration(
            ProjectName(target.StackKind),
            target.StackName,
            backendUrl,
            secretsUrl,
            environment,
            config,
            secretStackConfig);
    }

    private (string BackendUrl, IReadOnlyDictionary<string, string> Environment, string SecretsUrl)
        ResolveBackend(ProvisioningTarget target)
    {
        if (string.Equals(target.StateBackend.Kind, StateBackendReference.FileKind, StringComparison.Ordinal))
        {
            if (!AllowFileBackend)
            {
                throw new InvalidOperationException(
                    "File and passphrase Pulumi backends are test-only.");
            }

            var file = FileStateBackend.From(target.StateBackend);
            return (file.BackendUrl, file.EnvironmentVariables, "passphrase");
        }

        var backend = AzureBlobStateBackend.From(target.StateBackend);
        var secrets = AzureKeyVaultSecretsProvider.From(target.SecretsProvider);
        return (backend.BackendUrl, backend.EnvironmentVariables, secrets.SecretsProviderUrl);
    }
}
