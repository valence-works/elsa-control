using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaHandoffKeyRingTests
{
    private const string CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Theory]
    [InlineData("a")]
    [InlineData("K")]
    [InlineData("active-2026-10")]
    [InlineData("previous.2026.09")]
    [InlineData("kid_v1")]
    [InlineData("rotate-1.2_3")]
    public void Allowed_key_identifiers_match_the_platform_safe_format(string keyId)
    {
        Assert.True(ManagedElsaHandoffKeyIds.IsAllowed(keyId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData(".dot")]
    [InlineData("has:colon")]
    [InlineData("has/slash")]
    [InlineData("has space")]
    [InlineData("pre__fix")]
    [InlineData("-----BEGIN PUBLIC KEY-----")]
    public void Disallowed_key_identifiers_are_rejected(string? keyId)
    {
        Assert.False(ManagedElsaHandoffKeyIds.IsAllowed(keyId));
    }

    [Fact]
    public void Disallowed_key_identifiers_include_overlong_values()
    {
        Assert.False(ManagedElsaHandoffKeyIds.IsAllowed(new string('a', ManagedElsaHandoffKeyIds.MaximumLength + 1)));
        Assert.True(ManagedElsaHandoffKeyIds.IsAllowed(new string('a', ManagedElsaHandoffKeyIds.MaximumLength)));
    }

    [Fact]
    public void Previous_verification_key_overlap_covers_maximum_lifetime_and_clock_skew()
    {
        Assert.Equal(
            ManagedElsaHandoffDefaults.MaximumLifetime + ManagedElsaHandoffDefaults.VerificationClockSkewAllowance,
            ManagedElsaHandoffDefaults.PreviousVerificationKeyOverlap);
        Assert.True(ManagedElsaHandoffDefaults.PreviousVerificationKeyOverlap >
                    ManagedElsaHandoffDefaults.MaximumLifetime);
    }

    [Fact]
    public void Indexed_and_map_config_shapes_bind_allowed_previous_verification_keys()
    {
        using var active = RSA.Create(2048);
        using var previousFromList = RSA.Create(2048);
        using var previousFromMap = RSA.Create(2048);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActiveKeyId"] = "active-2026-10",
                [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActivePrivateKeyPem"] =
                    active.ExportRSAPrivateKeyPem(),
                [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:PreviousVerificationKeys:0:KeyId"] =
                    "previous-2026-09",
                [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:PreviousVerificationKeys:0:PublicKeyPem"] =
                    previousFromList.ExportRSAPublicKeyPem(),
                [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:PreviousPublicKeys:previous.2026.08"] =
                    previousFromMap.ExportRSAPublicKeyPem()
            })
            .Build();

        var options = new ManagedElsaHandoffOptions();
        configuration.GetSection(ManagedElsaHandoffDefaults.ConfigurationSection).Bind(options);
        using var keyRing = ManagedElsaHandoffKeyRing.CreateConfigured(options);

        Assert.Equal("active-2026-10", keyRing.ActiveKeyId);
        Assert.True(keyRing.ContainsKey("active-2026-10"));
        Assert.True(keyRing.ContainsKey("previous-2026-09"));
        Assert.True(keyRing.ContainsKey("previous.2026.08"));
        Assert.Equal(["previous-2026-09", "previous.2026.08"], keyRing.PreviousVerificationKeyIds);
    }

    [Fact]
    public void Zero_previous_verification_keys_are_accepted()
    {
        using var active = RSA.Create(2048);
        using var keyRing = ManagedElsaHandoffKeyRing.CreateConfigured(new ManagedElsaHandoffOptions
        {
            ActiveKeyId = "active-2026-10",
            ActivePrivateKeyPem = active.ExportRSAPrivateKeyPem()
        });

        Assert.Equal("active-2026-10", keyRing.ActiveKeyId);
        Assert.Empty(keyRing.PreviousVerificationKeyIds);
    }

    [Fact]
    public void Host_binds_indexed_previous_verification_keys_from_platform_config()
    {
        using var active = RSA.Create(2048);
        using var previous = RSA.Create(2048);
        using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Enabled"] = "true",
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:Issuer"] = "https://cloud.example.test",
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActiveKeyId"] = "active-2026-10",
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:ActivePrivateKeyPem"] =
                active.ExportRSAPrivateKeyPem(),
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:PreviousVerificationKeys:0:KeyId"] =
                "previous-2026-09",
            [$"{ManagedElsaHandoffDefaults.ConfigurationSection}:PreviousVerificationKeys:0:PublicKeyPem"] =
                previous.ExportRSAPublicKeyPem()
        });

        var keyRing = app.Services.GetRequiredService<ManagedElsaHandoffKeyRing>();
        Assert.Equal("active-2026-10", keyRing.ActiveKeyId);
        Assert.True(keyRing.ContainsKey("previous-2026-09"));
    }

    [Fact]
    public void Malformed_key_identifier_fails_closed_without_echoing_the_value()
    {
        using var active = RSA.Create(2048);
        const string malformedId = "-----BEGIN PUBLIC KEY-----\nleak-me\n-----END PUBLIC KEY-----";
        var options = new ManagedElsaHandoffOptions
        {
            ActiveKeyId = malformedId,
            ActivePrivateKeyPem = active.ExportRSAPrivateKeyPem()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ManagedElsaHandoffKeyRing.CreateConfigured(options));

        Assert.Contains("not an allowed format", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("leak-me", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(malformedId, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Previous_private_key_material_is_rejected_without_echoing_pem()
    {
        using var active = RSA.Create(2048);
        using var previous = RSA.Create(2048);
        var privatePem = previous.ExportRSAPrivateKeyPem();
        var options = new ManagedElsaHandoffOptions
        {
            ActiveKeyId = "active-2026-10",
            ActivePrivateKeyPem = active.ExportRSAPrivateKeyPem(),
            PreviousVerificationKeys =
            [
                new ManagedElsaHandoffPreviousVerificationKey
                {
                    KeyId = "previous-2026-09",
                    PublicKeyPem = privatePem
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => ManagedElsaHandoffKeyRing.CreateConfigured(options));

        Assert.Contains("previous-2026-09", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privatePem, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_signed_with_the_previous_key_verify_during_the_overlap_window()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = CreateSignedFixture(previous, "previous-2026-09", clock);
        var token = fixture.Issue();

        using var overlap = CreateOverlapRing(active, "active-2026-10", previous, "previous-2026-09");
        using var overlapFixture = CreateRedeemFixture(overlap, fixture.Authorizer, clock);

        var result = await overlapFixture.RedeemAsync(token);

        Assert.True(result.Succeeded);
        Assert.Equal("previous-2026-09", new JwtSecurityTokenHandler().ReadJwtToken(token).Header.Kid);
        Assert.Equal("active-2026-10", overlap.ActiveKeyId);
    }

    [Fact]
    public async Task Tokens_signed_with_the_previous_key_are_rejected_after_that_key_is_removed()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = CreateSignedFixture(previous, "previous-2026-09", clock);
        var token = fixture.Issue();
        clock.Advance(TimeSpan.FromSeconds(30));

        using var retired = new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(active));
        using var retiredFixture = CreateRedeemFixture(retired, fixture.Authorizer, clock);

        var result = await retiredFixture.RedeemAsync(token);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
        Assert.False(retired.ContainsKey("previous-2026-09"));
        Assert.True(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo > clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Rehearsal_rejects_a_fresh_unexpired_code_signed_with_the_removed_key()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = CreateSignedFixture(previous, "previous-2026-09", clock);
        using var retired = new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(active));
        using var retiredFixture = CreateRedeemFixture(retired, fixture.Authorizer, clock);

        var token = fixture.Issue();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.True(jwt.ValidTo > clock.GetUtcNow().UtcDateTime);
        Assert.Equal("previous-2026-09", jwt.Header.Kid);
        var result = await retiredFixture.RedeemAsync(token);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
        Assert.False(retired.ContainsKey("previous-2026-09"));
    }

    [Fact]
    public async Task Tokens_signed_with_an_unknown_key_are_rejected()
    {
        using var trusted = RSA.Create(2048);
        using var unknown = RSA.Create(2048);
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var unknownFixture = CreateSignedFixture(unknown, "unknown-2026-10", clock);
        var token = unknownFixture.Issue();

        using var trustedRing = new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(trusted));
        using var trustedFixture = CreateRedeemFixture(trustedRing, unknownFixture.Authorizer, clock);

        var result = await trustedFixture.RedeemAsync(token);

        Assert.Equal(ManagedElsaHandoffRedeemFailure.InvalidToken, result.Failure);
        Assert.False(trustedRing.ContainsKey("unknown-2026-10"));
    }

    [Fact]
    public async Task Removing_a_retired_key_records_key_ids_only()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        using var overlap = CreateOverlapRing(active, "active-2026-10", previous, "previous-2026-09");
        using var retired = new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(active));
        var audit = new RecordingAuditSink();
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);

        await ManagedElsaHandoffKeyRingAudit.RecordTransitionAsync(audit, overlap, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ManagedElsaHandoffKeyRingAudit.RecordTransitionAsync(audit, retired, clock);

        var actions = audit.Events.Select(item => item.Action).ToArray();
        Assert.Contains("key_ring.active:active-2026-10", actions);
        Assert.Contains("key_ring.previous:previous-2026-09", actions);
        Assert.Contains("key_ring.retired:previous-2026-09", actions);
        Assert.Equal(2, actions.Count(action => action == "key_ring.active:active-2026-10"));
        Assert.All(audit.Events, item =>
        {
            Assert.DoesNotContain("BEGIN", item.Action, StringComparison.Ordinal);
            Assert.True(string.IsNullOrEmpty(item.Jti));
            Assert.Null(item.Audience);
        });
        Assert.Equal(["previous-2026-09"], ManagedElsaHandoffKeyIds.RetiredKeyIds(
            overlap.TrustedKeyIds, retired.TrustedKeyIds));
        Assert.All(audit.Events, item => Assert.NotNull(item.KeyRingSnapshotId));
        Assert.Equal(2, audit.Events.Select(item => item.KeyRingSnapshotId).Distinct().Count());
    }

    [Fact]
    public async Task Revoking_the_previous_signer_records_a_revoked_reason()
    {
        using var previousSigner = RSA.Create(2048);
        using var replacement = RSA.Create(2048);
        using var first = new ManagedElsaHandoffKeyRing("signer-2026-09", CloneRsa(previousSigner));
        using var second = new ManagedElsaHandoffKeyRing("signer-2026-10", CloneRsa(replacement));
        var audit = new RecordingAuditSink();
        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);

        await ManagedElsaHandoffKeyRingAudit.RecordTransitionAsync(audit, first, clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ManagedElsaHandoffKeyRingAudit.RecordTransitionAsync(audit, second, clock);

        Assert.Contains("key_ring.retired:signer-2026-09:revoked", audit.Events.Select(item => item.Action));
    }

    [Fact]
    public async Task Production_restart_without_the_previous_key_persists_a_retired_audit_row()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        await using var connection = new SqliteConnection(
            $"Data Source=file:handoff-key-ring-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await using (var setup = CreateCatalogContext(connection))
            await setup.Database.MigrateAsync();

        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using (var overlapRing = CreateOverlapRing(active, "active-2026-10", previous, "previous-2026-09"))
        await using (var overlapHost = CreateProductionKeyRingHost(connection, overlapRing, clock))
        {
            await overlapHost.GetRequiredService<ManagedElsaHandoffConfigurationValidator>()
                .StartAsync(CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        using (var retiredRing = new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(active)))
        await using (var retiredHost = CreateProductionKeyRingHost(connection, retiredRing, clock))
        {
            await retiredHost.GetRequiredService<ManagedElsaHandoffConfigurationValidator>()
                .StartAsync(CancellationToken.None);
        }

        await using var verify = CreateCatalogContext(connection);
        var store = new EfCoreManagedElsaHandoffStore(verify);
        var retired = await store.ListActionsStartingWithAsync(
            ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix);
        Assert.Equal(["key_ring.retired:previous-2026-09"], retired);
        var snapshot = await store.GetLatestKeyRingSnapshotActionsAsync(
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix,
            ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix);
        Assert.Equal(["key_ring.active:active-2026-10"], snapshot);
        Assert.DoesNotContain(snapshot, action => action.Contains("BEGIN", StringComparison.Ordinal));
        var snapshotIds = await verify.ManagedElsaHandoffAuditEvents
            .Where(x => x.KeyRingSnapshotId != null)
            .Select(x => x.KeyRingSnapshotId)
            .Distinct()
            .ToListAsync();
        Assert.Equal(2, snapshotIds.Count);
    }

    [Fact]
    public async Task Concurrent_replica_startups_persist_exactly_one_retired_row_per_removed_key()
    {
        using var previous = RSA.Create(2048);
        using var active = RSA.Create(2048);
        var databaseName = "handoff-key-ring-concurrent-" + Guid.NewGuid().ToString("N");
        await using var setupConnection = new SqliteConnection(
            $"Data Source=file:{databaseName};Mode=Memory;Cache=Shared;Default Timeout=30");
        await setupConnection.OpenAsync();
        await using (var setup = CreateCatalogContext(setupConnection))
            await setup.Database.MigrateAsync();

        var clock = new TestTimeProvider(DateTimeOffset.UtcNow);
        using (var overlapRing = CreateOverlapRing(active, "active-2026-10", previous, "previous-2026-09"))
        await using (var overlapHost = CreateProductionKeyRingHost(setupConnection, overlapRing, clock))
        {
            await overlapHost.GetRequiredService<ManagedElsaHandoffConfigurationValidator>()
                .StartAsync(CancellationToken.None);
        }

        var connections = Enumerable.Range(0, 4)
            .Select(_ => new SqliteConnection(
                $"Data Source=file:{databaseName};Mode=Memory;Cache=Shared;Default Timeout=30"))
            .ToArray();
        var rings = Enumerable.Range(0, 4)
            .Select(_ => new ManagedElsaHandoffKeyRing("active-2026-10", CloneRsa(active)))
            .ToArray();
        try
        {
            foreach (var connection in connections)
                await connection.OpenAsync();

            await Task.WhenAll(connections.Zip(rings, (connection, ring) => (connection, ring)).Select(async pair =>
            {
                await using var host = CreateProductionKeyRingHost(pair.connection, pair.ring, clock);
                await host.GetRequiredService<ManagedElsaHandoffConfigurationValidator>()
                    .StartAsync(CancellationToken.None);
            }));
        }
        finally
        {
            foreach (var ring in rings)
                ring.Dispose();
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }

        await using var verify = CreateCatalogContext(setupConnection);
        var store = new EfCoreManagedElsaHandoffStore(verify);
        var retired = await store.ListActionsStartingWithAsync(
            ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix);
        Assert.Equal(["key_ring.retired:previous-2026-09"], retired);
        var snapshot = await store.GetLatestKeyRingSnapshotActionsAsync(
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix,
            ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix);
        Assert.Equal(["key_ring.active:active-2026-10"], snapshot);
    }

    private static CatalogDbContext CreateCatalogContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    private static ServiceProvider CreateProductionKeyRingHost(
        SqliteConnection connection,
        ManagedElsaHandoffKeyRing keyRing,
        TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(keyRing);
        services.AddSingleton(Options.Create(new ManagedElsaHandoffOptions
        {
            Enabled = true,
            Issuer = "https://cloud.example.test",
            ActiveKeyId = keyRing.ActiveKeyId,
            ActivePrivateKeyPem = "configured"
        }));
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly)));
        services.AddScoped<EfCoreManagedElsaHandoffStore>();
        services.AddScoped<IManagedElsaHandoffAuditSink, EfCoreManagedElsaHandoffAuditSink>();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
        services.AddSingleton<ManagedElsaHandoffConfigurationValidator>();
        return services.BuildServiceProvider();
    }

    private static ManagedElsaHandoffKeyRing CreateOverlapRing(
        RSA active,
        string activeKeyId,
        RSA previous,
        string previousKeyId)
    {
        var previousPublic = RSA.Create();
        previousPublic.ImportParameters(previous.ExportParameters(includePrivateParameters: false));
        return new ManagedElsaHandoffKeyRing(activeKeyId, CloneRsa(active), [(previousKeyId, previousPublic)]);
    }

    private static RSA CloneRsa(RSA source)
    {
        var clone = RSA.Create();
        clone.ImportParameters(source.ExportParameters(includePrivateParameters: true));
        return clone;
    }

    private static HandoffPair CreateSignedFixture(RSA signingKey, string keyId, TestTimeProvider clock)
    {
        var keyRing = new ManagedElsaHandoffKeyRing(keyId, CloneRsa(signingKey));
        return CreatePair(keyRing, new FakeAuthorizer(Guid.NewGuid(), Guid.NewGuid()), clock);
    }

    private static HandoffPair CreateRedeemFixture(
        ManagedElsaHandoffKeyRing keyRing,
        FakeAuthorizer authorizer,
        TestTimeProvider clock) =>
        CreatePair(keyRing, authorizer, clock, disposeKeyRing: false);

    private static HandoffPair CreatePair(
        ManagedElsaHandoffKeyRing keyRing,
        FakeAuthorizer authorizer,
        TestTimeProvider clock,
        bool disposeKeyRing = true)
    {
        var options = Options.Create(new ManagedElsaHandoffOptions
        {
            Enabled = true,
            Issuer = "https://cloud.example.test",
            TokenLifetime = TimeSpan.FromMinutes(1)
        });
        var replayStore = new InMemoryManagedElsaHandoffReplayStore(clock);
        var audit = new RecordingAuditSink();
        var issuer = new ManagedElsaHandoffIssuer(options, keyRing, clock);
        var redeemer = new ManagedElsaHandoffRedeemer(options, keyRing, replayStore, authorizer, clock, audit);
        return new HandoffPair(clock, authorizer, issuer, redeemer, keyRing, disposeKeyRing);
    }

    private sealed class HandoffPair(
        TestTimeProvider clock,
        FakeAuthorizer authorizer,
        ManagedElsaHandoffIssuer issuer,
        ManagedElsaHandoffRedeemer redeemer,
        ManagedElsaHandoffKeyRing keyRing,
        bool disposeKeyRing) : IDisposable
    {
        public FakeAuthorizer Authorizer { get; } = authorizer;

        public string Issue() => issuer.Issue(
            new TrustedWorkspaceIdentity("https://idp.example.test", "subject", "User", "user@example.test"),
            new ManagedElsaHandoffRequest(
                Authorizer.OrganizationId,
                Authorizer.InstanceId,
                Authorizer.Audience,
                Authorizer.RedirectUri,
                Authorizer.CodeChallenge),
            Authorizer.Authorization,
            clock.GetUtcNow().AddHours(1)).Token;

        public Task<ManagedElsaHandoffRedeemResult> RedeemAsync(string token) =>
            redeemer.RedeemAsync(token, Authorizer.Audience, Authorizer.RedirectUri, CodeVerifier);

        public void Dispose()
        {
            if (disposeKeyRing)
                keyRing.Dispose();
        }
    }

    private sealed class FakeAuthorizer(Guid organizationId, Guid instanceId) : IManagedElsaHandoffAuthorizer
    {
        public Guid OrganizationId { get; } = organizationId;
        public Guid InstanceId { get; } = instanceId;
        public Guid AccountId { get; } = Guid.NewGuid();
        public string Audience { get; } = $"urn:elsa:instance:{instanceId:D}";
        public Uri RedirectUri { get; } = new($"https://managed.example.test/instances/{instanceId:D}/auth/callback");
        public string CodeChallenge { get; } = ManagedElsaHandoffIssuer.CreateCodeChallenge(CodeVerifier);

        public ManagedElsaHandoffAuthorization Authorization => new(
            AccountId,
            OrganizationId,
            InstanceId,
            Audience,
            RedirectUri,
            CodeChallenge,
            new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal),
            1,
            new HashSet<string>([ManagedElsaRuntimePermissionMapping.StructuredLogsRead], StringComparer.Ordinal));

        public ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
            TrustedWorkspaceIdentity identity,
            ManagedElsaHandoffRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ManagedElsaHandoffAuthorization?>(Authorization);

        public ValueTask<bool> IsStillAuthorizedAsync(
            ManagedElsaHandoffClaims claims,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }

    private sealed class RecordingAuditSink : IManagedElsaHandoffAuditSink
    {
        private readonly object _gate = new();
        public ConcurrentQueue<ManagedElsaHandoffAuditEvent> Events { get; } = new();

        public ValueTask RecordAsync(
            ManagedElsaHandoffAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Enqueue(auditEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordKeyRingTransitionAsync(
            string activeKeyId,
            IReadOnlyList<string> previousKeyIds,
            DateTimeOffset occurredAt,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var previous = ReadLastTrustedSnapshot();
                var previousKeyList = previousKeyIds
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var currentTrusted = previousKeyList
                    .Append(activeKeyId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (string.Equals(previous.ActiveKeyId, activeKeyId, StringComparison.Ordinal) &&
                    previous.TrustedKeyIds.ToHashSet(StringComparer.Ordinal).SetEquals(currentTrusted))
                    return ValueTask.CompletedTask;

                var snapshotId = Guid.NewGuid();
                var previousSnapshotId = Events
                    .Where(item =>
                        item.KeyRingSnapshotId is not null &&
                        item.Action.StartsWith(
                            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix, StringComparison.Ordinal))
                    .OrderByDescending(item => item.OccurredAt)
                    .ThenByDescending(item => item.KeyRingSnapshotId)
                    .Select(item => item.KeyRingSnapshotId)
                    .FirstOrDefault() ?? Guid.Empty;
                foreach (var keyId in ManagedElsaHandoffKeyIds.RetiredKeyIds(previous.TrustedKeyIds, currentTrusted))
                {
                    var signerRevoked = string.Equals(keyId, previous.ActiveKeyId, StringComparison.Ordinal);
                    Events.Enqueue(new ManagedElsaHandoffAuditEvent(
                        ManagedElsaHandoffKeyRingAudit.RetiredAction(keyId, signerRevoked),
                        "",
                        null,
                        null,
                        null,
                        null,
                        occurredAt,
                        KeyRingSnapshotId: snapshotId));
                }

                Events.Enqueue(new ManagedElsaHandoffAuditEvent(
                    ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix + activeKeyId,
                    "",
                    null,
                    null,
                    null,
                    null,
                    occurredAt,
                    KeyRingSnapshotId: snapshotId,
                    KeyRingPreviousSnapshotId: previousSnapshotId));
                foreach (var keyId in previousKeyList)
                {
                    Events.Enqueue(new ManagedElsaHandoffAuditEvent(
                        ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix + keyId,
                        "",
                        null,
                        null,
                        null,
                        null,
                        occurredAt,
                        KeyRingSnapshotId: snapshotId));
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ManagedElsaHandoffTrustedKeySnapshot> GetLastTrustedSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadLastTrustedSnapshot());

        private ManagedElsaHandoffTrustedKeySnapshot ReadLastTrustedSnapshot()
        {
            var loaded = Events
                .Where(item =>
                    item.Action.StartsWith(ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix, StringComparison.Ordinal) ||
                    item.Action.StartsWith(ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix, StringComparison.Ordinal))
                .ToArray();
            if (loaded.Length == 0)
                return ManagedElsaHandoffTrustedKeySnapshot.Empty;

            var withIds = loaded.Where(item => item.KeyRingSnapshotId is not null).ToArray();
            if (withIds.Length > 0)
            {
                var referenced = loaded
                    .Where(item => item.KeyRingPreviousSnapshotId is not null)
                    .Select(item => item.KeyRingPreviousSnapshotId!.Value)
                    .ToHashSet();
                var active = withIds
                    .Where(item => item.Action.StartsWith(
                        ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix, StringComparison.Ordinal))
                    .ToArray();
                var tip = active
                    .Where(item => !referenced.Contains(item.KeyRingSnapshotId!.Value))
                    .OrderByDescending(item => item.OccurredAt)
                    .ThenByDescending(item => item.KeyRingSnapshotId)
                    .FirstOrDefault()
                    ?? active
                        .OrderByDescending(item => item.OccurredAt)
                        .ThenByDescending(item => item.KeyRingSnapshotId)
                        .First();
                return ManagedElsaHandoffKeyRingAudit.ParseTrustedSnapshot(
                    withIds.Where(item => item.KeyRingSnapshotId == tip.KeyRingSnapshotId).Select(item => item.Action));
            }

            var latest = loaded.Max(item => item.OccurredAt);
            return ManagedElsaHandoffKeyRingAudit.ParseTrustedSnapshot(
                loaded.Where(item => item.OccurredAt == latest).Select(item => item.Action));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(ManagedElsaHandoffKeyRingTests);
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
