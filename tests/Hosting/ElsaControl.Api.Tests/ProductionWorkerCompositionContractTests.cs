using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.Api.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using System.Text.RegularExpressions;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

/// <summary>
/// The checked-in production worker composition (infra/control-worker-composition) must compose
/// through the same seams Program.cs uses, against the template authority the API image ships
/// (infra/azure-production), with no Azure access. Tool paths come from the image and pending
/// operator decisions are substituted with documented test values.
/// </summary>
public sealed class ProductionWorkerCompositionContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"elsa-worker-composition-{Guid.NewGuid():N}");
    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string CompositionRoot = Path.Combine(RepositoryRoot, "infra", "control-worker-composition");
    private static readonly string TemplateRoot = Path.Combine(RepositoryRoot, "infra", "azure-production");
    private static readonly IReadOnlyDictionary<string, string> PendingTestValues = new Dictionary<string, string>();

    private readonly Dictionary<string, string?> _settings = Render("worker-settings.template.json");

    [Fact]
    public void Only_the_documented_decisions_are_pending()
    {
        var pending = ReadParameters().Where(pair => pair.Value is null).Select(pair => pair.Key).Order().ToArray();
        Assert.Empty(pending);
        Assert.True(PendingTestValues.Keys.All(pending.Contains));
    }

    [Fact]
    public void Worker_composition_composes_the_runner_lifecycle_ports_and_plan_resolver()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(_settings);

        var authority = AzureProviderRunnerComposition.AddRunner(services, configuration);
        Assert.NotNull(authority);
        Assert.True(AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, authority));
        ElsaInstancePlanResolutionComposition.AddResolver(services, configuration);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAzureProviderRunner));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AzureBicepProviderRunner));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAzureProviderRecoveryObserver));

        Assert.Equal("ada5e428-c5d8-4daf-b7f9-9f2c79d23815", authority.Options.AzureCliClientId);
        Assert.Equal("a54cd7b1-3d13-48ce-9dce-5ae013142c85", authority.Scope.SubscriptionId);
        Assert.Equal("rg-elsa-cloud-workloads-platform-prod-weu", authority.Scope.ResourceGroupName);
        Assert.Equal("valenceruntimeimages", authority.Scope.RegistryName);
        Assert.Equal(AzureProviderRegistryAuthorityMode.Narrow, authority.Options.RegistryAuthorityMode);
        Assert.False(authority.Options.DisposableProofMode);

        using var provider = services.BuildServiceProvider();
        var instanceProvider = provider.GetRequiredService<AzureElsaInstanceProviderOptions>();
        Assert.Equal(authority.TemplateFingerprint, instanceProvider.TemplateFingerprint);
        Assert.Equal(authority.ProviderScopeFingerprint, instanceProvider.ProviderScopeFingerprint);
        Assert.Equal(1, instanceProvider.ResourceGroupNamingVersion);
        Assert.Equal("unrestricted", provider.GetRequiredService<ElsaInstancePlanResolutionOptions>().DefaultEgress);
    }

    [Fact]
    public void Template_fingerprint_is_the_checked_in_azure_production_authority()
    {
        var authority = AzureProviderRunnerComposition.AddRunner(new ServiceCollection(), Configuration(_settings));
        Assert.NotNull(authority);
        var expected = new AzureProviderRunnerOptions { TemplateRoot = TemplateRoot }.ComputeTemplateAuthorityFingerprint();
        Assert.Equal(expected, authority.TemplateFingerprint);
    }

    [Fact]
    public void Control_plane_origin_is_a_valid_plan_authority()
    {
        var options = Configuration(_settings).GetSection(ElsaInstancePlanAuthorityOptions.ConfigurationSection).Get<ElsaInstancePlanAuthorityOptions>();
        Assert.NotNull(options);
        Assert.True(options.TryGetOrigin(out var origin));
        Assert.StartsWith("https://", origin, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_composition_binds_the_runtime_handoff_to_Control_and_into_the_provider_scope()
    {
        // A runner-section value must never retarget the Control trust anchor the runtime redeems at.
        var settings = new Dictionary<string, string?>(_settings)
        {
            ["Deployment:AzureProvider:Runner:ManagedHandoff:ControlBaseUrl"] = "https://elsewhere.example.test",
            ["Deployment:AzureProvider:Runner:ManagedHandoff:ControlContinuationUrl"] = "https://elsewhere.example.test/admin/runtimes",
            ["Deployment:AzureProvider:Runner:ManagedHandoff:RuntimeMaximumLifetime"] = "01:00:00",
            ["Deployment:AzureProvider:Runner:ManagedHandoff:RuntimePermissions:0"] = "read:*"
        };
        Assert.True(Configuration(settings).GetSection(ElsaInstancePlanAuthorityOptions.ConfigurationSection)
            .Get<ElsaInstancePlanAuthorityOptions>()!.TryGetOrigin(out var origin));

        var authority = AzureProviderRunnerComposition.AddRunner(new ServiceCollection(), Configuration(settings));

        var handoff = authority!.Options.ManagedHandoff;
        Assert.NotNull(handoff);
        Assert.Equal(origin, handoff.ControlBaseUrl);
        Assert.Equal(origin + "/admin/runtimes", handoff.ControlContinuationUrl);
        Assert.Equal(TimeSpan.FromHours(8), handoff.RuntimeMaximumLifetime);
        Assert.Equal(["*"], handoff.RuntimePermissions);
        var withoutHandoff = authority.Options with { ManagedHandoff = null };
        Assert.NotEqual(withoutHandoff.ComputeProviderScopeFingerprint(authority.Scope), authority.ProviderScopeFingerprint);
    }

    [Fact]
    public void Worker_composition_without_a_Control_origin_composes_no_runtime_handoff()
    {
        var settings = new Dictionary<string, string?>(_settings) { ["ControlPlane:Origin"] = null };

        var authority = AzureProviderRunnerComposition.AddRunner(new ServiceCollection(), Configuration(settings));

        Assert.Null(authority!.Options.ManagedHandoff);
    }

    [Fact]
    public async Task Worker_triplet_passes_the_startup_validator_with_a_succeeding_preflight()
    {
        var configuration = Configuration(_settings);
        var services = new ServiceCollection();
        var authority = AzureProviderRunnerComposition.AddRunner(services, configuration);
        AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, authority);
        using var provider = services.BuildServiceProvider();

        var validator = new ManagedAzureProviderConfigurationValidator(
            Options.Create(configuration.GetSection(ElsaInstanceLifecycleWorkerOptions.ConfigurationSection).Get<ElsaInstanceLifecycleWorkerOptions>()!),
            Options.Create(configuration.GetSection(AzureProviderOperationOptions.ConfigurationSection).Get<AzureProviderOperationOptions>()!),
            provider.GetRequiredService<AzureElsaInstanceProviderOptions>(),
            new SucceedingPreflight());

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Rollback_file_turns_every_worker_off_and_keeps_the_provider_fail_closed()
    {
        var rollback = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(CompositionRoot, "worker-rollback.json")));
        var settings = new Dictionary<string, string?>(_settings);
        foreach (var entry in rollback.EnumerateArray())
            settings[entry.GetProperty("name").GetString()!.Replace("__", ":", StringComparison.Ordinal)] = entry.GetProperty("value").GetString();
        var configuration = Configuration(settings);

        var services = new ServiceCollection();
        Assert.Null(AzureProviderRunnerComposition.AddRunner(services, configuration));
        Assert.False(AzureInstanceLifecycleComposition.AddProviderPorts(services, configuration, null));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<UnconfiguredAzureProviderRunner>(scope.ServiceProvider.GetRequiredService<IAzureProviderRunner>());

        var validator = new ManagedAzureProviderConfigurationValidator(
            Options.Create(configuration.GetSection(ElsaInstanceLifecycleWorkerOptions.ConfigurationSection).Get<ElsaInstanceLifecycleWorkerOptions>()!),
            Options.Create(configuration.GetSection(AzureProviderOperationOptions.ConfigurationSection).Get<AzureProviderOperationOptions>()!));
        await validator.StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("Deployment:AzureProvider:Secrets:0:Value", "runtime-only-secret", "Azure provider worker configuration must not contain raw secret values.")]
    [InlineData("Deployment:AzureProvider:Runner:DisposableProofMode", "true", "The production Azure provider worker must not use disposable proof mode.")]
    public void Composition_rejects_raw_secret_values_and_disposable_proof_mode(string key, string value, string message)
    {
        var settings = new Dictionary<string, string?>(_settings) { [key] = value };
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AzureProviderRunnerComposition.AddRunner(new ServiceCollection(), Configuration(settings)));
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void Release_verification_composition_composes_the_configured_verifier()
    {
        var settings = Render("release-verification.template.json");
        Assert.Equal("valenceruntimeimages.azurecr.io", settings["ReleaseCatalog:Verification:RegistryHost"]);
        Assert.Equal("release-manifests/release-manifest", settings["ReleaseCatalog:Verification:Repository"]);
        Assert.Equal("f35bcd45-7991-4e24-84f1-e964394501ad", settings["ReleaseCatalog:Verification:TenantId"]);
        Assert.Equal("/opt/elsa-control/verification/cosign", settings["ReleaseCatalog:Verification:CosignPath"]);
        Assert.Equal("/opt/elsa-control/verification/trusted-root.json", settings["ReleaseCatalog:Verification:TrustedRootPath"]);

        // The image owns cosign and the trust root; digest-matched fixtures stand in for them.
        Directory.CreateDirectory(_directory);
        var cosign = Path.Combine(_directory, "cosign");
        var root = Path.Combine(_directory, "trusted-root.json");
        File.WriteAllText(cosign, "fixture; never executed by composition");
        File.WriteAllText(root, "{}");
        settings["ReleaseCatalog:Verification:CosignPath"] = cosign;
        settings["ReleaseCatalog:Verification:CosignSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(cosign)));
        settings["ReleaseCatalog:Verification:TrustedRootPath"] = root;
        settings["ReleaseCatalog:Verification:TrustedRootSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(root)));

        var services = new ServiceCollection();
        ReleaseManifestVerifierComposition.AddVerifier(services, Configuration(settings));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ConfiguredAcrReleaseManifestSignatureVerifier>(provider.GetRequiredService<IReleaseManifestSignatureVerifier>());
        Assert.IsType<AcrReleaseRegistryReader>(provider.GetRequiredService<IReleaseRegistryReader>());
        Assert.IsType<SigstoreReleaseManifestBundleVerifier>(provider.GetRequiredService<IReleaseManifestBundleVerifier>());
    }

    [Fact]
    public void Composition_never_carries_image_owned_tool_paths_or_raw_values()
    {
        var template = ReadTemplate();
        Assert.DoesNotContain(template.Keys, key => key.EndsWith("__Value", StringComparison.Ordinal));
        Assert.DoesNotContain(template.Keys, key =>
            key is "Deployment__AzureProvider__Runner__AzureCliPath" or "Deployment__AzureProvider__Runner__SqlCmdPath"
                or "Deployment__AzureProvider__Runner__CurlPath" or "Deployment__AzureProvider__Runner__TemplateRoot");
    }

    private static Dictionary<string, string?> Render(string templateName)
    {
        var parameters = ReadParameters();
        var settings = new Dictionary<string, string?>();
        foreach (var (key, raw) in ReadTemplate(templateName))
        {
            var value = Placeholder.Replace(raw, match =>
            {
                var name = match.Groups[1].Value;
                if (PendingTestValues.TryGetValue(name, out var testValue))
                    return testValue;
                return parameters.TryGetValue(name, out var resolved) && resolved is not null
                    ? resolved
                    : throw new InvalidOperationException($"Parameter {name} is unresolved.");
            });
            settings[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        if (templateName.StartsWith("worker-", StringComparison.Ordinal))
        {
            // The image owns the runner tools and template root; the test stands in for the image.
            var tool = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
            settings["Deployment:AzureProvider:Runner:AzureCliPath"] = tool;
            settings["Deployment:AzureProvider:Runner:SqlCmdPath"] = tool;
            settings["Deployment:AzureProvider:Runner:CurlPath"] = tool;
            settings["Deployment:AzureProvider:Runner:TemplateRoot"] = TemplateRoot;
        }
        return settings;
    }

    private static Dictionary<string, string> ReadTemplate(string templateName = "worker-settings.template.json")
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompositionRoot, templateName)));
        return document.RootElement.EnumerateObject()
            .Where(property => !property.Name.StartsWith('$'))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    /// <summary>Resolved parameters map to their value; pending decisions map to null; anything else is malformed.</summary>
    private static Dictionary<string, string?> ReadParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompositionRoot, "worker-settings.parameters.production.json")));
        return document.RootElement.GetProperty("parameters").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value switch
            {
                { ValueKind: JsonValueKind.String } value => value.GetString(),
                { ValueKind: JsonValueKind.Object } value when value.TryGetProperty("pending", out var issue)
                    && issue.ValueKind == JsonValueKind.String
                    && Regex.IsMatch(issue.GetString()!, "^#[1-9][0-9]{0,5}$") => null,
                _ => throw new InvalidOperationException($"Parameter {property.Name} is neither an identifier nor a pending issue reference.")
            }, StringComparer.Ordinal);
    }

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

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

    private sealed class SucceedingPreflight : IAzureProviderAuthorityPreflight
    {
        public Task<AzureProviderAuthorityPreflightResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureProviderAuthorityPreflightResult(true, "ok", ""));
    }
}
