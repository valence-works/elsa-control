using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Core.ReleaseManifests;

/// <summary>
/// Maps the producer-owned 2.0.0 release contract into the provider-neutral
/// release-manifest model. The mapper deliberately copies only typed, validated
/// release facts; it never stores the source JSON, signer identity or producer
/// verification commands.
/// </summary>
internal static class ProducerReleaseManifestMapper
{
    private const string Canonicalization = "sorted-json-utf8-v1";
    private const string EvidenceDescription = "producer evidence";

    public static CommercialReleaseManifest? TryMap(
        JsonElement root,
        ReleaseManifestAdmissionOptions options,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Add(findings, "manifest.object.required", "The release manifest must be a JSON object.", "manifest");
            return null;
        }

        var release = Object(root, "release", "release", findings);
        if (release is null)
            return null;

        var distributionId = StringAny(release.Value, ["distributionId", "id"], "release.distributionId", findings);
        var generation = OptionalString(release.Value, ["generation"], "release.generation", findings) ?? "producer-2.0.0";
        var releaseLine = StringAny(release.Value, ["releaseLine"], "release.releaseLine", findings);
        var version = StringAny(release.Value, ["version", "exactVersion", "releaseVersion"], "release.version", findings);
        var channel = StringAny(release.Value, ["channel"], "release.channel", findings);
        var lifecycle = StringAny(release.Value, ["lifecycle"], "release.lifecycle", findings);
        var edition = OptionalString(release.Value, ["edition"], "release.edition", findings);
        var source = ParseSource(release.Value, findings);
        ValidateIdentity(distributionId, "release.distributionId", findings);
        ValidateIdentity(generation, "release.generation", findings);
        ValidateIdentity(releaseLine, "release.releaseLine", findings);
        ValidateIdentity(version, "release.version", findings);
        ValidateIdentity(channel, "release.channel", findings);
        ValidateIdentity(lifecycle, "release.lifecycle", findings);
        ValidateProductEdition(edition, "release.edition", findings);
        ValidateReleaseVersion(releaseLine, version, "release.version", findings);
        ValidateCompatibilityEngineVersion(release.Value, releaseLine, version, findings);

        ValidateSigning(root, options, source?.Workflow, findings);
        ValidateIntegrity(root, findings);
        var componentDeclarations = ParseComponentDeclarations(root, source?.Commit, findings);

        var rootEvidence = ParseEvidence(root, "evidence", findings);
        if (rootEvidence.Count == 0
            && root.TryGetProperty("componentEvidence", out var componentEvidence)
            && componentEvidence.ValueKind == JsonValueKind.Object)
            rootEvidence = ParseEvidence(componentEvidence, "sources", findings);
        var topologies = ParseDistributions(root, options, rootEvidence, distributionId, releaseLine, version, findings);
        if (topologies.Count == 0)
            Add(findings, "distributions.required", "At least one producer distribution is required.", "distributions");

        if (findings.Count > 0 || distributionId is null || generation is null || releaseLine is null || version is null || channel is null || lifecycle is null || source is null)
            return null;

