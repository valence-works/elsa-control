namespace ElsaControl.PackageCatalog.Core.Accounts;

/// <summary>
/// Shared bounds for provider-neutral billing references. The limit keeps
/// provider and reference pairs within SQL Server's composite index key size.
/// </summary>
public static class OrganizationBillingLimits
{
    public const int ProviderReferenceMaxLength = 256;
}

/// <summary>
/// Provider-neutral lifecycle owned by Elsa Control at the organization
/// boundary. Provider-specific plans and price names deliberately do not
/// appear in this model.
/// </summary>
public sealed class OrganizationSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string Provider { get; set; } = "";
    public string? ProviderCustomerReference { get; set; }
    public string? ProviderSubscriptionReference { get; set; }
    public OrganizationSubscriptionState State { get; set; } = OrganizationSubscriptionState.Trial;
    public DateTimeOffset TrialStartedAt { get; set; }
    public DateTimeOffset TrialEndsAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? PastDueAt { get; set; }
    /// <summary>UTC instant at which the failed-payment grace period ends.</summary>
    public DateTimeOffset? GraceEndsAt { get; set; }
    public DateTimeOffset? ConstrainedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    /// <summary>UTC instant at which the final retention period ends.</summary>
    public DateTimeOffset? RetentionEndsAt { get; set; }
    public DateTimeOffset? RetainedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>Set when the customer explicitly requests deletion before retention expires.</summary>
    public DateTimeOffset? EarlyDeletionRequestedAt { get; set; }
    /// <summary>Advances only for a control-plane lifecycle transition.</summary>
    public int LifecycleVersion { get; set; }
    public DateTimeOffset LastProviderEventOccurredAt { get; set; }
    public string? LastProviderEventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The commercial state vocabulary is intentionally independent of a payment
/// provider's product or price names. Trialing is retained as an alias for
/// integrations that use the conventional payment vocabulary.
/// </summary>
public enum OrganizationSubscriptionState
{
    Trial = 0,
    Trialing = Trial,
    Active = 1,
    PastDue = 2,
    Constrained = 3,
    Suspended = 4,
    Retained = 5,
    Deleted = 6
}

/// <summary>
/// Safe, already-normalized facts from a billing provider. Raw webhook data,
/// credentials and tokens never cross this boundary.
/// </summary>
public sealed record BillingProviderEvent(
    Guid OrganizationId,
    string Provider,
    string ProviderEventId,
    string EventType,
    OrganizationSubscriptionState? State,
    DateTimeOffset OccurredAt,
    string EventHash,
    string? ProviderCustomerReference = null,
    string? ProviderSubscriptionReference = null);

public enum BillingProviderEventProcessingStatus
{
    Accepted,
    Applied,
    IgnoredOutOfOrder,
    Rejected,
    RecordedUnknown
}

public enum OrganizationBillingLifecycleNoticeKind
{
    GraceStarted,
    ConstraintStarted,
    SuspensionStarted,
    ExportAvailable,
    DeletionScheduled
}

public enum OrganizationBillingNoticeDeliveryStatus
{
    Pending,
    Delivered,
    Failed
}

/// <summary>
/// Durable, safe notification intent. The unique organization/subscription/kind
/// identity makes scheduler retries idempotent while delivery remains a separate concern.
/// </summary>
public sealed class OrganizationBillingLifecycleNotice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid SubscriptionId { get; set; }
    public OrganizationSubscription? Subscription { get; set; }
    public OrganizationBillingLifecycleNoticeKind Kind { get; set; }
    public OrganizationSubscriptionState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public OrganizationBillingNoticeDeliveryStatus DeliveryStatus { get; set; } = OrganizationBillingNoticeDeliveryStatus.Pending;
    public DateTimeOffset? DeliveredAt { get; set; }
    public int DeliveryAttemptCount { get; set; }
    public string? LastFailureCode { get; set; }
}

public enum OrganizationBillingCleanupState
{
    Queued,
    InProgress,
    Confirmed,
    Failed
}

