using ElsaControl.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace ElsaControl.Api.Tests;

public sealed class UnresolvedKeyVaultReferenceGuardTests
{
    [Fact]
    public void Host_refuses_to_start_on_an_unresolved_reference_string()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/Authentication-ApiKey)"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => UnresolvedKeyVaultReferenceGuard.ThrowIfUnresolved(configuration));

        Assert.Contains("Authentication:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unresolved Key Vault reference", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretUri=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_refuses_to_start_when_indexed_webhook_secrets_are_unresolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Billing:Stripe:WebhookSources:0:SigningSecret"] =
                    "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/Billing-Stripe-WebhookSources-control-SigningSecret)",
                ["Billing:Stripe:WebhookSources:1:SigningSecret"] =
                    "@Microsoft.KeyVault(SecretUri=https://vault.example.vault.azure.net/secrets/Billing-Stripe-WebhookSources-hub-SigningSecret)"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => UnresolvedKeyVaultReferenceGuard.ThrowIfUnresolved(configuration));

        Assert.Contains("Billing:Stripe:WebhookSources:0:SigningSecret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Billing:Stripe:WebhookSources:1:SigningSecret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretUri=", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_starts_when_secret_settings_are_resolved_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKey"] = "test-only-key",
                ["Authentication:ControlIdentity:ClientSecret"] = "test-only-secret"
            })
            .Build();

        UnresolvedKeyVaultReferenceGuard.ThrowIfUnresolved(configuration);
    }

    [Fact]
    public void Host_starts_when_the_listed_settings_are_absent()
    {
        UnresolvedKeyVaultReferenceGuard.ThrowIfUnresolved(new ConfigurationBuilder().Build());
    }
}
