using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Core.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Core.Tests;

public sealed class GovernedReleaseCatalogIngestionServiceTests
{
    private const string ProducerSigner = "https://github.com/valence-works/elsa-production-image/.github/workflows/build-and-push.yml@refs/heads/main";

    [Fact]
    public async Task Admitted_fixture_projects_every_topology_with_server_owned_lifecycle()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload, 'a');
        var store = new RecordingStore();
        var result = await CreateService(artifact, store).AdmitAsync(
            artifact,
            CatalogOptions("supported"));

        Assert.True(result.Accepted, Findings(result));
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, result.WriteStatus);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(["combined", "server", "studio"], result.Entries.Select(x => x.Topology.Id));
        Assert.All(result.Entries, entry =>
        {
            Assert.Equal("3.8", entry.Distribution.ReleaseLine);
            Assert.Equal("3.8.0-preview.5413", entry.Distribution.ReleaseVersion);
            Assert.Equal("preview", entry.Distribution.ProducerLifecycle, ignoreCase: true);
            Assert.Equal("supported", entry.CatalogLifecycle);
            Assert.NotEmpty(entry.Topology.Components);
            Assert.NotEmpty(entry.Topology.Evidence);
            Assert.DoesNotContain(entry.Topology.Evidence, evidence =>
                evidence.Kind is ReleaseManifestEvidenceKinds.Manifest
                    or ReleaseManifestEvidenceKinds.Signature);
            Assert.Contains(entry.Topology.Evidence, evidence => evidence.Kind == ReleaseManifestEvidenceKinds.Sbom);
            Assert.Contains(entry.Topology.Evidence, evidence => evidence.Kind == ReleaseManifestEvidenceKinds.Provenance);
            Assert.Contains(entry.Topology.Evidence, evidence => evidence.Kind == ReleaseManifestEvidenceKinds.VulnerabilityScan);
        });

        var serialized = JsonSerializer.Serialize(result.Entries);
        Assert.DoesNotContain(payload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("certificateIdentity", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("cosign-keyless", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(ProducerSigner, serialized, StringComparison.Ordinal);
        Assert.All(result.Entries.SelectMany(x => x.Topology.Evidence), evidence =>
            Assert.True(ReleaseManifestEvidenceContract.IsSafe(
                evidence.Kind,
                evidence.Reference,
                evidence.Digest,
                ReleaseManifestEvidenceContract.DescriptionFor(evidence.Kind))));
        Assert.All(result.Entries, entry =>
            Assert.True(ReleaseManifestEvidenceContract.IsSafe(
                ReleaseManifestEvidenceKinds.Signature,
                entry.SignatureEvidenceReference,
                entry.SignatureEvidenceDigest,
                ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Signature))));
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData("3.8", "3.8.0-preview.5413", 'a')]
    [InlineData("3.8", "3.8.0-preview.5567-build.153", 'e')]
    [InlineData("3.9", "3.9.0", 'b')]
    [InlineData("4.1", "4.1.0", 'c')]
    [InlineData("5.0", "5.0.0", 'd')]
    public async Task Current_manifest_path_accepts_arbitrary_release_lines_and_versions(
        string releaseLine,
        string releaseVersion,
        char subjectDigit)
    {
        var payload = ProducerPayload(releaseLine, releaseVersion);
        var artifact = ProducerArtifact(payload, subjectDigit);
        var result = await CreateService(artifact, new RecordingStore()).AdmitAsync(
            artifact,
            CatalogOptions("preview"));

        Assert.True(result.Accepted, Findings(result));
        Assert.All(result.Entries, entry =>
        {
            Assert.Equal(releaseLine, entry.Distribution.ReleaseLine);
            Assert.Equal(releaseVersion, entry.Distribution.ReleaseVersion);
        });
        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task Rejected_admission_does_not_call_the_store()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload, 'a');
        var verifier = new StubSignatureVerifier(ProducerVerification(artifact, subject: "unapproved-signer"));
        var store = new RecordingStore();

        var result = await new GovernedReleaseCatalogIngestionService(
                new ReleaseManifestAdmissionService(verifier),
                store,
                TimeProvider.System)
            .AdmitAsync(artifact, CatalogOptions("preview"));

        Assert.False(result.Accepted);
        Assert.Contains(result.Findings, x => x.Code == "signature.subject.mismatch");
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Replay_with_a_different_admission_timestamp_is_unchanged()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var store = new FingerprintingStore();
        var artifact = ProducerArtifact(ProducerFixture(), 'a');
        var service = CreateService(artifact, store, clock);
        var options = CatalogOptions("supported");

        var first = await service.AdmitAsync(artifact, options);
        clock.Now = clock.Now.AddHours(1);
        var second = await service.AdmitAsync(artifact, options);

        Assert.True(first.Accepted, Findings(first));
        Assert.True(second.Accepted, Findings(second));
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, first.WriteStatus);
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Unchanged, second.WriteStatus);
        Assert.Equal(2, store.Calls);
        Assert.NotEqual(store.ReceivedEntries[0][0].AdmittedAt, store.ReceivedEntries[1][0].AdmittedAt);
    }

    [Fact]
    public async Task Catalog_lifecycle_is_required_and_is_not_copied_from_producer()
    {
        var store = new RecordingStore();
        var artifact = ProducerArtifact(ProducerFixture(), 'a');
        var result = await CreateService(artifact, store).AdmitAsync(
            artifact,
            CatalogOptions(""));

        Assert.False(result.Accepted);
        Assert.Contains(result.Findings, x => x.Code == "catalog.lifecycle.required");
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Admitted_values_exceeding_catalog_storage_limits_are_rejected_before_store()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["generation"] = new string('g', GovernedReleaseCatalogFieldLimits.Generation + 1);
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString(), 'a');
        var store = new RecordingStore();

        var result = await CreateService(artifact, store).AdmitAsync(
            artifact,
            CatalogOptions("preview"));

        Assert.False(result.Accepted);
        Assert.Equal("catalog.projection.invalid", Assert.Single(result.Findings).Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Producer_identity_lists_are_canonicalized_before_store_validation()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        var distribution = producer["distributions"]![0]!;
        var capabilities = distribution["capabilities"]!.AsArray();
        capabilities.Add(capabilities[0]!.GetValue<string>().ToUpperInvariant());
        distribution["runtimeKinds"] = new JsonArray("elsa.server", "ELSA.SERVER");
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString(), 'a');

        var result = await CreateService(artifact, new RecordingStore()).AdmitAsync(
            artifact,
            CatalogOptions("preview"));

        Assert.True(result.Accepted, Findings(result));
        Assert.All(result.Entries, entry =>
        {
            AssertDistinct(entry.Topology.RuntimeKinds);
            AssertDistinct(entry.Topology.Capabilities);
            Assert.All(entry.Topology.Components, component =>
            {
                AssertDistinct(component.Roles);
                AssertDistinct(component.Capabilities);
            });
        });
    }

    private static GovernedReleaseCatalogIngestionService CreateService(
        ReleaseManifestArtifact artifact,
        IGovernedReleaseCatalogStore store,
        TimeProvider? timeProvider = null)
    {
        return new(
            new ReleaseManifestAdmissionService(new StubSignatureVerifier(ProducerVerification(artifact))),
            store,
            timeProvider ?? TimeProvider.System);
    }

    private static GovernedReleaseCatalogAdmissionOptions CatalogOptions(string lifecycle) =>
        new(new(ProducerSigner, "paid"), lifecycle);

    private static string ProducerFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "producer-release-manifest-2.0.0.json"));

    private static string ProducerPayload(string releaseLine, string releaseVersion)
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["releaseLine"] = releaseLine;
        producer["release"]!["version"] = releaseVersion;
        producer["release"]!["compatibility"]!["engineVersion"] = releaseVersion;
        producer["release"]!["id"] = $"{releaseVersion}-build";
        RefreshProducerCanonicalDigest(producer);
        return producer.ToJsonString();
    }

    private static void RefreshProducerCanonicalDigest(JsonNode producer)
    {
        using var document = JsonDocument.Parse(producer.ToJsonString());
        var mapper = typeof(ReleaseManifestAdmissionService).Assembly.GetType(
            "ElsaControl.RuntimeBuilder.Core.ReleaseManifests.ProducerReleaseManifestMapper",
            throwOnError: true)!;
        var method = mapper.GetMethod(
            "CanonicalDigest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        producer["integrity"]!["canonicalContentDigest"] = (string)method.Invoke(
            null,
            [document.RootElement, true])!;
    }

    private static ReleaseManifestArtifact ProducerArtifact(string payload, char subjectDigit)
    {
        var subjectDigest = Digest(subjectDigit);
        return new($"oci://valence-runtime/release-manifests/release-manifest@{subjectDigest}", subjectDigest, payload);
    }

    private static ReleaseManifestSignatureVerification ProducerVerification(
        ReleaseManifestArtifact artifact,
        string? subject = null) =>
        new(
            true,
            subject ?? ProducerSigner,
            artifact.Digest,
            $"oci://valence-runtime/signatures/release@{Digest('c')}",
            Digest('c'),
            ReleaseManifestSchema.DefaultOidcIssuer,
            PayloadDigest(artifact.Payload));

    private static string Findings(GovernedReleaseCatalogAdmissionResult result) =>
        string.Join("; ", result.Findings.Select(x => $"{x.Code}:{x.Message}"));

    private static void AssertDistinct(IReadOnlyList<string> values) =>
        Assert.Equal(values.Count, values.Distinct(StringComparer.OrdinalIgnoreCase).Count());

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";

    private static string PayloadDigest(string payload) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}";

    private sealed class StubSignatureVerifier(ReleaseManifestSignatureVerification result) : IReleaseManifestSignatureVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<ReleaseManifestSignatureVerification> VerifyAsync(
            ReleaseManifestArtifact artifact,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingStore : IGovernedReleaseCatalogStore
    {
        public int Calls { get; private set; }

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GovernedReleaseCatalogWriteResult(
                GovernedReleaseCatalogWriteStatus.Stored,
                entries));
        }

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>([]);
    }

    private sealed class FingerprintingStore : IGovernedReleaseCatalogStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private string? fingerprint;
        private IReadOnlyList<GovernedReleaseCatalogEntry> stored = [];

        public int Calls { get; private set; }
        public List<IReadOnlyList<GovernedReleaseCatalogEntry>> ReceivedEntries { get; } = [];

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ReceivedEntries.Add(entries);
            var current = string.Join(
                "\n",
                entries
                    .Select(x => JsonSerializer.Serialize(x with { AdmittedAt = DateTimeOffset.UnixEpoch }, JsonOptions))
                    .Order(StringComparer.Ordinal));
            if (fingerprint is not null && string.Equals(fingerprint, current, StringComparison.Ordinal))
                return Task.FromResult(new GovernedReleaseCatalogWriteResult(
                    GovernedReleaseCatalogWriteStatus.Unchanged,
                    stored));

            fingerprint = current;
            stored = entries.ToArray();
            return Task.FromResult(new GovernedReleaseCatalogWriteResult(
                GovernedReleaseCatalogWriteStatus.Stored,
                entries));
        }

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(stored);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
