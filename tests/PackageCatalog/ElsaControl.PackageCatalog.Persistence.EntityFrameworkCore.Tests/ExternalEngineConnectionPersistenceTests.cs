using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ExternalEngineConnectionPersistenceTests : IAsyncLifetime
{
    private const string SqliteMigration = "20260917102139_AddExternalEngineHeartbeat";
    private const string SqliteTriggerRestoreMigration = "20260917102140_RestoreExternalEngineConnectionAuditTriggers";
    private const string SqlServerMigration = "20260917102150_AddExternalEngineHeartbeat";
    private const string SqliteConfirmationMigration = "20260917115000_ConfirmExternalEngineStudioDestination";
    private const string SqliteStudioTriggerRestoreMigration = "20260917115013_RestoreExternalEngineStudioDestinationAuditTriggers";
    private const string SqlServerConfirmationMigration = "20260917115012_ConfirmExternalEngineStudioDestination";
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000481");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000481");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T09:00:00Z");
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-connection-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var organization = new Organization
        {
            Id = OrganizationId,
            Name = "Connection test organization",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.Organizations.Add(organization);
        db.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId,
            OrganizationId = OrganizationId,
            Organization = organization,
            Name = "Connection test workspace",
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
    public async Task Create_is_scoped_idempotent_and_conflicting_reuse_fails()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var proposed = Connection(Guid.NewGuid(), "Customer engine");

        var created = await store.TryCreateAsync(proposed, "create-engine", "digest-a");
        var replayed = await store.TryCreateAsync(Connection(Guid.NewGuid(), "Customer engine"), "create-engine", "digest-a");
        var conflict = await store.TryCreateAsync(Connection(Guid.NewGuid(), "Other engine"), "create-engine", "digest-b");

        Assert.True(created.Succeeded);
        Assert.False(created.Replayed);
        Assert.True(replayed.Succeeded);
        Assert.True(replayed.Replayed);
        Assert.Equal(proposed.Id, replayed.Connection!.Id);
        Assert.Equal(ExternalEngineConnectionCreateFailure.Conflict, conflict.Failure);
        Assert.Single(await store.ListAsync(OrganizationId, WorkspaceId));
        Assert.Empty(await store.ListAsync(OrganizationId, Guid.NewGuid()));
        Assert.Null(await store.FindAsync(Guid.NewGuid(), WorkspaceId, proposed.Id));
    }

    [Fact]
    public async Task Concurrent_create_replay_has_one_durable_connection()
    {
        var first = CreateFromNewContextAsync(Connection(Guid.NewGuid(), "Concurrent engine"));
        var second = CreateFromNewContextAsync(Connection(Guid.NewGuid(), "Concurrent engine"));

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(results[0].Connection!.Id, results[1].Connection!.Id);
        await using var db = CreateDbContext();
        Assert.Single(await db.ExternalEngineConnections.ToListAsync());
    }

    [Fact]
    public async Task Disconnect_retains_a_value_free_tombstone_and_append_only_audit()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var destination = "https://studio.example.test/";
        var value = Connection(Guid.NewGuid(), "Customer engine") with
        {
            StudioDestinationCandidate = destination,
            StudioDestinationCandidateId = Guid.NewGuid(),
            StudioDestination = destination,
            StudioDestinationConfirmedAt = Now,
            StudioDestinationConfirmedByAccountId = Guid.NewGuid()
        };
        var current = (await store.TryCreateAsync(value, "disconnect-engine", "digest")).Connection!;
        current = (await store.TrySetPairingChallengeAsync(current, Guid.NewGuid(), Now.AddMinutes(1)))!;
        current = (await store.TrySetActiveIdentityAsync(current, Guid.NewGuid(), Now.AddMinutes(2)))!;

        var disconnected = await store.TryDisconnectAsync(current, Now.AddMinutes(3));

        Assert.NotNull(disconnected);
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, disconnected.Status);
        Assert.NotNull(disconnected.RevokedAt);
        Assert.NotNull(disconnected.ActiveIdentityId);
        Assert.Null(disconnected.StudioDestination);
        Assert.Null(disconnected.StudioDestinationCandidate);
        Assert.Null(disconnected.StudioDestinationCandidateId);
        Assert.Null(disconnected.StudioDestinationConfirmedAt);
        Assert.Null(disconnected.StudioDestinationConfirmedByAccountId);
        var persisted = await store.FindAsync(OrganizationId, WorkspaceId, value.Id);
        Assert.Equal(ExternalEngineConnectionStatus.Revoked, persisted!.Status);
        var audits = await db.ExternalEngineConnectionAuditEvents.OrderBy(x => x.OccurredAt).ToArrayAsync();
        Assert.Equal(["Created", "PairingIssued", "IdentityEnrolled", "Disconnected"], audits.Select(x => x.Action));
        Assert.Equal(
            ["Action", "ConnectionId", "Id", "OccurredAt", "OrganizationId", "WorkspaceId"],
            db.Model.FindEntityType(typeof(ExternalEngineConnectionAuditEventEntity))!
                .GetProperties().Select(x => x.Name).Order(StringComparer.Ordinal));
        var serialized = JsonSerializer.Serialize(audits);
        Assert.DoesNotContain("challenge", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", serialized, StringComparison.OrdinalIgnoreCase);

        db.ExternalEngineConnectionAuditEvents.Remove(audits[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Repair_clears_approved_and_candidate_studio_destinations()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var destination = "https://studio.example.test/";
        var value = Connection(Guid.NewGuid(), "Repair engine") with
        {
            StudioDestinationCandidate = destination,
            StudioDestinationCandidateId = Guid.NewGuid(),
            StudioDestination = destination,
            StudioDestinationConfirmedAt = Now,
            StudioDestinationConfirmedByAccountId = Guid.NewGuid()
        };
        var current = (await store.TryCreateAsync(value, "repair-engine", "digest")).Connection!;

        var repaired = await store.TryPrepareRepairAsync(current, Now.AddMinutes(1));

        Assert.NotNull(repaired);
        Assert.Null(repaired.StudioDestination);
        Assert.Null(repaired.StudioDestinationCandidate);
        Assert.Null(repaired.StudioDestinationCandidateId);
        Assert.Null(repaired.StudioDestinationConfirmedAt);
        Assert.Null(repaired.StudioDestinationConfirmedByAccountId);
    }

    [Fact]
    public async Task Heartbeat_update_is_monotonic_rate_limited_and_rechecks_the_active_identity()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var current = (await store.TryCreateAsync(Connection(Guid.NewGuid(), "Heartbeat engine"), "heartbeat-engine", "digest")).Connection!;
        var identityId = Guid.NewGuid();
        db.ExternalEngineConnectorIdentities.Add(new ExternalEngineConnectorIdentityEntity
        {
            Id = identityId,
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = current.Id,
            Audience = ExternalEngineEnrollmentDefaults.AudienceFor(current.Id),
            KeyAlgorithm = ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            KeyVersion = 1,
            PublicKey = "public-key",
            PublicKeyThumbprint = "thumbprint",
            EnrolledAt = Now
        });
        await db.SaveChangesAsync();
        var enrollmentStore = new EfCoreExternalEngineEnrollmentStore(db);
        var nonceConsumed = await enrollmentStore.TryConsumeAsync(
            new ExternalEngineConnectorProofNonce(
                Guid.NewGuid(),
                identityId,
                OrganizationId,
                WorkspaceId,
                current.Id,
                1,
                ExternalEngineEnrollmentProtocol.HashNonce(
                    ExternalEngineEnrollmentProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))),
                Now,
                Now.AddMinutes(5),
                Now.AddMinutes(1)),
            recordSuccessfulAudit: false);
        Assert.True(nonceConsumed);
        Assert.DoesNotContain(
            await db.ExternalEngineEnrollmentAuditEvents.Select(x => x.Action).ToListAsync(),
            action => action == ExternalEngineEnrollmentAuditAction.ProofNonceConsumed.ToString());
        current = (await store.TrySetActiveIdentityAsync(current, identityId, Now.AddMinutes(1)))!;
        var projection = new ExternalEngineHeartbeatProjection(
            1,
            Now.AddMinutes(2),
            ExternalEngineConnectionStatus.Connected,
            ExternalEngineRuntimeHealth.Healthy,
            ExternalEngineConnectorReachability.Reachable,
            "1",
            "1.4.0",
            "valence-runtime",
            "3.8.1",
            "server",
            ExternalEngineReleaseEvidenceLevel.VerifiedManifest,
            "manifest-sha256",
            "https://studio.example.test/",
            [ExternalEngineHeartbeatService.StatusCapability]);

        var applied = await store.TryApplyHeartbeatAsync(
            current, projection, identityId, Now.AddMinutes(2), TimeSpan.FromSeconds(5));

        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, applied.Status);
        Assert.Equal(1, applied.Connection!.LastHeartbeatSequence);
        Assert.Equal(projection.ObservedAt, applied.Connection.LastHeartbeatObservedAt);
        Assert.Equal("server", applied.Connection.ObservedRuntimeKind);
        Assert.Equal("manifest-sha256", applied.Connection.ReleaseEvidenceReference);
        Assert.Equal([ExternalEngineHeartbeatService.StatusCapability], applied.Connection.Capabilities);
        Assert.Contains("HeartbeatConnected", await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());

        var outOfOrder = await store.TryApplyHeartbeatAsync(
            applied.Connection, projection, identityId, Now.AddMinutes(2).AddSeconds(6), TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.OutOfOrder, outOfOrder.Status);

        var next = projection with { Sequence = 2, ObservedAt = projection.ObservedAt.AddSeconds(1) };
        var rateLimited = await store.TryApplyHeartbeatAsync(
            applied.Connection, next, identityId, Now.AddMinutes(2).AddSeconds(1), TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.RateLimited, rateLimited.Status);

        var repeatedCapabilities = await store.TryApplyHeartbeatAsync(
            applied.Connection, next, identityId, Now.AddMinutes(2).AddSeconds(6), TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, repeatedCapabilities.Status);
        Assert.Equal([ExternalEngineHeartbeatService.StatusCapability], repeatedCapabilities.Connection!.Capabilities);

        var identity = await db.ExternalEngineConnectorIdentities.SingleAsync(x => x.Id == identityId);
        identity.RevokedAt = Now.AddMinutes(2).AddSeconds(7);
        await db.SaveChangesAsync();
        var afterRevocation = next with { Sequence = 3, ObservedAt = next.ObservedAt.AddSeconds(1) };
        var revoked = await store.TryApplyHeartbeatAsync(
            repeatedCapabilities.Connection, afterRevocation, identityId, Now.AddMinutes(2).AddSeconds(12), TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Revoked, revoked.Status);

        var persisted = await store.FindAsync(OrganizationId, WorkspaceId, current.Id);
        Assert.Equal(2, persisted!.LastHeartbeatSequence);
        Assert.Equal(Now.AddMinutes(2).AddSeconds(6), persisted.LastAuthenticatedAt);
    }

    [Fact]
    public async Task Changing_the_studio_candidate_invalidates_approval_and_rejects_a_stale_candidate_id()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = (await store.TryCreateAsync(
            Connection(Guid.NewGuid(), "Studio candidate engine"), "studio-candidate-engine", "digest")).Connection!;
        var identityId = Guid.NewGuid();
        db.ExternalEngineConnectorIdentities.Add(new ExternalEngineConnectorIdentityEntity
        {
            Id = identityId,
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = connection.Id,
            Audience = ExternalEngineEnrollmentDefaults.AudienceFor(connection.Id),
            KeyAlgorithm = ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            KeyVersion = 1,
            PublicKey = "public-key",
            PublicKeyThumbprint = "thumbprint",
            EnrolledAt = Now
        });
        await db.SaveChangesAsync();
        connection = (await store.TrySetActiveIdentityAsync(connection, identityId, Now.AddMinutes(1)))!;

        var first = new ExternalEngineHeartbeatProjection(
            1,
            Now.AddMinutes(2),
            ExternalEngineConnectionStatus.Connected,
            ExternalEngineRuntimeHealth.Healthy,
            ExternalEngineConnectorReachability.Reachable,
            "1",
            "1.4.0",
            "valence-runtime",
            "3.8.1",
            "server",
            ExternalEngineReleaseEvidenceLevel.SelfReported,
            null,
            "https://studio-one.example.test/",
            [ExternalEngineHeartbeatService.StatusCapability, ExternalEngineHeartbeatService.StudioCapability]);
        var appliedFirst = await store.TryApplyHeartbeatAsync(
            connection, first, identityId, Now.AddMinutes(2), TimeSpan.Zero);
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, appliedFirst.Status);
        var candidateOneId = appliedFirst.Connection!.StudioDestinationCandidateId!.Value;
        Assert.Equal(first.StudioDestinationCandidate, appliedFirst.Connection.StudioDestinationCandidate);
        Assert.Null(appliedFirst.Connection.StudioDestination);

        var accountId = Guid.NewGuid();
        var confirmed = await store.TryConfirmStudioDestinationAsync(
            appliedFirst.Connection, candidateOneId, accountId, Now.AddMinutes(2).AddSeconds(1));
        Assert.Equal(first.StudioDestinationCandidate, confirmed!.StudioDestination);
        Assert.Equal(accountId, confirmed.StudioDestinationConfirmedByAccountId);
        Assert.Contains("StudioDestinationConfirmed",
            await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());

        var second = first with
        {
            Sequence = 2,
            ObservedAt = first.ObservedAt.AddSeconds(6),
            StudioDestinationCandidate = "https://studio-two.example.test/"
        };
        var appliedSecond = await store.TryApplyHeartbeatAsync(
            confirmed, second, identityId, Now.AddMinutes(2).AddSeconds(6), TimeSpan.Zero);

        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, appliedSecond.Status);
        Assert.Equal(second.StudioDestinationCandidate, appliedSecond.Connection!.StudioDestinationCandidate);
        Assert.NotEqual(candidateOneId, appliedSecond.Connection.StudioDestinationCandidateId);
        Assert.Null(appliedSecond.Connection.StudioDestination);
        Assert.Null(appliedSecond.Connection.StudioDestinationConfirmedAt);
        Assert.Null(appliedSecond.Connection.StudioDestinationConfirmedByAccountId);

        var staleConfirmation = await store.TryConfirmStudioDestinationAsync(
            appliedSecond.Connection,
            candidateOneId,
            accountId,
            Now.AddMinutes(2).AddSeconds(7));
        Assert.Null(staleConfirmation);

        var unsupported = await store.TryRecordUnsupportedProtocolAsync(
            appliedSecond.Connection,
            sequence: 3,
            observedAt: Now.AddMinutes(2).AddSeconds(12),
            identityId,
            receivedAt: Now.AddMinutes(2).AddSeconds(12),
            minimumInterval: TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, unsupported.Status);
        var unsupportedConnection = unsupported.Connection!;
        Assert.NotNull(unsupportedConnection.StudioDestinationCandidateId);
        var unsupportedConfirmation = await store.TryConfirmStudioDestinationAsync(
            unsupportedConnection,
            unsupportedConnection.StudioDestinationCandidateId.Value,
            accountId,
            Now.AddMinutes(2).AddSeconds(13));
        Assert.Null(unsupportedConfirmation);

        var recoveredProjection = second with
        {
            Sequence = 4,
            ObservedAt = Now.AddMinutes(2).AddSeconds(18)
        };
        var recovered = await store.TryApplyHeartbeatAsync(
            unsupportedConnection,
            recoveredProjection,
            identityId,
            Now.AddMinutes(2).AddSeconds(18),
            TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, recovered.Status);
        var currentConfirmation = await store.TryConfirmStudioDestinationAsync(
            recovered.Connection!,
            recovered.Connection!.StudioDestinationCandidateId!.Value,
            accountId,
            Now.AddMinutes(2).AddSeconds(19));
        Assert.Equal(second.StudioDestinationCandidate, currentConfirmation!.StudioDestination);

        var identity = await db.ExternalEngineConnectorIdentities.SingleAsync(x => x.Id == identityId);
        identity.RevokedAt = Now.AddMinutes(2).AddSeconds(20);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var revokedIdentityConfirmation = await store.TryConfirmStudioDestinationAsync(
            currentConfirmation,
            currentConfirmation.StudioDestinationCandidateId!.Value,
            accountId,
            Now.AddMinutes(2).AddSeconds(21));
        Assert.Null(revokedIdentityConfirmation);
    }

    [Fact]
    public async Task Unsupported_protocol_consumes_heartbeat_sequence_until_a_newer_supported_heartbeat_recovers()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = (await store.TryCreateAsync(
            Connection(Guid.NewGuid(), "Compatibility engine"), "compatibility-engine", "digest")).Connection!;
        var identityId = Guid.NewGuid();
        db.ExternalEngineConnectorIdentities.Add(new ExternalEngineConnectorIdentityEntity
        {
            Id = identityId,
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = connection.Id,
            Audience = ExternalEngineEnrollmentDefaults.AudienceFor(connection.Id),
            KeyAlgorithm = ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            KeyVersion = 1,
            PublicKey = "public-key",
            PublicKeyThumbprint = "thumbprint",
            EnrolledAt = Now
        });
        await db.SaveChangesAsync();
        connection = (await store.TrySetActiveIdentityAsync(connection, identityId, Now.AddMinutes(1)))!;

        var unsupported = await store.TryRecordUnsupportedProtocolAsync(
            connection,
            sequence: 1,
            observedAt: Now.AddMinutes(2),
            identityId,
            receivedAt: Now.AddMinutes(2),
            minimumInterval: TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, unsupported.Status);
        Assert.Equal(1, unsupported.Connection!.LastHeartbeatSequence);
        Assert.Equal(Now.AddMinutes(2), unsupported.Connection.LastHeartbeatObservedAt);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
            unsupported.Connection.ConnectorCompatibilityStatus);

        var replay = await store.TryRecordUnsupportedProtocolAsync(
            unsupported.Connection,
            sequence: 1,
            observedAt: Now.AddMinutes(2).AddSeconds(1),
            identityId,
            receivedAt: Now.AddMinutes(2).AddSeconds(6),
            minimumInterval: TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.OutOfOrder, replay.Status);

        var sameSequenceSupported = await store.TryApplyHeartbeatAsync(
            unsupported.Connection,
            new ExternalEngineHeartbeatProjection(
                1,
                Now.AddMinutes(2).AddSeconds(6),
                ExternalEngineConnectionStatus.Connected,
                ExternalEngineRuntimeHealth.Healthy,
                ExternalEngineConnectorReachability.Reachable,
                "1",
                "1.4.0",
                null,
                null,
                "server",
                ExternalEngineReleaseEvidenceLevel.None,
                null,
                null,
                [ExternalEngineHeartbeatService.StatusCapability]),
            identityId,
            Now.AddMinutes(2).AddSeconds(6),
            TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.OutOfOrder, sameSequenceSupported.Status);

        var recovered = await store.TryApplyHeartbeatAsync(
            unsupported.Connection,
            new ExternalEngineHeartbeatProjection(
                2,
                Now.AddMinutes(2).AddSeconds(6),
                ExternalEngineConnectionStatus.Connected,
                ExternalEngineRuntimeHealth.Healthy,
                ExternalEngineConnectorReachability.Reachable,
                "1",
                "1.4.0",
                null,
                null,
                "server",
                ExternalEngineReleaseEvidenceLevel.None,
                null,
                null,
                [ExternalEngineHeartbeatService.StatusCapability]),
            identityId,
            Now.AddMinutes(2).AddSeconds(6),
            TimeSpan.FromSeconds(5));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, recovered.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.Compatible,
            recovered.Connection!.ConnectorCompatibilityStatus);
        Assert.Equal(2, recovered.Connection.LastHeartbeatSequence);
    }

    [Fact]
    public async Task Concurrent_independent_context_heartbeats_preserve_the_highest_sequence_and_single_transition()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var current = (await store.TryCreateAsync(Connection(Guid.NewGuid(), "Concurrent heartbeat engine"), "concurrent-heartbeat-engine", "digest")).Connection!;
        var identityId = Guid.NewGuid();
        db.ExternalEngineConnectorIdentities.Add(new ExternalEngineConnectorIdentityEntity
        {
            Id = identityId,
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = current.Id,
            Audience = ExternalEngineEnrollmentDefaults.AudienceFor(current.Id),
            KeyAlgorithm = ExternalEngineEnrollmentDefaults.KeyAlgorithm,
            KeyVersion = 1,
            PublicKey = "public-key",
            PublicKeyThumbprint = "thumbprint",
            EnrolledAt = Now
        });
        await db.SaveChangesAsync();
        current = (await store.TrySetActiveIdentityAsync(current, identityId, Now.AddMinutes(1)))!;

        var lowerReceivedAt = Now.AddMinutes(2);
        var higherReceivedAt = Now.AddMinutes(3);
        var lower = new ExternalEngineHeartbeatProjection(
            1,
            lowerReceivedAt,
            ExternalEngineConnectionStatus.Connected,
            ExternalEngineRuntimeHealth.Healthy,
            ExternalEngineConnectorReachability.Reachable,
            "1",
            "1.4.0",
            "valence-runtime",
            "3.8.1",
            "server-v1",
            ExternalEngineReleaseEvidenceLevel.VerifiedManifest,
            "manifest-v1",
            "https://studio.example.test/",
            [ExternalEngineHeartbeatService.StatusCapability]);
        var higher = lower with
        {
            Sequence = 2,
            ObservedAt = higherReceivedAt,
            ConnectorVersion = "1.5.0",
            ObservedVersion = "3.8.2",
            ObservedRuntimeKind = "server-v2",
            ReleaseEvidenceReference = "manifest-v2"
        };

        var raced = await Task.WhenAll(
            ApplyHeartbeatFromNewContextAsync(current, lower, identityId, lowerReceivedAt),
            ApplyHeartbeatFromNewContextAsync(current, higher, identityId, higherReceivedAt));

        var initialWinner = Assert.Single(raced, result => result.Status == ExternalEngineHeartbeatStoreStatus.Applied);
        Assert.Single(raced, result => result.Status == ExternalEngineHeartbeatStoreStatus.Concurrent);

        // If the lower report won the initial race, retry the higher report against the new
        // persisted version. The stale lower snapshot must never be able to replace it afterward.
        if (initialWinner.Connection!.LastHeartbeatSequence != higher.Sequence)
        {
            ExternalEngineConnection latest;
            await using (var latestDb = CreateDbContext())
                latest = (await new EfCoreExternalEngineConnectionStore(latestDb)
                    .FindAsync(OrganizationId, WorkspaceId, current.Id))!;

            var higherRetry = await ApplyHeartbeatFromNewContextAsync(latest, higher, identityId, higherReceivedAt);
            Assert.Equal(ExternalEngineHeartbeatStoreStatus.Applied, higherRetry.Status);
        }

        var staleLower = await ApplyHeartbeatFromNewContextAsync(current, lower, identityId, higherReceivedAt.AddSeconds(1));
        Assert.Equal(ExternalEngineHeartbeatStoreStatus.Concurrent, staleLower.Status);

        await using var verify = CreateDbContext();
        var persisted = await new EfCoreExternalEngineConnectionStore(verify)
            .FindAsync(OrganizationId, WorkspaceId, current.Id);
        Assert.NotNull(persisted);
        Assert.Equal(higher.Sequence, persisted.LastHeartbeatSequence);
        Assert.Equal(higher.ObservedAt, persisted.LastHeartbeatObservedAt);
        Assert.Equal(higherReceivedAt, persisted.LastAuthenticatedAt);
        Assert.Equal(higherReceivedAt, persisted.CapabilitiesObservedAt);
        Assert.Equal(higher.ConnectorVersion, persisted.ConnectorVersion);
        Assert.Equal(higher.ObservedVersion, persisted.ObservedVersion);
        Assert.Equal(higher.ObservedRuntimeKind, persisted.ObservedRuntimeKind);
        Assert.Equal(higher.ReleaseEvidenceReference, persisted.ReleaseEvidenceReference);
        Assert.Equal(higher.Capabilities, persisted.Capabilities);

        var heartbeatTransitions = await verify.ExternalEngineConnectionAuditEvents
            .Where(item => item.ConnectionId == current.Id
                           && (item.Action == "HeartbeatConnected"
                               || item.Action == "HeartbeatRecovered"
                               || item.Action == "HeartbeatDegraded"))
            .OrderBy(item => item.OccurredAt)
            .ToArrayAsync();
        var connectedTransition = Assert.Single(heartbeatTransitions);
        Assert.Equal("HeartbeatConnected", connectedTransition.Action);
        Assert.Equal(initialWinner.Connection.LastAuthenticatedAt, connectedTransition.OccurredAt);
    }

    [Fact]
    public void Provider_migrations_are_synchronized()
    {
        using var sqlite = CreateDbContext();
        using var sqlServer = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqliteTriggerRestoreMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqliteConfirmationMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqliteStudioTriggerRestoreMigration, sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerConfirmationMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.False(sqlite.Database.HasPendingModelChanges());
        Assert.False(sqlServer.Database.HasPendingModelChanges());
        var script = sqlServer.GetService<IMigrator>().GenerateScript(
            toMigration: SqlServerConfirmationMigration,
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("ExternalEngineConnections", script, StringComparison.Ordinal);
        Assert.Contains("CustomerOperated", script, StringComparison.Ordinal);
        Assert.Contains("ExternalEngineConnectionAuditEvents", script, StringComparison.Ordinal);
        Assert.Contains("TR_ExternalEngineConnectionAuditEvents_AppendOnly", script, StringComparison.Ordinal);
        Assert.Contains("StudioDestinationCandidate", script, StringComparison.Ordinal);
        Assert.Contains("StudioDestinationConfirmedByAccountId", script, StringComparison.Ordinal);
        Assert.Contains("StudioDestinationConfirmed", script, StringComparison.Ordinal);
        var downScript = sqlServer.GetService<IMigrator>().GenerateScript(
            fromMigration: SqlServerMigration,
            toMigration: "20260917090821_AddExternalEngineConnections");
        Assert.Contains("THROW 51022", downScript, StringComparison.Ordinal);
        Assert.Contains("Cannot roll back external-engine heartbeat migration", downScript, StringComparison.Ordinal);
        var confirmationDownScript = sqlServer.GetService<IMigrator>().GenerateScript(
            fromMigration: SqlServerConfirmationMigration,
            toMigration: SqlServerMigration);
        Assert.Contains("Cannot roll back external-engine confirmation or compatibility data", confirmationDownScript, StringComparison.Ordinal);
        Assert.Contains("StudioDestinationConfirmed", confirmationDownScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_confirmation_downgrade_refuses_append_only_confirmation_history()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = Connection(Guid.NewGuid(), "Confirmed rollback engine");
        await store.TryCreateAsync(connection, "confirmed-rollback-engine", "digest");
        db.ExternalEngineConnectionAuditEvents.Add(new ExternalEngineConnectionAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = connection.Id,
            Action = "StudioDestinationConfirmed",
            OccurredAt = Now.AddMinutes(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            db.GetService<IMigrator>().MigrateAsync(SqliteMigration));

        Assert.Contains("Cannot roll back external-engine confirmation or compatibility data", failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SqliteConfirmationMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        Assert.Contains("StudioDestinationConfirmed",
            await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task Sqlite_clean_confirmation_downgrade_restores_heartbeat_schema_and_audit_guards()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = Connection(Guid.NewGuid(), "Clean confirmation rollback engine");
        await store.TryCreateAsync(connection, "clean-confirmation-rollback-engine", "digest");
        db.ChangeTracker.Clear();

        await db.GetService<IMigrator>().MigrateAsync(SqliteMigration);

        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ExternalEngineConnectionAuditEvents"));
        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS \"Value\" FROM pragma_table_info('ExternalEngineConnections')").ToListAsync();
        Assert.DoesNotContain("StudioDestinationCandidate", columns);
        Assert.DoesNotContain("StudioDestinationConfirmedByAccountId", columns);
        Assert.Contains("StudioDestination", columns);
    }

    [Fact]
    public async Task Sqlite_heartbeat_migration_preserves_audit_triggers_and_refuses_to_drop_heartbeat_data()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = Connection(Guid.NewGuid(), "Migration rollback engine");
        await store.TryCreateAsync(connection, "migration-rollback-engine", "digest");
        db.ChangeTracker.Clear();
        var migrator = db.GetService<IMigrator>();
        const string priorMigration = "20260917090635_AddExternalEngineConnections";
        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        Assert.Contains("Created", await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ExternalEngineConnections
            SET LastHeartbeatSequence = 1,
                LastHeartbeatObservedAt = 1,
                ObservedRuntimeKind = 'server',
                ReleaseEvidenceLevel = 'SupportedRelease',
                ReleaseEvidenceReference = 'manifest-sha256'
            WHERE Id = {connection.Id}
            """);
        db.ExternalEngineConnectionAuditEvents.Add(new ExternalEngineConnectionAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            WorkspaceId = WorkspaceId,
            ConnectionId = connection.Id,
            Action = "HeartbeatConnected",
            OccurredAt = Now.AddMinutes(1)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync(SqliteMigration);
        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        var failure = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(priorMigration));

        Assert.Contains("Cannot roll back external-engine heartbeat migration", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SqliteMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT LastHeartbeatSequence AS "Value"
            FROM ExternalEngineConnections
            WHERE Id = {connection.Id}
            """).SingleAsync());
        Assert.Contains("HeartbeatConnected", await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task Sqlite_clean_heartbeat_downgrade_restores_the_previous_schema_and_audit_guards()
    {
        await using var db = CreateDbContext();
        var store = new EfCoreExternalEngineConnectionStore(db);
        var connection = Connection(Guid.NewGuid(), "Clean rollback engine");
        await store.TryCreateAsync(connection, "clean-rollback-engine", "digest");
        db.ChangeTracker.Clear();

        await db.GetService<IMigrator>().MigrateAsync("20260917090635_AddExternalEngineConnections");

        Assert.Equal(2, await ConnectionAuditTriggerCountAsync(db));
        Assert.Contains("Created", await db.ExternalEngineConnectionAuditEvents.Select(x => x.Action).ToListAsync());
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ExternalEngineConnectionAuditEvents"));
        var columns = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS \"Value\" FROM pragma_table_info('ExternalEngineConnections')").ToListAsync();
        Assert.DoesNotContain("LastHeartbeatSequence", columns);
        Assert.DoesNotContain("ReleaseEvidenceReference", columns);
    }

    private static Task<int> ConnectionAuditTriggerCountAsync(CatalogDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS "Value"
            FROM sqlite_master
            WHERE type = 'trigger'
              AND tbl_name = 'ExternalEngineConnectionAuditEvents'
            """).SingleAsync();

    private CatalogDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                $"Data Source={_databasePath};Pooling=False;Default Timeout=30",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private async Task<ExternalEngineConnectionCreateResult> CreateFromNewContextAsync(ExternalEngineConnection connection)
    {
        await using var db = CreateDbContext();
        return await new EfCoreExternalEngineConnectionStore(db)
            .TryCreateAsync(connection, "concurrent-engine", "same-digest");
    }

    private async Task<ExternalEngineHeartbeatStoreResult> ApplyHeartbeatFromNewContextAsync(
        ExternalEngineConnection expected,
        ExternalEngineHeartbeatProjection projection,
        Guid identityId,
        DateTimeOffset receivedAt)
    {
        await using var db = CreateDbContext();
        return await new EfCoreExternalEngineConnectionStore(db)
            .TryApplyHeartbeatAsync(expected, projection, identityId, receivedAt, TimeSpan.Zero);
    }

    private static ExternalEngineConnection Connection(Guid id, string displayName) =>
        new(
            id,
            OrganizationId,
            WorkspaceId,
            displayName,
            ExternalEngineConnectionStatus.Pending,
            ExternalEngineRuntimeHealth.Unknown,
            ExternalEngineConnectorReachability.Unknown,
            null,
            null,
            null,
            null,
            null,
            ExternalEngineReleaseEvidenceLevel.None,
            null,
            [],
            null,
            null,
            null,
            Now,
            Now,
            null,
            1);
}
