using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Proof;

/// <summary>
/// Projects one already-admitted resolved plan into the disposable Azure proof boundary.
/// The host supplies typed admission output; this factory never parses manifest payloads.
/// </summary>
public sealed class AdmittedAzureProofPlanFactory(
    ElsaInstancePlanResolutionResult admittedResolution,
    AzureWorkloadTarget target,
    string templateFingerprint,
    string providerScopeFingerprint,
    IReadOnlyList<string> admittedFeatures,
    Guid proofOrganizationId,
    Guid proofInstanceId,
    Guid providerAssignmentId,
    string idempotencyPrefix = "azure-proof") : IAzureProviderProofPlanFactory
{
    public AzureProviderOperationSubmission Create(
        DeploymentProofSelection selection,
        DeploymentProofEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.Equals(environment.Provider, "azure", StringComparison.OrdinalIgnoreCase) ||
            !AzureWorkloadPlanTranslator.IsSupportedLocation(environment.Region) ||
            !string.Equals(environment.Region, target.Location, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(environment.Name, target.WorkloadName, StringComparison.OrdinalIgnoreCase))
            throw PlanFailure("azure.proof.environmentMismatch");
        if (!IsSha256(templateFingerprint) || !IsSha256(providerScopeFingerprint) ||
            proofOrganizationId == Guid.Empty || proofInstanceId == Guid.Empty || providerAssignmentId == Guid.Empty ||
            admittedFeatures is null ||
            !selection.Features.Order(StringComparer.Ordinal).SequenceEqual(
                admittedFeatures.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(idempotencyPrefix) || idempotencyPrefix.Length > 64 ||
            idempotencyPrefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw PlanFailure("azure.proof.authorityInvalid");

        if (!admittedResolution.Succeeded || admittedResolution.Plan is null ||
            admittedResolution.Reference is null || admittedResolution.CurrentResolvedRelease is null ||
            admittedResolution.Findings.Any(finding => string.Equals(finding.Severity, "error", StringComparison.OrdinalIgnoreCase)))
            throw PlanFailure("azure.proof.admissionRequired");

        // Proof plans deploy production-registry images: no provider scope, so the translator governs the production
        // repository, and the runner rejects any proof target scope that names another registry.
        var translation = AzureWorkloadPlanTranslator.Translate(admittedResolution.Plan, target);
        if (translation.Plan is null || translation.Findings.Count != 0)
            throw PlanFailure("azure.proof.planRejected");

        var plan = translation.Plan;
        var expectedImage = $"{plan.ImageRepository}@sha256:{plan.ImageDigest}";
        if (!string.Equals(plan.ElsaVersion, selection.ElsaVersion, StringComparison.Ordinal) ||
            !string.Equals(plan.Topology, selection.Topology, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedImage, selection.ImageReference, StringComparison.Ordinal) ||
            !string.Equals($"sha256:{plan.ImageDigest}", selection.ImageDigest, StringComparison.OrdinalIgnoreCase))
            throw PlanFailure("azure.proof.planMismatch");

        var idempotencyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', selection.SelectionId, plan.Fingerprint, providerScopeFingerprint.ToLowerInvariant()))));
        return new AzureProviderOperationSubmission(
            $"{idempotencyPrefix}:{idempotencyHash}",
            templateFingerprint.ToLowerInvariant(),
            plan,
            providerScopeFingerprint.ToLowerInvariant(),
            proofOrganizationId,
            proofInstanceId,
            ElsaInstanceOperationAction.Reconcile,
            providerAssignmentId);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static DeploymentProofStageException PlanFailure(string code) =>
        new(DeploymentProofStage.Plan, code, "The admitted Azure proof plan could not be projected safely.");
}
