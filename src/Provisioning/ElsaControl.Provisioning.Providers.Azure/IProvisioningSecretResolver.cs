namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Resolves engine vault seed values at run time. Locators may sit in stack
/// config; values never do. Tests inject a static source.
/// </summary>
public interface IProvisioningSecretResolver
{
    Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default);
}

public sealed class EmptyProvisioningSecretResolver : IProvisioningSecretResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stackConfig);
        return Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
