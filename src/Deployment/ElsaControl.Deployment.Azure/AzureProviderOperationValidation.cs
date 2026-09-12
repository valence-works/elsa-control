using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using ElsaControl.Deployment.Abstractions.Instances;
using NuGet.Versioning;

namespace ElsaControl.Deployment.Azure;

public static class AzureProviderOperationValidation
{
    /// <summary>
    /// Binds a delete's canonical key (including bounded retry lineage) to its lifecycle
    /// operation. Unattributable long-chain hashed keys require explicit recovery.
    /// </summary>
    public static bool IsLifecycleDeleteIdempotencyKey(string? key, Guid lifecycleOperationId)
    {
        var root = AzureElsaInstanceProvider.IdempotencyKey(lifecycleOperationId) + ":delete";
        if (lifecycleOperationId == Guid.Empty || key is null || key.Length > 512 ||
            !key.StartsWith(root, StringComparison.Ordinal))
            return false;
        var remaining = key.AsSpan(root.Length);
        for (var retries = 0; !remaining.IsEmpty; retries++)
        {
            const string separator = ":retry:";
            if (retries >= 31 || remaining.Length < separator.Length + 32 ||
                !remaining.StartsWith(separator, StringComparison.Ordinal))
                return false;
            var id = remaining.Slice(separator.Length, 32);
            if (!Guid.TryParseExact(id, "N", out var operationId) || operationId == Guid.Empty ||
                !id.SequenceEqual(operationId.ToString("N")))
                return false;
            remaining = remaining[(separator.Length + 32)..];
        }
        return true;
    }

    public static void ValidateCheckpoint(AzureProviderCheckpoint checkpoint)
    {
        if (checkpoint is null) throw new ArgumentNullException(nameof(checkpoint));
        if (checkpoint.Resources is null) throw new ArgumentException("Resources are required.", nameof(checkpoint));
        if (!Enum.IsDefined(checkpoint.Phase) || !Enum.IsDefined(checkpoint.Health) ||
            checkpoint.AttemptedStep is { } attemptedStep && !Enum.IsDefined(attemptedStep) ||
            !IsSafeCode(checkpoint.Code))
            throw new ArgumentException("Checkpoint code, phase, health, or attempted step is invalid.", nameof(checkpoint));
        if (string.IsNullOrWhiteSpace(checkpoint.Message) || checkpoint.Message.Length > 2000 || checkpoint.Message.Any(char.IsControl) || ContainsSensitiveMarker(checkpoint.Message))
            throw new ArgumentException("Checkpoint message is unsafe.", nameof(checkpoint));
        ValidateEndpoint(checkpoint.Endpoint);
        if (!IsSafeDiagnostics(checkpoint.Diagnostics)) throw new ArgumentException("Diagnostics are required and bounded.", nameof(checkpoint));
        ValidateReferences(checkpoint.Resources);
    }

    /// <summary>
    /// Applies the same bounded, value-free diagnostics contract to persisted values that are
    /// read back from storage. Invalid stored diagnostics must not be returned through status
    /// responses, even when their JSON is syntactically valid.
    /// </summary>
    public static bool IsSafeDiagnostics(IReadOnlyList<AzureProviderDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count > 20)
            return false;

        try
        {
            if (JsonSerializer.Serialize(diagnostics).Length > 10000)
                return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        return diagnostics.All(diagnostic => diagnostic is not null &&
            IsSafeCode(diagnostic.Code) &&
            !string.IsNullOrWhiteSpace(diagnostic.Message) &&
            diagnostic.Message.Length <= 2000 &&
            !diagnostic.Message.Any(char.IsControl) &&
            !ContainsSensitiveMarker(diagnostic.Message));
    }

    public static void ValidateCode(string code)
    {
        if (!IsSafeCode(code)) throw new ArgumentException("Operation event code is unsafe.", nameof(code));
    }

