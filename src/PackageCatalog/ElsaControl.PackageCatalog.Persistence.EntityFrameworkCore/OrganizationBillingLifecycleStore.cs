using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Durable clock and cleanup implementation for the provider-neutral billing
/// lifecycle. The caller supplies one UTC instant so tests and scheduled runs
/// observe the same decision boundary.
/// </summary>
public sealed partial class OrganizationBillingStore
{
    private const int LifecycleBatchSize = 100;
    private static readonly TimeSpan CleanupLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<OrganizationBillingLifecycleAdvance>> AdvanceDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        now = RequireUtc(now, nameof(now));
        var graceCutoff = now.Subtract(OrganizationSubscriptionLifecycle.PaymentGracePeriod);
        var constraintCutoff = now.Subtract(OrganizationSubscriptionLifecycle.ConstraintPeriod);
        var retentionCutoff = now.Subtract(OrganizationSubscriptionLifecycle.FinalRetentionPeriod);
        var candidates = await dbContext.OrganizationSubscriptions
            .AsNoTracking()
            .Where(x =>
                (x.State == OrganizationSubscriptionState.Trial && x.TrialEndsAt <= now) ||
                (x.State == OrganizationSubscriptionState.PastDue &&
                 ((x.GraceEndsAt != null && x.GraceEndsAt <= now) ||
                  (x.GraceEndsAt == null && x.PastDueAt != null && x.PastDueAt <= graceCutoff))) ||
                (x.State == OrganizationSubscriptionState.Constrained &&
                 x.ConstrainedAt != null && x.ConstrainedAt <= constraintCutoff) ||
                (x.State == OrganizationSubscriptionState.Suspended &&
                 ((x.RetentionEndsAt != null && x.RetentionEndsAt <= now) ||
                  (x.RetentionEndsAt == null && x.SuspendedAt != null && x.SuspendedAt <= retentionCutoff))) ||
                x.State == OrganizationSubscriptionState.Retained)
            .OrderBy(x => x.OrganizationId)
            .Take(LifecycleBatchSize)
            .Select(x => new
            {
                x.OrganizationId,
                x.Id,
                x.State,
                x.TrialEndsAt,
                x.PastDueAt,
                x.GraceEndsAt,
                x.ConstrainedAt,
                x.SuspendedAt,
                x.RetentionEndsAt
            })
            .ToListAsync(cancellationToken);

        var results = new List<OrganizationBillingLifecycleAdvance>();
        foreach (var candidate in candidates)
        {
            if (!IsDue(candidate.State, candidate.TrialEndsAt, candidate.PastDueAt, candidate.GraceEndsAt, candidate.ConstrainedAt, candidate.SuspendedAt, candidate.RetentionEndsAt, now) &&
                candidate.State != OrganizationSubscriptionState.Retained)
                continue;

            while (true)
            {
                var result = await AdvanceOneAsync(candidate.OrganizationId, candidate.Id, now, cancellationToken);
                if (result is null)
                    break;
                results.Add(result);
                if (result.CurrentState is OrganizationSubscriptionState.Retained or OrganizationSubscriptionState.Deleted)
                    break;
            }
        }

