using Pulumi.Automation;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Automation API workspace settings for one stack. Blob backend with locking,
/// Key Vault secrets provider, workload-identity environment. Secret values
/// travel only as Pulumi secret config, never in request history.
/// </summary>
public sealed record PulumiWorkspaceConfiguration(
    string ProjectName,
    string StackName,
    string BackendUrl,
    string SecretsProviderUrl,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyDictionary<string, string> StackConfig,
    IReadOnlyDictionary<string, string>? SecretStackConfig = null)
{
    public LocalWorkspaceOptions ToLocalWorkspaceOptions(PulumiFn program)
    {
        ArgumentNullException.ThrowIfNull(program);
        RejectSecretEnvironment(EnvironmentVariables);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in EnvironmentVariables)
            environment[pair.Key] = pair.Value;

        var config = new Dictionary<string, StackSettingsConfigValue>(StringComparer.Ordinal);
        foreach (var pair in StackConfig)
            config[pair.Key] = new StackSettingsConfigValue(pair.Value, isSecure: false);
        if (SecretStackConfig is not null)
        {
            foreach (var pair in SecretStackConfig)
                config[pair.Key] = new StackSettingsConfigValue(pair.Value, isSecure: true);
        }

        return new LocalWorkspaceOptions
        {
            ProjectSettings = new ProjectSettings(ProjectName, ProjectRuntimeName.Dotnet)
            {
                Backend = new ProjectBackend { Url = BackendUrl }
            },
            SecretsProvider = string.IsNullOrWhiteSpace(SecretsProviderUrl) ? null : SecretsProviderUrl,
            EnvironmentVariables = environment,
            StackSettings = new Dictionary<string, StackSettings>(StringComparer.Ordinal)
            {
                [StackName] = new StackSettings
                {
                    SecretsProvider = string.IsNullOrWhiteSpace(SecretsProviderUrl) ? null : SecretsProviderUrl,
                    Config = config
                }
            },
            Program = program
        };
    }

    private static void RejectSecretEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        foreach (var key in environment.Keys)
        {
            if (key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                || key.Contains("STORAGE_KEY", StringComparison.OrdinalIgnoreCase)
                || key.Contains("ACCESS_KEY", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Provisioning environment must not carry secret values.");
            }
        }
    }
}
