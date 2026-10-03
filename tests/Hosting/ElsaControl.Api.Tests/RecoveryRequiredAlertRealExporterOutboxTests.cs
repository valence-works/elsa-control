using System.Collections.Concurrent;
using System.Net;
using Azure.Core;
using Azure.Core.Pipeline;
using ElsaControl.Api.Telemetry;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using CatalogWorkspace = ElsaControl.PackageCatalog.Core.Accounts.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.Api.Tests;

[Collection("Managed Azure Monitor export")]
public sealed class RecoveryRequiredAlertRealExporterOutboxTests : IDisposable
{
    private const string StatsbeatVariable = "APPLICATIONINSIGHTS_STATSBEAT_DISABLED";
    private readonly string? _originalStatsbeat = Environment.GetEnvironmentVariable(StatsbeatVariable);

    public RecoveryRequiredAlertRealExporterOutboxTests() =>
        Environment.SetEnvironmentVariable(StatsbeatVariable, "true");

    public void Dispose() =>
        Environment.SetEnvironmentVariable(StatsbeatVariable, _originalStatsbeat);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "00000000-0000-0000-0000-00000000002a")]
    [InlineData(HttpStatusCode.Unauthorized, "00000000-0000-0000-0000-00000000002b")]
    public async Task Real_exporter_http_failure_leaves_the_row_pending_then_succeeds_on_retry(
        HttpStatusCode failure,
        string instrumentationKey)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.Parse("2026-10-03T03:00:00Z");
        var operationId = await SeedPendingAlertAsync(db, now);
        db.ChangeTracker.Clear();
        using var handler = new ScriptedStatusHandler(failure, HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory().Create(
            new ManagedLifecycleAzureMonitorTelemetryOptions
            {
                Enabled = true,
                ConnectionString =
                    $"InstrumentationKey={instrumentationKey};IngestionEndpoint=https://westeurope-1.in.applicationinsights.azure.com/",
                ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
            },
            new StaticCredential(),
            new HttpClientTransport(client));
        var clock = new MutableTimeProvider(now);
        var sender = new ActivityRecoveryRequiredAlertSender(sink);
        var dispatcher = new EfCoreRecoveryRequiredAlertOutboxDispatcher(db, sender, clock);

        Assert.Equal(0, await dispatcher.DispatchPendingAsync());
        var pending = await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync();
        Assert.Null(pending.SentAt);
        Assert.Equal(1, pending.DeliveryAttempts);
        Assert.Equal(operationId, pending.OperationId);

        clock.Advance(RecoveryRequiredAlertBackoff.Delay(1));
        Assert.Equal(1, await new EfCoreRecoveryRequiredAlertOutboxDispatcher(db, sender, clock)
            .DispatchPendingAsync());
        var delivered = await db.ElsaInstanceRecoveryRequiredAlertOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(delivered.SentAt);
        Assert.Equal(2, handler.Statuses.Count);
    }

    private static async Task<Guid> SeedPendingAlertAsync(CatalogDbContext db, DateTimeOffset now)
    {
        var workspace = new CatalogWorkspace { Name = "Real exporter alert" };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = workspace.OrganizationId,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = int.MaxValue,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, new FixedTimeProvider(now));
        var accepted = await new ElsaInstanceLifecycleService(store, new FixedTimeProvider(now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Real exporter Elsa",
                $"real-exporter-{Guid.NewGuid():N}",
                new ElsaInstanceIntent(
                    new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
                    new ElsaApplicationIntent("combined", "starter", packagePolicy: "approved"),
                    new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"),
                    ElsaDesiredLifecycle.Running),
                $"create-{Guid.NewGuid():N}"));
        db.ElsaInstanceRecoveryRequiredAlertOutbox.Add(new ElsaInstanceRecoveryRequiredAlertOutboxEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = workspace.OrganizationId,
            WorkspaceId = workspace.Id,
            InstanceId = accepted.Instance.Id,
            OperationId = accepted.Operation.Id,
            AttemptNumber = accepted.Operation.AttemptNumber,
            RunId = null,
            DedupeIdentity = ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                workspace.Id,
                accepted.Instance.Id,
                accepted.Operation.Id,
                accepted.Operation.AttemptNumber),
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        return accepted.Operation.Id;
    }

    private static CatalogDbContext CreateMigratedContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
    }

    private sealed class StaticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-export-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ScriptedStatusHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpStatusCode> _remaining = new(statuses);
        public ConcurrentQueue<HttpStatusCode> Statuses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var status = _remaining.TryDequeue(out var next) ? next : HttpStatusCode.OK;
            Statuses.Enqueue(status);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
