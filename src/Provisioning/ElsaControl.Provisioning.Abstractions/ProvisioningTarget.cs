namespace ElsaControl.Provisioning.Abstractions;

/// <summary>
/// Where a command runs. Knows the provider, stack identity, credentials, and
/// backends. Knows nothing about customers.
/// </summary>
public sealed record ProvisioningTarget
{
    public ProvisioningTarget(
        string providerId,
        ProvisioningStackKind stackKind,
        string stackName,
        CredentialReference credentials,
        StateBackendReference stateBackend,
        SecretsProviderReference secretsProvider,
        string? platformStackName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(stateBackend);
        ArgumentNullException.ThrowIfNull(secretsProvider);
        if (!Enum.IsDefined(stackKind))
            throw new ArgumentOutOfRangeException(nameof(stackKind));
        if (stackKind == ProvisioningStackKind.Engine && string.IsNullOrWhiteSpace(platformStackName))
            throw new ArgumentException("Engine stacks require a platform stack name.", nameof(platformStackName));

        ProviderId = providerId.Trim();
        StackKind = stackKind;
        StackName = stackName.Trim();
        Credentials = credentials;
        StateBackend = stateBackend;
        SecretsProvider = secretsProvider;
        PlatformStackName = string.IsNullOrWhiteSpace(platformStackName) ? null : platformStackName.Trim();
    }

    public string ProviderId { get; }
    public ProvisioningStackKind StackKind { get; }
    public string StackName { get; }
    public CredentialReference Credentials { get; }
    public StateBackendReference StateBackend { get; }
    public SecretsProviderReference SecretsProvider { get; }
    public string? PlatformStackName { get; }
}
