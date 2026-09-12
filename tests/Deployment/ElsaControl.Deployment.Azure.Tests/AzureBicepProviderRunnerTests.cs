using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureBicepProviderRunnerTests : IDisposable
{
    private const string OwnedGroupTags = "{\"managed-by\":\"elsa-control\",\"owner\":\"elsa-control\",\"workload-name\":\"proof\",\"sqlBootstrapObjectId\":\"11111111-1111-1111-1111-111111111111\"}";

    private const string HealthyCandidateRevision = "{\"active\":true,\"health\":\"Healthy\",\"fqdn\":\"proof-app--candidate.hash.azurecontainerapps.io\"}";

    private static bool IsCandidateReadinessProbe(string[] args) =>
        args.Contains("--fail") && args.Contains("https://proof-app--candidate.hash.azurecontainerapps.io/health");

    private AzureProviderResourceReferences PromotionResources() => _fixture.FoundationResources with
    {
        RegistryResourceId = _fixture.RegistryId,
        AcrPullDeploymentId = _fixture.RegistryDeploymentId,
        AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId,
        WorkloadResourceId = _fixture.AppId,
        WorkloadDeploymentId = _fixture.WorkloadDeploymentId,
        WorkloadRevisionName = "proof-app--candidate",
        StableTrafficRevisionName = "proof-app--stable"
    };
    private const string ExactSqlBootstrapFirewall = "[{\"name\":\"elsa-bootstrap\",\"startIpAddress\":\"203.0.113.10\",\"endIpAddress\":\"203.0.113.10\"}]";
    private static readonly AzureWorkloadCapacity StandardSmall = GovernedCapacity("standard-small");
    private readonly RunnerFixture _fixture = new();

    [Theory]
    [InlineData(AzureProviderRunnerStep.Foundation, "standard-small", "1", "1", "0.5", "1Gi")]
    [InlineData(AzureProviderRunnerStep.Workload, "standard-small", "1", "1", "0.5", "1Gi")]
    [InlineData(AzureProviderRunnerStep.Foundation, "standard", "1", "3", "1", "2Gi")]
    [InlineData(AzureProviderRunnerStep.Workload, "standard", "1", "3", "1", "2Gi")]
    public async Task Production_deployment_passes_the_exact_governed_capacity_to_Bicep(
        AzureProviderRunnerStep step, string profile, string minReplicas, string maxReplicas, string cpu, string memory)
    {
        var deployment = await ProductionDeploymentAsync(step, _fixture.Plan with { Capacity = GovernedCapacity(profile) });

        Assert.Equal(
            [$"workloadMinReplicas={minReplicas}", $"workloadMaxReplicas={maxReplicas}", $"workloadCpu={cpu}", $"workloadMemory={memory}"],
            deployment.Where(IsCapacityArgument));
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.Foundation, 1, 1, 500, 2048)]
    [InlineData(AzureProviderRunnerStep.Workload, 1, 1, 500, 2048)]
    [InlineData(AzureProviderRunnerStep.Workload, 1, 1, 300, 600)]
    [InlineData(AzureProviderRunnerStep.Workload, 1, 1, 4000, 8192)]
    [InlineData(AzureProviderRunnerStep.Workload, 0, 0, 500, 1024)]
    [InlineData(AzureProviderRunnerStep.Workload, 2, 1, 500, 1024)]
    [InlineData(AzureProviderRunnerStep.Foundation, 1, 301, 500, 1024)]
    public async Task Production_deployment_without_an_exact_Container_Apps_mapping_fails_before_any_Azure_call(
        AzureProviderRunnerStep step, int minReplicas, int maxReplicas, int cpuMillicores, int memoryMiB)
    {
        var (result, process) = await RunWithCapacityAsync(step, new(minReplicas, maxReplicas, cpuMillicores, memoryMiB));

        AssertFailedClosed(result, process, "azure.capacity.unsupported");
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.Foundation)]
    [InlineData(AzureProviderRunnerStep.AcrPull)]
    [InlineData(AzureProviderRunnerStep.SeedSecrets)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrap)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript)]
    [InlineData(AzureProviderRunnerStep.Workload)]
    public async Task Production_deployment_of_a_plan_retained_without_capacity_fails_before_any_Azure_call(AzureProviderRunnerStep step)
    {
        var (result, process) = await RunWithCapacityAsync(step, capacity: null);

        AssertFailedClosed(result, process, "azure.capacity.required");
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup)]
    [InlineData(AzureProviderRunnerStep.Health)]
    [InlineData(AzureProviderRunnerStep.Promotion)]
    [InlineData(AzureProviderRunnerStep.RestoreStableTraffic)]
    [InlineData(AzureProviderRunnerStep.Cleanup)]
    public async Task Steps_that_keep_an_existing_instance_recoverable_do_not_require_capacity(AzureProviderRunnerStep step)
    {
        var (result, _) = await RunWithCapacityAsync(step, capacity: null);

        Assert.False(result.Code.StartsWith("azure.capacity.", StringComparison.Ordinal), result.Code);
    }

    [Fact]
    public async Task Disposable_deployment_keeps_the_cost_boxed_template_sizing_and_takes_no_capacity()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("create"), FoundationOutputs());
        var options = _fixture.Options with
        {
            DisposableProofMode = true,
            DisposableExpiryUtc = new DateOnly(2026, 9, 30),
            AzureCliClientId = null
        };
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Plan = _fixture.Plan with { Capacity = null },
            Context = _fixture.Context with { ProviderScopeFingerprint = options.ComputeProviderScopeFingerprint(_fixture.Scope) }
        };

        var result = await new AzureBicepProviderRunner(options, _fixture.Scope, process).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(process.Calls.Single(call => call.Contains("deployment")), IsCapacityArgument);
    }

    [Fact]
    public async Task Disposable_foundation_preserves_the_proof_template_and_ownership_contract()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("create"), FoundationOutputs());
        var options = _fixture.Options with
        {
            DisposableProofMode = true,
            DisposableExpiryUtc = new DateOnly(2026, 9, 30),
            AzureCliClientId = null,
            RuntimeAdminUsername = "proof-admin"
        };
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Context = _fixture.Context with { ProviderScopeFingerprint = options.ComputeProviderScopeFingerprint(_fixture.Scope) }
        };
        var runner = new AzureBicepProviderRunner(options, _fixture.Scope, process);

        var result = await runner.RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        var create = process.Calls.Single(call => call is ["group", "create", ..]);
        Assert.Contains("proof=108", create);
        Assert.Contains("proof-name=proof", create);
        Assert.Contains("expiry=2026-09-30", create);
        var deployment = process.Calls.Single(call => call.Contains("deployment"));
        Assert.Contains("proofName=proof", deployment);
        Assert.Contains("expiryUtc=2026-09-30", deployment);
        Assert.Contains("adminUsername=proof-admin", deployment);
        Assert.DoesNotContain(deployment, value => value.StartsWith("workloadName=", StringComparison.Ordinal) ||
            value.StartsWith("releaseLine=", StringComparison.Ordinal) ||
            value.StartsWith("releaseFeedServiceIndex=", StringComparison.Ordinal));
        Assert.Contains("/elsa108-", result.Resources.FoundationDeploymentId);
    }

    [Fact]
    public async Task Foundation_projects_only_exact_resource_references()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("create"), FoundationOutputs());

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(_fixture.Scope.ResourceGroupName, result.Resources.ResourceGroupName);
        Assert.Equal(
            "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/proof-identity",
            result.Resources.WorkloadIdentityResourceId);
        Assert.Equal("proof-sql.database.windows.net", result.Resources.SqlServerFqdn);
        var deploymentCall = process.Calls.Single(call => call.Contains("deployment") && call.Contains("create"));
        Assert.DoesNotContain("topology=", string.Join(" ", deploymentCall), StringComparison.Ordinal);
        Assert.All(process.Calls, call => Assert.DoesNotContain("secret://", string.Join(" ", call), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Production_foundation_and_workload_pass_the_governed_release_feed_to_Bicep()
    {
        const string feed = "https://pkgs.example.test/v3/index.json";
        var options = _fixture.Options with { ReleaseFeedServiceIndex = feed };
        var context = _fixture.Context with
        {
            ProviderScopeFingerprint = options.ComputeProviderScopeFingerprint(_fixture.Scope)
        };

        var foundationProcess = new FakeCommandProcess();
        foundationProcess.Success(args => args is ["group", "exists", ..], "false");
        foundationProcess.Success(args => args is ["group", "create", ..]);
        foundationProcess.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("create"), FoundationOutputs());

        var foundation = await new AzureBicepProviderRunner(options, _fixture.Scope, foundationProcess)
            .RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation) with { Context = context });

        Assert.Equal(AzureProviderRunnerOutcome.Completed, foundation.Outcome);
        var foundationDeployment = foundationProcess.Calls.Single(call => call.Contains("deployment") && call.Contains("create"));
        Assert.Contains($"releaseFeedServiceIndex={feed}", foundationDeployment);

        var workloadProcess = new FakeCommandProcess();
        workloadProcess.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        workloadProcess.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        workloadProcess.Success(args => args.Contains("deployment") && args.Contains("create"), WorkloadOutputs());
        workloadProcess.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
        workloadProcess.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
        workloadProcess.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var workload = await new AzureBicepProviderRunner(options, _fixture.Scope, workloadProcess)
            .RunAsync(_fixture.Command(AzureProviderRunnerStep.Workload, resources) with { Context = context });

        Assert.Equal(AzureProviderRunnerOutcome.Completed, workload.Outcome);
        var workloadDeployment = workloadProcess.Calls.Single(call => call.Contains("deployment") && call.Contains("create"));
        Assert.Contains($"releaseFeedServiceIndex={feed}", workloadDeployment);
    }

    [Fact]
    public async Task Foundation_accepts_irrelevant_mixed_typed_outputs_when_consumed_values_are_strings()
    {
        var result = await RunFoundationAsync(FoundationOutputs());

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal("proof-rg", result.Resources.ResourceGroupName);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{\"kind\":\"metadata\"}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public async Task Foundation_rejects_non_string_consumed_output_values(string value)
    {
        var result = await RunFoundationAsync(FoundationOutputsWithResourceGroupValue(value));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.foundation.output-invalid", result.Code);
    }

    [Fact]
    public async Task Foundation_rejects_missing_consumed_output_values()
    {
        var result = await RunFoundationAsync(FoundationOutputsWithoutResourceGroup());

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.foundation.output-invalid", result.Code);
    }

    [Fact]
    public async Task Foundation_rejects_null_consumed_output_entries()
    {
        var result = await RunFoundationAsync(FoundationOutputsWithNullResourceGroupEntry());

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.foundation.output-invalid", result.Code);
    }

    [Fact]
    public async Task Assigned_foundation_targets_only_the_dedicated_resource_group()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("create"),
            FoundationOutputs().Replace("proof-rg", "rg-elsa-dedicated", StringComparison.Ordinal));
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation);
        var assignmentId = Guid.Parse(command.Context.ProviderAssignmentId);
        command = command with
        {
            Assignment = new(
                assignmentId,
                command.Context.WorkspaceId,
                command.Context.OrganizationId,
                command.Context.InstanceId,
                command.Context.ProviderScopeFingerprint!,
                1,
                _fixture.Scope.SubscriptionId,
                "rg-elsa-dedicated",
                command.Plan.WorkloadName,
                new string('f', 64),
                command.Plan.Location,
                AzureProviderAssignmentState.Reserved,
                new(),
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)
        };

        var result = await _fixture.Runner(process).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal("rg-elsa-dedicated", result.Resources.ResourceGroupName);
        Assert.All(process.Calls, call =>
            Assert.DoesNotContain("proof-rg", string.Join(" ", call), StringComparison.Ordinal));
        Assert.All(process.Calls.Where(call => call.Contains("group")), call =>
            Assert.Contains("rg-elsa-dedicated", call));
    }

    [Fact]
    public async Task Foundation_uncertainty_preserves_deterministic_partial_cleanup_handles()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Status(args => args.Contains("deployment") && args.Contains("create"),
            AzureCommandProcessStatus.TerminationUncertain,
            AzureCommandProcessFailureKind.TerminationUncertain);

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("proof-rg", result.Resources.ResourceGroupName);
        Assert.Equal(_fixture.FoundationResources.KeyVaultResourceId, result.Resources.KeyVaultResourceId);
        Assert.Equal(_fixture.FoundationResources.WorkloadIdentityResourceId, result.Resources.WorkloadIdentityResourceId);
        Assert.NotNull(result.Resources.FoundationDeploymentId);
    }

    [Fact]
    public async Task Recovery_observer_confirms_owned_foundation_without_mutation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("deployment") && args.Contains("show"), "Succeeded");
        var foundation = _fixture.FoundationResources with
        {
            FoundationDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/elsa-proof-aaaaaaaaaaaa-foundation"
        };

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(foundation, AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.Foundation, observation.CompletedStep);
        Assert.Equal("azure.recovery.foundation-observed", observation.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("set"));
    }

    [Fact]
    public async Task Recovery_observer_derives_and_confirms_the_exact_owned_acr_assignment()
    {
        var process = new FakeCommandProcess();
        var canonicalRole = "/subscriptions/22222222-2222-2222-2222-222222222222/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId +
            "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"" + canonicalRole + "\"}]");
        process.Success(args => args.Contains("deployment") && args.Contains("show"), "Succeeded");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = null
        };

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.AcrPull));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.AcrPull, observation.CompletedStep);
        Assert.Equal(_fixture.RegistryRoleAssignmentId, observation.Resources.AcrPullRoleAssignmentId);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("set"));
    }

    [Fact]
    public async Task Recovery_observer_keeps_an_absent_acr_assignment_in_progress_then_confirms_late_exact_visibility()
    {
        var process = new FakeCommandProcess();
        var canonicalRole = "/subscriptions/22222222-2222-2222-2222-222222222222/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = null
        };
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[]");

        var first = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.AcrPull));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, first.Kind);
        Assert.Null(first.CompletedStep);

        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId +
            "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"" + canonicalRole + "\"}]");
        process.Success(args => args.Contains("deployment") && args.Contains("show"), "Succeeded");

        var second = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.AcrPull));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, second.Kind);
        Assert.Equal(AzureProviderRunnerStep.AcrPull, second.CompletedStep);
        Assert.Equal(_fixture.RegistryRoleAssignmentId, second.Resources.AcrPullRoleAssignmentId);
        Assert.Equal(3, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("set"));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("")]
    public async Task Recovery_observer_keeps_a_nonterminal_foundation_deployment_in_progress_without_mutation(string deploymentState)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("deployment") && args.Contains("show"), deploymentState);
        var foundation = _fixture.FoundationResources with
        {
            FoundationDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/elsa-proof-aaaaaaaaaaaa-foundation"
        };

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(foundation, AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.Equal(3, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("set"));
    }

    [Fact]
    public async Task Recovery_observer_keeps_an_uncertain_foundation_deployment_in_progress_without_mutation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Status(args => args.Contains("deployment") && args.Contains("show"),
            AzureCommandProcessStatus.TerminationUncertain,
            AzureCommandProcessFailureKind.TerminationUncertain);
        var foundation = _fixture.FoundationResources with
        {
            FoundationDeploymentId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/elsa-proof-aaaaaaaaaaaa-foundation"
        };

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(foundation, AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.Equal(3, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("set"));
    }

    [Fact]
    public async Task Recovery_observer_rejects_a_deterministic_acr_assignment_with_an_extra_scoped_match()
    {
        var process = new FakeCommandProcess();
        var canonicalRole = "/subscriptions/22222222-2222-2222-2222-222222222222/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";
        var foreign = _fixture.RegistryId + "/providers/Microsoft.Authorization/roleAssignments/99999999-9999-9999-9999-999999999999";
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId +
            "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"" + canonicalRole + "\"},{\"id\":\"" + foreign +
            "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"" + canonicalRole + "\"}]");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = null
        };

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.AcrPull));

        Assert.Equal(AzureProviderRecoveryObservationKind.Ambiguous, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Recovery_observer_confirms_an_exact_owned_sql_firewall_create_without_mutation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        var resources = SqlFoundationResources();

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.FoundationSubmitted));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.SqlFirewallCreate, observation.CompletedStep);
        Assert.Equal("azure.recovery.sql-firewall-create-observed", observation.Code);
        Assert.Single(process.Calls);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("-Q"));
    }

    [Theory]
    [InlineData(AzureProviderOperationPhase.FoundationSubmitted)]
    [InlineData(AzureProviderOperationPhase.SeedSecretsObserved)]
    public async Task Recovery_observer_keeps_an_absent_uncertain_sql_firewall_create_in_progress(AzureProviderOperationPhase phase)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlFirewallCreate, phase));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Recovery_observer_confirms_late_exact_sql_firewall_visibility_without_replaying_creation_or_script()
    {
        var process = new FakeCommandProcess();
        var resources = SqlFoundationResources();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var first = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.SqlFirewallCreate,
                AzureProviderOperationPhase.FoundationSubmitted));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, first.Kind);
        Assert.Null(first.CompletedStep);

        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);

        var second = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(resources, AzureProviderRunnerStep.SqlFirewallCreate,
                AzureProviderOperationPhase.FoundationSubmitted));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, second.Kind);
        Assert.Equal(AzureProviderRunnerStep.SqlFirewallCreate, second.CompletedStep);
        Assert.Equal(resources, second.Resources);
        Assert.Equal(2, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete") || call.Contains("-Q"));
    }

    [Fact]
    public async Task Recovery_observer_confirms_sql_script_only_after_exact_principal_and_roles_are_proven()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("-Q"), "complete");

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlFirewallReady));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.SqlBootstrapScript, observation.CompletedStep);
        Assert.Equal("azure.recovery.sql-bootstrap-observed", observation.Code);
        var queryArguments = Assert.Single(process.Calls, call => call.Contains("-Q"));
        var query = queryArguments[Array.IndexOf(queryArguments, "-Q") + 1];
        Assert.Contains("sys.database_principals", query);
        Assert.Contains("db_datareader", query);
        Assert.Contains("db_datawriter", query);
        Assert.Contains("db_ddladmin", query);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete"));
    }

    [Fact]
    public async Task Recovery_observer_does_not_confirm_sql_script_for_an_incomplete_postcondition()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("-Q"), "incomplete");

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlFirewallReady));

        Assert.Equal(AzureProviderRecoveryObservationKind.InProgress, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete"));
    }

    [Fact]
    public async Task Recovery_observer_confirms_sql_cleanup_from_exact_firewall_absence_without_sql_replay()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.SqlBootstrapReady));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.SqlFirewallCleanup, observation.CompletedStep);
        Assert.Equal("azure.recovery.sql-firewall-cleanup-observed", observation.Code);
        Assert.Single(process.Calls);
        Assert.DoesNotContain(process.Calls, call => call.Contains("-Q") || call.Contains("delete") || call.Contains("create"));
    }

    [Fact]
    public async Task Recovery_observer_allows_cleanup_replay_only_when_firewall_and_sql_postcondition_are_exact()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("-Q"), "complete");

        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.SqlBootstrapReady));

        Assert.Equal(AzureProviderRecoveryObservationKind.Confirmed, observation.Kind);
        Assert.Equal(AzureProviderRunnerStep.SqlBootstrapScript, observation.CompletedStep);
        Assert.Equal("azure.recovery.sql-bootstrap-observed", observation.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("create") || call.Contains("delete"));
    }

    [Fact]
    public async Task Recovery_observer_rejects_legacy_sql_bootstrap_marker_without_remote_reads()
    {
        var process = new FakeCommandProcess();
        var observation = await _fixture.Runner(process)
            .ObserveAsync(CreateRecoveryRequest(SqlFoundationResources(), AzureProviderRunnerStep.SqlBootstrap, AzureProviderOperationPhase.FoundationSubmitted));

        Assert.Equal(AzureProviderRecoveryObservationKind.Ambiguous, observation.Kind);
        Assert.Null(observation.CompletedStep);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Rejects_a_durable_command_bound_to_a_different_provider_scope_before_execution()
    {
        var process = new FakeCommandProcess();
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Context = _fixture.Context with { ProviderScopeFingerprint = new string('f', 64) }
        };

        var result = await _fixture.Runner(process).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.runner.scope-invalid", result.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Rejects_a_durable_plan_outside_the_immutable_elsa_combined_profile()
    {
        var process = new FakeCommandProcess();
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Plan = _fixture.Plan with { ElsaVersion = "3.9.1" }
        };

        var result = await _fixture.Runner(process).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.runner.input-invalid", result.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Accepts_an_exact_version_within_the_governed_release_line()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Plan = _fixture.Plan with { ElsaVersion = "3.8.0-preview.5413" }
        };

        var result = await _fixture.Runner(process).RunAsync(command);

        Assert.NotEqual("azure.runner.input-invalid", result.Code);
        Assert.NotEmpty(process.Calls);
    }

    [Fact]
    public async Task Passes_admitted_non_38_package_versions_to_the_Bicep_authority()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("create"), FoundationOutputs());
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Plan = _fixture.Plan with
            {
                ElsaVersion = "5.0.0",
                ReleaseLine = "5.0",
                SqlWorkflowPackageVersion = "5.0.1",
                SqlQuartzPackageVersion = "5.0.2"
            }
        };

        await _fixture.Runner(process).RunAsync(command);

        var args = process.Calls.Single(call => call.Contains("deployment") && call.Contains("create"));
        Assert.Contains("sqlWorkflowPackageVersion=5.0.1", args);
        Assert.Contains("sqlQuartzPackageVersion=5.0.2", args);
    }

    [Fact]
    public async Task Stops_before_a_following_mutation_when_template_authority_drifts()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false", () => File.AppendAllText(Path.Combine(_fixture.TemplateRoot, "main.bicep"), "\n// drift"));
        process.Success(args => args.Contains("group") && args.Contains("create"));

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.runner.uncertain", result.Code);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Seeds_transient_secret_values_through_a_file_and_clears_the_file()
    {
        var process = new FakeCommandProcess();
        string? transientFile = null;
        string? observedSecret = null;
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[]");
        process.Success(args =>
        {
            transientFile = args[Array.IndexOf(args, "--file") + 1];
            observedSecret = File.ReadAllText(transientFile);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(transientFile));
            return true;
        });
        var resolver = new RecordingSecretResolver("database-password");
        var command = _fixture.Command(AzureProviderRunnerStep.SeedSecrets, _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        }) with
        {
            Plan = _fixture.Plan with { SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database"
            }}
        };

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal("database-password", observedSecret);
        Assert.NotNull(transientFile);
        Assert.False(File.Exists(transientFile));
        Assert.Single(resolver.Requests);
        Assert.Equal(command.Context.OperationId, resolver.Requests[0].OperationId);
        Assert.Equal(command.AttemptNumber, resolver.Requests[0].AttemptNumber);
        Assert.Same(resolver.Requests[0], resolver.AuthorizationRequests[0]);
        Assert.DoesNotContain("database-password", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain("database-password", string.Join(" ", process.Calls.SelectMany(x => x)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("denied", "azure.secrets.authorization-changed")]
    [InlineData("failed", "azure.runner.uncertain")]
    [InlineData("cancelled", "azure.runner.cancelled")]
    public async Task Rejects_changed_secret_authorization_before_set_and_cleans_the_transient_file(
        string authorizationOutcome, string expectedCode)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[]");
        var resolver = new RecordingSecretResolver("must-not-be-written")
        {
            AuthorizationResult = false,
            AuthorizationFailure = authorizationOutcome switch
            {
                "failed" => new InvalidOperationException("must-not-appear-in-diagnostics"),
                "cancelled" => new OperationCanceledException("must-not-appear-in-diagnostics"),
                _ => null
            }
        };
        var command = _fixture.Command(AzureProviderRunnerStep.SeedSecrets, _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        }) with
        {
            Plan = _fixture.Plan with { SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database"
            }}
        };
        var before = Directory.GetDirectories(Path.GetTempPath(), "elsa-azure-*");

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal(expectedCode, result.Code);
        Assert.Single(resolver.Requests);
        Assert.Single(resolver.AuthorizationRequests);
        Assert.Equal(resolver.Requests[0], resolver.AuthorizationRequests[0]);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
        Assert.Equal(before.Order(StringComparer.Ordinal),
            Directory.GetDirectories(Path.GetTempPath(), "elsa-azure-*").Order(StringComparer.Ordinal));
        Assert.DoesNotContain("must-not-appear-in-diagnostics", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_provider_owned_secret_metadata_skips_regeneration()
    {
        var process = new FakeCommandProcess();
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand();
        process.Success(args => args.Contains("secret") && args.Contains("list"),
            $"[{{\"managedBy\":\"elsa-control\",\"assignmentId\":\"{command.Context.ProviderAssignmentId}\",\"instanceId\":\"{command.Context.InstanceId:D}\",\"secretSlot\":\"admin-password\",\"generation\":\"provider-v1\"}}]");
        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.NoOp, result.Outcome);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("show"));
        var list = Assert.Single(process.Calls, call => call.Contains("secret") && call.Contains("list"));
        Assert.Contains(list, value => value.Contains("[?name=='admin-password']", StringComparison.Ordinal));
        Assert.Contains(list, value => value.Contains("secretSlot", StringComparison.Ordinal));
        Assert.Contains("--output", list);
        Assert.Contains("json", list);
    }

    [Fact]
    public async Task Existing_unmarked_provider_owned_secret_fails_closed_without_overwrite()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[{}]");
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand();

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.secrets.metadata-invalid", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
    }

    [Fact]
    public async Task Ambiguous_provider_owned_secret_metadata_fails_closed_without_overwrite()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[{},{}]");
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand();

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.secrets.inventory-invalid", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("show"));
    }

    [Fact]
    public async Task Null_provider_owned_secret_metadata_fails_closed_without_overwrite()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[null]");
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand();

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.secrets.metadata-invalid", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
    }

    [Fact]
    public async Task Wrong_type_provider_owned_secret_metadata_fails_closed_without_overwrite()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[1]");
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand();

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.step.failed", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
    }

    [Fact]
    public async Task New_provider_owned_secret_records_only_safe_ownership_metadata()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[]");
        string[]? setArguments = null;
        process.Success(args =>
        {
            setArguments = args;
            return args.Contains("secret") && args.Contains("set");
        });
        var resolver = new RecordingSecretResolver("generated-value");
        var command = GeneratedAdminSeedCommand();

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.NotNull(setArguments);
        Assert.Contains("--tags", setArguments!);
        Assert.Contains("managed-by=elsa-control", setArguments!);
        Assert.Contains($"provider-assignment={command.Context.ProviderAssignmentId}", setArguments!);
        Assert.Contains($"instance={command.Context.InstanceId:D}", setArguments!);
        Assert.Contains("secret-slot=admin-password", setArguments!);
        Assert.Contains("generation=provider-v1", setArguments!);
        Assert.DoesNotContain("generated-value", string.Join(" ", setArguments!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resumed_provider_owned_seed_fails_closed_when_secret_is_absent()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("secret") && args.Contains("list"), "[]");
        var resolver = new RecordingSecretResolver("must-not-be-generated");
        var command = GeneratedAdminSeedCommand(resume: true);

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.secrets.recovery-required", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("secret") && call.Contains("set"));
    }

    [Fact]
    public async Task Secret_observation_failure_does_not_resolve_or_seed_a_secret()
    {
        var process = new FakeCommandProcess();
        process.Failure(args => args.Contains("secret") && args.Contains("list"));
        var resolver = new RecordingSecretResolver("database-password");
        var command = _fixture.Command(AzureProviderRunnerStep.SeedSecrets, _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        }) with
        {
            Plan = _fixture.Plan with { SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database"
            }}
        };

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Empty(resolver.Requests);
        Assert.DoesNotContain(process.Calls, call => call.Contains("set"));
    }

    [Fact]
    public async Task Colliding_secret_names_fail_before_resolution_or_seeding()
    {
        var process = new FakeCommandProcess();
        var resolver = new RecordingSecretResolver("database-password");
        var command = _fixture.Command(AzureProviderRunnerStep.SeedSecrets, _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        }) with
        {
            Plan = _fixture.Plan with { SecretReferences = new Dictionary<string, string>
            {
                ["database:password"] = "secret://vault/database-password",
                ["database_password"] = "secret://vault/other-database-password"
            }}
        };

        var result = await _fixture.Runner(process, resolver).RunAsync(command);

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.runner.input-invalid", result.Code);
        Assert.Empty(resolver.Requests);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Does_not_promote_a_candidate_that_is_not_active_and_healthy()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("containerapp") && args.Contains("show") && args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("containerapp") && args.Contains("revision") && args.Contains("show"), "{\"active\":true,\"health\":\"Degraded\"}");
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.promotion.health-gate", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic") && call.Contains("set"));
    }

    [Fact]
    public async Task Reports_unhealthy_candidate_without_traffic_mutation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("containerapp") && args.Contains("show") && args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("containerapp") && args.Contains("revision") && args.Contains("show"), "{\"active\":true,\"health\":\"Unhealthy\"}");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId,
            WorkloadResourceId = _fixture.AppId,
            WorkloadDeploymentId = _fixture.WorkloadDeploymentId,
            WorkloadRevisionName = "proof-app--candidate"
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Health, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderHealth.Failed, result.Health);
        Assert.Equal("azure.health.unhealthy", result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.step.health.failed");
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic"));
    }

    [Fact]
    public async Task Cleanup_refuses_to_delete_when_inventory_contains_a_foreign_resource()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"), "[{\"id\":\"/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/foreign/providers/Microsoft.App/containerApps/other\",\"type\":\"Microsoft.App/containerApps\"}]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.cleanup.ownership-unverified", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("delete"));
    }

    [Fact]
    public async Task Cleanup_does_not_reject_owned_child_resources_under_governed_roots()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.SqlServerResourceId + "/databases/elsa\",\"type\":\"Microsoft.Sql/servers/databases\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("delete"));
        process.Success(args => args.Contains("group") && args.Contains("delete"));

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup));

        Assert.NotEqual("azure.cleanup.ownership-unverified", result.Code);
    }

    [Fact]
    public async Task Cleanup_deletes_an_owned_group_when_foundation_failed_before_vault_creation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("group") && args.Contains("delete"));
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("role") && call.Contains("assignment") && call.Contains("list"));
    }

    [Fact]
    public async Task Cleanup_refuses_a_vault_user_assignment_without_a_proven_workload_principal()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"type\":\"Microsoft.KeyVault/vaults\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"),
            "[{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6\"}]");
        var partial = _fixture.FoundationResources with { WorkloadIdentityPrincipalId = null };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, partial));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.cleanup.rbac-unverified", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("delete"));
    }

    [Fact]
    public async Task Cleanup_recovers_the_exact_owned_identity_principal_before_rbac_validation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.WorkloadIdentityResourceId + "\",\"type\":\"Microsoft.ManagedIdentity/userAssignedIdentities\"}," +
            "{\"id\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"type\":\"Microsoft.KeyVault/vaults\"}]");
        process.Success(args => args.Contains("identity") && args.Contains("show"), "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"),
            "[{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6\"}]");
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("group") && args.Contains("delete"));
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");
        var partial = _fixture.FoundationResources with { WorkloadIdentityPrincipalId = null };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, partial));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Contains(process.Calls, call => call.Contains("identity") && call.Contains("show"));
    }

    [Fact]
    public async Task Acr_pull_binds_the_role_to_the_exact_registry_scope()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("acr") && args.Contains("show"), _fixture.RegistryId);
        process.Success(args => args.Contains("deployment") && args.Contains("create"), "{\"roleAssignmentId\":{\"value\":\"" + _fixture.RegistryRoleAssignmentId + "\"}}");
        process.Success(args => args.Contains("role") && args.Contains("list"), "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/subscriptions/22222222-2222-2222-2222-222222222222/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\"}]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.AcrPull, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(_fixture.RegistryId, result.Resources.RegistryResourceId);
        Assert.Equal(_fixture.RegistryRoleAssignmentId, result.Resources.AcrPullRoleAssignmentId);
        AssertRegistryRoleObservationsAreScoped(process);
    }

    [Fact]
    public async Task Acr_pull_uncertainty_preserves_deterministic_cleanup_handles()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("acr") && args.Contains("show"), _fixture.RegistryId);
        process.Failure(args => args.Contains("deployment") && args.Contains("create"));

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.AcrPull, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal(_fixture.RegistryId, result.Resources.RegistryResourceId);
        Assert.Equal(_fixture.RegistryDeploymentId, result.Resources.AcrPullDeploymentId);
    }

    [Fact]
    public async Task Acr_pull_read_only_process_failure_preserves_fixed_step_and_failure_kind()
    {
        var process = new FakeCommandProcess();
        process.Failure(args => args.Contains("acr") && args.Contains("show"));

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.AcrPull, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.step.acr-pull.failed");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.step.acr-pull.process.non-zero-exit");
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(diagnostic.Code, diagnostic.Message));
    }

    [Fact]
    public async Task Cleanup_refuses_an_acr_assignment_that_does_not_match_its_durable_provenance()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"type\":\"Microsoft.KeyVault/vaults\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6\"},{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"11111111-1111-1111-1111-111111111111\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/b86a8fe4-44ce-4948-aee5-eccb2c155cd7\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\"}]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.cleanup.role-provenance-invalid", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("role") && call.Contains("delete"));
    }

    [Fact]
    public async Task Cleanup_refuses_a_non_deterministic_acr_deployment_reference_before_observation()
    {
        var process = new FakeCommandProcess();
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId.Replace("elsa-proof-", "foreign-", StringComparison.Ordinal),
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.runner.input-invalid", result.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Sql_bootstrap_removes_the_temporary_firewall_rule_and_temp_script()
    {
        var process = new FakeCommandProcess();
        string? scriptPath = null;
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args =>
        {
            scriptPath = args[Array.IndexOf(args, "-i") + 1];
            return args.Contains("--authentication-method");
        });
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.NotNull(scriptPath);
        Assert.False(File.Exists(scriptPath));
        var bootstrap = Assert.Single(process.Calls, arguments => arguments.Contains("-i"));
        Assert.Contains("ActiveDirectoryManagedIdentity", bootstrap);
        Assert.Equal(_fixture.Options.AzureCliClientId, bootstrap[Array.IndexOf(bootstrap, "-U") + 1]);
        Assert.DoesNotContain("ActiveDirectoryDefault", bootstrap);
        Assert.Contains("-b", bootstrap);
        var initialList = process.Calls.First(call => call.Contains("firewall-rule") && call.Contains("list"));
        Assert.Contains(_fixture.Scope.SubscriptionId, initialList);
        Assert.Contains(_fixture.Scope.ResourceGroupName, initialList);
        Assert.Contains("proof-sql", initialList);
    }

    [Fact]
    public async Task Sql_bootstrap_accepts_an_already_absent_temporary_firewall()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("--authentication-method"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Theory]
    [InlineData("[{\"name\":\"elsa-bootstrap\",\"startIpAddress\":\"203.0.113.11\",\"endIpAddress\":\"203.0.113.10\"}]")]
    [InlineData("[{\"name\":\"elsa-bootstrap\",\"startIpAddress\":\"203.0.113.10\",\"endIpAddress\":\"203.0.113.10\"},{\"name\":\"elsa-bootstrap\",\"startIpAddress\":\"203.0.113.10\",\"endIpAddress\":\"203.0.113.10\"}]")]
    [InlineData("[{\"name\":\"elsa-bootstrap\"}]")]
    [InlineData("[{\"name\":\"ELSA-BOOTSTRAP\",\"startIpAddress\":\"203.0.113.11\",\"endIpAddress\":\"203.0.113.10\"}]")]
    [InlineData("[null]")]
    [InlineData("[{\"name\":\"\",\"startIpAddress\":\"203.0.113.10\",\"endIpAddress\":\"203.0.113.10\"}]")]
    [InlineData("[{\"name\":\"other\",\"startIpAddress\":\"invalid\",\"endIpAddress\":\"203.0.113.10\"}]")]
    [InlineData("not-json")]
    public async Task Sql_bootstrap_refuses_to_delete_when_firewall_ownership_is_not_proven(string firewallList)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("--authentication-method"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), firewallList);
        // Cleanup retries a failed proof once, but must never issue a delete for an ambiguous rule.
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), firewallList);

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_bootstrap_refuses_to_delete_when_firewall_list_is_denied()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("--authentication-method"));
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("list"));
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("list"));

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_bootstrap_cleans_up_when_firewall_creation_fails_without_starting_bootstrap()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.step.uncertain", result.Code);
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("list"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("--authentication-method"));
    }

    [Fact]
    public async Task Sql_bootstrap_preserves_uncertainty_when_failed_firewall_cleanup_is_not_verified()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("list"));

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.runner.uncertain", result.Code);
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("list"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("--authentication-method"));
    }

    [Theory]
    [InlineData("TerminationUncertain", "TerminationUncertain", "azure.step.termination-uncertain")]
    [InlineData("Cancelled", "Cancelled", "azure.step.cancelled")]
    [InlineData("TimedOut", "TimedOut", "azure.step.uncertain")]
    public async Task Sql_bootstrap_cleans_up_when_firewall_creation_is_not_confirmed(
        string statusName,
        string failureKindName,
        string expectedCode)
    {
        var status = Enum.Parse<AzureCommandProcessStatus>(statusName);
        var failureKind = Enum.Parse<AzureCommandProcessFailureKind>(failureKindName);
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Status(args => args.Contains("firewall-rule") && args.Contains("create"), status, failureKind);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal(expectedCode, result.Code);
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("list"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("--authentication-method"));
    }

    [Fact]
    public async Task Sql_bootstrap_does_not_complete_when_sqlcmd_reports_a_batch_error()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        // Model go-sqlcmd's batch behavior: a SQL error exits nonzero only when -b is present.
        process.SqlBatchError(args => args.Contains("--authentication-method"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.sql.bootstrap-uncertain", result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.step.sql-bootstrap.process.non-zero-exit");
        var bootstrap = Assert.Single(process.Calls, arguments => arguments.Contains("-i"));
        Assert.Contains("-b", bootstrap);
        Assert.Contains(process.Calls, arguments => arguments.Contains("firewall-rule") && arguments.Contains("delete"));
    }

    [Fact]
    public async Task Sql_bootstrap_does_not_retry_when_process_termination_is_uncertain()
    {
        using var fixture = new RunnerFixture(observationAttempts: 3);
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Status(args => args.Contains("--authentication-method"), AzureCommandProcessStatus.TerminationUncertain,
            AzureCommandProcessFailureKind.TerminationUncertain);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        var resources = fixture.FoundationResources with
        {
            RegistryResourceId = fixture.RegistryId,
            AcrPullDeploymentId = fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = fixture.RegistryRoleAssignmentId
        };

        var result = await fixture.Runner(process).RunAsync(fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Single(process.Calls, call => call.Contains("--authentication-method"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.step.sql-bootstrap.process.termination-uncertain");
    }

    [Fact]
    public async Task Sql_bootstrap_treats_a_termination_uncertain_failure_kind_as_non_retryable()
    {
        using var fixture = new RunnerFixture(observationAttempts: 3);
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Status(args => args.Contains("--authentication-method"), AzureCommandProcessStatus.Failed,
            AzureCommandProcessFailureKind.TerminationUncertain);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        var resources = fixture.FoundationResources with
        {
            RegistryResourceId = fixture.RegistryId,
            AcrPullDeploymentId = fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = fixture.RegistryRoleAssignmentId
        };

        var result = await fixture.Runner(process).RunAsync(fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Single(process.Calls, call => call.Contains("--authentication-method"));
    }

    [Fact]
    public async Task Sql_bootstrap_attempts_firewall_cleanup_when_temp_directory_cleanup_fails()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("--authentication-method"), after: () =>
        {
            var script = process.Calls.Last()[Array.IndexOf(process.Calls.Last(), "-i") + 1];
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(script)!, "cleanup-blocker"), "block");
        });
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.SqlBootstrap, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_firewall_create_stage_does_not_start_script_or_cleanup()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("firewall-rule") && args.Contains("create"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlFirewallCreate, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.SqlFirewallReady, result.Phase);
        Assert.Equal(3, process.Calls.Count);
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("-i") || call.Contains("-Q") || call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_firewall_create_stage_is_idempotent_for_an_exact_existing_rule()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlFirewallCreate, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.NoOp, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.SqlFirewallReady, result.Phase);
        Assert.Single(process.Calls);
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
    }

    [Fact]
    public async Task Sql_firewall_create_stage_refuses_a_conflicting_rule_without_mutation()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"),
            "[{\"name\":\"elsa-bootstrap\",\"startIpAddress\":\"203.0.113.11\",\"endIpAddress\":\"203.0.113.11\"}]");

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlFirewallCreate, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.sql.firewall-uncertain", result.Code);
        Assert.Single(process.Calls);
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") && call.Contains("create"));
    }

    [Fact]
    public async Task Sql_bootstrap_script_stage_does_not_create_or_cleanup_a_firewall()
    {
        var process = new FakeCommandProcess();
        string? scriptPath = null;
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Success(args =>
        {
            scriptPath = args[Array.IndexOf(args, "-i") + 1];
            return args.Contains("--authentication-method");
        });

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlBootstrapScript, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.SqlBootstrapReady, result.Phase);
        Assert.NotNull(scriptPath);
        Assert.False(File.Exists(scriptPath));
        Assert.Single(process.Calls, call => call.Contains("-i"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("firewall-rule") || call.Contains("-Q"));
    }

    [Fact]
    public async Task Sql_script_failure_is_not_retried_even_when_read_observations_allow_retries()
    {
        using var fixture = new RunnerFixture(observationAttempts: 3);
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("-?"), "Microsoft sqlcmd --authentication-method ActiveDirectoryDefault");
        process.Failure(args => args.Contains("-i"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        var resources = fixture.FoundationResources with
        {
            RegistryResourceId = fixture.RegistryId,
            AcrPullDeploymentId = fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = fixture.RegistryRoleAssignmentId
        };

        var result = await fixture.Runner(process).RunAsync(
            fixture.Command(AzureProviderRunnerStep.SqlBootstrapScript, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.sql.bootstrap-uncertain", result.Code);
        Assert.Single(process.Calls, call => call.Contains("-i"));
        Assert.Single(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
        Assert.Equal(5, process.Calls.Count);
    }

    [Fact]
    public async Task Sql_bootstrap_script_stage_cleans_firewall_when_required_inputs_are_missing()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");
        var resources = SqlFoundationResources() with { WorkloadIdentityClientId = null };

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlBootstrapScript, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.sql.foundation-missing", result.Code);
        Assert.Equal(3, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("-?") || call.Contains("-i"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_bootstrap_script_stage_cleans_firewall_when_sqlcmd_compatibility_fails()
    {
        var process = new FakeCommandProcess();
        process.Failure(args => args.Contains("-?"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlBootstrapScript, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.step.failed", result.Code);
        Assert.Equal(4, process.Calls.Count);
        Assert.DoesNotContain(process.Calls, call => call.Contains("-i"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_bootstrap_script_stage_requires_uncertain_result_when_failed_preflight_cleanup_is_not_verified()
    {
        var process = new FakeCommandProcess();
        process.Failure(args => args.Contains("-?"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Failure(args => args.Contains("firewall-rule") && args.Contains("list"));

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlBootstrapScript, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.runner.uncertain", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("-i"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Sql_firewall_cleanup_stage_verifies_absence_and_does_not_run_script()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), ExactSqlBootstrapFirewall);
        process.Success(args => args.Contains("firewall-rule") && args.Contains("delete"));
        process.Success(args => args.Contains("firewall-rule") && args.Contains("list"), "[]");

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(AzureProviderRunnerStep.SqlFirewallCleanup, SqlFoundationResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(AzureProviderOperationPhase.FoundationReady, result.Phase);
        Assert.DoesNotContain(process.Calls, call => call.Contains("-i") || call.Contains("-Q"));
        Assert.Contains(process.Calls, call => call.Contains("firewall-rule") && call.Contains("delete"));
    }

    [Fact]
    public async Task Foundation_reapply_restores_and_verifies_the_exact_sql_bootstrap_admin_before_deployment()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "true");
        process.Success(args => args is ["group", "show", ..], OwnedGroupTags);
        process.Success(args => args is ["tag", "update", ..]);
        process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
        process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("ad-admin") && args.Contains("create"));
        process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
        process.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));
        process.Success(args => args.Contains("deployment") && args.Contains("create"), FoundationOutputs());

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        var adminCreate = process.Calls.FindIndex(call => call.Contains("ad-admin") && call.Contains("create"));
        var deploymentCreate = process.Calls.FindIndex(call => call.Contains("deployment") && call.Contains("create"));
        Assert.True(adminCreate >= 0 && adminCreate < deploymentCreate);
    }

    [Fact]
    public async Task Workload_uses_a_deterministic_revision_and_preserves_the_exact_sql_admin()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Success(args => args.Contains("deployment") && args.Contains("create"), WorkloadOutputs());
        process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
        process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
        process.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };
        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Workload, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal($"proof-app--{_fixture.Plan.Fingerprint[..24]}", result.Resources.WorkloadRevisionName);
        Assert.Equal(_fixture.AppId, result.Resources.WorkloadResourceId);
        Assert.DoesNotContain(process.Calls, call => call.Contains("ad-admin") && call.Contains("delete"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("ad-only-auth") && call.Contains("disable"));
    }

    [Fact]
    public async Task Workload_fails_closed_when_the_sql_server_is_missing_after_deployment()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Success(args => args.Contains("deployment") && args.Contains("create"), WorkloadOutputs());
        process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "0");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Workload, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.sql.admin-invalid", result.Code);
    }

    [Fact]
    public async Task Workload_does_not_reuse_a_revision_suffix_with_an_invalid_recovery_ordinal()
    {
        var process = new FakeCommandProcess();
        var invalidSuffix = _fixture.Plan.Fingerprint[..24] + "-rbad";
        process.Success(args => args.Contains("resource") && args.Contains("list"), "1");
        process.Success(args => args.Contains("containerapp") && args.Contains("show") && args.Any(x => x.Contains("traffic", StringComparison.Ordinal)), "[{\"revisionName\":\"proof-app--stable\",\"weight\":100}]");
        process.Success(args => args.Contains("revision") && args.Contains("show"), "{\"active\":true,\"health\":\"Healthy\"}");
        process.Success(args => args.Contains("resource") && args.Contains("list"), "1");
        process.Success(args => args.Contains("resource") && args.Contains("show"), invalidSuffix);
        process.Success(args => args.Contains("revision") && args.Contains("list"), "[\"proof-app--" + invalidSuffix + "\"]");
        process.Success(args => args.Contains("deployment") && args.Contains("create"), WorkloadOutputs());
        process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
        process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
        process.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));

        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Workload, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal($"proof-app--{_fixture.Plan.Fingerprint[..24]}", result.Resources.WorkloadRevisionName);
    }

    [Fact]
    public async Task Promotion_requires_health_then_confirms_single_candidate_traffic()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), HealthyCandidateRevision);
        process.Success(IsCandidateReadinessProbe, "Healthy");
        process.Success(args => args.Contains("traffic") && args.Contains("set"));
        process.Success(args => args.Contains("show") && args.Any(x => x.Contains("traffic", StringComparison.Ordinal)), "[{\"revisionName\":\"proof-app--candidate\",\"weight\":100},{\"revisionName\":\"proof-app--stable\",\"weight\":0}]");
        process.Success(args => args.Contains("--fail") && args.Contains("https://proof-app.hash.azurecontainerapps.io/health"), "Healthy");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal("proof-app--candidate", result.Resources.StableTrafficRevisionName);
        Assert.Equal("https://proof-app.hash.azurecontainerapps.io", result.Endpoint);
        var probe = process.Calls.FindIndex(IsCandidateReadinessProbe);
        var trafficSet = process.Calls.FindIndex(call => call.Contains("traffic") && call.Contains("set"));
        Assert.True(probe >= 0 && probe < trafficSet, "the candidate readiness probe must precede the traffic shift");
    }

    [Theory]
    [InlineData("Degraded", "azure.promotion.candidate-not-ready")]
    [InlineData("Unhealthy", "azure.promotion.candidate-not-ready")]
    [InlineData("<html>Healthy</html>", "azure.promotion.candidate-readiness-invalid")]
    [InlineData("", "azure.promotion.candidate-readiness-invalid")]
    [InlineData("healthy", "azure.promotion.candidate-readiness-invalid")]
    [InlineData(" Healthy ", "azure.promotion.candidate-readiness-invalid")]
    [InlineData("Healthy\n", "azure.promotion.candidate-readiness-invalid")]
    public async Task Promotion_refuses_a_candidate_whose_readiness_report_is_not_healthy(string report, string code)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), HealthyCandidateRevision);
        process.Success(IsCandidateReadinessProbe, report);

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal("proof-app--stable", result.Resources.StableTrafficRevisionName);
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic") && call.Contains("set"));
    }

    [Fact]
    public async Task Promotion_rejects_an_oversized_readiness_response_as_invalid_output()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), HealthyCandidateRevision);
        process.Success(IsCandidateReadinessProbe, new string('H', 65));

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.promotion.candidate-unready", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic") && call.Contains("set"));
    }

    [Fact]
    public async Task Promotion_fails_closed_when_the_candidate_readiness_route_does_not_answer()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), HealthyCandidateRevision);
        process.Failure(IsCandidateReadinessProbe);

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.promotion.candidate-unready", result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.promotion.candidate-unready");
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic") && call.Contains("set"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("proof-app.hash.azurecontainerapps.io")]
    [InlineData("proof-app--stable.hash.azurecontainerapps.io")]
    [InlineData("proof-app--candidate.evil.example")]
    [InlineData("https://proof-app--candidate.hash.azurecontainerapps.io")]
    [InlineData("proof-app--candidate.hash.azurecontainerapps.io/health")]
    public async Task Promotion_refuses_a_candidate_endpoint_outside_the_workload_origin(string fqdn)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), "{\"active\":true,\"health\":\"Healthy\",\"fqdn\":\"" + fqdn + "\"}");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal("azure.promotion.candidate-endpoint-invalid", result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "azure.promotion.candidate-endpoint-invalid");
        Assert.DoesNotContain(process.Calls, call => call.Contains("--fail"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("traffic") && call.Contains("set"));
    }

    [Fact]
    public async Task Promotion_is_uncertain_when_the_promoted_origin_stops_reporting_healthy()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)), "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("revision") && args.Contains("show"), HealthyCandidateRevision);
        process.Success(IsCandidateReadinessProbe, "Healthy");
        process.Success(args => args.Contains("traffic") && args.Contains("set"));
        process.Success(args => args.Contains("show") && args.Any(x => x.Contains("traffic", StringComparison.Ordinal)), "[{\"revisionName\":\"proof-app--candidate\",\"weight\":100},{\"revisionName\":\"proof-app--stable\",\"weight\":0}]");
        process.Success(args => args.Contains("--fail") && args.Contains("https://proof-app.hash.azurecontainerapps.io/health"), "Degraded");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Promotion, PromotionResources()));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.promotion.health-uncertain", result.Code);
        Assert.Equal("proof-app--stable", result.Resources.StableTrafficRevisionName);
    }


    [Fact]
    public async Task Stable_traffic_restore_requires_positive_zero_candidate_absence_proof()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("traffic") && args.Contains("set") && args.Any(x => x.Contains("proof-app--candidate=0", StringComparison.Ordinal)));
        process.Success(args => args.Contains("show") && args.Any(x => x.Contains("traffic", StringComparison.Ordinal)), "[{\"revisionName\":\"proof-app--stable\",\"weight\":100},{\"revisionName\":\"proof-app--candidate\",\"weight\":0}]");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId,
            WorkloadResourceId = _fixture.AppId,
            WorkloadDeploymentId = _fixture.WorkloadDeploymentId,
            WorkloadRevisionName = "proof-app--candidate"
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.RestoreStableTraffic, resources) with
        {
            StableTrafficRevisionName = "proof-app--stable"
        });

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.True(result.StableTrafficRestored);
        Assert.Equal("proof-app--stable", result.Resources.StableTrafficRevisionName);
    }

    [Fact]
    public async Task Stable_traffic_restore_is_uncertain_when_candidate_zero_entry_is_missing()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("traffic") && args.Contains("set"));
        process.Success(args => args.Contains("show") && args.Any(x => x.Contains("traffic", StringComparison.Ordinal)), "[{\"revisionName\":\"proof-app--stable\",\"weight\":100}]");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId,
            WorkloadResourceId = _fixture.AppId,
            WorkloadDeploymentId = _fixture.WorkloadDeploymentId,
            WorkloadRevisionName = "proof-app--candidate"
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.RestoreStableTraffic, resources) with
        {
            StableTrafficRevisionName = "proof-app--stable"
        });

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.rollback.uncertain", result.Code);
    }

    [Fact]
    public async Task Cleanup_requires_exact_rbac_and_positive_absence_for_every_owned_locator()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"type\":\"Microsoft.KeyVault/vaults\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6\"},{\"scope\":\"" + _fixture.FoundationResources.KeyVaultResourceId + "\",\"principalId\":\"11111111-1111-1111-1111-111111111111\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/b86a8fe4-44ce-4948-aee5-eccb2c155cd7\"}]");
        process.Success(args => args.Contains("role") && args.Contains("assignment") && args.Contains("list"), "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\"}]");
        process.Success(args => args.Contains("role") && args.Contains("delete"));
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("group") && args.Contains("delete"));
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.True(result.OwnedResourcesAbsent);
        Assert.DoesNotContain(result.Resources.GetType().GetProperties(), property => property.GetValue(result.Resources) is not null);
        var roleLists = process.Calls.Where(call => call.Contains("role") && call.Contains("assignment") && call.Contains("list")).ToArray();
        Assert.Equal(3, roleLists.Length);
        Assert.Single(roleLists, call =>
            call.Contains("--scope") &&
            call.Contains(_fixture.FoundationResources.KeyVaultResourceId!) &&
            !call.Contains("--all"));
        AssertRegistryRoleObservationsAreScoped(process);
        Assert.Equal(2, process.Calls.Count(call => call.Contains("list-deleted")));
    }

    [Fact]
    public async Task Cleanup_discovers_exact_acr_artifacts_when_uncertainty_prevented_handle_persistence()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        var exactRole = "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\"}]";
        process.Success(args => args.Contains("role") && args.Contains("list"), exactRole);
        process.Success(args => args.Contains("role") && args.Contains("list"), exactRole);
        process.Success(args => args.Contains("role") && args.Contains("delete"));
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Contains(process.Calls, call => call.Contains("role") && call.Contains("delete") && call.Contains(_fixture.RegistryRoleAssignmentId));
        Assert.Contains(process.Calls, call => call.Contains("deployment") && call.Contains("delete") && call.Contains(Path.GetFileName(_fixture.RegistryDeploymentId)));
        AssertRegistryRoleObservationsAreScoped(process);
    }

    private void AssertRegistryRoleObservationsAreScoped(FakeCommandProcess process)
    {
        var observations = process.Calls.Where(call =>
            call is ["role", "assignment", "list", ..] && call.Contains(_fixture.Scope.RegistrySubscriptionId)).ToArray();
        Assert.NotEmpty(observations);
        Assert.All(observations, call =>
        {
            Assert.DoesNotContain("--all", call);
            Assert.Contains("--scope", call);
            Assert.Equal(_fixture.RegistryId, call[Array.IndexOf(call, "--scope") + 1]);
            Assert.Contains("--assignee-object-id", call);
            Assert.Contains("--fill-principal-name", call);
            Assert.Equal("false", call[Array.IndexOf(call, "--fill-principal-name") + 1]);
            Assert.Contains("--fill-role-definition-name", call);
            Assert.Equal("false", call[Array.IndexOf(call, "--fill-role-definition-name") + 1]);
        });
    }

    [Fact]
    public async Task Cleanup_does_not_misprove_role_absence_when_Azure_changes_resource_id_casing()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        var assignment = "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId + "\",\"scope\":\"" + _fixture.RegistryId + "\",\"principalId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"roleDefinitionId\":\"/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\"}]";
        process.Success(args => args.Contains("role") && args.Contains("list"), assignment);
        process.Success(args => args.Contains("role") && args.Contains("delete"));
        var recasedAssignment = "[{\"id\":\"" + _fixture.RegistryRoleAssignmentId.ToUpperInvariant() + "\",\"scope\":\"" + _fixture.RegistryId.ToUpperInvariant() + "\",\"principalId\":\"BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB\",\"roleDefinitionId\":\"/PROVIDERS/MICROSOFT.AUTHORIZATION/ROLEDEFINITIONS/7F951DDA-4ED3-4680-A7CA-43FE172D538D\"}]";
        process.Success(args => args.Contains("role") && args.Contains("list"), recasedAssignment);
        var resources = _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        };

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, resources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.cleanup.role-uncertain", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("deployment") && call.Contains("delete"));
    }

    [Fact]
    public async Task Cleanup_skips_impossible_acr_discovery_when_the_bound_registry_group_is_absent()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists") && args.Contains("proof-rg"), "true");
        process.Success(args => args.Contains("group") && args.Contains("show"), OwnedGroupTags);
        process.Success(args => args.Contains("resource") && args.Contains("list"),
            "[{\"id\":\"" + _fixture.FoundationResources.WorkloadIdentityResourceId + "\",\"type\":\"Microsoft.ManagedIdentity/userAssignedIdentities\"}]");
        process.Failure(args => args.Contains("role") && args.Contains("list"));
        process.Success(args => args.Contains("group") && args.Contains("exists") && args.Contains("registry-rg"), "false");
        process.Success(args => args.Contains("group") && args.Contains("delete"));
        process.Success(args => args.Contains("group") && args.Contains("exists") && args.Contains("proof-rg"), "false");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("role") && call.Contains("assignment") && call.Contains("delete"));
        Assert.DoesNotContain(process.Calls, call => call.Contains("deployment") && call.Contains("delete"));
    }

    [Fact]
    public async Task Cleanup_never_purges_a_deleted_vault_without_the_exact_vault_identity()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[{\"name\":\"proof-kv\",\"properties\":{\"location\":\"westeurope\",\"vaultId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/other/providers/Microsoft.KeyVault/vaults/proof-kv\"}}]");
        process.Success(args => args.Contains("list-deleted"), "[{\"name\":\"proof-kv\",\"properties\":{\"location\":\"westeurope\",\"vaultId\":\"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/other/providers/Microsoft.KeyVault/vaults/proof-kv\"}}]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(process.Calls, call => call.Contains("keyvault") && call.Contains("purge"));
    }

    [Fact]
    public async Task Cleanup_is_uncertain_when_a_matching_deleted_vault_omits_its_identity()
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[{\"name\":\"proof-kv\",\"properties\":{\"location\":\"westeurope\"}}]");

        var result = await _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Cleanup, _fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.cleanup.vault-uncertain", result.Code);
        Assert.DoesNotContain(process.Calls, call => call.Contains("keyvault") && call.Contains("purge"));
    }

    [Fact]
    public async Task Cleanup_requires_consecutive_vault_absence_observations()
    {
        using var fixture = new RunnerFixture(observationAttempts: 3);
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("group") && args.Contains("exists"), "false");
        process.Success(args => args.Contains("role") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("deployment") && args.Contains("delete"));
        process.Success(args => args.Contains("deployment") && args.Contains("list"), "[]");
        process.Success(args => args.Contains("list-deleted"), "[]");
        process.Failure(args => args.Contains("list-deleted"));
        process.Success(args => args.Contains("list-deleted"), "[]");

        var result = await fixture.Runner(process).RunAsync(fixture.Command(AzureProviderRunnerStep.Cleanup, fixture.FoundationResources));

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal("azure.cleanup.vault-uncertain", result.Code);
    }

    public void Dispose() => _fixture.Dispose();

    private static string FoundationOutputs() => """
        {
          "resourceGroupName": { "value": "proof-rg" },
          "deploymentName": { "value": "foundation" },
          "workloadIdentityId": { "value": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/proof-identity" },
          "workloadIdentityClientId": { "value": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" },
          "workloadIdentityPrincipalId": { "value": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
          "keyVaultId": { "value": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.KeyVault/vaults/proof-kv" },
          "keyVaultUri": { "value": "https://proof-kv.vault.azure.net/" },
          "sqlServerId": { "value": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Sql/servers/proof-sql" },
          "sqlServerFqdn": { "value": "proof-sql.database.windows.net" },
          "containerAppsEnvironmentId": { "value": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/proof-aca" },
          "sqlShortTermRetentionDays": { "value": 7 },
          "unusedBoolean": { "value": true },
          "unusedObject": { "value": { "kind": "metadata" } },
          "unusedArray": { "value": [1, 2] },
          "unusedNull": { "value": null },
          "unusedNullEntry": null
        }
        """;

    private static string FoundationOutputsWithResourceGroupValue(string value) =>
        FoundationOutputs().Replace(
            "  \"resourceGroupName\": { \"value\": \"proof-rg\" },",
            $"  \"resourceGroupName\": {{ \"value\": {value} }},",
            StringComparison.Ordinal);

    private static string FoundationOutputsWithoutResourceGroup() =>
        FoundationOutputs().Replace(
            "  \"resourceGroupName\": { \"value\": \"proof-rg\" },\n",
            string.Empty,
            StringComparison.Ordinal);

    private static string FoundationOutputsWithNullResourceGroupEntry() =>
        FoundationOutputs().Replace(
            "  \"resourceGroupName\": { \"value\": \"proof-rg\" },",
            "  \"resourceGroupName\": null,",
            StringComparison.Ordinal);

    private Task<AzureProviderRunnerResult> RunFoundationAsync(string outputs)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args is ["group", "exists", ..], "false");
        process.Success(args => args is ["group", "create", ..]);
        process.Success(args => args.Contains("deployment") && args.Contains("group") && args.Contains("create"), outputs);
        return _fixture.Runner(process).RunAsync(_fixture.Command(AzureProviderRunnerStep.Foundation));
    }

    private static AzureWorkloadCapacity GovernedCapacity(string profile)
    {
        var governed = ElsaInstancePlanResolutionOptions.Default.EffectiveCapacityProfiles[profile];
        return new(governed.MinReplicas, governed.MaxReplicas, governed.CpuMillicores, governed.MemoryMiB);
    }

    private static bool IsCapacityArgument(string argument) =>
        argument.StartsWith("workloadMinReplicas=", StringComparison.Ordinal) ||
        argument.StartsWith("workloadMaxReplicas=", StringComparison.Ordinal) ||
        argument.StartsWith("workloadCpu=", StringComparison.Ordinal) ||
        argument.StartsWith("workloadMemory=", StringComparison.Ordinal);

    private AzureProviderResourceReferences RegistryReadyResources() => _fixture.FoundationResources with
    {
        RegistryResourceId = _fixture.RegistryId,
        AcrPullDeploymentId = _fixture.RegistryDeploymentId,
        AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
    };

    /// <summary>Runs a converging production foundation or workload step and returns its deployment arguments.</summary>
    private async Task<string[]> ProductionDeploymentAsync(AzureProviderRunnerStep step, AzureWorkloadPlan plan)
    {
        var process = new FakeCommandProcess();
        if (step == AzureProviderRunnerStep.Foundation)
        {
            process.Success(args => args is ["group", "exists", ..], "false");
            process.Success(args => args is ["group", "create", ..]);
            process.Success(args => args.Contains("deployment") && args.Contains("create"), FoundationOutputs());
        }
        else
        {
            process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
            process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
            process.Success(args => args.Contains("deployment") && args.Contains("create"), WorkloadOutputs());
            process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
            process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
            process.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));
        }

        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(step, step == AzureProviderRunnerStep.Foundation ? null : RegistryReadyResources()) with { Plan = plan });

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        return process.Calls.Single(call => call.Contains("deployment") && call.Contains("create"));
    }

    private async Task<(AzureProviderRunnerResult Result, FakeCommandProcess Process)> RunWithCapacityAsync(
        AzureProviderRunnerStep step,
        AzureWorkloadCapacity? capacity)
    {
        // No scripted responses: any Azure command would fail the test double.
        var process = new FakeCommandProcess();
        var result = await _fixture.Runner(process).RunAsync(
            _fixture.Command(step, RegistryReadyResources()) with { Plan = _fixture.Plan with { Capacity = capacity } });
        return (result, process);
    }

    private static void AssertFailedClosed(AzureProviderRunnerResult result, FakeCommandProcess process, string code)
    {
        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Empty(process.Calls);
    }

    private string WorkloadOutputs() => """
        {
          "deploymentName": { "value": "workload" },
          "containerAppId": { "value": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/containerApps/proof-app" },
          "containerAppEndpoint": { "value": "https://proof-app.hash.azurecontainerapps.io" }
        }
        """;

    private AzureProviderRecoveryRequest CreateRecoveryRequest(
        AzureProviderResourceReferences resources,
        AzureProviderRunnerStep attemptedStep,
        AzureProviderOperationPhase phase = AzureProviderOperationPhase.FoundationSubmitted)
    {
        var context = _fixture.Context;
        var operation = new AzureProviderOperation(
            context.OperationId,
            context.WorkspaceId,
            _fixture.Plan.WorkloadName,
            AzureProviderOperationAction.Reconcile,
            context.IdempotencyKey,
            new string('c', 64),
            context.OperationIdentity,
            _fixture.Plan.Fingerprint,
            context.TemplateFingerprint,
            _fixture.Plan.ElsaVersion,
            _fixture.Plan.ReleaseLine,
            _fixture.Plan.Topology,
            _fixture.Plan.Isolation,
            _fixture.Plan.Location,
            _fixture.Plan.ImageRepository,
            "sha256:" + _fixture.Plan.ImageDigest,
            _fixture.Plan.ReleaseManifestDigest,
            _fixture.Plan.ReleaseManifestSignatureDigest,
            AzureProviderOperationStatus.RecoveryRequired,
            phase,
            2,
            1,
            1,
            resources,
            null,
            AzureProviderHealth.Unknown,
            [],
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            _fixture.Plan.ReleaseManifestReference,
            _fixture.Plan.ReleaseManifestSignatureReference,
            _fixture.Plan.SecretReferences,
            false,
            context.ProviderScopeFingerprint,
            _fixture.Plan.SqlWorkflowPackageVersion,
            _fixture.Plan.SqlQuartzPackageVersion,
            context.OrganizationId,
            context.InstanceId,
            ElsaInstanceOperationAction.Reconcile,
            Guid.Parse(context.ProviderAssignmentId),
            attemptedStep,
            _fixture.Plan.Capacity);
        var assignmentId = Guid.Parse(context.ProviderAssignmentId);
        var assignment = new AzureProviderResourceAssignment(
            assignmentId,
            context.WorkspaceId,
            context.OrganizationId,
            context.InstanceId,
            context.ProviderScopeFingerprint!,
            1,
            _fixture.Scope.SubscriptionId,
            _fixture.Scope.ResourceGroupName,
            _fixture.Plan.WorkloadName,
            new string('d', 64),
            _fixture.Plan.Location,
            AzureProviderAssignmentState.Active,
            resources,
            operation.Id,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        return new(operation, _fixture.Plan, assignment);
    }

    private AzureProviderResourceReferences SqlFoundationResources() => _fixture.FoundationResources with
    {
        RegistryResourceId = _fixture.RegistryId,
        AcrPullDeploymentId = _fixture.RegistryDeploymentId,
        AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
    };

    private sealed class RunnerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"elsa-runner-{Guid.NewGuid():N}");
        private readonly string _tool;

        public RunnerFixture(int observationAttempts = 1)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "main.bicep"), "targetScope = 'resourceGroup'");
            File.WriteAllText(Path.Combine(_root, "acr-pull-role.bicep"), "targetScope = 'resourceGroup'");
            File.WriteAllText(Path.Combine(_root, "sql-bootstrap.sql"), "SELECT 1;");
            _tool = Environment.ProcessPath ?? (OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe" : "/bin/sh");
            Options = new AzureProviderRunnerOptions
            {
                Enabled = true,
                AzureCliPath = _tool,
                AzureCliClientId = "33333333-3333-3333-3333-333333333333",
                SqlCmdPath = _tool,
                CurlPath = _tool,
                TemplateRoot = _root,
                SqlBootstrapObjectId = "11111111-1111-1111-1111-111111111111",
                SqlBootstrapLogin = "proof-bootstrap",
                SqlBootstrapIp = "203.0.113.10",
                RuntimeAdminUsername = "runtime-admin",
                ObservationAttempts = observationAttempts,
                ObservationDelay = TimeSpan.Zero
            };
        }

        public AzureProviderRunnerOptions Options { get; }
        public string TemplateRoot => _root;
        public AzureProviderTargetScope Scope { get; } = new(
            "11111111-1111-1111-1111-111111111111", "proof-rg",
            "22222222-2222-2222-2222-222222222222", "registry-rg", "valenceruntimeimages", "westeurope");
        public AzureWorkloadPlan Plan { get; } = new(
            "proof", "westeurope", "3.8", "3.8", "combined", "Dedicated",
            "valenceruntimeimages.azurecr.io/runtime-combined", new string('b', 64),
            "oci://release/manifest@sha256:" + new string('c', 64), "sha256:" + new string('c', 64),
            "oci://release/signature@sha256:" + new string('d', 64), "sha256:" + new string('d', 64),
            new Dictionary<string, string>(), new string('a', 64),
            "3.8.0-preview.5413", "3.8.0-preview.342", StandardSmall);
        public AzureProviderResourceReferences FoundationResources { get; } = new(
            ResourceGroupName: "proof-rg",
            FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/foundation",
            WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/proof-identity",
            WorkloadIdentityClientId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            WorkloadIdentityPrincipalId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.KeyVault/vaults/proof-kv",
            KeyVaultUri: "https://proof-kv.vault.azure.net/",
            SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Sql/servers/proof-sql",
            SqlServerFqdn: "proof-sql.database.windows.net",
            ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/managedEnvironments/proof-aca");
        public string RegistryId => "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages";
        public string RegistryDeploymentId => "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/registry-rg/providers/Microsoft.Resources/deployments/elsa-proof-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("11111111-1111-1111-1111-111111111111/proof-rg/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/22222222-2222-2222-2222-222222222222/registry-rg/valenceruntimeimages")))[..12] + "-acr";
        public string RegistryRoleAssignmentId => RegistryId + "/providers/Microsoft.Authorization/roleAssignments/88843122-c847-55e8-9526-6429f5445c73";
        public string AppId => "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.App/containerApps/proof-app";
        public string WorkloadDeploymentId => "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/workload";
        public AzureProviderExecutionContext Context => new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "operation", "idempotency", "proof", "44444444-4444-4444-4444-444444444444",
            Plan.Fingerprint, Options.ComputeTemplateAuthorityFingerprint(), Options.ComputeProviderScopeFingerprint(Scope));

        public AzureBicepProviderRunner Runner(FakeCommandProcess process, IAzureSecretResolver? resolver = null) => new(Options, Scope, process, resolver);
        public AzureProviderRunnerCommand Command(AzureProviderRunnerStep step, AzureProviderResourceReferences? resources = null) => new(step, Plan, resources ?? new(), null, false, 1, Context);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private AzureProviderRunnerCommand GeneratedAdminSeedCommand(bool resume = false)
    {
        return _fixture.Command(AzureProviderRunnerStep.SeedSecrets, _fixture.FoundationResources with
        {
            RegistryResourceId = _fixture.RegistryId,
            AcrPullDeploymentId = _fixture.RegistryDeploymentId,
            AcrPullRoleAssignmentId = _fixture.RegistryRoleAssignmentId
        }) with
        {
            IsResume = resume,
            AttemptNumber = resume ? 2 : 1,
            Plan = _fixture.Plan with
            {
                SecretReferences = new Dictionary<string, string>
                {
                    ["admin:password"] = AzureManagedSecretReferences.AdminPassword
                }
            }
        };
    }

    private sealed class FakeCommandProcess : IAzureCommandProcess
    {
        private readonly Queue<Response> _responses = new();
        public List<string[]> Calls { get; } = [];

        public void Success(Func<string[], bool> matcher, string output = "", Action? after = null) => _responses.Enqueue(new(matcher, AzureCommandProcessStatus.Succeeded, output, after));
        public void Failure(Func<string[], bool> matcher, string output = "") => _responses.Enqueue(new(matcher, AzureCommandProcessStatus.Failed, output, null));
        public void SqlBatchError(Func<string[], bool> matcher) => _responses.Enqueue(new(matcher, AzureCommandProcessStatus.Succeeded, "", null, SimulateSqlBatchError: true));
        public void Status(Func<string[], bool> matcher, AzureCommandProcessStatus status, AzureCommandProcessFailureKind failureKind) =>
            _responses.Enqueue(new(matcher, status, string.Empty, null, failureKind));

        public Task<AzureCommandProcessResult<T>> ExecuteAsync<T>(AzureCommandProcessRequest request, AzureCommandOutputProjector<T> outputProjector, CancellationToken cancellationToken = default)
            where T : AzureCommandSafeOutput
        {
            var args = request.Arguments.Select(x => x.Value).ToArray();
            Calls.Add(args);
            var response = _responses.Count > 0 ? _responses.Dequeue() : throw new InvalidOperationException("Unexpected Azure command.");
            Assert.True(response.Matcher(args), $"Unexpected command: {string.Join(' ', args)}");
            var status = response.SimulateSqlBatchError
                ? args.Contains("-b") ? AzureCommandProcessStatus.Failed : AzureCommandProcessStatus.Succeeded
                : response.Status;
            if (status != AzureCommandProcessStatus.Succeeded)
                return Task.FromResult(new AzureCommandProcessResult<T>(status, response.FailureKind, 1, null, "test.command.failed", "The test command failed."));
            T projected;
            try
            {
                projected = outputProjector(response.Output.AsMemory());
            }
            catch (Exception)
            {
                return Task.FromResult(new AzureCommandProcessResult<T>(
                    AzureCommandProcessStatus.Failed,
                    AzureCommandProcessFailureKind.InvalidOutput,
                    1,
                    null,
                    "test.command.invalid-output",
                    "The test command returned invalid output."));
            }
            var result = new AzureCommandProcessResult<T>(status, AzureCommandProcessFailureKind.None, 0, projected, "test.command.succeeded", "The test command completed.");
            response.After?.Invoke();
            return Task.FromResult(result);
        }

        private sealed record Response(
            Func<string[], bool> Matcher,
            AzureCommandProcessStatus Status,
            string Output,
            Action? After,
            AzureCommandProcessFailureKind FailureKind = AzureCommandProcessFailureKind.NonZeroExitCode,
            bool SimulateSqlBatchError = false);
    }

    private sealed class RecordingSecretResolver(string value) : IAzureSecretResolver
    {
        public List<AzureSecretResolutionRequest> Requests { get; } = [];
        public List<AzureSecretResolutionRequest> AuthorizationRequests { get; } = [];
        public bool AuthorizationResult { get; init; } = true;
        public Exception? AuthorizationFailure { get; init; }
        public ValueTask<AzureSecretLease> ResolveAsync(AzureSecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new AzureSecretLease(value));
        }

        public ValueTask<bool> IsAuthorizedAsync(AzureSecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            AuthorizationRequests.Add(request);
            if (AuthorizationFailure is not null)
                throw AuthorizationFailure;
            return ValueTask.FromResult(AuthorizationResult);
        }
    }
}
