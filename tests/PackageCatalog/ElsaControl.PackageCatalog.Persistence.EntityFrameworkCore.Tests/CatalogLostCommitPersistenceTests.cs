using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Approvals;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CatalogLostCommitPersistenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Updating_external_identity_seen_returns_without_replaying_the_write()
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

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
            await new AccountWorkspaceStore(db).UpdateExternalIdentitySeenAsync(identityId, "Updated", "updated@example.com");

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.ExternalIdentities.CountAsync());
        Assert.Equal(1, await verify.Accounts.CountAsync(x => x.DisplayName == "Updated" && x.Email == "updated@example.com"));
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
        await using (var setup = await CreateDatabaseAsync(connection))
        {
            setup.SyncRuns.Add(new SyncRun { Status = SyncRunStatus.Running, StartedAt = Start.AddHours(-1) });
            await setup.SaveChangesAsync();
        }

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new SyncRunStore(db).ReconcileInterruptedRunsAsync(Start, Start.AddMinutes(1), "recovered");
            Assert.Equal(1, result);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.SyncRuns.CountAsync(x => x.Status == SyncRunStatus.Failed && x.Error == "recovered"));
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
    public async Task Updating_version_approval_returns_updated_without_duplicate_approval_audit_after_a_lost_acknowledgement()
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
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var version = await db.PackageVersions.SingleAsync(x => x.Id == versionId);
            var result = await new ApprovalStore(db).TryUpdateVersionApprovalAsync(version, PackageApprovalStatus.Approved, approval);
            Assert.Equal(VersionApprovalUpdateResult.Updated, result);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(1, await verify.ApprovalRecords.CountAsync());
        Assert.Equal(PackageApprovalStatus.Approved, (await verify.PackageVersions.SingleAsync(x => x.Id == versionId)).ApprovalStatus);
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

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var advances = await new OrganizationBillingStore(db).AdvanceDueAsync(trialEndsAt);
            var advance = Assert.Single(advances);
            Assert.Equal(OrganizationSubscriptionState.PastDue, advance.CurrentState);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.Equal(OrganizationSubscriptionState.PastDue, (await verify.OrganizationSubscriptions.SingleAsync(x => x.OrganizationId == organizationId)).State);
        Assert.Single(await verify.OrganizationBillingLifecycleNotices.ToListAsync());
        Assert.Equal(3, await verify.OrganizationAuditRecords.CountAsync());
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
        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).RequestDeletionAsync(organizationId, requestedAt);
            Assert.NotNull(result);
            Assert.Equal(OrganizationSubscriptionState.Suspended, result!.CurrentState);
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

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
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
        Assert.Equal(7, await verify.OrganizationAuditRecords.CountAsync());
    }

    [Fact]
    public async Task Granting_internal_entitlement_returns_granted_without_a_duplicate_audit_after_a_lost_acknowledgement()
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

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).GrantInternalEntitlementAsync(
                new(organizationId, new("Dogfood", 2, Start.AddDays(30)), "operator"), Start);
            Assert.Equal(OrganizationInternalEntitlementOutcome.Granted, result.Outcome);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.True((await verify.OrganizationEntitlementSnapshots.SingleAsync()).ManagedHostingEnabled);
        Assert.Single(await verify.OrganizationAuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Revoking_internal_entitlement_returns_revoked_without_a_duplicate_audit_after_a_lost_acknowledgement()
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

        var acknowledgement = new LostCommitAcknowledgementInterceptor();
        await using (var db = new CatalogDbContext(LostAckOptions(connection, acknowledgement)))
        {
            var result = await new OrganizationBillingStore(db).RevokeInternalEntitlementAsync(organizationId, "operator", Start.AddDays(1));
            Assert.Equal(OrganizationInternalEntitlementOutcome.Revoked, result.Outcome);
        }

        await using var verify = new CatalogDbContext(PlainOptions(connection));
        Assert.False((await verify.OrganizationEntitlementSnapshots.SingleAsync()).ManagedHostingEnabled);
        Assert.Equal(2, await verify.OrganizationAuditRecords.CountAsync());
    }

    private static async Task<CatalogDbContext> CreateDatabaseAsync(SqliteConnection connection)
    {
        var db = new CatalogDbContext(PlainOptions(connection));
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static DbContextOptions<CatalogDbContext> PlainOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(connection).Options;

    private static DbContextOptions<CatalogDbContext> LostAckOptions(SqliteConnection connection, LostCommitAcknowledgementInterceptor acknowledgement) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection, isTransient: exception => exception is LostCommitAcknowledgementException)
            .AddInterceptors(acknowledgement)
            .Options;

    private static SqliteConnection NewConnection() => new("Data Source=:memory:");
}
