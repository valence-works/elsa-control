using System.Text.Json;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.RuntimeConfigurations;

namespace ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;

/// <summary>
/// Projects a saved Builder intent into the narrow input shape accepted by managed
/// instance resolution. This adapter deliberately does not select a release, query
/// the catalog, or authorize secret references; those decisions remain at the
/// governed lifecycle resolver boundary.
/// </summary>
public static class ManagedRuntimeConfigurationAdapter
{
    private const string ManagedImageSlug = "elsa-instance";

    public static ManagedRuntimeConfigurationProjection Project(RuntimeBuilderIntent? intent)
    {
        if (intent is null)
        {
            return new(
                null,
                false,
                [Error("builder.intent.required", "A runtime builder intent is required for managed provisioning.", "builderIntent")]);
        }

        var findings = new List<ElsaInstancePlanResolutionFinding>();
        var image = ProjectImage(intent.Image, findings);
        var packages = ProjectPackages(intent.Packages, findings);
        var infrastructure = ProjectInfrastructure(intent.Infrastructure, findings);

        if (intent.PackageSources is { Count: > 0 })
        {
            findings.Add(Error(
                "builder.packageSources.unsupported",
                "Custom package sources are export-only and cannot cross the managed catalog boundary.",
                "builderIntent.packageSources"));
        }

        var localPackages = intent.LocalPackages;
        if (localPackages is { Enabled: true } || !string.IsNullOrWhiteSpace(localPackages?.DirectoryPath))
        {
            findings.Add(Error(
                "builder.localPackages.unsupported",
                "Local package paths cannot be used for managed provisioning.",
                "builderIntent.localPackages"));
        }

        if (!string.IsNullOrWhiteSpace(intent.Target))
        {
            findings.Add(Info(
                "builder.target.ignored",
                "The Builder deployment target is export metadata; managed provisioning uses the governed placement and provider path.",
                "builderIntent.target"));
        }

        var projected = new RuntimeBuilderIntent(
            image,
            packages,
            [],
            infrastructure,
            null,
            null);

        return new(
            projected,
            findings.All(x => !string.Equals(x.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            findings);
    }

    private static RuntimeImageSelection ProjectImage(
        RuntimeImageSelection? source,
        ICollection<ElsaInstancePlanResolutionFinding> findings)
    {
        if (source is null)
        {
            findings.Add(Error("builder.image.required", "A runtime image selection is required.", "builderIntent.image"));
            return new(ManagedImageSlug, null, null, null);
        }

        if (!IsSafeIdentityText(source.Slug))
        {
            findings.Add(Error("builder.image.slug.invalid", "The runtime image identity is invalid.", "builderIntent.image"));
        }
        else if (!string.Equals(source.Slug, ManagedImageSlug, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Info(
                "builder.image.slug.ignored",
                "The Builder image selection is export metadata; managed provisioning uses the image admitted by the governed release manifest.",
                "builderIntent.image"));
        }

        if (!string.IsNullOrWhiteSpace(source.Tag))
        {
            findings.Add(Info(
                "builder.image.tag.ignored",
                "The Builder image tag is export metadata; managed provisioning uses the exact image admitted by the governed release manifest.",
                "builderIntent.image"));
        }

        if (source.HostPort is not null)
        {
            findings.Add(Info(
                "builder.image.hostPort.ignored",
                "The Builder host port is export metadata; managed provisioning uses governed provider endpoints.",
                "builderIntent.image"));
        }

        if (source.EnvOverrides is { Count: > 0 })
        {
            findings.Add(Error(
                "builder.image.environment.unsupported",
                "Unvalidated image environment overrides cannot be used for managed provisioning.",
                "builderIntent.image"));
        }

        // The managed release admission, rather than the Builder image catalog,
        // owns the image identity that eventually reaches a provider.
        return new(ManagedImageSlug, null, null, null);
    }

    private static IReadOnlyList<BundlePackageSelection> ProjectPackages(
        IReadOnlyList<BundlePackageSelection>? source,
        ICollection<ElsaInstancePlanResolutionFinding> findings)
    {
        var projected = new List<BundlePackageSelection>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in source ?? [])
        {
            var packageFindingCount = findings.Count;
            if (selection is null)
            {
                findings.Add(Error("package.selection.invalid", "A package selection is invalid.", "builderIntent.packages"));
                continue;
            }

            if (selection.SourceId == Guid.Empty ||
                !IsSafeIdentityText(selection.PackageId) ||
                !ResolvedPackageVersionPolicy.IsExact(selection.Version))
            {
                findings.Add(Error("package.selection.invalid", "A package selection must use a governed source, package identity and exact version.", "builderIntent.packages"));
                continue;
            }

            var identity = $"{selection.SourceId:D}:{selection.PackageId}:{selection.Version}";
            if (!identities.Add(identity))
            {
                findings.Add(Error("package.selection.duplicate", "A package selection is duplicated.", "builderIntent.packages"));
                continue;
            }

            var features = new List<string>();
            foreach (var feature in selection.SelectedFeatures ?? [])
            {
                if (!IsSafeIdentityText(feature))
                {
                    findings.Add(Error("feature.selection.invalid", "A selected feature identity is invalid.", "builderIntent.packages"));
                    continue;
                }

                if (!features.Contains(feature, StringComparer.OrdinalIgnoreCase))
                    features.Add(feature);
            }

            var settings = ProjectSettings(selection.Settings, findings);
            if (findings.Count > packageFindingCount)
                continue;

            projected.Add(new(
                selection.SourceId,
                selection.PackageId,
                selection.Version,
                features,
                settings));
        }

        return projected;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? ProjectSettings(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? source,
        ICollection<ElsaInstancePlanResolutionFinding> findings)
    {
        if (source is null)
            return null;

        var projected = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in source)
        {
            if (!IsSafeIdentityText(feature.Key) || feature.Value is null)
            {
                findings.Add(Error("configuration.setting.invalid", "A feature configuration selection is invalid.", "builderIntent.packages"));
                continue;
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in feature.Value)
            {
                if (!IsSafeIdentityText(setting.Key) || setting.Value.ValueKind == JsonValueKind.Undefined)
                {
                    findings.Add(Error("configuration.setting.invalid", "A configuration setting identity or value is invalid.", "builderIntent.packages"));
                    continue;
                }

                if (values.ContainsKey(setting.Key))
                {
                    findings.Add(Error("configuration.setting.duplicate", "A configuration setting identity is duplicated.", "builderIntent.packages"));
                    continue;
                }

                var wellFormed = IsWellFormedJsonValue(setting.Value);
                if (!wellFormed || !IsSafeConfigurationValue(setting.Key, setting.Value))
                {
                    findings.Add(Error(
                        wellFormed
                            ? "configuration.secretValue.forbidden"
                            : "configuration.setting.invalid",
                        wellFormed
                            ? "Configuration values must be non-secret or safe external secret references."
                            : "A configuration setting contains an unsupported value.",
                        "builderIntent.packages"));
                    continue;
                }

                values.Add(setting.Key, setting.Value.Clone());
            }

            if (!projected.ContainsKey(feature.Key))
                projected.Add(feature.Key, values);
            else
                findings.Add(Error("configuration.feature.duplicate", "A feature configuration identity is duplicated.", "builderIntent.packages"));
        }

        return projected.Count == 0 ? null : projected;
    }

    private static IReadOnlyList<InfrastructureSelection> ProjectInfrastructure(
        IReadOnlyList<InfrastructureSelection>? source,
        ICollection<ElsaInstancePlanResolutionFinding> findings)
    {
        var projected = new List<InfrastructureSelection>();
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in source ?? [])
        {
            if (selection is null || !IsSafeCapabilityIdentity(selection.Kind))
            {
                findings.Add(Error("builder.infrastructure.kind.invalid", "An infrastructure capability identity is invalid.", "builderIntent.infrastructure"));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selection.ProviderId) ||
                !string.IsNullOrWhiteSpace(selection.Strategy) ||
                selection.Settings is { Count: > 0 })
            {
                findings.Add(Error(
                    "builder.infrastructure.provider.unsupported",
                    "Provider-specific infrastructure selections cannot cross the managed provider-neutral boundary.",
                    "builderIntent.infrastructure"));
                continue;
            }

            if (kinds.Add(selection.Kind))
            {
                // ProviderId and Strategy are intentionally null in the managed
                // projection. The existing resolver consumes only Kind, while
                // Builder export requires the provider-specific pair.
                projected.Add(new InfrastructureSelection(selection.Kind, null!, null!, null));
            }
        }