/// <summary>
/// Provider-neutral cleanup intent. References are opaque provider locators,
/// never credentials or raw customer data.
/// </summary>
public sealed class OrganizationBillingCleanup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid SubscriptionId { get; set; }
    public OrganizationSubscription? Subscription { get; set; }
    public string CleanupKey { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ProviderCustomerReference { get; set; }
    public string? ProviderSubscriptionReference { get; set; }
    public OrganizationBillingCleanupState State { get; set; } = OrganizationBillingCleanupState.Queued;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset NotBeforeAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? LastFailureCode { get; set; }
}

public enum OrganizationBillingCleanupOutcome
{
    ConfirmedAbsent,
    RetryableFailure,
    Unknown
}

public sealed record OrganizationBillingLifecycleAdvance(
    Guid OrganizationId,
    Guid SubscriptionId,
    OrganizationSubscriptionState PreviousState,
    OrganizationSubscriptionState CurrentState,
    DateTimeOffset TransitionedAt,
    bool NoticeCreated,
    bool CleanupQueued);

public sealed record OrganizationBillingCleanupWorkItem(
    Guid Id,
    Guid OrganizationId,
    Guid SubscriptionId,
    string CleanupKey,
    string Provider,
    string? ProviderCustomerReference,
    string? ProviderSubscriptionReference,
    int AttemptCount,
    string LeaseToken);

public sealed record OrganizationBillingCleanupCompletion(
    Guid CleanupId,
    Guid OrganizationId,
    Guid SubscriptionId,
    string LeaseToken,
    OrganizationBillingCleanupOutcome Outcome,
    DateTimeOffset CompletedAt,
    string? FailureCode = null);

public sealed record OrganizationBillingCleanupResult(
    OrganizationBillingCleanupState State,
    bool SubscriptionDeleted,
    string? FailureCode = null);

public sealed record OrganizationBillingCleanupRequest(
    Guid OrganizationId,
    Guid SubscriptionId,
    string CleanupKey,
    string Provider,
    string? ProviderCustomerReference,
    string? ProviderSubscriptionReference,
    int AttemptCount);

/// <summary>Provider-neutral port used by the durable cleanup worker.</summary>
public interface IOrganizationBillingLifecycleStore
{
    Task<IReadOnlyList<OrganizationBillingLifecycleAdvance>> AdvanceDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationBillingLifecycleAdvance?> RequestDeletionAsync(
        Guid organizationId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken = default);