    public static void ValidateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2000 || message.Any(char.IsControl) || ContainsSensitiveMarker(message))
            throw new ArgumentException("Operation event message is unsafe.", nameof(message));
    }

    public static void ValidateLeaseToken(string leaseToken)
    {
        if (string.IsNullOrWhiteSpace(leaseToken) || leaseToken.Length > 512 || leaseToken.Any(char.IsControl))
            throw new ArgumentException("Lease token is unsafe.", nameof(leaseToken));
    }

    public static void ValidateEndpoint(string? endpoint)
    {
        _ = NormalizeEndpoint(endpoint);
    }

    public static string? NormalizeEndpoint(string? endpoint)
    {
        if (endpoint is null) return null;
        if (!ElsaManagedEndpointOrigin.TryCreate(endpoint, out var origin))
            throw new ArgumentException("Endpoint must be a safe HTTPS origin.", nameof(endpoint));
        return origin.Value;
    }

    /// <summary>
    /// Validates an immutable evidence locator without resolving or dereferencing it. Evidence
    /// references are metadata only; their digest is validated separately by the admission
    /// contract. OCI and HTTPS are the only supported locator schemes.
    /// </summary>
    public static bool IsSafeImmutableLocator(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 2048 ||
            reference.Any(char.IsWhiteSpace) ||
            reference.Contains("/../", StringComparison.Ordinal) ||
            reference.Contains("/./", StringComparison.Ordinal) ||
            reference.Contains('\\') ||
            !Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "oci" && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath is "/" or "" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Any(char.IsControl) ||
            uri.AbsolutePath.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return false;

        var unescapedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        return !unescapedPath.Split('/').Any(segment => segment is "." or "..");
    }

    /// <summary>
    /// Validates an evidence locator and binds an optional digest embedded in the locator to
    /// the separately retained digest. Evidence without either digest form is not immutable.
    /// </summary>
    public static bool IsSafeImmutableEvidenceReference(string? reference, string? digest)
    {
        if (!IsSafeImmutableLocator(reference))
            return false;

        var at = reference!.IndexOf('@');
        var embeddedDigest = at < 0 ? null : reference[(at + 1)..];
        if (at >= 0 && (at != reference.LastIndexOf('@') || !IsSha256Digest(embeddedDigest)))
            return false;
        if (embeddedDigest is not null && IsSha256Digest(digest) &&
            !string.Equals(embeddedDigest, digest, StringComparison.OrdinalIgnoreCase))
            return false;
        return IsSha256Digest(digest) || embeddedDigest is not null;
    }

    public static void ValidateReferences(AzureProviderResourceReferences references)
    {
        if (references is null) throw new ArgumentNullException(nameof(references));
        ValidateAzureName(references.ResourceGroupName, 90, "resourceGroupName");
        ValidateAzureReference(references.FoundationDeploymentId, 512, "foundationDeploymentId");
        ValidateAzureReference(references.WorkloadDeploymentId, 512, "workloadDeploymentId");
        ValidateAzureReference(references.WorkloadResourceId, 1024, "workloadResourceId");
        ValidateAzureName(references.WorkloadRevisionName, 128, "workloadRevisionName");
        ValidateAzureName(references.StableTrafficRevisionName, 128, "stableTrafficRevisionName");
        ValidateAzureReference(references.WorkloadIdentityResourceId, 1024, "workloadIdentityResourceId");
        ValidateGuid(references.WorkloadIdentityClientId, "workloadIdentityClientId");
        ValidateGuid(references.WorkloadIdentityPrincipalId, "workloadIdentityPrincipalId");
        ValidateAzureReference(references.KeyVaultResourceId, 1024, "keyVaultResourceId");
        ValidateHttpsOrigin(references.KeyVaultUri, "keyVaultUri");
        ValidateAzureReference(references.SqlServerResourceId, 1024, "sqlServerResourceId");
        ValidateDnsName(references.SqlServerFqdn, 253, "sqlServerFqdn");
        ValidateAzureReference(references.ContainerAppsEnvironmentResourceId, 1024, "containerAppsEnvironmentResourceId");
        ValidateAzureReference(references.RegistryResourceId, 1024, "registryResourceId");
        ValidateAzureReference(references.AcrPullDeploymentId, 512, "acrPullDeploymentId");
        ValidateAzureReference(references.AcrPullRoleAssignmentId, 1024, "acrPullRoleAssignmentId");
        ValidateReferenceRelationships(references);
    }

    public static void ValidateWorkerId(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128 || !Regex.IsMatch(workerId, "^[A-Za-z0-9][A-Za-z0-9._-]*\\z"))
            throw new ArgumentException("Worker ID is unsafe.", nameof(workerId));
    }

    private static void ValidateAzureName(string? value, int max, string name)
    {
        if (value is null) return;
        if (value.Length > max || !Regex.IsMatch(value, "^[A-Za-z0-9._()\\-]+\\z") || ContainsSensitiveMarker(value))
            throw new ArgumentException("Azure resource name is unsafe.", name);
    }

    private static void ValidateAzureReference(string? value, int maxLength, string name)
    {
        if (value is null) return;
        if (value.Length > maxLength || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) ||
            value.Contains("?", StringComparison.Ordinal) || value.Contains("#", StringComparison.Ordinal) ||
            value.Contains("@", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal) ||
            ContainsSensitiveMarker(value) || !Regex.IsMatch(value, "^[A-Za-z0-9._:/()\\-]+\\z"))
            throw new ArgumentException("Azure resource reference is unsafe.", name);
    }

    private static void ValidateGuid(string? value, string name)
    {
        if (value is null) return;
        if (!Guid.TryParseExact(value, "D", out _) || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("Azure identity reference is unsafe.", name);
    }

    private static void ValidateHttpsOrigin(string? value, string name)
    {
        if (value is null) return;
        if (value.Length > 512 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            !uri.IsDefaultPort || !uri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
            throw new ArgumentException("Azure HTTPS origin is unsafe.", name);
    }

    private static void ValidateReferenceRelationships(AzureProviderResourceReferences references)
    {
        var foundation = ParseArmId(references.FoundationDeploymentId, "Microsoft.Resources", "deployments", "foundationDeploymentId");
        var workloadDeployment = ParseArmId(references.WorkloadDeploymentId, "Microsoft.Resources", "deployments", "workloadDeploymentId");
        var workload = ParseArmId(references.WorkloadResourceId, "Microsoft.App", "containerApps", "workloadResourceId");
        var identity = ParseArmId(references.WorkloadIdentityResourceId, "Microsoft.ManagedIdentity", "userAssignedIdentities", "workloadIdentityResourceId");
        var vault = ParseArmId(references.KeyVaultResourceId, "Microsoft.KeyVault", "vaults", "keyVaultResourceId");
        var sql = ParseArmId(references.SqlServerResourceId, "Microsoft.Sql", "servers", "sqlServerResourceId");
        var environment = ParseArmId(references.ContainerAppsEnvironmentResourceId, "Microsoft.App", "managedEnvironments", "containerAppsEnvironmentResourceId");
        var registry = ParseArmId(references.RegistryResourceId, "Microsoft.ContainerRegistry", "registries", "registryResourceId");
        var acrDeployment = ParseArmId(references.AcrPullDeploymentId, "Microsoft.Resources", "deployments", "acrPullDeploymentId");

        var groupFacts = new[] { foundation, workloadDeployment, workload, identity, vault, sql, environment }
            .Where(x => x is not null)
            .Cast<ArmResourceId>()
            .ToArray();
        if (groupFacts.Length > 0)
        {
            var first = groupFacts[0];
            if (references.ResourceGroupName is null ||
                groupFacts.Any(x => !string.Equals(x.SubscriptionId, first.SubscriptionId, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(x.ResourceGroupName, references.ResourceGroupName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Azure resource references do not belong to the owned target scope.", nameof(references));
        }

        if (registry is not null && acrDeployment is not null &&
            (!string.Equals(registry.SubscriptionId, acrDeployment.SubscriptionId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(registry.ResourceGroupName, acrDeployment.ResourceGroupName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Azure registry references do not share the configured registry scope.", nameof(references));

        if (references.AcrPullRoleAssignmentId is not null)
        {
            if (registry is null || !Regex.IsMatch(
                    references.AcrPullRoleAssignmentId,
                    $"^{Regex.Escape(references.RegistryResourceId!)}/providers/Microsoft\\.Authorization/roleAssignments/[0-9a-f]{{8}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{12}}\\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                throw new ArgumentException("The ACR role assignment is outside the exact registry scope.", nameof(references));
        }

        if (vault is not null && references.KeyVaultUri is not null &&
            !string.Equals(new Uri(references.KeyVaultUri).Host, $"{vault.Name}.vault.azure.net", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Key Vault URI does not match its resource identity.", nameof(references));
        if (sql is not null && references.SqlServerFqdn is not null &&
            !string.Equals(references.SqlServerFqdn, $"{sql.Name}.database.windows.net", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The SQL endpoint does not match its resource identity.", nameof(references));
    }

    private static ArmResourceId? ParseArmId(string? value, string provider, string type, string name)
    {
        if (value is null) return null;
        var match = Regex.Match(
            value,
            "^/subscriptions/(?<subscription>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/resourceGroups/(?<group>[A-Za-z0-9._()\\-]+)/providers/(?<provider>[A-Za-z0-9.]+)/(?<type>[A-Za-z0-9.]+)/(?<name>[A-Za-z0-9._()\\-]+)\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (!match.Success || !string.Equals(match.Groups["provider"].Value, provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(match.Groups["type"].Value, type, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Azure resource reference has an unexpected type or scope.", name);
        return new(match.Groups["subscription"].Value, match.Groups["group"].Value, match.Groups["name"].Value);
    }

    private sealed record ArmResourceId(string SubscriptionId, string ResourceGroupName, string Name);

    private static void ValidateDnsName(string? value, int maxLength, string name)
    {
        if (value is null) return;
        if (value.Length > maxLength || value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) ||
            !Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+\\z"))
            throw new ArgumentException("Azure DNS name is unsafe.", name);
    }

    private static bool IsSafeCode(string? value) => value is not null && value.Length <= 128 && Regex.IsMatch(value, "^[a-z0-9]+(?:[._-][a-z0-9]+)*\\z");

    private static bool IsSha256Digest(string? value) => value is not null && value.Length == "sha256:".Length + 64 &&
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && value["sha256:".Length..].All(Uri.IsHexDigit);

    private static bool IsSafeRepository(string? value) => value is not null && value.Length <= 512 &&
        Regex.IsMatch(value, "^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?)*\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static AzureProviderOperationRequest Normalize(AzureProviderOperationRequest request)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors), nameof(request));

        return request with
        {
            TargetKey = request.TargetKey.Trim().ToLowerInvariant(),
            PlanFingerprint = NormalizeFingerprint(request.PlanFingerprint),
            TemplateFingerprint = NormalizeFingerprint(request.TemplateFingerprint),
            ImageDigest = NormalizeDigest(request.ImageDigest),
            ReleaseManifestDigest = NormalizeOptionalDigest(request.ReleaseManifestDigest),
            ReleaseManifestSignatureDigest = NormalizeOptionalDigest(request.ReleaseManifestSignatureDigest),
            Location = request.Location.Trim().ToLowerInvariant(),
            Topology = request.Topology.Trim().ToLowerInvariant(),
            Isolation = request.Isolation.Trim().ToLowerInvariant(),
            ReleaseLine = request.ReleaseLine.Trim(),
            ElsaVersion = request.ElsaVersion.Trim(),
            ImageRepository = request.ImageRepository.Trim().ToLowerInvariant(),
            IdempotencyKey = request.IdempotencyKey.Trim(),
            ReleaseManifestReference = NormalizeOptionalReference(request.ReleaseManifestReference),
            ReleaseManifestSignatureReference = NormalizeOptionalReference(request.ReleaseManifestSignatureReference),
            SqlWorkflowPackageVersion = NormalizeOptionalSafe(request.SqlWorkflowPackageVersion),
            SqlQuartzPackageVersion = NormalizeOptionalSafe(request.SqlQuartzPackageVersion),
            SecretReferences = NormalizeSecretReferences(request.SecretReferences),
            ProviderScopeFingerprint = request.ProviderScopeFingerprint is null
                ? null
                : NormalizeFingerprint(request.ProviderScopeFingerprint)
        };
    }

    public static IReadOnlyList<string> Validate(AzureProviderOperationRequest request)
    {
        var errors = new List<string>();
        if (request.WorkspaceId == Guid.Empty) errors.Add("workspace.required");
        if (request.OrganizationId is { } organizationId && organizationId == Guid.Empty) errors.Add("organization.invalid");
        if (request.InstanceId is { } instanceId && instanceId == Guid.Empty) errors.Add("instance.invalid");
        if ((request.OrganizationId is null) != (request.InstanceId is null)) errors.Add("instanceBinding.incomplete");
        if (request.LifecycleAction is { } lifecycleAction && !Enum.IsDefined(lifecycleAction)) errors.Add("lifecycleAction.invalid");
        if (request.OrganizationId is not null && request.LifecycleAction is null) errors.Add("lifecycleAction.required");
        if (request.ProviderAssignmentId is { } assignmentId && assignmentId == Guid.Empty) errors.Add("providerAssignment.invalid");
        if (request.OrganizationId is not null && request.ProviderAssignmentId is null) errors.Add("providerAssignment.required");
        if (!Enum.IsDefined(request.Action)) errors.Add("action.invalid");
        Required(request.TargetKey, "target");
        Required(request.IdempotencyKey, "idempotency");
        Required(request.PlanFingerprint, "planFingerprint");
        Required(request.TemplateFingerprint, "templateFingerprint");
        Required(request.ElsaVersion, "elsaVersion");
        Required(request.ReleaseLine, "releaseLine");
        Required(request.Topology, "topology");
        Required(request.Isolation, "isolation");
        Required(request.Location, "location");
        Required(request.ImageRepository, "imageRepository");
        Required(request.ImageDigest, "imageDigest");

        if (!IsFingerprint(request.PlanFingerprint)) errors.Add("planFingerprint.invalid");
        if (!IsFingerprint(request.TemplateFingerprint)) errors.Add("templateFingerprint.invalid");
        if (request.ProviderScopeFingerprint is not null && !IsFingerprint(request.ProviderScopeFingerprint)) errors.Add("providerScopeFingerprint.invalid");
        if (!IsDigest(request.ImageDigest)) errors.Add("imageDigest.invalid");
        if (request.ReleaseManifestDigest is not null && !IsDigest(request.ReleaseManifestDigest)) errors.Add("releaseManifestDigest.invalid");
        if (request.ReleaseManifestSignatureDigest is not null && !IsDigest(request.ReleaseManifestSignatureDigest)) errors.Add("releaseManifestSignatureDigest.invalid");
        if (request.ReleaseManifestReference is not null && !IsSafeImmutableEvidenceReference(request.ReleaseManifestReference, request.ReleaseManifestDigest)) errors.Add("releaseManifestReference.invalid");
        if (request.ReleaseManifestSignatureReference is not null && !IsSafeImmutableEvidenceReference(request.ReleaseManifestSignatureReference, request.ReleaseManifestSignatureDigest)) errors.Add("releaseManifestSignatureReference.invalid");
        if ((request.SqlWorkflowPackageVersion is null) != (request.SqlQuartzPackageVersion is null))
            errors.Add("packageVersions.incomplete");
        if (request.SqlWorkflowPackageVersion is not null && !IsSafePackageVersion(request.SqlWorkflowPackageVersion))
            errors.Add("sqlWorkflowPackageVersion.invalid");
        if (request.SqlQuartzPackageVersion is not null && !IsSafePackageVersion(request.SqlQuartzPackageVersion))
            errors.Add("sqlQuartzPackageVersion.invalid");
        if ((request.ReleaseManifestReference is null) != (request.ReleaseManifestSignatureReference is null)) errors.Add("releaseManifestReferences.incomplete");
        if (request.Capacity is not null && AzureContainerAppsCapacity.Map(request.Capacity) is null) errors.Add("capacity.invalid");
        if (request.ManagedHandoff && request.Capacity is not { MaxReplicas: 1 }) errors.Add("managedHandoff.replicasUnsupported");
        ValidateSecretReferences(request.SecretReferences, errors);

        BoundedSafe(request.TargetKey, 128, "target", errors);
        BoundedSafe(request.IdempotencyKey, 512, "idempotency", errors);
        BoundedSafe(request.ElsaVersion, 128, "elsaVersion", errors);
        BoundedSafe(request.ReleaseLine, 64, "releaseLine", errors);
        BoundedSafe(request.Topology, 64, "topology", errors);
        BoundedSafe(request.Isolation, 64, "isolation", errors);
        BoundedSafe(request.Location, 64, "location", errors);
        BoundedSafe(request.ImageRepository, 512, "imageRepository", errors);
        if (!IsSafeRepository(request.ImageRepository))
            errors.Add("imageRepository.mustBeRepository");

        return errors;

        void Required(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name}.required");
        }
    }

    public static string ComputeRequestHash(AzureProviderOperationRequest request)
    {
        var normalized = Normalize(request);
        var canonical = normalized.OrganizationId is not null
            ? SerializeBoundRequest(normalized)
            : normalized.ProviderScopeFingerprint is null
            ? normalized.SqlWorkflowPackageVersion is null && normalized.SqlQuartzPackageVersion is null
                ? SerializeLegacyRequest(normalized)
                : SerializeRequestWithPackageMetadata(normalized, includeProviderScope: false)
            : JsonSerializer.Serialize(new
            {
                normalized.WorkspaceId,
                normalized.TargetKey,
                Action = normalized.Action.ToString(),
                normalized.PlanFingerprint,
                normalized.TemplateFingerprint,
                normalized.ElsaVersion,
                normalized.ReleaseLine,
                normalized.Topology,
                normalized.Isolation,
                normalized.Location,
                normalized.ImageRepository,
                normalized.ImageDigest,
                normalized.ReleaseManifestDigest,
                normalized.ReleaseManifestSignatureDigest,
                normalized.ReleaseManifestReference,
                normalized.ReleaseManifestSignatureReference,
                normalized.SqlWorkflowPackageVersion,
                normalized.SqlQuartzPackageVersion,
                normalized.ProviderScopeFingerprint,
                secretReferences = normalized.SecretReferences
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            WithManagedHandoff(WithCapacity(canonical, normalized.Capacity), normalized.ManagedHandoff)))).ToLowerInvariant();
    }

    /// <summary>
    /// The managed handoff joined the persisted projection after operations were retained. None of
    /// those configured it, so the suffix is only present when the request does and they keep hashing
    /// to exactly the value they were stored with.
    /// </summary>
    private static string WithManagedHandoff(string canonical, bool managedHandoff) =>
        managedHandoff ? $"{canonical}|managed-handoff:v1" : canonical;

    /// <summary>
    /// Capacity joined the persisted projection after operations were already retained. Those
    /// rows carry none and must keep hashing to exactly the value they were stored with, so the
    /// capacity suffix is only present when the request has one.
    /// </summary>
    private static string WithCapacity(string canonical, AzureWorkloadCapacity? capacity) =>
        capacity is null
            ? canonical
            : $"{canonical}|capacity:{capacity.MinReplicas}/{capacity.MaxReplicas}/{capacity.CpuMillicores}/{capacity.MemoryMiB}";

    public static string ComputeOperationIdentity(AzureProviderOperationRequest request)
    {
        var normalized = Normalize(request);
        var legacyValue = string.Join('|', normalized.WorkspaceId.ToString("N"), normalized.TargetKey,
            normalized.Action, normalized.PlanFingerprint, normalized.TemplateFingerprint,
            normalized.ImageDigest, normalized.Location, normalized.Topology, normalized.Isolation);
        var value = normalized.ProviderScopeFingerprint is null
            ? legacyValue
            : $"{legacyValue}|{normalized.ProviderScopeFingerprint}";
        var withBinding = normalized.OrganizationId is null
            ? value
            : $"{value}|{normalized.OrganizationId:D}|{normalized.InstanceId:D}|{normalized.LifecycleAction}|{normalized.ProviderAssignmentId:D}";
        var withPackageMetadata = normalized.SqlWorkflowPackageVersion is null && normalized.SqlQuartzPackageVersion is null
            ? withBinding
            : $"{withBinding}|{normalized.SqlWorkflowPackageVersion}|{normalized.SqlQuartzPackageVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(withPackageMetadata))).ToLowerInvariant();
    }

    private static string SerializeRequestWithPackageMetadata(
        AzureProviderOperationRequest normalized,
        bool includeProviderScope) => JsonSerializer.Serialize(new
        {
            normalized.WorkspaceId,
            normalized.TargetKey,
            Action = normalized.Action.ToString(),
            normalized.PlanFingerprint,
            normalized.TemplateFingerprint,
            normalized.ElsaVersion,
            normalized.ReleaseLine,
            normalized.Topology,
            normalized.Isolation,
            normalized.Location,
            normalized.ImageRepository,
            normalized.ImageDigest,
            normalized.ReleaseManifestDigest,
            normalized.ReleaseManifestSignatureDigest,
            normalized.ReleaseManifestReference,
            normalized.ReleaseManifestSignatureReference,
            normalized.SqlWorkflowPackageVersion,
            normalized.SqlQuartzPackageVersion,
            ProviderScopeFingerprint = includeProviderScope ? normalized.ProviderScopeFingerprint : null,
            secretReferences = normalized.SecretReferences
        });

    private static string SerializeLegacyRequest(AzureProviderOperationRequest normalized) => JsonSerializer.Serialize(new
    {
        normalized.WorkspaceId,
        normalized.TargetKey,
        Action = normalized.Action.ToString(),
        normalized.PlanFingerprint,
        normalized.TemplateFingerprint,
        normalized.ElsaVersion,
        normalized.ReleaseLine,
        normalized.Topology,
        normalized.Isolation,
        normalized.Location,
        normalized.ImageRepository,
        normalized.ImageDigest,
        normalized.ReleaseManifestDigest,
        normalized.ReleaseManifestSignatureDigest,
        normalized.ReleaseManifestReference,
        normalized.ReleaseManifestSignatureReference,
        secretReferences = normalized.SecretReferences
    });

    private static string SerializeBoundRequest(AzureProviderOperationRequest normalized) => JsonSerializer.Serialize(new
    {
        normalized.WorkspaceId,
        normalized.OrganizationId,
        normalized.InstanceId,
        LifecycleAction = normalized.LifecycleAction?.ToString(),
        normalized.ProviderAssignmentId,
        normalized.TargetKey,
        Action = normalized.Action.ToString(),
        normalized.PlanFingerprint,
        normalized.TemplateFingerprint,
        normalized.ElsaVersion,
        normalized.ReleaseLine,
        normalized.Topology,
        normalized.Isolation,
        normalized.Location,
        normalized.ImageRepository,
        normalized.ImageDigest,
        normalized.ReleaseManifestDigest,
        normalized.ReleaseManifestSignatureDigest,
        normalized.ReleaseManifestReference,
        normalized.ReleaseManifestSignatureReference,
        normalized.SqlWorkflowPackageVersion,
        normalized.SqlQuartzPackageVersion,
        normalized.ProviderScopeFingerprint,
        secretReferences = normalized.SecretReferences
    });

    private static void BoundedSafe(string? value, int max, string name, ICollection<string> errors)
    {
        if (value is not null && value.Length > max) errors.Add($"{name}.tooLong");
        if (value is not null && value.Any(char.IsControl)) errors.Add($"{name}.unsafe");
    }

    private static bool IsFingerprint(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsDigest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && IsFingerprint(value[7..]);
    private static string NormalizeFingerprint(string value) => value.Trim().ToLowerInvariant();
    private static string NormalizeDigest(string value) => $"sha256:{value.Trim()[7..].ToLowerInvariant()}";
    private static string? NormalizeOptionalDigest(string? value) => value is null ? null : NormalizeDigest(value);
    private static string? NormalizeOptionalReference(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeOptionalSafe(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyDictionary<string, string> NormalizeSecretReferences(IReadOnlyDictionary<string, string>? values) =>
        new ReadOnlyDictionary<string, string>((values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key.Trim().ToLowerInvariant(), x => x.Value.Trim(), StringComparer.OrdinalIgnoreCase));

    private static void ValidateSecretReferences(IReadOnlyDictionary<string, string>? values, ICollection<string> errors)
    {
        if (values is null)
            return;
        if (values.Count > 64)
            errors.Add("secretReferences.tooMany");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Key.Any(char.IsControl) || !keys.Add(pair.Key.Trim()))
                errors.Add("secretReferences.key.invalid");
            else
            {
                try
                {
                    if (!mappedNames.Add(MapSecretName(pair.Key)))
                        errors.Add("secretReferences.nameCollision");
                }
                catch (ArgumentException)
                {
                    errors.Add("secretReferences.key.invalid");
                }
            }
            if (!IsSafeSecretReference(pair.Value) ||
                !IsSecretReferenceBoundToKey(pair.Key, pair.Value))
                errors.Add("secretReferences.value.invalid");
        }
    }

    public static bool IsSafeSecretReferences(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count > 64)
            return false;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Key.Any(char.IsControl) ||
                !string.Equals(pair.Key, pair.Key.Trim().ToLowerInvariant(), StringComparison.Ordinal) ||
                !keys.Add(pair.Key) || !IsSafeSecretReference(pair.Value) ||
                !IsSecretReferenceBoundToKey(pair.Key, pair.Value))
                return false;
            try
            {
                if (!mappedNames.Add(MapSecretName(pair.Key)))
                    return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Checks that an Azure Key Vault locator names the provider-governed secret for its
    /// logical slot. Opaque references remain valid for other providers; the managed-identity
    /// resolver and named-reference preflight require a strict Key Vault locator before this
    /// check is reached. The provider-owned SQL instruction is valid only for its fixed slot.
    /// </summary>
    internal static bool IsSecretReferenceBoundToKey(string key, string reference)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(reference))
            return false;

        if (AzureManagedSecretReferences.IsProviderOwned(key, reference))
            return true;
        if (AzureManagedSecretReferences.IsProviderOwnedReference(reference))
            return false;

        if (!AzureKeyVaultSecretLocator.TryParsePlanReference(reference, out var locator))
            return true;

        try
        {
            return locator is not null && string.Equals(locator.Name, MapSecretName(key), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsSafePackageVersion(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) &&
        NuGetVersion.TryParse(value, out _);

    internal static string MapSecretName(string key)
    {
        var normalized = key.Trim().ToLowerInvariant();
        var mapped = normalized switch
        {
            "database:connectionstring" or "database:connection-string" or "sql-connection" => "sql-connection",
            "identity:signingkey" or "identity:signing-key" or "identity-signing-key" => "identity-signing-key",
            "admin:password" or "admin-password" => "admin-password",
            _ => normalized.Replace(':', '-').Replace('_', '-')
        };
        if (mapped.Length is 0 or > 127 || mapped.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("The secret reference key cannot be mapped to a governed Azure secret name.", nameof(key));
        return mapped;
    }

    /// <summary>
    /// Secret references are opaque provider locators. They are never dereferenced by the
    /// control plane and intentionally reject URI features that could make a locator behave
    /// like a filesystem path or carry credentials.
    /// </summary>
    public static bool IsSafeSecretReference(string? value)
    {
        if (AzureKeyVaultSecretLocator.TryParsePlanReference(value, out _))
            return true;

        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.Contains('\\') || value.Contains('%') || value.Contains('?') || value.Contains('#') ||
            value.Contains("/../", StringComparison.Ordinal) || value.Contains("/./", StringComparison.Ordinal) ||
            value.EndsWith("/..", StringComparison.Ordinal) || value.EndsWith("/.", StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "secret" || !uri.IsDefaultPort ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal))
            return false;

        // A host-only locator (for example secret://database) is a valid opaque provider key.
        // A trailing slash, empty segment or dot segment is rejected because it makes the
        // locator ambiguous if a downstream provider ever maps it to a hierarchical key.
        if (uri.AbsolutePath.Length == 0 ||
            (uri.AbsolutePath == "/" && !value.EndsWith("/", StringComparison.Ordinal)))
            return true;

        if (!uri.AbsolutePath.StartsWith("/", StringComparison.Ordinal))
            return false;

        var segments = uri.AbsolutePath[1..].Split('/', StringSplitOptions.None);
        return segments.All(segment => !string.IsNullOrEmpty(segment) && segment is not "." and not "..");
    }

    private static bool ContainsSensitiveMarker(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("stack trace", StringComparison.OrdinalIgnoreCase);
}
