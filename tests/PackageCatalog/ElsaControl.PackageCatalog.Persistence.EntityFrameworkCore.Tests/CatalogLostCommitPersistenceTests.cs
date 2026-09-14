using System.Data.Common;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Approvals;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CatalogLostCommitPersistenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Updating_external_identity_seen_does_not_overwrite_a_newer_login_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid identityId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var account = new Account { DisplayName = "Original", Email = "original@example.com" };
            var identity = new ExternalIdentity { Issuer = "issuer", Subject = "subject", Account = account };
            setup.ExternalIdentities.Add(identity);
            await setup.SaveChangesAsync();
            identityId = identity.Id;
        }

        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            await new AccountWorkspaceStore(followUp).UpdateExternalIdentitySeenAsync(
                identityId, "Newer", "newer@example.com", cancellationToken);
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
            await new AccountWorkspaceStore(db).UpdateExternalIdentitySeenAsync(identityId, "Original attempt", "attempt@example.com");

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.ExternalIdentities.CountAsync());
        Assert.Equal(1, acknowledgement.Committed);
        Assert.Equal(1, await verify.Accounts.CountAsync(x => x.DisplayName == "Newer" && x.Email == "newer@example.com"));
        Assert.Equal(1, await verify.ExternalIdentities.CountAsync(x => x.DisplayName == "Newer" && x.Email == "newer@example.com"));
    }

    [Fact]
    public async Task Creating_an_organization_workspace_returns_the_original_summary_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        var creatorId = Guid.NewGuid();
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            setup.Accounts.Add(new Account { Id = creatorId, DisplayName = "Creator" });
            setup.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organization.Id,
                MaxWorkspaces = 5,
                MaxSources = 5,
                SyncedAt = Start,
                CreatedAt = Start,
                UpdatedAt = Start
            });
            await setup.SaveChangesAsync();
            organizationId = organization.Id;
        }

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new AccountWorkspaceStore(db).CreateOrganizationWorkspaceAsync(
                organizationId,
                creatorId,
                new CreateOrganizationWorkspaceRequest("Shared", []));
            Assert.True(result.Succeeded);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.Workspaces.CountAsync(x => x.OrganizationId == organizationId));
        Assert.Equal(1, await verify.WorkspaceMemberships.CountAsync());
    }

    [Fact]
    public async Task Adding_a_workspace_source_returns_created_without_a_duplicate_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid workspaceId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            var workspace = new Workspace { Organization = organization, Name = "Shared" };
            setup.Workspaces.Add(workspace);
            await setup.SaveChangesAsync();
            workspaceId = workspace.Id;
        }

        var source = new PackageSource
        {
            OwnerWorkspaceId = workspaceId,
            Visibility = PackageSourceVisibility.Workspace,
            Name = "Feed",
            Url = "https://packages.example/feed"
        };
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new AccountWorkspaceStore(db).TryAddWorkspaceSourceAsync(source, 5);
            Assert.Equal(WorkspaceSourceAddStatus.Created, result.Status);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.PackageSources.CountAsync(x => x.Id == source.Id));
    }

    [Fact]
    public async Task Reconciling_interrupted_runs_returns_the_original_count_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid runId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var run = new SyncRun { Status = SyncRunStatus.Running, StartedAt = Start.AddHours(-1) };
            setup.SyncRuns.Add(run);
            await setup.SaveChangesAsync();
            runId = run.Id;
        }

        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            Assert.Equal(1, await followUp.SyncRuns.Where(x => x.Id == runId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, SyncRunStatus.Completed)
                    .SetProperty(x => x.CompletedAt, Start.AddMinutes(2))
                    .SetProperty(x => x.Error, (string?)null), cancellationToken));
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new SyncRunStore(db).ReconcileInterruptedRunsAsync(Start, Start.AddMinutes(1), "recovered");
            Assert.Equal(1, result);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, acknowledgement.Committed);
        Assert.Equal(1, await verify.SyncRuns.CountAsync(x =>
            x.Id == runId && x.Status == SyncRunStatus.Completed && x.CompletedAt == Start.AddMinutes(2) && x.Error == null));
        Assert.Equal(1, await verify.SyncRunReconciliationEvents.CountAsync());
    }

    [Fact]
    public async Task Deleting_old_runs_returns_the_original_cleanup_result_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var run = new SyncRun { Status = SyncRunStatus.Completed, StartedAt = Start.AddDays(-2), CompletedAt = Start.AddDays(-1) };
            run.Items.Add(new SyncRunItem { Status = SyncRunItemStatus.Indexed, CompletedAt = Start.AddDays(-1) });
            setup.SyncRuns.Add(run);
            await setup.SaveChangesAsync();
        }

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new SyncRunStore(db).DeleteBeforeAsync(Start, [SyncRunStatus.Completed]);
            Assert.Equal(1, result.DeletedRunCount);
            Assert.Equal(1, result.DeletedItemCount);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Empty(await verify.SyncRuns.ToListAsync());
        Assert.Empty(await verify.SyncRunItems.ToListAsync());
    }

    [Fact]
    public async Task Updating_version_approval_returns_updated_when_a_later_approval_changes_the_projection()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid versionId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var source = new PackageSource { Name = "Feed", Url = "https://packages.example/feed" };
            var package = new Package { PackageId = "Acme.Package", Source = source };
            var version = new PackageVersion
            {
                Package = package,
                Version = "1.0.0",
                ValidationStatus = ValidationStatus.Valid,
                ApprovalStatus = PackageApprovalStatus.Approved,
                ManifestHash = "hash"
            };
            package.Versions.Add(version);
            setup.Packages.Add(package);
            await setup.SaveChangesAsync();
            versionId = version.Id;
        }

        var approval = new ApprovalRecord
        {
            TargetType = ApprovalTargetType.PackageVersion,
            TargetId = versionId,
            Status = PackageApprovalStatus.Approved,
            Actor = "operator"
        };
        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            var laterVersion = await followUp.PackageVersions.SingleAsync(x => x.Id == versionId, cancellationToken);
            var laterApproval = new ApprovalRecord
            {
                TargetType = ApprovalTargetType.PackageVersion,
                TargetId = versionId,
                Status = PackageApprovalStatus.Rejected,
                Actor = "later-operator"
            };
            Assert.Equal(VersionApprovalUpdateResult.Updated,
                await new ApprovalStore(followUp).TryUpdateVersionApprovalAsync(
                    laterVersion, PackageApprovalStatus.Rejected, laterApproval, cancellationToken));
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var version = await db.PackageVersions.SingleAsync(x => x.Id == versionId);
            var result = await new ApprovalStore(db).TryUpdateVersionApprovalAsync(version, PackageApprovalStatus.Approved, approval);
            Assert.Equal(VersionApprovalUpdateResult.Updated, result);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, acknowledgement.Committed);
        Assert.Equal(2, await verify.ApprovalRecords.CountAsync());
        Assert.Equal(PackageApprovalStatus.Rejected, (await verify.PackageVersions.SingleAsync(x => x.Id == versionId)).ApprovalStatus);
    }

    [Fact]
    public async Task Advancing_due_billing_lifecycle_returns_the_original_transition_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        DateTimeOffset trialEndsAt;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            var started = await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            organizationId = organization.Id;
            trialEndsAt = Assert.IsType<OrganizationSubscription>(started.Subscription).TrialEndsAt;
        }

        var acknowledgement = new ConcurrentAuditLostCommitAcknowledgementInterceptor(connection, organizationId);
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var advances = await new OrganizationBillingStore(db).AdvanceDueAsync(trialEndsAt);
            var advance = Assert.Single(advances);
            Assert.Equal(OrganizationSubscriptionState.PastDue, advance.CurrentState);
            Assert.True(advance.NoticeCreated);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(OrganizationSubscriptionState.PastDue, (await verify.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId)).State);
        Assert.Single(await verify.OrganizationBillingLifecycleNotices.ToListAsync());
        Assert.Equal(4, await verify.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Requesting_deletion_returns_the_original_transition_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            organizationId = organization.Id;
        }

        var requestedAt = Start.AddDays(1);
        var acknowledgement = new ConcurrentAuditLostCommitAcknowledgementInterceptor(connection, organizationId);
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).RequestDeletionAsync(organizationId, requestedAt);
            Assert.NotNull(result);
            Assert.Equal(OrganizationSubscriptionState.Suspended, result!.CurrentState);
            Assert.True(result.NoticeCreated);
            Assert.True(result.CleanupQueued);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(OrganizationSubscriptionState.Suspended, (await verify.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId)).State);
        Assert.Single(await verify.OrganizationBillingCleanups.ToListAsync());
        Assert.Equal(3, await verify.OrganizationBillingLifecycleNotices.CountAsync());
    }

    [Fact]
    public async Task Claiming_cleanup_returns_the_original_work_item_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            await new OrganizationBillingStore(setup).RequestDeletionAsync(organization.Id, Start.AddDays(1));
            organizationId = organization.Id;
        }

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var work = await new OrganizationBillingStore(db).TryClaimCleanupAsync("worker", Start.AddDays(1));
            Assert.NotNull(work);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(OrganizationBillingCleanupState.InProgress, (await verify.OrganizationBillingCleanups.SingleAsync(x => x.OrganizationId == organizationId)).State);
        Assert.Equal(1, (await verify.OrganizationBillingCleanups.SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task Empty_cleanup_claim_stays_empty_when_work_is_enqueued_after_its_commit()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            organizationId = organization.Id;
        }

        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            Assert.NotNull(await new OrganizationBillingStore(followUp)
                .RequestDeletionAsync(organizationId, Start, cancellationToken));
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var work = await new OrganizationBillingStore(db)
                .TryClaimCleanupAsync("worker", Start.AddDays(1));
            Assert.Null(work);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, acknowledgement.Committed);
        Assert.Single(await verify.OrganizationBillingCleanups.ToListAsync());
        Assert.Equal(OrganizationBillingCleanupState.Queued,
            (await verify.OrganizationBillingCleanups.SingleAsync()).State);
    }

    [Fact]
    public async Task Completing_cleanup_returns_the_original_result_after_a_lost_acknowledgement()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        OrganizationBillingCleanupWorkItem work;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            await new OrganizationBillingStore(setup).RequestDeletionAsync(organization.Id, Start.AddDays(1));
            work = Assert.IsType<OrganizationBillingCleanupWorkItem>(await new OrganizationBillingStore(setup).TryClaimCleanupAsync("worker", Start.AddDays(1)));
            organizationId = organization.Id;
        }

        var acknowledgement = new ConcurrentAuditLostCommitAcknowledgementInterceptor(connection, organizationId);
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).CompleteCleanupAsync(new(
                work.Id, work.OrganizationId, work.SubscriptionId, work.LeaseToken,
                OrganizationBillingCleanupOutcome.ConfirmedAbsent, Start.AddDays(1).AddMinutes(1)));
            Assert.True(result.SubscriptionDeleted);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(OrganizationBillingCleanupState.Confirmed, (await verify.OrganizationBillingCleanups.SingleAsync()).State);
        Assert.Equal(OrganizationSubscriptionState.Deleted, (await verify.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId)).State);
        Assert.Equal(8, await verify.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Retrying_cleanup_returns_the_committed_failure_after_a_lost_acknowledgement_and_concurrent_audit()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        int auditCount;
        OrganizationBillingCleanupWorkItem work;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            await new OrganizationBillingStore(setup).StartTrialAsync(organization.Id, "stripe", Start);
            await new OrganizationBillingStore(setup).RequestDeletionAsync(organization.Id, Start.AddDays(1));
            work = Assert.IsType<OrganizationBillingCleanupWorkItem>(await new OrganizationBillingStore(setup)
                .TryClaimCleanupAsync("worker", Start.AddDays(1)));
            organizationId = organization.Id;
            auditCount = await setup.OrganizationAuditRecords.CountAsync();
        }

        var acknowledgement = new ConcurrentAuditLostCommitAcknowledgementInterceptor(connection, organizationId);
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).CompleteCleanupAsync(new(
                work.Id, work.OrganizationId, work.SubscriptionId, work.LeaseToken,
                OrganizationBillingCleanupOutcome.RetryableFailure, Start.AddDays(1).AddMinutes(1), "provider.timeout"));
            Assert.Equal(OrganizationBillingCleanupState.Queued, result.State);
            Assert.False(result.SubscriptionDeleted);
            Assert.Equal("provider.timeout", result.FailureCode);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        var cleanup = await verify.OrganizationBillingCleanups.SingleAsync();
        Assert.Equal(OrganizationBillingCleanupState.Queued, cleanup.State);
        Assert.Equal("provider.timeout", cleanup.LastFailureCode);
        Assert.Equal(auditCount + 2, await verify.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Granting_internal_entitlement_returns_granted_when_a_later_revoke_changes_the_projection()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            organizationId = organization.Id;
        }

        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            var revoked = await new OrganizationBillingStore(followUp)
                .RevokeInternalEntitlementAsync(organizationId, "later-operator", Start.AddMinutes(1), cancellationToken);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, revoked.Outcome);
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).GrantInternalEntitlementAsync(
                new(organizationId, new("Dogfood", 2, Start.AddDays(30)), "operator"), Start);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, result.Outcome);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, acknowledgement.Committed);
        Assert.False((await verify.OrganizationEntitlementSnapshots.SingleAsync()).ManagedHostingEnabled);
        Assert.Equal(2, await verify.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Revoking_internal_entitlement_returns_revoked_when_a_later_grant_changes_the_projection()
    {
        await using var connection = NewConnection();
        await connection.OpenAsync();
        Guid organizationId;
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            var organization = new Organization { Name = "Acme" };
            setup.Organizations.Add(organization);
            await setup.SaveChangesAsync();
            organizationId = organization.Id;
            await new OrganizationBillingStore(setup).GrantInternalEntitlementAsync(
                new(organizationId, new("Dogfood", 2, Start.AddDays(30)), "operator"), Start);
        }

        var acknowledgement = new FollowUpLostCommitAcknowledgementInterceptor(async (_, cancellationToken) =>
        {
            await using var followUp = new CatalogDbContext(PlainOptions(connection));
            var granted = await new OrganizationBillingStore(followUp).GrantInternalEntitlementAsync(
                new(organizationId, new("Dogfood later", 3, Start.AddDays(60)), "later-operator"),
                Start.AddDays(1),
                cancellationToken);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Regranted, granted.Outcome);
        });
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).RevokeInternalEntitlementAsync(organizationId, "operator", Start.AddDays(1));
            Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, result.Outcome);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, acknowledgement.Committed);
        Assert.True((await verify.OrganizationEntitlementSnapshots.SingleAsync()).ManagedHostingEnabled);
        Assert.Equal(3, (await verify.OrganizationEntitlementSnapshots.SingleAsync()).MaxInstances);
        Assert.Equal(3, await verify.OrganizationAuditRecords.CountAsync());
    }

    private static async Task<CatalogDbContext> CreateDatabaseAsync(SqliteConnection connection)
    {
        var db = new CatalogDbContext(PlainOptions(connection));
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static DbContextOptions<CatalogDbContext> PlainOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options;

    private static DbContextOptions<CatalogDbContext> LostAckOptions(SqliteConnection connection, DbTransactionInterceptor acknowledgement) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;

    private static SqliteConnection NewConnection() => new("Data Source=:memory:");

    private sealed class ConcurrentAuditLostCommitAcknowledgementInterceptor(
        SqliteConnection connection,
        Guid organizationId) : DbTransactionInterceptor
    {
        private int _remainingFailures = 1;

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                await using var db = new CatalogDbContext(PlainOptions(connection));
                db.OrganizationAuditRecords.Add(new OrganizationAuditRecord
                {
                    OrganizationId = organizationId,
                    Action = OrganizationAuditAction.BillingCleanupRequested,
                    TargetType = "subscription",
                    TargetId = organizationId.ToString("D"),
                    Summary = "Concurrent lifecycle audit.",
                    CreatedAt = Start
                });
                await db.SaveChangesAsync(cancellationToken);
                throw new LostCommitAcknowledgementException();
            }

            await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }
}
