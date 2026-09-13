using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Core.Plans;

/// <summary>
/// Resolves provider-neutral Elsa instance intent into an immutable application plan.
/// It consumes only an already admitted release manifest and catalog projections; it
/// neither reads provider configuration nor carries raw manifest/secret payloads.
/// </summary>
public sealed class ElsaInstancePlanResolver(
    IPublicCatalogQueries catalog,
    IPackageCompatibilityService compatibility,
    ElsaInstancePlanResolutionOptions? options = null) : IElsaInstancePlanResolver
{
    private const int MaxPlanIdLength = 128;
    private static readonly Regex PlanIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly IReadOnlyDictionary<string, ElsaCapacityProfile> EmptyProfiles =
        new Dictionary<string, ElsaCapacityProfile>(StringComparer.OrdinalIgnoreCase);

    private ElsaInstancePlanResolutionOptions Options => options ?? ElsaInstancePlanResolutionOptions.Default;

    public async Task<ElsaInstancePlanResolutionResult> ResolveAsync(
        ElsaInstancePlanResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(compatibility);

        var findings = new List<ElsaInstancePlanResolutionFinding>();
        ValidateRequestShape(request, findings);
        if (findings.Count > 0)
            return ElsaInstancePlanResolutionResult.Failed(findings);

        var manifest = request.ReleaseManifest.Manifest!;
        ValidateManifestEvidence(manifest, request.ReleaseManifest, findings);
        ValidateManifestImages(manifest, findings);
        ValidateReleaseSelection(request.InstanceIntent, request.ReleaseManifest, manifest, findings);
        ValidatePlacement(request.InstanceIntent.Placement, findings);
        ValidateLegacyBuilderInputs(request.BuilderIntent, findings);
        ValidatePackageInputs(request.BuilderIntent, findings);
        ValidateApplicationSelection(request.InstanceIntent.Application, request.BuilderIntent, findings);
        if (findings.Count > 0)
            return ElsaInstancePlanResolutionResult.Failed(findings);

        try
        {
            ReleaseManifestPlanProjector.ValidateProjectionShape(manifest);
        }
        catch (ReleaseManifestProjectionValidationException)
        {
            findings.Add(Error("manifest.projection.invalid", "The admitted release manifest cannot produce a valid application plan.", "releaseManifest"));
            return ElsaInstancePlanResolutionResult.Failed(findings);
        }

        PackageResolution packageResolution;
        try
        {
            packageResolution = await ResolvePackagesAsync(request, findings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            findings.Add(Error("catalog.unavailable", "Required catalog metadata could not be loaded.", "catalog"));
            return ElsaInstancePlanResolutionResult.Failed(findings);
        }

        if (findings.Count > 0)
            return ElsaInstancePlanResolutionResult.Failed(findings);

        var selectedFeatures = packageResolution.Packages.SelectMany(x => x.Features).ToArray();
        var configuration = ResolveConfiguration(
            request.BuilderIntent,
            request.InstanceIntent.Application,
            request.GovernedSecretReferences,
            packageResolution.FeatureMetadata,
            findings);
        var basePlan = CreateBasePlan(request, packageResolution.Packages, configuration, selectedFeatures, findings);
        if (findings.Count > 0)
            return ElsaInstancePlanResolutionResult.Failed(findings);

        ResolvedElsaApplicationPlan plan;
        try
        {
            plan = ReleaseManifestPlanProjector.Project(request.ReleaseManifest, basePlan);
        }
        catch (ReleaseManifestProjectionValidationException)
        {
            findings.Add(Error("manifest.projection.invalid", "The admitted release manifest cannot produce a valid application plan.", "releaseManifest"));
            return ElsaInstancePlanResolutionResult.Failed(findings);
        }
        catch (ArgumentException)
        {
            findings.Add(Error("plan.invalid", "Resolved application plan values are invalid.", "plan"));
            return ElsaInstancePlanResolutionResult.Failed(findings);
        }
        catch (InvalidOperationException)
        {
            findings.Add(Error("manifest.projection.invalid", "The admitted release manifest cannot produce a valid application plan.", "releaseManifest"));
            return ElsaInstancePlanResolutionResult.Failed(findings);
        }
        var validationFindings = ResolvedElsaApplicationPlanValidator.Validate(plan);
        foreach (var finding in validationFindings)
            findings.Add(Error(finding.Code, SafeValidationMessage(finding.Code), SafeScope(finding.Scope)));
        if (findings.Count > 0)
            return ElsaInstancePlanResolutionResult.Failed(findings);

        try
        {
            var contentHash = ComputePlanHash(plan);
            var reference = new ElsaResolvedPlanReference(
                request.PlanId,
                int.Parse(ResolvedElsaApplicationPlanSchema.CurrentVersion, System.Globalization.CultureInfo.InvariantCulture),
                contentHash,
                request.PlanUri);
            var currentRelease = new ElsaCurrentResolvedRelease(
                reference,
                plan.Release.DistributionId,
                plan.Release.ReleaseLine,
                plan.Release.Version,
                plan.Release.ReleaseManifestDigest,
                plan.Topology.Components.Select(component => new ElsaComponentDigest(component.Id, component.Image.Digest)));
            return new ElsaInstancePlanResolutionResult(true, plan, reference, currentRelease, findings);
        }
        catch (ArgumentException)
        {
            return ElsaInstancePlanResolutionResult.Failed(
                [.. findings, Error("plan.reference.invalid", "The immutable plan identity is not a safe API reference.", "plan.reference")]);
        }
    }

    private async Task<PackageResolution> ResolvePackagesAsync(
        ElsaInstancePlanResolutionRequest request,
        List<ElsaInstancePlanResolutionFinding> findings,
        CancellationToken cancellationToken)
    {
        var selections = request.BuilderIntent.Packages ?? [];
        var resolved = new List<ResolvedElsaPackage>(selections.Count);
        var featureMetadata = new List<FeatureConfigurationCandidate>();
        var compatibilitySelections = new List<SelectedPackageVersion>(selections.Count);
        var selectedFeatureIds = new List<string>();
        var topologyRuntimeKinds = request.ReleaseManifest.Manifest!.Topologies
            .First(topology => string.Equals(topology.Id, request.InstanceIntent.Application.TopologyId, StringComparison.OrdinalIgnoreCase))
            .RuntimeKinds;

        foreach (var selection in selections)
        {
            if (selection is null || selection.SourceId == Guid.Empty || !IsSafeIdentityText(selection.PackageId) || !IsSafeIdentityText(selection.Version))
            {
                findings.Add(Error("package.selection.invalid", "A package selection is invalid.", "packages"));
                continue;
            }

            var version = request.WorkspaceId is { } workspaceId
                ? await catalog.GetVersionForWorkspaceAsync(workspaceId, selection.SourceId, selection.PackageId, selection.Version, cancellationToken)
                : await catalog.GetVersionAsync(selection.SourceId, selection.PackageId, selection.Version, cancellationToken);
            if (version is null)
            {
                findings.Add(Error("package.notFound", "A selected package version is not available in the governed catalog.", "packages"));
                continue;
            }

            if (version.Source is null
                || version.Source.Id != selection.SourceId
                || !string.Equals(version.PackageId, selection.PackageId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(version.Version, selection.Version, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Error("package.selection.mismatch", "Catalog metadata does not match the selected package identity.", "packages"));
                continue;
            }

            if (!IsSha256(version.ManifestDigest))
            {
                findings.Add(Error("package.manifestDigest.invalid", "A selected package is missing an immutable manifest digest.", "packages"));
                continue;
            }

            var extensionClass = MapExtensionClass(version.ExtensionClass);
            if (extensionClass == ResolvedExtensionClass.Unspecified)
            {
                findings.Add(Error("package.extensionClass.required", "A selected package is missing its governed extension classification.", "packages"));
                continue;
            }
            if (!IsSha256(version.PolicyEvidenceDigest))
            {
                findings.Add(Error("package.policyEvidenceDigest.invalid", "A selected package is missing immutable policy evidence.", "packages"));
                continue;
            }
            if (!ResolvedExtensionPolicy.IsAllowed(request.InstanceIntent.Placement.IsolationProfile, extensionClass))
            {
                findings.Add(Error("package.extensionClass.forbidden", "The selected extension class is not permitted by the isolation profile.", "packages"));
                continue;
            }

            if (!RuntimeKindCompatibilityPolicy.IsCompatible(version.RuntimeKinds, topologyRuntimeKinds))
                findings.Add(Error("package.runtimeKindUnsupported", "A selected package is incompatible with the selected topology runtime kinds.", "packages"));

            var requestedFeatures = (selection.SelectedFeatures ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            selectedFeatureIds.AddRange(requestedFeatures);
            var features = new List<ResolvedElsaFeature>();
            foreach (var featureId in requestedFeatures)
            {
                var feature = (version.Features ?? []).FirstOrDefault(candidate => candidate is not null && FeatureMatches(candidate, featureId));
                if (feature is null)
                {
                    findings.Add(Error("feature.notFound", "A selected feature is not available for the selected package version.", "features"));
                    continue;
                }

                featureMetadata.Add(new(selection, feature));
                if (!RuntimeKindCompatibilityPolicy.IsCompatible(feature.RuntimeKinds, version.RuntimeKinds))
                    findings.Add(Error("feature.runtimeKindUnsupported", "A selected feature is incompatible with the selected package runtime kinds.", "features"));

                features.Add(new(
                    feature.FeatureId,
                    feature.TypeName,
                    feature.RuntimeKinds ?? [],
                    (feature.RequiredCapabilities ?? [])
                        .Concat((feature.Infrastructure ?? [])
                            .Where(requirement => requirement is not null)
                            .SelectMany(requirement => requirement.Capabilities ?? []))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
            }

            compatibilitySelections.Add(new(selection.SourceId, version.PackageId, version.Version));
            resolved.Add(new(
                version.Source.Id,
                version.PackageId,
                version.Version,
                version.ManifestDigest!,
                version.RuntimeKinds ?? [],
                features,
                extensionClass,
                version.PolicyEvidenceDigest));
        }

        if (findings.Count > 0)
            return new PackageResolution(resolved, featureMetadata);

        var compatibilityResult = await compatibility.CheckAsync(
            new CompatibilityCheckRequest(
                request.ReleaseManifest.Manifest!.Distribution.ReleaseVersion,
                null,
                compatibilitySelections,
                selectedFeatureIds.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                request.WorkspaceId,
                request.ReleaseManifest.Manifest!.Topologies.FirstOrDefault(topology => string.Equals(topology.Id, request.InstanceIntent.Application.TopologyId, StringComparison.OrdinalIgnoreCase))?.RuntimeKinds),
            cancellationToken);
        if (!compatibilityResult.Compatible)
            findings.Add(Error("compatibility.rejected", "Catalog compatibility checks rejected the selected application inputs.", "compatibility"));

        return new PackageResolution(
            resolved
                .OrderBy(x => x.SourceId)
                .ThenBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            featureMetadata);
    }

    private ResolvedConfigurationShape ResolveConfiguration(
        RuntimeBuilderIntent builderIntent,
        ElsaApplicationIntent application,
        IReadOnlyDictionary<string, string>? governedSecretReferences,
        IReadOnlyList<FeatureConfigurationCandidate> features,
        List<ElsaInstancePlanResolutionFinding> findings)
    {
        var entries = new List<ResolvedConfigurationEntry>();
        var governedSecrets = governedSecretReferences ?? new Dictionary<string, string>();
        if (governedSecrets.Count > 64)
            findings.Add(Error("configuration.governedSecrets.tooMany", "Governed secret references exceed the supported limit.", "configuration"));
        var governedSecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in governedSecrets)
        {
            var key = pair.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || !governedSecretKeys.Add(key) ||
                !ConfigurationKeyPolicy.IsSafe(key) ||
                !IsSafeSecretReference(pair.Value))
            {
                findings.Add(Error("configuration.governedSecret.invalid", "A governed secret reference is invalid or ambiguous.", "configuration"));
                continue;
            }

            entries.Add(new(
                key,
                "string",
                true,
                true,
                true,
                null,
                null,
                pair.Value.Trim(),
                null));
        }
        var ambiguousSettingNames = features
            .SelectMany(candidate => (candidate.Feature.Settings ?? [])
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Name))
                .Select(setting => setting.Name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var settingName in ambiguousSettingNames)
            findings.Add(Error("configuration.key.ambiguous", "Configuration setting identities must be unique across selected features.", "configuration"));

        var supplied = (builderIntent.Packages ?? [])
            .Where(x => x is not null && x.Settings is not null)
            .SelectMany(selection => selection.Settings!
                .Where(feature => feature.Value is not null)
                .SelectMany(feature => feature.Value.Select(setting => (selection, FeatureId: feature.Key, Setting: setting.Key, Value: setting.Value))))
            .ToList();

        foreach (var suppliedValue in supplied)
        {
            if (string.IsNullOrWhiteSpace(suppliedValue.Setting))
            {
                findings.Add(Error("configuration.key.required", "A configuration setting identity is required.", "configuration.entries"));
                continue;
            }

            if (!ConfigurationKeyPolicy.IsSafe(suppliedValue.Setting))
            {
                findings.Add(Error("configuration.key.invalid", "Configuration key must be a canonical safe identifier.", "configuration.entries"));
                continue;
            }

            if (ambiguousSettingNames.Contains(suppliedValue.Setting))
                continue;

            var matchingFeatures = features
                .Where(candidate => PackageSelectionMatches(candidate.Selection, suppliedValue.selection)
                    && FeatureMatches(candidate.Feature, suppliedValue.FeatureId))
                .ToList();
            if (matchingFeatures.Count > 1)
            {
                findings.Add(Error("configuration.feature.ambiguous", "Configuration feature identities must resolve to one selected package feature.", "configuration"));
                continue;
            }

            if (matchingFeatures.Count == 0)
            {
                findings.Add(Error("configuration.feature.unknown", "Configuration references an unselected feature.", "configuration"));
                continue;
            }

            var matchingSettings = (matchingFeatures[0].Feature.Settings ?? [])
                .Where(setting => string.Equals(setting.Name, suppliedValue.Setting, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matchingSettings.Count > 1)
            {
                findings.Add(Error("configuration.setting.ambiguous", "Configuration setting identities must resolve to one governed setting.", "configuration"));
                continue;
            }

            var matching = matchingSettings.SingleOrDefault();
            if (matching is null)
            {
                findings.Add(Error("configuration.setting.unknown", "Configuration references an unknown feature setting.", "configuration"));
                continue;
            }

            var key = matching.Name;
            if (string.IsNullOrWhiteSpace(key))
            {
                findings.Add(Error("configuration.key.required", "A configuration setting identity is required.", "configuration.entries"));
                continue;
            }

            if (!ConfigurationKeyPolicy.IsSafe(key))
            {
                findings.Add(Error("configuration.key.invalid", "Configuration key must be a canonical safe identifier.", "configuration.entries"));
                continue;
            }

            if (entries.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(Error("configuration.key.duplicate", "Configuration contains duplicate setting identities.", "configuration"));
                continue;
            }

            if (!IsSupportedJsonType(matching.JsonType))
            {
                findings.Add(Error("configuration.type.unsupported", "A configuration setting has an unsupported JSON type.", "configuration"));
                continue;
            }

            if (matching.Secret)
            {
                if (suppliedValue.Value.ValueKind != JsonValueKind.String || !IsSafeSecretReference(suppliedValue.Value.GetString()))
                {
                    findings.Add(Error("configuration.secretValue.forbidden", "Secret settings must use a safe external secret reference.", "configuration"));
                    continue;
                }

                entries.Add(new(key, matching.JsonType, matching.Required, true, matching.RestartRequired, matching.EnvironmentVariable, null, suppliedValue.Value.GetString(), suppliedValue.FeatureId));
            }
            else
            {
                if (ContainsSensitiveKey(key) || ContainsSecretLikeValue(suppliedValue.Value))
                {
                    findings.Add(Error("configuration.secretValue.forbidden", "Secret values cannot be embedded in a resolved plan.", "configuration"));
                    continue;
                }

                if (!MatchesJsonType(suppliedValue.Value, matching.JsonType))
                {
                    findings.Add(Error("configuration.value.typeMismatch", "A configuration value does not match its governed JSON type.", "configuration"));
                    continue;
                }

                entries.Add(new(key, matching.JsonType, matching.Required, false, matching.RestartRequired, matching.EnvironmentVariable, suppliedValue.Value, null, suppliedValue.FeatureId));
            }
        }

        foreach (var candidate in features)
        {
            foreach (var setting in candidate.Feature.Settings ?? [])
            {
                if (string.IsNullOrWhiteSpace(setting.Name))
                {
                    findings.Add(Error("configuration.key.required", "A configuration setting identity is required.", "configuration.entries"));
                    continue;
                }

                if (!ConfigurationKeyPolicy.IsSafe(setting.Name))
                {
                    findings.Add(Error("configuration.key.invalid", "Configuration key must be a canonical safe identifier.", "configuration.entries"));
                    continue;
                }

                if (!IsSupportedJsonType(setting.JsonType))
                {
                    findings.Add(Error("configuration.type.unsupported", "A configuration setting has an unsupported JSON type.", "configuration"));
                    continue;
                }

                if (setting.Secret && !string.IsNullOrWhiteSpace(setting.DefaultValueJson))
                    findings.Add(Error("configuration.secretValue.forbidden", "Secret values cannot be embedded in a resolved plan.", "configuration"));
                if (ambiguousSettingNames.Contains(setting.Name)
                    || entries.Any(x => string.Equals(x.Key, setting.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                JsonElement? value = null;
                if (!setting.Secret && !string.IsNullOrWhiteSpace(setting.DefaultValueJson))
                {
                    try
                    {
                        value = ParseJsonElement(setting.DefaultValueJson);
                        if (ContainsSensitiveKey(setting.Name) || ContainsSecretLikeValue(value.Value)
                            || !MatchesJsonType(value.Value, setting.JsonType))
                        {
                            findings.Add(Error(
                                ContainsSensitiveKey(setting.Name) || ContainsSecretLikeValue(value.Value)
                                    ? "configuration.secretValue.forbidden"
                                    : "configuration.default.typeMismatch",
                                "A catalog configuration default is not safe for a resolved plan.",
                                "configuration"));
                            value = null;
                        }
                    }
                    catch (JsonException)
                    {
                        findings.Add(Error("configuration.default.invalid", "A catalog configuration default is invalid.", "configuration"));
                    }
                }
                if (setting.Required && value is null)
                    findings.Add(Error(
                        "configuration.requiredValue.missing",
                        setting.Secret
                            ? "Required configuration needs an external secret reference."
                            : "Required configuration needs a governed value.",
                        "configuration"));

                entries.Add(new(setting.Name, setting.JsonType, setting.Required, setting.Secret, setting.RestartRequired, setting.EnvironmentVariable, value, null, candidate.Feature.FeatureId));
            }
        }

        foreach (var overrideValue in application.FeatureOverrides ?? new Dictionary<string, ElsaFeatureOverride>(StringComparer.OrdinalIgnoreCase))
        {
            if (overrideValue.Value is null)
            {
                findings.Add(Error("application.featureOverride.invalid", "A feature override is invalid.", "application.featureOverrides"));
                continue;
            }

            if (!IsSafeIdentityText(overrideValue.Key))
            {
                findings.Add(Error("application.featureOverride.invalid", "A feature override is invalid.", "application.featureOverrides"));
                continue;
            }

            var definition = Options.EffectiveFeatureOverrideDefinitions
                .FirstOrDefault(x => string.Equals(x.Key, overrideValue.Key, StringComparison.OrdinalIgnoreCase));
            if (definition.Key is null)
            {
                findings.Add(Error("application.featureOverride.unsupported", "The requested feature override is not governed for resolution.", "application.featureOverrides"));
                continue;
            }

            if (definition.Value != overrideValue.Value.Kind)
            {
                findings.Add(Error("application.featureOverride.kindMismatch", "The requested feature override kind does not match its governed definition.", "application.featureOverrides"));
                continue;
            }

            var key = $"featureOverride.{overrideValue.Key}";
            if (!ConfigurationKeyPolicy.IsSafe(key))
            {
                findings.Add(Error("configuration.key.invalid", "Configuration key must be a canonical safe identifier.", "configuration.entries"));
                continue;
            }

            if (ContainsSensitiveKey(key) || ContainsSecretLikeValue(JsonSerializer.SerializeToElement(overrideValue.Value.Value)))
            {
                findings.Add(Error("configuration.secretValue.forbidden", "Secret values cannot be embedded in a resolved plan.", "configuration"));
                continue;
            }

            if (entries.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                findings.Add(Error("configuration.key.duplicate", "Configuration contains duplicate setting identities.", "configuration"));
                continue;
            }

            var jsonType = overrideValue.Value.Kind switch
            {
                ElsaFeatureOverrideKind.Boolean => "boolean",
                ElsaFeatureOverrideKind.Number => "number",
                ElsaFeatureOverrideKind.Catalog => "string",
                _ => ""
            };

            decimal number = default;
            if (overrideValue.Value.Kind == ElsaFeatureOverrideKind.Number &&
                !decimal.TryParse(overrideValue.Value.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
            {
                findings.Add(Error("configuration.override.invalid", "A typed feature override is invalid.", "configuration"));
                continue;
            }

            var value = overrideValue.Value.Kind switch
            {
                ElsaFeatureOverrideKind.Boolean => JsonSerializer.SerializeToElement(overrideValue.Value.Value.Equals("true", StringComparison.OrdinalIgnoreCase)),
                ElsaFeatureOverrideKind.Number => JsonSerializer.SerializeToElement(number),
                ElsaFeatureOverrideKind.Catalog => JsonSerializer.SerializeToElement(overrideValue.Value.Value),
                _ => default
            };

            entries.Add(new(key, jsonType, false, false, false, null, value, null, null));
        }

        return new ResolvedConfigurationShape(entries);
    }

    private ResolvedElsaApplicationPlan CreateBasePlan(
        ElsaInstancePlanResolutionRequest request,
        IReadOnlyList<ResolvedElsaPackage> packages,
        ResolvedConfigurationShape configuration,
        IReadOnlyList<ResolvedElsaFeature> selectedFeatures,
        List<ElsaInstancePlanResolutionFinding> findings)
    {
        var placement = request.InstanceIntent.Placement;
        var topologyId = request.InstanceIntent.Application.TopologyId;
        var profile = FindCapacityProfile(placement.CapacityProfile);
        if (profile is null)
            findings.Add(Error("capacity.profile.unsupported", "The requested capacity profile is not governed for resolution.", "placement.capacityProfile"));

        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in selectedFeatures)
            foreach (var capability in feature.RequiredCapabilities)
                AddSafeToken(capabilities, capability, "feature.capability.invalid", "providerCapabilities", findings);
        foreach (var infrastructure in request.BuilderIntent.Infrastructure ?? [])
            if (infrastructure is not null)
                AddSafeToken(capabilities, infrastructure.Kind, "infrastructure.kind.invalid", "providerCapabilities", findings);
        if (request.InstanceIntent.Application.FeaturePresetId is { } featurePresetId)
            AddSafeToken(capabilities, $"feature-preset:{featurePresetId}", "application.featurePreset.invalid", "providerCapabilities", findings);
        if (request.InstanceIntent.Application.PackagePolicy is { } packagePolicy)
            AddSafeToken(capabilities, $"package-policy:{packagePolicy}", "application.packagePolicy.invalid", "providerCapabilities", findings);
        if (request.InstanceIntent.Application.ConfigurationShapeRevisionId is { } shapeRevisionId)
            AddSafeToken(capabilities, $"configuration-shape:{shapeRevisionId}", "application.configurationShape.invalid", "providerCapabilities", findings);

        var capacityComponents = profile is null
            ? []
            : request.ReleaseManifest.Manifest!.Topologies
                .First(topology => string.Equals(topology.Id, topologyId, StringComparison.OrdinalIgnoreCase))
                .Images
                .Where(image => image is not null && string.Equals(image.RegistryClass, request.ReleaseManifest.RegistryClass, StringComparison.OrdinalIgnoreCase))
                .GroupBy(image => image!.ComponentId ?? topologyId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ResolvedComponentCapacity(group.Key, profile.MinReplicas, profile.MaxReplicas, profile.CpuMillicores, profile.MemoryMiB, profile.EphemeralStorageMiB))
                .ToArray();

        var manifestTopology = request.ReleaseManifest.Manifest!.Topologies.First(topology => string.Equals(topology.Id, topologyId, StringComparison.OrdinalIgnoreCase));
        var networkEndpoints = manifestTopology.Images
            .Where(image => image is not null && string.Equals(image.RegistryClass, request.ReleaseManifest.RegistryClass, StringComparison.OrdinalIgnoreCase))
            .SelectMany(image => (image!.Endpoints ?? []).Select(endpoint => new ResolvedNetworkEndpoint(
                image.ComponentId ?? topologyId,
                endpoint.Name,
                endpoint.Protocol,
                endpoint.Port,
                endpoint.Visibility,
                endpoint.RequiresTls,
                EndpointPathPolicy.Normalize(endpoint.Path))))
            .GroupBy(endpoint => $"{endpoint.ComponentId}:{endpoint.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var providerCapabilities = capabilities
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(id => new ProviderCapabilityRequirement(id, "Required provider capability for the resolved application.", true, []))
            .ToArray();

        return new(
            ResolvedElsaApplicationPlanSchema.CurrentVersion,
            new("pending", "pending", "pending", "https://control.example.invalid/source", new string('0', 40), "oci://pending/manifest@sha256:" + new string('0', 64), "sha256:" + new string('0', 64)),
            new(topologyId, []),
            packages,
            configuration,
            new(capacityComponents, []),
            new(placement.NetworkOutcome, Options.DefaultEgress, string.Equals(placement.NetworkOutcome, "private", StringComparison.OrdinalIgnoreCase), [], networkEndpoints),
            placement.IsolationProfile,
            new(request.InstanceIntent.Release.Channel, request.ReleaseManifest.Manifest!.Distribution.Lifecycle, Options.RolloutRing, request.InstanceIntent.Release.PatchUpdates, request.InstanceIntent.Release.MinorUpdates, request.InstanceIntent.Release.MajorMigrations),
            providerCapabilities,
            request.ExistingEvidence ?? []);
    }

    private ElsaCapacityProfile? FindCapacityProfile(string name)
    {
        var profiles = Options.CapacityProfiles ?? EmptyProfiles;
        if (Options.CapacityProfiles is null)
            profiles = Options.EffectiveCapacityProfiles;
        return profiles.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static bool PackageSelectionMatches(BundlePackageSelection selected, BundlePackageSelection requested) =>
        selected.SourceId == requested.SourceId
        && string.Equals(selected.PackageId, requested.PackageId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(selected.Version, requested.Version, StringComparison.OrdinalIgnoreCase);

    private static bool FeatureMatches(PublicFeatureProjection feature, string requestedFeatureId)
    {
        string? shellName = null;
        if (!string.IsNullOrWhiteSpace(feature.ExtensionsJson))
        {
            try
            {
                using var json = JsonDocument.Parse(feature.ExtensionsJson);
                if (json.RootElement.TryGetProperty("cshellsFeatureName", out var property) && property.ValueKind == JsonValueKind.String)
                    shellName = property.GetString();
            }
            catch (JsonException)
            {
                // A malformed extension cannot grant an alias; exact identity remains valid.
            }
        }

        return FeatureDependencyIdentityPolicy.Matches(feature.FeatureId, shellName, requestedFeatureId);
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void ValidateRequestShape(ElsaInstancePlanResolutionRequest request, List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (request.InstanceIntent is null)
            findings.Add(Error("intent.required", "Instance intent is required.", "intent"));
        if (request.BuilderIntent is null)
            findings.Add(Error("builderIntent.required", "Builder intent is required.", "builderIntent"));
        if (request.ReleaseManifest is null)
            findings.Add(Error("releaseManifest.required", "An admitted release manifest is required.", "releaseManifest"));
        else if (!request.ReleaseManifest.Accepted || request.ReleaseManifest.Manifest is null || request.ReleaseManifest.SignatureEvidence is null)
            findings.Add(Error("releaseManifest.notAdmitted", "Only an admitted signed release manifest can be resolved.", "releaseManifest"));
        else if (request.ReleaseManifest.Manifest.Distribution is null || request.ReleaseManifest.Manifest.Topologies is null)
            findings.Add(Error("releaseManifest.invalid", "The admitted release manifest is structurally incomplete.", "releaseManifest"));
        else
        {
            var signatureEvidence = request.ReleaseManifest.SignatureEvidence;
            if (!ReleaseManifestAdmissionService.IsDigest(request.ReleaseManifest.Digest)
                || string.IsNullOrWhiteSpace(request.ReleaseManifest.Reference)
                || !ReleaseManifestAdmissionService.IsSafeEvidenceReference(request.ReleaseManifest.Reference!, request.ReleaseManifest.Digest)
                || !ReleaseManifestAdmissionService.IsDigest(signatureEvidence.Digest)
                || !ReleaseManifestAdmissionService.IsSafeEvidenceReference(signatureEvidence.Reference, signatureEvidence.Digest))
                findings.Add(Error("releaseManifest.evidence.invalid", "Admitted release evidence must retain safe immutable references and sha256 digests.", "releaseManifest.evidence"));
            if (request.ReleaseManifest.Findings is null || request.ReleaseManifest.Findings.Count > 0)
                findings.Add(Error("releaseManifest.findings.invalid", "An admitted release manifest cannot carry unresolved admission findings.", "releaseManifest"));
        }
        var validPlanId = false;
        if (string.IsNullOrWhiteSpace(request.PlanId))
            findings.Add(Error("plan.id.required", "An immutable plan identity is required.", "plan"));
        else if (!IsSafePlanId(request.PlanId))
            findings.Add(Error("plan.id.invalid", "The immutable plan identity must be a bounded API token.", "plan"));
        else
            validPlanId = true;
        if (string.IsNullOrWhiteSpace(request.PlanUri))
            findings.Add(Error("plan.uri.required", "A dereferenceable plan API URI is required.", "plan"));
        else if (validPlanId && !IsInstancePlanUri(request.PlanUri, request.PlanId, request.WorkspaceId))
            findings.Add(Error("plan.uri.invalid", "The plan URI must be the control-plane instance resolved-plan route.", "plan"));
    }

    private void ValidateReleaseSelection(ElsaInstanceIntent intent, ReleaseManifestAdmissionResult admission, CommercialReleaseManifest manifest, List<ElsaInstancePlanResolutionFinding> findings)
    {
        var release = intent.Release;
        var distribution = manifest.Distribution;
        if (!string.Equals(release.DistributionId, distribution.Id, StringComparison.OrdinalIgnoreCase))
            findings.Add(Error("release.distribution.mismatch", "The selected release distribution is not the admitted distribution.", "release"));
        if (!string.Equals(release.ReleaseLine, distribution.ReleaseLine, StringComparison.OrdinalIgnoreCase))
            findings.Add(Error("release.line.mismatch", "The selected release line is not the admitted release line.", "release"));
        if (!string.Equals(release.Channel, distribution.Channel, StringComparison.OrdinalIgnoreCase))
            findings.Add(Error("release.channel.mismatch", "The selected release channel is not the admitted channel.", "release"));
        if (!Options.EffectiveSupportedChannels.Contains(distribution.Channel))
            findings.Add(Error("release.channel.unsupported", "The admitted release channel is not supported for instance resolution.", "release"));
        if (release.RequestedVersion is not null && !string.Equals(release.RequestedVersion, distribution.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
            findings.Add(Error("release.version.mismatch", "The requested release version is not the admitted exact version.", "release"));
        if (!BelongsToLine(release.ReleaseLine, distribution.ReleaseVersion))
            findings.Add(Error("release.version.lineMismatch", "The admitted release version does not belong to the selected release line.", "release"));
        if (!Options.EffectiveSupportedLifecycles.Contains(distribution.Lifecycle))
            findings.Add(Error("release.lifecycle.unsupported", "The admitted release lifecycle is not supported for deployment.", "release"));

        var topology = manifest.Topologies?.FirstOrDefault(x => x is not null && string.Equals(x.Id, intent.Application.TopologyId, StringComparison.OrdinalIgnoreCase));
        if (topology is null)
            findings.Add(Error("topology.notFound", "The selected topology is not present in the admitted release manifest.", "topology"));
        var admittedTopologyId = string.IsNullOrWhiteSpace(admission.TopologyId)
            ? manifest.Topologies?.FirstOrDefault(x => x is not null)?.Id
            : admission.TopologyId;
        if (!string.Equals(admittedTopologyId, intent.Application.TopologyId, StringComparison.OrdinalIgnoreCase))
            findings.Add(Error("topology.selection.mismatch", "The admitted topology selection does not match instance intent.", "topology"));
    }

    private static void ValidateManifestEvidence(
        CommercialReleaseManifest manifest,
        ReleaseManifestAdmissionResult admission,
        List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (admission.SignatureEvidence is null)
            findings.Add(Error("supplyChain.signatures.required", "Retained release signature evidence is required.", "releaseManifest.evidence"));

        foreach (var topology in manifest.Topologies ?? [])
        {
            if (topology is null)
            {
                findings.Add(Error("supplyChain.invalid", "Release supply-chain evidence is incomplete.", "releaseManifest.evidence"));
                continue;
            }

            var supplyChain = topology.SupplyChain;
            if (supplyChain is null)
            {
                findings.Add(Error("supplyChain.required", "Retained release supply-chain evidence is required.", "releaseManifest.evidence"));
                continue;
            }

            if (supplyChain.Sbom is null)
                findings.Add(Error("supplyChain.sbom.required", "Retained release SBOM evidence is required.", "releaseManifest.evidence"));
            else if (!IsStrictEvidenceReference(supplyChain.Sbom.Uri, supplyChain.Sbom.Digest))
                findings.Add(Error("supplyChain.sbom.invalid", "Release SBOM evidence must be a safe immutable locator with a sha256 digest.", "releaseManifest.evidence"));

            if (supplyChain.Provenance is null)
                findings.Add(Error("supplyChain.provenance.required", "Retained release provenance evidence is required.", "releaseManifest.evidence"));
            else if (!IsStrictEvidenceReference(supplyChain.Provenance.Uri, supplyChain.Provenance.Digest))
                findings.Add(Error("supplyChain.provenance.invalid", "Release provenance evidence must be a safe immutable locator with a sha256 digest.", "releaseManifest.evidence"));

            if (supplyChain.VulnerabilityScan is null)
                findings.Add(Error("supplyChain.vulnerabilityScan.required", "Retained release vulnerability-scan evidence is required.", "releaseManifest.evidence"));
            else if (!IsStrictEvidenceReference(supplyChain.VulnerabilityScan.Report, supplyChain.VulnerabilityScan.Digest))
                findings.Add(Error("supplyChain.vulnerabilityScan.invalid", "Release vulnerability-scan evidence must be a safe immutable locator with a sha256 digest.", "releaseManifest.evidence"));
        }
    }

    private static void ValidateManifestImages(CommercialReleaseManifest manifest, List<ElsaInstancePlanResolutionFinding> findings)
    {
        foreach (var topology in manifest.Topologies ?? [])
        {
            foreach (var image in topology?.Images ?? [])
            {
                if (image is null
                    || !ReleaseManifestAdmissionService.IsDigest(image.IndexDigest)
                    || !ReleaseManifestAdmissionService.IsSafeImageReference(image.Reference)
                    || !ReleaseManifestAdmissionService.IsImmutableImageReference(image.Reference)
                    || !string.Equals(ReleaseManifestAdmissionService.ExtractDigest(image.Reference), image.IndexDigest, StringComparison.OrdinalIgnoreCase)
                    || (image.PlatformDigests?.Any(x => !ReleaseManifestAdmissionService.IsDigest(x.Value)) ?? false)
                    || (image.Endpoints?.Where(x => x is not null).GroupBy(x => x!.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1) ?? false)
                    || (image.Endpoints?.Any(endpoint => endpoint is null
                        || (!string.IsNullOrWhiteSpace(endpoint.Path) && !EndpointPathPolicy.IsSafe(endpoint.Path))) ?? false))
                    findings.Add(Error("releaseManifest.image.invalid", "Topology components must use safe immutable image references and sha256 digests.", "releaseManifest.images"));
            }
        }
    }

    private static void ValidatePlacement(ElsaPlacementIntent placement, List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (string.Equals(placement.TargetMode, "managed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(placement.RegionCode))
            findings.Add(Error("placement.region.required", "Managed placement requires a governed region outcome.", "placement"));
        if (string.Equals(placement.TargetMode, "managed", StringComparison.OrdinalIgnoreCase) &&
            !ResolvedExtensionPolicy.IsAvailableForManagedLaunch(placement.IsolationProfile))
            findings.Add(Error("isolation.profile.unavailable", "The selected isolation profile is not available for managed deployment.", "isolation"));
    }

    private void ValidateLegacyBuilderInputs(RuntimeBuilderIntent builderIntent, List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (builderIntent.Image is null)
        {
            findings.Add(Error("builder.image.required", "A builder image selection is required.", "builderIntent.image"));
        }
        else
        {
            if (!IsSafeIdentityText(builderIntent.Image.Slug))
                findings.Add(Error("builder.image.slug.invalid", "A runtime image identity is invalid.", "builderIntent.image"));
            if (!string.IsNullOrWhiteSpace(builderIntent.Image.Tag))
                findings.Add(Error("builder.image.tag.unsupported", "Mutable image tags cannot be used for instance resolution.", "builderIntent.image"));
            if (builderIntent.Image.HostPort is not null)
                findings.Add(Error("builder.image.hostPort.unsupported", "Provider-specific host ports cannot be used for instance resolution.", "builderIntent.image"));
            if (builderIntent.Image.EnvOverrides is { Count: > 0 })
                findings.Add(Error("builder.image.environment.unsupported", "Unvalidated environment overrides cannot be used for instance resolution.", "builderIntent.image"));
        }

        if (builderIntent.PackageSources is { Count: > 0 })
            findings.Add(Error("builder.packageSources.unsupported", "Package source selections are not part of the provider-neutral instance contract.", "builderIntent.packageSources"));

        if (builderIntent.LocalPackages is not null &&
            (builderIntent.LocalPackages.Enabled || !string.IsNullOrWhiteSpace(builderIntent.LocalPackages.DirectoryPath)))
            findings.Add(Error("builder.localPackages.unsupported", "Local package paths cannot be used for instance resolution.", "builderIntent.localPackages"));

        if (builderIntent.Target is not null)
            findings.Add(Error("builder.target.unsupported", "Provider-specific deployment targets cannot be used for instance resolution.", "builderIntent.target"));

        foreach (var infrastructure in builderIntent.Infrastructure ?? [])
        {
            if (infrastructure is null)
            {
                findings.Add(Error("builder.infrastructure.invalid", "Infrastructure selections cannot be null.", "builderIntent.infrastructure"));
                continue;
            }

            if (infrastructure.ProviderId is not null || infrastructure.Strategy is not null)
                findings.Add(Error("builder.infrastructure.provider.unsupported", "Provider-specific infrastructure selections cannot be used for instance resolution.", "builderIntent.infrastructure"));
            if (infrastructure.Settings is { Count: > 0 })
                findings.Add(Error("builder.infrastructure.settings.unsupported", "Infrastructure settings cannot cross the provider-neutral plan boundary.", "builderIntent.infrastructure"));
        }
    }

    private static void ValidatePackageInputs(RuntimeBuilderIntent builderIntent, List<ElsaInstancePlanResolutionFinding> findings)
    {
        foreach (var selection in builderIntent.Packages ?? [])
        {
            if (selection is null)
            {
                findings.Add(Error("package.selection.invalid", "A package selection is invalid.", "packages"));
                continue;
            }

            if (selection.SourceId == Guid.Empty || !IsSafeIdentityText(selection.PackageId) || !IsSafeIdentityText(selection.Version))
                findings.Add(Error("package.selection.invalid", "A package selection is invalid.", "packages"));
            else if (!ResolvedPackageVersionPolicy.IsExact(selection.Version))
                findings.Add(Error("package.version.inexact", "An exact semantic package version is required.", "packages"));

            foreach (var featureId in selection.SelectedFeatures ?? [])
                if (!IsSafeIdentityText(featureId))
                    findings.Add(Error("feature.selection.invalid", "A selected feature identity is invalid.", "features"));

            foreach (var feature in selection.Settings ?? new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>())
            {
                if (feature.Value is null)
                {
                    findings.Add(Error("configuration.feature.invalid", "A feature configuration selection is invalid.", "configuration"));
                    continue;
                }

                foreach (var setting in feature.Value)
                    if (!IsSafeIdentityText(feature.Key)
                        || !IsSafeIdentityText(setting.Key)
                        || setting.Value.ValueKind == JsonValueKind.Undefined)
                        findings.Add(Error("configuration.setting.invalid", "A configuration setting selection is invalid.", "configuration"));
            }
        }
    }

    private void ValidateApplicationSelection(
        ElsaApplicationIntent application,
        RuntimeBuilderIntent builderIntent,
        List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (application.FeaturePresetId is not null)
        {
            var preset = Options.EffectiveFeaturePresets.FirstOrDefault(x => string.Equals(x.Key, application.FeaturePresetId, StringComparison.OrdinalIgnoreCase));
            if (preset.Value is null)
            {
                findings.Add(Error("application.featurePreset.unsupported", "The requested feature preset is not governed for resolution.", "application.featurePreset"));
            }
            else
            {
                var selected = (builderIntent.Packages ?? [])
                    .Where(x => x is not null)
                    .SelectMany(x => x.SelectedFeatures ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var requiredFeature in preset.Value)
                    if (!selected.Contains(requiredFeature))
                        findings.Add(Error("application.featurePreset.incomplete", "The selected packages do not satisfy the governed feature preset.", "application.featurePreset"));
            }
        }

        if (application.PackagePolicy is not null)
        {
            var policy = Options.EffectivePackagePolicies.FirstOrDefault(x => string.Equals(x.Key, application.PackagePolicy, StringComparison.OrdinalIgnoreCase));
            if (policy.Value is null)
                findings.Add(Error("application.packagePolicy.unsupported", "The requested package policy is not governed for resolution.", "application.packagePolicy"));
            else if (policy.Value.Count > 0)
            {
                var selectedPackages = (builderIntent.Packages ?? [])
                    .Where(x => x is not null)
                    .Select(x => x.PackageId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (policy.Value.Any(packageId => !selectedPackages.Contains(packageId)))
                    findings.Add(Error("application.packagePolicy.incomplete", "The selected packages do not satisfy the governed package policy.", "application.packagePolicy"));
            }
        }

        if (application.ConfigurationShapeRevisionId is not null &&
            (Options.ConfigurationShapeRevisions is null ||
             !Options.ConfigurationShapeRevisions.Keys.Any(x => string.Equals(x, application.ConfigurationShapeRevisionId, StringComparison.OrdinalIgnoreCase))))
            findings.Add(Error("application.configurationShape.unsupported", "The requested configuration shape revision is not governed for resolution.", "application.configurationShape"));
    }

    private static bool BelongsToLine(string releaseLine, string version) =>
        string.Equals(releaseLine, version, StringComparison.OrdinalIgnoreCase)
        || version.StartsWith(releaseLine + ".", StringComparison.OrdinalIgnoreCase);

    private static string ComputePlanHash(ResolvedElsaApplicationPlan plan)
        => ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan);

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 71
        && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && value[7..].All(Uri.IsHexDigit);

    private static ResolvedExtensionClass MapExtensionClass(PackageExtensionClass extensionClass) =>
        extensionClass switch
        {
            PackageExtensionClass.BuiltIn => ResolvedExtensionClass.BuiltIn,
            PackageExtensionClass.ValenceApproved => ResolvedExtensionClass.ValenceApproved,
            PackageExtensionClass.ArbitraryCustomer => ResolvedExtensionClass.ArbitraryCustomer,
            _ => ResolvedExtensionClass.Unspecified
        };

    private static bool IsStrictEvidenceReference(string reference, string? digest) =>
        (digest is null || ReleaseManifestAdmissionService.IsDigest(digest))
        && ReleaseManifestAdmissionService.IsSafeEvidenceReference(reference, digest);

    private static bool IsInstancePlanUri(string value, string? planId, Guid? expectedWorkspaceId)
    {
        if (string.IsNullOrWhiteSpace(planId)
            || !string.Equals(planId, planId.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var segments = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 7
            || !segments[0].Equals("api", StringComparison.Ordinal)
            || !segments[1].Equals("workspaces", StringComparison.Ordinal)
            || !Guid.TryParseExact(segments[2], "D", out var workspaceId)
            || !segments[3].Equals("instances", StringComparison.Ordinal)
            || !Guid.TryParseExact(segments[4], "D", out _)
            || !segments[5].Equals("resolved-plans", StringComparison.Ordinal)
            || !segments[6].Equals(Uri.EscapeDataString(planId), StringComparison.Ordinal))
            return false;

        if (!uri.AbsolutePath.EndsWith('/' + segments[6], StringComparison.Ordinal))
            return false;

        return expectedWorkspaceId is null || workspaceId == expectedWorkspaceId.Value;
    }

    private static bool IsSafeSecretReference(string? value)
        => SecretReferencePolicy.IsSafe(value);

    private static bool IsSafePlanId(string? value)
    {
        if (value is null
            || value.Any(char.IsControl)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return false;

        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxPlanIdLength
            && PlanIdPattern.IsMatch(value);
    }

    private static bool ContainsSensitiveKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSecretLikeValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ContainsSecretLikeText(value.GetString()),
            JsonValueKind.Object => value.EnumerateObject().Any(property =>
                ContainsSensitiveKey(property.Name) || ContainsSecretLikeValue(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(ContainsSecretLikeValue),
            _ => false
        };
    }

    private static bool ContainsSecretLikeText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api-key=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apikey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("credential=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("connectionstring=", StringComparison.OrdinalIgnoreCase));

    private static bool MatchesJsonType(JsonElement value, string? jsonType) =>
        jsonType?.Trim().ToLowerInvariant() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };

    private static bool IsSupportedJsonType(string? jsonType) =>
        jsonType?.Trim().ToLowerInvariant() is "string" or "boolean" or "integer" or "number" or "array" or "object" or "null";

    private static bool IsSafeIdentityText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl)
        && !value.Any(char.IsWhiteSpace);

    private static void AddSafeToken(HashSet<string> target, string? value, string code, string scope, List<ElsaInstancePlanResolutionFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace))
        {
            findings.Add(Error(code, "A governed capability identity is invalid.", scope));
            return;
        }
        target.Add(value.Trim());
    }

    private static string SafeValidationMessage(string code) =>
        code switch
        {
            "configuration.secretValue.forbidden" => "Secret values cannot be embedded in a resolved plan.",
            "configuration.secretReference.invalid" => SecretReferencePolicy.InvalidReferenceMessage,
            _ => "Resolved plan validation rejected a required contract value."
        };

    private static string SafeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsControl))
            return "plan";

        var category = scope.Split([':', '/', '.'], 2)[0];
        return category switch
        {
            "schemaVersion" => "releaseManifest",
            "release" => "release",
            "topology" or "component" => "topology",
            "package" or "packages" => "packages",
            "feature" or "features" => "features",
            "configuration" => "configuration",
            "capacity" or "storage" => "capacity",
            "network" => "network",
            "providerCapability" or "providerCapabilities" => "providerCapabilities",
            "evidence" => "evidence",
            "releasePolicy" => "releasePolicy",
            "isolation" => "placement",
            _ => "plan"
        };
    }

    private static ElsaInstancePlanResolutionFinding Error(string code, string message, string scope) =>
        ElsaInstancePlanResolutionFinding.Error(code, message, scope);

    private sealed record PackageResolution(
        IReadOnlyList<ResolvedElsaPackage> Packages,
        IReadOnlyList<FeatureConfigurationCandidate> FeatureMetadata);

    private sealed record FeatureConfigurationCandidate(
        BundlePackageSelection Selection,
        PublicFeatureProjection Feature);
}
