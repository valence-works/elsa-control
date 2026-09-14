namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Bounds for a guided design-partner entitlement backed by an active Azure
/// subscription bind. Azure infrastructure charges remain separate from Elsa fees.
/// </summary>
public static class OrganizationAzureBoundEntitlementPolicy
{
    public const int DefaultInstanceCap = 1;
    public const string ReasonField = OrganizationInternalEntitlementPolicy.ReasonField;
    public const string MaxInstancesField = OrganizationInternalEntitlementPolicy.MaxInstancesField;
    public const string ExpiresAtField = OrganizationInternalEntitlementPolicy.ExpiresAtField;

    public static IReadOnlyDictionary<string, string[]> Validate(
        string? reason,
        int? maxInstances,
        string? expiresAt,
        DateTimeOffset now,
        out OrganizationAzureBoundEntitlementTerms? terms)
    {
        var effectiveCap = maxInstances ?? DefaultInstanceCap;
        var expiryWasOmitted = string.IsNullOrWhiteSpace(expiresAt);
        var validationExpiry = expiryWasOmitted
            ? now.ToUniversalTime().Add(OrganizationInternalEntitlementPolicy.MaxGrantDuration)
                .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'")
            : expiresAt;
        var errors = OrganizationInternalEntitlementPolicy.Validate(
            reason,
            effectiveCap,
            validationExpiry,
            now,
            out var validated);
        terms = errors.Count == 0
            ? new(validated!.Reason, validated.MaxInstances, expiryWasOmitted ? null : validated.ExpiresAt)
            : null;
        return errors;
    }

    /// <summary>Persistence-boundary guard; callers must never bypass the bounds.</summary>
    public static void EnsureValid(OrganizationAzureBoundEntitlementTerms terms, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terms);
        OrganizationInternalEntitlementPolicy.EnsureValid(
            new OrganizationInternalEntitlementTerms(
                terms.Reason,
                terms.MaxInstances,
                terms.ExpiresAt ?? now.ToUniversalTime().Add(OrganizationInternalEntitlementPolicy.MaxGrantDuration)),
            now);
    }
}

public sealed record OrganizationAzureBoundEntitlementTerms(
    string Reason,
    int MaxInstances,
    DateTimeOffset? ExpiresAt);

public sealed record OrganizationAzureBoundEntitlementMint(
    Guid OrganizationId,
    OrganizationAzureBoundEntitlementTerms Terms,
    string? OperatorSubject);

public enum OrganizationAzureBoundEntitlementState
{
    None,
    Active,
    Expired,
    Revoked,
    Closed,
    CommercialSubscription
}

/// <summary>Value-free projection: no bind locator, reason, or operator identity.</summary>
public sealed record OrganizationAzureBoundEntitlementStatus(
    Guid OrganizationId,
    OrganizationAzureBoundEntitlementState State,
    int? MaxInstances,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UpdatedAt);

public enum OrganizationAzureBoundEntitlementOutcome
{
    Current,
    Minted,
    Revoked,
    Unchanged,
    Invalid,
    OrganizationNotFound,
    NotMinted,
    BindingRequired,
    CommercialSubscriptionExists,
    AlreadyMinted
}

public sealed record OrganizationAzureBoundEntitlementResult(
    OrganizationAzureBoundEntitlementOutcome Outcome,
    OrganizationAzureBoundEntitlementStatus? Status = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public interface IOrganizationAzureBoundEntitlementStore
{
    Task<OrganizationAzureBoundEntitlementResult> GetAzureBoundEntitlementAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationAzureBoundEntitlementResult> MintAzureBoundEntitlementAsync(
        OrganizationAzureBoundEntitlementMint mint,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationAzureBoundEntitlementResult> RevokeAzureBoundEntitlementAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizationAzureBoundEntitlementService(
    IOrganizationAzureBoundEntitlementStore store,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<OrganizationAzureBoundEntitlementResult> GetAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        store.GetAzureBoundEntitlementAsync(organizationId, Now, cancellationToken);

    public async Task<OrganizationAzureBoundEntitlementResult> MintAsync(
        Guid organizationId,
        string? reason,
        int? maxInstances,
        string? expiresAt,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        var now = Now;
        var errors = OrganizationAzureBoundEntitlementPolicy.Validate(
            reason,
            maxInstances,
            expiresAt,
            now,
            out var terms);
        return terms is null
            ? new(OrganizationAzureBoundEntitlementOutcome.Invalid, Errors: errors)
            : await store.MintAzureBoundEntitlementAsync(
                new OrganizationAzureBoundEntitlementMint(organizationId, terms, operatorSubject),
                now,
                cancellationToken);
    }

    public Task<OrganizationAzureBoundEntitlementResult> RevokeAsync(
        Guid organizationId,
        string? operatorSubject,
        CancellationToken cancellationToken = default) =>
        store.RevokeAzureBoundEntitlementAsync(organizationId, operatorSubject, Now, cancellationToken);

    private DateTimeOffset Now => _timeProvider.GetUtcNow().ToUniversalTime();
}
