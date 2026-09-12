using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using NuGet.Versioning;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Pure admission and translation boundary for the first governed Azure workload profile.
/// Azure resource realization remains in checked-in Bicep.
/// </summary>
public static class AzureWorkloadPlanTranslator
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public const string SupportedLocation = "westeurope";
    public static IReadOnlySet<string> SupportedLocations { get; } =
        new HashSet<string>([SupportedLocation, "northeurope", "swedencentral"], StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedLocation(string? location) =>
        !string.IsNullOrWhiteSpace(location) && SupportedLocations.Contains(location.Trim());
    public const string SupportedTopology = "combined";
    public const string SupportedIsolation = "Dedicated";
    /// <summary>
    /// Release lines are governed catalog data, not a finite provider enum. The
    /// provider profile validates the immutable image/evidence shape while the
    /// catalog admission boundary determines which Elsa versions are available.
    /// </summary>
    public const string SupportedReleaseLine = "*";
    public const string SupportedRegistryClass = "paid";
    public const string SupportedRegistryHost = "valenceruntimeimages.azurecr.io";
    public const string SupportedRepository = SupportedRegistryHost + "/runtime-combined";
    public const string SqlWorkflowPackageId = "Elsa.Persistence.EFCore.SqlServer";
    public const string SqlQuartzPackageId = "Elsa.Scheduling.Quartz.EFCore.SqlServer";
    public static IReadOnlyList<string> RequiredSecretKeys { get; } = Array.AsReadOnly(
        ["database:connectionstring", "identity:signingkey", "admin:password"]);
    public static AzureWorkloadPlanTranslation Translate(
        ResolvedElsaApplicationPlan? resolvedPlan,
        AzureWorkloadTarget? target)
    {
        var findings = ResolvedElsaApplicationPlanValidator.Validate(resolvedPlan).ToList();
        var basePlanIsValid = findings.Count == 0;
        ValidateTarget(target, findings);

        if (basePlanIsValid && resolvedPlan is not null)
            ValidateProviderProfile(resolvedPlan, findings);

        if (findings.Count > 0 || resolvedPlan is null || target is null)
            return Rejected(findings);

        ResolvedElsaApplicationPlan normalized;
        try
        {
            normalized = resolvedPlan.Normalize();
        }
        catch (ArgumentException)
        {
            findings.Add(new("azure.plan.normalization.invalid", "The resolved plan could not be normalized safely.", "plan"));
            return Rejected(findings);
        }
        var releasePackages = normalized.Release.ComponentDeclarations?.Packages;
        var sqlWorkflowPackageVersion = RequiredPackageVersion(releasePackages, SqlWorkflowPackageId, findings);
        var sqlQuartzPackageVersion = RequiredPackageVersion(releasePackages, SqlQuartzPackageId, findings);
        var component = normalized.Topology.Components.Single();
        var capacity = RequiredCapacity(normalized.Capacity, component, findings);
        if (findings.Count > 0)
            return Rejected(findings);
        var evidence = normalized.Evidence.Single(x =>
            string.Equals(x.Kind, ReleaseManifestEvidenceKinds.Manifest, StringComparison.OrdinalIgnoreCase));
        var signatureEvidence = normalized.Evidence.Single(x =>
            string.Equals(x.Kind, ReleaseManifestEvidenceKinds.Signature, StringComparison.OrdinalIgnoreCase));
        var secretReferences = new ReadOnlyDictionary<string, string>(normalized.Configuration.Entries
            .Where(x => x.Secret && x.SecretReference is not null)
            .ToDictionary(x => x.Key.ToLowerInvariant(), x => x.SecretReference!, StringComparer.OrdinalIgnoreCase));
        foreach (var requiredSecretKey in RequiredSecretKeys)
        {
            if (!secretReferences.ContainsKey(requiredSecretKey))
                findings.Add(new("azure.secret.required", "The admitted plan is missing a secret reference required by the Azure workload profile.", $"configuration:{requiredSecretKey}"));
        }
        if (findings.Count > 0)
            return Rejected(findings);
        var canonicalTarget = new
        {
            workloadName = target.WorkloadName.Trim().ToLowerInvariant(),
            location = target.Location.Trim().ToLowerInvariant()
        };
        var fingerprintInputs = new
        {
            // v2 binds the workload capacity. A capacity change therefore yields a new plan
            // fingerprint and, through it, a new Container Apps revision suffix.
            schema = "azure-workload-plan/v2",
            canonicalTarget.workloadName,
            canonicalTarget.location,
            elsaVersion = normalized.Release.Version,
            releaseLine = normalized.Release.ReleaseLine,
            sourceCommit = normalized.Release.SourceCommit.Trim().ToLowerInvariant(),
            topology = SupportedTopology,
            isolation = SupportedIsolation,
            imageRepository = component.Image.Repository,
            imageDigest = component.Image.Digest.ToLowerInvariant(),
            releaseManifestReference = evidence.Reference,
            releaseManifestDigest = evidence.Digest!.ToLowerInvariant(),
            // The verified OCI manifest digest already binds its payload descriptor.
            // PayloadDigest remains admission evidence in the resolved plan, not a
            // second Azure deployment-intent identity that would be lost on resume.
            releaseManifestSignatureReference = signatureEvidence.Reference,
            releaseManifestSignatureDigest = signatureEvidence.Digest!.ToLowerInvariant(),
            sqlWorkflowPackageVersion,
            sqlQuartzPackageVersion,
            capacity = new
            {
                minReplicas = capacity!.MinReplicas,
                maxReplicas = capacity.MaxReplicas,
                cpuMillicores = capacity.CpuMillicores,
                memoryMiB = capacity.MemoryMiB
            },
            secretReferences = secretReferences
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new { key = x.Key.ToLowerInvariant(), reference = x.Value })
                .ToArray()
        };
        var fingerprintInput = JsonSerializer.Serialize(fingerprintInputs, FingerprintJsonOptions);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));

        return new(
            new(
                canonicalTarget.workloadName,
                canonicalTarget.location,
                normalized.Release.Version,
                normalized.Release.ReleaseLine,
                SupportedTopology,
                SupportedIsolation,
                component.Image.Repository,
                component.Image.Digest["sha256:".Length..].ToLowerInvariant(),
                evidence.Reference,
                evidence.Digest!.ToLowerInvariant(),
                signatureEvidence.Reference,
                signatureEvidence.Digest!.ToLowerInvariant(),
                secretReferences,
                fingerprint,
                sqlWorkflowPackageVersion,
                sqlQuartzPackageVersion,
                capacity),
            []);
    }

    /// <summary>
    /// Selects the governed capacity of the single workload component. Consumption ephemeral
    /// storage is derived from CPU, so a plan asking for more than that CPU provides is
    /// rejected rather than silently given less.
    /// </summary>
    private static AzureWorkloadCapacity? RequiredCapacity(
        ResolvedCapacityOutcome resolvedCapacity,
        ResolvedElsaComponent component,
        ICollection<ResolvedPlanValidationFinding> findings)
    {
        var matches = resolvedCapacity.Components
            .Where(x => string.Equals(x.ComponentId, component.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            findings.Add(new("azure.capacity.required", "The admitted plan must carry exactly one capacity outcome for the Azure workload component.", $"capacity:{component.Id}"));
            return null;
        }

        var resolved = matches[0];
        var capacity = new AzureWorkloadCapacity(resolved.MinReplicas, resolved.MaxReplicas, resolved.CpuMillicores, resolved.MemoryMiB);
        var size = AzureContainerAppsCapacity.Map(capacity);
        if (size is null || resolved.EphemeralStorageMiB > size.EphemeralStorageMiB)
        {
            findings.Add(new("azure.capacity.unsupported", "The resolved capacity has no exact Azure Container Apps consumption mapping.", $"capacity:{component.Id}"));
            return null;
        }
        return capacity;
    }

    private static string? RequiredPackageVersion(
        IReadOnlyList<ResolvedReleasePackageDeclaration>? packages,
        string packageId,
        ICollection<ResolvedPlanValidationFinding> findings)
    {
        var matches = (packages ?? [])
            .Where(package => package is not null &&
                              string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            findings.Add(new("azure.packageMetadata.required", "The admitted plan must contain the provider package metadata required by the Azure profile.", $"packages:{packageId}"));
            return null;
        }
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Version) ||
            matches[0].Version.Length > 128 || matches[0].Version.Any(char.IsControl) ||
            matches[0].Version.Any(char.IsWhiteSpace) ||
            !NuGetVersion.TryParse(matches[0].Version, out _))
        {
            findings.Add(new("azure.packageMetadata.invalid", "The admitted plan contains ambiguous or invalid provider package metadata.", $"packages:{packageId}"));
            return null;
        }
        return matches[0].Version.Trim();
    }

    private static void ValidateTarget(
        AzureWorkloadTarget? target,
        List<ResolvedPlanValidationFinding> findings)
    {
        if (target is null)
        {
            findings.Add(new("azure.target.required", "An Azure workload target is required.", "azure.target"));
            return;
        }

        var workloadName = target.WorkloadName?.Trim();
        if (string.IsNullOrWhiteSpace(workloadName))
            findings.Add(new("azure.workloadName.required", "An Azure workload name is required.", "azure.target.workloadName"));
        else if (workloadName.Length is < 3 or > 16 ||
                 !char.IsAsciiLetterOrDigit(workloadName[0]) ||
                 !char.IsAsciiLetterOrDigit(workloadName[^1]) ||
                 workloadName.Any(x => !char.IsAsciiLetterOrDigit(x) && x != '-'))
            findings.Add(new("azure.workloadName.invalid", "The Azure workload name must contain 3-16 ASCII letters, numbers or hyphens and start and end with a letter or number.", "azure.target.workloadName"));

        if (string.IsNullOrWhiteSpace(target.Location))
            findings.Add(new("azure.location.required", "An Azure location is required.", "azure.target.location"));
        else if (!IsSupportedLocation(target.Location))
            findings.Add(new("azure.location.unsupported", "The requested Azure location is not supported by the initial provider profile.", "azure.target.location"));
    }

    private static void ValidateProviderProfile(
        ResolvedElsaApplicationPlan plan,
        List<ResolvedPlanValidationFinding> findings)
    {
        if (plan.Topology is not null && !string.Equals(plan.Topology.Id, SupportedTopology, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("azure.topology.unsupported", "The requested topology is not supported by the initial Azure provider profile.", "topology.id"));
        else if (plan.Topology?.Components?.Count != 1)
            findings.Add(new("azure.topology.components.unsupported", "The initial Azure Combined profile requires exactly one component.", "topology.components"));

        if (!string.Equals(plan.Isolation, SupportedIsolation, StringComparison.OrdinalIgnoreCase))
            findings.Add(new("azure.isolation.unsupported", "The requested isolation profile is not supported by the initial Azure provider profile.", "isolation"));

        if (plan.Network is not null &&
            (!string.Equals(plan.Network.Ingress, "public", StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(plan.Network.Egress, "unrestricted", StringComparison.OrdinalIgnoreCase) ||
             plan.Network.RequiresPrivateConnectivity ||
             (plan.Network.Endpoints ?? []).Any(x => x is not null && !string.Equals(x.Visibility, "public", StringComparison.OrdinalIgnoreCase))))
        {
            findings.Add(new("azure.network.unsupported", "The initial Azure provider profile supports public ingress, public endpoints and unrestricted egress without private connectivity.", "network"));
        }

        var publicEndpoints = (plan.Network?.Endpoints ?? [])
            .Where(x => x is not null && string.Equals(x.Visibility, "public", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Protocol, x.RequiresTls))
            .Concat((plan.Topology?.Components ?? [])
                .Where(x => x is not null)
                .SelectMany(x => x.Endpoints ?? [])
                .Where(x => x is not null && string.Equals(x.Visibility, "public", StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Protocol, x.RequiresTls)));
        if (publicEndpoints.Any(x => !string.Equals(x.Protocol, "https", StringComparison.OrdinalIgnoreCase) || !x.RequiresTls))
            findings.Add(new("azure.network.tlsRequired", "Public Azure workload endpoints must require HTTPS and TLS.", "network.endpoints"));

        foreach (var capability in plan.ProviderCapabilities ?? [])
        {
            if (capability is null || !capability.Required)
                continue;

            if (!string.Equals(capability.Id, "managed-runtime", StringComparison.OrdinalIgnoreCase) ||
                (capability.Parameters ?? []).Except(["container", "persistent-storage"], StringComparer.OrdinalIgnoreCase).Any())
            {
                findings.Add(new("azure.providerCapability.unsupported", "A required provider capability is not supported by the initial Azure profile.", "providerCapabilities"));
            }
        }

        var images = plan.Topology?.Components?
            .Where(x => x?.Image is not null)
            .Select(x => x.Image)
            .ToArray() ?? [];
        if (images.Any(x => !IsSafeImageRepository(x.Repository)))
            findings.Add(new("azure.imageRepository.invalid", "Azure image repositories must be credential-free registry paths.", "topology.components.image.repository"));
        if (images.Any(x => !string.Equals(x.RegistryClass, SupportedRegistryClass, StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(x.Repository) ||
                            !x.Repository.StartsWith($"{SupportedRegistryHost}/", StringComparison.Ordinal)))
        {
            findings.Add(new("azure.imageRegistry.unsupported", "The image is outside the initial governed Azure registry authority.", "topology.components.image.registry"));
        }
        if (images.Any(x => !ImageReferenceMatchesRepository(x)))
            findings.Add(new("azure.imageReference.repositoryMismatch", "The immutable image reference must match its repository field.", "topology.components.image.reference"));

        ValidateManifestEvidence(plan, findings);
        ValidateSignatureEvidence(plan, findings);
    }

    private static void ValidateSignatureEvidence(
        ResolvedElsaApplicationPlan plan,
        List<ResolvedPlanValidationFinding> findings)
    {
        var evidence = (plan.Evidence ?? [])
            .Where(x => x is not null && string.Equals(x.Kind, ReleaseManifestEvidenceKinds.Signature, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (evidence.Length == 0)
        {
            findings.Add(new("azure.releaseManifestSignatureEvidence.required", "Verified release-manifest signature evidence is required for Azure workload translation.", "evidence"));
            return;
        }

        if (evidence.Length != 1 || evidence[0].Digest is null || !IsSafeEvidenceReference(evidence[0].Reference, evidence[0].Digest))
            findings.Add(new("azure.releaseManifestSignatureEvidence.invalid", "Release-manifest signature evidence must have one safe immutable reference and digest.", "evidence"));
    }

    private static bool IsSafeEvidenceReference(string reference, string? digest)
        => AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(reference, digest);

    private static bool IsSafeImageRepository(string repository)
    {
        return string.Equals(repository, SupportedRepository, StringComparison.Ordinal);
    }

    private static bool ImageReferenceMatchesRepository(ResolvedImageIdentity image)
    {
        if (string.IsNullOrWhiteSpace(image.Reference))
            return false;

        var marker = image.Reference.LastIndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        return marker > 0 && string.Equals(image.Reference[..marker], image.Repository, StringComparison.Ordinal);
    }

    private static void ValidateManifestEvidence(
        ResolvedElsaApplicationPlan plan,
        List<ResolvedPlanValidationFinding> findings)
    {
        if (plan.Release is null)
            return;

        var evidence = (plan.Evidence ?? [])
            .Where(x => x is not null && string.Equals(x.Kind, ReleaseManifestEvidenceKinds.Manifest, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (evidence.Length == 0)
        {
            findings.Add(new("azure.releaseManifestEvidence.required", "Verified release-manifest evidence is required for Azure workload translation.", "evidence"));
            return;
        }

        if (evidence.Length != 1 ||
            !string.Equals(evidence[0].Reference, plan.Release.ReleaseManifestReference, StringComparison.Ordinal) ||
            !string.Equals(evidence[0].Digest, plan.Release.ReleaseManifestDigest, StringComparison.OrdinalIgnoreCase) ||
            !IsSafeEvidenceReference(evidence[0].Reference, evidence[0].Digest))
        {
            findings.Add(new("azure.releaseManifestEvidence.mismatch", "Release-manifest evidence must uniquely match the admitted release reference and digest.", "evidence"));
        }
    }

    private static AzureWorkloadPlanTranslation Rejected(IEnumerable<ResolvedPlanValidationFinding> findings) =>
        new(null, findings
            .DistinctBy(x => (x.Code, x.Scope, x.Message))
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ThenBy(x => x.Scope, StringComparer.Ordinal)
            .ToArray());
}
