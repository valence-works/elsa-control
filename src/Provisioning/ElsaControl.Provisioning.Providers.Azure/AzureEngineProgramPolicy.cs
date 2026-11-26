using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fail-closed admission for the elsa-engine program. Same regions and
/// isolation as the current Bicep workload profile. Image identity is
/// digest-only. Handoff stays off unless Control configured it.
/// </summary>
public static class AzureEngineProgramPolicy
{
    public const string SupportedIsolation = "Dedicated";
    public const string SupportedTopology = "combined";
    public const string ImageDigestPrefix = "sha256:";

    public static IReadOnlySet<string> AdmittedLocations { get; } =
        new HashSet<string>(["westeurope", "northeurope", "swedencentral"], StringComparer.OrdinalIgnoreCase);

    public static void Validate(IReadOnlyDictionary<string, string> config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Require(config, "instanceId", out var instanceId);
        if (!Guid.TryParse(instanceId, out var parsed) || parsed == Guid.Empty)
            throw new InvalidOperationException("Engine stack identity is invalid.");

        Require(config, "location", out var location);
        if (!AdmittedLocations.Contains(location))
            throw new InvalidOperationException("The requested location is not admitted.");

        Require(config, "isolation", out var isolation);
        if (!string.Equals(isolation, SupportedIsolation, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested isolation is not admitted.");

        Require(config, "topology", out var topology);
        if (!string.Equals(topology, SupportedTopology, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested topology is not admitted.");

        Require(config, "imageRepository", out var repository);
        if (repository.Contains(':', StringComparison.Ordinal) || repository.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException("Image identity must be a repository without a tag.");

        Require(config, "imageDigest", out var digest);
        if (digest.StartsWith(ImageDigestPrefix, StringComparison.OrdinalIgnoreCase))
            digest = digest[ImageDigestPrefix.Length..];
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Image identity must be a lower-case digest.");

        Require(config, "planFingerprint", out var fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Plan fingerprint is invalid.");

        Require(config, "platformStack", out _);
        Require(config, "sqlBootstrapObjectId", out var bootstrapObjectId);
        if (!Guid.TryParse(bootstrapObjectId, out var bootstrap) || bootstrap == Guid.Empty)
            throw new InvalidOperationException("SQL bootstrap identity is invalid.");
        Require(config, "sqlBootstrapLogin", out var bootstrapLogin);
        if (bootstrapLogin.Length > 128)
            throw new InvalidOperationException("SQL bootstrap login is invalid.");
        Require(config, "sqlConnectionSecretValue", out _);
        Require(config, "signingKeySecretValue", out _);
        Require(config, "adminPasswordSecretValue", out _);

        var revisionSuffix = Get(config, "revisionSuffix");
        if (!string.IsNullOrWhiteSpace(revisionSuffix))
            ProvisioningRevisionSuffix.Validate(revisionSuffix);

        var handoff = Get(config, "managedHandoff");
        if (string.Equals(handoff, "true", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(Get(config, "handoffConfigured"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Managed handoff is declared but not configured.");
            if (Get(config, "minReplicas") != "1" || Get(config, "maxReplicas") != "1")
                throw new InvalidOperationException("Managed handoff requires exactly one replica.");
            Require(config, "handoffControlBaseUrl", out _);
            Require(config, "handoffControlContinuationUrl", out _);
            Require(config, "handoffAudience", out _);
            Require(config, "handoffRuntimeMaximumLifetime", out _);
            Require(config, "handoffAllowedRuntimePermissions", out var permissions);
            if (permissions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length == 0)
                throw new InvalidOperationException("Managed handoff requires runtime permissions.");
        }
    }

    public static string ImmutableImage(IReadOnlyDictionary<string, string> config)
    {
        Validate(config);
        var digest = config["imageDigest"].Trim();
        if (digest.StartsWith(ImageDigestPrefix, StringComparison.OrdinalIgnoreCase))
            digest = digest[ImageDigestPrefix.Length..];
        return $"{config["imageRepository"].Trim()}@{ImageDigestPrefix}{digest.ToLowerInvariant()}";
    }

    private static void Require(IReadOnlyDictionary<string, string> config, string key, out string value)
    {
        if (!config.TryGetValue(key, out var found) || string.IsNullOrWhiteSpace(found))
            throw new InvalidOperationException("Required engine stack config is missing.");
        value = found.Trim();
    }

    private static string? Get(IReadOnlyDictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
