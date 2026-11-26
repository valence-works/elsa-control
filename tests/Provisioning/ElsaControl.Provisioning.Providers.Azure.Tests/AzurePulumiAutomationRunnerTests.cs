using ElsaControl.Provisioning.Abstractions;
using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Core.Stores;
using ElsaControl.Provisioning.Providers.Azure.Programs;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class AzurePulumiAutomationRunnerTests
{
    [Fact]
    public void Workspace_uses_blob_backend_with_locking_and_key_vault_secrets()
    {
        var runner = new AzurePulumiAutomationRunner();
        var target = AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging");
        var workspace = runner.CreateWorkspace(
            target,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AZURE_CLIENT_ID"] = "workload-identity-client" });

        Assert.Equal(PulumiProjectNames.Engine, workspace.ProjectName);
        Assert.Equal("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", workspace.StackName);
        Assert.Equal("azblob://pulumi-state", workspace.BackendUrl);
        Assert.Equal("azurekeyvault://kv-elsa-platform.vault.azure.net/keys/pulumi-secrets", workspace.SecretsProviderUrl);
        Assert.Equal("stelsaplatform", workspace.EnvironmentVariables["AZURE_STORAGE_ACCOUNT"]);
        Assert.Equal("workload-identity-client", workspace.EnvironmentVariables["AZURE_CLIENT_ID"]);
        Assert.Equal("true", workspace.EnvironmentVariables["PULUMI_DISABLE_AUTOMATIC_PLUGIN_ACQUISITION"]);
        Assert.DoesNotContain(workspace.EnvironmentVariables.Keys, key =>
            key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
            || key.Contains("STORAGE_KEY", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("organization/elsa-platform/staging", workspace.StackConfig["platformStack"]);
        Assert.True(AzureBlobStateBackend.From(target.StateBackend).LockingEnabled);

        var options = workspace.ToLocalWorkspaceOptions(runner.CreateProgram(ProvisioningStackKind.Engine));
        Assert.Equal("azblob://pulumi-state", options.ProjectSettings!.Backend!.Url);
        Assert.Equal(workspace.SecretsProviderUrl, options.SecretsProvider);
        Assert.NotNull(options.Program);
    }

    [Fact]
    public void File_backend_is_used_for_local_tests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"elsa-pulumi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var runner = new AzurePulumiAutomationRunner(allowFileBackend: true);
        var target = new ProvisioningTarget(
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Platform,
            "staging",
            new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity-client"),
            new StateBackendReference(StateBackendReference.FileKind, directory),
            new SecretsProviderReference(SecretsProviderReference.InMemoryKind, "memory://secrets"));
        var workspace = runner.CreateWorkspace(
            target,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["environmentName"] = "staging" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(new Uri(directory).AbsoluteUri, workspace.BackendUrl);
        Assert.StartsWith("file:///", workspace.BackendUrl, StringComparison.Ordinal);
        Assert.Equal("passphrase", workspace.SecretsProviderUrl);
        Assert.True(FileStateBackend.From(target.StateBackend).LockingEnabled);
    }

    [Fact]
    public void File_backend_is_refused_outside_tests()
    {
        var runner = new AzurePulumiAutomationRunner(allowFileBackend: false);
        var directory = Path.Combine(Path.GetTempPath(), $"elsa-pulumi-{Guid.NewGuid():N}");
        var target = new ProvisioningTarget(
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Platform,
            "staging",
            new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity-client"),
            new StateBackendReference(StateBackendReference.FileKind, directory),
            new SecretsProviderReference(SecretsProviderReference.InMemoryKind, "memory://secrets"));
        var error = Assert.Throws<InvalidOperationException>(() =>
            runner.CreateWorkspace(
                target,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["environmentName"] = "staging" },
                new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Equal("File and passphrase Pulumi backends are test-only.", error.Message);
    }

    [Fact]
    public void Diy_platform_stack_reference_uses_the_literal_organization()
    {
        var runner = new AzurePulumiAutomationRunner();
        var workspace = runner.CreateWorkspace(
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal("organization/elsa-platform/staging", workspace.StackConfig["platformStack"]);
        Assert.Equal(
            "control/elsa-platform/staging",
            runner.CreateWorkspace(
                AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["engineName"] = "engine-one",
                    ["platformStack"] = "control/elsa-platform/staging"
                },
                new Dictionary<string, string>(StringComparer.Ordinal)).StackConfig["platformStack"]);
    }

    [Fact]
    public void Engine_stack_config_is_independent_per_stack()
    {
        var runner = new AzurePulumiAutomationRunner();
        var one = runner.CreateWorkspace(
            AzureTarget("engine-one", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var two = runner.CreateWorkspace(
            AzureTarget("engine-two", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-two" },
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(PulumiProjectNames.Engine, one.ProjectName);
        Assert.Equal(PulumiProjectNames.Engine, two.ProjectName);
        Assert.NotEqual(one.StackName, two.StackName);
        Assert.Equal("engine-one", one.StackConfig["engineName"]);
        Assert.Equal("engine-two", two.StackConfig["engineName"]);
        Assert.Equal("organization/elsa-platform/staging", one.StackConfig["platformStack"]);
        Assert.Equal("organization/elsa-platform/staging", two.StackConfig["platformStack"]);
    }

    [Fact]
    public void Platform_and_engine_programs_are_the_checked_in_stacks()
    {
        var runner = new AzurePulumiAutomationRunner();
        Assert.Equal(PulumiProjectNames.Platform, runner.ProjectName(ProvisioningStackKind.Platform));
        Assert.Equal(PulumiProjectNames.Engine, runner.ProjectName(ProvisioningStackKind.Engine));
        Assert.NotNull(runner.CreateProgram(ProvisioningStackKind.Platform));
        Assert.NotNull(runner.CreateProgram(ProvisioningStackKind.Engine));
        Assert.Equal(nameof(ElsaPlatformStack), typeof(ElsaPlatformStack).Name);
        Assert.Equal(nameof(ElsaEngineStack), typeof(ElsaEngineStack).Name);
    }

    [Fact]
    public void Secret_environment_keys_are_rejected()
    {
        var runner = new AzurePulumiAutomationRunner();
        var workspace = runner.CreateWorkspace(
            AzureTarget("staging", ProvisioningStackKind.Platform),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["AZURE_STORAGE_KEY"] = "must-not-appear" });
        var error = Assert.Throws<InvalidOperationException>(() =>
            workspace.ToLocalWorkspaceOptions(runner.CreateProgram(ProvisioningStackKind.Platform)));
        Assert.Equal("Provisioning environment must not carry secret values.", error.Message);
    }

    [Fact]
    public async Task Azure_provider_emits_sanitised_events_without_running_the_bicep_runner()
    {
        var automation = new FakePulumiAutomationClient();
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Preview,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-preview",
            "azure-preview-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Preview,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal)
                {
                    ProvisioningCapability.ContainerRuntime,
                    ProvisioningCapability.ManagedSql
                }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-preview",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" });

        var result = await provider.PreviewAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.Equal(ProvisioningCommand.Preview, automation.LastCommand);
        Assert.NotEmpty(result.Events);
        foreach (var value in result.Events)
        {
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.Message));
            Assert.False(ProvisioningEventSanitizer.ContainsSensitiveToken(value.ResourceKind));
        }
    }

    [Fact]
    public async Task Cancel_is_a_no_op_and_never_destroys()
    {
        var automation = new FakePulumiAutomationClient();
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Cancel,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-cancel",
            "azure-cancel-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Cancel,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-cancel");
        var result = await provider.CancelAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.Equal(ProvisioningFailureCodes.NothingToCancel, result.FailureCode);
        Assert.Equal(ProvisioningCommand.Cancel, automation.LastCommand);
    }

    [Fact]
    public async Task Destroy_records_owned_resource_group_absence()
    {
        var automation = new FakePulumiAutomationClient();
        var probe = new RecordingResourceGroupAbsenceProbe { Result = ResourceGroupPresence.Absent };
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation,
            probe);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Destroy,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-destroy",
            "azure-destroy-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Destroy,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-destroy",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceGroupName"] = "elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"
            });
        var result = await provider.DestroyAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.True(result.OwnedResourcesAbsent);
        Assert.Equal("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", probe.LastName);
        Assert.Equal(ProvisioningCommand.Destroy, automation.LastCommand);
        Assert.Contains(result.Events, value => value.Message == "Owned resource group is absent.");
    }

    [Fact]
    public async Task Destroy_fails_closed_when_the_owned_resource_group_is_still_present()
    {
        var automation = new FakePulumiAutomationClient();
        var probe = new RecordingResourceGroupAbsenceProbe { Result = ResourceGroupPresence.Present };
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation,
            probe);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Destroy,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-destroy-present",
            "azure-destroy-present-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Destroy,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-destroy-present");
        var result = await provider.DestroyAsync(operation, request);
        Assert.False(result.Succeeded);
        Assert.False(result.OwnedResourcesAbsent);
        Assert.Equal(ProvisioningFailureCodes.ResourceGroupPresent, result.FailureCode);
        Assert.Equal("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", probe.LastName);
    }

    [Fact]
    public async Task Destroy_does_not_certify_absence_without_a_probe()
    {
        var automation = new FakePulumiAutomationClient();
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Destroy,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-destroy-unknown",
            "azure-destroy-unknown-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Destroy,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-destroy-unknown");
        var result = await provider.DestroyAsync(operation, request);
        Assert.False(result.Succeeded);
        Assert.False(result.OwnedResourcesAbsent);
        Assert.Equal(ProvisioningFailureCodes.ResourceGroupUnknown, result.FailureCode);
    }

    [Fact]
    public async Task Held_stack_lock_maps_to_a_clear_failure_code()
    {
        var automation = new FakePulumiAutomationClient { ThrowLockConflict = true };
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-locked",
            "azure-locked-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Provision,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-locked");
        var result = await provider.ProvisionAsync(operation, request);
        Assert.False(result.Succeeded);
        Assert.Equal(ProvisioningFailureCodes.StackLocked, result.FailureCode);
    }

    [Fact]
    public void Secret_stack_config_is_not_written_unencrypted_to_stack_settings()
    {
        var runner = new AzurePulumiAutomationRunner();
        var workspace = runner.CreateWorkspace(
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" },
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProvisioningEventSanitizer.AdminPasswordOverrideKey] = "override-admin"
            });
        Assert.Equal("override-admin", workspace.SecretStackConfig![ProvisioningEventSanitizer.AdminPasswordOverrideKey]);
        var options = workspace.ToLocalWorkspaceOptions(runner.CreateProgram(ProvisioningStackKind.Engine));
        var config = options.StackSettings![workspace.StackName].Config;
        Assert.NotNull(config);
        Assert.False(config.ContainsKey(ProvisioningEventSanitizer.AdminPasswordOverrideKey));
        Assert.False(config.ContainsKey("sqlConnectionSecretValue"));
        Assert.False(config.ContainsKey("signingKeySecretValue"));
        Assert.False(config.ContainsKey("adminPasswordSecretValue"));
    }

    [Fact]
    public void Self_managed_backend_lock_messages_are_detected()
    {
        Assert.True(PulumiLockConflict.Matches(
            new InvalidOperationException(
                "the stack is currently locked by 1.lock; pulumi created the lock [409]")));
        Assert.True(PulumiLockConflict.Matches(
            new InvalidOperationException("lock conflict: another update is currently in progress")));
        Assert.True(PulumiLockConflict.Matches(
            new InvalidOperationException("Concurrent update failed with [409] Conflict")));
        Assert.False(PulumiLockConflict.Matches(new InvalidOperationException("preview failed")));
    }

    [Fact]
    public async Task Fresh_create_does_not_pass_shared_seed_values()
    {
        var automation = new FakePulumiAutomationClient();
        var secrets = new EmptyProvisioningSecretResolver();
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new AzureWorkloadIdentityCredentials(),
            automation,
            secrets: secrets);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Provision,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-seeds",
            "azure-seeds-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Provision,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-seeds",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["engineName"] = "engine-one" });
        var result = await provider.ProvisionAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.False(automation.LastWorkspace!.SecretStackConfig?.ContainsKey("sqlConnectionSecretValue") ?? false);
        Assert.False(automation.LastWorkspace.SecretStackConfig?.ContainsKey("signingKeySecretValue") ?? false);
        Assert.False(automation.LastWorkspace.SecretStackConfig?.ContainsKey("adminPasswordSecretValue") ?? false);
        Assert.False(automation.LastWorkspace.StackConfig.ContainsKey("sqlConnectionSecretValue"));
        var options = automation.LastWorkspace.ToLocalWorkspaceOptions(
            new AzurePulumiAutomationRunner().CreateProgram(ProvisioningStackKind.Engine));
        Assert.False(options.StackSettings![automation.LastWorkspace.StackName].Config!
            .ContainsKey("sqlConnectionSecretValue"));
    }

    [Fact]
    public async Task Destroy_uses_the_stack_subscription_from_the_workspace()
    {
        var automation = new FakePulumiAutomationClient();
        var probe = new RecordingResourceGroupAbsenceProbe { Result = ResourceGroupPresence.Absent };
        var subscription = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D");
        var provider = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new StaticCredentialResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AZURE_CLIENT_ID"] = "workload-identity-client",
                ["ARM_SUBSCRIPTION_ID"] = subscription
            }),
            automation,
            probe);
        var operation = new ProvisioningOperation(
            Guid.NewGuid(),
            ProvisioningCommand.Destroy,
            ProvisioningOperationState.Running,
            AzurePulumiAutomationRunner.ProviderId,
            ProvisioningStackKind.Engine,
            "engine-aaaaaaaabbbbccccddddeeeeeeeeeeee",
            "azure-destroy-sub",
            "azure-destroy-sub-hash",
            DateTimeOffset.UtcNow);
        var request = new ProvisioningRequest(
            ProvisioningCommand.Destroy,
            new EngineSpec(
                "elsa-server:test",
                new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime }),
            AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging"),
            "azure-destroy-sub",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceGroupName"] = "elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"
            });
        var result = await provider.DestroyAsync(operation, request);
        Assert.True(result.Succeeded);
        Assert.True(result.OwnedResourcesAbsent);
        Assert.Equal("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", probe.LastName);
        Assert.Equal(subscription, probe.LastSubscription);
        Assert.Contains(result.Events, value => value.Message == "Owned resource group is absent.");
    }

    [Fact]
    public async Task Cancelled_create_then_destroy_certifies_absence()
    {
        var automation = new FakePulumiAutomationClient { Stall = TimeSpan.FromSeconds(30) };
        var probe = new RecordingResourceGroupAbsenceProbe { Result = ResourceGroupPresence.Absent };
        var secrets = new EmptyProvisioningSecretResolver();
        var subscription = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToString("D");
        var azure = new AzureInfrastructureProvider(
            new AzurePulumiAutomationRunner(),
            new StaticCredentialResolver(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AZURE_CLIENT_ID"] = "workload-identity-client",
                ["ARM_SUBSCRIPTION_ID"] = subscription
            }),
            automation,
            probe,
            secrets);
        var store = new ElsaControl.Provisioning.Core.Stores.InMemoryProvisioningStore();
        var runs = new ProvisioningRunRegistry();
        var service = new ProvisioningService(store, store, runs);
        var worker = new ProvisioningOperationWorker(
            store,
            store,
            azure,
            runs,
            heartbeat: TimeSpan.FromMilliseconds(25));
        var engine = AzureTarget("engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", ProvisioningStackKind.Engine, "staging");
        var spec = new EngineSpec(
            "elsa-server:test",
            new HashSet<string>(StringComparer.Ordinal) { ProvisioningCapability.ContainerRuntime });
        var config = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engineName"] = "engine-one",
            ["resourceGroupName"] = "elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee"
        };

        var create = await service.ProvisionAsync(new ProvisioningRequest(
            ProvisioningCommand.Provision,
            spec,
            engine,
            "azure-cancel-create",
            config));
        var processing = worker.ProcessOnceAsync();
        await automation.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = await service.CancelAsync(new ProvisioningRequest(
            ProvisioningCommand.Cancel,
            spec,
            engine,
            "azure-cancel-signal",
            config));
        Assert.Equal(ProvisioningOperationState.Succeeded, cancel.State);
        await processing;
        var created = await service.GetOperationAsync(create.OperationId);
        Assert.Equal(ProvisioningOperationState.Cancelled, created!.State);
        Assert.True(created.StateLockMayBeHeld);

        await worker.ReconcileInterruptedAsync();
        Assert.False((await service.GetOperationAsync(create.OperationId))!.StateLockMayBeHeld);

        var destroy = await service.DestroyAsync(new ProvisioningRequest(
            ProvisioningCommand.Destroy,
            spec,
            engine,
            "azure-cancel-destroy",
            config));
        Assert.Equal(ProvisioningOperationState.Queued, destroy.State);
        Assert.Equal(1, await worker.ProcessOnceAsync());
        var destroyed = await service.GetOperationAsync(destroy.OperationId);
        Assert.Equal(ProvisioningOperationState.Succeeded, destroyed!.State);
        Assert.Contains(automation.Commands, command => command == ProvisioningCommand.Cancel);
        Assert.Equal(ProvisioningCommand.Destroy, automation.LastCommand);
        Assert.Equal("elsa-engine-aaaaaaaabbbbccccddddeeeeeeeeeeee", probe.LastName);
        Assert.Equal(subscription, probe.LastSubscription);
    }

    private sealed class StaticCredentialResolver(IReadOnlyDictionary<string, string> environment) : ICredentialResolver
    {
        public string Kind => CredentialReference.AzureWorkloadIdentityKind;

        public Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(reference);
            return Task.FromResult(environment);
        }
    }

    private static ProvisioningTarget AzureTarget(
        string stackName,
        ProvisioningStackKind kind,
        string? platformStackName = null) =>
        new(
            AzurePulumiAutomationRunner.ProviderId,
            kind,
            stackName,
            new CredentialReference(CredentialReference.AzureWorkloadIdentityKind, "workload-identity-client"),
            new StateBackendReference(StateBackendReference.AzureBlobKind, "stelsaplatform/pulumi-state"),
            new SecretsProviderReference(SecretsProviderReference.AzureKeyVaultKind, "kv-elsa-platform/pulumi-secrets"),
            platformStackName);
}
