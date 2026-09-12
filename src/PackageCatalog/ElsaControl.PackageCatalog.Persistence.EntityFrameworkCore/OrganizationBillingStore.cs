using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Transactional adapter for the provider-neutral billing boundary. A single
/// serializable transaction claims the inbox identity, advances the
/// subscription, projects the entitlement snapshot and writes safe audit
/// metadata together.
/// </summary>
public sealed partial class OrganizationBillingStore(CatalogDbContext dbContext) : IOrganizationBillingStore, IOrganizationBillingLifecycleStore
{
    public async Task<BillingEventConsumptionResult> ConsumeAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        if (receivedAt == default)
            throw new ArgumentException("Event receipt timestamp is required.", nameof(receivedAt));
        providerEvent = NormalizeEvent(providerEvent);
        ValidateEvent(providerEvent, allowUnknown: false);

        return await ConsumeCoreAsync(providerEvent, receivedAt, cancellationToken, attempt: 0);
    }

    public async Task<BillingEventConsumptionResult> RecordUnknownAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        if (receivedAt == default)
            throw new ArgumentException("Event receipt timestamp is required.", nameof(receivedAt));
        providerEvent = NormalizeEvent(providerEvent);
        ValidateEvent(providerEvent, allowUnknown: true);

        return await RecordUnknownCoreAsync(providerEvent, receivedAt, cancellationToken, attempt: 0);
    }

    private async Task<BillingEventConsumptionResult> RecordUnknownCoreAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken,
        int attempt)
    {
        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await EnsureOrganizationExistsAsync(providerEvent.OrganizationId, cancellationToken);
            var existingEvent = await dbContext.BillingProviderEvents
                .SingleOrDefaultAsync(x => x.Provider == providerEvent.Provider && x.ProviderEventId == providerEvent.ProviderEventId, cancellationToken);
            if (existingEvent is not null)
            {
                EnsureSameEvent(existingEvent, providerEvent);
                var replaySubscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existingEvent.OrganizationId, cancellationToken);
                var replayEntitlement = await dbContext.OrganizationEntitlementSnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existingEvent.OrganizationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(BillingEventConsumptionOutcome.Replayed, replaySubscription, replayEntitlement, existingEvent);
            }

            var now = receivedAt.ToUniversalTime();
            var inbox = new BillingProviderEventInboxEntry
            {
                OrganizationId = providerEvent.OrganizationId,
                Provider = providerEvent.Provider,
                ProviderEventId = providerEvent.ProviderEventId,
                EventType = providerEvent.EventType,
                State = null,
                EventHash = providerEvent.EventHash,
                ProviderCustomerReference = providerEvent.ProviderCustomerReference,
                ProviderSubscriptionReference = providerEvent.ProviderSubscriptionReference,
                OccurredAt = providerEvent.OccurredAt.ToUniversalTime(),
                ReceivedAt = now,
                ProcessedAt = now,
                ProcessingStatus = BillingProviderEventProcessingStatus.RecordedUnknown,
                RejectionCode = "provider.event.unknown"
            };
            dbContext.BillingProviderEvents.Add(inbox);
            AddBillingAudit(providerEvent.OrganizationId, inbox.Id, "A correlated unsupported billing provider event was recorded.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var subscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == providerEvent.OrganizationId, cancellationToken);
            var entitlement = await dbContext.OrganizationEntitlementSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == providerEvent.OrganizationId, cancellationToken);
            return new(BillingEventConsumptionOutcome.RecordedUnknown, subscription, entitlement, inbox, inbox.RejectionCode);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            var existing = await dbContext.BillingProviderEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Provider == providerEvent.Provider && x.ProviderEventId == providerEvent.ProviderEventId, cancellationToken);
            if (existing is not null)
            {
                EnsureSameEvent(existing, providerEvent);
                var replaySubscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existing.OrganizationId, cancellationToken);
                var replayEntitlement = await dbContext.OrganizationEntitlementSnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existing.OrganizationId, cancellationToken);
                return new(BillingEventConsumptionOutcome.Replayed, replaySubscription, replayEntitlement, existing);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await RecordUnknownCoreAsync(providerEvent, receivedAt, cancellationToken, attempt + 1);
        }
    }

    private async Task<BillingEventConsumptionResult> ConsumeCoreAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken,
        int attempt)
    {
        try
        {
            return await ConsumeTransactionAsync(providerEvent, receivedAt, cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            // A concurrent consumer may have committed the same inbox key or
            // a subscription row. Re-read and retry the normalized event so
            // both same-key and different-key concurrent deliveries converge.
            var existing = await dbContext.BillingProviderEvents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Provider == providerEvent.Provider && x.ProviderEventId == providerEvent.ProviderEventId, cancellationToken);
            if (existing is not null)
            {
                EnsureSameEvent(existing, providerEvent);
                var replaySubscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existing.OrganizationId, cancellationToken);
                var replayEntitlement = await dbContext.OrganizationEntitlementSnapshots.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OrganizationId == existing.OrganizationId, cancellationToken);
                return new(BillingEventConsumptionOutcome.Replayed, replaySubscription, replayEntitlement, existing);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await ConsumeCoreAsync(providerEvent, receivedAt, cancellationToken, attempt + 1);
        }
    }

    private async Task<BillingEventConsumptionResult> ConsumeTransactionAsync(
        BillingProviderEvent providerEvent,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureOrganizationExistsAsync(providerEvent.OrganizationId, cancellationToken);
        var existingEvent = await dbContext.BillingProviderEvents
            .SingleOrDefaultAsync(x => x.Provider == providerEvent.Provider && x.ProviderEventId == providerEvent.ProviderEventId, cancellationToken);
        if (existingEvent is not null)
        {
            EnsureSameEvent(existingEvent, providerEvent);
            var replaySubscription = await dbContext.OrganizationSubscriptions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == existingEvent.OrganizationId, cancellationToken);
            var replayEntitlement = await dbContext.OrganizationEntitlementSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == existingEvent.OrganizationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BillingEventConsumptionOutcome.Replayed, replaySubscription, replayEntitlement, existingEvent);
        }

        var now = receivedAt.ToUniversalTime();
        var occurrence = providerEvent.OccurredAt.ToUniversalTime();
        var subscription = await dbContext.OrganizationSubscriptions
            .SingleOrDefaultAsync(x => x.OrganizationId == providerEvent.OrganizationId, cancellationToken);
        var existingSubscription = subscription;
        if (subscription is not null)
        {
            EnsureSameProvider(subscription, providerEvent.Provider);
            EnsureReferenceMatches(subscription.ProviderCustomerReference, providerEvent.ProviderCustomerReference, "customer");
            EnsureReferenceMatches(subscription.ProviderSubscriptionReference, providerEvent.ProviderSubscriptionReference, "subscription");
        }
        var inbox = new BillingProviderEventInboxEntry
        {
            OrganizationId = providerEvent.OrganizationId,
            Provider = providerEvent.Provider,
            ProviderEventId = providerEvent.ProviderEventId,
            EventType = providerEvent.EventType,
            State = providerEvent.State,
            EventHash = providerEvent.EventHash,
            ProviderCustomerReference = providerEvent.ProviderCustomerReference,
            ProviderSubscriptionReference = providerEvent.ProviderSubscriptionReference,
            OccurredAt = occurrence,
            ReceivedAt = now,
            ProcessingStatus = BillingProviderEventProcessingStatus.Accepted
        };
        dbContext.BillingProviderEvents.Add(inbox);

        // A Stripe trialing event must never create a late trial or move its
        // end date; checkout starts it first. Legacy/provider reconciliation
        // may still materialize a non-trial lifecycle row for an active event
        // so it can be audited and converged on the next checkout.
        if (subscription is null && providerEvent.State == OrganizationSubscriptionState.Trial)
        {
            inbox.ProcessingStatus = BillingProviderEventProcessingStatus.Rejected;
            inbox.RejectionCode = "subscription.missing";
            inbox.ProcessedAt = now;
            AddBillingAudit(providerEvent.OrganizationId, inbox.Id, "A normalized billing provider event was rejected because no control-plane subscription exists.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BillingEventConsumptionOutcome.Rejected, null, null, inbox, inbox.RejectionCode);
        }

        var isNewSubscription = subscription is null;
        subscription ??= OrganizationSubscriptionLifecycle.CreateTrial(
            providerEvent.OrganizationId,
            providerEvent.Provider,
            occurrence);

        var isOlderEvent = occurrence < subscription.LastProviderEventOccurredAt ||
                           (occurrence == subscription.LastProviderEventOccurredAt &&
                            subscription.LastProviderEventId is not null &&
                            string.CompareOrdinal(providerEvent.ProviderEventId, subscription.LastProviderEventId) < 0);
        if (isOlderEvent)
        {
            inbox.ProcessingStatus = BillingProviderEventProcessingStatus.IgnoredOutOfOrder;
            inbox.ProcessedAt = now;
            AddBillingAudit(providerEvent.OrganizationId, inbox.Id, "A normalized billing provider event was ignored as out of order.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BillingEventConsumptionOutcome.IgnoredOutOfOrder, subscription, null, inbox);
        }

        if (!OrganizationSubscriptionLifecycle.CanTransition(subscription.State, providerEvent.State!.Value))
        {
            inbox.ProcessingStatus = BillingProviderEventProcessingStatus.Rejected;
            inbox.RejectionCode = "subscription.transition.invalid";
            inbox.ProcessedAt = now;
            AddBillingAudit(providerEvent.OrganizationId, inbox.Id, "A normalized billing provider event was rejected.", now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(BillingEventConsumptionOutcome.Rejected, existingSubscription, null, inbox, inbox.RejectionCode);
        }

        OrganizationSubscriptionLifecycle.ApplyState(subscription, providerEvent.State.Value, occurrence);
        subscription.ProviderCustomerReference ??= providerEvent.ProviderCustomerReference;
        subscription.ProviderSubscriptionReference ??= providerEvent.ProviderSubscriptionReference;
        await BackfillQueuedCleanupReferencesAsync(subscription, cancellationToken);
        if (isNewSubscription)
            dbContext.OrganizationSubscriptions.Add(subscription);
        subscription.LastProviderEventOccurredAt = occurrence;
        subscription.LastProviderEventId = providerEvent.ProviderEventId;
        subscription.UpdatedAt = now;
        inbox.ProcessingStatus = BillingProviderEventProcessingStatus.Applied;
        inbox.ProcessedAt = now;

        var entitlement = await ProjectEntitlementAsync(subscription, now, cancellationToken);
        AddBillingAudit(providerEvent.OrganizationId, inbox.Id, "A normalized billing provider event was consumed.", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(BillingEventConsumptionOutcome.Applied, subscription, entitlement, inbox);
    }

    private async Task BackfillQueuedCleanupReferencesAsync(
        OrganizationSubscription subscription,
        CancellationToken cancellationToken)
    {
        var cleanup = await dbContext.OrganizationBillingCleanups
            .SingleOrDefaultAsync(
                x => x.OrganizationId == subscription.OrganizationId &&
                     x.SubscriptionId == subscription.Id &&
                     (x.State == OrganizationBillingCleanupState.Queued ||
                      x.State == OrganizationBillingCleanupState.InProgress),
                cancellationToken);
        if (cleanup is null)
            return;

        cleanup.ProviderCustomerReference ??= subscription.ProviderCustomerReference;
        cleanup.ProviderSubscriptionReference ??= subscription.ProviderSubscriptionReference;
    }

    public async Task<BillingEventConsumptionResult> StartTrialAsync(
        Guid organizationId,
        string provider,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        provider = provider.Trim();
        RequireSafeCode(provider, nameof(provider));
        RequireBillingProvider(provider, nameof(provider));
        var started = startedAt.ToUniversalTime();
        if (started == default)
            throw new ArgumentException("Trial start timestamp is required.", nameof(startedAt));

        return await StartTrialCoreAsync(organizationId, provider, started, cancellationToken, attempt: 0);
    }

    private async Task<BillingEventConsumptionResult> StartTrialCoreAsync(
        Guid organizationId,
        string provider,
        DateTimeOffset started,
        CancellationToken cancellationToken,
        int attempt)
    {
        try
        {
            return await StartTrialTransactionAsync(organizationId, provider, started, cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            var existing = await dbContext.OrganizationSubscriptions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
            if (existing is not null)
            {
                EnsureSameProvider(existing, provider);
                var entitlement = await CurrentEntitlementAsync(organizationId, cancellationToken);
                return new(BillingEventConsumptionOutcome.Replayed, existing, entitlement, null);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await StartTrialCoreAsync(organizationId, provider, started, cancellationToken, attempt + 1);
        }
    }

    private async Task<BillingEventConsumptionResult> StartTrialTransactionAsync(
        Guid organizationId,
        string provider,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await dbContext.OrganizationSubscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (existing is not null)
        {
            EnsureSameProvider(existing, provider);
            await transaction.CommitAsync(cancellationToken);
            return new(BillingEventConsumptionOutcome.Replayed, existing, await CurrentEntitlementAsync(organizationId, cancellationToken), null);
        }

        var subscription = OrganizationSubscriptionLifecycle.CreateTrial(organizationId, provider, started);
        dbContext.OrganizationSubscriptions.Add(subscription);
        var entitlement = await ProjectEntitlementAsync(subscription, started, cancellationToken);
        dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
        {
            OrganizationId = organizationId,
            Action = OrganizationAuditAction.SubscriptionChanged,
            TargetType = "subscription",
            TargetId = subscription.Id.ToString("D"),
            Summary = "Organization subscription trial started.",
            CreatedAt = started
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(BillingEventConsumptionOutcome.Applied, subscription, entitlement, null);
    }

    public Task<OrganizationSubscription?> GetSubscriptionAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);

    private async Task<OrganizationEntitlementSnapshot> ProjectEntitlementAsync(
        OrganizationSubscription subscription,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entitlement = await dbContext.OrganizationEntitlementSnapshots
            .SingleOrDefaultAsync(x => x.OrganizationId == subscription.OrganizationId, cancellationToken);
        if (entitlement is null)
        {
            entitlement = new OrganizationEntitlementSnapshot
            {
                OrganizationId = subscription.OrganizationId,
                CreatedAt = now
            };
            dbContext.OrganizationEntitlementSnapshots.Add(entitlement);
        }

        // Billing lifecycle is orthogonal to the existing product capability
        // policy. Preserve every capability and limit field while recording
        // the latest provider-neutral lifecycle projection.
        entitlement.SubscriptionState = subscription.State;
        entitlement.SubscriptionId = subscription.Id;
        entitlement.SyncedAt = now;
        entitlement.UpdatedAt = now;
        return entitlement;
    }

    private Task<OrganizationEntitlementSnapshot?> CurrentEntitlementAsync(Guid organizationId, CancellationToken cancellationToken) =>
        dbContext.OrganizationEntitlementSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);

    private void AddBillingAudit(Guid organizationId, Guid eventId, string summary, DateTimeOffset createdAt) =>
        dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
        {
            OrganizationId = organizationId,
            Action = OrganizationAuditAction.BillingEventConsumed,
            TargetType = "billing-event",
            TargetId = eventId.ToString("D"),
            Summary = summary,
            CreatedAt = createdAt
        });

    private static void EnsureSameProvider(OrganizationSubscription subscription, string provider)
    {
        if (!string.Equals(subscription.Provider, provider, StringComparison.Ordinal))
            throw new BillingProviderEventConflictException("A subscription cannot change billing provider.");
    }

    private static void EnsureReferenceMatches(string? existing, string? incoming, string referenceName)
    {
        if (existing is not null && incoming is not null && !string.Equals(existing, incoming, StringComparison.Ordinal))
            throw new BillingProviderEventConflictException($"A subscription cannot change its provider {referenceName} reference.");
    }

    private async Task EnsureOrganizationExistsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
            throw new ArgumentException("Billing event organization does not exist.", nameof(organizationId));
    }

    private static void ValidateEvent(BillingProviderEvent providerEvent, bool allowUnknown)
    {
        if (providerEvent.OrganizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(providerEvent));
        RequireSafeCode(providerEvent.Provider, nameof(providerEvent.Provider));
        RequireBillingProvider(providerEvent.Provider, nameof(providerEvent.Provider));
        RequireSafeToken(providerEvent.ProviderEventId, nameof(providerEvent.ProviderEventId));
        RequireSafeCode(providerEvent.EventType, nameof(providerEvent.EventType));
        RequireSha256(providerEvent.EventHash, nameof(providerEvent.EventHash));
        RequireSafeReference(providerEvent.ProviderCustomerReference, nameof(providerEvent.ProviderCustomerReference));
        RequireSafeReference(providerEvent.ProviderSubscriptionReference, nameof(providerEvent.ProviderSubscriptionReference));
        if (providerEvent.OccurredAt == default)
            throw new ArgumentException("Billing event occurrence timestamp is required.", nameof(providerEvent));

        if (allowUnknown)
        {
            if (providerEvent.State.HasValue)
                throw new ArgumentException("Unknown billing events must not contain a lifecycle state.", nameof(providerEvent));
        }
        else if (!providerEvent.State.HasValue)
        {
            throw new ArgumentException("Known billing events require a lifecycle state.", nameof(providerEvent));
        }
        else if (!Enum.IsDefined(providerEvent.State.Value))
        {
            throw new ArgumentException("Billing event lifecycle state is invalid.", nameof(providerEvent));
        }
    }

    private static BillingProviderEvent NormalizeEvent(BillingProviderEvent providerEvent)
    {
        var hash = providerEvent.EventHash?.Trim() ?? "";
        if (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            hash = "sha256:" + hash;
        else if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && hash.Length == 71)
            hash = "sha256:" + hash[7..];

        return providerEvent with
        {
            Provider = providerEvent.Provider?.Trim() ?? "",
            ProviderEventId = providerEvent.ProviderEventId?.Trim() ?? "",
            EventType = providerEvent.EventType?.Trim() ?? "",
            EventHash = hash.ToLowerInvariant(),
            ProviderCustomerReference = string.IsNullOrWhiteSpace(providerEvent.ProviderCustomerReference) ? null : providerEvent.ProviderCustomerReference.Trim(),
            ProviderSubscriptionReference = string.IsNullOrWhiteSpace(providerEvent.ProviderSubscriptionReference) ? null : providerEvent.ProviderSubscriptionReference.Trim(),
            OccurredAt = providerEvent.OccurredAt.ToUniversalTime()
        };
    }

    private static void EnsureSameEvent(BillingProviderEventInboxEntry existing, BillingProviderEvent incoming)
    {
        if (existing.OrganizationId != incoming.OrganizationId ||
            !string.Equals(existing.EventHash, incoming.EventHash, StringComparison.OrdinalIgnoreCase) ||
            existing.State != incoming.State ||
            !string.Equals(existing.EventType, incoming.EventType, StringComparison.Ordinal) ||
            existing.OccurredAt != incoming.OccurredAt.ToUniversalTime() ||
            !string.Equals(existing.ProviderCustomerReference, incoming.ProviderCustomerReference, StringComparison.Ordinal) ||
            !string.Equals(existing.ProviderSubscriptionReference, incoming.ProviderSubscriptionReference, StringComparison.Ordinal))
            throw new BillingProviderEventConflictException("A provider event ID was previously received with different normalized facts.");
    }

    private static bool IsRetryableConflict(Exception exception)
    {
        if (exception is DbUpdateException update && EfCoreDatabaseExceptionPolicy.IsUniqueViolation(update))
            return true;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
            if (current is SqlException { Number: 1205 or 3960 })
                return true;
        }

        return false;
    }

    private static void RequireSafeCode(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':')))
            throw new ArgumentException($"{name} must be a stable safe code.", name);
    }

    // The internal provider is written only by the operator grant path.
    private static void RequireBillingProvider(string provider, string name)
    {
        if (string.Equals(provider, BillingProviderNames.Internal, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{name} is reserved for operator-granted internal entitlements.", name);
    }

    private static void RequireSafeToken(string? value, string name)
    {
        RequireSafeReference(value, name);
        if (value!.Length > 256 || value.Contains('/', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal) || value.Contains('+', StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a safe token.", name);
    }

    private static void RequireSafeReference(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (value.Length > OrganizationBillingLimits.ProviderReferenceMaxLength || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '/' or '+')))
            throw new ArgumentException($"{name} must be a safe reference.", name);
    }

    private static void RequireSha256(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || value[7..].Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException($"{name} must be a SHA-256 digest.", name);
    }
}
