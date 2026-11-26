using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Maps an admitted Azure workload plan onto a cloud-neutral engine spec and
/// stack config. Org and slug are tags only. The engine stack name is
/// <c>engine-{instanceId}</c>.
/// </summary>
public static class AzureWorkloadPlanProvisioningMapper
{
    public static string EngineStackName(Guid instanceId) =>
        ProvisioningStackName.ForEngine(instanceId);

    public static string PlatformStackReference(string environmentName, string? organization = null) =>
        ProvisioningStackReference.Platform(environmentName, organization);

    public static EngineSpec Spec(AzureWorkloadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new EngineSpec(
            $"{plan.ImageRepository}@sha256:{plan.ImageDigest}",
            new HashSet<string>(StringComparer.Ordinal)
            {
                ProvisioningCapability.ContainerRuntime,
                ProvisioningCapability.ManagedSql,
                ProvisioningCapability.Secrets,
                ProvisioningCapability.Ingress,
                ProvisioningCapability.Scale
            },
            databaseKind: ProvisioningCapability.ManagedSql,
            secretsKind: ProvisioningCapability.Secrets,
            endpointKind: ProvisioningCapability.Ingress,
            scaleKind: "consumption");
    }

    public static IReadOnlyDictionary<string, string> StackConfig(
        AzureWorkloadPlan plan,
        Guid instanceId,
        string platformStackName,
        string? organizationSlug = null,
        string? engineSlug = null,
        AzureWorkloadCapacity? capacity = null,
        bool handoffConfigured = false,
        AzureManagedHandoffOptions? handoff = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (instanceId == Guid.Empty)
            throw new ArgumentException("Engine stacks require an immutable instance ID.", nameof(instanceId));
        if (!AzureWorkloadPlanTranslator.IsSupportedLocation(plan.Location))
            throw new ArgumentException("The requested location is not admitted.", nameof(plan));
        if (!string.Equals(plan.Isolation, AzureWorkloadPlanTranslator.SupportedIsolation, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The requested isolation is not admitted.", nameof(plan));
        if (plan.ImageDigest.Length != 64 || !plan.ImageDigest.All(Uri.IsHexDigit))
            throw new ArgumentException("Image identity must be a digest.", nameof(plan));
        if (plan.ImageRepository.Contains(':', StringComparison.Ordinal)
            || plan.ImageRepository.Contains('@', StringComparison.Ordinal))
            throw new ArgumentException("Image identity must be a repository without a tag.", nameof(plan));
        if (plan.Fingerprint.Length != 64 || !plan.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Plan fingerprint is invalid.", nameof(plan));
        if (capacity is not null && capacity != plan.Capacity)
            throw new ArgumentException("Capacity override must match the fingerprinted plan capacity.", nameof(capacity));
        var effectiveCapacity = capacity ?? plan.Capacity;
        if (plan.ManagedHandoff && effectiveCapacity is not { MinReplicas: 1, MaxReplicas: 1 })
            throw new ArgumentException("Managed handoff requires exactly one replica.", nameof(plan));

        var size = AzureContainerAppsCapacity.Map(effectiveCapacity);
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = instanceId.ToString("D").ToLowerInvariant(),
            ["location"] = plan.Location.Trim().ToLowerInvariant(),
            ["isolation"] = AzureWorkloadPlanTranslator.SupportedIsolation,
            ["topology"] = AzureWorkloadPlanTranslator.SupportedTopology,
            ["imageRepository"] = plan.ImageRepository.Trim(),
            ["imageDigest"] = plan.ImageDigest.Trim().ToLowerInvariant(),
            ["planFingerprint"] = plan.Fingerprint.ToLowerInvariant(),
            ["platformStack"] = platformStackName.Trim(),
            ["resourceGroupName"] = ProvisioningEngineResourceGroup.ForInstance(instanceId),
            ["revisionSuffix"] = ProvisioningRevisionSuffix.FromFingerprint(plan.Fingerprint),
            ["elsaVersion"] = plan.ElsaVersion,
            ["managedHandoff"] = plan.ManagedHandoff ? "true" : "false",
            ["handoffConfigured"] = handoffConfigured ? "true" : "false",
            ["minReplicas"] = effectiveCapacity?.MinReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["maxReplicas"] = effectiveCapacity?.MaxReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1"
        };
        if (size is not null)
        {
            config["cpu"] = size.Cpu;
            config["memory"] = size.Memory;
        }

        if (!string.IsNullOrWhiteSpace(plan.SqlWorkflowPackageVersion))
            config["sqlWorkflowPackageVersion"] = plan.SqlWorkflowPackageVersion;
        if (!string.IsNullOrWhiteSpace(plan.SqlQuartzPackageVersion))
            config["sqlQuartzPackageVersion"] = plan.SqlQuartzPackageVersion;
        if (!string.IsNullOrWhiteSpace(organizationSlug))
            config["organizationSlug"] = organizationSlug.Trim();
        if (!string.IsNullOrWhiteSpace(engineSlug))
            config["engineSlug"] = engineSlug.Trim();
        config["engineName"] = EngineStackName(instanceId);
        if (handoffConfigured && handoff is not null)
        {
            handoff.Validate();
            config["handoffControlBaseUrl"] = handoff.ControlBaseUrl;
            config["handoffControlContinuationUrl"] = handoff.ControlContinuationUrl;
            config["handoffAudience"] = ElsaInstanceIdentityBinding.AudienceFor(instanceId);
            config["handoffRuntimeMaximumLifetime"] = handoff.RuntimeMaximumLifetimeValue;
            config["handoffAllowedRuntimePermissions"] = string.Join(',', handoff.AllowedRuntimePermissions);
        }

        return config;
    }
}
