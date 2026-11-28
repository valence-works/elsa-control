using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using Microsoft.Extensions.Options;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzurePulumiProvisioningBackendSelectorTests
{
    [Fact]
    public void Complete_azure_worker_pins_pulumi()
    {
        var selector = new AzurePulumiProvisioningBackendSelector(
            Options.Create(new ProvisioningOptions { WorkerEnabled = true }),
            Options.Create(CompleteOptions()),
            new StubProvider("azure"));
        Assert.Equal(ElsaInstanceProvisioningBackend.Pulumi, selector.SelectForCreate());
    }

    [Theory]
    [InlineData(false, "azure", true)]
    [InlineData(true, "unconfigured", true)]
    [InlineData(true, "fake", true)]
    [InlineData(true, "azure", false)]
    public void Missing_worker_provider_or_options_pin_bicep(bool workerEnabled, string providerId, bool complete)
    {
        var selector = new AzurePulumiProvisioningBackendSelector(
            Options.Create(new ProvisioningOptions { WorkerEnabled = workerEnabled }),
            Options.Create(complete ? CompleteOptions() : new AzurePulumiDispatchOptions()),
            new StubProvider(providerId));
        Assert.Equal(ElsaInstanceProvisioningBackend.Bicep, selector.SelectForCreate());
    }

    [Theory]
    [InlineData(null, ElsaInstanceProvisioningBackend.Bicep, true)]
    [InlineData("bicep", ElsaInstanceProvisioningBackend.Bicep, true)]
    [InlineData("pulumi", ElsaInstanceProvisioningBackend.Pulumi, true)]
    [InlineData("unknown", ElsaInstanceProvisioningBackend.Unknown, false)]
    [InlineData("terraform", ElsaInstanceProvisioningBackend.Unknown, false)]
    public void Owner_parse_fails_closed_on_unknown_values(
        string? value,
        ElsaInstanceProvisioningBackend expected,
        bool parsed)
    {
        Assert.Equal(parsed, ElsaInstanceProvisioningBackendText.TryParse(value, out var backend));
        Assert.Equal(expected, backend);
        Assert.Equal(expected, ElsaInstanceProvisioningBackendText.Parse(value));
    }

    [Fact]
    public void Missing_seed_vault_pins_bicep()
    {
        var options = CompleteOptions();
        options.SeedVaultUri = null;
        var selector = new AzurePulumiProvisioningBackendSelector(
            Options.Create(new ProvisioningOptions { WorkerEnabled = true }),
            Options.Create(options),
            new StubProvider("azure"));
        Assert.Equal(ElsaInstanceProvisioningBackend.Bicep, selector.SelectForCreate());
    }

    private static AzurePulumiDispatchOptions CompleteOptions() => new()
    {
        EnvironmentName = "staging",
        StateBackendLocator = "stelsaplatform/pulumi-state",
        SecretsProviderLocator = "kv-elsa-platform/pulumi-secrets",
        CredentialLocator = "workload-identity",
        SqlBootstrapObjectId = Guid.NewGuid().ToString("D"),
        SqlBootstrapLogin = "sql-bootstrap",
        SeedVaultUri = "https://kv.example.vault.azure.net/",
        RegistryLoginServer = "valenceruntimeimages.azurecr.io",
        RegistryId = "/subscriptions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/resourceGroups/rg-valence-runtime/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages"
    };

    private sealed class StubProvider(string providerId) : IInfrastructureProvider
    {
        public string ProviderId { get; } = providerId;
        public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Task<ProviderRunResult> PreviewAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> ProvisionAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> UpdateAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> RefreshAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> CancelAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ProviderRunResult> DestroyAsync(ProvisioningOperation operation, ProvisioningRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
