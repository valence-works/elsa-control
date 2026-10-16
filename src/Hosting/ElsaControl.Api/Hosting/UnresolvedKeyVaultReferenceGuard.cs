namespace ElsaControl.Api.Hosting;

/// <summary>
/// Refuses host start when a secret App Service setting still holds the
/// unresolved Key Vault reference string that App Service leaves in place
/// when resolution fails.
/// </summary>
public static class UnresolvedKeyVaultReferenceGuard
{
    public const string ReferencePrefix = "@Microsoft.KeyVault(";

    public static readonly string[] ConfigurationKeys =
    [
        "Authentication:ApiKey",
        "Authentication:ControlIdentity:ClientSecret",
        "Authentication:BuilderClientApiKey",
        "Billing:Stripe:SecretKey",
        "Billing:Stripe:WebhookSigningSecret",
        "Billing:Stripe:WebhookSources:0:SigningSecret",
        "Billing:Stripe:WebhookSources:1:SigningSecret",
        "ManagedElsa:Handoff:ActivePrivateKeyPem"
    ];

    public static bool IsUnresolvedKeyVaultReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            && trimmed.EndsWith(')');
    }

    public static void ThrowIfUnresolved(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var unresolved = ConfigurationKeys
            .Where(key => IsUnresolvedKeyVaultReference(configuration[key]))
            .ToArray();
        if (unresolved.Length == 0)
            return;

        throw new InvalidOperationException(
            "Host refused to start because these settings still hold an unresolved Key Vault reference: "
            + string.Join(", ", unresolved)
            + ".");
    }
}
