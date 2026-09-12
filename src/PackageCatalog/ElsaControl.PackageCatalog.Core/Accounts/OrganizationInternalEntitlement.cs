using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Bounds of the operator-granted internal managed-hosting entitlement used by
/// internal dogfood organizations. A grant is recorded as a subscription of the
/// reserved <see cref="BillingProviderNames.Internal"/> provider, so it is always
/// distinguishable from, and never replaces, a billing provider's state.
/// </summary>
public static class OrganizationInternalEntitlementPolicy
{
    public const int ReasonMinLength = 3;
    public const int ReasonMaxLength = 200;
    public const int MinInstanceCap = 1;
    public const int MaxInstanceCap = 3;
    public const string ReasonField = "reason";
    public const string MaxInstancesField = "maxInstances";
    public const string ExpiresAtField = "expiresAt";
    public static readonly TimeSpan MaxGrantDuration = TimeSpan.FromDays(90);

    private static readonly string[] UtcTimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    /// <summary>
    /// Validates operator input against <paramref name="now"/>. Error keys are the
    /// public request field names and messages are fixed text that never echoes
    /// the submitted values. <paramref name="terms"/> is set only when valid.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(
        string? reason,
        int? maxInstances,
        string? expiresAt,
        DateTimeOffset now,
        out OrganizationInternalEntitlementTerms? terms)
    {
        var parsed = TryParseUtcTimestamp(expiresAt, out var expiry);
        var errors = Collect(reason, maxInstances, parsed ? expiry : null, now);
        if (!parsed && !string.IsNullOrWhiteSpace(expiresAt))
            errors[ExpiresAtField] = ["ExpiresAt must be an ISO-8601 UTC timestamp."];
        terms = errors.Count == 0 ? new(reason!.Trim(), maxInstances!.Value, expiry) : null;
        return errors;
    }

    /// <summary>Persistence-boundary guard; callers must never bypass the bounds.</summary>
    public static void EnsureValid(OrganizationInternalEntitlementTerms terms, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var errors = Collect(terms.Reason, terms.MaxInstances, terms.ExpiresAt, now);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors.Values.SelectMany(x => x)), nameof(terms));
    }

    /// <summary>
    /// Accepts only an ISO-8601 timestamp that explicitly denotes UTC. A value
    /// without an offset is rejected rather than interpreted as server-local time.
    /// </summary>
    public static bool TryParseUtcTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return !string.IsNullOrWhiteSpace(value) &&
               DateTimeOffset.TryParseExact(
                   value.Trim(),
                   UtcTimestampFormats,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out timestamp) &&
               timestamp.Offset == TimeSpan.Zero;
    }

    /// <summary>
    /// Operator subjects are recorded as a one-way fingerprint, matching the
    /// managed-instance audit convention. Verify an operator with
    /// <c>printf '%s' "&lt;subject&gt;" | shasum -a 256</c>.
    /// </summary>
    public static string? FingerprintOperatorSubject(string? subject) =>
        string.IsNullOrWhiteSpace(subject)
            ? null
            : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Trim())));

    private static Dictionary<string, string[]> Collect(
        string? reason,
        int? maxInstances,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var trimmedReason = reason?.Trim();
        if (string.IsNullOrEmpty(trimmedReason))
            errors[ReasonField] = ["Reason is required."];
        else if (trimmedReason.Length is < ReasonMinLength or > ReasonMaxLength)
            errors[ReasonField] = [$"Reason must be between {ReasonMinLength} and {ReasonMaxLength} characters."];
        else if (trimmedReason.Any(IsDisallowedReasonCharacter))
            errors[ReasonField] = ["Reason must not contain control or formatting characters."];

        if (maxInstances is null)
            errors[MaxInstancesField] = ["MaxInstances is required."];
        else if (maxInstances is < MinInstanceCap or > MaxInstanceCap)
            errors[MaxInstancesField] = [$"MaxInstances must be between {MinInstanceCap} and {MaxInstanceCap}."];

        var utcNow = now.ToUniversalTime();
        if (expiresAt is null)
            errors[ExpiresAtField] = ["ExpiresAt is required."];
        else if (expiresAt.Value <= utcNow)
            errors[ExpiresAtField] = ["ExpiresAt must be in the future."];
        else if (expiresAt.Value > utcNow.Add(MaxGrantDuration))
            errors[ExpiresAtField] = [$"ExpiresAt must be at most {MaxGrantDuration.TotalDays:0} days ahead."];

        return errors;
    }

    // Bidirectional overrides, zero-width and line/paragraph separators can
    // disguise audit text as effectively as C0/C1 control characters.
    private static bool IsDisallowedReasonCharacter(char character) =>
        char.IsControl(character) ||
        char.GetUnicodeCategory(character) is UnicodeCategory.Format or
            UnicodeCategory.LineSeparator or
            UnicodeCategory.ParagraphSeparator;
}

