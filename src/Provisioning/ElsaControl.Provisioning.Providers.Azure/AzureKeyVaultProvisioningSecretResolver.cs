using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Resolves allowlisted secret locators from Key Vault using the runtime
/// identity. Engine seeds are generated per stack and are never loaded from
/// the shared vault. Fail closed if a locator cannot be fetched or if a
/// shared seed key is present.
/// </summary>
public sealed class AzureKeyVaultProvisioningSecretResolver : IProvisioningSecretResolver
{
    private readonly Uri? _vaultUri;
    private readonly Func<SecretClient> _client;

    public AzureKeyVaultProvisioningSecretResolver(string? vaultUri)
        : this(vaultUri, () => new SecretClient(new Uri(vaultUri!), new DefaultAzureCredential()))
    {
    }

    internal AzureKeyVaultProvisioningSecretResolver(string? vaultUri, Func<SecretClient> client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _vaultUri = TryParseVault(vaultUri);
    }

    public bool IsReady => _vaultUri is not null;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
            throw new InvalidOperationException("Provisioning seed vault is not configured.");
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stackConfig);
        AzureEngineProgramPolicy.RejectSharedSeedValues(stackConfig);
        if (!IsReady)
            throw new InvalidOperationException("Provisioning seed vault is not configured.");

        var client = _client();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in stackConfig)
        {
            if (!ProvisioningEventSanitizer.IsSecretLocator(pair.Value))
                continue;
            if (!ProvisioningEventSanitizer.IsAdmittedSecretConfigKey(pair.Key))
            {
                throw new InvalidOperationException("Shared engine seed values are not admitted.");
            }

            var name = pair.Value["secret://".Length..].Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Provisioning seed vault secret is missing.");

            var secret = await client.GetSecretAsync(name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var value = secret.Value.Value;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Provisioning seed vault secret is missing.");
            resolved[pair.Key] = value;
        }

        return resolved;
    }

    private static Uri? TryParseVault(string? vaultUri)
    {
        if (string.IsNullOrWhiteSpace(vaultUri))
            return null;
        if (!Uri.TryCreate(vaultUri, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return uri;
    }
}