    Task<OrganizationBillingCleanupWorkItem?> TryClaimCleanupAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<OrganizationBillingCleanupResult> CompleteCleanupAsync(
        OrganizationBillingCleanupCompletion completion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A billing provider implements only its own remote cleanup semantics. The
/// lifecycle and retention policy never depend on this interface's provider type.
/// </summary>
public interface IOrganizationBillingCleanupProvider
{
    string Provider { get; }

    Task<OrganizationBillingCleanupOutcome> CleanupAsync(
        OrganizationBillingCleanupRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationBillingLifecycleBatchResult(
    IReadOnlyList<OrganizationBillingLifecycleAdvance> Advances,
    int CleanupAttempts);

/// <summary>
/// Durable inbox entry. It contains only normalized, bounded metadata.
/// </summary>
public sealed class BillingProviderEventInboxEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public OrganizationSubscriptionState? State { get; set; }
    public string EventHash { get; set; } = "";
    public string? ProviderCustomerReference { get; set; }
    public string? ProviderSubscriptionReference { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public BillingProviderEventProcessingStatus ProcessingStatus { get; set; }
    public string? RejectionCode { get; set; }
}

public enum BillingEventConsumptionOutcome
{
    Applied,
    Replayed,
    IgnoredOutOfOrder,
    Rejected,
    RecordedUnknown
}

public sealed record BillingEventConsumptionResult(
    BillingEventConsumptionOutcome Outcome,
    OrganizationSubscription? Subscription,
    OrganizationEntitlementSnapshot? Entitlement,
    BillingProviderEventInboxEntry? Event,
    string? RejectionCode = null);

public sealed class BillingProviderEventConflictException(string message) : InvalidOperationException(message);

public interface IOrganizationBillingStore
{
    Task<BillingEventConsumptionResult> ConsumeAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);

    Task<BillingEventConsumptionResult> RecordUnknownAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);

    Task<BillingEventConsumptionResult> StartTrialAsync(
        Guid organizationId,
        string provider,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<OrganizationSubscription?> GetSubscriptionAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

public static class OrganizationSubscriptionLifecycle
{
    public static readonly TimeSpan TrialDuration = TimeSpan.FromDays(14);
    public static readonly TimeSpan PaymentGracePeriod = TimeSpan.FromDays(7);
    public static readonly TimeSpan ConstraintPeriod = TimeSpan.FromDays(1);
    public static readonly TimeSpan FinalRetentionPeriod = TimeSpan.FromDays(30);

    public static bool CanTransition(OrganizationSubscriptionState current, OrganizationSubscriptionState next) =>
        current == next || (current, next) switch
        {
            (OrganizationSubscriptionState.Trial, OrganizationSubscriptionState.Active or OrganizationSubscriptionState.PastDue or OrganizationSubscriptionState.Suspended) => true,
            (OrganizationSubscriptionState.Active, OrganizationSubscriptionState.PastDue or OrganizationSubscriptionState.Constrained or OrganizationSubscriptionState.Suspended) => true,
            (OrganizationSubscriptionState.PastDue, OrganizationSubscriptionState.Active or OrganizationSubscriptionState.Constrained or OrganizationSubscriptionState.Suspended) => true,
            (OrganizationSubscriptionState.Constrained, OrganizationSubscriptionState.Active or OrganizationSubscriptionState.Suspended) => true,
            (OrganizationSubscriptionState.Suspended, OrganizationSubscriptionState.Retained) => true,
            (OrganizationSubscriptionState.Retained, OrganizationSubscriptionState.Deleted) => true,
            _ => false
        };

    public static bool CanTransition(OrganizationSubscription subscription, OrganizationSubscriptionState next) =>
        CanTransition(subscription.State, next) ||
        subscription.State == OrganizationSubscriptionState.Suspended &&
        next == OrganizationSubscriptionState.Deleted &&
        subscription.EarlyDeletionRequestedAt is not null ||
        subscription.State == OrganizationSubscriptionState.Active &&
        next == OrganizationSubscriptionState.Deleted &&
        subscription.EarlyDeletionRequestedAt is not null &&
        string.Equals(subscription.Provider, BillingProviderNames.AzureBound, StringComparison.Ordinal);

    public static OrganizationSubscription CreateTrial(Guid organizationId, string provider, DateTimeOffset startedAt)
    {
        var started = startedAt.ToUniversalTime();
        return new OrganizationSubscription
        {
            OrganizationId = organizationId,
            Provider = provider,
            State = OrganizationSubscriptionState.Trial,
            TrialStartedAt = started,
            TrialEndsAt = started.Add(TrialDuration),
            LastProviderEventOccurredAt = started,
            CreatedAt = started,
            UpdatedAt = started
        };
    }

    public static void ApplyState(OrganizationSubscription subscription, OrganizationSubscriptionState next, DateTimeOffset occurredAt, bool advanceLifecycleVersion = false)
    {
        if (!CanTransition(subscription, next))
            throw new InvalidOperationException($"Subscription cannot transition from {subscription.State} to {next}.");

        var stateChanged = subscription.State != next;
        var timestamp = occurredAt.ToUniversalTime();
        subscription.State = next;
        switch (next)
        {
            case OrganizationSubscriptionState.Active:
                subscription.ActivatedAt ??= timestamp;
                break;
            case OrganizationSubscriptionState.PastDue:
                subscription.PastDueAt ??= timestamp;
                subscription.GraceEndsAt ??= subscription.PastDueAt.Value.Add(PaymentGracePeriod);
                break;
            case OrganizationSubscriptionState.Constrained:
                subscription.ConstrainedAt ??= timestamp;
                break;
            case OrganizationSubscriptionState.Suspended:
                subscription.SuspendedAt ??= timestamp;
                subscription.RetentionEndsAt ??= subscription.SuspendedAt.Value.Add(FinalRetentionPeriod);
                break;
            case OrganizationSubscriptionState.Retained:
                subscription.RetainedAt ??= timestamp;
                break;
            case OrganizationSubscriptionState.Deleted:
                subscription.DeletedAt ??= timestamp;
                break;
        }

        if (advanceLifecycleVersion && stateChanged)
            subscription.LifecycleVersion++;
    }
}
