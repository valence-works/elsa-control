namespace ElsaControl.Deployment.Abstractions.Azure;

/// <summary>
/// The safe, value-free observation contract used to verify an Azure Lighthouse bind.
/// Implementations must observe the customer subscription through the managing tenant; they
/// must not infer delegated access from the managing-tenant role-assignment list.
/// </summary>
public interface IAzureLighthouseAuthorityObserver
{
    Task<AzureLighthouseAuthorityObservationResult> ObserveAsync(
        AzureLighthouseAuthorityObservationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AzureLighthouseAuthorityObservationRequest(
    string CustomerTenantId,
    string SubscriptionId,
    string ManagingTenantId,
    string ManagingPrincipalObjectId,
    string ManagingPrincipalClientId,
    string RegistrationDefinitionId,
    string? RegistrationDefinitionFingerprint);

/// <summary>Only stable verification classifications cross the observer boundary.</summary>
public sealed record AzureLighthouseAuthorityObservationResult(
    bool Succeeded,
    string Code,
    string Message,
    string? RegistrationDefinitionFingerprint = null);

/// <summary>Role definition IDs shared with the managed provider preflight policy.</summary>
public static class AzureProviderAuthorityRoleDefinitionIds
{
    public const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    public const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string UserAccessAdministrator = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9";
    public const string RbacAdministrator = "f58310d9-a9f6-439a-9e8d-f62e7b41a168";
    public const string KeyVaultSecretsUser = "4633458b-17de-408a-b874-0445c86b69e6";
}

/// <summary>Stable identity for the versioned v1 registration in the checked-in offer.</summary>
public static class AzureLighthouseOfferIdentity
{
    public const string Version = "v1";
    public const string ArtifactUrl = "https://github.com/valence-works/elsa-control/tree/main/infra/azure-lighthouse/v1";
    public const string ArtifactLabel = "Azure Lighthouse ARM artifact v1";
    public const string RegistrationDefinitionName = "9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9";
    public const string RegistrationAssignmentName = "50f0f9d1-8c11-47a3-8af5-9a87fa5c7af9";

    /// <summary>
    /// Mirrors the fixed registration name in the checked-in v1 Bicep artifact. The resulting
    /// path is always scoped to the requested subscription; managing tenant identity is a
    /// property of the observed definition, not an input that changes its resource ID.
    /// </summary>
    public static string RegistrationDefinitionId(string subscriptionId)
    {
        if (!Guid.TryParseExact(subscriptionId, "D", out var subscription) || subscription == Guid.Empty)
            throw new ArgumentException("The subscription ID must be a GUID.", nameof(subscriptionId));

        return $"/subscriptions/{subscription:D}/providers/Microsoft.ManagedServices/registrationDefinitions/{RegistrationDefinitionName}";
    }
}