        return new(
            ReleaseManifestSchema.CurrentVersion,
            new(
                distributionId,
                generation,
                releaseLine,
                version,
                    channel,
                    lifecycle,
                    new(source.Repository, source.Commit, source.Workflow, source.RunId),
                    edition),
            topologies,
            componentDeclarations);
    }

    private static IReadOnlyList<ReleaseManifestTopology> ParseDistributions(
        JsonElement root,
        ReleaseManifestAdmissionOptions options,
        IReadOnlyList<ProducerEvidence> rootEvidence,
        string? releaseDistributionId,
        string? releaseLine,
        string? version,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!root.TryGetProperty("distributions", out var distributions) || distributions.ValueKind != JsonValueKind.Array)
        {
            Add(findings, "distributions.required", "Producer distributions are required.", "distributions");
            return [];
        }

        var builders = new Dictionary<string, TopologyBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var distribution in distributions.EnumerateArray())
        {
            if (distribution.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "distribution.object.required", "Producer distributions must be objects.", "distributions");
                continue;
            }

            var explicitDistributionId = OptionalString(distribution, ["distributionId"], "distribution.distributionId", findings);
            var distributionAlias = OptionalString(distribution, ["id"], "distribution.id", findings);
            var id = explicitDistributionId ?? distributionAlias ?? releaseDistributionId;
            var topologyId = OptionalString(distribution, ["topology", "topologyId"], "distribution.topology", findings)
                ?? (explicitDistributionId is null ? distributionAlias : null);
            var explicitEdition = OptionalString(distribution, ["edition"], "distribution.edition", findings);
            Required(id, "distribution.id.required", "Producer distribution identity is required.", "distribution.id", findings);
            Required(topologyId, "distribution.topology.required", "Producer topology identity is required.", "distribution.topology", findings);
            if (explicitDistributionId is not null && releaseDistributionId is not null &&
                !string.Equals(explicitDistributionId, releaseDistributionId, StringComparison.OrdinalIgnoreCase))
                Add(findings, "distribution.id.mismatch", "Producer distribution identity must match the release identity.", "distribution.distributionId");
            ValidateIdentity(id, "distribution.id", findings);
            ValidateIdentity(topologyId, "distribution.topology", findings);
            ValidateRegistryClass(explicitEdition, "distribution.edition", findings);

            var editionImages = new List<(string Edition, JsonElement? Image)>();
            if (distribution.TryGetProperty("images", out var imageMap) && imageMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var image in imageMap.EnumerateObject())
                {
                    ValidateRegistryClass(image.Name, "distribution.edition", findings);
                    editionImages.Add((image.Name, image.Value));
                }
            }
            else if (explicitEdition is not null)
            {
                editionImages.Add((explicitEdition, null));
            }
            else
            {
                Add(findings, "distribution.edition.required", "Producer distribution edition is required.", "distribution.edition");
            }

            var capabilities = StringArrayAny(distribution, ["capabilities"], "distribution.capabilities", findings);
            var runtimeKinds = StringArrayAny(distribution, ["runtimeKinds"], "distribution.runtimeKinds", findings);
            var endpoints = ParseEndpoints(distribution, findings);
            var localEvidence = ParseEvidence(distribution, "evidence", findings);
            if (localEvidence.Count == 0)
                localEvidence = rootEvidence;

            foreach (var editionImage in editionImages)
            {
                var components = ParseComponents(
                    distribution,
                    id,
                    topologyId,
                    editionImage.Edition,
                    capabilities,
                    runtimeKinds,
                    endpoints,
                    localEvidence,
                    releaseLine,
                    version,
                    options.ExpectedSignatureSubject,
                    options.ExpectedOidcIssuer ?? ReleaseManifestSchema.DefaultOidcIssuer,
                    findings,
                    editionImage.Image);

                foreach (var component in components)
                {
                    if (component is null || topologyId is null)
                        continue;

                    var key = $"{topologyId}:{component.ComponentId}";
                    if (!builders.TryGetValue(key, out var builder))
                    {
                        builder = new(topologyId);
                        builders.Add(key, builder);
                    }

                    if (!builder.Add(editionImage.Edition, component, findings))
                        continue;
                }
            }
        }

        var topologies = new List<ReleaseManifestTopology>(builders.Count);
        foreach (var group in builders.Values.GroupBy(x => x.TopologyId, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var images = group.SelectMany(x => x.Images.Values).ToArray();
            foreach (var builder in group)
            {
                foreach (var requiredEdition in new[] { "paid", "community" })
                {
                    if (builder.Images.ContainsKey(requiredEdition))
                        continue;
                    Add(findings, "distribution.edition.missing", "Both governed image editions are required for each topology component.", "distributions");
                }
            }

            var selected = group.FirstOrDefault(x => x.Images.ContainsKey(options.RegistryClass));
            if (selected is null)
                Add(findings, "selection.registryClass.notFound", "The selected registry class is not present in the producer distribution.", "selection.registryClass");

            foreach (var builder in group)
            {
                if (builder.Images.Count == 2)
                {
                    var paid = builder.Images.GetValueOrDefault("paid");
                    var community = builder.Images.GetValueOrDefault("community");
                    if (paid is not null && community is not null &&
                        !string.Equals(paid.Model.IndexDigest, community.Model.IndexDigest, StringComparison.OrdinalIgnoreCase))
                        Add(findings, "distribution.edition.digest.mismatch", "Paid and community image subjects must share the same immutable digest.", "distributions");
                }
            }

            var topologyRuntimeKinds = group
                .SelectMany(x => x.RuntimeKinds)
                .Concat(first.RuntimeKinds.Count == 0 ? DeriveRuntimeKinds(first.TopologyId, first.Capabilities) : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var topologyCapabilities = group
                .SelectMany(x => x.Capabilities)
                .Concat(group.SelectMany(x => x.Images.Values).SelectMany(x => x.Model.Capabilities ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var builder in group)
                components[builder.ComponentId] = builder.ComponentVersion;

            topologies.Add(new(
                first.TopologyId,
                topologyRuntimeKinds,
                images.Select(x => x.Model).ToArray(),
                components,
                group.SelectMany(x => x.Endpoints)
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase),
                new("producer-2.0.0", topologyCapabilities),
                first.BuildSupplyChain(options.RegistryClass, findings)));
        }

        return topologies;
    }

    private static IReadOnlyList<ParsedComponent> ParseComponents(
        JsonElement distribution,
        string? distributionId,
        string? topologyId,
        string? edition,
        IReadOnlyList<string> distributionCapabilities,
        IReadOnlyList<string> distributionRuntimeKinds,
        IReadOnlyDictionary<string, string> distributionEndpoints,
        IReadOnlyList<ProducerEvidence> distributionEvidence,
        string? releaseLine,
        string? version,
        string expectedSigner,
        string expectedIssuer,
        List<ReleaseManifestAdmissionFinding> findings,
        JsonElement? imageOverride)
    {
        if (!distribution.TryGetProperty("components", out var components))
        {
            var image = imageOverride ?? ImageElement(distribution, edition, findings);
            var parsed = image is null
                ? null
                : ParseComponent(
                    image.Value,
                    distributionId ?? topologyId,
                    topologyId,
                    edition,
                    distributionCapabilities,
                    distributionRuntimeKinds,
                    distributionEndpoints,
                    distributionEvidence,
                    releaseLine,
                    version,
                    expectedSigner,
                    expectedIssuer,
                    findings);
            return parsed is null ? [] : [parsed];
        }

        var result = new List<ParsedComponent>();
        if (components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object)
                {
                    Add(findings, "component.object.required", "Producer components must be objects.", "distribution.components");
                    continue;
                }

                var id = StringAny(component, ["id", "componentId"], "component.id", findings);
                var componentVersion = OptionalString(component, ["version", "releaseVersion"], "component.version", findings) ?? version;
                ValidateIdentity(componentVersion, "component.version", findings);
                var image = ImageElement(component, edition, findings);
                if (image is null)
                    continue;
                var capabilities = StringArrayAny(component, ["capabilities"], "component.capabilities", findings);
                var runtimeKinds = StringArrayAny(component, ["runtimeKinds"], "component.runtimeKinds", findings);
                var endpoints = ParseEndpoints(component, findings);
                if (endpoints.Count == 0)
                    endpoints = distributionEndpoints;
                var evidence = ParseEvidence(component, "evidence", findings);
                if (evidence.Count == 0)
                    evidence = distributionEvidence;
                var parsed = ParseComponent(
                    image.Value,
                    id ?? distributionId ?? topologyId,
                    topologyId,
                    edition,
                    capabilities.Count > 0 ? capabilities : distributionCapabilities,
                    runtimeKinds.Count > 0 ? runtimeKinds : distributionRuntimeKinds,
                    endpoints,
                    evidence,
                    releaseLine,
                    componentVersion,
                    expectedSigner,
                    expectedIssuer,
                    findings);
                if (parsed is not null)
                    result.Add(parsed);
            }
        }
        else if (components.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in components.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    Add(findings, "component.object.required", "Producer components must be objects.", "distribution.components");
                    continue;
                }

                var componentVersion = OptionalString(property.Value, ["version", "releaseVersion"], "component.version", findings) ?? version;
                ValidateIdentity(componentVersion, "component.version", findings);
                var image = ImageElement(property.Value, edition, findings);
                if (image is null)
                    continue;
                var capabilities = StringArrayAny(property.Value, ["capabilities"], "component.capabilities", findings);
                var runtimeKinds = StringArrayAny(property.Value, ["runtimeKinds"], "component.runtimeKinds", findings);
                var endpoints = ParseEndpoints(property.Value, findings);
                if (endpoints.Count == 0)
                    endpoints = distributionEndpoints;
                var evidence = ParseEvidence(property.Value, "evidence", findings);
                if (evidence.Count == 0)
                    evidence = distributionEvidence;
                var parsed = ParseComponent(
                    image.Value,
                    property.Name,
                    topologyId,
                    edition,
                    capabilities.Count > 0 ? capabilities : distributionCapabilities,
                    runtimeKinds.Count > 0 ? runtimeKinds : distributionRuntimeKinds,
                    endpoints,
                    evidence,
                    releaseLine,
                    componentVersion,
                    expectedSigner,
                    expectedIssuer,
                    findings);
                if (parsed is not null)
                    result.Add(parsed);
            }
        }
        else
        {
            Add(findings, "component.array.required", "Producer components must be an array or object.", "distribution.components");
        }

        return result;
    }

    private static ParsedComponent? ParseComponent(
        JsonElement image,
        string? componentId,
        string? topologyId,
        string? edition,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> runtimeKinds,
        IReadOnlyDictionary<string, string> endpoints,
        IReadOnlyList<ProducerEvidence> evidence,
        string? releaseLine,
        string? version,
        string expectedSigner,
        string expectedIssuer,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        ValidateIdentity(componentId, "component.id", findings);
        var imageModel = ParseImage(image, edition, componentId, topologyId, capabilities, runtimeKinds, evidence, releaseLine, expectedSigner, expectedIssuer, findings);
        if (imageModel is null || topologyId is null || edition is null || componentId is null)
            return null;

        var kinds = runtimeKinds.Count > 0 ? runtimeKinds : DeriveRuntimeKinds(topologyId, capabilities);
        var safeCapabilities = capabilities.Count > 0
            ? capabilities
            : imageModel.Model.Capabilities ?? [];
        return new(
            componentId,
            version ?? "producer",
            kinds,
            safeCapabilities,
            endpoints,
            imageModel);
    }

    private static ParsedImage? ParseImage(
        JsonElement image,
        string? edition,
        string? componentId,
        string? topologyId,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> runtimeKinds,
        IReadOnlyList<ProducerEvidence> inheritedEvidence,
        string? releaseLine,
        string expectedSigner,
        string expectedIssuer,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        var reference = StringAny(image, ["reference", "subject"], "image.reference", findings);
        var digest = StringAny(image, ["digest", "indexDigest", "subjectDigest"], "image.digest", findings);
        RequiredDigest(digest, "image.digest", findings);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            if (!ReleaseManifestAdmissionService.IsSafeImageReference(reference)
                || !ReleaseManifestAdmissionService.IsImmutableImageReference(reference))
                Add(findings, "image.reference.invalid", "Producer image references must be safe immutable OCI locators.", "image.reference");
            var embedded = ReleaseManifestAdmissionService.ExtractDigest(reference);
            if (embedded is null || !string.Equals(embedded, digest, StringComparison.OrdinalIgnoreCase))
                Add(findings, "image.referenceDigest.mismatch", "The immutable image reference must match its declared subject digest.", "image.reference");
        }

        var platformDigests = ParsePlatformDigests(image, findings);
        var imageCapabilities = StringArrayAny(image, ["capabilities"], "image.capabilities", findings);
        var allCapabilities = capabilities.Count > 0 ? capabilities : imageCapabilities;
        var imageKinds = runtimeKinds.Count > 0 ? runtimeKinds : DeriveRuntimeKinds(componentId, allCapabilities);
        ValidateRuntimeIntegrations(image, topologyId, allCapabilities, imageKinds, releaseLine, reference, digest, findings);
        var roles = imageKinds.Select(ToRole).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var imageEvidence = ParseEvidence(image, "evidence", findings);
        if (imageEvidence.Count == 0)
            imageEvidence = ParseEvidence(image, "attestations", findings);
        if (imageEvidence.Count == 0)
            imageEvidence = inheritedEvidence
                .Where(x => string.Equals(x.Subject, reference, StringComparison.Ordinal)
                    && string.Equals(x.SubjectDigest, digest, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        ValidateImageSignature(image, reference, expectedSigner, expectedIssuer, findings);
        var vulnerability = image.TryGetProperty("vulnerability", out var vulnerabilityElement)
            ? ParseVulnerability(vulnerabilityElement, reference, digest, findings)
            : imageEvidence.FirstOrDefault(x => string.Equals(x.Kind, "vulnerability", StringComparison.OrdinalIgnoreCase));
        if (vulnerability is not null)
            imageEvidence = imageEvidence.Concat([vulnerability]).ToArray();

        if (imageModelMissing(reference, digest))
            return null;

        var model = new ReleaseManifestImage(
            edition ?? string.Empty,
            reference!,
            digest!,
            platformDigests,
            componentId,
            roles,
            allCapabilities,
            null,
            null);
        return new(model, imageEvidence);

        bool imageModelMissing(string? imageReference, string? imageDigest) =>
            string.IsNullOrWhiteSpace(imageReference) || !ReleaseManifestAdmissionService.IsDigest(imageDigest);
    }

    private static ProducerEvidence? ParseVulnerability(
        JsonElement vulnerability,
        string? imageReference,
        string? imageDigest,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (vulnerability.ValueKind != JsonValueKind.Object)
        {
            Add(findings, "evidence.vulnerability.object.required", "Vulnerability evidence must be an object.", "image.vulnerability");
            return null;
        }

        var scanDigest = StringAny(vulnerability, ["scanDigest", "subjectDigest"], "image.vulnerability.scanDigest", findings);
        RequiredDigest(scanDigest, "image.vulnerability.scanDigest", findings);
        var report = vulnerability.TryGetProperty("report", out var reportElement) && reportElement.ValueKind == JsonValueKind.Object
            ? reportElement
            : vulnerability;
        var reference = StringAny(report, ["reference"], "evidence.vulnerability.reference", findings);
        var digest = StringAny(report, ["digest"], "evidence.vulnerability.digest", findings);
        var subject = OptionalString(report, ["subject"], "evidence.vulnerability.subject", findings) ?? imageReference;
        var subjectDigest = OptionalString(report, ["subjectDigest"], "evidence.vulnerability.subjectDigest", findings) ?? scanDigest;
        var payloadDigest = OptionalString(report, ["payloadDigest"], "evidence.vulnerability.payloadDigest", findings) ?? digest;
        var fact = ParseEvidenceValues(
            "vulnerability",
            reference,
            digest,
            subject,
            subjectDigest,
            payloadDigest,
            StringAny(vulnerability, ["scanner", "tool"], "evidence.vulnerability.tool", findings),
            StringAny(vulnerability, ["policy"], "evidence.vulnerability.policy", findings),
            findings);
        if (fact is not null && !string.Equals(scanDigest, fact.SubjectDigest, StringComparison.OrdinalIgnoreCase))
            Add(findings, "evidence.vulnerability.subjectDigest.mismatch", "Vulnerability evidence must bind the declared image subject.", "image.vulnerability");
        return fact;
    }

    private static void ValidateImageSignature(JsonElement image, string? reference, string expectedSigner, string expectedIssuer, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!image.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object)
        {
            Add(findings, "image.signature.required", "Each producer image must carry signature facts.", "image.signature");
            return;
        }

        var method = StringAny(signature, ["method"], "image.signature.method", findings);
        if (!string.Equals(method, "cosign-keyless", StringComparison.Ordinal))
            Add(findings, "image.signature.method.invalid", "Producer images must use cosign keyless signatures.", "image.signature.method");
        var issuer = StringAny(signature, ["oidcIssuer"], "image.signature.oidcIssuer", findings);
        if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
            Add(findings, "image.signature.oidcIssuer.invalid", "The producer image OIDC issuer is not approved.", "image.signature.oidcIssuer");
        var identity = StringAny(signature, ["certificateIdentity", "signer"], "image.signature.identity", findings);
        ValidateIdentity(identity, "image.signature.identity", findings);
        if (!string.Equals(identity, expectedSigner, StringComparison.Ordinal))
            Add(findings, "image.signature.identity.mismatch", "The producer image signer is not approved.", "image.signature.identity");
        var signedSubject = StringAny(signature, ["subject"], "image.signature.subject", findings);
        if (!EquivalentImageReference(signedSubject, reference))
            Add(findings, "image.signature.subject.mismatch", "The producer image signature must bind the declared immutable image reference.", "image.signature.subject");
    }

    private static bool EquivalentImageReference(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
            || !ReleaseManifestAdmissionService.IsSafeImageReference(left)
            || !ReleaseManifestAdmissionService.IsSafeImageReference(right))
            return false;

        return string.Equals(
            left.StartsWith("oci://", StringComparison.OrdinalIgnoreCase) ? left["oci://".Length..] : left,
            right.StartsWith("oci://", StringComparison.OrdinalIgnoreCase) ? right["oci://".Length..] : right,
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<ProducerEvidence> ParseEvidence(JsonElement parent, string propertyName, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            return [];
        if (value.ValueKind != JsonValueKind.Array)
        {
            Add(findings, "evidence.array.required", "Producer evidence must be an array.", propertyName);
            return [];
        }

        var result = new List<ProducerEvidence>();
        foreach (var evidence in value.EnumerateArray())
        {
            if (evidence.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "evidence.object.required", "Producer evidence records must be objects.", propertyName);
                continue;
            }

            var kind = StringAny(evidence, ["kind", "type"], "evidence.kind", findings)?.ToLowerInvariant();
            if (kind is not ("sbom" or "provenance" or "vulnerability" or "signature"))
            {
                Add(findings, "evidence.kind.invalid", "Producer evidence kind is not supported.", "evidence.kind");
                continue;
            }

            var fact = ParseEvidenceValues(
                kind,
                StringAny(evidence, ["reference"], "evidence.reference", findings),
                StringAny(evidence, ["digest"], "evidence.digest", findings),
                StringAny(evidence, ["subject"], "evidence.subject", findings),
                StringAny(evidence, ["subjectDigest"], "evidence.subjectDigest", findings),
                StringAny(evidence, ["payloadDigest"], "evidence.payloadDigest", findings),
                OptionalString(evidence, ["tool", "scanner"], "evidence.tool", findings),
                OptionalString(evidence, ["policy"], "evidence.policy", findings),
                findings);
            if (fact is not null)
                result.Add(fact);
        }

        return result;
    }

    private static ProducerEvidence? ParseEvidenceValues(
        string kind,
        string? reference,
        string? digest,
        string? subject,
        string? subjectDigest,
        string? payloadDigest,
        string? tool,
        string? policy,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        var normalizedReference = NormalizeEvidenceReference(reference);
        Required(reference, "evidence.reference.required", "Producer evidence reference is required.", "evidence.reference", findings);
        RequiredDigest(digest, "evidence.digest", findings);
        Required(subject, "evidence.subject.required", "Producer evidence subject is required.", "evidence.subject", findings);
        RequiredDigest(subjectDigest, "evidence.subjectDigest", findings);
        RequiredDigest(payloadDigest, "evidence.payloadDigest", findings);
        if (!string.IsNullOrWhiteSpace(reference)
            && (normalizedReference is null
                || !ReleaseManifestAdmissionService.IsSafeEvidenceReference(normalizedReference, digest)
                || !string.Equals(ReleaseManifestAdmissionService.ExtractDigest(normalizedReference), digest, StringComparison.OrdinalIgnoreCase)))
            Add(findings, "evidence.reference.invalid", "Producer evidence references must be immutable safe locators bound to their digest.", "evidence.reference");
        if (!string.IsNullOrWhiteSpace(subject)
            && (!ReleaseManifestAdmissionService.IsSafeImageReference(subject)
                || !ReleaseManifestServiceDigestMatches(subject, subjectDigest)))
            Add(findings, "evidence.subject.invalid", "Producer evidence subjects must be safe immutable image references.", "evidence.subject");
        if (normalizedReference is null || digest is null || subject is null || subjectDigest is null || payloadDigest is null)
            return null;
        return new(kind, normalizedReference, digest, subject, subjectDigest, payloadDigest, tool ?? EvidenceDescription, policy ?? EvidenceDescription);
    }

    private static string? NormalizeEvidenceReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains("://", StringComparison.Ordinal))
            return reference;

        // Producer manifests commonly omit the OCI scheme. Prefix only values that
        // already pass the strict bare-image validator; arbitrary values must still
        // be rejected at this trust boundary rather than made valid by normalization.
        return ReleaseManifestAdmissionService.IsSafeImageReference(reference)
            ? $"oci://{reference}"
            : null;
    }

    private static IReadOnlyDictionary<string, string> ParseEndpoints(JsonElement parent, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!parent.TryGetProperty("endpoints", out var endpoints))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (endpoints.ValueKind != JsonValueKind.Array)
        {
            Add(findings, "endpoints.array.required", "Producer endpoints must be an array.", "endpoints");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "endpoint.object.required", "Producer endpoints must be objects.", "endpoints");
                continue;
            }

            var name = StringAny(endpoint, ["name", "id"], "endpoint.name", findings);
            var path = StringAny(endpoint, ["path"], "endpoint.path", findings);
            ValidateIdentity(name, "endpoint.name", findings);
            if (!string.IsNullOrWhiteSpace(path) && !EndpointPathPolicy.IsSafe(path))
                Add(findings, "endpoint.path.invalid", "Producer endpoint paths must be safe absolute paths.", "endpoint.path");
            if (name is not null && path is not null && !result.TryAdd(name, path))
                Add(findings, "endpoint.duplicate", "Producer endpoint names must be unique within a distribution.", "endpoints");
        }

        return result;
    }

    private static JsonElement? ImageElement(JsonElement parent, string? edition, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (parent.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
            return image;
        if (parent.TryGetProperty("artifact", out var artifact) && artifact.ValueKind == JsonValueKind.Object)
            return artifact;
        if (parent.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object && edition is not null && images.TryGetProperty(edition, out var editionImage) && editionImage.ValueKind == JsonValueKind.Object)
            return editionImage;
        if (parent.TryGetProperty("reference", out _) || parent.TryGetProperty("subject", out _))
            return parent;

        Add(findings, "image.required", "A producer distribution must contain an image.", "distribution.image");
        return null;
    }

    private static IReadOnlyDictionary<string, string>? ParsePlatformDigests(JsonElement image, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (image.TryGetProperty("platformDigests", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in map.EnumerateObject())
            {
                var digest = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : null;
                RequiredDigest(digest, "image.platformDigest", findings);
                if (digest is not null && !result.TryAdd(item.Name, digest))
                    Add(findings, "image.platform.duplicate", "Producer platform identities must be unique.", "image.platformDigests");
            }

            return result;
        }

        if (!image.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array)
            return null;
        var platformDigests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var platform in platforms.EnumerateArray())
        {
            if (platform.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "image.platform.object.required", "Producer platform identities must be objects.", "image.platforms");
                continue;
            }

            var name = StringAny(platform, ["platform", "name"], "image.platform", findings);
            var digest = StringAny(platform, ["digest"], "image.platformDigest", findings);
            RequiredDigest(digest, "image.platformDigest", findings);
            if (name is not null && digest is not null && !platformDigests.TryAdd(name, digest))
                Add(findings, "image.platform.duplicate", "Producer platform identities must be unique.", "image.platforms");
        }

        return platformDigests;
    }

    private static ProducerSource? ParseSource(JsonElement release, List<ReleaseManifestAdmissionFinding> findings)
    {
        var source = Object(release, "source", "release.source", findings);
        if (source is null)
            return null;
        var repository = StringAny(source.Value, ["repository"], "release.source.repository", findings);
        var commit = StringAny(source.Value, ["commit"], "release.source.commit", findings);
        JsonElement? workflow = null;
        if (source.Value.TryGetProperty("workflow", out var workflowValue))
        {
            if (workflowValue.ValueKind == JsonValueKind.Object)
                workflow = workflowValue;
            else if (workflowValue.ValueKind != JsonValueKind.String)
                Add(findings, "release.source.workflow.invalid", "Release source workflow must be an object or safe HTTPS locator.", "release.source.workflow");
        }
        var workflowRef = workflow is not null
            ? StringAny(workflow.Value, ["ref", "reference", "url"], "release.source.workflow.ref", findings)
            : StringAny(source.Value, ["workflowRef", "workflow"], "release.source.workflow.ref", findings);
        var runId = workflow is not null
            ? StringAny(workflow.Value, ["runId", "id"], "release.source.workflow.runId", findings)
            : StringAny(source.Value, ["runId"], "release.source.workflow.runId", findings);

        Required(repository, "source.repository.required", "Release source repository is required.", "release.source.repository", findings);
        Required(commit, "source.commit.required", "Release source commit is required.", "release.source.commit", findings);
        Required(workflowRef, "source.workflow.required", "Release source workflow is required.", "release.source.workflow", findings);
        Required(runId, "source.runId.required", "Release source workflow run identity is required.", "release.source.runId", findings);
        if (!string.IsNullOrWhiteSpace(repository) && (!IsSafeHttps(repository) || repository.Contains('@', StringComparison.Ordinal)))
            Add(findings, "source.repository.invalid", "Release source repository must be a safe HTTPS locator.", "release.source.repository");
        if (!string.IsNullOrWhiteSpace(commit) && (commit.Length != 40 || commit.Any(x => !Uri.IsHexDigit(x))))
            Add(findings, "source.commit.invalid", "Release source commit must be a full hexadecimal identity.", "release.source.commit");
        if (!string.IsNullOrWhiteSpace(workflowRef) && !IsSafeHttps(workflowRef))
            Add(findings, "source.workflow.invalid", "Release source workflow must be a safe HTTPS locator.", "release.source.workflow");
        if (!string.IsNullOrWhiteSpace(runId) && (runId.Any(x => !char.IsAsciiDigit(x)) || runId.Length > 32))
            Add(findings, "source.runId.invalid", "Release source workflow identity must be decimal.", "release.source.runId");

        return repository is null || commit is null || workflowRef is null || runId is null
            ? null
            : new(repository, commit, workflowRef, runId);
    }

    private static void ValidateSigning(JsonElement root, ReleaseManifestAdmissionOptions options, string? workflowRef, List<ReleaseManifestAdmissionFinding> findings)
    {
        var signing = Object(root, "signing", "signing", findings);
        if (signing is null)
            return;
        var method = StringAny(signing.Value, ["method"], "signing.method", findings);
        if (!string.Equals(method, "cosign-keyless", StringComparison.Ordinal))
            Add(findings, "signing.method.invalid", "Release manifests must use cosign keyless signatures.", "signing.method");
        var issuer = StringAny(signing.Value, ["oidcIssuer"], "signing.oidcIssuer", findings);
        var expectedIssuer = options.ExpectedOidcIssuer ?? ReleaseManifestSchema.DefaultOidcIssuer;
        if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
            Add(findings, "signing.oidcIssuer.mismatch", "The release-manifest OIDC issuer is not approved.", "signing.oidcIssuer");
        var identity = StringAny(signing.Value, ["certificateIdentity", "signer"], "signing.identity", findings);
        if (!string.Equals(identity, options.ExpectedSignatureSubject, StringComparison.Ordinal))
            Add(findings, "signing.identity.mismatch", "The release-manifest signer is not approved.", "signing.identity");
        if (workflowRef is not null && !string.Equals(identity, workflowRef, StringComparison.Ordinal))
            Add(findings, "signing.workflow.mismatch", "The release-manifest signer must match the recorded release workflow.", "signing.identity");
    }

    private static void ValidateIntegrity(JsonElement root, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!root.TryGetProperty("integrity", out var integrity) || integrity.ValueKind != JsonValueKind.Object)
            return;
        var canonicalization = StringAny(integrity, ["canonicalization"], "integrity.canonicalization", findings);
        if (!string.Equals(canonicalization, Canonicalization, StringComparison.Ordinal))
            Add(findings, "integrity.canonicalization.invalid", "The producer canonicalization is not supported.", "integrity");
        var declared = StringAny(integrity, ["canonicalContentDigest"], "integrity.canonicalContentDigest", findings);
        RequiredDigest(declared, "integrity.canonicalContentDigest", findings);
        if (ReleaseManifestAdmissionService.IsDigest(declared))
        {
            var actual = CanonicalDigest(root, removeSelfDigest: true);
            if (!string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase))
                Add(findings, "integrity.canonicalContentDigest.mismatch", "The producer canonical content digest does not match the signed manifest.", "integrity");
        }
    }

    private static ReleaseManifestComponentDeclarations? ParseComponentDeclarations(
        JsonElement root,
        string? commit,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!root.TryGetProperty("componentDeclarations", out var declarations) || declarations.ValueKind != JsonValueKind.Object)
        {
            Add(findings, "componentDeclarations.required", "Producer component declarations are required.", "componentDeclarations");
            return null;
        }
        var format = StringAny(declarations, ["format"], "componentDeclarations.format", findings);
        if (!string.Equals(format, "central-package-declarations-v1", StringComparison.Ordinal))
            Add(findings, "componentDeclarations.format.invalid", "The producer component declaration format is not supported.", "componentDeclarations");
        var digest = StringAny(declarations, ["digest"], "componentDeclarations.digest", findings);
        RequiredDigest(digest, "componentDeclarations.digest", findings);
        var source = StringAny(declarations, ["source"], "componentDeclarations.source", findings);
        var sourceMarker = source?.LastIndexOf('@') ?? -1;
        var sourceCommit = sourceMarker > 0 ? source![(sourceMarker + 1)..] : null;
        if (commit is not null && !string.Equals(sourceCommit, commit, StringComparison.Ordinal))
            Add(findings, "componentDeclarations.source.mismatch", "Component declarations must be sourced from the release commit.", "componentDeclarations.source");
        if (!declarations.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
        {
            Add(findings, "componentDeclarations.packages.required", "Component declarations must contain a package array.", "componentDeclarations.packages");
            return null;
        }

        var parsed = new List<ReleaseManifestPackageDeclaration>();
        var ids = new List<string>();
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "componentDeclarations.package.invalid", "Component declarations must contain package objects.", "componentDeclarations.packages");
                continue;
            }

            var id = StringAny(package, ["id", "packageId"], "componentDeclarations.package.id", findings);
            var version = StringAny(package, ["version"], "componentDeclarations.package.version", findings);
            ValidateIdentity(id, "componentDeclarations.package.id", findings);
            ValidateIdentity(version, "componentDeclarations.package.version", findings);
            if (id is null || version is null)
                continue;

            ids.Add(id);
            parsed.Add(new(id, version));
        }

        if (parsed.Count == 0)
            Add(findings, "componentDeclarations.packages.required", "Component declarations must contain at least one package.", "componentDeclarations.packages");
        if (!ids.SequenceEqual(ids.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            Add(findings, "componentDeclarations.packages.unsorted", "Component declarations must be sorted by package identity.", "componentDeclarations.packages");
        if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Count)
            Add(findings, "componentDeclarations.packages.duplicate", "Component declarations must not contain duplicate package identities.", "componentDeclarations.packages");

        return format is null || digest is null || parsed.Count == 0
            ? null
            : new(format, digest, parsed);
    }

    private static string CanonicalDigest(JsonElement root, bool removeSelfDigest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteCanonical(root, writer, removeSelfDigest, rootObject: true);
            writer.Flush();
        }

        return $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray())).ToLowerInvariant()}";
    }

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer,
        bool removeSelfDigest,
        bool rootObject = false,
        bool integrityObject = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (removeSelfDigest && rootObject && property.NameEquals("integrity"))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(property.Value, writer, removeSelfDigest: true, integrityObject: true);
                        continue;
                    }

                    if (removeSelfDigest && integrityObject && property.NameEquals("canonicalContentDigest"))
                        continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, removeSelfDigest, rootObject: false, integrityObject: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer, removeSelfDigest, rootObject: false, integrityObject: false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static IReadOnlyList<string> DeriveRuntimeKinds(string? topologyId, IReadOnlyList<string> capabilities)
    {
        var result = new List<string>();
        if (capabilities.Any(x => x.Equals("workflow-runtime", StringComparison.OrdinalIgnoreCase) || x.Equals("management-api", StringComparison.OrdinalIgnoreCase)))
            result.Add("elsa.server");
        if (capabilities.Any(x => x.Equals("workflow-designer", StringComparison.OrdinalIgnoreCase) || x.Equals("browser-studio", StringComparison.OrdinalIgnoreCase)))
            result.Add("elsa.studio");
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(topologyId))
            result.Add($"elsa.{topologyId}");
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateRuntimeIntegrations(
        JsonElement image,
        string? topologyId,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> runtimeKinds,
        string? releaseLine,
        string? imageReference,
        string? imageDigest,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        var hasManagedHandoff = capabilities.Contains(
            ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2,
            StringComparer.Ordinal);
        var hasStudioGrants = capabilities.Contains(
            ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaStudioGrantsV1,
            StringComparer.Ordinal);
        var hasServerRuntime = runtimeKinds.Contains("elsa.server", StringComparer.OrdinalIgnoreCase);
        var hasIntegrations = image.TryGetProperty("integrations", out var integrations);

        if (hasStudioGrants && !hasManagedHandoff)
            Add(findings, "integration.studioGrants.handoffRequired", "Studio grants require the managed handoff capability.", "image.capabilities");
        if (hasStudioGrants && !hasServerRuntime)
            Add(findings, "integration.studioGrants.runtimeKindRequired", "Studio grants require an Elsa server runtime kind.", "image.capabilities");

        if (!hasIntegrations)
        {
            if (hasManagedHandoff)
                Add(findings, "integration.managedHandoff.required", "The managed handoff integration descriptor is required for this capability.", "image.integrations");
            return;
        }

        if (integrations.ValueKind != JsonValueKind.Array)
        {
            Add(findings, "integration.array.invalid", "Runtime integration descriptors must be an array.", "image.integrations");
            return;
        }

        var descriptors = integrations.EnumerateArray().ToArray();
        var managedDescriptors = new List<JsonElement>();
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ValueKind != JsonValueKind.Object)
            {
                Add(findings, "integration.object.invalid", "Runtime integration descriptors must be objects.", "image.integrations");
                continue;
            }

            if (descriptor.EnumerateObject().Any(property => property.Name is not ("capability" or "elsaVersionRange" or "artifactReference" or "artifactDigest")))
                Add(findings, "integration.property.unsupported", "Runtime integration descriptors contain an unsupported property.", "image.integrations");

            if (!descriptor.TryGetProperty("capability", out var capability)
                || capability.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(capability.GetString()))
            {
                Add(findings, "integration.capability.required", "A runtime integration capability is required.", "image.integrations");
                continue;
            }

            if (!string.Equals(capability.GetString(), ReleaseManifestRuntimeIntegrationCapabilities.ManagedElsaHandoffV2, StringComparison.Ordinal))
            {
                Add(findings, "integration.capability.unsupported", "The runtime integration capability is not supported.", "image.integrations");
                continue;
            }

            managedDescriptors.Add(descriptor);
        }

        if (!hasManagedHandoff && managedDescriptors.Count > 0)
            Add(findings, "integration.managedHandoff.capabilityRequired", "The managed handoff integration requires its declared capability.", "image.integrations");
        if (hasManagedHandoff && managedDescriptors.Count == 0)
            Add(findings, "integration.managedHandoff.required", "The managed handoff integration descriptor is required for this capability.", "image.integrations");
        if (managedDescriptors.Count > 1)
            Add(findings, "integration.managedHandoff.duplicate", "Only one managed handoff integration descriptor is permitted.", "image.integrations");
        if (hasManagedHandoff && !hasServerRuntime)
            Add(findings, "integration.managedHandoff.runtimeKindRequired", "The managed handoff capability requires an Elsa server runtime kind.", "image.integrations");
        if (hasManagedHandoff && string.Equals(topologyId, "studio", StringComparison.OrdinalIgnoreCase))
            Add(findings, "integration.managedHandoff.topologyUnsupported", "The managed handoff capability is not supported by a studio topology.", "image.integrations");

        foreach (var descriptor in managedDescriptors.Take(1))
            ValidateManagedHandoffDescriptor(descriptor, releaseLine, imageReference, imageDigest, findings);
    }

    private static void ValidateManagedHandoffDescriptor(
        JsonElement descriptor,
        string? releaseLine,
        string? imageReference,
        string? imageDigest,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        var versionRange = RequiredDescriptorString(descriptor, "elsaVersionRange", "integration.managedHandoff.versionRange.required", findings);
        if (!TryGetCanonicalManagedHandoffRange(releaseLine, out var expectedRange))
            Add(findings, "integration.managedHandoff.versionRange.invalid", "The release line cannot produce a canonical managed handoff version range.", "image.integrations");
        else if (versionRange is not null && !string.Equals(versionRange, expectedRange, StringComparison.Ordinal))
            Add(findings, "integration.managedHandoff.versionRange.mismatch", "The managed handoff version range must match the release line.", "image.integrations");

        var artifactReference = RequiredDescriptorString(descriptor, "artifactReference", "integration.managedHandoff.artifactReference.required", findings);
        if (artifactReference is not null)
        {
            if (!ReleaseManifestAdmissionService.IsSafeImageReference(artifactReference)
                || !ReleaseManifestAdmissionService.IsImmutableImageReference(artifactReference))
                Add(findings, "integration.managedHandoff.artifactReference.invalid", "The managed handoff artifact reference must be a safe immutable image locator.", "image.integrations");

            if (imageReference is null || !string.Equals(artifactReference, imageReference, StringComparison.Ordinal))
                Add(findings, "integration.managedHandoff.artifactReference.mismatch", "The managed handoff artifact reference must identify the selected image.", "image.integrations");
        }

        var artifactDigest = RequiredDescriptorString(descriptor, "artifactDigest", "integration.managedHandoff.artifactDigest.required", findings);
        if (artifactDigest is not null && !ReleaseManifestAdmissionService.IsDigest(artifactDigest))
            Add(findings, "integration.managedHandoff.artifactDigest.invalid", "The managed handoff artifact identity must use a sha256 digest.", "image.integrations");
        if (artifactDigest is not null && (imageDigest is null || !string.Equals(artifactDigest, imageDigest, StringComparison.OrdinalIgnoreCase)))
            Add(findings, "integration.managedHandoff.artifactDigest.mismatch", "The managed handoff artifact identity must match the selected image digest.", "image.integrations");
        if (artifactReference is not null && artifactDigest is not null
            && !string.Equals(ReleaseManifestAdmissionService.ExtractDigest(artifactReference), artifactDigest, StringComparison.OrdinalIgnoreCase))
            Add(findings, "integration.managedHandoff.artifactReferenceDigest.mismatch", "The managed handoff artifact reference and digest must identify the same image.", "image.integrations");
    }

    private static string? RequiredDescriptorString(
        JsonElement descriptor,
        string propertyName,
        string code,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!descriptor.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            Add(findings, code, "A managed handoff descriptor value is required.", "image.integrations");
            return null;
        }

        return value.GetString();
    }

    private static bool TryGetCanonicalManagedHandoffRange(string? releaseLine, out string range)
    {
        range = string.Empty;
        if (!TryParseReleaseLine(releaseLine, out var major, out var minor))
            return false;

        range = FormattableString.Invariant($"[{major}.{minor}.0-0,{major}.{minor + 1}.0)");
        return true;
    }

    private static void ValidateReleaseVersion(
        string? releaseLine,
        string? releaseVersion,
        string scope,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(releaseVersion))
            return;

        var match = Regex.Match(
            releaseVersion,
            "\\A(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success)
        {
            var code = scope == "release.compatibility.engineVersion"
                ? "release.compatibility.engineVersion.invalid"
                : "release.version.invalid";
            Add(findings, code, "The release version must use a supported version format.", scope);
            return;
        }

        var expectedLine = $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        if (!string.IsNullOrWhiteSpace(releaseLine)
            && !string.Equals(releaseLine, expectedLine, StringComparison.Ordinal))
        {
            var code = scope == "release.compatibility.engineVersion"
                ? "release.compatibility.engineVersion.releaseLine.mismatch"
                : "release.version.releaseLine.mismatch";
            Add(findings, code, "The release version must belong to the declared release line.", scope);
        }
    }

    private static void ValidateCompatibilityEngineVersion(
        JsonElement release,
        string? releaseLine,
        string? releaseVersion,
        List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!release.TryGetProperty("compatibility", out var compatibility))
            return;
        if (compatibility.ValueKind != JsonValueKind.Object)
        {
            Add(findings, "release.compatibility.invalid", "Release compatibility metadata must be an object.", "release.compatibility");
            return;
        }
        if (!compatibility.TryGetProperty("engineVersion", out var engineVersion))
            return;
        if (engineVersion.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(engineVersion.GetString()))
        {
            Add(findings, "release.compatibility.engineVersion.invalid", "The compatibility engine version must be a non-empty string.", "release.compatibility.engineVersion");
            return;
        }

        var engineVersionValue = engineVersion.GetString();
        ValidateReleaseVersion(releaseLine, engineVersionValue, "release.compatibility.engineVersion", findings);
        if (releaseVersion is not null && !string.Equals(releaseVersion, engineVersionValue, StringComparison.Ordinal))
            Add(findings, "release.compatibility.engineVersion.mismatch", "The compatibility engine version must match the release version.", "release.compatibility.engineVersion");
    }

    private static bool TryParseReleaseLine(string? releaseLine, out BigInteger major, out BigInteger minor)
    {
        major = default;
        minor = default;
        if (string.IsNullOrWhiteSpace(releaseLine))
            return false;

        var parts = releaseLine.Split('.', StringSplitOptions.None);
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9')))
            return false;

        return BigInteger.TryParse(parts[0], out major) && BigInteger.TryParse(parts[1], out minor);
    }

    private static string ToRole(string runtimeKind) => runtimeKind.StartsWith("elsa.", StringComparison.OrdinalIgnoreCase)
        ? runtimeKind["elsa.".Length..]
        : runtimeKind;

    private static void ValidateProductEdition(string? edition, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (edition is null)
            return;
        if (!edition.Equals("commercial", StringComparison.OrdinalIgnoreCase))
            Add(findings, "release.edition.invalid", "The producer release edition is not governed.", scope);
    }

    private static void ValidateRegistryClass(string? registryClass, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (registryClass is null)
            return;
        if (!registryClass.Equals("paid", StringComparison.OrdinalIgnoreCase) && !registryClass.Equals("community", StringComparison.OrdinalIgnoreCase))
            Add(findings, "distribution.registryClass.invalid", "The producer registry class is not governed.", scope);
    }

    private static void ValidateIdentity(string? value, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (value.Length > 256 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            Add(findings, "identity.invalid", "Producer identity values must be bounded single-line values.", scope);
    }

    private static void Required(string? value, string code, string message, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(findings, code, message, scope);
    }

    private static void RequiredDigest(string? value, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!ReleaseManifestAdmissionService.IsDigest(value))
            Add(findings, $"{scope}.invalid", "A strict sha256 digest is required.", scope);
    }

    private static JsonElement? Object(JsonElement parent, string propertyName, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            Add(findings, $"{scope}.required", "A producer object is required.", scope);
            return null;
        }

        return value;
    }

    private static string? StringAny(JsonElement parent, IReadOnlyList<string> names, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind != JsonValueKind.String)
            {
                Add(findings, $"{scope}.invalid", "A producer string value is required.", scope);
                return null;
            }

            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                Add(findings, $"{scope}.required", "A producer string value is required.", scope);
            return text;
        }

        Add(findings, $"{scope}.required", "A producer string value is required.", scope);
        return null;
    }

    private static string? OptionalString(JsonElement parent, IReadOnlyList<string> names, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind != JsonValueKind.String)
            {
                Add(findings, $"{scope}.invalid", "A producer string value is required.", scope);
                return null;
            }

            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                Add(findings, $"{scope}.invalid", "A producer string value is required.", scope);
            return text;
        }

        return null;
    }

    private static IReadOnlyList<string> StringArrayAny(JsonElement parent, IReadOnlyList<string> names, string scope, List<ReleaseManifestAdmissionFinding> findings)
    {
        foreach (var name in names)
        {
            if (!parent.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind != JsonValueKind.Array)
            {
                Add(findings, $"{scope}.invalid", "A producer string array is required.", scope);
                return [];
            }

            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    Add(findings, $"{scope}.item.invalid", "Producer array values must be non-empty strings.", scope);
                else
                    result.Add(item.GetString()!);
            }

            return result;
        }

        return [];
    }

    private static bool IsSafeHttps(string value) =>
        ReleaseManifestAdmissionService.IsSafeRetainedReference(value)
        && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool ReleaseManifestServiceDigestMatches(string reference, string? digest) =>
        ReleaseManifestAdmissionService.IsDigest(digest)
        && string.Equals(ReleaseManifestAdmissionService.ExtractDigest(reference), digest, StringComparison.OrdinalIgnoreCase);

    private static void Add(List<ReleaseManifestAdmissionFinding> findings, string code, string message, string scope) =>
        findings.Add(new(code, message, scope));

    private sealed record ProducerSource(string Repository, string Commit, string Workflow, string RunId);

    private sealed record ProducerEvidence(
        string Kind,
        string Reference,
        string Digest,
        string Subject,
        string SubjectDigest,
        string PayloadDigest,
        string Tool,
        string Policy);

    private sealed record ParsedImage(ReleaseManifestImage Model, IReadOnlyList<ProducerEvidence> Evidence);

    private sealed record ParsedComponent(
        string ComponentId,
        string Version,
        IReadOnlyList<string> RuntimeKinds,
        IReadOnlyList<string> Capabilities,
        IReadOnlyDictionary<string, string> Endpoints,
        ParsedImage Image);

    private sealed class TopologyBuilder(string topologyId)
    {
        public string TopologyId { get; } = topologyId;
        public string ComponentId { get; private set; } = string.Empty;
        public string ComponentVersion { get; private set; } = string.Empty;
        public IReadOnlyList<string> RuntimeKinds { get; private set; } = [];
        public IReadOnlyList<string> Capabilities { get; private set; } = [];
        public IReadOnlyDictionary<string, string> Endpoints { get; private set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ParsedImage> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Add(string edition, ParsedComponent component, List<ReleaseManifestAdmissionFinding> findings)
        {
            if (Images.Count == 0)
            {
                ComponentId = component.ComponentId;
                ComponentVersion = component.Version;
                RuntimeKinds = component.RuntimeKinds;
                Capabilities = component.Capabilities;
                Endpoints = component.Endpoints;
            }
            else if (!string.Equals(ComponentId, component.ComponentId, StringComparison.OrdinalIgnoreCase))
            {
                ProducerReleaseManifestMapper.Add(findings, "component.identity.mismatch", "Producer editions must describe the same component identity.", "distribution.components");
                return false;
            }
            else if (!string.Equals(ComponentVersion, component.Version, StringComparison.OrdinalIgnoreCase))
            {
                ProducerReleaseManifestMapper.Add(findings, "component.version.mismatch", "Producer editions must describe the same component version.", "distribution.components");
                return false;
            }

            if (!Images.TryAdd(edition, component.Image))
            {
                ProducerReleaseManifestMapper.Add(findings, "distribution.edition.duplicate", "A producer distribution contains duplicate edition identities.", "distribution.edition");
                return false;
            }

            return true;
        }

        public ReleaseManifestSupplyChain BuildSupplyChain(string registryClass, List<ReleaseManifestAdmissionFinding> findings)
        {
            if (!Images.TryGetValue(registryClass, out var image))
            {
                ProducerReleaseManifestMapper.Add(findings, "selection.registryClass.notFound", "The selected registry class has no image evidence.", "selection.registryClass");
                return new(null, null, [], null);
            }

            var sbom = image.Evidence.FirstOrDefault(x => string.Equals(x.Kind, "sbom", StringComparison.OrdinalIgnoreCase));
            var provenance = image.Evidence.FirstOrDefault(x => string.Equals(x.Kind, "provenance", StringComparison.OrdinalIgnoreCase));
            var vulnerability = image.Evidence.FirstOrDefault(x => string.Equals(x.Kind, "vulnerability", StringComparison.OrdinalIgnoreCase));
            if (sbom is null)
                ProducerReleaseManifestMapper.Add(findings, "supplyChain.sbom.required", "Producer SBOM evidence is required.", "supplyChain.sbom");
            if (provenance is null)
                ProducerReleaseManifestMapper.Add(findings, "supplyChain.provenance.required", "Producer provenance evidence is required.", "supplyChain.provenance");
            if (vulnerability is null)
                ProducerReleaseManifestMapper.Add(findings, "supplyChain.vulnerabilityScan.required", "Producer vulnerability evidence is required.", "supplyChain.vulnerabilityScan");

            var signatures = image.Evidence
                .Where(x => string.Equals(x.Kind, "signature", StringComparison.OrdinalIgnoreCase))
                .Select(x => new ReleaseManifestSignatureEvidence(registryClass, "verified-producer-signature", x.Reference, x.Digest))
                .ToArray();
            return new(
                sbom is null ? null : new(sbom.Reference, sbom.Digest, sbom.PayloadDigest),
                provenance is null ? null : new(provenance.Reference, provenance.Digest, provenance.PayloadDigest),
                signatures,
                vulnerability is null ? null : new(vulnerability.Tool, vulnerability.Policy, vulnerability.Reference, vulnerability.Digest, vulnerability.PayloadDigest));
        }
    }
}
