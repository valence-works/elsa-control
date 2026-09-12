using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class DeploymentRunRecoveryConcurrencyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

    [Fact]
    public async Task Concurrent_stale_sweeps_mark_a_running_run_only_once()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var firstContext = new CatalogDbContext(database.Options);
            await using var secondContext = new CatalogDbContext(database.Options);
            var firstSweep = new DeploymentWorkspaceStore(firstContext)
                .MarkStaleRunningRunsRecoveryRequiredAsync(Now.AddMinutes(10), TimeSpan.FromMinutes(5));
            var secondSweep = new DeploymentWorkspaceStore(secondContext)
                .MarkStaleRunningRunsRecoveryRequiredAsync(Now.AddMinutes(10), TimeSpan.FromMinutes(5));

            var results = await Task.WhenAll(firstSweep, secondSweep);

            Assert.Equal(1, results.Sum());
            await using var verify = new CatalogDbContext(database.Options);
            var run = await verify.DeploymentRuns.SingleAsync(x => x.Id == database.RunId);
            Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, run.Status);
            Assert.Equal(1, await verify.DeploymentRunHistoryEvents.CountAsync(x =>
                x.RunId == database.RunId && x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired));
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    [Fact]
    public async Task Terminal_update_cannot_overwrite_a_recovery_required_run()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var recoveryContext = new CatalogDbContext(database.Options);
            var recoveryStore = new DeploymentWorkspaceStore(recoveryContext);
            Assert.Equal(1, await recoveryStore.MarkStaleRunningRunsRecoveryRequiredAsync(
                Now.AddMinutes(10), TimeSpan.FromMinutes(5)));

            await using var completionContext = new CatalogDbContext(database.Options);
            var completionStore = new DeploymentWorkspaceStore(completionContext);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => completionStore.UpdateRunStatusAsync(
                database.WorkspaceId,
                database.RunId,
                WorkspaceDeploymentRunStatus.Succeeded,
                "Late completion.",
                Now.AddMinutes(11)));

            Assert.Equal("Deployment run requires recovery before a terminal update can be applied.", exception.Message);
            await using var verify = new CatalogDbContext(database.Options);
            var run = await verify.DeploymentRuns.SingleAsync(x => x.Id == database.RunId);
            Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, run.Status);
            Assert.Null(run.CompletedAt);
            Assert.Equal(1, await verify.DeploymentRunHistoryEvents.CountAsync(x =>
                x.RunId == database.RunId && x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired));
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    [Fact]
    public async Task Direct_recovery_update_records_the_recovery_reason()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var context = new CatalogDbContext(database.Options);
            var store = new DeploymentWorkspaceStore(context);
            const string recoveryReason = "Deployment command requires recovery after stale runtime heartbeat.";

            await store.UpdateRunStatusAsync(
                database.WorkspaceId,
                database.RunId,
                WorkspaceDeploymentRunStatus.RecoveryRequired,
                recoveryReason,
                Now.AddMinutes(10));

            await using var verify = new CatalogDbContext(database.Options);
            var run = await verify.DeploymentRuns.SingleAsync(x => x.Id == database.RunId);
            Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, run.Status);
            Assert.Equal(recoveryReason, run.RecoveryReason);
            Assert.Null(run.CompletedAt);
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    [Fact]
    public async Task Completed_run_is_not_marked_recovery_when_stale_sweep_runs_after_completion()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using var completionContext = new CatalogDbContext(database.Options);
            var completionStore = new DeploymentWorkspaceStore(completionContext);
            await completionStore.UpdateRunStatusAsync(
                database.WorkspaceId,
                database.RunId,
                WorkspaceDeploymentRunStatus.Succeeded,
                "Deployment completed.",
                Now.AddMinutes(11));

            await using var recoveryContext = new CatalogDbContext(database.Options);
            var recoveryStore = new DeploymentWorkspaceStore(recoveryContext);
            Assert.Equal(0, await recoveryStore.MarkStaleRunningRunsRecoveryRequiredAsync(
                Now.AddMinutes(12), TimeSpan.FromMinutes(5)));

            await using var verify = new CatalogDbContext(database.Options);
            var run = await verify.DeploymentRuns.SingleAsync(x => x.Id == database.RunId);
            Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, run.Status);
            Assert.Equal(Now.AddMinutes(11), run.CompletedAt);
            Assert.Equal(0, await verify.DeploymentRunHistoryEvents.CountAsync(x =>
                x.RunId == database.RunId && x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired));
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    [Fact]
    public async Task Direct_recovery_update_cannot_regress_a_terminal_run()
    {
        var database = await CreateDatabaseAsync();

        try
        {
            await using (var completionContext = new CatalogDbContext(database.Options))
            {
                await new DeploymentWorkspaceStore(completionContext).UpdateRunStatusAsync(
                    database.WorkspaceId,
                    database.RunId,
                    WorkspaceDeploymentRunStatus.Succeeded,
                    "Deployment completed.",
                    Now.AddMinutes(11));
            }

            await using var recoveryContext = new CatalogDbContext(database.Options);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new DeploymentWorkspaceStore(recoveryContext).UpdateRunStatusAsync(
                    database.WorkspaceId,
                    database.RunId,
                    WorkspaceDeploymentRunStatus.RecoveryRequired,
                    "Recovery requested after completion.",
                    Now.AddMinutes(12)));

            Assert.Equal("A terminal deployment run cannot be moved into recovery.", exception.Message);
            await using var verify = new CatalogDbContext(database.Options);
            var run = await verify.DeploymentRuns.SingleAsync(x => x.Id == database.RunId);
            Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, run.Status);
            Assert.Equal(Now.AddMinutes(11), run.CompletedAt);
            Assert.DoesNotContain(await verify.DeploymentRunHistoryEvents.Where(x => x.RunId == database.RunId).ToListAsync(),
                x => x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired);
        }
        finally
        {
            DeleteDatabase(database.Path);
        }
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-control-run-recovery-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite($"Data Source={path};Default Timeout=30")
            .Options;
        var workspaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await using var setup = new CatalogDbContext(options);
        await setup.Database.OpenConnectionAsync();
        await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await setup.Database.EnsureCreatedAsync();
        setup.Accounts.Add(new Account
        {
            Id = accountId,
            DisplayName = "Run recovery owner",
            Email = "run-recovery-owner@example.test"
        });
        setup.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Run recovery concurrency" });
        await setup.SaveChangesAsync();

        var store = new DeploymentWorkspaceStore(setup);
        var application = await store.CreateApplicationAsync(
            workspaceId,
            new CreateWorkflowApplicationRequest("Run recovery app", null, accountId));
        var environment = await store.CreateEnvironmentAsync(
            workspaceId,
            new CreateDeploymentEnvironmentRequest(application.Id, "Production", EnvironmentTier.Production));
        var engine = await store.RegisterEngineAsync(
            workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "run-recovery-engine",
                "https://engine.example.test",
                null,
                "Azure Key Vault",
                "kv://run-recovery/engine",
                [],
                [],
                null));
        var revision = await store.CreateRevisionAsync(
            workspaceId,
            new CreateDesiredStateRevisionRequest(
                application.Id,
                environment.Id,
                "Baseline",
                "run-recovery",
                "{\"records\":[]}",
                accountId));
        var confirmation = await store.CreateConfirmationAsync(
            workspaceId,
            new CreateActionConfirmationRequest(
                ConfirmationActionType.Deploy,
                revision.Id.ToString("D"),
                accountId),
            Now);
        var run = await store.CreateRunAsync(
            workspaceId,
            new QueueWorkspaceDeploymentRunRequest(
                revision.Id,
                environment.Id,
                engine.Id,
                confirmation.Id,
                accountId),
            Now);
        await store.ClaimNextQueuedRunAsync("run-recovery-worker", Now.AddMinutes(1));

        return new TestDatabase(path, options, workspaceId, run.Id);
    }

    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete($"{path}-shm");
        File.Delete($"{path}-wal");
    }

    private sealed record TestDatabase(
        string Path,
        DbContextOptions<CatalogDbContext> Options,
        Guid WorkspaceId,
        Guid RunId);
}