        return projected;
    }

    private static bool IsSafeConfigurationValue(string key, JsonElement value)
    {
        if (ContainsSensitiveKey(key))
            return IsSafeSecretReferenceValue(value);

        return !ContainsUnsafeSecretLikeValue(value);
    }

    private static bool IsWellFormedJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined => false,
            JsonValueKind.Object => value.EnumerateObject().All(property => IsWellFormedJsonValue(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().All(IsWellFormedJsonValue),
            _ => true
        };

    private static bool IsSafeSecretReferenceValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && SecretReferencePolicy.IsSafe(value.GetString());

    private static bool ContainsUnsafeSecretLikeValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => ContainsUnsafeSecretLikeText(value.GetString()),
            JsonValueKind.Object => value.EnumerateObject().Any(property =>
                (ContainsSensitiveKey(property.Name) && !IsSafeSecretReferenceValue(property.Value)) ||
                ContainsUnsafeSecretLikeValue(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(ContainsUnsafeSecretLikeValue),
            _ => false
        };

    private static bool ContainsUnsafeSecretLikeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.StartsWith("secret://", StringComparison.OrdinalIgnoreCase))
            return !SecretReferencePolicy.IsSafe(value);

        return value.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("password=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("token=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api-key=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apikey=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("credential=", StringComparison.OrdinalIgnoreCase)
            || value.Contains("connectionstring=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSensitiveKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || key.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeCapabilityIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Any(char.IsControl)
        && !value.Any(char.IsWhiteSpace);

    private static bool IsSafeIdentityText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && !value.Any(char.IsControl)
        && !value.Any(char.IsWhiteSpace);

    private static ElsaInstancePlanResolutionFinding Error(string code, string message, string scope) =>
        ElsaInstancePlanResolutionFinding.Error(code, message, scope);

    private static ElsaInstancePlanResolutionFinding Info(string code, string message, string scope) =>
        new("info", code, message, scope);
}
