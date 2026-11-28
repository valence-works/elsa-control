using System.Text.Json;
using System.Text.RegularExpressions;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.Workspace;
using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Providers.Azure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Builds the real API host with the staging Pulumi overlay and both worker
/// flags on. CI never composed that combination, so the ambiguous
/// <see cref="AzureProviderOperationStepLedger"/> constructors reached staging
/// as a startup crash (#1074).
/// </summary>
public sealed class StagingPulumiHostCompositionTests : IDisposable
{
    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string CompositionRoot = Path.Combine(RepositoryRoot, "infra", "control-worker-composition");
    private static readonly string TemplateRoot = Path.Combine(RepositoryRoot, "infra", "azure-production");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"elsa-pulumi-host-{Guid.NewGuid():N}");

    [Fact]
    public void Staging_pulumi_worker_host_resolves_the_step_ledger_and_hosted_workers()
    {
        using var application = new StagingPulumiHostApplication(HostValues());
        var services = application.Services;

        Assert.IsType<AzureProviderOperationStepLedger>(services.GetRequiredService<IProvisioningStepLedger>());

        var hosted = services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, service => service is ProvisioningOperationHostedService);
        Assert.Contains(hosted, service => service is ProvisioningProviderGuard);
        Assert.Contains(hosted, service => service is AzureProvisioningSecretGuard);
        Assert.Contains(hosted, service => service is AzureProviderOperationHostedService);

        var dispatch = services.GetRequiredService<IOptions<AzurePulumiDispatchOptions>>().Value;
        Assert.True(dispatch.IsComplete);
        var secrets = AzureKeyVaultSecretsProvider.From(
            new SecretsProviderReference(
                SecretsProviderReference.AzureKeyVaultKind,
                dispatch.SecretsProviderLocator!));
        Assert.Equal("ecseedkvtud53zotij43k", secrets.VaultName);
        Assert.Equal("pulumi-secrets", secrets.KeyName);
        Assert.Equal(dispatch.SecretsProviderLocator, secrets.SecretsProviderUrl);

        var resolver = Assert.IsType<AzureKeyVaultProvisioningSecretResolver>(
            services.GetRequiredService<IProvisioningSecretResolver>());
        Assert.True(resolver.IsReady);
        Assert.Equal("valenceruntimeimages.azurecr.io", dispatch.RegistryLoginServer);
        Assert.Contains("/registries/valenceruntimeimages", dispatch.RegistryId, StringComparison.Ordinal);

        var overlayed = ProvisioningRequestRefresh.OverlayRegistry(
            new Dictionary<string, string>(StringComparer.Ordinal),
            services.GetRequiredService<IOptions<ProvisioningOptions>>().Value);
        Assert.Equal(dispatch.RegistryLoginServer, overlayed["registryLoginServer"]);
        Assert.Equal(dispatch.RegistryId, overlayed["registryId"]);
    }

    private Dictionary<string, string?> HostValues()
    {
        Directory.CreateDirectory(_directory);
        var catalog = Path.Combine(_directory, "catalog.db");
        var keys = Path.Combine(_directory, "data-protection-keys");
        Directory.CreateDirectory(keys);

        var values = RenderStagingWorkers();
        foreach (var (key, value) in PulumiOverlay())
            values[key] = value;

        values["Database:Provider"] = "Sqlite";
        values["ConnectionStrings:Catalog"] = $"Data Source={catalog}";
        values["DataProtection:KeysPath"] = keys;
        values[ApiKeyAuthenticationDefaults.ConfigurationKey] = "local-dev-key";
        values[BuilderClientApiKeyAuthenticationDefaults.ConfigurationKey] = "builder-dev-key";
        values[$"{ControlIdentityDefaults.ConfigurationSection}:Provider"] = ControlIdentityProviderKind.GenericOidc.ToString();
        values[$"{ControlIdentityDefaults.ConfigurationSection}:Authority"] = "";
        values[$"{ControlIdentityDefaults.ConfigurationSection}:Audience"] = ControlApiTestApplication.TestControlIdentityAudience;
        values[$"{ControlIdentityDefaults.ConfigurationSection}:Issuer"] = ControlApiTestApplication.TestControlIdentityIssuer;
        values[$"{ControlIdentityDefaults.ConfigurationSection}:SymmetricSigningKey"] = ControlApiTestApplication.TestControlIdentitySigningKey;
        values[$"{ControlIdentityDefaults.ConfigurationSection}:ClientId"] = "";
        values[$"{ControlIdentityDefaults.ConfigurationSection}:ClientSecret"] = "";
        values[$"{ControlIdentityDefaults.ConfigurationSection}:RequireHttpsMetadata"] = "false";
        values[TrustedHeaderWorkspaceIdentityReader.EnabledConfigurationKey] = "false";
        values[StripeBillingOptions.ExpectedModeConfigurationKey] = StripeBillingOptions.TestMode;
        values["Deployment:EngineVerification:Enabled"] = "false";
        values["Deployment:QueueWorker:Enabled"] = "false";
        values["Deployment:WebhookDispatch:Enabled"] = "false";
        values["Sync:Scheduled:Enabled"] = "false";
        values["Billing:Lifecycle:Enabled"] = "false";
        values["ManagedElsa:Handoff:Enabled"] = "false";
        values["Hosting:BuildOnly"] = "true";
        return values;
    }

    private static Dictionary<string, string?> RenderStagingWorkers()
    {
        var parameters = ReadParameters();
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, raw) in ReadTemplate())
        {
            var value = Placeholder.Replace(raw, match =>
            {
                var name = match.Groups[1].Value;
                return parameters.TryGetValue(name, out var resolved) && resolved is not null
                    ? resolved
                    : throw new InvalidOperationException($"Parameter {name} is unresolved.");
            });
            settings[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        var tool = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        settings["Deployment:AzureProvider:Runner:AzureCliPath"] = tool;
        settings["Deployment:AzureProvider:Runner:SqlCmdPath"] = tool;
        settings["Deployment:AzureProvider:Runner:CurlPath"] = tool;
        settings["Deployment:AzureProvider:Runner:TemplateRoot"] = TemplateRoot;
        settings["Deployment:AzureProvider:BatchSize"] = "1";
        settings["Deployment:AzureProvider:Runner:CommandTimeout"] = "00:45:00";
        return settings;
    }

    private static Dictionary<string, string?> PulumiOverlay()
    {
        var parameters = ReadParameters();
        string Parameter(string name) =>
            parameters.TryGetValue(name, out var value) && value is not null
                ? value
                : throw new InvalidOperationException($"Parameter {name} is unresolved.");

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Provisioning:WorkerEnabled"] = "true",
            ["Provisioning:EnvironmentName"] = "staging",
            ["ARM_SUBSCRIPTION_ID"] = Parameter("WorkloadSubscriptionId"),
            ["ARM_TENANT_ID"] = Parameter("TenantId"),
            ["ARM_USE_MSI"] = "true",
            ["ARM_CLIENT_ID"] = Parameter("ProvisionerClientId"),
            ["Provisioning:CredentialLocator"] = Parameter("ProvisionerClientId"),
            ["Provisioning:SqlBootstrapObjectId"] = Parameter("ProvisionerPrincipalId"),
            ["Provisioning:SqlBootstrapLogin"] = Parameter("ProvisionerIdentityName"),
            ["Provisioning:StateBackendLocator"] = "stpulumitud53zotij43k/pulumi-state",
            ["Provisioning:SecretsProviderLocator"] = "azurekeyvault://ecseedkvtud53zotij43k.vault.azure.net/keys/pulumi-secrets",
            ["Provisioning:SeedVaultUri"] = "https://ecseedkvtud53zotij43k.vault.azure.net/",
            ["Provisioning:RegistryLoginServer"] = "valenceruntimeimages.azurecr.io",
            ["Provisioning:RegistryId"] = $"/subscriptions/{Parameter("RegistrySubscriptionId")}/resourceGroups/{Parameter("RegistryResourceGroupName")}/providers/Microsoft.ContainerRegistry/registries/{Parameter("RegistryName")}"
        };
    }

    private static Dictionary<string, string> ReadTemplate()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompositionRoot, "worker-settings.template.json")));
        return document.RootElement.EnumerateObject()
            .Where(property => !property.Name.StartsWith('$'))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    private static Dictionary<string, string?> ReadParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompositionRoot, "worker-settings.parameters.staging.json")));
        return document.RootElement.GetProperty("parameters").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value switch
            {
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                _ => throw new InvalidOperationException($"Parameter {property.Name} is not an identifier.")
            }, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "infra", "control-worker-composition")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root with infra/control-worker-composition was not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class StagingPulumiHostApplication(IReadOnlyDictionary<string, string?> configuration)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development allows SQLite; Testing skips the Pulumi worker registrations
            // that crashed staging. UseSetting is visible to Program.cs; an in-memory
            // source added here is not always applied before that code reads flags.
            builder.UseEnvironment("Development");
            foreach (var (key, value) in configuration)
                builder.UseSetting(key, value ?? "");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseDefaultServiceProvider((_, options) =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
            // Hosting:BuildOnly stops Program after Build(), before migrations and
            // app.Run(). Skipping Start keeps hosted workers unstarted if that
            // boundary is removed.
            return builder.Build();
        }
    }
}