        return results;
    }

    public async Task<OrganizationBillingLifecycleAdvance?> RequestDeletionAsync(
        Guid organizationId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken = default)
    {
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        return await RequestDeletionCoreAsync(organizationId, RequireUtc(requestedAt, nameof(requestedAt)), cancellationToken, 0);
    }

    private async Task<OrganizationBillingLifecycleAdvance?> AdvanceOneAsync(
        Guid organizationId,
        Guid subscriptionId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await AdvanceOneCoreAsync(organizationId, subscriptionId, now, cancellationToken, attempt: 0);

    private async Task<OrganizationBillingLifecycleAdvance?> AdvanceOneCoreAsync(
        Guid organizationId,
        Guid subscriptionId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int attempt)
    {
        try
        {
            return await dbContext.ExecuteInTransactionAsync<OrganizationBillingLifecycleAdvance?>(IsolationLevel.Serializable, async () =>
            {
                var subscription = await dbContext.OrganizationSubscriptions
                    .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == subscriptionId, cancellationToken);
                if (subscription is null || subscription.State == OrganizationSubscriptionState.Deleted)
                {
                    return null;
                }

                var previous = subscription.State;
                var transitionAt = now;
                var changed = false;
                switch (subscription.State)
                {
                    case OrganizationSubscriptionState.Trial when subscription.TrialEndsAt <= now:
                        transitionAt = subscription.TrialEndsAt.ToUniversalTime();
                        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.PastDue, transitionAt, advanceLifecycleVersion: true);
                        changed = true;
                        break;
                    case OrganizationSubscriptionState.PastDue:
                    {
                        var graceEndsAt = subscription.GraceEndsAt ?? subscription.PastDueAt?.ToUniversalTime().Add(OrganizationSubscriptionLifecycle.PaymentGracePeriod);
                        if (graceEndsAt is null || graceEndsAt > now)
                            break;
                        transitionAt = graceEndsAt.Value;
                        subscription.GraceEndsAt ??= transitionAt;
                        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Constrained, transitionAt, advanceLifecycleVersion: true);
                        changed = true;
                        break;
                    }
                    case OrganizationSubscriptionState.Constrained:
                    {
                        var suspensionAt = subscription.ConstrainedAt?.ToUniversalTime().Add(OrganizationSubscriptionLifecycle.ConstraintPeriod);
                        if (suspensionAt is null || suspensionAt > now)
                            break;
                        transitionAt = suspensionAt.Value;
                        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Suspended, transitionAt, advanceLifecycleVersion: true);
                        changed = true;
                        break;
                    }
                    case OrganizationSubscriptionState.Suspended:
                    {
                        var retentionEndsAt = subscription.RetentionEndsAt ?? subscription.SuspendedAt?.ToUniversalTime().Add(OrganizationSubscriptionLifecycle.FinalRetentionPeriod);
                        if (retentionEndsAt is null || retentionEndsAt > now)
                            break;
                        transitionAt = retentionEndsAt.Value;
                        subscription.RetentionEndsAt ??= transitionAt;
                        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Retained, transitionAt, advanceLifecycleVersion: true);
                        changed = true;
                        break;
                    }
                }

                var noticeCreated = false;
                var cleanupQueued = false;
                if (changed)
                {
                    subscription.UpdatedAt = now;
                    await ProjectEntitlementAsync(subscription, now, cancellationToken);
                    if (NoticeFor(subscription.State) is { } noticeKind)
                    {
                        noticeCreated |= await AddNoticeAsync(subscription, noticeKind, now, cancellationToken);
                    }
                    if (subscription.State == OrganizationSubscriptionState.Suspended)
                        noticeCreated |= await AddNoticeAsync(subscription, OrganizationBillingLifecycleNoticeKind.ExportAvailable, now, cancellationToken);
                    AddLifecycleAudit(
                        subscription,
                        OrganizationAuditAction.SubscriptionChanged,
                        $"Organization subscription lifecycle advanced from {previous} to {subscription.State}.",
                        now);
                }

                if (subscription.State == OrganizationSubscriptionState.Retained)
                {
                    noticeCreated |= await AddNoticeAsync(subscription, OrganizationBillingLifecycleNoticeKind.DeletionScheduled, now, cancellationToken);
                    cleanupQueued = await EnsureCleanupAsync(subscription, now, early: false, cancellationToken);
                }

                if (!changed && !noticeCreated && !cleanupQueued)
                {
                    return null;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return new OrganizationBillingLifecycleAdvance(organizationId, subscription.Id, previous, subscription.State, transitionAt, noticeCreated, cleanupQueued);
            }, cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableLifecycleConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await AdvanceOneCoreAsync(organizationId, subscriptionId, now, cancellationToken, attempt + 1);
        }
    }

    private async Task<OrganizationBillingLifecycleAdvance?> RequestDeletionCoreAsync(
        Guid organizationId,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken,
        int attempt)
    {
        try
        {
            return await dbContext.ExecuteInTransactionAsync<OrganizationBillingLifecycleAdvance?>(IsolationLevel.Serializable, async () =>
            {
                var subscription = await dbContext.OrganizationSubscriptions
                    .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
                if (subscription is null)
                {
                    return null;
                }

                var previous = subscription.State;
                var changed = false;
                if (subscription.State == OrganizationSubscriptionState.Deleted)
                {
                    return new(organizationId, subscription.Id, previous, subscription.State, subscription.DeletedAt ?? requestedAt, false, false);
                }

                subscription.EarlyDeletionRequestedAt ??= requestedAt;
                if (subscription.State is not OrganizationSubscriptionState.Suspended and
                    not OrganizationSubscriptionState.Retained)
                {
                    OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Suspended, requestedAt, advanceLifecycleVersion: true);
                    changed = true;
                }

                subscription.UpdatedAt = requestedAt;
                if (changed)
                    await ProjectEntitlementAsync(subscription, requestedAt, cancellationToken);
                var noticeCreated = NoticeFor(subscription.State) is { } noticeKind && await AddNoticeAsync(subscription, noticeKind, requestedAt, cancellationToken);
                if (subscription.State == OrganizationSubscriptionState.Suspended)
                    noticeCreated |= await AddNoticeAsync(subscription, OrganizationBillingLifecycleNoticeKind.ExportAvailable, requestedAt, cancellationToken);
                noticeCreated |= await AddNoticeAsync(subscription, OrganizationBillingLifecycleNoticeKind.DeletionScheduled, requestedAt, cancellationToken);
                if (changed)
                    AddLifecycleAudit(subscription, OrganizationAuditAction.SubscriptionChanged, $"Customer requested early deletion; subscription advanced from {previous} to Suspended.", requestedAt);
                var cleanupQueued = await EnsureCleanupAsync(subscription, requestedAt, early: true, cancellationToken);
                if (cleanupQueued || changed)
                    AddLifecycleAudit(subscription, OrganizationAuditAction.BillingCleanupRequested, "Provider-neutral billing cleanup was queued.", requestedAt);
                await dbContext.SaveChangesAsync(cancellationToken);
                return new OrganizationBillingLifecycleAdvance(organizationId, subscription.Id, previous, subscription.State, requestedAt, noticeCreated, cleanupQueued);
            }, cancellationToken);
        }
        catch (Exception exception) when (attempt < 2 && IsRetryableLifecycleConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            return await RequestDeletionCoreAsync(organizationId, requestedAt, cancellationToken, attempt + 1);
        }
    }

    public async Task<OrganizationBillingCleanupWorkItem?> TryClaimCleanupAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        workerId = workerId.Trim();
        now = RequireUtc(now, nameof(now));

        return await dbContext.ExecuteInTransactionAsync<OrganizationBillingCleanupWorkItem?>(IsolationLevel.Serializable, async () =>
        {
            var cleanup = await dbContext.OrganizationBillingCleanups
                .Where(x => (x.State == OrganizationBillingCleanupState.Queued && x.NotBeforeAt <= now) ||
                            (x.State == OrganizationBillingCleanupState.InProgress && x.LeaseExpiresAt <= now))
                .OrderBy(x => x.NotBeforeAt)
                .ThenBy(x => x.OrganizationId)
                .FirstOrDefaultAsync(cancellationToken);
            if (cleanup is null)
            {
                return null;
            }

            var leaseToken = Guid.NewGuid().ToString("N");
            cleanup.State = OrganizationBillingCleanupState.InProgress;
            cleanup.LeaseOwner = workerId;
            cleanup.LeaseToken = leaseToken;
            cleanup.LeaseExpiresAt = now.Add(CleanupLeaseDuration);
            cleanup.LastAttemptAt = now;
            cleanup.AttemptCount++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new OrganizationBillingCleanupWorkItem(
                cleanup.Id,
                cleanup.OrganizationId,
                cleanup.SubscriptionId,
                cleanup.CleanupKey,
                cleanup.Provider,
                cleanup.ProviderCustomerReference,
                cleanup.ProviderSubscriptionReference,
                cleanup.AttemptCount,
                leaseToken);
        }, cancellationToken);
    }

    public async Task<OrganizationBillingCleanupResult> CompleteCleanupAsync(
        OrganizationBillingCleanupCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var completedAt = RequireUtc(completion.CompletedAt, nameof(completion.CompletedAt));
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var cleanup = await dbContext.OrganizationBillingCleanups
                .SingleOrDefaultAsync(x => x.Id == completion.CleanupId &&
                                           x.OrganizationId == completion.OrganizationId &&
                                           x.SubscriptionId == completion.SubscriptionId, cancellationToken);
            if (cleanup is null)
                throw new ArgumentException("Billing cleanup does not exist.", nameof(completion));

            if (cleanup.State != OrganizationBillingCleanupState.InProgress ||
                !string.Equals(cleanup.LeaseToken, completion.LeaseToken, StringComparison.Ordinal))
            {
                return new(cleanup.State, false, cleanup.LastFailureCode);
            }

            cleanup.LeaseOwner = null;
            cleanup.LeaseToken = null;
            cleanup.LeaseExpiresAt = null;
            var deleted = false;
            switch (completion.Outcome)
            {
                case OrganizationBillingCleanupOutcome.ConfirmedAbsent:
                {
                    cleanup.State = OrganizationBillingCleanupState.Confirmed;
                    cleanup.CompletedAt = completedAt;
                    cleanup.LastFailureCode = null;
                    cleanup.ProviderCustomerReference = null;
                    cleanup.ProviderSubscriptionReference = null;
                    var subscription = await dbContext.OrganizationSubscriptions
                        .SingleOrDefaultAsync(x => x.OrganizationId == completion.OrganizationId && x.Id == completion.SubscriptionId, cancellationToken);
                    if (subscription is not null && subscription.State != OrganizationSubscriptionState.Deleted)
                    {
                        OrganizationSubscriptionLifecycle.ApplyState(subscription, OrganizationSubscriptionState.Deleted, completedAt, advanceLifecycleVersion: true);
                        subscription.ProviderCustomerReference = null;
                        subscription.ProviderSubscriptionReference = null;
                        subscription.LastProviderEventId = null;
                        subscription.UpdatedAt = completedAt;
                        await ProjectEntitlementAsync(subscription, completedAt, cancellationToken);
                        AddLifecycleAudit(subscription, OrganizationAuditAction.BillingCleanupCompleted, "Provider-neutral billing cleanup was confirmed and the subscription was tombstoned.", completedAt);
                        deleted = true;
                    }

                    break;
                }
                case OrganizationBillingCleanupOutcome.RetryableFailure:
                case OrganizationBillingCleanupOutcome.Unknown:
                    cleanup.State = OrganizationBillingCleanupState.Queued;
                    cleanup.NotBeforeAt = completedAt.Add(CleanupRetryDelay);
                    cleanup.CompletedAt = null;
                    cleanup.LastFailureCode = SafeFailureCode(completion.FailureCode);
                    break;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return new OrganizationBillingCleanupResult(cleanup.State, deleted, cleanup.LastFailureCode);
        }, cancellationToken);
    }

    private static bool IsDue(
        OrganizationSubscriptionState state,
        DateTimeOffset trialEndsAt,
        DateTimeOffset? pastDueAt,
        DateTimeOffset? graceEndsAt,
        DateTimeOffset? constrainedAt,
        DateTimeOffset? suspendedAt,
        DateTimeOffset? retentionEndsAt,
        DateTimeOffset now) => state switch
        {
            OrganizationSubscriptionState.Trial => trialEndsAt <= now,
            OrganizationSubscriptionState.PastDue => (graceEndsAt ?? pastDueAt?.Add(OrganizationSubscriptionLifecycle.PaymentGracePeriod)) is { } graceDeadline && graceDeadline <= now,
            OrganizationSubscriptionState.Constrained => constrainedAt?.Add(OrganizationSubscriptionLifecycle.ConstraintPeriod) is { } suspensionDeadline && suspensionDeadline <= now,
            OrganizationSubscriptionState.Suspended => (retentionEndsAt ?? suspendedAt?.Add(OrganizationSubscriptionLifecycle.FinalRetentionPeriod)) is { } retentionDeadline && retentionDeadline <= now,
            _ => false
        };

    private async Task<bool> AddNoticeAsync(
        OrganizationSubscription subscription,
        OrganizationBillingLifecycleNoticeKind kind,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (dbContext.OrganizationBillingLifecycleNotices.Local.Any(x => x.OrganizationId == subscription.OrganizationId && x.SubscriptionId == subscription.Id && x.Kind == kind) ||
            await dbContext.OrganizationBillingLifecycleNotices.AnyAsync(x => x.OrganizationId == subscription.OrganizationId && x.SubscriptionId == subscription.Id && x.Kind == kind, cancellationToken))
            return false;

        var notice = new OrganizationBillingLifecycleNotice
        {
            OrganizationId = subscription.OrganizationId,
            SubscriptionId = subscription.Id,
            Kind = kind,
            State = subscription.State,
            CreatedAt = now
        };
        dbContext.OrganizationBillingLifecycleNotices.Add(notice);
        AddLifecycleAudit(subscription, OrganizationAuditAction.BillingLifecycleNoticeIssued, $"Billing lifecycle notice {kind} was recorded.", now, notice.Id.ToString("D"));
        return true;
    }

    private async Task<bool> EnsureCleanupAsync(OrganizationSubscription subscription, DateTimeOffset now, bool early, CancellationToken cancellationToken)
    {
        var cleanup = dbContext.OrganizationBillingCleanups.Local.FirstOrDefault(x => x.SubscriptionId == subscription.Id) ??
            await dbContext.OrganizationBillingCleanups.FirstOrDefaultAsync(x => x.SubscriptionId == subscription.Id, cancellationToken);
        if (cleanup is null)
        {
            cleanup = new OrganizationBillingCleanup
            {
                OrganizationId = subscription.OrganizationId,
                SubscriptionId = subscription.Id,
                CleanupKey = $"billing-cleanup-{subscription.Id:N}",
                Provider = subscription.Provider,
                ProviderCustomerReference = subscription.ProviderCustomerReference,
                ProviderSubscriptionReference = subscription.ProviderSubscriptionReference,
                RequestedAt = now,
                NotBeforeAt = early ? now : subscription.RetentionEndsAt ?? now
            };
            dbContext.OrganizationBillingCleanups.Add(cleanup);
            return true;
        }

        var changed = false;
        if (early && cleanup.NotBeforeAt > now)
        {
            cleanup.NotBeforeAt = now;
            changed = true;
        }
        return changed;
    }

    private void AddLifecycleAudit(
        OrganizationSubscription subscription,
        OrganizationAuditAction action,
        string summary,
        DateTimeOffset createdAt,
        string? targetId = null) =>
        dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
        {
            OrganizationId = subscription.OrganizationId,
            Action = action,
            TargetType = action == OrganizationAuditAction.BillingLifecycleNoticeIssued ? "billing-notice" : "subscription",
            TargetId = targetId ?? subscription.Id.ToString("D"),
            Summary = summary,
            CreatedAt = createdAt
        });

    private static OrganizationBillingLifecycleNoticeKind? NoticeFor(OrganizationSubscriptionState state) => state switch
    {
        OrganizationSubscriptionState.PastDue => OrganizationBillingLifecycleNoticeKind.GraceStarted,
        OrganizationSubscriptionState.Constrained => OrganizationBillingLifecycleNoticeKind.ConstraintStarted,
        OrganizationSubscriptionState.Suspended => OrganizationBillingLifecycleNoticeKind.SuspensionStarted,
        _ => null
    };

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default)
            throw new ArgumentException("A UTC timestamp is required.", name);
        return value.ToUniversalTime();
    }

    private static bool IsRetryableLifecycleConflict(Exception exception) =>
        exception is DbUpdateException update && EfCoreDatabaseExceptionPolicy.IsUniqueViolation(update) ||
        exception is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception is Microsoft.Data.SqlClient.SqlException { Number: 1205 or 3960 };

    private static string? SafeFailureCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "cleanup.unknown";
        value = value.Trim();
        return value.Length <= 128 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':')
            ? value
            : "cleanup.failed";
    }
}
