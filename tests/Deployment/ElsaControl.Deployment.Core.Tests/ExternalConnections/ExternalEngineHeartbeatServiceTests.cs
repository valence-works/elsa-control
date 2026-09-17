using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Core.ExternalConnections;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests.ExternalConnections;

public sealed class ExternalEngineHeartbeatServiceTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000482");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000482");
    private static readonly Guid ConnectionId = Guid.Parse("30000000-0000-0000-0000-000000000482");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T10:00:00Z");
    private const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Exact_valence_component_evidence_is_catalog_matched_and_safe_capabilities_replace_the_snapshot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, [CatalogEntry("valence-runtime", "Supported", ImageDigest)]);
        var report = Report(1, components: [new("runtime", ImageDigest)], capabilities:
            [ExternalEngineHeartbeatService.StatusCapability, ExternalEngineHeartbeatService.StudioCapability]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.NotNull(result);
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.VerifiedManifest, result.Connection!.ReleaseEvidenceLevel);
        Assert.Equal("manifest-sha256", result.Connection.ReleaseEvidenceReference);
        Assert.Equal("server", result.Connection.ObservedRuntimeKind);
        Assert.Equal(2, result.Connection.Capabilities.Count);
        Assert.Equal("https://studio.example.test/elsa/", result.Connection.StudioDestinationCandidate);
        Assert.NotNull(result.Connection.StudioDestinationCandidateId);
        Assert.Null(result.Connection.StudioDestination);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Fresh,
            ExternalEngineConnectionFreshness.Classify(result.Connection, fixture.Time.GetUtcNow()));
    }

    [Fact]
    public void Protocol_v1_canonical_payload_matches_the_portable_golden_vector()
    {
        var report = Report(
            1,
            components:
            [
                new("worker", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                new("runtime", ImageDigest)
            ],
            capabilities:
            [
                ExternalEngineHeartbeatService.StudioCapability,
                ExternalEngineHeartbeatService.StatusCapability,
                ExternalEngineHeartbeatService.StudioCapability
            ]);

        var canonical = Encoding.UTF8.GetString(ExternalEngineHeartbeatService.CreateCanonicalPayload(report));

        Assert.Equal(
            "{\"sequence\":1,\"observedAt\":\"2026-09-17T10:00:00.0000000Z\",\"connectorProtocol\":\"1\",\"connectorVersion\":\"1.4.0\",\"runtimeHealth\":\"healthy\",\"runtimeKind\":\"server\",\"observedDistribution\":\"valence-runtime\",\"observedVersion\":\"3.8.1\",\"studioDestination\":\"https://studio.example.test/elsa/\",\"capabilities\":[\"connection.status\",\"studio.open\"],\"components\":[{\"id\":\"runtime\",\"imageDigest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},{\"id\":\"worker\",\"imageDigest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}",
            canonical);
        Assert.Equal("lv-I-PKdau9NK9KBH6y_lxB0lXkpUFiZRNkXxeLS6pA", ExternalEngineHeartbeatService.CreatePayloadDigest(report));
    }

    [Fact]
    public async Task Version_only_or_mismatched_component_evidence_stays_self_reported()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, [CatalogEntry("valence-runtime", "Supported", ImageDigest)]);
        var report = Report(1, components:
            [new("runtime", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, result!.Status);
        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SelfReported, result.Connection!.ReleaseEvidenceLevel);
        Assert.Null(result.Connection.ReleaseEvidenceReference);
    }

    [Fact]
    public async Task Supported_non_valence_release_requires_complete_component_evidence_and_supported_catalog_policy()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, [CatalogEntry("elsa-oss", "Supported", ImageDigest)]);
        var report = Report(1, distribution: "elsa-oss", components: [new("runtime", ImageDigest)]);

        var result = await fixture.Service.SubmitAsync(fixture.Request(report, key));

        Assert.Equal(ExternalEngineReleaseEvidenceLevel.SupportedRelease, result!.Connection!.ReleaseEvidenceLevel);
        Assert.Equal("manifest-sha256", result.Connection.ReleaseEvidenceReference);
    }

    [Fact]
    public async Task Unsupported_protocol_is_reported_only_after_proof_verification_and_other_invalid_reports_do_not_change_it()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, []);
        var unsupportedReport = Report(1) with { ConnectorProtocol = "2" };
        var invalidProofRequest = fixture.Request(unsupportedReport, key);
        var invalidProof = invalidProofRequest with
        {
            Proof = invalidProofRequest.Proof with { Signature = "invalid" }
        };

        var denied = await fixture.Service.SubmitAsync(invalidProof);
        Assert.Equal(ExternalEngineHeartbeatStatus.ProofDenied, denied!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.Unknown, fixture.Store.Connection.ConnectorCompatibilityStatus);

        var unsupported = await fixture.Service.SubmitAsync(fixture.Request(unsupportedReport, key));
        Assert.Equal(ExternalEngineHeartbeatStatus.UnsupportedProtocol, unsupported!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
            unsupported.Connection!.ConnectorCompatibilityStatus);
        Assert.Equal(Now, unsupported.Connection.ConnectorCompatibilityObservedAt);
        Assert.Null(unsupported.Connection.ConnectorProtocol);
        Assert.Equal(Now, unsupported.Connection.LastAuthenticatedAt);
        Assert.Equal(1, unsupported.Connection.LastHeartbeatSequence);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var unknown = await fixture.Service.SubmitAsync(
            fixture.Request(Report(2, capabilities: ["engine.delete"]), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.InvalidReport, unknown!.Status);
        Assert.Empty(fixture.Store.Connection.Capabilities);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
            fixture.Store.Connection.ConnectorCompatibilityStatus);

        var unsupportedReplay = await fixture.Service.SubmitAsync(fixture.Request(unsupportedReport, key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, unsupportedReplay!.Status);
        var sameSequenceRecovery = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, sameSequenceRecovery!.Status);

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var recovered = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, recovered!.Status);
        Assert.Equal(ExternalEngineConnectorCompatibilityStatus.Compatible,
            recovered.Connection!.ConnectorCompatibilityStatus);
        Assert.Equal(2, recovered.Connection.LastHeartbeatSequence);
    }

    [Fact]
    public async Task Proof_replay_and_new_proof_with_old_sequence_do_not_refresh_last_seen()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, []);
        var firstRequest = fixture.Request(Report(1), key);
        var first = await fixture.Service.SubmitAsync(firstRequest);
        var acceptedAt = first!.Connection!.LastAuthenticatedAt;

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        var replay = await fixture.Service.SubmitAsync(firstRequest);
        Assert.Equal(ExternalEngineHeartbeatStatus.ProofDenied, replay!.Status);
        Assert.Equal(ExternalEngineConnectorProofFailure.Replay, replay.ProofFailure);

        var oldSequence = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, oldSequence!.Status);
        Assert.Equal(acceptedAt, fixture.Store.Connection.LastAuthenticatedAt);
    }

    [Fact]
    public async Task Fresh_connection_projects_stale_then_recovers_with_a_new_heartbeat()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, []);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Waiting,
            ExternalEngineConnectionFreshness.Classify(fixture.Store.Connection, fixture.Time.GetUtcNow()));

        var connected = await fixture.Service.SubmitAsync(fixture.Request(Report(1), key));
        Assert.Equal(ExternalEngineConnectionStatus.Connected, connected!.Connection!.Status);

        fixture.Time.Advance(ExternalEngineHeartbeatService.FreshnessWindow + TimeSpan.FromSeconds(1));
        var stale = ExternalEngineConnectionFreshness.Project(fixture.Store.Connection, fixture.Time.GetUtcNow());
        Assert.Equal(ExternalEngineConnectionStatus.Degraded, stale.Status);
        Assert.Equal(ExternalEngineConnectorReachability.Unreachable, stale.ConnectorReachability);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Stale,
            ExternalEngineConnectionFreshness.Classify(stale, fixture.Time.GetUtcNow()));

        var recovered = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, recovered!.Status);
        Assert.Equal(ExternalEngineConnectionStatus.Connected, recovered.Connection!.Status);
        Assert.Equal(ExternalEngineHeartbeatFreshness.Fresh,
            ExternalEngineConnectionFreshness.Classify(recovered.Connection, fixture.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Concurrent_higher_sequence_wins_and_a_later_sequence_retries_against_the_new_version()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = await Fixture.CreateAsync(key, []);
        fixture.Store.InjectConcurrentSequence(3);

        var superseded = await fixture.Service.SubmitAsync(fixture.Request(Report(2), key));

        Assert.Equal(ExternalEngineHeartbeatStatus.OutOfOrder, superseded!.Status);
        Assert.Equal(3, fixture.Store.Connection.LastHeartbeatSequence);

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        fixture.Store.InjectConcurrentSequence(4);
        var newest = await fixture.Service.SubmitAsync(fixture.Request(Report(5), key));
        Assert.Equal(ExternalEngineHeartbeatStatus.Accepted, newest!.Status);
        Assert.Equal(5, fixture.Store.Connection.LastHeartbeatSequence);
    }

    private static ExternalEngineHeartbeatReport Report(
        long sequence,
        string distribution = "valence-runtime",
        IReadOnlyList<ExternalEngineComponentObservation>? components = null,
        IReadOnlyList<string>? capabilities = null) =>
        new(
            sequence,
            Now,
            ExternalEngineHeartbeatService.CurrentProtocol,
            "1.4.0",
            ExternalEngineRuntimeHealth.Healthy,
            "server",
            distribution,
            "3.8.1",
            "https://studio.example.test/elsa/",
            capabilities ?? [ExternalEngineHeartbeatService.StatusCapability],
            components ?? []);

    private static GovernedReleaseCatalogEntry CatalogEntry(string distribution, string lifecycle, string imageDigest) =>
        new(
            "1",
            "manifest-ref",
            "manifest-sha256",
            "payload-sha256",
            "signature-ref",
            "signature-sha256",
            "paid",
            new(distribution, "3", "3.8", "3.8.1", "stable", "released", "community", "repo", "commit", "run"),
            new(
                "combined",
                "1",
                ["server"],
                [],
                [],
                [new("runtime", "registry.example/runtime@" + imageDigest, imageDigest,
                    new Dictionary<string, string>(), [], [], [], null)],
                []),
            lifecycle,
            Now);

    private sealed class Fixture(
        MutableTimeProvider time,
        RecordingConnectionStore store,
        ExternalEngineHeartbeatService service,
        ExternalEngineConnectorIdentity identity)
    {
        private long _nonce;

        public MutableTimeProvider Time { get; } = time;
        public RecordingConnectionStore Store { get; } = store;
        public ExternalEngineHeartbeatService Service { get; } = service;

        public static async Task<Fixture> CreateAsync(ECDsa key, IReadOnlyList<GovernedReleaseCatalogEntry> entries)
        {
            var time = new MutableTimeProvider(Now);
            var enrollmentStore = new InMemoryExternalEngineEnrollmentStore();
            var enrollment = new ExternalEngineEnrollmentService(enrollmentStore, time);
            var issued = await enrollment.IssueAsync(new(OrganizationId, WorkspaceId, ConnectionId));
            var publicKey = ExternalEngineEnrollmentProtocol.ExportPublicKey(key);
            var unsigned = new ExternalEngineEnrollmentRedeemRequest(
                issued.ChallengeId, OrganizationId, WorkspaceId, ConnectionId, issued.Purpose, issued.Audience,
                issued.Challenge, publicKey, "");
            var redemption = unsigned with
            {
                Signature = ExternalEngineEnrollmentProtocol.Sign(key,
                    ExternalEngineEnrollmentProtocol.CreateRedemptionPayload(
                        unsigned.ChallengeId, unsigned.OrganizationId, unsigned.WorkspaceId, unsigned.ConnectionId,
                        unsigned.Purpose, unsigned.Audience,
                        ExternalEngineEnrollmentProtocol.HashChallenge(unsigned.Challenge),
                        ExternalEngineEnrollmentProtocol.PublicKeyThumbprint(publicKey)))
            };
            var redeemed = await enrollment.RedeemAsync(redemption);
            Assert.True(redeemed.Succeeded);
            var connection = new ExternalEngineConnection(
                ConnectionId, OrganizationId, WorkspaceId, "Engine", ExternalEngineConnectionStatus.Pending,
                ExternalEngineRuntimeHealth.Unknown, ExternalEngineConnectorReachability.Unknown,
                null, null, null, null, null, ExternalEngineReleaseEvidenceLevel.None, null, [], null,
                redeemed.Identity!.Id, issued.ChallengeId, Now, Now, null, 1);
            var store = new RecordingConnectionStore(connection);
            var service = new ExternalEngineHeartbeatService(store, enrollment, new StaticCatalog(entries), time);
            return new(time, store, service, redeemed.Identity);
        }

        public ExternalEngineHeartbeatRequest Request(ExternalEngineHeartbeatReport report, ECDsa key)
        {
            var proof = new ExternalEngineConnectorProof(
                identity.Id,
                OrganizationId,
                WorkspaceId,
                ConnectionId,
                identity.Audience,
                identity.KeyVersion,
                ExternalEngineHeartbeatService.HeartbeatOperation,
                ExternalEngineHeartbeatService.CreatePayloadDigest(report),
                Time.GetUtcNow(),
                ExternalEngineEnrollmentProtocol.Base64UrlEncode(BitConverter.GetBytes(++_nonce).Concat(RandomNumberGenerator.GetBytes(8)).ToArray()),
                "");
            return new(proof with
            {
                Signature = ExternalEngineEnrollmentProtocol.Sign(key,
                    ExternalEngineEnrollmentProtocol.CreateConnectorProofPayload(proof))
            }, report);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private sealed class StaticCatalog(IReadOnlyList<GovernedReleaseCatalogEntry> entries) : IGovernedReleaseCatalogStore
    {
        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> values,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>(entries.Where(entry =>
                (query.DistributionId is null || string.Equals(entry.Distribution.Id, query.DistributionId, StringComparison.OrdinalIgnoreCase))
                && (query.ReleaseVersion is null || string.Equals(entry.Distribution.ReleaseVersion, query.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
                && (query.RuntimeKind is null || entry.Topology.RuntimeKinds.Contains(query.RuntimeKind, StringComparer.OrdinalIgnoreCase))).ToArray());
    }

    private sealed class RecordingConnectionStore(ExternalEngineConnection connection) : IExternalEngineConnectionStore
    {
        private long? _concurrentSequence;
        public ExternalEngineConnection Connection { get; private set; } = connection;

        public void InjectConcurrentSequence(long sequence) => _concurrentSequence = sequence;

        public Task<IReadOnlyList<ExternalEngineConnection>> ListAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalEngineConnection>>([Connection]);

        public Task<ExternalEngineConnection?> FindAsync(Guid organizationId, Guid workspaceId, Guid connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalEngineConnection?>(organizationId == OrganizationId && workspaceId == WorkspaceId && connectionId == ConnectionId ? Connection : null);

        public Task<ExternalEngineHeartbeatStoreResult> TryApplyHeartbeatAsync(
            ExternalEngineConnection expected,
            ExternalEngineHeartbeatProjection projection,
            Guid identityId,
            DateTimeOffset receivedAt,
            TimeSpan minimumInterval,
            CancellationToken cancellationToken = default)
        {
            if (_concurrentSequence is { } concurrentSequence)
            {
                _concurrentSequence = null;
                Connection = Connection with
                {
                    Status = ExternalEngineConnectionStatus.Connected,
                    ConnectorReachability = ExternalEngineConnectorReachability.Reachable,
                    LastAuthenticatedAt = receivedAt.Subtract(TimeSpan.FromSeconds(6)),
                    LastHeartbeatSequence = concurrentSequence,
                    LastHeartbeatObservedAt = projection.ObservedAt,
                    UpdatedAt = receivedAt,
                    Version = Connection.Version + 1
                };
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            }
            if (expected.Version != Connection.Version)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            if (identityId != Connection.ActiveIdentityId)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, Connection));
            if (Connection.LastHeartbeatSequence is not null && projection.Sequence <= Connection.LastHeartbeatSequence)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.OutOfOrder, Connection));
            if (Connection.LastAuthenticatedAt is { } last && receivedAt - last < minimumInterval)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.RateLimited, Connection, minimumInterval - (receivedAt - last)));
            var studioCandidate = projection.Capabilities.Contains(
                    ExternalEngineHeartbeatService.StudioCapability, StringComparer.Ordinal)
                ? projection.StudioDestinationCandidate
                : null;
            var candidateChanged = !string.Equals(
                Connection.StudioDestinationCandidate, studioCandidate, StringComparison.Ordinal);
            Connection = Connection with
            {
                Status = projection.Status,
                RuntimeHealth = projection.RuntimeHealth,
                ConnectorReachability = projection.ConnectorReachability,
                LastAuthenticatedAt = receivedAt,
                ConnectorProtocol = projection.ConnectorProtocol,
                ConnectorVersion = projection.ConnectorVersion,
                ObservedDistribution = projection.ObservedDistribution,
                ObservedVersion = projection.ObservedVersion,
                ObservedRuntimeKind = projection.ObservedRuntimeKind,
                ReleaseEvidenceLevel = projection.ReleaseEvidenceLevel,
                ReleaseEvidenceReference = projection.ReleaseEvidenceReference,
                StudioDestinationCandidate = studioCandidate,
                StudioDestinationCandidateId = candidateChanged
                    ? studioCandidate is null ? null : Guid.NewGuid()
                    : Connection.StudioDestinationCandidateId,
                StudioDestination = candidateChanged ? null : Connection.StudioDestination,
                StudioDestinationConfirmedAt = candidateChanged ? null : Connection.StudioDestinationConfirmedAt,
                StudioDestinationConfirmedByAccountId = candidateChanged ? null : Connection.StudioDestinationConfirmedByAccountId,
                Capabilities = projection.Capabilities,
                ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.Compatible,
                ConnectorCompatibilityObservedAt = receivedAt,
                CapabilitiesObservedAt = receivedAt,
                LastHeartbeatSequence = projection.Sequence,
                LastHeartbeatObservedAt = projection.ObservedAt,
                UpdatedAt = receivedAt,
                Version = Connection.Version + 1
            };
            return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Applied, Connection));
        }

        public Task<ExternalEngineHeartbeatStoreResult> TryRecordUnsupportedProtocolAsync(
            ExternalEngineConnection expected,
            long sequence,
            DateTimeOffset observedAt,
            Guid identityId,
            DateTimeOffset receivedAt,
            TimeSpan minimumInterval,
            CancellationToken cancellationToken = default)
        {
            if (expected.Version != Connection.Version)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Concurrent, Connection));
            if (identityId != Connection.ActiveIdentityId)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.ScopeMismatch, Connection));
            if (Connection.LastHeartbeatSequence is { } lastSequence && sequence <= lastSequence)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.OutOfOrder, Connection));
            if (Connection.LastAuthenticatedAt is { } last && receivedAt - last < minimumInterval)
                return Task.FromResult(new ExternalEngineHeartbeatStoreResult(
                    ExternalEngineHeartbeatStoreStatus.RateLimited,
                    Connection,
                    minimumInterval - (receivedAt - last)));
            Connection = Connection with
            {
                ConnectorCompatibilityStatus = ExternalEngineConnectorCompatibilityStatus.UnsupportedProtocol,
                ConnectorCompatibilityObservedAt = receivedAt,
                ConnectorReachability = ExternalEngineConnectorReachability.Reachable,
                LastAuthenticatedAt = receivedAt,
                LastHeartbeatSequence = sequence,
                LastHeartbeatObservedAt = observedAt,
                UpdatedAt = receivedAt,
                Version = Connection.Version + 1
            };
            return Task.FromResult(new ExternalEngineHeartbeatStoreResult(ExternalEngineHeartbeatStoreStatus.Applied, Connection));
        }

        public Task<ExternalEngineConnectionCreateResult> TryCreateAsync(ExternalEngineConnection value, string idempotencyKey, string requestDigest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TrySetPairingChallengeAsync(ExternalEngineConnection expected, Guid challengeId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TrySetActiveIdentityAsync(ExternalEngineConnection expected, Guid identityId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TryPrepareRepairAsync(ExternalEngineConnection expected, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalEngineConnection?> TryDisconnectAsync(ExternalEngineConnection expected, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
