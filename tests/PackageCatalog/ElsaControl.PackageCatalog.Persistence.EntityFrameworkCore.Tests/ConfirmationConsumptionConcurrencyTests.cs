using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ConfirmationConsumptionConcurrencyTests
{
    [Fact]
    public async Task Concurrent_consumers_across_contexts_allow_exactly_one_use()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var firstContext = new CatalogDbContext(database.Options);
            await using var secondContext = new CatalogDbContext(database.Options);
            var readBarrier = new AsyncBarrier(2);
            var firstService = new ConfirmationService(
                new ReadBarrierMutationStore(new DeploymentWorkspaceStore(firstContext), readBarrier),
                new StaticTimeProvider(database.Now));
            var secondService = new ConfirmationService(
                new ReadBarrierMutationStore(new DeploymentWorkspaceStore(secondContext), readBarrier),
                new StaticTimeProvider(database.Now));

            var results = await Task.WhenAll(
                firstService.ConsumeConfirmationAsync(
                    database.WorkspaceId,
                    database.ConfirmationId,
                    database.AccountId,
                    ConfirmationActionType.Deploy,
                    database.TargetId),
                secondService.ConsumeConfirmationAsync(
                    database.WorkspaceId,
                    database.ConfirmationId,
                    database.AccountId,
                    ConfirmationActionType.Deploy,
                    database.TargetId));

            Assert.Single(results, x => x.Succeeded);
            Assert.Single(results, x =>
                !x.Succeeded && x.Validation.Id == "deployment.confirmation.used");
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    [Fact]
    public async Task Conditional_use_rejects_a_stale_unused_snapshot_from_another_context()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var firstContext = new CatalogDbContext(database.Options);
            await using var secondContext = new CatalogDbContext(database.Options);
            var firstStore = new DeploymentWorkspaceStore(firstContext);
            var secondStore = new DeploymentWorkspaceStore(secondContext);

            var snapshots = await Task.WhenAll(
                firstStore.GetConfirmationAsync(database.WorkspaceId, database.ConfirmationId),
                secondStore.GetConfirmationAsync(database.WorkspaceId, database.ConfirmationId));
            var attempts = new[]
            {
                await firstStore.TryMarkConfirmationUsedAsync(database.WorkspaceId, database.ConfirmationId, database.Now),
                await secondStore.TryMarkConfirmationUsedAsync(database.WorkspaceId, database.ConfirmationId, database.Now)
            };

            Assert.All(snapshots.Select(x => x!.UsedAt), x => Assert.Null(x));
            Assert.All(attempts, Assert.NotNull);
            Assert.Single(attempts, x => x!.Consumed);
            Assert.Single(attempts, x => !x!.Consumed);
            Assert.All(attempts.Select(x => x!.Confirmation.UsedAt), x => Assert.Equal(database.Now, x));
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-control-confirmation-{Guid.NewGuid():N}.db");
        var options = CreateOptions(path);
        var now = DateTimeOffset.Parse("2026-07-16T10:00:00Z");
        var workspaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var targetId = Guid.NewGuid().ToString("D");

        await using var setupContext = new CatalogDbContext(options);
        await setupContext.Database.OpenConnectionAsync();
        await setupContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await setupContext.Database.EnsureCreatedAsync();
        setupContext.Accounts.Add(new Account
        {
            Id = accountId,
            DisplayName = "Confirmation owner",
            Email = "confirmation-owner@example.test"
        });
        setupContext.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Confirmation concurrency" });
        await setupContext.SaveChangesAsync();

        var confirmation = await new DeploymentWorkspaceStore(setupContext).CreateConfirmationAsync(
            workspaceId,
            new CreateActionConfirmationRequest(ConfirmationActionType.Deploy, targetId, accountId),
            now);

        return new TestDatabase(path, options, now, workspaceId, accountId, targetId, confirmation.Id);
    }

    private static DbContextOptions<CatalogDbContext> CreateOptions(string databasePath) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite($"Data Source={databasePath};Default Timeout=5")
            .Options;

    private static void DeleteDatabase(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete($"{databasePath}-shm");
        File.Delete($"{databasePath}-wal");
    }

    private sealed record TestDatabase(
        string Path,
        DbContextOptions<CatalogDbContext> Options,
        DateTimeOffset Now,
        Guid WorkspaceId,
        Guid AccountId,
        string TargetId,
        Guid ConfirmationId);

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AsyncBarrier(int participantCount)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participantCount)
                _completion.TrySetResult();

            return _completion.Task;
        }
    }

    private sealed class ReadBarrierMutationStore(
        IWorkspaceDeploymentMutationStore inner,
        AsyncBarrier readBarrier) : IWorkspaceDeploymentMutationStore
    {
        public Task<ActionConfirmation> CreateConfirmationAsync(
            Guid workspaceId,
            CreateActionConfirmationRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            inner.CreateConfirmationAsync(workspaceId, request, now, cancellationToken);

        public async Task<ActionConfirmation?> GetConfirmationAsync(
            Guid workspaceId,
            Guid confirmationId,
            CancellationToken cancellationToken = default)
        {
            var confirmation = await inner.GetConfirmationAsync(workspaceId, confirmationId, cancellationToken);
            await readBarrier.SignalAndWaitAsync();
            return confirmation;
        }

        public Task<ConfirmationUseAttempt?> TryMarkConfirmationUsedAsync(
            Guid workspaceId,
            Guid confirmationId,
            DateTimeOffset usedAt,
            CancellationToken cancellationToken = default) =>
            inner.TryMarkConfirmationUsedAsync(workspaceId, confirmationId, usedAt, cancellationToken);

        public Task<bool> HasActiveRunAsync(Guid workspaceId, Guid environmentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceDeploymentRun> CreateRunAsync(Guid workspaceId, QueueWorkspaceDeploymentRunRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceDeploymentRun?> GetRunAsync(Guid workspaceId, Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeploymentRunHistoryEvent>> GetRunHistoryAsync(Guid workspaceId, Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceDeploymentRun?> ClaimNextQueuedRunAsync(string workerId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceDeploymentRun> UpdateRunStatusAsync(
            Guid workspaceId,
            Guid runId,
            WorkspaceDeploymentRunStatus status,
            string message,
            DateTimeOffset now,
            string? failureMessage = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> MarkStaleRunningRunsRecoveryRequiredAsync(
            DateTimeOffset now,
            TimeSpan staleAfter,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RuntimeControlExecution> RecordRuntimeControlExecutionAsync(
            Guid workspaceId,
            RuntimeControlExecution execution,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
