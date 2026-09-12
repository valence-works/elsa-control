using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.Deployment.Artifacts;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class DeploymentCommandPersistenceTests : IDisposable
{
    private readonly CatalogDbContext _db;
    private readonly DeploymentWorkspaceStore _store;
    private readonly Guid _workspaceId;
    private readonly Guid _accountId;

    public DeploymentCommandPersistenceTests()
    {
        _db = CreateDbContext();
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        var workspace = new Workspace { Name = "Runtime Command Workspace" };
        var account = new Account { DisplayName = "Runtime User", Email = "runtime@example.test" };
        _db.Workspaces.Add(workspace);
        _db.Accounts.Add(account);
        _db.SaveChanges();
        _workspaceId = workspace.Id;
        _accountId = account.Id;
        _store = new DeploymentWorkspaceStore(_db);
    }

    [Fact]
    public async Task Creating_run_creates_pending_command_for_target_engine()
    {
        var topology = await SeedTopologyAsync();

        var run = await QueueRunAsync(topology);
        var commands = await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow);

        Assert.Single(commands, x =>
            x.RunId == run.Id
            && x.Action == DeploymentCommandAction.Deploy
            && x.Revision!.RevisionId == topology.Revision.Id);
    }

    [Fact]
    public async Task Artifact_backed_revision_projects_artifact_reference_into_command()
    {
        var topology = await SeedTopologyAsync(createRevision: false);
        var artifact = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:payment-retry", "/tmp/payment-retry"));
        var desiredStateJson = $$"""
            {
              "records": [
                {
                  "kind": "ArtifactReference",
                  "name": "Payment Retry",
                  "payload": {
                    "artifactRecordId": "{{artifact.Id:D}}",
                    "artifactId": "{{artifact.ArtifactId}}",
                    "artifactTypeId": "{{artifact.ArtifactTypeId}}",
                    "contentDigest": {
                      "algorithm": "{{artifact.ContentDigest.Algorithm}}",
                      "value": "{{artifact.ContentDigest.Value}}"
                    }
                  }
                }
              ]
            }
            """;
        var revision = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(topology.Application.Id, topology.Environment.Id, "Artifact v1", "abc123", desiredStateJson, _accountId));

        await QueueRunAsync(topology with { Revision = revision });
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();

        Assert.NotNull(command.Artifact);
        Assert.Equal(artifact.Id, command.Artifact!.ArtifactRecordId);
        Assert.Equal(artifact.ArtifactId, command.Artifact.ArtifactId);
        Assert.Equal(ArtifactTypeIds.ElsaWorkflowDefinition, command.Artifact.ArtifactTypeId);
        Assert.Equal(artifact.ContentDigest, command.Artifact.ContentDigest);
    }

    [Fact]
    public async Task Multi_artifact_revision_projects_all_artifacts_into_safe_command_payload()
    {
        var topology = await SeedTopologyAsync(createRevision: false);
        var first = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:payment-retry", "/tmp/payment-retry"));
        var second = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:invoice-sync", "/tmp/invoice-sync"));
        var revision = await CreateArtifactBackedRevisionAsync(topology, first, second);

        await QueueRunAsync(topology with { Revision = revision });
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        var rawArtifactJson = await ReadCommandArtifactJsonAsync(command.Id);

        var commandArtifacts = command.Artifacts!;
        Assert.Equal(first.Id, command.Artifact!.ArtifactRecordId);
        Assert.Equal(2, commandArtifacts.Count());
        Assert.Equal(new[] { first.Id, second.Id }, commandArtifacts.Select(x => x.ArtifactRecordId));
        Assert.All(commandArtifacts, x => Assert.Null(x.DownloadUrl));
        Assert.DoesNotContain("/tmp/payment-retry", rawArtifactJson);
        Assert.DoesNotContain("/tmp/invoice-sync", rawArtifactJson);
    }

    [Fact]
    public async Task Artifact_backed_revision_rejects_missing_artifact_reference()
    {
        var topology = await SeedTopologyAsync(createRevision: false);
        var desiredStateJson = """
            {
              "records": [
                {
                  "kind": "ArtifactReference",
                  "name": "Payment Retry",
                  "payload": {
                    "artifactId": "sha256:missing"
                  }
                }
              ]
            }
            """;
        var revision = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(topology.Application.Id, topology.Environment.Id, "Missing artifact", "abc123", desiredStateJson, _accountId));

        var act = () => QueueRunAsync(topology with { Revision = revision });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal("Artifact-backed revision references an artifact that is not visible in the workspace.", exception.Message);
    }

    [Fact]
    public async Task Completing_artifact_command_rejects_observed_digest_mismatch()
    {
        var topology = await SeedTopologyAsync(createRevision: false);
        var artifact = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:payment-retry", "/tmp/payment-retry"));
        var revision = await CreateArtifactBackedRevisionAsync(topology, artifact);
        await QueueRunAsync(topology with { Revision = revision });
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);

        var complete = () => _store.CompleteCommandAsync(
            _workspaceId,
            command.Id,
            new CompleteDeploymentCommandRequest("lease-1", new WorkspaceArtifactDigest("sha256", "wrong"), "elsa://workflow/payment-retry", []),
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(complete);
        Assert.Equal("Observed artifact digest does not match command artifact digest.", exception.Message);
    }

    [Fact]
    public async Task Failing_command_preserves_per_artifact_outcomes_and_marks_run_failed()
    {
        var topology = await SeedTopologyAsync(createRevision: false);
        var first = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:payment-retry", "/tmp/payment-retry"));
        var second = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration("sha256:invoice-sync", "/tmp/invoice-sync"));
        var revision = await CreateArtifactBackedRevisionAsync(topology, first, second);
        var run = await QueueRunAsync(topology with { Revision = revision });
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);

        var failed = await _store.FailCommandAsync(
            _workspaceId,
            command.Id,
            new FailDeploymentCommandRequest(
                "lease-1",
                [new DeploymentCommandDiagnostic("apply-failed", DeploymentCommandDiagnosticSeverity.Error, "failed")],
                [
                    new DeploymentCommandArtifactOutcome(first.Id, DeploymentCommandArtifactStatus.Applied, first.ContentDigest, "elsa://workflow/payment-retry"),
                    new DeploymentCommandArtifactOutcome(second.Id, DeploymentCommandArtifactStatus.Failed, second.ContentDigest, null, [new DeploymentCommandDiagnostic("apply-failed", DeploymentCommandDiagnosticSeverity.Error, "runtime rejected")])
                ]),
            DateTimeOffset.UtcNow);
        var failedRun = await _store.GetRunAsync(_workspaceId, run.Id);

        var failedArtifacts = failed.Artifacts!;
        Assert.Equal(DeploymentCommandStatus.Failed, failed.Status);
        Assert.Equal(2, failedArtifacts.Count());
        Assert.Equal(DeploymentCommandArtifactStatus.Applied, failedArtifacts.Single(x => x.ArtifactRecordId == first.Id).Status);
        Assert.Equal("elsa://workflow/payment-retry", failedArtifacts.Single(x => x.ArtifactRecordId == first.Id).RuntimeReference);
        Assert.Equal(DeploymentCommandArtifactStatus.Failed, failedArtifacts.Single(x => x.ArtifactRecordId == second.Id).Status);
        Assert.Equal("runtime rejected", failedArtifacts.Single(x => x.ArtifactRecordId == second.Id).Diagnostics!.Single().Message);
        Assert.Equal(WorkspaceDeploymentRunStatus.Failed, failedRun!.Status);
    }

    [Fact]
    public async Task Claim_enforces_single_active_lease()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();

        var claimed = await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);
        var duplicate = () => _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-2", TimeSpan.FromMinutes(5)),
            "lease-2",
            DateTimeOffset.UtcNow);

        Assert.Equal(DeploymentCommandStatus.Claimed, claimed.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(duplicate);
    }

    [Fact]
    public async Task Claim_is_atomic_across_db_contexts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"elsa-control-command-claim-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using (var seedDb = CreateDbContext(connectionString))
            {
                await seedDb.Database.EnsureCreatedAsync();
                var workspace = new Workspace { Name = "Runtime Command Workspace" };
                var account = new Account { DisplayName = "Runtime User", Email = "runtime-concurrent@example.test" };
                seedDb.Workspaces.Add(workspace);
                seedDb.Accounts.Add(account);
                await seedDb.SaveChangesAsync();
                var seedStore = new DeploymentWorkspaceStore(seedDb);
                var topology = await SeedTopologyAsync(seedStore, workspace.Id, account.Id);
                await QueueRunAsync(seedStore, workspace.Id, account.Id, topology);
                var command = (await seedStore.PollPendingCommandsAsync(workspace.Id, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();

                await using var firstDb = CreateDbContext(connectionString);
                await using var secondDb = CreateDbContext(connectionString);
                var firstStore = new DeploymentWorkspaceStore(firstDb);
                var secondStore = new DeploymentWorkspaceStore(secondDb);
                var readyCount = 0;
                var readyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var firstClaim = StartClaimContender(firstStore, workspace.Id, topology.Engine.Id, command.Id, "worker-1", "lease-1");
                var secondClaim = StartClaimContender(secondStore, workspace.Id, topology.Engine.Id, command.Id, "worker-2", "lease-2");
                await readyGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                startGate.SetResult();
                var claims = await Task.WhenAll(firstClaim, secondClaim);

                Assert.Equal(1, claims.Count(x => x is not null));

                Task<DeploymentCommand?> StartClaimContender(
                    DeploymentWorkspaceStore store,
                    Guid contenderWorkspaceId,
                    Guid contenderEngineId,
                    Guid contenderCommandId,
                    string workerId,
                    string leaseToken) =>
                    Task.Run(async () =>
                    {
                        if (Interlocked.Increment(ref readyCount) == 2)
                            readyGate.SetResult();
                        await startGate.Task;
                        return await TryClaimAsync(store, contenderWorkspaceId, contenderEngineId, contenderCommandId, workerId, leaseToken);
                    });
            }
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Claim_rejects_command_before_available_at()
    {
        var topology = await SeedTopologyAsync();
        var run = await QueueRunAsync(topology);
        var now = DateTimeOffset.Parse("2026-05-28T10:00:00Z");
        var delayed = await _store.CreateCommandAsync(
            _workspaceId,
            new CreateDeploymentCommandRequest(
                run.Id,
                topology.Environment.Id,
                topology.Engine.Id,
                DeploymentCommandAction.Deploy,
                null,
                new DeploymentCommandRevisionReference(topology.Revision.Id),
                $"delayed-{Guid.NewGuid():N}",
                now.AddMinutes(5),
                null),
            now);

        var claim = () => _store.ClaimCommandAsync(
            _workspaceId,
            delayed.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            now);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(claim);
        Assert.Equal("Command is not available.", exception.Message);
    }

    [Fact]
    public async Task Heartbeat_refreshes_parent_run_worker_heartbeat()
    {
        var topology = await SeedTopologyAsync();
        var run = await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        var claimedAt = run.QueuedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddMinutes(4);
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(10)),
            "lease-1",
            claimedAt);

        await _store.HeartbeatCommandAsync(
            _workspaceId,
            command.Id,
            new DeploymentCommandHeartbeatRequest("lease-1", "worker-1"),
            heartbeatAt);
        var refreshed = await _store.GetRunAsync(_workspaceId, run.Id);

        Assert.Equal(heartbeatAt, refreshed!.WorkerHeartbeatAt);
        Assert.Equal("worker-1", refreshed.WorkerId);
    }

    [Fact]
    public async Task Redeploying_same_revision_creates_distinct_command_idempotency_keys()
    {
        var topology = await SeedTopologyAsync();

        await QueueRunAsync(topology);
        var firstCommand = Assert.Single(
            await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow));
        await _store.ClaimCommandAsync(
            _workspaceId,
            firstCommand.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);
        await _store.CompleteCommandAsync(
            _workspaceId,
            firstCommand.Id,
            new CompleteDeploymentCommandRequest("lease-1", new WorkspaceArtifactDigest("sha256", "observed"), null, []),
            DateTimeOffset.UtcNow);
        await QueueRunAsync(topology);
        var commands = await _db.DeploymentCommands
            .Where(x => x.WorkspaceId == _workspaceId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, commands.Count());
        Assert.Equal(commands.Select(x => x.IdempotencyKey).Count(), commands.Select(x => x.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task Completing_command_updates_run_history_and_deployed_revision()
    {
        var topology = await SeedTopologyAsync();
        var run = await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);

        var completed = await _store.CompleteCommandAsync(
            _workspaceId,
            command.Id,
            new CompleteDeploymentCommandRequest("lease-1", new WorkspaceArtifactDigest("sha256", "observed"), "elsa://workflow/payment-retry", []),
            DateTimeOffset.UtcNow);
        var completedRun = await _store.GetRunAsync(_workspaceId, run.Id);
        var history = await _store.GetRunHistoryAsync(_workspaceId, run.Id);
        _db.ChangeTracker.Clear();
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Equal(DeploymentCommandStatus.Completed, completed.Status);
        Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, completedRun!.Status);
        Assert.Contains(history, x => x.Message == "Runtime command completed.");
        Assert.Equal(topology.Revision.RevisionNumber, cockpit.Applications.Single().Environments.Single().DeployedRevision);
        var commandSummary = cockpit.History.Single().Commands.Single();
        Assert.Equal(command.Id, commandSummary.Id);
        Assert.Equal(DeploymentCommandStatus.Completed, commandSummary.Status);
        Assert.Null(commandSummary.ProgressMessage);
        Assert.Equal("elsa://workflow/payment-retry", commandSummary.RuntimeReference);
    }

    [Fact]
    public async Task Completed_command_requires_same_lease_for_idempotent_replay()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);
        await _store.CompleteCommandAsync(
            _workspaceId,
            command.Id,
            new CompleteDeploymentCommandRequest("lease-1", null, "elsa://workflow/payment-retry", []),
            DateTimeOffset.UtcNow);

        var replay = await _store.CompleteCommandAsync(
            _workspaceId,
            command.Id,
            new CompleteDeploymentCommandRequest("lease-1", null, "elsa://workflow/payment-retry", []),
            DateTimeOffset.UtcNow);
        var wrongLease = () => _store.CompleteCommandAsync(
            _workspaceId,
            command.Id,
            new CompleteDeploymentCommandRequest("lease-2", null, "elsa://workflow/payment-retry", []),
            DateTimeOffset.UtcNow);

        Assert.Equal(DeploymentCommandStatus.Completed, replay.Status);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(wrongLease);
        Assert.Equal("Command lease token is invalid.", exception.Message);
    }

    [Fact]
    public async Task Stale_command_recovery_marks_run_recovery_required()
    {
        var topology = await SeedTopologyAsync();
        var run = await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        var claimedAt = run.QueuedAt.AddSeconds(1);
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            claimedAt);

        var recovered = await _store.MarkStaleCommandsRecoveryRequiredAsync(claimedAt.AddMinutes(20), TimeSpan.FromMinutes(10));
        var recoveredRun = await _store.GetRunAsync(_workspaceId, run.Id);
        var recoveredCommand = await _store.GetCommandAsync(_workspaceId, command.Id);

        Assert.Equal(1, recovered);
        Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, recoveredRun!.Status);
        Assert.Equal(DeploymentCommandStatus.RecoveryRequired, recoveredCommand!.Status);
    }

    [Fact]
    public async Task Webhook_notification_persists_safe_trigger_metadata()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();

        var notification = await _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);
        await using var commandReader = _db.Database.GetDbConnection().CreateCommand();
        commandReader.CommandText = "SELECT SafePayloadJson FROM DeploymentCommandWebhookNotifications WHERE Id = $id";
        var parameter = commandReader.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = notification.Id;
        commandReader.Parameters.Add(parameter);
        var rawPayload = (string)(await commandReader.ExecuteScalarAsync())!;

        Assert.Equal(WebhookNotificationStatus.Pending, notification.Status);
        Assert.Equal("{\"reason\":\"command-available\"}", rawPayload);
    }

    [Fact]
    public async Task Webhook_dispatch_targets_include_runtime_endpoint_and_safe_payload()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        var notification = await _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);

        var targets = await _store.ListPendingWebhookNotificationTargetsAsync(10, DateTimeOffset.UtcNow);

        Assert.Single(targets, x =>
            x.Id == notification.Id
            && x.WorkspaceId == _workspaceId
            && x.EngineId == topology.Engine.Id
            && x.CommandId == command.Id
            && x.EngineBaseUrl == topology.Engine.BaseUrl
            && x.SafePayloadJson == "{\"reason\":\"command-available\"}");
    }

    [Fact]
    public async Task Webhook_dispatch_status_transitions_remove_notification_from_pending_targets()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        var sentAt = DateTimeOffset.Parse("2026-05-28T12:00:00Z");
        var sent = await _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);
        var failed = await _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);
        var skipped = await _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);

        var sentResult = await _store.MarkWebhookNotificationSentAsync(_workspaceId, sent.Id, sentAt);
        var failedResult = await _store.MarkWebhookNotificationFailedAsync(_workspaceId, failed.Id, sentAt);
        var skippedResult = await _store.MarkWebhookNotificationSkippedAsync(_workspaceId, skipped.Id, sentAt);
        var pendingTargets = await _store.ListPendingWebhookNotificationTargetsAsync(10, DateTimeOffset.UtcNow);

        Assert.Equal(WebhookNotificationStatus.Sent, sentResult.Status);
        Assert.Equal(sentAt, sentResult.SentAt);
        Assert.Equal(WebhookNotificationStatus.Failed, failedResult.Status);
        Assert.Equal(WebhookNotificationStatus.Skipped, skippedResult.Status);
        Assert.DoesNotContain(pendingTargets, x => x.Id == sent.Id || x.Id == failed.Id || x.Id == skipped.Id);
    }

    [Fact]
    public async Task Webhook_notification_rejects_wrong_engine()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();

        var create = () => _store.CreateWebhookNotificationAsync(
            _workspaceId,
            Guid.NewGuid(),
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(create);
        Assert.Equal("Command does not target the requested runtime engine.", exception.Message);
    }

    [Fact]
    public async Task Webhook_notification_rejects_non_pending_command()
    {
        var topology = await SeedTopologyAsync();
        await QueueRunAsync(topology);
        var command = (await _store.PollPendingCommandsAsync(_workspaceId, topology.Engine.Id, 10, DateTimeOffset.UtcNow)).Single();
        await _store.ClaimCommandAsync(
            _workspaceId,
            command.Id,
            new ClaimDeploymentCommandRequest(topology.Engine.Id, "worker-1", TimeSpan.FromMinutes(5)),
            "lease-1",
            DateTimeOffset.UtcNow);

        var create = () => _store.CreateWebhookNotificationAsync(
            _workspaceId,
            topology.Engine.Id,
            command.Id,
            "{\"reason\":\"command-available\"}",
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(create);
        Assert.Equal("Command is not pending.", exception.Message);
    }

    public void Dispose() => _db.Dispose();

    private async Task<WorkspaceDeploymentRun> QueueRunAsync(DeploymentTopology topology) =>
        await QueueRunAsync(_store, _workspaceId, _accountId, topology);

    private async Task<DeploymentTopology> SeedTopologyAsync(bool createRevision = true)
    {
        return await SeedTopologyAsync(_store, _workspaceId, _accountId, createRevision);
    }

    private async Task<WorkspaceDesiredStateRevision> CreateArtifactBackedRevisionAsync(
        DeploymentTopology topology,
        params WorkspaceArtifact[] artifacts)
    {
        var records = string.Join(",", artifacts.Select(ArtifactRecordJson));
        var desiredStateJson = $$"""
            {
              "records": [{{records}}]
            }
            """;
        return await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(topology.Application.Id, topology.Environment.Id, "Artifact v1", "abc123", desiredStateJson, _accountId));
    }

    private static string ArtifactRecordJson(WorkspaceArtifact artifact) =>
        $$"""
        {
          "kind": "ArtifactReference",
          "name": "{{artifact.ArtifactId}}",
          "payload": {
            "artifactRecordId": "{{artifact.Id:D}}",
            "artifactId": "{{artifact.ArtifactId}}",
            "artifactTypeId": "{{artifact.ArtifactTypeId}}",
            "contentDigest": {
              "algorithm": "{{artifact.ContentDigest.Algorithm}}",
              "value": "{{artifact.ContentDigest.Value}}"
            }
          }
        }
        """;

    private async Task<string> ReadCommandArtifactJsonAsync(Guid commandId)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT ArtifactJson FROM DeploymentCommands WHERE Id = $id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = commandId;
        command.Parameters.Add(parameter);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<WorkspaceDeploymentRun> QueueRunAsync(
        DeploymentWorkspaceStore store,
        Guid workspaceId,
        Guid accountId,
        DeploymentTopology topology) =>
        await store.CreateRunAsync(
            workspaceId,
            new QueueWorkspaceDeploymentRunRequest(
                topology.Revision.Id,
                topology.Environment.Id,
                topology.Engine.Id,
                Guid.NewGuid(),
                accountId,
                null),
            DateTimeOffset.UtcNow);

    private static async Task<DeploymentTopology> SeedTopologyAsync(
        DeploymentWorkspaceStore store,
        Guid workspaceId,
        Guid accountId,
        bool createRevision = true)
    {
        var application = await store.CreateApplicationAsync(workspaceId, new CreateWorkflowApplicationRequest("Claims", null, accountId));
        var environment = await store.CreateEnvironmentAsync(workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await store.RegisterEngineAsync(
            workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://runtime.example.test",
                "westeurope",
                "Azure Key Vault",
                "kv://claims/runtime",
                [new EngineCapability("workflow-definition.apply", "Apply workflow definitions", CapabilityBoundary.EngineApi)],
                [],
                "container-apps"));
        var revision = createRevision
            ? await store.CreateRevisionAsync(
                workspaceId,
                new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "v1", "abc123", "{\"records\":[]}", accountId))
            : null;
        return new DeploymentTopology(application, environment, engine, revision!);
    }

    private static async Task<DeploymentCommand?> TryClaimAsync(
        DeploymentWorkspaceStore store,
        Guid workspaceId,
        Guid engineId,
        Guid commandId,
        string workerId,
        string leaseToken)
    {
        try
        {
            return await store.ClaimCommandAsync(
                workspaceId,
                commandId,
                new ClaimDeploymentCommandRequest(engineId, workerId, TimeSpan.FromMinutes(5)),
                leaseToken,
                DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private RegisterWorkspaceArtifactRequest ArtifactRegistration(string artifactId, string reference) =>
        new(
            artifactId,
            ArtifactLayoutConstants.LayoutVersion,
            new WorkspaceArtifactDigest("sha256", artifactId.Replace("sha256:", "", StringComparison.Ordinal)),
            WorkspaceArtifactFormat.Folder,
            "local",
            reference,
            new WorkspaceArtifactManifestSummary("claims", "1.0.0", "prod"),
            [new WorkspaceArtifactResourceSummary("workflowDefinition", "payment-retry", null, "1", new WorkspaceArtifactDigest("sha256", "workflow-hash"))],
            [],
            _accountId,
            ArtifactEnvelopeConstants.EnvelopeVersion,
            ArtifactTypeIds.ElsaWorkflowDefinition);

    private static CatalogDbContext CreateDbContext()
    {
        return CreateDbContext("Data Source=:memory:");
    }

    private static CatalogDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connectionString)
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed record DeploymentTopology(
        WorkspaceDeploymentApplication Application,
        WorkspaceDeploymentEnvironment Environment,
        WorkspaceWorkflowEngine Engine,
        WorkspaceDesiredStateRevision Revision);
}
