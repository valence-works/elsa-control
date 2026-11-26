using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Resolves the three engine seed values from Key Vault using the runtime
/// identity. Locators may be <c>secret://name</c> in stack config; values
/// never are. Fail closed when the vault or a seed is missing.
/// </summary>
public sealed class AzureKeyVaultProvisioningSecretResolver : IProvisioningSecretResolver
{
    public const string SqlConnectionConfigKey = "sqlConnectionSecretValue";
    public const string SigningKeyConfigKey = "signingKeySecretValue";
    public const string AdminPasswordConfigKey = "adminPasswordSecretValue";
    public const string SqlConnectionSecretName = "sql-connection";
    public const string SigningKeySecretName = "identity-signing-key";
    public const string AdminPasswordSecretName = "admin-password";

    private static readonly (string ConfigKey, string SecretName)[] Seeds =
    [
        (SqlConnectionConfigKey, SqlConnectionSecretName),
        (SigningKeyConfigKey, SigningKeySecretName),
        (AdminPasswordConfigKey, AdminPasswordSecretName)
    ];

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

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
            throw new InvalidOperationException("Provisioning seed vault is not configured.");
        _ = await ResolveSecretConfigAsync(
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveSecretConfigAsync(
        IReadOnlyDictionary<string, string> stackConfig,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stackConfig);
        if (!IsReady)
            throw new InvalidOperationException("Provisioning seed vault is not configured.");

        var client = _client();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (configKey, secretName) in Seeds)
        {
            var name = secretName;
            if (stackConfig.TryGetValue(configKey, out var locator)
                && locator.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
            {
                name = locator["secret://".Length..].Trim().Trim('/');
            }

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Provisioning seed vault secret is missing.");

            var secret = await client.GetSecretAsync(name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var value = secret.Value.Value;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Provisioning seed vault secret is missing.");
            resolved[configKey] = value;
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
