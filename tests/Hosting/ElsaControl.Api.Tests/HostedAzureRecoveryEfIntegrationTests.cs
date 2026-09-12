using System.Text;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed partial class ElsaInstanceProviderReconciliationHostedServiceTests
{
    [PosixHostedRecoveryFact]
    public async Task Hosted_recovery_dispatches_real_azure_provider_and_persists_the_handoff_after_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"elsa-hosted-azure-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var templateRoot = Path.Combine(root, "templates");
            Directory.CreateDirectory(templateRoot);
            foreach (var file in new[] { "main.bicep", "acr-pull-role.bicep", "sql-bootstrap.sql" })
                await File.WriteAllTextAsync(Path.Combine(templateRoot, file), "targetScope = 'resourceGroup'\n");

            var azPath = Path.Combine(root, "az");
            var sqlCmdPath = Path.Combine(root, "sqlcmd");
            var curlPath = Path.Combine(root, "curl");
            var callsPath = Path.Combine(root, "calls");
            var scope = new AzureProviderTargetScope(
                "11111111-1111-1111-1111-111111111111",
                "hosted-recovery-rg",
                "22222222-2222-2222-2222-222222222222",
                "registry-rg",
                "registry",
                "westeurope");
            var instanceId = Guid.NewGuid();
            var workloadName = $"e{instanceId:N}"[..16];
            const string sqlBootstrapObjectId = "33333333-3333-3333-3333-333333333333";
            var options = new AzureProviderRunnerOptions
            {
                Enabled = true,
                DisposableProofMode = true,
                DisposableExpiryUtc = new DateOnly(2026, 12, 31),
                Owner = "elsa-control",
                AzureCliPath = azPath,
                AzureCliClientId = null,
                SqlCmdPath = sqlCmdPath,
                CurlPath = curlPath,
                TemplateRoot = templateRoot,
                SqlBootstrapObjectId = sqlBootstrapObjectId,
                SqlBootstrapLogin = "elsa-bootstrap",
                SqlBootstrapIp = "203.0.113.10",
                RuntimeAdminUsername = "elsa-admin",
                CommandTimeout = TimeSpan.FromSeconds(10),
                ObservationAttempts = 1,
                ObservationDelay = TimeSpan.Zero
            };
            await WriteExecutableAsync(sqlCmdPath, "#!/bin/sh\nexit 97\n");
            await WriteExecutableAsync(curlPath, "#!/bin/sh\nexit 97\n");

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = CreateMigratedContext(connection);
            await db.Database.MigrateAsync();
            var workspace = await CreateWorkspaceAsync(db, "Hosted Azure recovery EF workspace");
            var operationStore = new AzureProviderOperationStore(db);
            var lifecycle = new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(
                    db,
                    EmptyResolutionInputSource.Instance,
                    recoveryObservationStore: operationStore),
                new FixedTimeProvider(Now));
            var created = await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId,
                workspace.Id,
                "Managed Azure",
                "hosted-azure-recovery",
                WorkerIntent(),
                "hosted-azure-recovery-create",
                instanceId));

            var deploymentStore = new DeploymentWorkspaceStore(db);
            var application = await deploymentStore.CreateApplicationAsync(
                workspace.Id,
                new CreateWorkflowApplicationRequest("Hosted Azure application", null, null));
            var environment = await deploymentStore.CreateEnvironmentAsync(
                workspace.Id,
                new CreateDeploymentEnvironmentRequest(application.Id, "Production", EnvironmentTier.Production));
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE DeploymentEnvironments SET ElsaInstanceId = {instanceId} WHERE Id = {environment.Id} AND WorkspaceId = {workspace.Id}"));
            var target = new ElsaInstanceLifecycleDeploymentTarget(
                application.Id, environment.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var resolution = ValidAzureResolution(workspace.Id, instanceId);
            var lifecycleStore = new EfCoreElsaInstanceLifecycleStore(
                db,
                new StaticResolutionInputSource(target),
                new FixedTimeProvider(Now),
                recoveryObservationStore: operationStore);
            var queued = await new ElsaInstanceLifecycleWorker(
                    lifecycleStore,
                    new StaticResolver(resolution),
                    new FixedTimeProvider(Now))
                .ProcessAvailableAsync("hosted-azure-recovery-worker");
            Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, Assert.Single(queued.Results).Outcome);

            var expectedResourceGroup = AzureProviderResourceAssignmentNaming.ResourceGroupName(
                scope.ResourceGroupName, instanceId);
            var translated = AzureWorkloadPlanTranslator.Translate(
                resolution.Plan,
                new AzureWorkloadTarget(workloadName, scope.Location));
            Assert.True(translated.IsAccepted, string.Join(", ", translated.Findings.Select(finding => finding.Code)));
            var expectedDeployment = $"elsa108-{workloadName}-{translated.Plan!.Fingerprint[..12]}-foundation";
            await WriteExecutableAsync(azPath, $$"""
                #!/bin/sh
                set -eu
                calls={{ShellQuote(callsPath)}}
                expected_subscription={{ShellQuote(scope.SubscriptionId)}}
                expected_resource_group={{ShellQuote(expectedResourceGroup)}}
                expected_deployment={{ShellQuote(expectedDeployment)}}
                expected_registry_subscription={{ShellQuote(scope.RegistrySubscriptionId)}}
                expected_registry_group={{ShellQuote(scope.RegistryResourceGroupName)}}
                expected_registry_name={{ShellQuote(scope.RegistryName)}}
                record() { printf '%s\n' "$1" >> "$calls"; }
                if [ "$#" -eq 9 ] &&
                   [ "$1" = group ] && [ "$2" = exists ] &&
                   [ "$3" = --subscription ] && [ "$4" = "$expected_subscription" ] &&
                   [ "$5" = --name ] && [ "$6" = "$expected_resource_group" ] &&
                   [ "$7" = --output ] && [ "$8" = tsv ] && [ "$9" = --only-show-errors ]; then
                    record foundation-group-exists
                    printf '%s' true
                    exit 0
                fi
                if [ "$#" -eq 11 ] &&
                   [ "$1" = group ] && [ "$2" = show ] &&
                   [ "$3" = --subscription ] && [ "$4" = "$expected_subscription" ] &&
                   [ "$5" = --name ] && [ "$6" = "$expected_resource_group" ] &&
                   [ "$7" = --query ] && [ "$8" = tags ] &&
                   [ "$9" = --output ] && [ "${10}" = json ] && [ "${11}" = --only-show-errors ]; then
                    record foundation-group-tags
                    printf '%s' '{"proof":"108","owner":"elsa-control","proof-name":"'"{{workloadName}}"'","expiry":"2026-12-31","sqlBootstrapObjectId":"33333333-3333-3333-3333-333333333333"}'
                    exit 0
                fi
                if [ "$#" -eq 14 ] &&
                   [ "$1" = deployment ] && [ "$2" = group ] && [ "$3" = show ] &&
                   [ "$4" = --subscription ] && [ "$5" = "$expected_subscription" ] &&
                   [ "$6" = --resource-group ] && [ "$7" = "$expected_resource_group" ] &&
                   [ "$8" = --name ] && [ "$9" = "$expected_deployment" ] &&
                   [ "${10}" = --query ] && [ "${11}" = properties.provisioningState ] &&
                   [ "${12}" = --output ] && [ "${13}" = tsv ] && [ "${14}" = --only-show-errors ]; then
                    record foundation-deployment
                    printf '%s' Succeeded
                    exit 0
                fi
                if [ "$#" -eq 13 ] &&
                   [ "$1" = acr ] && [ "$2" = show ] &&
                   [ "$3" = --subscription ] && [ "$4" = "$expected_registry_subscription" ] &&
                   [ "$5" = --resource-group ] && [ "$6" = "$expected_registry_group" ] &&
                   [ "$7" = --name ] && [ "$8" = "$expected_registry_name" ] &&
                   [ "$9" = --query ] && [ "${10}" = id ] &&
                   [ "${11}" = --output ] && [ "${12}" = tsv ] && [ "${13}" = --only-show-errors ]; then
                    record acr-rejected
                    exit 97
                fi
                record unexpected-command
                exit 96
                """);

            var templateFingerprint = options.ComputeTemplateAuthorityFingerprint();
            var scopeFingerprint = options.ComputeProviderScopeFingerprint(scope);
            var providerOptions = new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = templateFingerprint,
                ProviderScopeFingerprint = scopeFingerprint,
                SubscriptionId = scope.SubscriptionId,
                ResourceGroupNamePrefix = scope.ResourceGroupName
            };

            var runner = new AzureBicepProviderRunner(options, scope);
            var provider = new AzureElsaInstanceProvider(
                new AzureProviderOperationService(operationStore),
                operationStore,
                operationStore,
                new FixedTimeProvider(Now),
                providerOptions,
                recoveryObserver: runner,
                recoveryObservationStore: operationStore);
            var pendingSubmission = Assert.Single(await lifecycleStore.ListPendingProviderOperationsAsync(16)).Submission
                ?? throw new InvalidOperationException("The resolved provider submission was not persisted.");
            var submitted = await provider.SubmitAsync(pendingSubmission);
            await lifecycleStore.CommitProviderSubmissionAsync(new(
                workspace.Id,
                instanceId,
                created.Operation.Id,
                created.Operation.AttemptNumber,
                submitted.CorrelationId,
                Now,
                submitted.PlacementAssignmentId));

            var providerOperation = await operationStore.GetLatestReconcileAsync(
                workspace.Id,
                $"e{instanceId:N}"[..16],
                scopeFingerprint);
            Assert.NotNull(providerOperation);
            var assignment = await ((IAzureProviderResourceAssignmentStore)operationStore).GetAsync(
                workspace.Id, Guid.Parse(submitted.PlacementAssignmentId!));
            Assert.NotNull(assignment);
            var claimed = Assert.IsType<AzureProviderOperation>(await operationStore.ClaimAsync(
                workspace.Id, providerOperation!.Id, "hosted-proof-worker", "hosted-proof-lease",
                TimeSpan.FromMinutes(5), Now, providerOperation.Version));
            var resources = new AzureProviderResourceReferences(
                ResourceGroupName: assignment!.ResourceGroupName,
                FoundationDeploymentId: $"/subscriptions/{scope.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/elsa108-{providerOperation.TargetKey}-{providerOperation.PlanFingerprint[..12]}-foundation",
                WorkloadIdentityResourceId: $"/subscriptions/{scope.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{providerOperation.TargetKey}-identity",
                WorkloadIdentityClientId: "44444444-4444-4444-4444-444444444444",
                WorkloadIdentityPrincipalId: "55555555-5555-5555-5555-555555555555",
                KeyVaultResourceId: $"/subscriptions/{scope.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.KeyVault/vaults/{providerOperation.TargetKey}-kv",
                KeyVaultUri: $"https://{providerOperation.TargetKey}-kv.vault.azure.net/",
                SqlServerResourceId: $"/subscriptions/{scope.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Sql/servers/{providerOperation.TargetKey}-sql",
                SqlServerFqdn: $"{providerOperation.TargetKey}-sql.database.windows.net",
                ContainerAppsEnvironmentResourceId: $"/subscriptions/{scope.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.App/managedEnvironments/{providerOperation.TargetKey}-aca");
            var checkpointed = Assert.IsType<AzureProviderOperation>(await operationStore.CheckpointAsync(
                workspace.Id,
                providerOperation.Id,
                "hosted-proof-lease",
                new(AzureProviderOperationPhase.FoundationSubmitted, "azure.foundation.submitted",
                    "The foundation call was accepted before its response was lost.", resources, null,
                    AzureProviderHealth.Unknown, [], AttemptedStep: AzureProviderRunnerStep.Foundation),
                Now,
                claimed.Version));
            await operationStore.FinalizeAsync(
                workspace.Id,
                providerOperation.Id,
                "hosted-proof-lease",
                AzureProviderOperationStatus.RecoveryRequired,
                "azure.step.uncertain",
                Now,
                checkpointed.Version);

            var reconciler = new ElsaInstanceProviderReconciliationService(
                lifecycleStore,
                provider,
                new FixedTimeProvider(Now.AddMinutes(1)));
            var observed = await reconciler.ReconcileAsync(workspace.Id, created.Operation.Id);
            Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, observed.Outcome);
            Assert.True(observed.RetrySafe);
            var current = await lifecycleStore.GetInstanceAsync(workspace.Id, instanceId)
                ?? throw new InvalidOperationException("The managed instance was not persisted.");
            var accepted = await new ElsaInstanceLifecycleService(
                    lifecycleStore,
                    new FixedTimeProvider(Now.AddMinutes(2)))
                .RecoverAsync(new(workspace.Id, instanceId, current.Version, "hosted-real-azure-recovery"));
            Assert.Equal(ElsaInstanceOperationState.Queued, accepted.Operation.State);

            await using var restartDb = CreateMigratedContext(connection);
            var restartStore = new EfCoreElsaInstanceLifecycleStore(
                restartDb,
                EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(3)),
                recoveryObservationStore: new AzureProviderOperationStore(restartDb));
            var restarted = Assert.Single(await restartStore.ListPendingProviderOperationsAsync(16));
            Assert.NotNull(restarted.Submission);
            Assert.NotNull(restarted.Recovery);
            var restartedSubmission = restarted.Submission!;
            var restartedRecovery = restarted.Recovery!;
            restartedRecovery.Validate();
            Assert.Equal(created.Operation.Id, restartedSubmission.OperationId);
            Assert.Equal(workspace.OrganizationId, restartedSubmission.OrganizationId);
            Assert.Equal(instanceId, restartedSubmission.InstanceId);
            Assert.Equal(accepted.Operation.AttemptNumber, restartedSubmission.AttemptNumber);
            Assert.Equal(submitted.PlacementAssignmentId, restartedSubmission.PlacementAssignmentId);
            Assert.Equal(accepted.Operation.RecoveryIdempotencyScope, restartedRecovery.IdempotencyScope);
            Assert.Equal(accepted.Operation.RecoveryIdempotencyKey, restartedRecovery.IdempotencyKey);
            Assert.Equal(accepted.Operation.RecoveryRequestHash, restartedRecovery.RequestHash);
            Assert.Equal(accepted.Operation.AttemptNumber, restartedRecovery.AcceptedLifecycleAttemptNumber);
            Assert.True(restartedRecovery.AcceptedInstanceVersion > restartedRecovery.ObservedInstanceVersion);

            var restartProviderStore = new AzureProviderOperationStore(restartDb);
            var restartRunner = new AzureBicepProviderRunner(options, scope);
            var restartExecutor = new AzureProviderExecutor(
                restartProviderStore,
                restartRunner,
                new FixedTimeProvider(Now.AddMinutes(3)),
                leaseDuration: TimeSpan.FromMinutes(5),
                workerId: "hosted-restart-executor",
                assignmentStore: restartProviderStore);
            var restartProvider = new AzureElsaInstanceProvider(
                new AzureProviderOperationService(restartProviderStore),
                restartProviderStore,
                restartProviderStore,
                new FixedTimeProvider(Now.AddMinutes(3)),
                providerOptions,
                restartExecutor,
                restartRunner,
                restartProviderStore);
            var restartReconciler = new ElsaInstanceProviderReconciliationService(
                restartStore, restartProvider, new FixedTimeProvider(Now.AddMinutes(3)));
            await using var services = CreateRealHostedServices(
                restartStore,
                restartProvider,
                restartReconciler,
                new EfCoreElsaInstanceCommercialGate(restartDb));
            await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);

            var callsAfterFirstPass = await File.ReadAllLinesAsync(callsPath);
            Assert.Equal(
                [
                    "foundation-group-exists",
                    "foundation-group-tags",
                    "foundation-deployment",
                    "foundation-group-exists",
                    "foundation-group-tags",
                    "foundation-deployment",
                    "acr-rejected"
                ],
                callsAfterFirstPass);

            // A terminal provider/lifecycle result must make a second hosted poll a no-op.
            // This catches an accidental normal-submission fallback after recovery dispatch.
            await CreateHostedService(services).ProcessPendingAsync(CancellationToken.None);
            Assert.Equal(callsAfterFirstPass, await File.ReadAllLinesAsync(callsPath));

            // Re-open both stores in a third context. Assertions against restartDb would only
            // prove the tracked in-memory view and could miss an uncommitted hand-off.
            await using var finalDb = CreateMigratedContext(connection);
            var retainedProviderIds = await finalDb.Database.SqlQuery<Guid>(
                $"SELECT Id AS Value FROM AzureProviderOperations WHERE WorkspaceId = {workspace.Id} AND InstanceId = {instanceId}")
                .ToArrayAsync();
            Assert.Equal(providerOperation.Id, Assert.Single(retainedProviderIds));
            var finalProviderStore = new AzureProviderOperationStore(finalDb);
            var persistedProvider = await finalProviderStore.GetLatestReconcileAsync(
                workspace.Id,
                providerOperation.TargetKey,
                scopeFingerprint);
            Assert.NotNull(persistedProvider);
            Assert.Equal(providerOperation.Id, persistedProvider!.Id);
            Assert.Equal(AzureProviderOperationStatus.Failed, persistedProvider.Status);
            Assert.Equal(AzureProviderRunnerStep.AcrPull, persistedProvider.AttemptedStep);
            Assert.Equal(submitted.CorrelationId, persistedProvider.OperationIdentity);
            Assert.Equal(submitted.PlacementAssignmentId, persistedProvider.ProviderAssignmentId?.ToString("D"));
            Assert.Equal(scopeFingerprint, persistedProvider.ProviderScopeFingerprint);

            var finalLifecycleStore = new EfCoreElsaInstanceLifecycleStore(
                finalDb,
                EmptyResolutionInputSource.Instance,
                new FixedTimeProvider(Now.AddMinutes(4)),
                recoveryObservationStore: finalProviderStore);
            Assert.Empty(await finalLifecycleStore.ListPendingProviderOperationsAsync(16));
            var finalApi = new EfCoreManagedElsaInstanceApiStore(finalDb);
            var persisted = await finalApi
                .GetOperationAsync(workspace.Id, instanceId, created.Operation.Id);
            Assert.NotNull(persisted);
            Assert.Equal(ElsaInstanceOperationState.Failed, persisted!.State);
            Assert.Equal(WorkspaceDeploymentRunStatus.Failed,
                Assert.Single(await finalApi.ListDeploymentsAsync(workspace.Id, instanceId)).Status);
            Assert.Equal(2,
                (await finalApi.ListAuditAsync(workspace.Id, instanceId))
                .Count(x => x.EventType == "lifecycle.provider-submitted"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider CreateRealHostedServices(
        EfCoreElsaInstanceLifecycleStore pending,
        AzureElsaInstanceProvider provider,
        IElsaInstanceProviderReconciliationService reconciler,
        IElsaInstanceCommercialGate commercialGate)
    {
        var services = new ServiceCollection();
        services.AddSingleton(pending);
        services.AddSingleton<IElsaInstanceProviderPendingOperationStore>(pending);
        services.AddSingleton<IElsaInstanceProviderSubmissionPort>(provider);
        services.AddSingleton<IElsaInstanceProviderRecoveryPort>(provider);
        services.AddSingleton<IElsaInstanceProviderSubmissionStore>(pending);
        services.AddSingleton<IElsaInstanceProviderReconciliationService>(reconciler);
        services.AddSingleton(commercialGate);
        return services.BuildServiceProvider();
    }

    private static ElsaInstancePlanResolutionResult ValidAzureResolution(Guid workspaceId, Guid instanceId)
    {
        var source = SuccessfulResolution(workspaceId, instanceId);
        var plan = source.Plan!;
        const string imageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var component = plan.Topology.Components.Single();
        var capacity = ElsaInstancePlanResolutionOptions.Default.EffectiveCapacityProfiles["standard-small"];
        plan = plan with
        {
            Release = plan.Release with
            {
                ComponentDeclarations = new(
                    "1", imageDigest,
                    [
                        new(AzureWorkloadPlanTranslator.SqlWorkflowPackageId, "5.0.0-preview.1"),
                        new(AzureWorkloadPlanTranslator.SqlQuartzPackageId, "5.0.0-preview.1")
                    ])
            },
            Topology = plan.Topology with
            {
                Id = AzureWorkloadPlanTranslator.SupportedTopology,
                Components = [component with
                {
                    Image = component.Image with
                    {
                        Repository = AzureWorkloadPlanTranslator.SupportedRepository,
                        Reference = $"{AzureWorkloadPlanTranslator.SupportedRepository}@{imageDigest}",
                        Digest = imageDigest
                    }
                }]
            },
            Capacity = plan.Capacity with
            {
                Components = [new(component.Id, capacity.MinReplicas, capacity.MaxReplicas, capacity.CpuMillicores, capacity.MemoryMiB, capacity.EphemeralStorageMiB)]
            },
            Isolation = AzureWorkloadPlanTranslator.SupportedIsolation,
            Network = plan.Network with { Egress = "unrestricted" },
            Configuration = new([
                new("database:connectionstring", "string", true, true, false, null, null, "secret://vault/database-connection", null),
                new("identity:signingkey", "string", true, true, false, null, null, "secret://vault/identity-signing-key", null),
                new("admin:password", "string", true, true, false, null, null, "secret://vault/admin-password", null)
            ]),
            Evidence = [
                new(ReleaseManifestEvidenceKinds.Manifest, plan.Release.ReleaseManifestReference, plan.Release.ReleaseManifestDigest,
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Manifest)),
                new(ReleaseManifestEvidenceKinds.Signature, "https://example.test/signatures/5.0.0-preview.1.sig",
                    "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                    ReleaseManifestEvidenceContract.DescriptionFor(ReleaseManifestEvidenceKinds.Signature))]
        };
        var reference = new ElsaResolvedPlanReference(
            source.Reference!.PlanId,
            source.Reference.SchemaVersion,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan),
            source.Reference.PlanUri);
        return source with
        {
            Plan = plan,
            Reference = reference,
            CurrentResolvedRelease = new ElsaCurrentResolvedRelease(
                reference,
                plan.Release.DistributionId,
                plan.Release.ReleaseLine,
                plan.Release.Version,
                plan.Release.ReleaseManifestDigest,
                plan.Topology.Components.Select(component => new ElsaComponentDigest(
                    component.Id,
                    component.Image.Digest)))
        };
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed class PosixHostedRecoveryFactAttribute : FactAttribute
    {
        public PosixHostedRecoveryFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "The fake Azure command process uses POSIX executable scripts.";
        }
    }
}
