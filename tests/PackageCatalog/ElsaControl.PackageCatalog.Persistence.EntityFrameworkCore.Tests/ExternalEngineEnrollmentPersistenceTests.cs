using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ExternalEngineEnrollmentPersistenceTests : IAsyncLifetime
{
    private const string SqliteMigration = "20260917071227_AddExternalEngineEnrollmentPersistence";
    private const string SqlServerMigration = "20260917071238_AddExternalEngineEnrollmentPersistence";
    private const string SqliteLifecycleMigration = "20260917075801_AddExternalEngineConnectorIdentityLifecycle";
    private const string SqlServerLifecycleMigration = "20260917075811_AddExternalEngineConnectorIdentityLifecycle";
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000009");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000009");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000009");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T09:00:00Z");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-enrollment-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var organization = new Organization
        {
            Id = OrganizationId,
            Name = "Enrollment test organization",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.Organizations.Add(organization);
        db.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId,
            OrganizationId = OrganizationId,
            Organization = organization,
            Name = "Enrollment test workspace",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Enrollment_round_trip_persists_hash_public_identity_and_safe_audit_only()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store);
        var issued = await service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redemption = Redemption(issued, connectorKey);

        var result = await service.RedeemAsync(redemption);

        Assert.True(result.Succeeded);
        db.ChangeTracker.Clear();
        var challenge = await db.ExternalEngineEnrollmentChallenges.SingleAsync();
        var identity = await db.ExternalEngineConnectorIdentities.SingleAsync();
        var audits = await db.ExternalEngineEnrollmentAuditEvents.OrderBy(x => x.OccurredAt).ToArrayAsync();
        Assert.Equal(ExternalEngineEnrollmentProtocol.HashChallenge(issued.Challenge), challenge.ChallengeHash);
        Assert.NotEqual(issued.Challenge, challenge.ChallengeHash);
        Assert.Equal(redemption.PublicKey, identity.PublicKey);
        Assert.Equal(redemption.ConnectionId, identity.ConnectionId);
        Assert.Equal(
            [nameof(ExternalEngineEnrollmentAuditAction.ChallengeIssued), nameof(ExternalEngineEnrollmentAuditAction.RedemptionSucceeded)],
            audits.Select(x => x.Action));

        var serializedAudit = JsonSerializer.Serialize(audits);
        Assert.DoesNotContain(issued.Challenge, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(redemption.Signature, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(redemption.PublicKey, serializedAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("https://engine.example", serializedAudit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Challenge_and_identity_reads_require_exact_scope()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store);
        var issued = await service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Redemption(issued, connectorKey);
        var crossWorkspace = SignRedemption(valid with { WorkspaceId = Guid.NewGuid() }, connectorKey);

        Assert.Equal(
            ExternalEngineEnrollmentRedeemFailure.InvalidRequest,
            (await service.RedeemAsync(crossWorkspace)).Failure);
        var identity = (await service.RedeemAsync(valid)).Identity!;

        Assert.Null(await store.FindChallengeAsync(Guid.NewGuid(), WorkspaceId, ConnectionId, issued.ChallengeId));
        Assert.Null(await store.FindChallengeAsync(OrganizationId, Guid.NewGuid(), ConnectionId, issued.ChallengeId));
        Assert.Null(await store.FindChallengeAsync(OrganizationId, WorkspaceId, Guid.NewGuid(), issued.ChallengeId));
        Assert.Null(await store.FindIdentityAsync(Guid.NewGuid(), WorkspaceId, ConnectionId, identity.Id));
        Assert.Null(await store.FindIdentityAsync(OrganizationId, Guid.NewGuid(), ConnectionId, identity.Id));
        Assert.Null(await store.FindIdentityAsync(OrganizationId, WorkspaceId, Guid.NewGuid(), identity.Id));
        Assert.NotNull(await store.FindIdentityAsync(OrganizationId, WorkspaceId, ConnectionId, identity.Id));
    }

    [Fact]
    public async Task Concurrent_redemption_across_contexts_has_one_winner_without_duplicate_identity()
    {
        ExternalEngineEnrollmentIssueResult issued;
        await using (var issueDb = CreateDbContext())
        {
            var issueStore = new EfCoreExternalEngineEnrollmentStore(issueDb);
            issued = await new ExternalEngineEnrollmentService(issueStore, new FixedTimeProvider(Now), issueStore)
                .IssueAsync(Request());
        }

        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var redemption = Redemption(issued, connectorKey);
        var results = await Task.WhenAll(RedeemFromNewContextAsync(redemption), RedeemFromNewContextAsync(redemption));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Failure is ExternalEngineEnrollmentRedeemFailure.Replay or ExternalEngineEnrollmentRedeemFailure.AlreadyEnrolled);
        await using var verifyDb = CreateDbContext();
        Assert.Equal(1, await verifyDb.ExternalEngineConnectorIdentities.CountAsync());
        Assert.NotNull((await verifyDb.ExternalEngineEnrollmentChallenges.SingleAsync()).RedeemedAt);
    }

    [Fact]
    public async Task Separate_challenges_racing_for_one_connection_leave_the_loser_unconsumed()
    {
        ExternalEngineEnrollmentIssueResult first;
        ExternalEngineEnrollmentIssueResult second;
        await using (var issueDb = CreateDbContext())
        {
            var store = new EfCoreExternalEngineEnrollmentStore(issueDb);
            var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store);
            first = await service.IssueAsync(Request());
            second = await service.IssueAsync(Request());
        }

        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var requests = new[] { Redemption(first, firstKey), Redemption(second, secondKey) };
        var results = await Task.WhenAll(requests.Select(async request =>
            (Request: request, Result: await RedeemFromNewContextAsync(request))));

        Assert.Single(results, item => item.Result.Succeeded);
        var loser = Assert.Single(results, item => item.Result.Failure == ExternalEngineEnrollmentRedeemFailure.AlreadyEnrolled);
        await using var verifyDb = CreateDbContext();
        Assert.Equal(1, await verifyDb.ExternalEngineConnectorIdentities.CountAsync());
        Assert.Null((await verifyDb.ExternalEngineEnrollmentChallenges.SingleAsync(x => x.Id == loser.Request.ChallengeId)).RedeemedAt);
    }

    [Fact]
    public async Task Proof_nonce_consumption_is_hash_only_scoped_and_single_use()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store);
        var issued = await service.IssueAsync(Request());
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(issued, connectorKey))).Identity!;
        var nonceHash = Digest("one-time-proof-nonce");
        var nonce = new ExternalEngineConnectorProofNonce(
            Guid.NewGuid(), identity.Id, OrganizationId, WorkspaceId, ConnectionId, identity.KeyVersion,
            nonceHash, Now, Now.AddMinutes(2), Now.AddSeconds(1));

        Assert.True(await store.TryConsumeAsync(nonce));
        Assert.False(await store.TryConsumeAsync(nonce with { Id = Guid.NewGuid() }));
        Assert.False(await store.TryConsumeAsync(nonce with { Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }));
        db.ChangeTracker.Clear();
        var persisted = await db.ExternalEngineConnectorProofNonces.SingleAsync();
        Assert.Equal(nonceHash, persisted.NonceHash);
        Assert.DoesNotContain("one-time-proof-nonce", JsonSerializer.Serialize(persisted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Consuming_a_proof_prunes_expired_nonce_markers()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store);
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), connectorKey))).Identity!;
        var expired = new ExternalEngineConnectorProofNonce(
            Guid.NewGuid(), identity.Id, OrganizationId, WorkspaceId, ConnectionId, identity.KeyVersion,
            Digest("expired-proof-nonce"), Now, Now.AddSeconds(2), Now.AddSeconds(1));
        var current = new ExternalEngineConnectorProofNonce(
            Guid.NewGuid(), identity.Id, OrganizationId, WorkspaceId, ConnectionId, identity.KeyVersion,
            Digest("current-proof-nonce"), Now.AddSeconds(2), Now.AddMinutes(2), Now.AddSeconds(3));

        Assert.True(await store.TryConsumeAsync(expired));
        db.ChangeTracker.Clear();
        Assert.True(await store.TryConsumeAsync(current));
        db.ChangeTracker.Clear();

        var persisted = await db.ExternalEngineConnectorProofNonces.SingleAsync();
        Assert.Equal(current.NonceHash, persisted.NonceHash);
    }

    [Fact]
    public async Task Audit_events_are_append_only_in_change_tracker_and_database()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        await new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store).IssueAsync(Request());
        db.ChangeTracker.Clear();
        var audit = await db.ExternalEngineEnrollmentAuditEvents.SingleAsync();
        audit.Reason = nameof(ExternalEngineEnrollmentAuditReason.InvalidRequest);

        var trackedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", trackedFailure.Message, StringComparison.Ordinal);

        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ExternalEngineEnrollmentAuditEvents"));
    }

    [Fact]
    public async Task Rejection_audit_requires_an_existing_challenge_in_the_exact_scope()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var issued = await new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store).IssueAsync(Request());
        var rejected = new ExternalEngineEnrollmentAuditRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WorkspaceId,
            ConnectionId,
            issued.ChallengeId,
            null,
            ExternalEngineEnrollmentAuditAction.RedemptionRejected,
            ExternalEngineEnrollmentAuditReason.InvalidRequest,
            Now);

        await store.RecordAsync(rejected);

        Assert.Equal(1, await db.ExternalEngineEnrollmentAuditEvents.CountAsync());
        Assert.Equal(nameof(ExternalEngineEnrollmentAuditAction.ChallengeIssued),
            (await db.ExternalEngineEnrollmentAuditEvents.SingleAsync()).Action);
    }

    [Fact]
    public async Task Rotation_revocation_and_fresh_challenge_repair_are_durable()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store);
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), currentKey))).Identity!;
        var overlap = TimeSpan.FromMinutes(2);
        var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
        var rotation = SignProof(Proof(
            identity,
            ExternalEngineEnrollmentDefaults.RotationOperation,
            ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, overlap)), currentKey);

        var rotated = await service.RotateConnectorKeyAsync(
            new ExternalEngineConnectorKeyRotationRequest(rotation, nextPublicKey, overlap));
        var revocation = SignProof(Proof(
            rotated.Identity!,
            ExternalEngineEnrollmentDefaults.RevocationOperation,
            ExternalEngineEnrollmentProtocol.CreateRevocationPayloadDigest()), nextKey);
        var revoked = await service.RevokeConnectorAsync(revocation);

        Assert.True(rotated.Succeeded);
        Assert.True(revoked.Succeeded);
        db.ChangeTracker.Clear();
        var persistedRevoked = await db.ExternalEngineConnectorIdentities.SingleAsync();
        Assert.Equal(2, persistedRevoked.KeyVersion);
        Assert.Equal(1, persistedRevoked.PreviousKeyVersion);
        Assert.NotNull(persistedRevoked.RevokedAt);

        var repairService = new ExternalEngineEnrollmentService(
            store,
            new FixedTimeProvider(Now.AddSeconds(1)),
            store,
            store);
        var repaired = await repairService.RedeemAsync(
            Redemption(await repairService.IssueAsync(Request()), replacementKey));

        Assert.True(repaired.Succeeded);
        Assert.Equal(identity.Id, repaired.Identity!.Id);
        Assert.Equal(3, repaired.Identity.KeyVersion);
        Assert.Null(repaired.Identity.PreviousKeyVersion);
        Assert.Null(repaired.Identity.RevokedAt);
        db.ChangeTracker.Clear();
        var actions = await db.ExternalEngineEnrollmentAuditEvents.Select(x => x.Action).ToArrayAsync();
        Assert.Contains(nameof(ExternalEngineEnrollmentAuditAction.KeyRotated), actions);
        Assert.Contains(nameof(ExternalEngineEnrollmentAuditAction.IdentityRevoked), actions);
        Assert.Contains(nameof(ExternalEngineEnrollmentAuditAction.IdentityRepaired), actions);
    }

    [Fact]
    public async Task Repair_rejects_a_pairing_challenge_issued_before_revocation()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var time = new AdjustableTimeProvider(Now);
        var service = new ExternalEngineEnrollmentService(store, time, store, store);
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), currentKey))).Identity!;
        var preRevocationChallenge = await service.IssueAsync(Request());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(await service.RevokeConnectorForRecoveryAsync(
            identity.OrganizationId,
            identity.WorkspaceId,
            identity.ConnectionId,
            identity.Id));
        Assert.Equal(
            ExternalEngineEnrollmentRedeemFailure.InvalidRequest,
            (await service.RedeemAsync(Redemption(preRevocationChallenge, replacementKey))).Failure);

        time.Advance(TimeSpan.FromSeconds(1));
        var repaired = await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), replacementKey));
        Assert.True(repaired.Succeeded);
        Assert.Equal(identity.KeyVersion + 1, repaired.Identity!.KeyVersion);
    }

    [Fact]
    public async Task Concurrent_rotations_across_contexts_have_one_winner()
    {
        ExternalEngineConnectorIdentity identity;
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using (var enrollmentDb = CreateDbContext())
        {
            var store = new EfCoreExternalEngineEnrollmentStore(enrollmentDb);
            var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store);
            identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), currentKey))).Identity!;
        }

        using var firstNextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondNextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var overlap = TimeSpan.FromMinutes(2);
        var requests = new[] { firstNextKey, secondNextKey }.Select(nextKey =>
        {
            var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
            var proof = SignProof(Proof(
                identity,
                ExternalEngineEnrollmentDefaults.RotationOperation,
                ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(publicKey, overlap)), currentKey);
            return new ExternalEngineConnectorKeyRotationRequest(proof, publicKey, overlap);
        }).ToArray();

        var results = await Task.WhenAll(requests.Select(RotateFromNewContextAsync));

        Assert.Single(results, result => result.Succeeded);
        Assert.Single(results, result => result.Failure is
            ExternalEngineConnectorProofFailure.InvalidRequest or
            ExternalEngineConnectorProofFailure.KeyVersionMismatch);
        await using var verifyDb = CreateDbContext();
        var persisted = await verifyDb.ExternalEngineConnectorIdentities.SingleAsync();
        Assert.Equal(2, persisted.KeyVersion);
        Assert.Equal(1, await verifyDb.ExternalEngineEnrollmentAuditEvents.CountAsync(
            x => x.Action == nameof(ExternalEngineEnrollmentAuditAction.KeyRotated)));
    }

    [Fact]
    public async Task Sqlite_downgrade_preserves_lifecycle_audit_rows_and_append_only_guards()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store);
        using var currentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), currentKey))).Identity!;
        var overlap = TimeSpan.FromMinutes(2);
        var nextPublicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(nextKey);
        var rotation = SignProof(Proof(
            identity,
            ExternalEngineEnrollmentDefaults.RotationOperation,
            ExternalEngineEnrollmentProtocol.CreateRotationPayloadDigest(nextPublicKey, overlap)), currentKey);
        Assert.True((await service.RotateConnectorKeyAsync(
            new ExternalEngineConnectorKeyRotationRequest(rotation, nextPublicKey, overlap))).Succeeded);

        db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync(SqliteMigration);

        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM ExternalEngineEnrollmentAuditEvents WHERE Action = 'KeyRotated'").SingleAsync());
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ExternalEngineEnrollmentAuditEvents"));
    }

    [Fact]
    public async Task Sqlite_downgrade_fails_closed_while_a_revoked_identity_exists()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        var service = new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store);
        using var connectorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = (await service.RedeemAsync(Redemption(await service.IssueAsync(Request()), connectorKey))).Identity!;
        Assert.NotNull(await service.RevokeConnectorForRecoveryAsync(
            identity.OrganizationId,
            identity.WorkspaceId,
            identity.ConnectionId,
            identity.Id));

        db.ChangeTracker.Clear();
        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => db.GetService<IMigrator>().MigrateAsync(SqliteMigration));

        Assert.Contains("NoRevokedDowngrade", failure.Message, StringComparison.Ordinal);
        Assert.Contains(SqliteLifecycleMigration, await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void Model_has_no_raw_bearer_or_private_key_fields()
    {
        using var db = CreateDbContext();
        Assert.Equal(
            ["Audience", "ChallengeHash", "ConnectionId", "ExpiresAt", "Id", "IssuedAt", "OrganizationId", "Purpose", "RedeemedAt", "WorkspaceId"],
            PropertyNames<ExternalEngineEnrollmentChallengeEntity>(db));
        Assert.DoesNotContain("PrivateKey", PropertyNames<ExternalEngineConnectorIdentityEntity>(db));
        Assert.Contains("PublicKey", PropertyNames<ExternalEngineConnectorIdentityEntity>(db));
        Assert.Equal(128, db.Model.FindEntityType(typeof(ExternalEngineConnectorIdentityEntity))!
            .FindProperty(nameof(ExternalEngineConnectorIdentityEntity.PublicKey))!.GetMaxLength());
        Assert.Contains("NonceHash", PropertyNames<ExternalEngineConnectorProofNonceEntity>(db));
        Assert.DoesNotContain("Nonce", PropertyNames<ExternalEngineConnectorProofNonceEntity>(db));
        Assert.Equal(
            ["Action", "ChallengeId", "ConnectionId", "Id", "IdentityId", "OccurredAt", "OrganizationId", "Reason", "WorkspaceId"],
            PropertyNames<ExternalEngineEnrollmentAuditEventEntity>(db));
    }

    [Fact]
    public void Provider_migrations_are_discoverable_synchronized_and_guard_audit_rows()
    {
        using var sqlite = CreateDbContext();
        using var sqlServer = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqliteLifecycleMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerLifecycleMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.False(sqlite.Database.HasPendingModelChanges());
        Assert.False(sqlServer.Database.HasPendingModelChanges());
        var script = sqlServer.GetService<IMigrator>().GenerateScript(
            toMigration: SqlServerLifecycleMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("ExternalEngineEnrollmentChallenges", script, StringComparison.Ordinal);
        Assert.Contains("ExternalEngineConnectorIdentities", script, StringComparison.Ordinal);
        Assert.Contains("PreviousKeyValidUntil", script, StringComparison.Ordinal);
        Assert.Contains("TR_ExternalEngineEnrollmentAuditEvents_AppendOnly", script, StringComparison.Ordinal);
        var downgradeScript = sqlServer.GetService<IMigrator>().GenerateScript(
            fromMigration: SqlServerLifecycleMigration,
            toMigration: SqlServerMigration);
        Assert.Contains("IdentityRepaired", downgradeScript, StringComparison.Ordinal);
        Assert.Contains("Revoked", downgradeScript, StringComparison.Ordinal);
        Assert.Contains("Cannot downgrade connector identity lifecycle while revoked identities exist", downgradeScript, StringComparison.Ordinal);
    }

    private async Task<ExternalEngineEnrollmentRedeemResult> RedeemFromNewContextAsync(
        ExternalEngineEnrollmentRedeemRequest request)
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        return await new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store).RedeemAsync(request);
    }

    private async Task<ExternalEngineConnectorKeyRotationResult> RotateFromNewContextAsync(
        ExternalEngineConnectorKeyRotationRequest request)
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineEnrollmentStore(db);
        return await new ExternalEngineEnrollmentService(store, new FixedTimeProvider(Now), store, store)
            .RotateConnectorKeyAsync(request);
    }

    private CatalogDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                $"Data Source={_databasePath};Pooling=False;Default Timeout=30",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private static string[] PropertyNames<TEntity>(CatalogDbContext db) =>
        db.Model.FindEntityType(typeof(TEntity))!.GetProperties().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();

    private static ExternalEngineEnrollmentIssueRequest Request() =>
        new(OrganizationId, WorkspaceId, ConnectionId);

    private static ExternalEngineEnrollmentRedeemRequest Redemption(
        ExternalEngineEnrollmentIssueResult issued,
        ECDsa key)
    {
        var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
        var unsigned = new ExternalEngineEnrollmentRedeemRequest(
            issued.ChallengeId,
            issued.OrganizationId,
            issued.WorkspaceId,
            issued.ConnectionId,
            issued.Purpose,
            issued.Audience,
            issued.Challenge,
            publicKey,
            "");
        return SignRedemption(unsigned, key);
    }

    private static ExternalEngineEnrollmentRedeemRequest SignRedemption(
        ExternalEngineEnrollmentRedeemRequest unsigned,
        ECDsa key)
    {
        var payload = ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
            unsigned.ChallengeId,
            unsigned.OrganizationId,
            unsigned.WorkspaceId,
            unsigned.ConnectionId,
            unsigned.Purpose,
            unsigned.Audience,
            ExternalEngineEnrollmentProtocol.HashChallenge(unsigned.Challenge),
            ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(unsigned.PublicKey));
        return unsigned with { Signature = ExternalEngineEnrollmentProtocol.Sign(key, payload) };
    }

    private static ExternalEngineConnectorProof Proof(
        ExternalEngineConnectorIdentity identity,
        string operation,
        string payloadDigest) =>
        new(
            identity.Id,
            identity.OrganizationId,
            identity.WorkspaceId,
            identity.ConnectionId,
            identity.Audience,
            identity.KeyVersion,
            operation,
            payloadDigest,
            Now,
            ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            "");

    private static ExternalEngineConnectorProof SignProof(ExternalEngineConnectorProof proof, ECDsa key) =>
        proof with
        {
            Signature = ExternalEngineEnrollmentProtocol.Sign(
                key,
                ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof))
        };

    private static string Digest(string value) =>
        ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }
}