public sealed record OrganizationInternalEntitlementTerms(string Reason, int MaxInstances, DateTimeOffset ExpiresAt);

public sealed record OrganizationInternalEntitlementGrant(
    Guid OrganizationId,
    OrganizationInternalEntitlementTerms Terms,
    string? OperatorSubject);

public enum OrganizationInternalEntitlementState
{
    /// <summary>No subscription exists; an internal grant can be created.</summary>
    None,
    /// <summary>Managed hosting is granted and has not reached its expiry.</summary>
    Active,
    /// <summary>The grant reached its expiry; create/update is denied.</summary>
    Expired,
    /// <summary>An operator revoked the grant; create/update is denied.</summary>
    Revoked,
    /// <summary>The internal subscription left Active (customer deletion); it cannot be re-granted.</summary>
    Closed,
    /// <summary>A billing provider owns the commercial state; internal grants are refused.</summary>
    CommercialSubscription
}

/// <summary>Value-free projection: no reason text, provider references or operator identity.</summary>
public sealed record OrganizationInternalEntitlementStatus(
    Guid OrganizationId,
    OrganizationInternalEntitlementState State,
    int? MaxInstances,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UpdatedAt);

public enum OrganizationInternalEntitlementOutcome
{
    Current,
    Granted,
    Regranted,
    Revoked,
    Unchanged,
    Invalid,
    OrganizationNotFound,
    NotGranted,
    CommercialSubscriptionExists,
    SubscriptionClosed
}

public sealed record OrganizationInternalEntitlementResult(
    OrganizationInternalEntitlementOutcome Outcome,
    OrganizationInternalEntitlementStatus? Status = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// Transactional port for internal grants. Implementations check provider
/// ownership inside the same serializable transaction that writes, so a
/// provider-owned subscription is never modified.
/// </summary>
public interface IOrganizationInternalEntitlementStore
{
    Task<OrganizationInternalEntitlementResult> GetInternalEntitlementAsync(
        Guid organizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationInternalEntitlementResult> GrantInternalEntitlementAsync(
        OrganizationInternalEntitlementGrant grant,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationInternalEntitlementResult> RevokeInternalEntitlementAsync(
        Guid organizationId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Operator application boundary. The injected clock supplies the single
/// instant used for validation and for the persisted decision.
/// </summary>
public sealed class OrganizationInternalEntitlementService(
    IOrganizationInternalEntitlementStore store,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<OrganizationInternalEntitlementResult> GetAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default) =>
        store.GetInternalEntitlementAsync(organizationId, Now, cancellationToken);

    public async Task<OrganizationInternalEntitlementResult> GrantAsync(
        Guid organizationId,
        string? reason,
        int? maxInstances,
        string? expiresAt,
        string? operatorSubject,
        CancellationToken cancellationToken = default)
    {
        var now = Now;
        var errors = OrganizationInternalEntitlementPolicy.Validate(reason, maxInstances, expiresAt, now, out var terms);
        return terms is null
            ? new(OrganizationInternalEntitlementOutcome.Invalid, Errors: errors)
            : await store.GrantInternalEntitlementAsync(new(organizationId, terms, operatorSubject), now, cancellationToken);
    }

    public Task<OrganizationInternalEntitlementResult> RevokeAsync(
        Guid organizationId,
        string? operatorSubject,
        CancellationToken cancellationToken = default) =>
        store.RevokeInternalEntitlementAsync(organizationId, operatorSubject, Now, cancellationToken);

    private DateTimeOffset Now => _timeProvider.GetUtcNow().ToUniversalTime();
}
