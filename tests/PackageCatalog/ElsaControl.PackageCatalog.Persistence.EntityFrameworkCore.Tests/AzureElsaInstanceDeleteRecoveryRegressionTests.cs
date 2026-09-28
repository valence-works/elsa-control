using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [PosixFact]
    public async Task Provider_delete_recovery_reobserves_the_same_operation_and_finishes_lifecycle_delete()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setupDb = CreateMigratedContext(connection);
        await setupDb.Database.MigrateAsync();

        var (workspace, accepted) = await QueueManagedLifecycleRunAsync(
            setupDb, "Provider delete recovery workspace");
        await CompleteManagedRunAsync(setupDb, accepted.Operation.Id, accepted.Instance.Id);

        var scope = new AzureProviderTargetScope(
            "11111111-1111-1111-1111-111111111111",
            "rg-delete-recovery",
            "22222222-2222-2222-2222-222222222222",
            "registry-rg",
            "valenceruntimeimages",
            "westeurope");
        await using var tools = await CreateCleanupToolsAsync(scope, accepted.Instance.Id);
        var runnerOptions = tools.Options;
        var providerOptions = new AzureElsaInstanceProviderOptions
        {
            Enabled = true,
            TemplateFingerprint = runnerOptions.ComputeTemplateAuthorityFingerprint(),
            ProviderScopeFingerprint = runnerOptions.ComputeProviderScopeFingerprint(scope),
            SubscriptionId = scope.SubscriptionId,
            ResourceGroupNamePrefix = "rg-delete-recovery"
        };

        await using (var providerDb = CreateMigratedContext(connection))
        {
            var operationStore = new AzureProviderOperationStore(providerDb);
            var provider = CreateProvider(operationStore, providerOptions, Now.AddMinutes(2));
            var run = await providerDb.DeploymentRuns.AsNoTracking()
                .SingleAsync(x => x.ElsaInstanceId == accepted.Instance.Id);
            var target = new ElsaInstanceLifecycleDeploymentTarget(
                run.ApplicationId,
                run.EnvironmentId,
                run.EngineId,
                run.SourceRevisionId,
                run.ConfirmationId,
                run.ActorAccountId);
            var resolved = AzureProviderResolution(workspace.Id, accepted.Instance.Id);
            var submission = await provider.SubmitAsync(new(
                workspace.Id,
                accepted.Instance.Id,
                accepted.Operation.Id,
                accepted.Operation.AttemptNumber,
                ElsaDesiredLifecycle.Running,
                resolved.Plan!,
                target,
                scope.Location,
                workspace.OrganizationId,
                ElsaInstanceOperationAction.Reconcile,
                accepted.Operation.Id.ToString("D")));
            Assert.False(submission.Replayed);

            var reconcile = Assert.IsType<AzureProviderOperation>(await operationStore.GetLatestReconcileAsync(
                workspace.Id,
                $"e{accepted.Instance.Id:N}"[..16],
                providerOptions.ProviderScopeFingerprint));
            var assignmentId = Assert.IsType<Guid>(reconcile.ProviderAssignmentId);
            var assignment = Assert.IsType<AzureProviderResourceAssignment>(await
                ((IAzureProviderResourceAssignmentStore)operationStore).GetAsync(workspace.Id, assignmentId));
            var claimed = Assert.IsType<AzureProviderOperation>(await operationStore.ClaimAsync(
                workspace.Id,
                reconcile.Id,
                "delete-recovery-setup",
                "delete-recovery-setup-lease",
                TimeSpan.FromMinutes(5),
                Now.AddMinutes(2),
                reconcile.Version));
            var checkpointed = Assert.IsType<AzureProviderOperation>(await operationStore.CheckpointAsync(
                workspace.Id,
                reconcile.Id,
                "delete-recovery-setup-lease",
                new(
                    AzureProviderOperationPhase.HealthVerified,
                    "health.verified",
                    "The retained provider workload is healthy.",
                    new(ResourceGroupName: assignment.ResourceGroupName),
                    "https://runtime.example.test",
                    AzureProviderHealth.Healthy,
                    []),
                Now.AddMinutes(2),
                claimed.Version));
            Assert.Equal(
                AzureProviderOperationStatus.Succeeded,
                (await operationStore.FinalizeAsync(
                    workspace.Id,
                    reconcile.Id,
                    "delete-recovery-setup-lease",
                    AzureProviderOperationStatus.Succeeded,
                    "operation.succeeded",
                    Now.AddMinutes(2),
                    checkpointed.Version))?.Status);

            // Bind the lifecycle instance to the exact durable assignment created by the real
            // provider submission. This is valid setup, not a foreign/local-binding failure.
            var instance = await providerDb.ElsaInstances.SingleAsync(x => x.Id == accepted.Instance.Id);
            instance.PlacementAssignmentId = assignment.Id.ToString("D");
            instance.Version++;
            await providerDb.SaveChangesAsync();
        }

        Guid deleteOperationId;
        ElsaInstanceLifecycleAcceptance deletion;
        await using (var deleteDb = CreateMigratedContext(connection))
        {
            var current = Assert.IsType<ElsaInstance>(await CreateStore(deleteDb)
                .GetInstanceAsync(workspace.Id, accepted.Instance.Id));
            deletion = await new ElsaInstanceLifecycleService(
                    CreateStore(deleteDb), new FixedTimeProvider(Now.AddMinutes(3)))
                .DeleteAsync(await CreateConfirmedDeleteRequestAsync(
                    deleteDb,
                    workspace.Id,
                    accepted.Instance.Id,
                    current.Version,
                    "provider-delete-recovery",
                    Now.AddMinutes(3)));

            // The actual lifecycle deletion worker creates the one durable provider Delete
            // operation, then defers while that operation is accepted.
            var operationStore = new AzureProviderOperationStore(deleteDb);
            var pending = await new ElsaInstanceDeletionWorker(
                    new EfCoreElsaInstanceLifecycleStore(deleteDb, EmptyResolutionInputSource.Instance,
                        new FixedTimeProvider(Now.AddMinutes(3))),
                    CreateProvider(operationStore, providerOptions, Now.AddMinutes(3)),
                    new FixedTimeProvider(Now.AddMinutes(3)))
                .ProcessAvailableAsync("delete-recovery-worker");
            Assert.Empty(pending.Results);
            deleteOperationId = (await deleteDb.AzureProviderOperations.AsNoTracking()
                .SingleAsync(x => x.Action == AzureProviderOperationAction.Delete)).Id;
        }

        await using (var providerDb = CreateMigratedContext(connection))
        {
            var operationStore = new AzureProviderOperationStore(providerDb);
            var processed = await CreateProviderWorker(
                    operationStore,
                    runnerOptions,
                    scope,
                    Now.AddMinutes(4))
                .ProcessOnceAsync();

            Assert.Equal(1, processed);
            var uncertain = await operationStore.GetAsync(workspace.Id, deleteOperationId);
            Assert.NotNull(uncertain);
            Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, uncertain!.Status);
            Assert.Equal(AzureProviderOperationPhase.CleanupSubmitted, uncertain.Phase);
            Assert.Equal(AzureProviderRunnerStep.Cleanup, uncertain.AttemptedStep);
            Assert.Contains("group delete", await tools.ReadLogAsync(), StringComparison.Ordinal);
        }

        await using (var lifecycleDb = CreateMigratedContext(connection))
        {
            var operationStore = new AzureProviderOperationStore(lifecycleDb);
            var observed = await new ElsaInstanceDeletionWorker(
                    new EfCoreElsaInstanceLifecycleStore(lifecycleDb, EmptyResolutionInputSource.Instance,
                        new FixedTimeProvider(Now.AddMinutes(5))),
                    CreateProvider(operationStore, providerOptions, Now.AddMinutes(5)),
                    new FixedTimeProvider(Now.AddMinutes(5)))
                .ProcessAvailableAsync("delete-recovery-worker");
            var result = Assert.Single(observed.Results);
            Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, result.Outcome);
            Assert.Equal(ElsaInstanceOperationState.RecoveryRequired,
                (await lifecycleDb.ElsaInstanceOperations.AsNoTracking()
                    .SingleAsync(x => x.Id == deletion.Operation.Id)).State);

            var current = Assert.IsType<ElsaInstance>(await CreateStore(lifecycleDb)
                .GetInstanceAsync(workspace.Id, accepted.Instance.Id));
            var recovered = await new ElsaInstanceLifecycleService(
                    CreateStore(lifecycleDb), new FixedTimeProvider(Now.AddMinutes(6)))
                .RecoverAsync(new(
                    workspace.Id,
                    accepted.Instance.Id,
                    current.Version,
                    "provider-delete-recovery-resume"));
            Assert.Equal(deletion.Operation.Id, recovered.Operation.Id);
            Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
            Assert.Equal(deletion.Operation.AttemptNumber + 1, recovered.Operation.AttemptNumber);
            var recoveryRow = await lifecycleDb.ElsaInstanceRecoveryRequests.AsNoTracking()
                .SingleAsync(x => x.OperationId == deletion.Operation.Id);
            Assert.True(AzureProviderDeleteRecoveryAuthority.TryParse(recoveryRow.AzureDeleteRecoveryAuthority,
                out var capturedAuthority));
            Assert.Equal(recovered.Instance.Version, capturedAuthority!.InstanceVersion);
        }

        // Ordinary provider polling must never authorize recovery, even after the lifecycle
        // request is accepted. Only the deletion path may consume that durable authority.
        await using (var resumedProviderDb = CreateMigratedContext(connection))
        {
            var operationStore = new AzureProviderOperationStore(resumedProviderDb);
            var resumed = await CreateProviderWorker(
                    operationStore,
                    runnerOptions,
                    scope,
                    Now.AddMinutes(7))
                .ProcessOnceAsync();
            Assert.Equal(0, resumed);
        }

        await using (var finalizeDb = CreateMigratedContext(connection))
        {
            await using var recoveryDb = CreateMigratedContext(connection);
            var recoveryStore = new AzureProviderOperationStore(recoveryDb);
            var recoveryProvider = CreateProvider(recoveryStore, providerOptions, Now.AddMinutes(8),
                CreateDeleteRecoveryExecutor(recoveryStore, runnerOptions, scope, Now.AddMinutes(8)));
            var operationStore = new AzureProviderOperationStore(finalizeDb);
            var deletionWorker = new ElsaInstanceDeletionWorker(
                    new EfCoreElsaInstanceLifecycleStore(finalizeDb, EmptyResolutionInputSource.Instance,
                        new FixedTimeProvider(Now.AddMinutes(8))),
                    CreateProvider(operationStore, providerOptions, Now.AddMinutes(8)),
                    new FixedTimeProvider(Now.AddMinutes(8)),
                    recoveryProvider);
            var completed = await deletionWorker.ProcessAvailableAsync("delete-recovery-worker");
            var result = Assert.Single(completed.Results);
            Assert.True(result.Outcome == ElsaInstanceLifecycleWorkerOutcome.Deleted, result.FailureCode);
            var resumedOperation = await operationStore.GetAsync(workspace.Id, deleteOperationId);
            Assert.Equal(AzureProviderOperationStatus.Succeeded, resumedOperation?.Status);
            Assert.Single(await finalizeDb.AzureProviderOperations.AsNoTracking()
                .Where(x => x.Action == AzureProviderOperationAction.Delete).ToArrayAsync());
            var commandLines = (await tools.ReadLogAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(1, commandLines.Count(line =>
                line.StartsWith("group delete ", StringComparison.Ordinal)));
            Assert.Equal(8, commandLines.Length);
            Assert.Equal(3, commandLines.Count(line => line.StartsWith("group exists ", StringComparison.Ordinal)));
            Assert.Equal(2, commandLines.Count(line => line.StartsWith("keyvault list-deleted ", StringComparison.Ordinal)));
            Assert.Equal(ElsaObservedLifecycle.Deleted,
                (await finalizeDb.ElsaInstances.AsNoTracking()
                    .SingleAsync(x => x.Id == accepted.Instance.Id)).ObservedLifecycle);
            Assert.Null((await finalizeDb.DeploymentEnvironments.AsNoTracking()
                .SingleAsync(x => x.WorkspaceId == workspace.Id)).ElsaInstanceId);
            Assert.Equal(1, await finalizeDb.ElsaInstanceAuditEvents.CountAsync(x =>
                x.InstanceId == accepted.Instance.Id && x.EventType == "lifecycle.deleted"));
            Assert.Empty((await deletionWorker.ProcessAvailableAsync("delete-recovery-worker")).Results);
            Assert.Equal(commandLines, (await tools.ReadLogAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal(1, await finalizeDb.ElsaInstanceAuditEvents.CountAsync(x =>
                x.InstanceId == accepted.Instance.Id && x.EventType == "lifecycle.deleted"));
        }
    }

    private static AzureProviderOperationWorker CreateProviderWorker(
        AzureProviderOperationStore operationStore,
        AzureProviderRunnerOptions runnerOptions,
        AzureProviderTargetScope scope,
        DateTimeOffset now)
    {
        return new(
            operationStore,
            CreateDeleteRecoveryExecutor(operationStore, runnerOptions, scope, now),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(now));
    }

    private static AzureProviderExecutor CreateDeleteRecoveryExecutor(
        AzureProviderOperationStore operationStore,
        AzureProviderRunnerOptions runnerOptions,
        AzureProviderTargetScope scope,
        DateTimeOffset now) => new(
        operationStore,
        new AzureBicepProviderRunner(runnerOptions, scope),
        new FixedTimeProvider(now),
        leaseDuration: TimeSpan.FromMinutes(5),
        workerId: "delete-recovery-provider-worker",
        assignmentStore: operationStore);

    private static AzureElsaInstanceProvider CreateProvider(
        AzureProviderOperationStore operationStore,
        AzureElsaInstanceProviderOptions options,
        DateTimeOffset now,
        AzureProviderExecutor? executor = null) => new(
        new AzureProviderOperationService(operationStore, new FixedTimeProvider(now)),
        operationStore,
        operationStore,
        timeProvider: new FixedTimeProvider(now),
        options: options,
        executor: executor);

    private static async Task<CleanupTools> CreateCleanupToolsAsync(
        AzureProviderTargetScope scope,
        Guid instanceId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"elsa-delete-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var templates = Path.Combine(root, "templates");
            Directory.CreateDirectory(templates);
            foreach (var name in new[] { "main.bicep", "acr-pull-role.bicep", "sql-bootstrap.sql" })
                await File.WriteAllTextAsync(Path.Combine(templates, name), "targetScope = 'resourceGroup'\n");

            var deleteRequested = Path.Combine(root, "delete-requested");
            var absenceObserved = Path.Combine(root, "absence-observed");
            var log = Path.Combine(root, "commands.log");
            var executable = Path.Combine(root, "azure-command");
            var workloadName = $"e{instanceId:N}"[..16];
            var expectedGroup = AzureProviderResourceAssignmentNaming.ResourceGroupName(scope.ResourceGroupName, instanceId);
            var script = $$"""
#!/bin/sh
set -eu
delete_requested={{ShellQuote(deleteRequested)}}
absence_observed={{ShellQuote(absenceObserved)}}
log={{ShellQuote(log)}}
workload={{ShellQuote(workloadName)}}
subscription={{ShellQuote(scope.SubscriptionId)}}
resource_group={{ShellQuote(expectedGroup)}}

printf '%s\n' "$*" >> "$log"
if [ "$#" -eq 9 ] && [ "$*" = "group exists --subscription $subscription --name $resource_group --output tsv --only-show-errors" ]; then
    if [ -f "$delete_requested" ] && [ ! -f "$absence_observed" ]; then
        : > "$absence_observed"
        printf '%s' 'true'
    elif [ -f "$delete_requested" ]; then
        printf '%s' 'false'
    else
        printf '%s' 'true'
    fi
    exit 0
fi
if [ "$#" -eq 11 ] && [ "$*" = "group show --subscription $subscription --name $resource_group --query tags --output json --only-show-errors" ]; then
    printf '%s' "{\"managed-by\":\"elsa-control\",\"owner\":\"elsa-control\",\"workload-name\":\"$workload\",\"sqlBootstrapObjectId\":\"11111111-1111-1111-1111-111111111111\"}"
    exit 0
fi
if [ "$#" -eq 9 ] && [ "$*" = "resource list --subscription $subscription --resource-group $resource_group --output json --only-show-errors" ]; then
    printf '%s' '[]'
    exit 0
fi
if [ "$#" -eq 11 ] && [ "$*" = "group delete --subscription $subscription --name $resource_group --yes --no-wait --output none --only-show-errors" ]; then
    : > "$delete_requested"
    exit 0
fi
if [ "$#" -eq 9 ] && [ "$*" = "keyvault list-deleted --subscription $subscription --resource-type vault --output json --only-show-errors" ]; then
    printf '%s' '[]'
    exit 0
fi
exit 97
""";
            await File.WriteAllTextAsync(executable, script);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var options = new AzureProviderRunnerOptions
            {
                Enabled = true,
                AzureCliPath = executable,
                AzureCliClientId = "11111111-1111-1111-1111-111111111111",
                SqlCmdPath = executable,
                CurlPath = executable,
                TemplateRoot = templates,
                SqlBootstrapObjectId = "11111111-1111-1111-1111-111111111111",
                SqlBootstrapLogin = "elsa-bootstrap",
                SqlBootstrapIp = "203.0.113.10",
                RuntimeAdminUsername = "runtime-admin",
                ObservationAttempts = 1,
                ObservationDelay = TimeSpan.Zero,
                CommandTimeout = TimeSpan.FromSeconds(30)
            };
            options.ValidateRegistryAuthority(scope);
            return new CleanupTools(root, options, log);
        }
        catch
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed class PosixFactAttribute : FactAttribute
    {
        public PosixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "The fake Azure command boundary uses a POSIX executable script.";
        }
    }

    private sealed class CleanupTools(string root, AzureProviderRunnerOptions options, string log) : IAsyncDisposable
    {
        public AzureProviderRunnerOptions Options { get; } = options;

        public async Task<string> ReadLogAsync() =>
            File.Exists(log) ? await File.ReadAllTextAsync(log) : string.Empty;

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
