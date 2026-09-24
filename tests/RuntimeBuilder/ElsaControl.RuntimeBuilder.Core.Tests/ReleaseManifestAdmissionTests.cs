using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.Plans;
using ElsaControl.RuntimeBuilder.Core.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Core.Tests;

public sealed class ReleaseManifestAdmissionTests
{
    private const string ProducerSigner = "https://github.com/valence-works/elsa-production-image/.github/workflows/build-and-push.yml@refs/heads/main";

    [Fact]
    public async Task Producer_v2_fixture_projects_release_identity_and_separate_subject_payload_evidence()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload);
        var verifier = new StubSignatureVerifier(ProducerVerification(artifact));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new(ProducerSigner, "paid", "combined"));

        Assert.True(admission.Accepted);
        Assert.Empty(admission.Findings);
        Assert.Equal(artifact.Digest, admission.Digest);
        Assert.Equal(PayloadDigest(payload), admission.PayloadDigest);
        Assert.NotEqual(admission.Digest, admission.PayloadDigest);
        Assert.Equal("valence-runtime", admission.Manifest!.Distribution.Id);
        Assert.Equal("3.8", admission.Manifest.Distribution.ReleaseLine);
        Assert.Equal("3.8.0-preview.5413", admission.Manifest.Distribution.ReleaseVersion);
        Assert.Equal("preview", admission.Manifest.Distribution.Channel);
        Assert.Equal("preview", admission.Manifest.Distribution.Lifecycle);
        Assert.Equal("commercial", admission.Manifest.Distribution.Edition);
        Assert.Equal("producer-2.0.0", admission.Manifest.Distribution.Generation);
        Assert.Equal("central-package-declarations-v1", admission.Manifest.ComponentDeclarations!.Format);
        Assert.Equal("sha256:1b12815e61c57e538729dc99f7fde637e9576e889d67e58a5928a0380ce7b482", admission.Manifest.ComponentDeclarations.Digest);
        Assert.Contains(admission.Manifest.ComponentDeclarations.Packages,
            package => package.Id == AzureWorkloadPlanTranslator.SqlWorkflowPackageId && package.Version == "3.8.0-preview.5413");
        Assert.Contains(admission.Manifest.ComponentDeclarations.Packages,
            package => package.Id == AzureWorkloadPlanTranslator.SqlQuartzPackageId && package.Version == "3.8.0-preview.5413");

        var serialized = JsonSerializer.Serialize(admission);
        Assert.DoesNotContain("certificateIdentity", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("verification", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, serialized, StringComparison.Ordinal);

        var projected = ReleaseManifestPlanProjector.Project(admission, CreatePlan());
        var manifestEvidence = Assert.Single(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Manifest);
        Assert.Equal(admission.PayloadDigest, manifestEvidence.PayloadDigest);
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Sbom && x.PayloadDigest == "sha256:6e71a3cb3add948ebd219bd2e14c38276488a87a0da3267f4aeb89b5b24e307e");
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Provenance && x.PayloadDigest == "sha256:9d71bdfffae9c73820cfcea1af2803bc24612cadffd109fe0ef6b7d82d66a00d");
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.VulnerabilityScan && x.PayloadDigest == "sha256:7273962ea21c67474dff90e662f8f6e44805e7ee0ed6e2eef73ead4355d95af5");
        var persisted = ResolvedElsaApplicationPlanSerialization.Deserialize(
            ResolvedElsaApplicationPlanSerialization.Serialize(projected)).Normalize();
        Assert.All(
            persisted.Evidence,
            evidence => Assert.True(
                ReleaseManifestEvidenceContract.IsSafe(evidence.Kind, evidence.Reference, evidence.Digest, evidence.Description),
                $"Persisted evidence is not safe for retrieval: {evidence.Kind} {evidence.Reference}"));
        Assert.All(
            persisted.Evidence.Where(x => x.Kind is ReleaseManifestEvidenceKinds.Sbom or ReleaseManifestEvidenceKinds.Provenance or ReleaseManifestEvidenceKinds.VulnerabilityScan),
            evidence => Assert.StartsWith("oci://", evidence.Reference, StringComparison.Ordinal));
        Assert.Equal(admission.Manifest.ComponentDeclarations.Format, persisted.Release.ComponentDeclarations!.Format);
        Assert.Equal(admission.Manifest.ComponentDeclarations.Digest, persisted.Release.ComponentDeclarations.Digest);
        Assert.Equal(
            admission.Manifest.ComponentDeclarations.Packages
                .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Select(package => (package.Id, package.Version)),
            persisted.Release.ComponentDeclarations.Packages.Select(package => (package.Id, package.Version)));
        Assert.DoesNotContain(
            admission.Manifest.Topologies.SelectMany(topology => topology.Images),
            image => image.Capabilities?.Contains(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2, StringComparer.Ordinal) == true);
    }

    [Theory]
    [InlineData("3.8")]
    [InlineData("3.10")]
    [InlineData("4.1")]
    [InlineData("5.0")]
    public async Task Managed_handoff_capability_is_admitted_and_projected_for_arbitrary_release_lines(string releaseLine)
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(producer, releaseLine);
        var artifact = ProducerArtifact(producer.ToJsonString());
        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.True(admission.Accepted, string.Join("; ", admission.Findings.Select(x => x.Code)));
        Assert.Equal(releaseLine, admission.Manifest!.Distribution.ReleaseLine);

        var selectedImage = admission.Manifest.Topologies
            .Single(topology => topology.Id == "combined")
            .Images.Single(image => image.RegistryClass == "paid");
        Assert.Contains(
            selectedImage.Capabilities!,
            capability => capability == ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2);

        var projected = ReleaseManifestPlanProjector.Project(admission, CreatePlan());
        var component = Assert.Single(projected.Topology.Components);
        Assert.Contains(
            component.Capabilities,
            capability => capability == ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2);
        Assert.Equal(selectedImage.Reference, component.Image.Reference);
        Assert.Equal(selectedImage.IndexDigest, component.Image.Digest);
        Assert.DoesNotContain("artifactReference", JsonSerializer.Serialize(admission));
        Assert.DoesNotContain("elsaVersionRange", JsonSerializer.Serialize(admission));
    }

    [Fact]
    public async Task Studio_grant_capability_is_projected_only_from_a_signed_handoff_image()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(producer, "3.8", "combined");
        var combined = producer["distributions"]!.AsArray()
            .Single(distribution => distribution!["topology"]!.GetValue<string>() == "combined")!;
        combined["capabilities"]!.AsArray().Add(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1);
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.True(admission.Accepted, string.Join("; ", admission.Findings.Select(x => x.Code)));
        var component = Assert.Single(ReleaseManifestPlanProjector.Project(admission, CreatePlan()).Topology.Components);
        Assert.Contains(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1, component.Capabilities);
    }

    [Fact]
    public async Task Studio_grant_capability_without_managed_handoff_is_rejected()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        var combined = producer["distributions"]!.AsArray()
            .Single(distribution => distribution!["topology"]!.GetValue<string>() == "combined")!;
        combined["capabilities"]!.AsArray().Add(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1);
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "integration.studioGrants.handoffRequired");
    }

    [Fact]
    public async Task Managed_handoff_claim_on_studio_only_image_is_rejected_before_admission()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(producer, "3.8", "studio");
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "studio"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "integration.managedHandoff.runtimeKindRequired");
        Assert.All(admission.Findings, finding => Assert.DoesNotContain("runtime-studio", finding.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Managed_handoff_claim_on_studio_topology_cannot_be_rescued_by_runtime_capabilities()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(producer, "3.8", "studio");
        var studio = producer["distributions"]!
            .AsArray()
            .Single(distribution => string.Equals(
                distribution!["topology"]!.GetValue<string>(),
                "studio",
                StringComparison.OrdinalIgnoreCase))!
            .AsObject();
        var capabilities = studio["capabilities"]!.AsArray();
        capabilities.Add("workflow-runtime");
        capabilities.Add("management-api");
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "studio"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "integration.managedHandoff.topologyUnsupported");
        Assert.DoesNotContain(admission.Findings, finding => finding.Code == "integration.managedHandoff.runtimeKindRequired");
    }

    [Fact]
    public async Task Managed_handoff_claim_requires_a_descriptor_and_exact_image_binding()
    {
        var missing = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(missing, "3.8");
        missing["distributions"]![0]! ["images"]!["paid"]!["integrations"] = new JsonArray();
        RefreshProducerCanonicalDigest(missing);
        var missingArtifact = ProducerArtifact(missing.ToJsonString());
        var missingAdmission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(missingArtifact)))
            .AdmitAsync(missingArtifact, new(ProducerSigner, "paid", "combined"));
        Assert.False(missingAdmission.Accepted);
        Assert.Contains(missingAdmission.Findings, finding => finding.Code == "integration.managedHandoff.required");

        var unsafeBinding = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(unsafeBinding, "3.8");
        unsafeBinding["distributions"]![0]! ["images"]!["paid"]!["integrations"]![0]!["artifactReference"] =
            "ghcr.io/runtime/runtime:latest@" + Digest('e');
        RefreshProducerCanonicalDigest(unsafeBinding);
        var unsafeArtifact = ProducerArtifact(unsafeBinding.ToJsonString());
        var unsafeAdmission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(unsafeArtifact)))
            .AdmitAsync(unsafeArtifact, new(ProducerSigner, "paid", "combined"));
        Assert.False(unsafeAdmission.Accepted);
        Assert.Contains(unsafeAdmission.Findings, finding => finding.Code == "integration.managedHandoff.artifactReference.invalid");
        Assert.Contains(unsafeAdmission.Findings, finding => finding.Code == "integration.managedHandoff.artifactReference.mismatch");
        Assert.All(unsafeAdmission.Findings, finding => Assert.DoesNotContain("ghcr.io", finding.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Managed_handoff_rejects_release_version_outside_declared_release_line()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        AddManagedHandoffCapability(producer, "3.8");
        producer["release"]!["version"] = "5.0.0-preview.1";
        producer["release"]!["compatibility"] = new JsonObject
        {
            ["engineVersion"] = "5.0.0"
        };
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "release.version.releaseLine.mismatch");
        Assert.Contains(admission.Findings, finding => finding.Code == "release.compatibility.engineVersion.releaseLine.mismatch");
        Assert.Contains(admission.Findings, finding => finding.Code == "release.compatibility.engineVersion.mismatch");
        Assert.All(admission.Findings, finding => Assert.DoesNotContain("5.0", finding.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Producer_release_version_rejects_empty_prerelease_identifiers()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["version"] = "3.8.0-preview..1";
        producer["release"]!["compatibility"]!["engineVersion"] = "3.8.0-preview..1";
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "release.version.invalid");
        Assert.Contains(admission.Findings, finding => finding.Code == "release.compatibility.engineVersion.invalid");
        Assert.All(admission.Findings, finding => Assert.DoesNotContain("preview", finding.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("01.8.0")]
    [InlineData("3.08.0")]
    [InlineData("3.8.00")]
    [InlineData("3.8.0-preview.01")]
    public async Task Producer_release_version_rejects_noncanonical_semver_identifiers(string version)
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["version"] = version;
        producer["release"]!["compatibility"]!["engineVersion"] = version;
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "release.version.invalid");
        Assert.Contains(admission.Findings, finding => finding.Code == "release.compatibility.engineVersion.invalid");
    }

    [Fact]
    public async Task Missing_release_line_does_not_emit_a_version_line_mismatch()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["releaseLine"] = "";
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, finding => finding.Code == "release.releaseLine.required");
        Assert.DoesNotContain(admission.Findings, finding => finding.Code == "release.version.releaseLine.mismatch");
    }

    [Fact]
    public async Task Producer_component_declarations_are_required_and_duplicate_packages_fail_closed()
    {
        var missing = JsonNode.Parse(ProducerFixture())!.AsObject();
        missing.Remove("componentDeclarations");
        RefreshProducerCanonicalDigest(missing);
        var missingArtifact = ProducerArtifact(missing.ToJsonString());
        var missingAdmission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(missingArtifact)))
            .AdmitAsync(missingArtifact, new(ProducerSigner, "paid", "combined"));
        Assert.False(missingAdmission.Accepted);
        Assert.Contains(missingAdmission.Findings, finding => finding.Code == "componentDeclarations.required");

        var duplicate = JsonNode.Parse(ProducerFixture())!.AsObject();
        var packages = duplicate["componentDeclarations"]!["packages"]!.AsArray();
        packages.Insert(1, packages[0]!.DeepClone());
        RefreshProducerCanonicalDigest(duplicate);
        var duplicateArtifact = ProducerArtifact(duplicate.ToJsonString());
        var duplicateAdmission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(duplicateArtifact)))
            .AdmitAsync(duplicateArtifact, new(ProducerSigner, "paid", "combined"));
        Assert.False(duplicateAdmission.Accepted);
        Assert.Contains(duplicateAdmission.Findings, finding => finding.Code == "componentDeclarations.packages.duplicate");
    }

    [Fact]
    public async Task Producer_payload_mutation_is_rejected_when_verifier_binding_remains_original()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload + "\n");
        var verifier = new StubSignatureVerifier(ProducerVerification(ProducerArtifact(payload)));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new(ProducerSigner));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.boundPayloadDigest.mismatch");
    }

    [Fact]
    public async Task Producer_unbound_subject_is_rejected_even_when_local_payload_hash_matches()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload);
        var verifier = new StubSignatureVerifier(ProducerVerification(artifact, boundPayloadDigest: Digest('9')));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new(ProducerSigner));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.boundPayloadDigest.mismatch");
    }

    [Fact]
    public async Task Producer_wrong_signer_and_oidc_issuer_are_rejected()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload);

        var wrongSigner = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact, subject: "unapproved-signer")))
            .AdmitAsync(artifact, new(ProducerSigner));
        Assert.False(wrongSigner.Accepted);
        Assert.Contains(wrongSigner.Findings, x => x.Code == "signature.subject.mismatch");

        var wrongIssuer = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact, oidcIssuer: "https://issuer.attacker.example")))
            .AdmitAsync(artifact, new(ProducerSigner));
        Assert.False(wrongIssuer.Accepted);
        Assert.Contains(wrongIssuer.Findings, x => x.Code == "signature.oidcIssuer.mismatch");
    }

    [Fact]
    public async Task Producer_evidence_subject_digest_mismatch_is_rejected()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["distributions"]![0]!["images"]!["paid"]!["attestations"]![0]!["subjectDigest"] = Digest('7');
        var payload = producer.ToJsonString();
        var artifact = ProducerArtifact(payload);

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "evidence.subject.invalid");
    }

    [Fact]
    public async Task Producer_requires_both_editions_for_each_topology_component()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        var first = producer["distributions"]![0]!;
        var second = producer["distributions"]![1]!;
        first["topology"] = "split-topology";
        second["topology"] = "split-topology";
        first["images"]!.AsObject().Remove("community");
        second["images"]!.AsObject().Remove("paid");
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "split-topology"));

        Assert.False(admission.Accepted);
        Assert.Equal(2, admission.Findings.Count(x => x.Code == "distribution.edition.missing"));
    }

    [Fact]
    public async Task Producer_non_string_platform_digest_is_rejected_without_throwing()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["distributions"]![0]!["images"]!["paid"]!["platformDigests"] = new JsonObject
        {
            ["linux/amd64"] = 42
        };
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.platformDigest.invalid");
    }

    [Fact]
    public async Task Producer_present_non_array_evidence_is_rejected_instead_of_inherited()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["distributions"]![0]!["evidence"] = new JsonObject();
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "evidence.array.required");
    }

    [Fact]
    public async Task Producer_present_non_array_endpoints_are_rejected()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["distributions"]![0]!["endpoints"] = new JsonObject();
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "endpoints.array.required");
    }

    [Fact]
    public void Producer_canonical_digest_removes_only_the_integrity_self_digest()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        producer["release"]!["source"]!["extension"] = new JsonObject
        {
            ["canonicalContentDigest"] = Digest('8')
        };
        RefreshProducerCanonicalDigest(producer);
        var first = producer["integrity"]!["canonicalContentDigest"]!.GetValue<string>();

        producer["release"]!["source"]!["extension"]!["canonicalContentDigest"] = Digest('9');
        RefreshProducerCanonicalDigest(producer);
        var second = producer["integrity"]!["canonicalContentDigest"]!.GetValue<string>();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Producer_image_signature_subject_allows_the_optional_oci_scheme()
    {
        var producer = JsonNode.Parse(ProducerFixture())!;
        var paid = producer["distributions"]![0]!["images"]!["paid"]!;
        paid["signature"]!["subject"] = "oci://" + paid["reference"]!.GetValue<string>();
        RefreshProducerCanonicalDigest(producer);
        var artifact = ProducerArtifact(producer.ToJsonString());

        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        Assert.True(admission.Accepted, string.Join("; ", admission.Findings.Select(x => x.Code)));
        Assert.DoesNotContain(admission.Findings, x => x.Code == "image.signature.subject.mismatch");
    }

    [Fact]
    public async Task Producer_fixture_flows_through_resolver_to_azure_translator()
    {
        var payload = ProducerFixture();
        var artifact = ProducerArtifact(payload);
        var admission = await new ReleaseManifestAdmissionService(
                new StubSignatureVerifier(ProducerVerification(artifact)))
            .AdmitAsync(artifact, new(ProducerSigner, "paid", "combined"));

        var request = new ElsaInstancePlanResolutionRequest(
            new(
                new("valence-runtime", "3.8", "3.8.0-preview.5413", "preview"),
                new("combined"),
                new("managed", "westeurope", "Dedicated", "standard-small", "public", "managed")),
            new(new("runtime-combined", null, null, null), [], [], [], null),
            admission,
            "plan_01JPRODUCER202",
            "https://control.example.test/api/workspaces/00000000-0000-0000-0000-000000000001/instances/00000000-0000-0000-0000-000000000002/resolved-plans/plan_01JPRODUCER202",
            GovernedSecretReferences: new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database-connection",
                ["identity:signingkey"] = "secret://vault/identity-signing-key",
                ["admin:password"] = "secret://vault/admin-password"
            });

        var resolved = await new ElsaInstancePlanResolver(
            new EmptyCatalog(),
            new CompatibleCatalog(),
            new ElsaInstancePlanResolutionOptions(DefaultEgress: "unrestricted"))
            .ResolveAsync(request);
        Assert.True(resolved.Succeeded, string.Join("; ", resolved.Findings.Select(x => x.Code + ":" + x.Message)));

        var translated = AzureWorkloadPlanTranslator.Translate(
            resolved.Plan,
            new AzureWorkloadTarget("runtime-prod", "westeurope"));

        Assert.True(translated.IsAccepted, string.Join("; ", translated.Findings.Select(x => x.Code + ":" + x.Message)));
        Assert.Equal("valenceruntimeimages.azurecr.io/runtime-combined", translated.Plan!.ImageRepository);
        Assert.Equal("e782d9426f03fa1e42da85977b7cf609119ab11dc5bfb86b0a385fc280ce2a33", translated.Plan.ImageDigest);
        Assert.Equal(admission.Digest, translated.Plan.ReleaseManifestDigest);
        Assert.Equal(admission.Reference, translated.Plan.ReleaseManifestReference);
        Assert.Equal("3.8.0-preview.5413", translated.Plan.SqlWorkflowPackageVersion);
        Assert.Equal("3.8.0-preview.5413", translated.Plan.SqlQuartzPackageVersion);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.0")]
    public async Task Historical_producer_schemas_are_rejected_by_default(string schemaVersion)
    {
        var payload = schemaVersion == "1"
            ? ManifestJson(schemaVersion: schemaVersion)
            : """{"schemaVersion":"1.0.0"}""";
        var artifact = ProducerArtifact(payload);
        var verifier = new StubSignatureVerifier(ProducerVerification(artifact));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new(ProducerSigner));

        Assert.False(admission.Accepted);
        Assert.Null(admission.Manifest);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.schema.unsupported");
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Known_good_manifest_projects_verified_release_topology_images_and_safe_evidence()
    {
        var imageDigest = Digest('b');
        var signatureDigest = Digest('c');
        var artifact = Artifact(imageDigest, releaseLine: "3.8");
        var verifier = new StubSignatureVerifier(new(
            true,
            "workflow://valence-works/elsa-production-image/release",
            artifact.Digest,
            $"oci://signatures/release@{signatureDigest}",
            signatureDigest));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new("workflow://valence-works/elsa-production-image/release", AllowLegacySchema: true));

        Assert.True(admission.Accepted);
        Assert.Empty(admission.Findings);

        var projected = ReleaseManifestPlanProjector.Project(admission, CreatePlan());

        Assert.Equal("3.8", projected.Release.ReleaseLine);
        Assert.Equal("3.8.0-preview.5413", projected.Release.Version);
        Assert.Equal(artifact.Digest, projected.Release.ReleaseManifestDigest);
        Assert.Equal("combined", projected.Topology.Id);
        var component = Assert.Single(projected.Topology.Components);
        Assert.Equal(imageDigest, component.Image.Digest);
        Assert.Equal($"runtime/runtime@{imageDigest}", component.Image.Reference);
        Assert.Equal("runtime/runtime", component.Image.Repository);
        Assert.Equal("/elsa/api", Assert.Single(component.Endpoints).Path);
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Manifest && x.Digest == artifact.Digest);
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Signature && x.Digest == signatureDigest);
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Sbom && x.Digest == Digest('d'));
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.Provenance && x.Digest == Digest('e'));
        Assert.Contains(projected.Evidence, x => x.Kind == ReleaseManifestEvidenceKinds.VulnerabilityScan && x.Digest == Digest('f'));
        Assert.DoesNotContain(projected.Evidence, x => x.Description.Contains("workflow://", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_schema_path_accepts_a_synthetic_later_release_line_without_major_version_branching()
    {
        var artifact = Artifact(Digest('b'), releaseLine: "5.0", releaseVersion: "5.0.0");
        var verifier = new StubSignatureVerifier(new(
            true,
            "subject",
            artifact.Digest,
            $"oci://signatures/release@{Digest('c')}",
            Digest('c')));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(
            artifact,
            new("subject", AllowLegacySchema: true));

        Assert.True(admission.Accepted);
        var projected = ReleaseManifestPlanProjector.Project(admission, CreatePlan());

        Assert.Equal("5.0", projected.Release.ReleaseLine);
        Assert.Equal("5.0.0", projected.Release.Version);
    }

    [Fact]
    public async Task Unknown_schema_is_rejected_before_projection()
    {
        var artifact = WithPayload(Artifact(Digest('b')), ManifestJson(schemaVersion: "2"));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.schema.unsupported");
        Assert.Throws<ReleaseManifestProjectionValidationException>(() => ReleaseManifestPlanProjector.Project(admission, CreatePlan()));
    }

    [Fact]
    public async Task Artifact_payload_digest_mismatch_is_rejected_before_verification()
    {
        var artifact = Artifact(Digest('b')) with
        {
            PayloadDigest = Digest('9')
        };
        var verifier = new StubSignatureVerifier(new(
            true,
            "subject",
            artifact.Digest,
            $"oci://signatures/release@{Digest('c')}",
            Digest('c')));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(artifact, new("subject", AllowLegacySchema: true));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.payloadDigest.mismatch");
        Assert.Equal(0, verifier.Calls);
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
    }

    [Theory]
    [InlineData("sha1:9999999999999999999999999999999999999999")]
    [InlineData("sha256:9999999999999999999999999999999999999999999999999999999999999999")]
    public async Task Artifact_payload_digest_failure_uses_stable_format_and_binding_diagnostic(string suppliedDigest)
    {
        var artifact = Artifact(Digest('b')) with { PayloadDigest = suppliedDigest };

        var admission = await Admit(artifact);

        var finding = Assert.Single(admission.Findings, x => x.Code == "manifest.payloadDigest.mismatch");
        Assert.Equal(
            "The release-manifest payload digest must be a sha256 digest matching the exact UTF-8 payload.",
            finding.Message);
        Assert.Equal("artifact.payloadDigest", finding.Scope);
    }

    [Fact]
    public async Task Scheme_less_legacy_evidence_reference_is_rejected_at_admission()
    {
        var payload = ManifestJson().Replace(
            "oci://evidence/sbom@",
            "evidence.example/sbom@",
            StringComparison.Ordinal);

        var admission = await Admit(WithPayload(Artifact(Digest('b')), payload));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.sbom.invalid");
    }

    [Fact]
    public async Task Invalid_options_do_not_invoke_signature_verifier()
    {
        var artifact = Artifact(Digest('b'));
        var verifier = new StubSignatureVerifier(new(
            true,
            "subject",
            artifact.Digest,
            $"oci://signatures/release@{Digest('c')}",
            Digest('c')));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(artifact, new(""));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.subject.expected.required");
        Assert.Equal(0, verifier.Calls);
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
    }

    [Fact]
    public async Task Artifact_reference_digest_mismatch_is_rejected()
    {
        var artifact = Artifact(Digest('b')) with
        {
            Reference = $"oci://valence-runtime/release-manifest@{Digest('9')}"
        };
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.referenceDigest.mismatch");
    }

    [Fact]
    public async Task Mutable_artifact_reference_without_digest_is_rejected()
    {
        var artifact = Artifact(Digest('b')) with
        {
            Reference = "oci://valence-runtime/release-manifest"
        };
        var verifier = new StubSignatureVerifier(new(
            true,
            "subject",
            artifact.Digest,
            $"oci://signatures/release@{Digest('c')}",
            Digest('c')));

        var admission = await new ReleaseManifestAdmissionService(verifier).AdmitAsync(artifact, new("subject"));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.referenceDigest.required");
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Hostless_artifact_reference_is_rejected()
    {
        var artifact = Artifact(Digest('b'));
        artifact = artifact with { Reference = $"oci:///release-manifest@{artifact.Digest}" };
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "manifest.reference.invalid");
        Assert.Null(admission.Reference);
        Assert.Null(admission.Digest);
    }

    [Fact]
    public async Task Wrong_signature_subject_is_rejected()
    {
        var artifact = Artifact(Digest('b'));
        var admission = await Admit(artifact, new("expected-subject", AllowLegacySchema: true), new(
            true, "different-subject", artifact.Digest, $"oci://signatures/release@{Digest('c')}", Digest('c')));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.subject.mismatch");
    }

    [Fact]
    public async Task Unsigned_manifest_is_rejected()
    {
        var artifact = Artifact(Digest('b'));
        var admission = await Admit(artifact, verification: new(
            false, "subject", artifact.Digest, $"oci://signatures/release@{Digest('c')}", Digest('c')));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.invalid");
    }

    [Fact]
    public async Task Signature_subject_digest_mismatch_is_rejected()
    {
        var artifact = Artifact(Digest('b'));
        var admission = await Admit(artifact, new("subject", AllowLegacySchema: true), new(
            true, "subject", Digest('9'), $"oci://signatures/release@{Digest('c')}", Digest('c')));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "signature.subjectDigest.mismatch");
    }

    [Fact]
    public async Task Missing_retained_supply_chain_evidence_is_rejected()
    {
        var artifact = WithPayload(Artifact(Digest('b')), ManifestJson(includeEvidence: false));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.sbom.required");
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.provenance.required");
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.signatures.required");
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.vulnerabilityScan.required");
    }

    [Fact]
    public async Task Mutable_image_reference_and_digest_mismatch_are_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson(imageReference: $"oci://runtime/runtime:latest@{Digest('c')}", imageDigest: Digest('b')));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.reference.immutableRequired");
        Assert.Contains(admission.Findings, x => x.Code == "image.referenceDigest.mismatch");
    }

    [Fact]
    public async Task Image_reference_with_credentials_is_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson(imageReference: $"oci://user:secret@runtime/runtime@{Digest('b')}", imageDigest: Digest('b')));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.reference.invalid");
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
    }

    [Fact]
    public async Task Https_scheme_image_reference_is_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson(imageReference: $"https://runtime/runtime@{Digest('b')}", imageDigest: Digest('b')));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.reference.invalid");
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
    }

    [Fact]
    public async Task Hostless_image_reference_is_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson(imageReference: $"oci:///runtime/runtime@{Digest('b')}", imageDigest: Digest('b')));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.reference.invalid");
    }

    [Theory]
    [InlineData("?token=secret")]
    [InlineData("#mutable-fragment")]
    public async Task Image_reference_with_query_or_fragment_is_rejected(string suffix)
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson(imageReference: $"oci://runtime/runtime@{Digest('b')}{suffix}", imageDigest: Digest('b')));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "image.reference.invalid");
        Assert.Null(admission.Manifest);
        Assert.Null(admission.SignatureEvidence);
    }

    [Fact]
    public async Task Unsafe_retained_evidence_reference_is_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson().Replace(
                "oci://evidence/sbom@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                "https://evidence.example/sbom?token=secret",
                StringComparison.Ordinal));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.sbom.invalid");
    }

    [Fact]
    public async Task Hostless_retained_evidence_reference_is_rejected()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson().Replace(
                $"oci://evidence/sbom@{Digest('d')}",
                $"oci:///evidence/sbom@{Digest('d')}",
                StringComparison.Ordinal));
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.sbom.invalid");
    }

    [Fact]
    public async Task Rejected_result_does_not_echo_unsafe_artifact_identifiers()
    {
        var artifact = Artifact(Digest('b'));
        artifact = artifact with { Reference = $"oci://user:secret@runtime/release-manifest@{artifact.Digest}" };
        var admission = await Admit(artifact);

        Assert.False(admission.Accepted);
        Assert.Null(admission.Reference);
        Assert.Null(admission.Digest);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(admission), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_evidence_reference_uses_a_stable_required_finding_code()
    {
        var payload = ManifestJson().Replace(
            $"\"uri\":\"oci://evidence/sbom@{Digest('d')}\",\"digest\"",
            "\"uri\":\"\",\"digest\"",
            StringComparison.Ordinal);
        var admission = await Admit(WithPayload(Artifact(Digest('b')), payload));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "supplyChain.sbom.required");
        Assert.DoesNotContain(admission.Findings, x => x.Code == "supplyChain.sbom.invalid.required");
    }

    [Theory]
    [InlineData("https://attacker.example.test/callback")]
    [InlineData("/elsa/api?token=secret")]
    [InlineData("/elsa/api/../admin")]
    public async Task Unsafe_topology_endpoint_paths_are_rejected(string path)
    {
        var payload = ManifestJson().Replace(
            "\"api\": \"/elsa/api\"",
            $"\"api\": \"{path}\"",
            StringComparison.Ordinal);
        var admission = await Admit(WithPayload(Artifact(Digest('b')), payload));

        Assert.False(admission.Accepted);
        Assert.Contains(admission.Findings, x => x.Code == "topology.endpoint.path.invalid");
    }

    [Fact]
    public async Task Unmodeled_payload_fields_do_not_cross_the_admission_boundary()
    {
        var artifact = WithPayload(
            Artifact(Digest('b')),
            ManifestJson().Replace(
                "\"schemaVersion\": \"1\",",
                "\"schemaVersion\": \"1\", \"unsafePayload\": \"secret-token\",",
                StringComparison.Ordinal));
        var admission = await Admit(artifact);

        Assert.True(admission.Accepted);
        Assert.NotNull(admission.SignatureEvidence);
        Assert.Equal($"oci://signatures/release@{Digest('c')}", admission.SignatureEvidence.Reference);
        Assert.Equal(Digest('c'), admission.SignatureEvidence.Digest);
        var serialized = JsonSerializer.Serialize(admission);
        Assert.DoesNotContain("secret-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("subject", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("identity", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Projector_rejects_unsafe_unrelated_existing_evidence()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new("existing", "https://evidence.example/a?token=secret", Digest('a'), "Existing evidence")]
        };

        Assert.True(admission.Accepted);
        Assert.Throws<ReleaseManifestProjectionValidationException>(() => ReleaseManifestPlanProjector.Project(admission, plan));
    }

    [Fact]
    public async Task Projector_rejects_existing_evidence_with_mismatched_digest_binding()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new("existing", $"https://evidence.example/a@{Digest('a')}", Digest('b'), "Retained immutable evidence.")]
        };

        Assert.True(admission.Accepted);
        Assert.Throws<ReleaseManifestProjectionValidationException>(() => ReleaseManifestPlanProjector.Project(admission, plan));
    }

    [Fact]
    public async Task Projector_rejects_existing_evidence_with_unallowlisted_description()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new("existing", $"https://evidence.example/a@{Digest('a')}", Digest('a'), "Evidence supplied by customer.")]
        };

        Assert.True(admission.Accepted);
        Assert.Throws<ReleaseManifestProjectionValidationException>(() => ReleaseManifestPlanProjector.Project(admission, plan));
    }

    [Fact]
    public async Task Projector_rejects_existing_evidence_with_unsafe_payload_identity()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new("existing", $"https://evidence.example/a@{Digest('a')}", Digest('a'), "Retained immutable evidence.", "secret-token")]
        };

        Assert.True(admission.Accepted);
        Assert.Throws<ReleaseManifestProjectionValidationException>(() => ReleaseManifestPlanProjector.Project(admission, plan));
    }

    [Fact]
    public async Task Projector_retains_unrelated_evidence_with_separately_retained_digest()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new("existing", "https://evidence.example/a", Digest('a'), "Retained immutable evidence.")]
        };

        var projected = ReleaseManifestPlanProjector.Project(admission, plan);

        Assert.Contains(projected.Evidence, evidence => evidence.Kind == "existing" && evidence.Digest == Digest('a'));
    }

    [Fact]
    public async Task Projector_drops_legacy_existing_evidence_with_missing_kind()
    {
        var admission = await Admit(Artifact(Digest('b')));
        var plan = CreatePlan() with
        {
            Evidence = [new(null!, "https://evidence.example/a", Digest('a'), "Legacy evidence")]
        };

        var projected = ReleaseManifestPlanProjector.Project(admission, plan);

        Assert.DoesNotContain(projected.Evidence, x => x.Reference == "https://evidence.example/a");
    }

    [Fact]
    public async Task Rejection_findings_never_echo_untrusted_identifiers_or_control_characters()
    {
        var payloads = new (string Payload, ReleaseManifestAdmissionOptions Options)[]
        {
            (ManifestJson(schemaVersion: "2\\r\\nschema-secret"), new("subject")),
            (ManifestJson().Replace("\"id\": \"combined\"", "\"id\": \"topology-secret\\r\\nforged\"", StringComparison.Ordinal), new("subject", TopologyId: "combined")),
            (ManifestJson().Replace("\"elsaCore\": \"3.8.0-preview.5413\"", "\"component-secret\\r\\nforged\": \"\"", StringComparison.Ordinal), new("subject")),
            (ManifestJson().Replace("\"registryClass\": \"paid\"", "\"registryClass\": \"registry-secret\\r\\nforged\"", StringComparison.Ordinal), new("subject"))
        };

        foreach (var testCase in payloads)
        {
            var admission = await Admit(WithPayload(Artifact(Digest('b')), testCase.Payload), testCase.Options);
            var serializedFindings = JsonSerializer.Serialize(admission.Findings);

            Assert.False(admission.Accepted);
            Assert.DoesNotContain("secret", serializedFindings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("forged", serializedFindings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(serializedFindings, "\r", StringComparison.Ordinal);
            Assert.DoesNotContain(serializedFindings, "\n", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Null_images_emit_only_null_findings_without_duplicate_noise()
    {
        var payload = ManifestJson().Replace("\"images\": [{", "\"images\": [null, null, {", StringComparison.Ordinal);
        var admission = await Admit(WithPayload(Artifact(Digest('b')), payload));

        Assert.False(admission.Accepted);
        Assert.Equal(2, admission.Findings.Count(x => x.Code == "image.null"));
        Assert.DoesNotContain(admission.Findings, x => x.Code == "image.duplicate");
    }

    private static async Task<ReleaseManifestAdmissionResult> Admit(
        ReleaseManifestArtifact artifact,
        ReleaseManifestAdmissionOptions? options = null,
        ReleaseManifestSignatureVerification? verification = null)
    {
        verification ??= new(true, "subject", artifact.Digest, $"oci://signatures/release@{Digest('c')}", Digest('c'));
        return await new ReleaseManifestAdmissionService(new StubSignatureVerifier(verification)).AdmitAsync(
            artifact,
            options ?? new("subject", AllowLegacySchema: true));
    }

    private static string ProducerFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "producer-release-manifest-2.0.0.json"));

    private static void AddManagedHandoffCapability(JsonNode producer, string releaseLine, string? topologyId = null)
    {
        producer["release"]!["releaseLine"] = releaseLine;
        producer["release"]!["version"] = $"{releaseLine}.0-preview.1";
        if (producer["release"]!["compatibility"] is JsonObject compatibility)
            compatibility["engineVersion"] = $"{releaseLine}.0-preview.1";
        var distributions = producer["distributions"]!.AsArray();
        foreach (var distribution in distributions)
        {
            if (distribution is not JsonObject distributionObject
                || (topologyId is not null && !string.Equals(distributionObject["topology"]?.GetValue<string>(), topologyId, StringComparison.OrdinalIgnoreCase)))
                continue;

            var distributionCapabilities = distributionObject["capabilities"]!.AsArray();
            var providesRuntimeApi = distributionCapabilities.Any(capability => string.Equals(capability?.GetValue<string>(), "workflow-runtime", StringComparison.Ordinal))
                && distributionCapabilities.Any(capability => string.Equals(capability?.GetValue<string>(), "management-api", StringComparison.Ordinal));
            if (topologyId is null && !providesRuntimeApi)
                continue;

            var capabilities = distributionCapabilities;
            if (!capabilities.Any(capability => string.Equals(capability?.GetValue<string>(), ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2, StringComparison.Ordinal)))
                capabilities.Add(ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2);

            foreach (var imageNode in distributionObject["images"]!.AsObject().Select(property => property.Value))
            {
                var image = imageNode!.AsObject();
                var reference = image["reference"]!.GetValue<string>();
                var digest = image["digest"]!.GetValue<string>();
                image["integrations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["capability"] = ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2,
                        ["elsaVersionRange"] = ManagedHandoffVersionRange(releaseLine),
                        ["artifactReference"] = reference,
                        ["artifactDigest"] = digest
                    }
                };
            }
        }

        RefreshProducerCanonicalDigest(producer);
    }

    private static string ManagedHandoffVersionRange(string releaseLine)
    {
        var parts = releaseLine.Split('.');
        var major = int.Parse(parts[0]);
        var minor = int.Parse(parts[1]);
        return $"[{major}.{minor}.0-0,{major}.{minor + 1}.0)";
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

    private static ReleaseManifestArtifact ProducerArtifact(string payload)
    {
        var subjectDigest = Digest('a');
        return new($"oci://valence-runtime/release-manifests/release-manifest@{subjectDigest}", subjectDigest, payload);
    }

    private static ReleaseManifestSignatureVerification ProducerVerification(
        ReleaseManifestArtifact artifact,
        string? subject = null,
        string? oidcIssuer = null,
        string? boundPayloadDigest = null) =>
        new(
            true,
            subject ?? ProducerSigner,
            artifact.Digest,
            $"oci://valence-runtime/signatures/release@{Digest('c')}",
            Digest('c'),
            oidcIssuer ?? ReleaseManifestSchema.DefaultOidcIssuer,
            boundPayloadDigest ?? PayloadDigest(artifact.Payload));

    private static ReleaseManifestArtifact Artifact(
        string imageDigest,
        string releaseLine = "3.8",
        string releaseVersion = "3.8.0-preview.5413")
    {
        var payload = ManifestJson(
            releaseLine: releaseLine,
            releaseVersion: releaseVersion,
            imageReference: $"oci://runtime/runtime@{imageDigest}",
            imageDigest: imageDigest);
        var digest = PayloadDigest(payload);
        return new($"oci://valence-runtime/release-manifest@{digest}", digest, payload);
    }

    private static ReleaseManifestArtifact WithPayload(ReleaseManifestArtifact artifact, string payload)
    {
        var digest = PayloadDigest(payload);
        return artifact with
        {
            Reference = $"oci://valence-runtime/release-manifest@{digest}",
            Digest = digest,
            Payload = payload
        };
    }

    private static string ManifestJson(
        string schemaVersion = "1",
        string releaseLine = "3.8",
        string releaseVersion = "3.8.0-preview.5413",
        string imageReference = "oci://runtime/runtime@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        string imageDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        bool includeEvidence = true)
    {
        var evidence = includeEvidence
            ? "\"sbom\":{\"uri\":\"oci://evidence/sbom@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"digest\":\"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"},\"provenance\":{\"uri\":\"oci://evidence/provenance@sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"digest\":\"sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\"},\"signatures\":[{\"registryClass\":\"paid\",\"identity\":\"workflow\",\"uri\":\"oci://signatures/release@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"digest\":\"sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"}],\"vulnerabilityScan\":{\"tool\":\"trivy\",\"policy\":\"fixable-high-critical\",\"report\":\"oci://evidence/scan@sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\",\"digest\":\"sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"}"
            : "";

        return $$"""
        {
          "schemaVersion": "{{schemaVersion}}",
          "distribution": {
            "id": "valence-runtime",
            "generation": "elsa-3",
            "releaseLine": "{{releaseLine}}",
            "releaseVersion": "{{releaseVersion}}",
            "channel": "preview",
            "lifecycle": "Preview",
            "source": {
              "repository": "https://github.com/valence-works/elsa-production-image",
              "commit": "1aeee8df455b21cf3bf3d2b26dfbd512d76da27b",
              "workflow": ".github/workflows/build-and-push.yml",
              "runId": "33253333014"
            }
          },
          "topologies": [{
            "id": "combined",
            "runtimeKinds": ["elsa.server", "elsa.studio"],
            "images": [{
              "registryClass": "paid",
              "reference": "{{imageReference}}",
              "indexDigest": "{{imageDigest}}",
              "platformDigests": {"linux/amd64": "{{imageDigest}}"}
            }],
            "components": {"elsaCore": "{{releaseVersion}}"},
            "endpoints": {"api": "/elsa/api"},
            "compatibility": {"packageManifestSchema": "1.0", "runtimeCapabilities": ["workflow.runtime", "workflow.studio"]},
            "supplyChain": {{{evidence}}}
          }]
        }
        """;
    }

    private static ResolvedElsaApplicationPlan CreatePlan() => new(
        ResolvedElsaApplicationPlanSchema.CurrentVersion,
        new("placeholder", "placeholder", "placeholder", "https://example.invalid", new('a', 40), "oci://placeholder", Digest('a')),
        new("placeholder", [new ResolvedElsaComponent("placeholder", ["server"], new("paid", "oci://placeholder/runtime", $"oci://placeholder/runtime@{Digest('a')}", Digest('a')), ["elsa.server"], [], [])]),
        [],
        new([]),
        new([], []),
        new("public", "restricted", false, [], []),
        "Dedicated",
        new("preview", "Preview", "internal", "automatic-within-minor", "explicit-approval", "explicit-migration"),
        [],
        []);

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";

    private static string PayloadDigest(string payload) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant()}";

    private sealed class StubSignatureVerifier(ReleaseManifestSignatureVerification result) : IReleaseManifestSignatureVerifier
    {
        public int Calls { get; private set; }

        public ValueTask<ReleaseManifestSignatureVerification> VerifyAsync(ReleaseManifestArtifact artifact, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class EmptyCatalog : IPublicCatalogQueries
    {
        public Task<IReadOnlyList<PublicPackageProjection>> ListPackagesAsync(IReadOnlyList<Guid> sourceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicPackageProjection>> ListPackagesForWorkspaceAsync(Guid workspaceId, IReadOnlyList<Guid> sourceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicPackageProjection?> GetPackageAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicPackageProjection?> GetPackageForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicPackageVersionProjection>> ListVersionsAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PublicPackageVersionProjection>>([]);
        public Task<IReadOnlyList<PublicPackageVersionProjection>> ListVersionsForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PublicPackageVersionProjection>>([]);
        public Task<PublicPackageVersionProjection?> GetVersionAsync(Guid sourceId, string packageId, string version, CancellationToken cancellationToken = default) => Task.FromResult<PublicPackageVersionProjection?>(null);
        public Task<PublicPackageVersionProjection?> GetVersionForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, string version, CancellationToken cancellationToken = default) => Task.FromResult<PublicPackageVersionProjection?>(null);
        public Task<IReadOnlyList<PublicFeatureProjection>> ListFeaturesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicFeatureProjection?> GetFeatureAsync(string featureId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompatibleCatalog : IPackageCompatibilityService
    {
        public Task<CompatibilityCheckResult> CheckAsync(CompatibilityCheckRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompatibilityCheckResult(true, []));
    }
}
