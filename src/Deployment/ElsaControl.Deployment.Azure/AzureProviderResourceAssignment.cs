namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Provider-owned lifecycle for the durable Azure placement assigned to one Elsa Instance.
/// The control-plane instance retains only this assignment's opaque identifier.
/// </summary>
public enum AzureProviderAssignmentState
{
    Reserved,
    Provisioning,
    Active,
    Deleting,
    Unknown,
    Deleted
}

/// <summary>
/// Durable provider placement and ownership authority. Provider resource identities never cross
/// into the provider-neutral Elsa Instance contract.
/// </summary>
public sealed record AzureProviderResourceAssignment(
    Guid Id,
    Guid WorkspaceId,
    Guid OrganizationId,
    Guid InstanceId,
    string ProviderScopeFingerprint,
    int NamingVersion,
    string SubscriptionId,
    string ResourceGroupName,
    string WorkloadName,
    string OwnershipKey,
    string Location,
    AzureProviderAssignmentState State,
    AzureProviderResourceReferences Resources,
    Guid? LastOperationId,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null);

public sealed record AzureProviderResourceAssignmentRequest(
    Guid WorkspaceId,
    Guid OrganizationId,
    Guid InstanceId,
    string ProviderScopeFingerprint,
    string SubscriptionId,
    string ResourceGroupNamePrefix,
    string WorkloadName,
    string Location,
    int NamingVersion = AzureProviderResourceAssignmentNaming.CurrentVersion,
    AzureProviderAssignmentRebindContext? Rebind = null);

/// <summary>
/// Host-owned current scope used to rebind an existing live assignment when only
/// the template or tool fingerprint rotated and placement is unchanged.
/// </summary>
public sealed record AzureProviderAssignmentScopeAuthority(
    Guid WorkspaceId,
    Guid InstanceId,
    string ProviderScopeFingerprint,
    string SubscriptionId,
    string ResourceGroupNamePrefix,
    int NamingVersion = AzureProviderResourceAssignmentNaming.CurrentVersion,
    AzureProviderAssignmentRebindContext? Rebind = null);

/// <summary>
/// Who or what requested a placement-stable provider-scope rebind. Only safe
/// identifiers cross this boundary; it is not a credential or secret.
/// </summary>
public sealed record AzureProviderAssignmentRebindContext(
    string TriggeredBy,
    Guid? TriggerOperationId = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TriggeredBy) || TriggeredBy.Length > 64 ||
            TriggeredBy.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("The Azure assignment rebind trigger is invalid.", nameof(TriggeredBy));
        if (TriggerOperationId == Guid.Empty)
            throw new ArgumentException("The Azure assignment rebind trigger operation is invalid.", nameof(TriggerOperationId));
    }
}

/// <summary>
/// Append-only audit of a governed assignment rebind. Records the old and new
/// provider-scope fingerprints and the trigger that authorized the rotation.
/// </summary>
public sealed record AzureProviderAssignmentRebindRecord(
    Guid Id,
    Guid AssignmentId,
    Guid WorkspaceId,
    Guid InstanceId,
    string FromProviderScopeFingerprint,
    string ToProviderScopeFingerprint,
    string TriggeredBy,
    Guid? TriggerOperationId,
    DateTimeOffset OccurredAt);

public static class AzureProviderAssignmentRebindDiagnostics
{
    public const string OperationsInFlight = "assignment.rebind.operations-inflight";
    public const string PlacementMismatch = "assignment.rebind.placement-mismatch";
    public const string Ambiguous = "assignment.rebind.ambiguous";
}

/// <summary>
/// Refuses a provider-scope rebind when placement changed, more than one live
/// assignment exists, or an in-flight operation must drain first.
/// </summary>
public sealed class AzureProviderAssignmentRebindException : InvalidOperationException
{
    public AzureProviderAssignmentRebindException(string diagnosticCode, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(diagnosticCode) || diagnosticCode.Length > 128 ||
            diagnosticCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            throw new ArgumentException("The Azure assignment rebind diagnostic is invalid.", nameof(diagnosticCode));
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

public interface IAzureProviderResourceAssignmentStore
{
    Task<AzureProviderResourceAssignment> CreateOrGetAsync(
        AzureProviderResourceAssignmentRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<AzureProviderResourceAssignment?> GetAsync(
        Guid workspaceId,
        Guid assignmentId,
        CancellationToken cancellationToken = default);

    Task<AzureProviderResourceAssignment?> RebindToCurrentScopeAsync(
        AzureProviderAssignmentScopeAuthority authority,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Azure provider assignment rebind is not supported by this store.");

    Task<bool> HasRebindLineageAsync(
        Guid workspaceId,
        Guid assignmentId,
        string fromProviderScopeFingerprint,
        string toProviderScopeFingerprint,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            !string.IsNullOrWhiteSpace(fromProviderScopeFingerprint) &&
            string.Equals(
                fromProviderScopeFingerprint.Trim(),
                toProviderScopeFingerprint?.Trim(),
                StringComparison.OrdinalIgnoreCase));

    Task<IReadOnlyList<AzureProviderAssignmentRebindRecord>> ListRebindsAsync(
        Guid workspaceId,
        Guid assignmentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AzureProviderAssignmentRebindRecord>>([]);
}

public static class AzureProviderResourceAssignmentNaming
{
    /// <summary>Legacy proof hosts bind the exact caller-supplied disposable group.</summary>
    public const int ExplicitDisposableGroup = 0;

    public const int CurrentVersion = 1;

    public static string ResourceGroupName(string prefix, Guid instanceId, int namingVersion = CurrentVersion)
    {
        if (namingVersion == ExplicitDisposableGroup && instanceId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(prefix) && prefix.Length <= 90 &&
            prefix.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '(' or ')' or '-'))
            return prefix;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 50 ||
            prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The Azure resource-group prefix is unsafe.", nameof(prefix));
        if (instanceId == Guid.Empty || namingVersion != CurrentVersion)
            throw new ArgumentException("The Azure assignment naming authority is invalid.", nameof(instanceId));

        return $"{prefix.TrimEnd('-')}-{instanceId:N}";
    }

    public static string OwnershipKey(Guid assignmentId, Guid instanceId, string providerScopeFingerprint)
    {
        if (assignmentId == Guid.Empty || instanceId == Guid.Empty ||
            providerScopeFingerprint is not { Length: 64 } || !providerScopeFingerprint.All(char.IsAsciiHexDigit))
            throw new ArgumentException("The Azure assignment ownership identity is invalid.");

        var value = $"{assignmentId:D}:{instanceId:D}:{providerScopeFingerprint.ToLowerInvariant()}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
