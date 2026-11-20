using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.ReleaseCatalog;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Opt-in production-composition proof for the release admission boundary. The ordinary test
/// run never reads the proof configuration or contacts a registry; the custom fact marks this
/// test skipped unless an operator explicitly enables it. The operator runs this fact in three
/// separate processes with staged files for Admit, wrong-subject RejectPolicy and wrong-issuer
/// RejectPolicy; no process mutates configuration at runtime.
/// </summary>
public sealed class ProductionReleaseManifestAdmissionProofTests
{
    private const string Gate = "ELSA_CONTROL_LIVE_RELEASE_ADMISSION_PROOF";
    private const string ConfigurationPathVariable = "ELSA_CONTROL_LIVE_RELEASE_ADMISSION_CONFIG";
    private const string EnvironmentApiKeyVariable = "ELSA_CONTROL_LIVE_RELEASE_ADMISSION_API_KEY";
    private const string StandardEnvironmentApiKeyVariable = "Authentication__ApiKey";
    private const string ProofConfigurationSection = "ReleaseCatalog:AdmissionProof";
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";
    private const string DotNetEnvironmentVariable = "DOTNET_ENVIRONMENT";
    private const string ContentRootVariable = "ASPNETCORE_TEST_CONTENTROOT_ELSACONTROL_API";
    private const string ScenarioConfigurationKey = $"{ProofConfigurationSection}:Scenario";
    private const int MaximumPublicationBytes = 64 * 1024;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumBundleBytes = 256 * 1024;
    private const int MinimumTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 1_800;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions FixtureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };
    private static readonly string[] ProtectedConfigurationSections =
    [
        "ConnectionStrings",
        "Database",
        "DataProtection",
        "ReleaseCatalog:AdmissionProof",
        "ReleaseCatalog:Verification",
        "ReleaseCatalog:Admission",
        "Deployment:AzureProvider",
        "Deployment:ElsaInstanceLifecycle",
        "Deployment:QueueWorker",
        "Deployment:WebhookDispatch",
        "Deployment:EngineVerification",
        "Billing:Lifecycle",
        "Sync:Scheduled",
        "ManagedElsa:Handoff",
        "Authentication:Admin",
        "Authentication:WorkspaceTrustedHeaders"
    ];
    private static readonly (string Key, bool DefaultValue)[] HostedServiceFlags =
    [
        ("Deployment:QueueWorker:Enabled", false),
        ("Deployment:ElsaInstanceLifecycle:Enabled", false),
        ("Billing:Lifecycle:Enabled", false),
        ("Deployment:AzureProvider:WorkerEnabled", false),
        ("Deployment:AzureProvider:InstanceLifecycle:Enabled", false),
        ("Deployment:WebhookDispatch:Enabled", false),
        ("Deployment:EngineVerification:Enabled", true),
        ("Sync:Scheduled:Enabled", false),
        ("ManagedElsa:Handoff:Enabled", false)
    ];

    [Fact]
    public void Production_packaging_guard_requires_early_environment_and_staged_config()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsa-control-live-proof-content"));
        var stagedConfiguration = Path.Combine(contentRoot, "appsettings.Production.json");

        Assert.True(HasProductionPackagingContract("Production", null, contentRoot, stagedConfiguration));
        Assert.True(HasProductionPackagingContract("production", "Production", contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Development", null, contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", "Development", contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", string.Empty, contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", null, "relative-content-root", stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", null, contentRoot, Path.Combine(contentRoot, "live-proof.json")));
    }

    [Fact]
    public void Effective_configuration_guard_accepts_matching_staged_values()
    {
        var values = HostedServiceFlags.ToDictionary(
            flag => flag.Key,
            flag => (string?)"false",
            StringComparer.OrdinalIgnoreCase);
        values["Deployment:EngineVerification:Enabled"] = "false";

        var staged = InMemoryConfiguration(values);
        var effective = InMemoryConfiguration(values);

        ValidateEffectiveConfiguration(staged, effective);
    }

    [Fact]
    public void Effective_configuration_guard_rejects_database_authority_and_worker_overrides()
    {
        var stagedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Catalog"] = "/tmp/elsa-control-live-proof.db",
            ["ReleaseCatalog:Verification:TrustedRootSha256"] = "trusted-root-digest",
            ["Deployment:EngineVerification:Enabled"] = "false"
        };

        var changedKeys = new[]
        {
            "Database:Provider",
            "ConnectionStrings:Catalog",
            "ReleaseCatalog:Verification:TrustedRootSha256",
            "Deployment:EngineVerification:Enabled"
        };
        foreach (var changedKey in changedKeys)
        {
            var effectiveValues = new Dictionary<string, string?>(stagedValues, StringComparer.OrdinalIgnoreCase)
            {
                [changedKey] = changedKey == "Deployment:EngineVerification:Enabled"
                    ? "true"
                    : "different"
            };

            Assert.Throws<ProofConfigurationException>(() => ValidateEffectiveConfiguration(
                InMemoryConfiguration(stagedValues), InMemoryConfiguration(effectiveValues)));
        }
    }

    [Fact]
    public void Effective_configuration_guard_rejects_every_conditional_hosted_service()
    {
        foreach (var (key, _) in HostedServiceFlags)
        {
            var stagedValues = new Dictionary<string, string?> { [key] = "false" };
            var effectiveValues = new Dictionary<string, string?> { [key] = "true" };

            Assert.Throws<ProofConfigurationException>(() => ValidateEffectiveConfiguration(
                InMemoryConfiguration(stagedValues), InMemoryConfiguration(effectiveValues)));
        }
    }

    [Fact]
    public void Proof_scenario_defaults_to_admit_and_rejects_unknown_values()
    {
        Assert.Equal(ProofScenario.Admit, ParseScenario(null));
        Assert.Equal(ProofScenario.Admit, ParseScenario("admit"));
        Assert.Equal(ProofScenario.RejectPolicy, ParseScenario("RejectPolicy"));
        Assert.Throws<ProofConfigurationException>(() => ParseScenario("unexpected"));
    }

    [Fact]
    public void Publication_deserialization_rejects_excessive_json_depth()
    {
        var bytes = StrictUtf8.GetBytes(new string('[', FixtureJsonOptions.MaxDepth + 1) +
                                        new string(']', FixtureJsonOptions.MaxDepth + 1));

        Assert.Throws<ProofConfigurationException>(() => DeserializePublication(bytes));
    }

    [ProductionReleaseManifestAdmissionFact]
    public async Task Production_composition_admits_replays_and_rejects_without_catalog_mutation()
    {
        ProductionReleaseManifestAdmissionApplication? application = null;
        var stage = "preflight";

        try
        {
            var inputs = LoadInputs();
            stage = "startup";
            application = new ProductionReleaseManifestAdmissionApplication(inputs.ContentRoot);
            using var timeout = new CancellationTokenSource(inputs.Timeout);
            var cancellationToken = timeout.Token;
            var client = application.CreateClient();
            client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, inputs.ApiKey);

            using (var health = await client.GetAsync("/health", cancellationToken))
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var before = await SnapshotAsync(application.Services, cancellationToken);
            if (inputs.Scenario == ProofScenario.RejectPolicy)
            {
                stage = "policy-rejection";
                Assert.Empty(before);
                using var rejection = await client.PostControlJsonAsync(
                    "/api/admin/release-catalog/manifests", inputs.Request, cancellationToken);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, rejection.StatusCode);
                Assert.Equal(before, await SnapshotAsync(application.Services, cancellationToken));
                return;
            }

            Assert.DoesNotContain(before, identity => identity.ManifestDigest == inputs.Publication.Digest);

            stage = "admission";
            using var first = await client.PostControlJsonAsync(
                "/api/admin/release-catalog/manifests",
                inputs.Request,
                cancellationToken);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstResult = await ReadAdmissionAsync(first, cancellationToken);
            Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, firstResult.Status);
            Assert.NotEmpty(firstResult.Entries);
            AssertIdentities(firstResult.Entries, inputs);

            var afterFirst = await SnapshotAsync(application.Services, cancellationToken);
            var admitted = afterFirst
                .Where(identity => identity.ManifestDigest == inputs.Publication.Digest)
                .ToArray();
            Assert.NotEmpty(admitted);
            Assert.All(admitted, identity => AssertExpectedIdentity(identity, inputs));

            stage = "replay";
            using var replay = await client.PostControlJsonAsync(
                "/api/admin/release-catalog/manifests",
                inputs.Request,
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayResult = await ReadAdmissionAsync(replay, cancellationToken);
            Assert.Equal(GovernedReleaseCatalogWriteStatus.Unchanged, replayResult.Status);
            Assert.Equal(firstResult.Entries.Count, replayResult.Entries.Count);
            AssertIdentities(replayResult.Entries, inputs);

            var afterReplay = await SnapshotAsync(application.Services, cancellationToken);
            Assert.Equal(afterFirst, afterReplay);

            stage = "payload-rejection";
            using var tamperedPayload = await client.PostControlJsonAsync(
                "/api/admin/release-catalog/manifests",
                inputs.Request with { Payload = Tamper(inputs.Request.Payload!) },
                cancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tamperedPayload.StatusCode);
            var afterTamperedPayload = await SnapshotAsync(application.Services, cancellationToken);
            Assert.Equal(afterFirst, afterTamperedPayload);

            stage = "mutable-reference-rejection";
            using var mutableReference = await client.PostControlJsonAsync(
                "/api/admin/release-catalog/manifests",
                inputs.Request with
                {
                    Reference = inputs.Request.Reference![..inputs.Request.Reference!.IndexOf('@')] + ":mutable"
                },
                cancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, mutableReference.StatusCode);
            var afterMutableReference = await SnapshotAsync(application.Services, cancellationToken);
            Assert.Equal(afterFirst, afterMutableReference);

        }
        catch (OperationCanceledException)
        {
            throw new XunitException($"The opt-in production release-admission proof timed out at {stage}.");
        }
        catch (Exception)
        {
            // Do not surface HTTP bodies, configuration, API keys, transport errors or raw
            // verifier diagnostics through the test failure. The operator can inspect the
            // host's separately retained, value-free test-run status.
            throw new XunitException($"The opt-in production release-admission proof failed at {stage}.");
        }
        finally
        {
            if (application is not null)
            {
                try
                {
                    await application.DisposeAsync();
                }
                catch (Exception)
                {
                    // Preserve the value-free proof result.
                }
            }
        }
    }

    private static async Task<AdminReleaseCatalogAdmissionResponse> ReadAdmissionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var admission = await response.Content.ReadControlJsonAsync<AdminReleaseCatalogAdmissionResponse>(cancellationToken);
        return admission ?? throw new InvalidDataException();
    }

    private static void AssertIdentities(
        IReadOnlyList<ReleaseCatalogEntryResponse> entries,
        ProofInputs inputs)
    {
        Assert.All(entries, entry =>
        {
            Assert.Equal(inputs.Publication.Reference, entry.ManifestReference);
            Assert.Equal(inputs.Publication.Digest, entry.ManifestDigest);
            Assert.Equal(inputs.Publication.PayloadDigest, entry.PayloadDigest);
            Assert.Equal(inputs.Publication.SignatureEvidence.Reference, entry.SignatureEvidenceReference);
            Assert.Equal(inputs.Publication.SignatureEvidence.Digest, entry.SignatureEvidenceDigest);
            Assert.Equal(inputs.RegistryClass, entry.RegistryClass);
        });
    }

    private static void AssertExpectedIdentity(CatalogIdentity identity, ProofInputs inputs)
    {
        Assert.Equal(inputs.Publication.Reference, identity.ManifestReference);
        Assert.Equal(inputs.Publication.Digest, identity.ManifestDigest);
        Assert.Equal(inputs.Publication.PayloadDigest, identity.PayloadDigest);
        Assert.Equal(inputs.Publication.SignatureEvidence.Reference, identity.SignatureEvidenceReference);
        Assert.Equal(inputs.Publication.SignatureEvidence.Digest, identity.SignatureEvidenceDigest);
        Assert.Equal(inputs.RegistryClass, identity.RegistryClass);
    }

    private static async Task<IReadOnlyList<CatalogIdentity>> SnapshotAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGovernedReleaseCatalogStore>();
        var entries = await store.QueryAsync(new GovernedReleaseCatalogQuery(), cancellationToken);
        return entries
            .Select(entry => new CatalogIdentity(
                entry.ManifestReference,
                entry.ManifestDigest,
                entry.PayloadDigest,
                entry.SignatureEvidenceReference,
                entry.SignatureEvidenceDigest,
                entry.RegistryClass,
                entry.Topology.Id,
                entry.Distribution.Id,
                entry.Distribution.Generation,
                entry.Distribution.ReleaseLine,
                entry.Distribution.ReleaseVersion,
                entry.Distribution.Channel,
                entry.Distribution.ProducerLifecycle,
                entry.CatalogLifecycle,
                entry.Distribution.SourceRepository,
                entry.Distribution.SourceCommit,
                entry.Distribution.SourceRunId,
                entry.ComponentDeclarations?.Digest,
                JsonSerializer.Serialize(entry, FixtureJsonOptions)))
            .OrderBy(identity => identity.ManifestDigest, StringComparer.Ordinal)
            .ThenBy(identity => identity.TopologyId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProofInputs LoadInputs()
    {
        var aspNetCoreEnvironment = Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable);
        var dotNetEnvironment = Environment.GetEnvironmentVariable(DotNetEnvironmentVariable);
        var contentRootValue = Environment.GetEnvironmentVariable(ContentRootVariable);
        var configurationPathValue = Environment.GetEnvironmentVariable(ConfigurationPathVariable);
        if (!HasProductionPackagingContract(
                aspNetCoreEnvironment, dotNetEnvironment, contentRootValue, configurationPathValue))
            throw new ProofConfigurationException();

        var contentRoot = RequiredAbsoluteDirectory(contentRootValue);
        var configurationPath = RequiredAbsoluteFile(configurationPathValue);
        var expectedConfigurationPath = Path.GetFullPath(Path.Combine(contentRoot, "appsettings.Production.json"));
        if (!string.Equals(configurationPath, expectedConfigurationPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ProofConfigurationException();

        var stagedConfiguration = BuildConfiguration(contentRoot, includeEnvironmentVariables: false);
        var configuration = BuildConfiguration(contentRoot, includeEnvironmentVariables: true);
        ValidateEffectiveConfiguration(stagedConfiguration, configuration);
        EnsureHostedServicesDisabled(stagedConfiguration);
        EnsureHostedServicesDisabled(configuration);

        if (!configuration.GetValue<bool>("ReleaseCatalog:Verification:Enabled"))
            throw new ProofConfigurationException();

        ValidateIsolatedSqliteDatabase(configuration);
        var publicationPath = RequiredAbsoluteFile(configuration[$"{ProofConfigurationSection}:PublicationPath"]);
        var payloadPath = RequiredAbsoluteFile(configuration[$"{ProofConfigurationSection}:PayloadPath"]);
        var subjectPath = RequiredAbsoluteFile(configuration[$"{ProofConfigurationSection}:SubjectPath"]);
        var signatureEvidencePath = RequiredAbsoluteFile(configuration[$"{ProofConfigurationSection}:SignatureEvidencePath"]);

        var publication = DeserializePublication(ReadBoundedFile(publicationPath, MaximumPublicationBytes));
        var payloadBytes = ReadBoundedFile(payloadPath, MaximumManifestBytes);
        var subjectBytes = ReadBoundedFile(subjectPath, MaximumManifestBytes);
        var signatureEvidenceBytes = ReadBoundedFile(signatureEvidencePath, MaximumBundleBytes);
        var payload = StrictUtf8.GetString(payloadBytes);

        if (!string.Equals(publication.ArtifactType, ReleaseRegistryProtocol.ReleaseArtifactType, StringComparison.Ordinal) ||
            !ReleaseRegistryProtocol.IsDigest(publication.Digest) ||
            !ReleaseRegistryProtocol.IsDigest(publication.PayloadDigest) ||
            !ReleaseRegistryProtocol.IsDigest(publication.SignatureEvidence.Digest) ||
            string.IsNullOrWhiteSpace(publication.Reference) ||
            string.IsNullOrWhiteSpace(publication.SignatureEvidence.Reference) ||
            !string.Equals(Digest(subjectBytes), publication.Digest, StringComparison.Ordinal) ||
            !string.Equals(Digest(payloadBytes), publication.PayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(Digest(signatureEvidenceBytes), publication.SignatureEvidence.Digest, StringComparison.Ordinal))
            throw new ProofConfigurationException();

        var apiKey = configuration[ApiKeyAuthenticationDefaults.ConfigurationKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable(StandardEnvironmentApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable(EnvironmentApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(char.IsControl))
            throw new ProofConfigurationException();

        var registryClass = configuration[$"{ReleaseCatalogAdmissionOptions.ConfigurationSection}:RegistryClass"]?.Trim();
        if (string.IsNullOrWhiteSpace(registryClass) || registryClass.Any(char.IsControl))
            throw new ProofConfigurationException();

        var timeoutSeconds = ParseTimeout(configuration[$"{ProofConfigurationSection}:TimeoutSeconds"]);
        var scenario = ParseScenario(configuration[ScenarioConfigurationKey]);
        return new(
            contentRoot,
            apiKey,
            registryClass,
            publication,
            new AdminReleaseManifestIngestionRequest(publication.Reference, publication.Digest, payload),
            TimeSpan.FromSeconds(timeoutSeconds),
            scenario);
    }

    private static ReleaseManifestPublication DeserializePublication(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var publication = JsonSerializer.Deserialize<ReleaseManifestPublication>(
                bytes, FixtureJsonOptions);
            return publication is { SignatureEvidence: not null }
                ? publication
                : throw new ProofConfigurationException();
        }
        catch (JsonException)
        {
            throw new ProofConfigurationException();
        }
    }

    private static IConfiguration BuildConfiguration(string contentRoot, bool includeEnvironmentVariables)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Production.json", optional: false, reloadOnChange: false);
        if (includeEnvironmentVariables)
            builder.AddEnvironmentVariables();
        return builder.Build();
    }

    private static IConfiguration InMemoryConfiguration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void ValidateEffectiveConfiguration(IConfiguration staged, IConfiguration effective)
    {
        foreach (var section in ProtectedConfigurationSections)
        {
            var stagedValues = ConfigurationValues(staged, section);
            var effectiveValues = ConfigurationValues(effective, section);
            if (!stagedValues.SequenceEqual(effectiveValues, StringComparer.Ordinal))
                throw new ProofConfigurationException();
        }
    }

    private static void EnsureHostedServicesDisabled(IConfiguration configuration)
    {
        foreach (var (key, defaultValue) in HostedServiceFlags)
        {
            if (configuration.GetValue(key, defaultValue))
                throw new ProofConfigurationException();
        }
    }

    private static IReadOnlyList<string> ConfigurationValues(IConfiguration configuration, string section) =>
        configuration.GetSection(section)
            .AsEnumerable()
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Key}\0{pair.Value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool HasProductionPackagingContract(
        string? aspNetCoreEnvironment,
        string? dotNetEnvironment,
        string? contentRoot,
        string? configurationPath)
    {
        if (!string.Equals(aspNetCoreEnvironment, "Production", StringComparison.OrdinalIgnoreCase) ||
            (dotNetEnvironment is not null &&
             !string.Equals(dotNetEnvironment, "Production", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(contentRoot) ||
            string.IsNullOrWhiteSpace(configurationPath) ||
            !Path.IsPathFullyQualified(contentRoot) ||
            !Path.IsPathFullyQualified(configurationPath))
            return false;

        try
        {
            var expectedPath = Path.GetFullPath(Path.Combine(contentRoot, "appsettings.Production.json"));
            var suppliedPath = Path.GetFullPath(configurationPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(expectedPath, suppliedPath, comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 || length > maximumBytes)
            throw new ProofConfigurationException();

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length > maximumBytes || bytes.LongLength != length)
            throw new ProofConfigurationException();
        return bytes;
    }

    private static string RequiredAbsoluteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ProofConfigurationException();
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) || IsReparsePoint(fullPath))
            throw new ProofConfigurationException();
        return fullPath;
    }

    private static string RequiredAbsoluteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ProofConfigurationException();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || IsReparsePoint(fullPath))
            throw new ProofConfigurationException();
        return fullPath;
    }

    private static bool IsReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static void ValidateIsolatedSqliteDatabase(IConfiguration configuration)
    {
        if (!string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ProofConfigurationException();

        var connectionString = configuration.GetConnectionString("Catalog");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ProofConfigurationException();

        var connection = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = connection.DataSource?.Trim();
        if (string.IsNullOrWhiteSpace(dataSource) ||
            connection.Mode == SqliteOpenMode.Memory ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(dataSource))
            throw new ProofConfigurationException();

        var databasePath = Path.GetFullPath(dataSource);
        if (File.Exists(databasePath) || File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
            throw new ProofConfigurationException();
    }

    private static int ParseTimeout(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? 300
            : int.TryParse(value, out var seconds) && seconds is >= MinimumTimeoutSeconds and <= MaximumTimeoutSeconds
                ? seconds
                : throw new ProofConfigurationException();

    private static ProofScenario ParseScenario(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "Admit", StringComparison.OrdinalIgnoreCase)
            ? ProofScenario.Admit
            : string.Equals(value.Trim(), "RejectPolicy", StringComparison.OrdinalIgnoreCase)
                ? ProofScenario.RejectPolicy
                : throw new ProofConfigurationException();

    private static string Tamper(string payload) => payload + "\n";

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class ProductionReleaseManifestAdmissionApplication(string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseContentRoot(contentRoot);
        }
    }

    private sealed record ProofInputs(
        string ContentRoot,
        string ApiKey,
        string RegistryClass,
        ReleaseManifestPublication Publication,
        AdminReleaseManifestIngestionRequest Request,
        TimeSpan Timeout,
        ProofScenario Scenario);

    private enum ProofScenario
    {
        Admit,
        RejectPolicy
    }

    private sealed record ReleaseManifestPublication(
        string ArtifactType,
        string Reference,
        string Digest,
        string PayloadDigest,
        SignatureEvidencePublication SignatureEvidence);

    private sealed record SignatureEvidencePublication(string Reference, string Digest);

    private sealed record CatalogIdentity(
        string ManifestReference,
        string ManifestDigest,
        string PayloadDigest,
        string SignatureEvidenceReference,
        string SignatureEvidenceDigest,
        string RegistryClass,
        string TopologyId,
        string DistributionId,
        string DistributionGeneration,
        string ReleaseLine,
        string ReleaseVersion,
        string Channel,
        string ProducerLifecycle,
        string CatalogLifecycle,
        string SourceRepository,
        string SourceCommit,
        string SourceRunId,
        string? ComponentDeclarationsDigest,
        string ProjectionFingerprint);

    private sealed class ProofConfigurationException : Exception;

    private sealed class ProductionReleaseManifestAdmissionFactAttribute : FactAttribute
    {
        public ProductionReleaseManifestAdmissionFactAttribute()
        {
            if (!IsEnabled())
                Skip = $"{Gate} is not enabled; the production release-admission proof was explicitly skipped.";
        }

        private static bool IsEnabled() =>
            string.Equals(Environment.GetEnvironmentVariable(Gate), "1", StringComparison.Ordinal) ||
            string.Equals(Environment.GetEnvironmentVariable(Gate), "true", StringComparison.OrdinalIgnoreCase);
    }
}
