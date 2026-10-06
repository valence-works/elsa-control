using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Retry_evidence_without_replacement_is_retained_only_for_the_same_diagnostic(
        bool aggregateChanges, bool sameDiagnostic)
    {
        await using var fixture = await RetryEvidenceScenario.CreateAsync();
        var first = await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, withEvidence: true);
        var initial = await fixture.ReadOperationAsync();
        var code = sameDiagnostic
            ? ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying
            : ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator;

        var second = await fixture.ObserveAsync(code, withEvidence: false, aggregateChanges);
        var current = await fixture.ReadOperationAsync();

        Assert.Equal(code, current.ReconciliationDiagnosticCode);
        Assert.Equal(aggregateChanges, second.Projection.InstanceVersion > first.Projection.InstanceVersion);
        Assert.Equal(sameDiagnostic ? initial.ReconciliationRetryEvidenceReference : null,
            current.ReconciliationRetryEvidenceReference);
        Assert.Equal(sameDiagnostic ? initial.ReconciliationRetryEvidenceDigest : null,
            current.ReconciliationRetryEvidenceDigest);
        Assert.Equal(sameDiagnostic, ElsaInstanceProviderReconciliationService.HasRecoverableResumeEvidence(
            current.FailureCode, current.ReconciliationDiagnosticCode,
            current.ReconciliationRetryEvidenceReference, current.ReconciliationRetryEvidenceDigest));

        // The topology read model exposes safe current diagnostics, never retry locators.
        var topology = await new EfCoreManagedElsaInstanceApiStore(fixture.Db)
            .GetLifecycleTopologyAsync(fixture.WorkspaceId, fixture.InstanceId);
        Assert.NotNull(topology);
        Assert.DoesNotContain(initial.ReconciliationRetryEvidenceReference!, JsonSerializer.Serialize(topology));
        if (sameDiagnostic)
            Assert.Equal(ElsaInstanceOperationState.Queued, (await fixture.RecoverAsync()).Operation.State);
        else
        {
            var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(fixture.RecoverAsync);
            Assert.Equal("Provider reconciliation has not established that retry is safe.", conflict.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_evidence_complete_replacement_belongs_to_the_new_diagnostic(bool aggregateChanges)
    {
        await using var fixture = await RetryEvidenceScenario.CreateAsync();
        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, withEvidence: true);
        var initial = await fixture.ReadOperationAsync();

        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator,
            withEvidence: true, aggregateChanges);
        var current = await fixture.ReadOperationAsync();

        Assert.NotNull(current.ReconciliationRetryEvidenceReference);
        Assert.NotNull(current.ReconciliationRetryEvidenceDigest);
        Assert.NotEqual(initial.ReconciliationRetryEvidenceReference, current.ReconciliationRetryEvidenceReference);
        Assert.NotEqual(initial.ReconciliationRetryEvidenceDigest, current.ReconciliationRetryEvidenceDigest);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator, current.ReconciliationDiagnosticCode);
        Assert.Equal(ElsaInstanceOperationState.Queued, (await fixture.RecoverAsync()).Operation.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_evidence_is_cleared_on_exhaustion_but_the_recover_exemption_remains(bool aggregateChanges)
    {
        await using var fixture = await RetryEvidenceScenario.CreateAsync();
        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, withEvidence: true);

        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
            withEvidence: false, aggregateChanges);
        var current = await fixture.ReadOperationAsync();

        Assert.Null(current.ReconciliationRetryEvidenceReference);
        Assert.Null(current.ReconciliationRetryEvidenceDigest);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, current.ReconciliationDiagnosticCode);
        Assert.Equal(ElsaInstanceOperationState.Queued, (await fixture.RecoverAsync()).Operation.State);
    }

    [Fact]
    public async Task Retry_evidence_is_cleared_when_a_later_observation_fails_without_evidence()
    {
        await using var fixture = await RetryEvidenceScenario.CreateAsync();
        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, withEvidence: true);

        await fixture.ObserveAsync(ElsaInstanceProviderReconciliationService.FailedCode,
            withEvidence: false, observed: ElsaObservedLifecycle.Failed);
        var current = await fixture.ReadOperationAsync();

        Assert.Equal(ElsaInstanceOperationState.Failed, current.State);
        Assert.Null(current.ReconciliationRetryEvidenceReference);
        Assert.Null(current.ReconciliationRetryEvidenceDigest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_evidence_partial_replacement_never_combines_with_the_old_pair(bool referenceOnly)
    {
        await using var fixture = await RetryEvidenceScenario.CreateAsync();
        await fixture.ObserveAsync(ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, withEvidence: true);

        await fixture.CommitPartialEvidenceAsync(referenceOnly);
        var current = await fixture.ReadOperationAsync();

        Assert.Null(current.ReconciliationRetryEvidenceReference);
        Assert.Null(current.ReconciliationRetryEvidenceDigest);
        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(fixture.RecoverAsync);
    }

    private sealed class RetryEvidenceScenario(
        SqliteConnection connection, CatalogDbContext db, Guid workspaceId, Guid instanceId, Guid operationId) : IAsyncDisposable
    {
        public CatalogDbContext Db { get; } = db;
        public Guid WorkspaceId { get; } = workspaceId;
        public Guid InstanceId { get; } = instanceId;
        private int observationNumber;

        public static async Task<RetryEvidenceScenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            CatalogDbContext? db = null;
            try
            {
                await connection.OpenAsync();
                db = CreateMigratedContext(connection);
                await db.Database.MigrateAsync();
                var (workspace, accepted) = await QueueManagedLifecycleRunAsync(db, "Retry evidence current diagnostic");
                var runs = new DeploymentWorkspaceStore(db);
                Assert.NotNull(await runs.ClaimNextQueuedRunAsync("deployment-worker", Now));
                Assert.Equal(1, await runs.MarkStaleRunningRunsRecoveryRequiredAsync(
                    Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
                db.ChangeTracker.Clear();
                return new(connection, db, workspace.Id, accepted.Instance.Id, accepted.Operation.Id);
            }
            catch
            {
                if (db is not null)
                    await db.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<ElsaInstanceProviderReconciliationResult> ObserveAsync(
            string code, bool withEvidence, bool aggregateChanges = false, ElsaObservedLifecycle? observed = null)
        {
            observationNumber++;
            var correlation = $"retry-evidence-{observationNumber}";
            var evidence = withEvidence
                ? new ElsaInstanceProviderRetryEvidence("https://evidence.example/retry/" + correlation,
                    "sha256:" + new string((char)('a' + observationNumber), 64))
                : null;
            var observation = new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Confirmed,
                observed ?? (aggregateChanges ? ElsaObservedLifecycle.Updating : ElsaObservedLifecycle.Provisioning),
                ElsaInstanceProviderHealthGate.Unknown, correlation, evidence) { ReasonCode = code };
            return new ElsaInstanceProviderReconciliationService(CreateStore(Db), new QueueProviderPort(observation),
                new FixedTimeProvider(Now.AddMinutes(10 + observationNumber)))
                .ReconcileAsync(WorkspaceId, operationId);
        }

        public async Task CommitPartialEvidenceAsync(bool referenceOnly)
        {
            var store = CreateStore(Db);
            var target = await store.GetTargetAsync(WorkspaceId, operationId);
            Assert.NotNull(target);
            await store.CommitAsync(new(
                WorkspaceId, InstanceId, operationId, target.Instance.Version, target.Operation.AttemptNumber,
                target.ReconciliationVersion, new string('f', 64), target.Instance, target.Operation,
                ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying, false,
                referenceOnly ? "https://evidence.example/retry/partial" : null,
                referenceOnly ? null : "sha256:" + new string('e', 64), Now.AddMinutes(16)));
        }

        public Task<ElsaInstanceOperationEntity> ReadOperationAsync()
        {
            Db.ChangeTracker.Clear();
            return Db.ElsaInstanceOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        }

        public async Task<ElsaInstanceLifecycleAcceptance> RecoverAsync()
        {
            var store = CreateStore(Db);
            var instance = await store.GetInstanceAsync(WorkspaceId, InstanceId);
            Assert.NotNull(instance);
            return await new ElsaInstanceLifecycleService(store, new FixedTimeProvider(Now.AddMinutes(20)))
                .RecoverAsync(new(WorkspaceId, InstanceId, instance.Version, "recover-current-evidence"));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
