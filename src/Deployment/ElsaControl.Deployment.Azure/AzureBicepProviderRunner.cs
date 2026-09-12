using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Concrete adapter for the checked-in disposable Azure workload authority. The adapter keeps
/// Azure CLI/SQL command details below <see cref="IAzureProviderRunner"/> and projects every
/// successful response into bounded resource references. It never returns provider payloads,
/// command output, or resolved secret values.
/// </summary>
public sealed class AzureBicepProviderRunner : IAzureProviderRunner, IAzureProviderRecoveryObserver
{
    private const string AcrPullRoleDefinitionId = "7f951dda-4ed3-4680-a7ca-43fe172d538d";
    private const string KeyVaultSecretsUserRoleDefinitionId = "4633458b-17de-408a-b874-0445c86b69e6";
    private const string KeyVaultSecretsOfficerRoleDefinitionId = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7";
    private const string ProofTag = "108";
    private const string ManagedByTag = "elsa-control";
    private const string SqlConnectionSecretName = "sql-connection";
    private const string SigningKeySecretName = "identity-signing-key";
    private const string AdminPasswordSecretName = "admin-password";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AzureProviderRunnerOptions _options;
    private readonly AzureProviderTargetScope _scope;
    private readonly IAzureCommandProcess _process;
    private readonly IAzureSecretResolver _secretResolver;

    public AzureBicepProviderRunner(
        AzureProviderRunnerOptions options,
        AzureProviderTargetScope scope,
        IAzureSecretResolver? secretResolver = null)
        : this(options, scope, new AzureCommandProcess(options.CommandTimeout, options.MaximumOutputCharacters), secretResolver)
    {
    }

    internal AzureBicepProviderRunner(
        AzureProviderRunnerOptions options,
        AzureProviderTargetScope scope,
        IAzureCommandProcess process,
        IAzureSecretResolver? secretResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _secretResolver = secretResolver ?? new UnconfiguredAzureSecretResolver();
        _options.Validate();
        _scope.Validate();
    }

    public async Task<AzureProviderRunnerResult> RunAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            ValidateCommand(command);
        }
        catch (ArgumentException exception)
        {
            return Failed(command, CurrentPhase(command.Step), "azure.runner.input-invalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failed(command, CurrentPhase(command.Step), "azure.runner.scope-invalid", exception.Message);
        }

        try
        {
            return command.Step switch
            {
                AzureProviderRunnerStep.Foundation => await RunFoundationAsync(command, cancellationToken),
                AzureProviderRunnerStep.AcrPull => await RunAcrPullAsync(command, cancellationToken),
                AzureProviderRunnerStep.SeedSecrets => await RunSeedSecretsAsync(command, cancellationToken),
                AzureProviderRunnerStep.SqlBootstrap => await RunSqlBootstrapAsync(command, cancellationToken),
                AzureProviderRunnerStep.SqlFirewallCreate => await RunSqlFirewallCreateAsync(command, cancellationToken),
                AzureProviderRunnerStep.SqlBootstrapScript => await RunSqlBootstrapScriptAsync(command, cancellationToken),
                AzureProviderRunnerStep.SqlFirewallCleanup => await RunSqlFirewallCleanupAsync(command, cancellationToken),
                AzureProviderRunnerStep.Workload => await RunWorkloadAsync(command, cancellationToken),
                AzureProviderRunnerStep.Health => await RunHealthAsync(command, cancellationToken),
                AzureProviderRunnerStep.Promotion => await RunPromotionAsync(command, cancellationToken),
                AzureProviderRunnerStep.RestoreStableTraffic => await RunRestoreStableTrafficAsync(command, cancellationToken),
                AzureProviderRunnerStep.Cleanup => await RunCleanupAsync(command, cancellationToken),
                _ => Failed(command, CurrentPhase(command.Step), "azure.runner.step-invalid", "The Azure lifecycle step is not supported.")
            };
        }
        catch (OperationCanceledException)
        {
            return Uncertain(command, CurrentPhase(command.Step), "azure.runner.cancelled", "The Azure lifecycle step was interrupted before its result was confirmed.");
        }
        catch (Exception)
        {
            // Provider exceptions are deliberately value-free. A process/SDK failure may have
            // committed a remote mutation, so the durable executor must recover it explicitly.
            return Uncertain(command, CurrentPhase(command.Step), "azure.runner.uncertain", "The Azure lifecycle step failed before its external result was confirmed.");
        }
    }

    /// <summary>
    /// Performs the provider-owned read-only observation used before an accepted recovery
    /// claim. Each confirmed result proves one retained checkpoint only; later lifecycle steps
    /// remain under the normal executor and its health/traffic gates.
    /// </summary>
    public async Task<AzureProviderRecoveryObservation> ObserveAsync(
        AzureProviderRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request.Validate();
            if (request.Assignment is null || request.Operation.OrganizationId is not { } organizationId ||
                request.Operation.InstanceId is not { } instanceId || request.Operation.LifecycleAction is null)
                return RecoveryObservationInProgress(request);

            var operation = request.Operation;
            var sqlRecoveryStep = GetSqlRecoveryStep(operation);
            var observesAcrPull = AzureProviderRecoveryObservationSupport.IsAcrPullEligible(operation);
            var observesFoundation = AzureProviderRecoveryObservationSupport.IsFoundationOnlyEligible(operation);
            if (sqlRecoveryStep is null && !observesAcrPull && !observesFoundation)
                return RecoveryObservationUnsupported(request);

            var assignment = request.Assignment;
            if (!IsRecoveryAssignmentAuthorityValid(operation, assignment, request.Plan))
                return RecoveryObservationAmbiguous(request);

            var command = new AzureProviderRunnerCommand(
                sqlRecoveryStep ?? (observesAcrPull ? AzureProviderRunnerStep.AcrPull : AzureProviderRunnerStep.Foundation),
                request.Plan,
                operation.Resources,
                operation.Resources.StableTrafficRevisionName,
                IsResume: true,
                operation.AttemptNumber,
                new AzureProviderExecutionContext(
                    operation.WorkspaceId,
                    organizationId,
                    instanceId,
                    operation.Id,
                    operation.OperationIdentity,
                    operation.IdempotencyKey,
                    operation.TargetKey,
                    assignment.Id.ToString("D"),
                    operation.PlanFingerprint,
                    operation.TemplateFingerprint,
                    operation.ProviderScopeFingerprint),
                assignment);
            ValidateCommand(command);

            return sqlRecoveryStep is not null
                ? await ObserveSqlRecoveryAsync(request, command, sqlRecoveryStep.Value, cancellationToken)
                : observesAcrPull
                    ? await ObserveAcrPullAsync(request, command, cancellationToken)
                    : await ObserveFoundationAsync(request, command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return RecoveryObservationInProgress(request);
        }
    }

    private async Task<AzureProviderRecoveryObservation> ObserveFoundationAsync(
        AzureProviderRecoveryRequest request,
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var assignment = request.Assignment!;
        if (!string.Equals(operation.Resources.ResourceGroupName, assignment.ResourceGroupName, StringComparison.Ordinal) ||
            operation.Resources.FoundationDeploymentId is null)
            return RecoveryObservationAmbiguous(request);
        try
        {
            ValidateExactDeploymentId(operation.Resources.FoundationDeploymentId, _scope.SubscriptionId, assignment.ResourceGroupName);
        }
        catch (ArgumentException)
        {
            return RecoveryObservationAmbiguous(request);
        }

        var groupExists = await ExecuteAzAsync(command,
            ["group", "exists", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command),
                "--output", "tsv", "--only-show-errors"],
            ParseBooleanAsync,
            cancellationToken);
        if (!groupExists.Succeeded || groupExists.Value is null || !groupExists.Value.Value)
            return RecoveryObservationInProgress(request);

        var tags = await ExecuteAzAsync(command,
            ["group", "show", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command),
                "--query", "tags", "--output", "json", "--only-show-errors"],
            ParseTagsAsync,
            cancellationToken);
        if (!tags.Succeeded || tags.Value is null || !OwnsGroup(tags.Value.Value, request.Plan.WorkloadName))
            return RecoveryObservationAmbiguous(request);

        if (RequireFoundation(operation.Resources) is not null)
            return RecoveryObservationInProgress(request);

        var deploymentName = ResourceName(operation.Resources.FoundationDeploymentId);
        if (deploymentName is null || !string.Equals(deploymentName, FoundationDeploymentName(command), StringComparison.Ordinal))
            return RecoveryObservationAmbiguous(request);
        var deployment = await ExecuteAzAsync(command,
            ["deployment", "group", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", deploymentName, "--query", "properties.provisioningState", "--output", "tsv", "--only-show-errors"],
            ParseStringAsync,
            cancellationToken);
        if (!deployment.Succeeded || !string.Equals(deployment.Value?.Value, "Succeeded", StringComparison.OrdinalIgnoreCase))
            return RecoveryObservationInProgress(request);

        return new(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Foundation,
            operation.Resources,
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.foundation-observed",
            "The retained Azure foundation completion was observed without mutation.");
    }

    private async Task<AzureProviderRecoveryObservation> ObserveAcrPullAsync(
        AzureProviderRecoveryRequest request,
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var resources = operation.Resources;
        var assignment = request.Assignment!;
        if (!string.Equals(resources.ResourceGroupName, assignment.ResourceGroupName, StringComparison.Ordinal) ||
            RequireFoundation(resources) is not null || resources.RegistryResourceId is null ||
            resources.AcrPullDeploymentId is null || resources.WorkloadIdentityPrincipalId is null ||
            resources.WorkloadIdentityResourceId is null)
            return RecoveryObservationAmbiguous(request);

        try
        {
            ValidateExactResourceId(resources.RegistryResourceId, _scope.RegistrySubscriptionId,
                _scope.RegistryResourceGroupName, "Microsoft.ContainerRegistry", "registries", _scope.RegistryName);
            ValidateExactDeploymentId(resources.AcrPullDeploymentId, _scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName);
            ValidateExactAcrDeploymentId(resources.AcrPullDeploymentId, command, resources.WorkloadIdentityPrincipalId);
            var expectedAssignmentId = ExpectedAcrPullRoleAssignmentId(resources.RegistryResourceId, resources.WorkloadIdentityResourceId);
            if (resources.AcrPullRoleAssignmentId is not null &&
                !string.Equals(resources.AcrPullRoleAssignmentId, expectedAssignmentId, StringComparison.OrdinalIgnoreCase))
                return RecoveryObservationAmbiguous(request);
        }
        catch (ArgumentException)
        {
            return RecoveryObservationAmbiguous(request);
        }

        var expectedRoleAssignmentId = ExpectedAcrPullRoleAssignmentId(resources.RegistryResourceId, resources.WorkloadIdentityResourceId);
        var role = await DiscoverAcrRoleAssignmentAsync(
            command,
            resources.RegistryResourceId,
            resources.WorkloadIdentityPrincipalId,
            expectedRoleAssignmentId,
            cancellationToken);
        if (role.Status is AcrRoleDiscoveryStatus.Uncertain or AcrRoleDiscoveryStatus.Absent)
            return RecoveryObservationInProgress(request);
        if (role.Status != AcrRoleDiscoveryStatus.Exact || role.AssignmentId is null)
            return RecoveryObservationAmbiguous(request);

        try
        {
            ValidateExactRoleAssignmentId(role.AssignmentId, resources.RegistryResourceId);
        }
        catch (ArgumentException)
        {
            return RecoveryObservationAmbiguous(request);
        }

        var deploymentName = ResourceName(resources.AcrPullDeploymentId);
        if (deploymentName is null)
            return RecoveryObservationAmbiguous(request);
        var deployment = await ExecuteAzAsync(command,
            ["deployment", "group", "show", "--subscription", _scope.RegistrySubscriptionId,
                "--resource-group", _scope.RegistryResourceGroupName, "--name", deploymentName,
                "--query", "properties.provisioningState", "--output", "tsv", "--only-show-errors"],
            ParseStringAsync,
            cancellationToken);
        if (!deployment.Succeeded || !string.Equals(deployment.Value?.Value, "Succeeded", StringComparison.OrdinalIgnoreCase))
            return RecoveryObservationInProgress(request);

        return new(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.AcrPull,
            resources with { AcrPullRoleAssignmentId = role.AssignmentId },
            AzureProviderHealth.Unknown,
            null,
            "azure.recovery.acr-pull-observed",
            "The retained Azure registry access checkpoint was observed without mutation.");
    }

    private static AzureProviderRecoveryObservation RecoveryObservationInProgress(AzureProviderRecoveryRequest request) =>
        new(AzureProviderRecoveryObservationKind.InProgress, null, request.Operation.Resources, AzureProviderHealth.Unknown,
            null, "azure.recovery.observation-in-progress", "The retained Azure postcondition is not yet proven.");

    private static AzureProviderRecoveryObservation RecoveryObservationAmbiguous(AzureProviderRecoveryRequest request) =>
        new(AzureProviderRecoveryObservationKind.Ambiguous, null, request.Operation.Resources, AzureProviderHealth.Unknown,
            null, "azure.recovery.observation-ambiguous", "The retained Azure ownership boundary is ambiguous.");

    private static AzureProviderRecoveryObservation RecoveryObservationUnsupported(AzureProviderRecoveryRequest request) =>
        new(AzureProviderRecoveryObservationKind.Ambiguous, null, request.Operation.Resources, AzureProviderHealth.Unknown,
            null, "azure.recovery.step-unsupported", "The retained Azure recovery step is not supported by this provider observer.");

    private static AzureProviderRunnerStep? GetSqlRecoveryStep(AzureProviderOperation operation) =>
        operation.AttemptedStep switch
        {
            AzureProviderRunnerStep.SqlFirewallCreate when AzureProviderRecoveryObservationSupport.IsSqlFirewallCreateEligible(operation) =>
                AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderRunnerStep.SqlBootstrapScript when AzureProviderRecoveryObservationSupport.IsSqlBootstrapScriptEligible(operation) =>
                AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderRunnerStep.SqlFirewallCleanup when AzureProviderRecoveryObservationSupport.IsSqlFirewallCleanupEligible(operation) =>
                AzureProviderRunnerStep.SqlFirewallCleanup,
            _ => null
        };

    private async Task<AzureProviderRecoveryObservation> ObserveSqlRecoveryAsync(
        AzureProviderRecoveryRequest request,
        AzureProviderRunnerCommand command,
        AzureProviderRunnerStep attemptedStep,
        CancellationToken cancellationToken)
    {
        var missing = RequireRegistry(command.Resources);
        if (missing is not null || command.Resources.WorkloadIdentityClientId is null ||
            command.Resources.SqlServerFqdn is null)
            return RecoveryObservationAmbiguous(request);

        // Observation must rebind the retained executable/template/scope authority before the
        // first provider read. This method never creates or deletes a firewall rule.
        EnsureMutationAuthority(command);
        var firewall = await ObserveSqlFirewallAsync(command, cancellationToken);
        switch (firewall)
        {
            case SqlFirewallObservationState.Uncertain:
                return RecoveryObservationInProgress(request);
            case SqlFirewallObservationState.Ambiguous:
                return RecoveryObservationAmbiguous(request);
            case SqlFirewallObservationState.Absent when attemptedStep == AzureProviderRunnerStep.SqlFirewallCreate:
                // A create request may still be in flight after the caller lost its result. An
                // absent read is therefore not proof that the remote create did not commit.
                return RecoveryObservationInProgress(request);
            case SqlFirewallObservationState.Absent when attemptedStep == AzureProviderRunnerStep.SqlFirewallCleanup:
                return new(
                    AzureProviderRecoveryObservationKind.Confirmed,
                    AzureProviderRunnerStep.SqlFirewallCleanup,
                    request.Operation.Resources,
                    AzureProviderHealth.Unknown,
                    null,
                    "azure.recovery.sql-firewall-cleanup-observed",
                    "The exact temporary SQL firewall rule was observed absent without mutation.");
            case SqlFirewallObservationState.Absent:
                // An uncertain SQL script cannot be replayed and the observer cannot reopen the
                // firewall. Only an independently reachable SQL endpoint could prove completion.
                return await ObserveSqlBootstrapPostconditionAsync(request, command, cancellationToken);
            case SqlFirewallObservationState.ExactPresent:
                break;
            default:
                return RecoveryObservationAmbiguous(request);
        }

        if (attemptedStep == AzureProviderRunnerStep.SqlFirewallCreate)
            return new(
                AzureProviderRecoveryObservationKind.Confirmed,
                AzureProviderRunnerStep.SqlFirewallCreate,
                request.Operation.Resources,
                AzureProviderHealth.Unknown,
                null,
                "azure.recovery.sql-firewall-create-observed",
                "The exact temporary SQL firewall rule was observed without mutation.");

        return await ObserveSqlBootstrapPostconditionAsync(request, command, cancellationToken);
    }

    private async Task<AzureProviderRecoveryObservation> ObserveSqlBootstrapPostconditionAsync(
        AzureProviderRecoveryRequest request,
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        // This path is intentionally read-only. When the firewall is absent it is useful only
        // where the retained SQL endpoint remains reachable; it never creates a rule and
        // therefore cannot turn a missing firewall into evidence that SQL bootstrap completed.
        var result = await ExecuteSqlCmdAsync(
            command,
            ["-S", $"tcp:{command.Resources.SqlServerFqdn},1433", "-d", "Elsa", ..SqlAuthenticationArguments(),
                "-b", "-h", "-1", "-W", "-Q", SqlBootstrapPostconditionQuery(command)],
            ParseSqlBootstrapPostconditionAsync,
            cancellationToken);
        if (!result.Succeeded || result.Value is null)
            return RecoveryObservationInProgress(request);

        var postcondition = result.Value.Value;
        return postcondition switch
        {
            SqlBootstrapPostconditionState.Complete => new(
                AzureProviderRecoveryObservationKind.Confirmed,
                AzureProviderRunnerStep.SqlBootstrapScript,
                request.Operation.Resources,
                AzureProviderHealth.Unknown,
                null,
                "azure.recovery.sql-bootstrap-observed",
                "The exact SQL bootstrap principal and role postcondition was observed without mutation."),
            SqlBootstrapPostconditionState.Conflict => RecoveryObservationAmbiguous(request),
            _ => RecoveryObservationInProgress(request)
        };
    }

    private async Task<SqlFirewallObservationState> ObserveSqlFirewallAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var listed = await ExecuteAzAsync(
            command,
            ["sql", "server", "firewall-rule", "list", "--subscription", _scope.SubscriptionId,
                "--resource-group", ResourceGroupName(command), "--server", SqlServerName(command),
                "--output", "json", "--only-show-errors"],
            ParseFirewallRulesAsync,
            cancellationToken);
        if (!listed.Succeeded || listed.Value is null)
            return SqlFirewallObservationState.Uncertain;

        return ClassifySqlFirewall(listed.Value.Value);
    }

    private SqlFirewallObservationState ClassifySqlFirewall(IReadOnlyList<FirewallRule> rules)
    {
        if (!AreWellFormedFirewallRules(rules))
            return SqlFirewallObservationState.Ambiguous;

        var ownedRules = rules.Where(rule =>
            string.Equals(rule.Name, TemporaryFirewallRuleName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (ownedRules.Length == 0)
            return SqlFirewallObservationState.Absent;
        if (ownedRules.Length != 1 ||
            !string.Equals(ownedRules[0].StartIpAddress, _options.SqlBootstrapIp, StringComparison.Ordinal) ||
            !string.Equals(ownedRules[0].EndIpAddress, _options.SqlBootstrapIp, StringComparison.Ordinal))
            return SqlFirewallObservationState.Ambiguous;

        return SqlFirewallObservationState.ExactPresent;
    }

    private static string SqlBootstrapPostconditionQuery(AzureProviderRunnerCommand command)
    {
        var principalName = SqlLiteral($"{command.Plan.WorkloadName}-identity");
        var clientId = SqlLiteral(command.Resources.WorkloadIdentityClientId!);
        return $"SET NOCOUNT ON; DECLARE @expectedName sysname = N'{principalName}'; DECLARE @expectedClientId uniqueidentifier = '{clientId}'; DECLARE @expectedSid varbinary(16) = CONVERT(varbinary(16), @expectedClientId); DECLARE @principalCount int = (SELECT COUNT(*) FROM sys.database_principals WHERE name = @expectedName); DECLARE @matchingPrincipal bit = CASE WHEN EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @expectedName AND type = 'E' AND sid = @expectedSid) THEN 1 ELSE 0 END; DECLARE @matchingRoles int = (SELECT COUNT(DISTINCT role_principal.name) FROM sys.database_role_members drm JOIN sys.database_principals role_principal ON role_principal.principal_id = drm.role_principal_id JOIN sys.database_principals member_principal ON member_principal.principal_id = drm.member_principal_id WHERE member_principal.name = @expectedName AND role_principal.name IN (N'db_datareader', N'db_datawriter', N'db_ddladmin')); SELECT CASE WHEN @principalCount = 1 AND @matchingPrincipal = 1 AND @matchingRoles = 3 THEN N'complete' WHEN @principalCount > 0 THEN N'conflict' ELSE N'incomplete' END;";
    }

    private static string SqlLiteral(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\'', StringComparison.Ordinal))
            throw new ArgumentException("The SQL verification literal is unsafe.");
        return value;
    }

    private async Task<AzureProviderRunnerResult> RunFoundationAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        if (CapacityFailure(command, AzureProviderOperationPhase.FoundationSubmitted) is { } capacityFailure)
            return capacityFailure;

        var resources = command.Resources with
        {
            ResourceGroupName = ResourceGroupName(command),
            FoundationDeploymentId = DeploymentId(_scope.SubscriptionId, ResourceGroupName(command), FoundationDeploymentName(command)),
            WorkloadIdentityResourceId = ResourceId(command, "Microsoft.ManagedIdentity", "userAssignedIdentities", $"{command.Plan.WorkloadName}-identity"),
            KeyVaultResourceId = ResourceId(command, "Microsoft.KeyVault", "vaults", $"{command.Plan.WorkloadName}-kv"),
            KeyVaultUri = $"https://{command.Plan.WorkloadName}-kv.vault.azure.net/",
            SqlServerResourceId = ResourceId(command, "Microsoft.Sql", "servers", $"{command.Plan.WorkloadName}-sql"),
            SqlServerFqdn = $"{command.Plan.WorkloadName}-sql.database.windows.net",
            ContainerAppsEnvironmentResourceId = ResourceId(command, "Microsoft.App", "managedEnvironments", $"{command.Plan.WorkloadName}-aca")
        };
        var groupId = ResourceGroupId(command);
        var exists = await ExecuteAzAsync(command,
            ["group", "exists", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command), "--output", "tsv", "--only-show-errors"],
            ParseBooleanAsync,
            cancellationToken);
        if (!exists.Succeeded)
            return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, exists, resources, mutation: false);

        if (!exists.Value!.Value)
        {
            EnsureMutationAuthority(command);
            var created = await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["group", "create", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command),
                    "--location", _scope.Location, "--tags", ..ResourceGroupTags(command.Plan.WorkloadName),
                    "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!created.Succeeded)
                return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, created, resources, mutation: true);
        }
        else
        {
            var tags = await ExecuteAzAsync(command,
                ["group", "show", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command),
                    "--query", "tags", "--output", "json", "--only-show-errors"],
                ParseTagsAsync,
                cancellationToken);
            if (!tags.Succeeded || tags.Value is null)
                return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, tags, resources, mutation: false);
            if (!OwnsGroup(tags.Value!.Value, command.Plan.WorkloadName))
                return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.foundation.ownership-invalid", "The target resource group is not owned by this workload.");

            EnsureMutationAuthority(command);
            var updated = await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["tag", "update", "--subscription", _scope.SubscriptionId, "--resource-id", groupId,
                    "--operation", "Merge", "--tags", ..ResourceGroupTags(command.Plan.WorkloadName),
                    "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!updated.Succeeded)
                return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, updated, resources, mutation: true);

            var adminReady = await EnsureExactSqlBootstrapAdminAsync(command, allowMissingServer: true, cancellationToken: cancellationToken);
            if (adminReady is null)
                return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.foundation.sql-admin-uncertain", "The SQL bootstrap administrator could not be confirmed for reconciliation.", resources);
            if (!adminReady.Value)
                return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.foundation.sql-admin-invalid", "The existing SQL administrator does not match the governed bootstrap identity.");
        }

        var deploymentName = FoundationDeploymentName(command);
        EnsureMutationAuthority(command);
        var output = await ExecuteAzAsync(command,
            FoundationDeploymentArguments(command, deploymentName),
            ParseDeploymentOutputsAsync,
            cancellationToken);
        if (!output.Succeeded || output.Value is null)
            return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, output, resources, mutation: true);

        try
        {
            resources = ProjectFoundation(command, output.Value!.Value, command.Plan, deploymentName);
            return Completed(command, AzureProviderOperationPhase.FoundationSubmitted, resources);
        }
        catch (ArgumentException exception)
        {
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.foundation.output-invalid", exception.Message, resources);
        }
    }

    private async Task<AzureProviderRunnerResult> RunAcrPullAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var foundation = RequireFoundation(command.Resources);
        if (foundation is not null)
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.foundation-missing", foundation);

        var registryIdResult = await ExecuteAzAsync(command,
            ["acr", "show", "--subscription", _scope.RegistrySubscriptionId, "--resource-group", _scope.RegistryResourceGroupName,
                "--name", _scope.RegistryName, "--query", "id", "--output", "tsv", "--only-show-errors"],
            ParseStringAsync,
            cancellationToken);
        if (!registryIdResult.Succeeded || string.IsNullOrWhiteSpace(registryIdResult.Value?.Value))
            return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, registryIdResult, command.Resources, mutation: false);

        try
        {
            ValidateExactResourceId(registryIdResult.Value!.Value, _scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName,
                "Microsoft.ContainerRegistry", "registries", _scope.RegistryName);
        }
        catch (ArgumentException exception)
        {
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.scope-invalid", exception.Message);
        }

        var deploymentName = AcrDeploymentName(command, command.Resources.WorkloadIdentityPrincipalId!);
        var resources = command.Resources with
        {
            RegistryResourceId = registryIdResult.Value!.Value,
            AcrPullDeploymentId = DeploymentId(_scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName, deploymentName)
        };
        EnsureMutationAuthority(command);
        var deployment = await ExecuteAzAsync(command,
            AcrDeploymentArguments(command, command.Resources.WorkloadIdentityResourceId!, command.Resources.WorkloadIdentityPrincipalId!, deploymentName),
            ParseDeploymentOutputsAsync,
            cancellationToken);
        if (!deployment.Succeeded || deployment.Value is null)
            return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, deployment, resources, mutation: true);

        var roleAssignmentId = deployment.Value!.Value.String("roleAssignmentId");
        if (roleAssignmentId is null)
            return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.output-invalid", "The ACR role assignment identity was not returned.", resources);
        try
        {
            ValidateExactRoleAssignmentId(roleAssignmentId, registryIdResult.Value!.Value);
        }
        catch (ArgumentException exception)
        {
            return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.role-scope-invalid", exception.Message, resources);
        }

        var assignment = await WaitForRoleAssignmentAsync(command,
            registryIdResult.Value!.Value,
            command.Resources.WorkloadIdentityPrincipalId!,
            roleAssignmentId,
            cancellationToken);
        if (assignment is null)
            return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.role-observation-uncertain", "The ACR role assignment could not be confirmed.", resources with { AcrPullRoleAssignmentId = roleAssignmentId });
        if (assignment is false)
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.acr.role-invalid", "The ACR role assignment did not match the governed registry scope.");

        resources = resources with
        {
            RegistryResourceId = registryIdResult.Value!.Value,
            AcrPullDeploymentId = DeploymentId(_scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName, deploymentName),
            AcrPullRoleAssignmentId = roleAssignmentId
        };
        return Completed(command, AzureProviderOperationPhase.FoundationSubmitted, resources);
    }

    private async Task<AzureProviderRunnerResult> RunSeedSecretsAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var missing = RequireRegistry(command.Resources);
        if (missing is not null)
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.foundation-missing", missing);

        var vaultName = ResourceName(command.Resources.KeyVaultResourceId);
        if (vaultName is null)
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.vault-missing", "The Key Vault resource identity is missing.");

        (string Key, string Reference, string Name)[] secretReferences;
        try
        {
            secretReferences = (command.Plan.SecretReferences ?? EmptyReferences)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Key, Reference: x.Value, Name: AzureProviderOperationValidation.MapSecretName(x.Key)))
                .ToArray();
        }
        catch (ArgumentException exception)
        {
            return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.name-invalid", exception.Message);
        }
        var changed = false;
        foreach (var (key, reference, secretName) in secretReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await ExecuteAzAsync(command,
                ["keyvault", "secret", "list", "--subscription", _scope.SubscriptionId, "--vault-name", vaultName,
                    "--query", $"[?name=='{secretName}'] | [].{{managedBy:tags.\"managed-by\",assignmentId:tags.\"provider-assignment\",instanceId:tags.\"instance\",secretSlot:tags.\"secret-slot\",generation:tags.\"generation\"}}",
                    "--output", "json", "--only-show-errors"],
                ParseSecretSeedMetadataCollectionAsync,
                cancellationToken);
            if (!existing.Succeeded || existing.Value is null)
                return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, existing, command.Resources, mutation: false);
            var existingSecrets = existing.Value.Value;
            if (existingSecrets.Count == 1)
            {
                if (IsGeneratedProviderOwnedSecret(key, reference))
                {
                    if (!IsOwnedSecretMetadata(command, secretName, existingSecrets[0]))
                        return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.metadata-invalid", "The provider-owned secret metadata is missing or invalid.");
                }
                continue;
            }
            if (existingSecrets.Count != 0)
                return Failed(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.inventory-invalid", "The secret inventory is ambiguous.");
            if (command.IsResume && IsGeneratedProviderOwnedSecret(key, reference))
                return Uncertain(
                    command,
                    AzureProviderOperationPhase.FoundationSubmitted,
                    "azure.secrets.recovery-required",
                    "A provider-owned secret is absent after an interrupted seed and requires explicit recovery.");

            var secretRequest = new AzureSecretResolutionRequest(
                command.Context.WorkspaceId,
                command.Context.OrganizationId,
                command.Context.InstanceId,
                command.Context.ProviderAssignmentId,
                key,
                reference,
                command.Resources)
            {
                OperationId = command.Context.OperationId,
                AttemptNumber = command.AttemptNumber
            };
            AzureSecretLease lease;
            try
            {
                lease = await _secretResolver.ResolveAsync(secretRequest, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.cancelled", "Secret reference seeding was interrupted before completion.");
            }
            catch (Exception)
            {
                return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.resolve-uncertain", "An approved secret reference could not be resolved.");
            }

            await using (lease)
            {
                string directory;
                string file;
                try
                {
                    (directory, file) = await WriteTransientSecretFileAsync(lease, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return Uncertain(command, AzureProviderOperationPhase.FoundationSubmitted, "azure.secrets.cancelled", "Secret reference seeding was interrupted before completion.");
                }
                try
                {
                    EnsureMutationAuthority(command);
                    // Recheck the durable lease generation without materializing another secret.
                    // Reject a generation change observed here; the lease may still change
                    // after this check, and an already-submitted request cannot be fenced.
                    if (!await _secretResolver.IsAuthorizedAsync(secretRequest, cancellationToken))
                        return Uncertain(
                            command,
                            AzureProviderOperationPhase.FoundationSubmitted,
                            "azure.secrets.authorization-changed",
                            "Secret seeding authorization changed before the remote mutation.");
                    var seedArguments = new List<string>
                    {
                        "keyvault", "secret", "set", "--subscription", _scope.SubscriptionId, "--vault-name", vaultName,
                        "--name", secretName, "--file", file, "--output", "none", "--only-show-errors"
                    };
                    if (IsGeneratedProviderOwnedSecret(key, reference))
                    {
                        seedArguments.Add("--tags");
                        seedArguments.AddRange(OwnedSecretMetadataArguments(command, secretName));
                    }
                    var seeded = await ExecuteAzAsync<AzureCommandNoOutput>(command,
                        seedArguments,
                        static _ => AzureCommandNoOutput.Instance,
                        cancellationToken);
                    if (!seeded.Succeeded)
                        return ProcessFailure(command, AzureProviderOperationPhase.FoundationSubmitted, seeded, command.Resources, mutation: true);
                    changed = true;
                }
                finally
                {
                    DeleteTransientSecretFile(directory, file);
                }
            }
        }

        return Completed(command, AzureProviderOperationPhase.FoundationSubmitted, command.Resources, noOp: !changed);
    }

    private async Task<AzureProviderRunnerResult> RunSqlBootstrapAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var missing = RequireRegistry(command.Resources);
        if (missing is not null)
            return Failed(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.foundation-missing", missing);
        if (command.Resources.SqlServerFqdn is null || command.Resources.WorkloadIdentityClientId is null)
            return Failed(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.output-missing", "The SQL bootstrap identities are incomplete.");

        var compatibility = await ExecuteSqlCmdAsync<SafeValue<bool>>(command,
            ["-?"],
            output => new SafeValue<bool>(output.ToString().Contains("--authentication-method", StringComparison.Ordinal)),
            cancellationToken);
        if (!compatibility.Succeeded || compatibility.Value?.Value != true)
            return ProcessFailure(command, AzureProviderOperationPhase.FoundationReady, compatibility, command.Resources, mutation: false);

        var temporaryDirectory = string.Empty;
        var scriptPath = string.Empty;
        var firewallCleaned = false;
        EnsureMutationAuthority(command);
        try
        {
            var firewall = await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["sql", "server", "firewall-rule", "create", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--server", SqlServerName(command), "--name", TemporaryFirewallRuleName, "--start-ip-address", _options.SqlBootstrapIp,
                    "--end-ip-address", _options.SqlBootstrapIp, "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!firewall.Succeeded)
                return ProcessFailure(command, AzureProviderOperationPhase.FoundationReady, firewall, command.Resources, mutation: true);

            (temporaryDirectory, scriptPath) = await WriteSqlBootstrapFileAsync(command.Resources.WorkloadIdentityClientId, command.Plan.WorkloadName, cancellationToken);
            var sqlSucceeded = false;
            AzureCommandProcessFailureKind? bootstrapFailureKind = null;
            for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
            {
                EnsureMutationAuthority(command);
                var bootstrap = await ExecuteSqlCmdAsync<AzureCommandNoOutput>(command,
                    ["-S", $"tcp:{command.Resources.SqlServerFqdn},1433", "-d", "Elsa", ..SqlAuthenticationArguments(),
                        "-b", "-i", scriptPath],
                    static _ => AzureCommandNoOutput.Instance,
                    cancellationToken);
                if (bootstrap.Succeeded)
                {
                    sqlSucceeded = true;
                    break;
                }
                bootstrapFailureKind = bootstrap.FailureKind;
                if (bootstrap.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                    break;
                if (bootstrap.Status == AzureCommandProcessStatus.TerminationUncertain ||
                    bootstrap.FailureKind == AzureCommandProcessFailureKind.TerminationUncertain)
                    break;
                if (attempt + 1 < _options.ObservationAttempts)
                    await Task.Delay(_options.ObservationDelay, cancellationToken);
            }

            var firewallAbsent = await DeleteAndVerifyFirewallAsync(command, SqlServerName(command), CancellationToken.None);
            if (!firewallAbsent)
                return Uncertain(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.firewall-uncertain", "The temporary SQL firewall rule could not be proven absent.");
            firewallCleaned = true;
            if (!sqlSucceeded)
                return cancellationToken.IsCancellationRequested
                    ? Uncertain(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.cancelled", "SQL bootstrap was interrupted before completion.", processFailureKind: bootstrapFailureKind)
                    : Uncertain(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.bootstrap-uncertain", "SQL bootstrap did not produce a confirmed result.", processFailureKind: bootstrapFailureKind);

            return Completed(command, AzureProviderOperationPhase.FoundationReady, command.Resources);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                DeleteTransientSecretFile(temporaryDirectory, scriptPath);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            try
            {
                if (!firewallCleaned && !await DeleteAndVerifyFirewallAsync(command, SqlServerName(command), CancellationToken.None))
                    throw new InvalidOperationException("The temporary SQL firewall rule could not be proven absent.");
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("SQL bootstrap cleanup could not be proven complete.", cleanupFailure);
        }
    }

    private async Task<AzureProviderRunnerResult> RunSqlFirewallCreateAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var missing = RequireRegistry(command.Resources);
        if (missing is not null)
            return Failed(command, AzureProviderOperationPhase.SqlFirewallReady, "azure.sql.foundation-missing", missing);

        EnsureMutationAuthority(command);
        var existing = await ObserveSqlFirewallAsync(command, cancellationToken);
        if (existing == SqlFirewallObservationState.ExactPresent)
            return Completed(command, AzureProviderOperationPhase.SqlFirewallReady, command.Resources, noOp: true);
        if (existing != SqlFirewallObservationState.Absent)
            return existing == SqlFirewallObservationState.Uncertain
                ? Uncertain(command, AzureProviderOperationPhase.SqlFirewallReady, "azure.sql.firewall-uncertain", "The SQL firewall ownership boundary could not be proven before creation.")
                : Failed(command, AzureProviderOperationPhase.SqlFirewallReady, "azure.sql.firewall-uncertain", "The SQL firewall ownership boundary is ambiguous; creation was refused.");

        var firewall = await ExecuteAzAsync<AzureCommandNoOutput>(command,
            ["sql", "server", "firewall-rule", "create", "--subscription", _scope.SubscriptionId,
                "--resource-group", ResourceGroupName(command), "--server", SqlServerName(command),
                "--name", TemporaryFirewallRuleName, "--start-ip-address", _options.SqlBootstrapIp,
                "--end-ip-address", _options.SqlBootstrapIp, "--output", "none", "--only-show-errors"],
            static _ => AzureCommandNoOutput.Instance,
            cancellationToken);
        if (!firewall.Succeeded)
            return ProcessFailure(command, AzureProviderOperationPhase.SqlFirewallReady, firewall, command.Resources, mutation: true);

        var created = await ObserveSqlFirewallAsync(command, cancellationToken);
        if (created != SqlFirewallObservationState.ExactPresent)
            return Uncertain(command, AzureProviderOperationPhase.SqlFirewallReady, "azure.sql.firewall-uncertain", "The SQL firewall create result could not be proven exact.");

        return Completed(command, AzureProviderOperationPhase.SqlFirewallReady, command.Resources);
    }

    private async Task<AzureProviderRunnerResult> RunSqlBootstrapScriptAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = string.Empty;
        var scriptPath = string.Empty;
        var scriptSucceeded = false;
        try
        {
            var missing = RequireRegistry(command.Resources);
            if (missing is not null)
                return Failed(command, AzureProviderOperationPhase.SqlBootstrapReady, "azure.sql.foundation-missing", missing);
            if (command.Resources.SqlServerFqdn is null || command.Resources.WorkloadIdentityClientId is null)
                return Failed(command, AzureProviderOperationPhase.SqlBootstrapReady, "azure.sql.output-missing", "The SQL bootstrap identities are incomplete.");

            var compatibility = await ExecuteSqlCmdAsync<SafeValue<bool>>(command,
                ["-?"],
                output => new SafeValue<bool>(output.ToString().Contains("--authentication-method", StringComparison.Ordinal)),
                cancellationToken);
            if (!compatibility.Succeeded || compatibility.Value?.Value != true)
                return ProcessFailure(command, AzureProviderOperationPhase.SqlBootstrapReady, compatibility, command.Resources, mutation: false);

            (temporaryDirectory, scriptPath) = await WriteSqlBootstrapFileAsync(
                command.Resources.WorkloadIdentityClientId,
                command.Plan.WorkloadName,
                cancellationToken);

            // A failed process may already have committed part of the SQL script. Read
            // retries are safe, but repeating this mutation requires explicit recovery.
            EnsureMutationAuthority(command);
            var bootstrap = await ExecuteSqlCmdAsync<AzureCommandNoOutput>(command,
                ["-S", $"tcp:{command.Resources.SqlServerFqdn},1433", "-d", "Elsa", ..SqlAuthenticationArguments(),
                    "-b", "-i", scriptPath],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);

            if (!bootstrap.Succeeded)
                return cancellationToken.IsCancellationRequested
                    ? Uncertain(command, AzureProviderOperationPhase.SqlBootstrapReady, "azure.sql.cancelled", "SQL bootstrap was interrupted before completion.", processFailureKind: bootstrap.FailureKind)
                    : Uncertain(command, AzureProviderOperationPhase.SqlBootstrapReady, "azure.sql.bootstrap-uncertain", "SQL bootstrap did not produce a confirmed result.", processFailureKind: bootstrap.FailureKind);

            scriptSucceeded = true;
            return Completed(command, AzureProviderOperationPhase.SqlBootstrapReady, command.Resources);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                DeleteTransientSecretFile(temporaryDirectory, scriptPath);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            if (!scriptSucceeded)
            {
                try
                {
                    if (!await DeleteAndVerifyFirewallAsync(command, SqlServerName(command), CancellationToken.None))
                        throw new InvalidOperationException("The temporary SQL firewall rule could not be proven absent.");
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("SQL bootstrap cleanup could not be proven complete.", cleanupFailure);
        }
    }

    private async Task<AzureProviderRunnerResult> RunSqlFirewallCleanupAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var missing = RequireRegistry(command.Resources);
        if (missing is not null)
            return Failed(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.foundation-missing", missing);

        if (!await DeleteAndVerifyFirewallAsync(command, SqlServerName(command), cancellationToken))
            return Uncertain(command, AzureProviderOperationPhase.FoundationReady, "azure.sql.firewall-uncertain", "The temporary SQL firewall rule could not be proven absent.");

        return Completed(command, AzureProviderOperationPhase.FoundationReady, command.Resources);
    }

    private async Task<AzureProviderRunnerResult> RunWorkloadAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        if (CapacityFailure(command, AzureProviderOperationPhase.WorkloadReady) is { } capacityFailure)
            return capacityFailure;

        var missing = RequireRegistry(command.Resources);
        if (missing is not null)
            return Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.workload.foundation-missing", missing);

        var stable = command.Resources.StableTrafficRevisionName;
        if (stable is null)
        {
            var stableObservation = await ResolveStableTrafficAsync(command, cancellationToken);
            if (stableObservation.Error is not null)
                return stableObservation.Error;
            stable = stableObservation.Revision;
        }

        var revision = await ResolveRevisionSuffixAsync(command, cancellationToken);
        if (revision.Error is not null)
            return revision.Error;

        var deploymentName = WorkloadDeploymentName(command);
        EnsureMutationAuthority(command);
        var output = await ExecuteAzAsync(command,
            WorkloadDeploymentArguments(command, deploymentName, revision.Suffix!, stable),
            ParseDeploymentOutputsAsync,
            cancellationToken);
        if (!output.Succeeded || output.Value is null)
            return ProcessFailure(command, AzureProviderOperationPhase.WorkloadReady, output, command.Resources, mutation: true);

        AzureProviderResourceReferences resources;
        try
        {
            resources = ProjectWorkload(command, output.Value!.Value, command.Resources, command.Plan, deploymentName, revision.Suffix!, stable);
        }
        catch (ArgumentException exception)
        {
            return Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.workload.output-invalid", exception.Message);
        }

        var adminReady = await EnsureExactSqlBootstrapAdminAsync(command, allowMissingServer: false, cancellationToken: cancellationToken);
        if (adminReady is null)
            return Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.sql.admin-verification-uncertain", "The SQL bootstrap administrator could not be confirmed after workload deployment.", resources);
        if (!adminReady.Value)
            return Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.sql.admin-invalid", "The SQL administrator does not match the governed bootstrap identity.", resources);

        return Completed(command, AzureProviderOperationPhase.WorkloadReady, resources);
    }

    private async Task<AzureProviderRunnerResult> RunHealthAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Resources.WorkloadRevisionName is null || command.Resources.WorkloadResourceId is null)
            return Failed(command, AzureProviderOperationPhase.HealthVerified, "azure.health.workload-missing", "The candidate workload identity is missing.");
        var endpointResult = await ResolveEndpointAsync(command, cancellationToken);
        if (!endpointResult.Succeeded || string.IsNullOrWhiteSpace(endpointResult.Value?.Value))
            return ProcessFailure(command, AzureProviderOperationPhase.HealthVerified, endpointResult, command.Resources, mutation: false);
        var endpoint = endpointResult.Value!.Value;

        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var state = await ExecuteAzAsync(command,
                ["containerapp", "revision", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--name", AppName(command), "--revision", command.Resources.WorkloadRevisionName,
                    "--query", "properties.healthState", "--output", "tsv", "--only-show-errors"],
                ParseStringAsync,
                cancellationToken);
            if (state.Succeeded && string.Equals(state.Value?.Value, "Healthy", StringComparison.OrdinalIgnoreCase))
            {
                AzureProviderOperationValidation.ValidateEndpoint(endpoint);
                return Completed(command, AzureProviderOperationPhase.HealthVerified, command.Resources, health: AzureProviderHealth.Healthy, endpoint: endpoint);
            }
            if (state.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                return Uncertain(command, AzureProviderOperationPhase.HealthVerified, "azure.health.cancelled", "Candidate health verification was interrupted.");
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }

        return new AzureProviderRunnerResult(
            AzureProviderRunnerOutcome.Failed,
            AzureProviderOperationPhase.HealthVerified,
            command.Resources,
            AzureProviderHealth.Failed,
            null,
            AzureProviderSafeDiagnostics.Failure(command.Step, AzureProviderRunnerOutcome.Failed, "azure.health.unhealthy"),
            "azure.health.unhealthy",
            "The candidate did not become healthy within the bounded observation window.");
    }

    private async Task<AzureProviderRunnerResult> RunPromotionAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Resources.WorkloadRevisionName is null || command.Resources.WorkloadResourceId is null)
            return Failed(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.input-missing", "The candidate revision is missing.");
        var endpointResult = await ResolveEndpointAsync(command, cancellationToken);
        if (!endpointResult.Succeeded || string.IsNullOrWhiteSpace(endpointResult.Value?.Value))
            return ProcessFailure(command, AzureProviderOperationPhase.TrafficPromoted, endpointResult, command.Resources, mutation: false);
        var endpoint = endpointResult.Value!.Value;
        AzureProviderOperationValidation.ValidateEndpoint(endpoint);
        var candidateState = await ExecuteAzAsync(command,
            ["containerapp", "revision", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--revision", command.Resources.WorkloadRevisionName,
                "--query", "properties.{active:active,health:healthState,fqdn:fqdn}", "--output", "json", "--only-show-errors"],
            ParseRevisionStateAsync,
            cancellationToken);
        if (!candidateState.Succeeded || candidateState.Value is null)
            return ProcessFailure(command, AzureProviderOperationPhase.TrafficPromoted, candidateState, command.Resources, mutation: false);
        if (!candidateState.Value.Value.Active || !string.Equals(candidateState.Value.Value.Health, "Healthy", StringComparison.OrdinalIgnoreCase))
            return Failed(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.health-gate", "The candidate revision is not active and healthy.");

        // The platform health state only proves the readiness probe passed at some point. Before any
        // traffic moves, the exact candidate revision must answer the runtime health contract on its
        // own revision-scoped endpoint, which is the only route that cannot be served by the stable revision.
        var candidateHost = CandidateRevisionHost(candidateState.Value.Value.Fqdn, endpoint, AppName(command), command.Resources.WorkloadRevisionName);
        if (candidateHost is null)
            return Failed(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.candidate-endpoint-invalid", "The candidate revision endpoint is missing or outside the workload origin.");
        var readiness = await ExecuteHealthProbeAsync(command, $"https://{candidateHost}/health", cancellationToken);
        if (!readiness.Succeeded)
            return ProcessFailure(command, AzureProviderOperationPhase.TrafficPromoted, readiness, command.Resources, mutation: false,
                failedCode: "azure.promotion.candidate-unready", failedMessage: "The candidate revision did not answer its readiness route.");
        var candidateReadiness = ClassifyRuntimeHealth(readiness.Value?.Value);
        if (candidateReadiness != RuntimeHealthReport.Healthy)
            return candidateReadiness == RuntimeHealthReport.NotHealthy
                ? Failed(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.candidate-not-ready", "The candidate revision reports it is not healthy.")
                : Failed(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.candidate-readiness-invalid", "The candidate revision readiness response is outside the health contract.");

        EnsureMutationAuthority(command);
        var promoted = await ExecuteAzAsync<AzureCommandNoOutput>(command,
            ["containerapp", "ingress", "traffic", "set", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--revision-weight", $"{command.Resources.WorkloadRevisionName}=100", "--output", "none", "--only-show-errors"],
            static _ => AzureCommandNoOutput.Instance,
            cancellationToken);
        if (!promoted.Succeeded)
            return ProcessFailure(command, AzureProviderOperationPhase.TrafficPromoted, promoted, command.Resources, mutation: true);

        var traffic = await WaitForTrafficAsync(command, command.Resources.WorkloadRevisionName, requiredZeroRevision: null, cancellationToken);
        if (traffic is not true)
            return traffic is null
                ? Uncertain(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.traffic-uncertain", "Candidate traffic promotion could not be confirmed.")
                : Uncertain(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.traffic-invalid", "Candidate traffic did not reach the required single-revision state.");

        var health = await ExecuteHealthProbeAsync(command, $"{endpoint.TrimEnd('/')}/health", cancellationToken);
        if (!health.Succeeded || ClassifyRuntimeHealth(health.Value?.Value) != RuntimeHealthReport.Healthy)
            return Uncertain(command, AzureProviderOperationPhase.TrafficPromoted, "azure.promotion.health-uncertain", "Candidate external health could not be confirmed.", command.Resources);

        return Completed(
            command,
            AzureProviderOperationPhase.TrafficPromoted,
            command.Resources with { StableTrafficRevisionName = command.Resources.WorkloadRevisionName },
            health: AzureProviderHealth.Healthy,
            endpoint: endpoint);
    }

    private Task<AzureCommandProcessResult<SafeValue<string>>> ResolveEndpointAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAzAsync(command,
            ["containerapp", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--query", "properties.configuration.ingress.fqdn", "--output", "tsv", "--only-show-errors"],
            output =>
            {
                var host = output.ToString().Trim();
                if (string.IsNullOrWhiteSpace(host))
                    throw new FormatException();
                var rawEndpoint = host.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? host : $"https://{host}";
                var endpoint = AzureProviderOperationValidation.NormalizeEndpoint(rawEndpoint)!;
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                    !uri.Host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase) ||
                    !uri.Host.StartsWith(AppName(command) + ".", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException();
                return new SafeValue<string>(endpoint);
            },
            cancellationToken);

    private async Task<AzureProviderRunnerResult> RunRestoreStableTrafficAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var stable = command.StableTrafficRevisionName;
        if (string.IsNullOrWhiteSpace(stable))
            return Uncertain(command, AzureProviderOperationPhase.HealthVerified, "azure.rollback.stable-missing", "No previously verified stable traffic revision is available.");
        var candidate = command.Resources.WorkloadRevisionName;
        var weights = candidate is null || string.Equals(candidate, stable, StringComparison.Ordinal)
            ? $"{stable}=100"
            : $"{stable}=100 {candidate}=0";
        EnsureMutationAuthority(command);
        var restored = await ExecuteAzAsync<AzureCommandNoOutput>(command,
            ["containerapp", "ingress", "traffic", "set", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--revision-weight", weights, "--output", "none", "--only-show-errors"],
            static _ => AzureCommandNoOutput.Instance,
            cancellationToken);
        if (!restored.Succeeded)
            return Uncertain(command, AzureProviderOperationPhase.HealthVerified, "azure.rollback.uncertain", "Stable traffic restoration was not confirmed.", command.Resources);

        var requiredZeroRevision = candidate is null || string.Equals(candidate, stable, StringComparison.Ordinal) ? null : candidate;
        var traffic = await WaitForTrafficAsync(command, stable, requiredZeroRevision, cancellationToken);
        if (traffic is not true)
            return Uncertain(command, AzureProviderOperationPhase.HealthVerified, "azure.rollback.uncertain", "Stable traffic restoration was not confirmed.", command.Resources);
        return Completed(command, AzureProviderOperationPhase.HealthVerified, command.Resources with { StableTrafficRevisionName = stable },
            health: AzureProviderHealth.Unknown,
            stableTrafficRestored: true,
            endpoint: null);
    }

    private async Task<AzureProviderRunnerResult> RunCleanupAsync(
        AzureProviderRunnerCommand command,
        CancellationToken cancellationToken)
    {
        var resources = command.Resources;
        var groupExists = await ExecuteAzAsync(command,
            ["group", "exists", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command), "--output", "tsv", "--only-show-errors"],
            ParseBooleanAsync,
            cancellationToken);
        if (!groupExists.Succeeded)
            return ProcessFailure(command, AzureProviderOperationPhase.CleanupVerified, groupExists, resources, mutation: false);

        if (groupExists.Value!.Value)
        {
            var tags = await ExecuteAzAsync(command,
                ["group", "show", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command), "--query", "tags", "--output", "json", "--only-show-errors"],
                ParseTagsAsync,
                cancellationToken);
            if (!tags.Succeeded || tags.Value is null || !OwnsGroup(tags.Value.Value, command.Plan.WorkloadName))
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.ownership-unverified", "The target resource group is not proven to belong to this workload.");

            var inventory = await ExecuteAzAsync(command,
                ["resource", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command), "--output", "json", "--only-show-errors"],
                ParseResourcesAsync,
                cancellationToken);
            if (!inventory.Succeeded || inventory.Value is null)
                return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.inventory-uncertain", "The owned resource inventory could not be confirmed.");
            if (!IsExactInventory(command, inventory.Value!.Value, command.Plan.WorkloadName))
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.ownership-unverified", "The resource inventory contains an unowned resource.");

            var identityId = ResourceId(command, "Microsoft.ManagedIdentity", "userAssignedIdentities", $"{command.Plan.WorkloadName}-identity");
            var identityPresent = inventory.Value.Value.Any(resource => string.Equals(resource.Id, identityId, StringComparison.OrdinalIgnoreCase));
            if (resources.WorkloadIdentityPrincipalId is null && identityPresent)
            {
                var principal = await ExecuteAzAsync(command,
                    ["identity", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                        "--name", $"{command.Plan.WorkloadName}-identity", "--query", "principalId", "--output", "tsv", "--only-show-errors"],
                    ParseStringAsync,
                    cancellationToken);
                if (!principal.Succeeded || string.IsNullOrWhiteSpace(principal.Value?.Value))
                    return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.identity-observation-uncertain", "The owned workload identity principal could not be confirmed before cleanup.");
                try
                {
                    resources = resources with
                    {
                        WorkloadIdentityPrincipalId = NormalizeGuid(principal.Value.Value, "workloadIdentityPrincipalId")
                    };
                }
                catch (ArgumentException)
                {
                    return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.identity-invalid", "The owned workload identity principal is invalid.");
                }
            }

            var vaultId = resources.KeyVaultResourceId ?? ResourceId(command, "Microsoft.KeyVault", "vaults", $"{command.Plan.WorkloadName}-kv");
            var vaultPresent = inventory.Value.Value.Any(resource =>
                string.Equals(resource.Id, vaultId, StringComparison.OrdinalIgnoreCase));
            if (vaultPresent)
            {
                var assignments = await ExecuteAzAsync(command,
                    ["role", "assignment", "list", "--subscription", _scope.SubscriptionId, "--scope", vaultId,
                        "--output", "json", "--only-show-errors"],
                    ParseRoleAssignmentsAsync,
                    cancellationToken);
                if (!assignments.Succeeded || assignments.Value is null || !HasSafeVaultAssignmentsForCleanup(command, assignments.Value.Value, resources, command.Plan.WorkloadName))
                    return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.rbac-unverified", "The owned Key Vault role-assignment inventory is not exact.");
            }
        }

        var registryId = resources.RegistryResourceId ?? RegistryResourceId();
        var roleAssignmentId = resources.AcrPullRoleAssignmentId;
        var acrDeploymentId = resources.AcrPullDeploymentId;
        var workloadIdentityPrincipalId = resources.WorkloadIdentityPrincipalId;
        var registryGroupPresent = true;
        if (registryGroupPresent && workloadIdentityPrincipalId is not null)
        {
            acrDeploymentId ??= DeploymentId(
                _scope.RegistrySubscriptionId,
                _scope.RegistryResourceGroupName,
                AcrDeploymentName(command, workloadIdentityPrincipalId));
            if (roleAssignmentId is null)
            {
                var discovered = await DiscoverAcrRoleAssignmentAsync(
                    command,
                    registryId,
                    workloadIdentityPrincipalId,
                    expectedAssignmentId: null,
                    cancellationToken);
                if (discovered.Status == AcrRoleDiscoveryStatus.Uncertain)
                {
                    var registryGroupExists = await ExecuteAzAsync(command,
                        ["group", "exists", "--subscription", _scope.RegistrySubscriptionId, "--name", _scope.RegistryResourceGroupName,
                            "--output", "tsv", "--only-show-errors"],
                        ParseBooleanAsync,
                        cancellationToken);
                    if (!registryGroupExists.Succeeded || registryGroupExists.Value is null || registryGroupExists.Value.Value)
                        return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-observation-uncertain", "The owned ACR role assignment could not be observed before deletion.");
                    registryGroupPresent = false;
                    acrDeploymentId = null;
                }
                if (discovered.Status == AcrRoleDiscoveryStatus.Ambiguous)
                    return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-provenance-invalid", "The ACR role-assignment inventory is not exact for the workload identity.");
                if (registryGroupPresent)
                    roleAssignmentId = discovered.AssignmentId;
            }
        }

        if (registryGroupPresent && roleAssignmentId is not null)
        {
            try { ValidateExactRoleAssignmentId(roleAssignmentId, registryId); }
            catch (ArgumentException exception) { return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-scope-invalid", exception.Message); }
            if (workloadIdentityPrincipalId is null)
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-provenance-invalid", "The owned ACR role assignment lacks the exact registry and workload identity provenance required for deletion.");
            var roleProvenance = await ValidateAcrRoleAssignmentAsync(command, roleAssignmentId,
                registryId, workloadIdentityPrincipalId, cancellationToken);
            if (roleProvenance is null)
                return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-observation-uncertain", "The owned ACR role assignment could not be observed before deletion.");
            if (!roleProvenance.Value)
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-provenance-invalid", "The ACR role assignment does not match the exact registry, workload identity, and AcrPull role.");
            EnsureMutationAuthority(command);
            await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["role", "assignment", "delete", "--ids", roleAssignmentId, "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!await RoleAssignmentAbsentAsync(command, registryId, roleAssignmentId, workloadIdentityPrincipalId, cancellationToken))
                return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.role-uncertain", "The owned ACR role assignment could not be proven absent.");
        }

        if (registryGroupPresent && acrDeploymentId is not null)
        {
            try
            {
                ValidateExactAcrDeploymentId(acrDeploymentId, command, workloadIdentityPrincipalId);
            }
            catch (ArgumentException exception)
            {
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.deployment-scope-invalid", exception.Message);
            }
            var deployment = ResourceName(acrDeploymentId);
            if (deployment is null)
                return Failed(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.deployment-invalid", "The owned ACR deployment identity is invalid.");
            EnsureMutationAuthority(command);
            await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["deployment", "group", "delete", "--subscription", _scope.RegistrySubscriptionId, "--resource-group", _scope.RegistryResourceGroupName,
                    "--name", deployment, "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!await DeploymentAbsentAsync(command, deployment, cancellationToken))
                return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.deployment-uncertain", "The owned ACR deployment record could not be proven absent.");
        }

        if (groupExists.Value!.Value)
        {
            EnsureMutationAuthority(command);
            await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["group", "delete", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command), "--yes", "--no-wait", "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!await ResourceGroupAbsentAsync(command, cancellationToken))
                return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.group-uncertain", "The owned resource group could not be proven absent.");
        }

        var vaultName = $"{command.Plan.WorkloadName}-kv";
        var exactVaultId = resources.KeyVaultResourceId ?? ResourceId(command, "Microsoft.KeyVault", "vaults", vaultName);
        if (!await PurgeAndVerifyVaultAsync(command, vaultName, exactVaultId, cancellationToken))
            return Uncertain(command, AzureProviderOperationPhase.CleanupVerified, "azure.cleanup.vault-uncertain", "The owned Key Vault could not be proven absent.");

        return new AzureProviderRunnerResult(
            AzureProviderRunnerOutcome.Completed,
            AzureProviderOperationPhase.CleanupVerified,
            new AzureProviderResourceReferences(),
            AzureProviderHealth.Unknown,
            null,
            [],
            "azure.cleanup.completed",
            "Exact owned-resource cleanup was verified.",
            OwnedResourcesAbsent: true);
    }

    private async Task<AzureCommandProcessResult<T>> ExecuteAzAsync<T>(
        AzureProviderRunnerCommand command,
        IReadOnlyList<string> arguments,
        AzureCommandOutputProjector<T> projector,
        CancellationToken cancellationToken)
        where T : AzureCommandSafeOutput
    {
        _options.ValidateExecutionAuthority(command.Context, _scope);
        var request = new AzureCommandProcessRequest(
            _options.AzureCliPath,
            arguments.Select(AzureCommandArgument.Safe).ToArray(),
            workingDirectory: _options.TemplateRoot);
        return await _process.ExecuteAsync(request, projector, cancellationToken);
    }

    private async Task<AzureCommandProcessResult<T>> ExecuteSqlCmdAsync<T>(
        AzureProviderRunnerCommand command,
        IReadOnlyList<string> arguments,
        AzureCommandOutputProjector<T> projector,
        CancellationToken cancellationToken)
        where T : AzureCommandSafeOutput
    {
        _options.ValidateExecutionAuthority(command.Context, _scope);
        var request = new AzureCommandProcessRequest(
            _options.SqlCmdPath,
            arguments.Select(AzureCommandArgument.Safe).ToArray(),
            workingDirectory: _options.TemplateRoot);
        return await _process.ExecuteAsync(request, projector, cancellationToken);
    }

    /// <summary>
    /// Probes a runtime health route and returns its bounded body. The runtime health contract is a
    /// short plain-text report (<c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>) matched byte-exact;
    /// anything longer than <see cref="MaximumHealthReportCharacters"/> is rejected before it is inspected.
    /// </summary>
    private async Task<AzureCommandProcessResult<SafeValue<string>>> ExecuteHealthProbeAsync(AzureProviderRunnerCommand command, string endpoint, CancellationToken cancellationToken)
    {
        _options.ValidateExecutionAuthority(command.Context, _scope);
        var request = new AzureCommandProcessRequest(
            _options.CurlPath,
            new[] { "--fail", "--silent", "--show-error", "--retry", "30", "--retry-all-errors", "--retry-delay", "5", "--max-time", "10", endpoint }
                .Select(AzureCommandArgument.Safe).ToArray());
        return await _process.ExecuteAsync(request, ParseHealthReportAsync, cancellationToken);
    }

    private const int MaximumHealthReportCharacters = 64;

    private enum RuntimeHealthReport { Healthy, NotHealthy, Invalid }

    // The report is kept byte-exact: padding or line endings are outside the contract and fail closed.
    private static SafeValue<string> ParseHealthReportAsync(ReadOnlyMemory<char> output) =>
        output.Length <= MaximumHealthReportCharacters ? new(output.ToString()) : throw new FormatException();

    private static RuntimeHealthReport ClassifyRuntimeHealth(string? report) => report switch
    {
        "Healthy" => RuntimeHealthReport.Healthy,
        "Degraded" or "Unhealthy" => RuntimeHealthReport.NotHealthy,
        _ => RuntimeHealthReport.Invalid
    };

    /// <summary>
    /// The candidate revision's own host is exactly <c>{revision}.{environment domain}</c>, where the
    /// environment domain is the workload origin host without its <c>{app}.</c> prefix. Any other value
    /// (missing, foreign domain, another revision, scheme or path) is rejected so the readiness probe
    /// can never be answered by the stable revision or an unrelated host.
    /// </summary>
    private static string? CandidateRevisionHost(string? revisionFqdn, string endpoint, string appName, string revisionName)
    {
        var host = revisionFqdn?.Trim();
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) || origin.HostNameType != UriHostNameType.Dns)
            return null;
        var prefix = $"{appName}.";
        if (!origin.Host.StartsWith(prefix, StringComparison.Ordinal) || !revisionName.StartsWith($"{appName}--", StringComparison.Ordinal))
            return null;
        var expected = $"{revisionName}.{origin.Host[prefix.Length..]}";
        return string.Equals(host, expected, StringComparison.Ordinal) ? host : null;
    }

    private async Task<bool?> WaitForRoleAssignmentAsync(AzureProviderRunnerCommand command, string registryId, string principalId, string expectedAssignmentId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var list = await ListRegistryRoleAssignmentsAsync(command, registryId, principalId, cancellationToken);
            if (!list.Succeeded)
            {
                if (list.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                    return null;
            }
            else
            {
                var exact = list.Value!.Value.Where(x => string.Equals(x.Id, expectedAssignmentId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 1)
                    return string.Equals(exact[0].Scope, registryId, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(exact[0].PrincipalId, principalId, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(RoleDefinitionId(exact[0].RoleDefinitionId), AcrPullRoleDefinitionId, StringComparison.OrdinalIgnoreCase);
                if (exact.Length > 1)
                    return false;
            }
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return null;
    }

    private async Task<(string? Revision, AzureProviderRunnerResult? Error)> ResolveStableTrafficAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken)
    {
        var count = await ExecuteAzAsync(command,
            ["resource", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command), "--resource-type", "Microsoft.App/containerApps",
                "--query", "[?name=='" + AppName(command) + "'] | length(@)", "--output", "tsv", "--only-show-errors"],
            ParseIntegerAsync,
            cancellationToken);
        if (!count.Succeeded)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.observation-uncertain", "Existing workload traffic could not be observed."));
        if (count.Value!.Value == 0)
            return (null, null);
        if (count.Value.Value != 1)
            return (null, Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.ambiguous", "Expected exactly one governed Container App."));

        var traffic = await ExecuteAzAsync(command,
            ["containerapp", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command), "--name", AppName(command),
                "--query", "properties.configuration.ingress.traffic", "--output", "json", "--only-show-errors"],
            ParseTrafficAsync,
            cancellationToken);
        if (!traffic.Succeeded || traffic.Value is null)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.observation-uncertain", "Existing workload traffic could not be observed."));
        var stable = traffic.Value!.Value.SingleOrDefault(x => x.Weight == 100);
        if (stable is null || traffic.Value.Value.Sum(x => x.Weight) != 100 || string.IsNullOrWhiteSpace(stable.RevisionName))
            return (null, Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.ambiguous", "Existing workload traffic has no single 100% revision."));
        var state = await ExecuteAzAsync(command,
            ["containerapp", "revision", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--revision", stable.RevisionName, "--query", "properties.{active:active,health:healthState}",
                "--output", "json", "--only-show-errors"],
            ParseRevisionStateAsync,
            cancellationToken);
        if (!state.Succeeded || state.Value is null)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.observation-uncertain", "Existing stable revision health could not be observed."));
        return state.Value!.Value.Active && string.Equals(state.Value.Value.Health, "Healthy", StringComparison.OrdinalIgnoreCase)
            ? (stable.RevisionName, null)
            : (null, Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.traffic.unhealthy", "Existing stable traffic is not active and healthy."));
    }

    private async Task<(string? Suffix, AzureProviderRunnerResult? Error)> ResolveRevisionSuffixAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken)
    {
        var baseSuffix = command.Plan.Fingerprint[..24];
        var count = await ExecuteAzAsync(command,
            ["resource", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command), "--resource-type", "Microsoft.App/containerApps",
                "--query", "[?name=='" + AppName(command) + "'] | length(@)", "--output", "tsv", "--only-show-errors"],
            ParseIntegerAsync,
            cancellationToken);
        if (!count.Succeeded)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.revision.observation-uncertain", "Existing workload revisions could not be observed."));
        if (count.Value!.Value == 0)
            return (baseSuffix, null);
        if (count.Value.Value != 1)
            return (null, Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.revision.ambiguous", "Expected exactly one governed Container App."));

        var current = await ExecuteAzAsync(command,
            ["resource", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--resource-type", "Microsoft.App/containerApps", "--name", AppName(command), "--query", "properties.template.revisionSuffix",
                "--output", "tsv", "--only-show-errors"],
            ParseStringAsync,
            cancellationToken);
        if (!current.Succeeded)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.revision.observation-uncertain", "Existing workload revisions could not be observed."));
        if (IsRevisionSuffixForPlan(current.Value?.Value, baseSuffix))
            return (current.Value!.Value, null);

        var names = await ExecuteAzAsync(command,
            ["containerapp", "revision", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", AppName(command), "--query", "[].name", "--output", "json", "--only-show-errors"],
            ParseStringArrayAsync,
            cancellationToken);
        if (!names.Succeeded || names.Value is null)
            return (null, Uncertain(command, AzureProviderOperationPhase.WorkloadReady, "azure.revision.observation-uncertain", "Existing workload revisions could not be observed."));
        for (var ordinal = 0; ordinal < 1000; ordinal++)
        {
            var candidate = ordinal == 0 ? baseSuffix : $"{baseSuffix}-r{ordinal}";
            if (!names.Value!.Value.Contains($"{AppName(command)}--{candidate}", StringComparer.OrdinalIgnoreCase))
                return (candidate, null);
        }
        return (null, Failed(command, AzureProviderOperationPhase.WorkloadReady, "azure.revision.exhausted", "No deterministic workload revision suffix was available."));
    }

    private async Task<bool?> WaitForTrafficAsync(
        AzureProviderRunnerCommand command,
        string desiredRevision,
        string? requiredZeroRevision,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var traffic = await ExecuteAzAsync(command,
                ["containerapp", "show", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--name", AppName(command), "--query", "properties.configuration.ingress.traffic", "--output", "json", "--only-show-errors"],
                ParseTrafficAsync,
                cancellationToken);
            if (traffic.Succeeded && traffic.Value is not null && traffic.Value.Value.Sum(x => x.Weight) == 100)
            {
                var desiredEntries = traffic.Value.Value.Where(x => string.Equals(x.RevisionName, desiredRevision, StringComparison.OrdinalIgnoreCase)).ToArray();
                var nonDesired = traffic.Value.Value.Any(x => !string.Equals(x.RevisionName, desiredRevision, StringComparison.OrdinalIgnoreCase) && x.Weight != 0);
                var requiredZero = requiredZeroRevision is null ||
                    traffic.Value.Value.Count(x => string.Equals(x.RevisionName, requiredZeroRevision, StringComparison.OrdinalIgnoreCase) && x.Weight == 0) == 1;
                if (desiredEntries.Length == 1 && desiredEntries[0].Weight == 100 && !nonDesired && requiredZero)
                    return true;
            }
            if (traffic.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                return null;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private async Task<bool> DeleteAndVerifyFirewallAsync(AzureProviderRunnerCommand command, string serverName, CancellationToken cancellationToken)
    {
        EnsureMutationAuthority(command);
        var beforeDelete = await ExecuteAzAsync(command,
            ["sql", "server", "firewall-rule", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--server", serverName, "--output", "json", "--only-show-errors"],
            ParseFirewallRulesAsync,
            cancellationToken);
        if (!beforeDelete.Succeeded || beforeDelete.Value is null)
            return false;

        var rules = beforeDelete.Value.Value;
        var state = ClassifySqlFirewall(rules);
        if (state == SqlFirewallObservationState.Absent)
            return true;
        if (state != SqlFirewallObservationState.ExactPresent)
            return false;

        await ExecuteAzAsync<AzureCommandNoOutput>(command,
            ["sql", "server", "firewall-rule", "delete", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--server", serverName, "--name", TemporaryFirewallRuleName, "--output", "none", "--only-show-errors"],
            static _ => AzureCommandNoOutput.Instance,
            cancellationToken);
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var list = await ExecuteAzAsync(command,
                ["sql", "server", "firewall-rule", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--server", serverName, "--output", "json", "--only-show-errors"],
                ParseFirewallRulesAsync,
                cancellationToken);
            if (list.Succeeded && list.Value is not null &&
                ClassifySqlFirewall(list.Value.Value) == SqlFirewallObservationState.Absent)
                return true;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private async Task<bool?> EnsureExactSqlBootstrapAdminAsync(
        AzureProviderRunnerCommand command,
        bool allowMissingServer,
        CancellationToken cancellationToken)
    {
        var server = $"{command.Plan.WorkloadName}-sql";
        var count = await ExecuteAzAsync(command,
            ["sql", "server", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--query", $"[?name=='{server}'] | length(@)", "--output", "tsv", "--only-show-errors"],
            ParseIntegerAsync,
            cancellationToken);
        if (!count.Succeeded || count.Value is null)
            return null;
        if (count.Value.Value == 0)
            return allowMissingServer;
        if (count.Value.Value != 1)
            return false;

        var admins = await ExecuteAzAsync(command,
            ["sql", "server", "ad-admin", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--server", server, "--output", "json", "--only-show-errors"],
            ParseAdminsAsync,
            cancellationToken);
        if (!admins.Succeeded || admins.Value is null)
            return null;
        if (admins.Value.Value.Count > 1)
            return false;
        if (admins.Value.Value.Count == 0)
        {
            EnsureMutationAuthority(command);
            var created = await ExecuteAzAsync<AzureCommandNoOutput>(command,
                ["sql", "server", "ad-admin", "create", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--server", server, "--display-name", _options.SqlBootstrapLogin, "--object-id", _options.SqlBootstrapObjectId,
                    "--output", "none", "--only-show-errors"],
                static _ => AzureCommandNoOutput.Instance,
                cancellationToken);
            if (!created.Succeeded)
                return null;
            admins = await ExecuteAzAsync(command,
                ["sql", "server", "ad-admin", "list", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                    "--server", server, "--output", "json", "--only-show-errors"],
                ParseAdminsAsync,
                cancellationToken);
            if (!admins.Succeeded || admins.Value is null)
                return null;
        }

        if (admins.Value.Value.Count != 1 ||
            !string.Equals(admins.Value.Value[0].Login, _options.SqlBootstrapLogin, StringComparison.Ordinal) ||
            !string.Equals(admins.Value.Value[0].Sid, _options.SqlBootstrapObjectId, StringComparison.OrdinalIgnoreCase))
            return false;

        EnsureMutationAuthority(command);
        var enabled = await ExecuteAzAsync<AzureCommandNoOutput>(command,
            ["sql", "server", "ad-only-auth", "enable", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
                "--name", server, "--output", "none", "--only-show-errors"],
            static _ => AzureCommandNoOutput.Instance,
            cancellationToken);
        return enabled.Succeeded ? true : null;
    }

    private async Task<bool> RoleAssignmentAbsentAsync(
        AzureProviderRunnerCommand command,
        string registryId,
        string assignmentId,
        string principalId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var list = await ListRegistryRoleAssignmentsAsync(command, registryId, principalId, cancellationToken);
            if (list.Succeeded && list.Value is not null && !list.Value.Value.Any(x => string.Equals(x.Id, assignmentId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private async Task<AcrRoleDiscovery> DiscoverAcrRoleAssignmentAsync(
        AzureProviderRunnerCommand command,
        string registryId,
        string principalId,
        string? expectedAssignmentId,
        CancellationToken cancellationToken)
    {
        var list = await ListRegistryRoleAssignmentsAsync(command, registryId, principalId, cancellationToken);
        if (!list.Succeeded || list.Value is null)
            return new(AcrRoleDiscoveryStatus.Uncertain, null);

        var scoped = list.Value.Value.Where(assignment =>
            string.Equals(assignment.Scope, registryId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(assignment.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (expectedAssignmentId is not null)
        {
            var expectedRole = ExpectedAcrPullRoleDefinitionId(registryId);
            if (scoped.Length == 0)
                return new(AcrRoleDiscoveryStatus.Absent, null);
            if (scoped.Any(x => !string.Equals(x.RoleDefinitionId, expectedRole, StringComparison.OrdinalIgnoreCase)))
                return new(AcrRoleDiscoveryStatus.Ambiguous, null);
            if (scoped.Length != 1)
                return new(AcrRoleDiscoveryStatus.Ambiguous, null);
        }

        var exact = expectedAssignmentId is null
            ? scoped.Where(assignment =>
                string.Equals(RoleDefinitionId(assignment.RoleDefinitionId), AcrPullRoleDefinitionId, StringComparison.OrdinalIgnoreCase)).ToArray()
            : scoped.Where(assignment =>
                string.Equals(assignment.RoleDefinitionId, ExpectedAcrPullRoleDefinitionId(registryId), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(assignment.Id, expectedAssignmentId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 0)
            return new(expectedAssignmentId is null ? AcrRoleDiscoveryStatus.Absent : AcrRoleDiscoveryStatus.Ambiguous, null);
        if (exact.Length != 1 || string.IsNullOrWhiteSpace(exact[0].Id))
            return new(AcrRoleDiscoveryStatus.Ambiguous, null);
        try
        {
            ValidateExactRoleAssignmentId(exact[0].Id!, registryId);
            return new(AcrRoleDiscoveryStatus.Exact, exact[0].Id);
        }
        catch (ArgumentException)
        {
            return new(AcrRoleDiscoveryStatus.Ambiguous, null);
        }
    }

    private async Task<bool?> ValidateAcrRoleAssignmentAsync(
        AzureProviderRunnerCommand command,
        string assignmentId,
        string registryId,
        string principalId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var list = await ListRegistryRoleAssignmentsAsync(command, registryId, principalId, cancellationToken);
            if (list.Succeeded && list.Value is not null)
            {
                var exact = list.Value.Value.Where(x => string.Equals(x.Id, assignmentId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exact.Length == 0)
                    return true;
                if (exact.Length != 1)
                    return false;
                return string.Equals(exact[0].Scope, registryId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(exact[0].PrincipalId, principalId, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(RoleDefinitionId(exact[0].RoleDefinitionId), AcrPullRoleDefinitionId, StringComparison.OrdinalIgnoreCase);
            }
            if (list.Status == AzureCommandProcessStatus.Cancelled || cancellationToken.IsCancellationRequested)
                return null;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return null;
    }

    private Task<AzureCommandProcessResult<SafeValue<IReadOnlyList<RoleAssignment>>>> ListRegistryRoleAssignmentsAsync(
        AzureProviderRunnerCommand command,
        string registryId,
        string principalId,
        CancellationToken cancellationToken) =>
        ExecuteAzAsync(command,
            ["role", "assignment", "list", "--subscription", _scope.RegistrySubscriptionId,
                "--scope", registryId, "--assignee-object-id", principalId,
                "--fill-principal-name", "false", "--fill-role-definition-name", "false",
                "--output", "json", "--only-show-errors"],
            ParseRoleAssignmentsAsync,
            cancellationToken);

    private async Task<bool> DeploymentAbsentAsync(AzureProviderRunnerCommand command, string deploymentName, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var list = await ExecuteAzAsync(command,
                ["deployment", "group", "list", "--subscription", _scope.RegistrySubscriptionId, "--resource-group", _scope.RegistryResourceGroupName,
                    "--output", "json", "--only-show-errors"],
                ParseDeploymentsAsync,
                cancellationToken);
            if (list.Succeeded && list.Value is not null && !list.Value.Value.Any(x => string.Equals(x.Name, deploymentName, StringComparison.Ordinal)))
                return true;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private async Task<bool> ResourceGroupAbsentAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.ObservationAttempts; attempt++)
        {
            var exists = await ExecuteAzAsync(command,
                ["group", "exists", "--subscription", _scope.SubscriptionId, "--name", ResourceGroupName(command), "--output", "tsv", "--only-show-errors"],
                ParseBooleanAsync,
                cancellationToken);
            if (exists.Succeeded && exists.Value?.Value == false)
                return true;
            if (attempt + 1 < _options.ObservationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private async Task<bool> PurgeAndVerifyVaultAsync(
        AzureProviderRunnerCommand command,
        string vaultName,
        string exactVaultId,
        CancellationToken cancellationToken)
    {
        var purgeRequested = false;
        var consecutiveAbsenceObservations = 0;
        var observationAttempts = Math.Max(2, _options.ObservationAttempts);
        for (var attempt = 0; attempt < observationAttempts; attempt++)
        {
            var deleted = await ExecuteAzAsync(command,
                ["keyvault", "list-deleted", "--subscription", _scope.SubscriptionId, "--resource-type", "vault", "--output", "json", "--only-show-errors"],
                ParseDeletedVaultsAsync,
                cancellationToken);
            if (!deleted.Succeeded || deleted.Value is null)
            {
                consecutiveAbsenceObservations = 0;
                if (attempt + 1 < observationAttempts)
                    await Task.Delay(_options.ObservationDelay, cancellationToken);
                continue;
            }
            var candidates = deleted.Value!.Value.Where(x => string.Equals(x.Name, vaultName, StringComparison.Ordinal) &&
                                                       string.Equals(x.EffectiveLocation, _scope.Location, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0)
            {
                consecutiveAbsenceObservations++;
                if (consecutiveAbsenceObservations >= 2)
                    return true;
                if (attempt + 1 < observationAttempts)
                    await Task.Delay(_options.ObservationDelay, cancellationToken);
                continue;
            }
            if (candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.EffectiveVaultId)))
                return false;
            var matches = candidates.Where(candidate =>
                string.Equals(candidate.EffectiveVaultId, exactVaultId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                consecutiveAbsenceObservations++;
                if (consecutiveAbsenceObservations >= 2)
                    return true;
                if (attempt + 1 < observationAttempts)
                    await Task.Delay(_options.ObservationDelay, cancellationToken);
                continue;
            }
            if (matches.Length != 1)
                return false;
            consecutiveAbsenceObservations = 0;
            if (!purgeRequested)
            {
                EnsureMutationAuthority(command);
                await ExecuteAzAsync<AzureCommandNoOutput>(command,
                    ["keyvault", "purge", "--subscription", _scope.SubscriptionId, "--name", vaultName, "--location", _scope.Location,
                        "--output", "none", "--only-show-errors"],
                    static _ => AzureCommandNoOutput.Instance,
                    cancellationToken);
                purgeRequested = true;
            }
            if (attempt + 1 < observationAttempts)
                await Task.Delay(_options.ObservationDelay, cancellationToken);
        }
        return false;
    }

    private IReadOnlyList<string> FoundationDeploymentArguments(AzureProviderRunnerCommand command, string deploymentName) =>
        ["deployment", "group", "create", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
            "--name", deploymentName, "--template-file", Path.Combine(_options.TemplateRoot, "main.bicep"), "--parameters",
            ..TemplateIdentityArguments(command), $"location={_scope.Location}", $"imageRepository={command.Plan.ImageRepository}", $"imageDigest={command.Plan.ImageDigest}",
            $"registryName={_scope.RegistryName}", $"registrySubscriptionId={_scope.RegistrySubscriptionId}",
            $"registryResourceGroupName={_scope.RegistryResourceGroupName}", $"sqlBootstrapObjectId={_options.SqlBootstrapObjectId}",
            $"sqlBootstrapLogin={_options.SqlBootstrapLogin}", $"owner={_options.Owner}",
            $"sqlConnectionSecretName={SqlConnectionSecretName}", $"signingKeySecretName={SigningKeySecretName}",
            $"adminPasswordSecretName={AdminPasswordSecretName}", $"adminUsername={_options.RuntimeAdminUsername}",
            $"elsaVersion={command.Plan.ElsaVersion}", ..ReleaseIdentityArguments(command),
            $"sqlWorkflowPackageVersion={command.Plan.SqlWorkflowPackageVersion}", $"sqlQuartzPackageVersion={command.Plan.SqlQuartzPackageVersion}",
            ..CapacityArguments(command),
            $"templateFingerprint={command.Context.TemplateFingerprint}", "deployWorkload=false", "--query", "properties.outputs", "--output", "json", "--only-show-errors"];

    private IReadOnlyList<string> AcrDeploymentArguments(AzureProviderRunnerCommand command, string identityId, string principalId, string deploymentName) =>
        ["deployment", "group", "create", "--subscription", _scope.RegistrySubscriptionId, "--resource-group", _scope.RegistryResourceGroupName,
            "--name", deploymentName, "--template-file", Path.Combine(_options.TemplateRoot, "acr-pull-role.bicep"), "--parameters",
            $"registryName={_scope.RegistryName}", $"workloadIdentityId={identityId}", $"workloadPrincipalId={principalId}",
            "--query", "properties.outputs", "--output", "json", "--only-show-errors"];

    private IReadOnlyList<string> WorkloadDeploymentArguments(AzureProviderRunnerCommand command, string deploymentName, string revision, string? stable) =>
        ["deployment", "group", "create", "--subscription", _scope.SubscriptionId, "--resource-group", ResourceGroupName(command),
            "--name", deploymentName, "--template-file", Path.Combine(_options.TemplateRoot, "main.bicep"), "--parameters",
            ..TemplateIdentityArguments(command), $"location={_scope.Location}", $"imageRepository={command.Plan.ImageRepository}", $"imageDigest={command.Plan.ImageDigest}",
            $"registryName={_scope.RegistryName}", $"registrySubscriptionId={_scope.RegistrySubscriptionId}",
            $"registryResourceGroupName={_scope.RegistryResourceGroupName}", $"sqlBootstrapObjectId={_options.SqlBootstrapObjectId}",
            $"sqlBootstrapLogin={_options.SqlBootstrapLogin}", $"owner={_options.Owner}",
            $"sqlConnectionSecretName={SqlConnectionSecretName}", $"signingKeySecretName={SigningKeySecretName}",
            $"adminPasswordSecretName={AdminPasswordSecretName}", $"adminUsername={_options.RuntimeAdminUsername}",
            $"elsaVersion={command.Plan.ElsaVersion}", ..ReleaseIdentityArguments(command),
            $"sqlWorkflowPackageVersion={command.Plan.SqlWorkflowPackageVersion}", $"sqlQuartzPackageVersion={command.Plan.SqlQuartzPackageVersion}",
            ..CapacityArguments(command),
            $"templateFingerprint={command.Context.TemplateFingerprint}", "deployWorkload=true", $"workloadRevisionSuffix={revision}",
            $"stableTrafficRevisionName={stable ?? string.Empty}", "--query", "properties.outputs", "--output", "json", "--only-show-errors"];

    private void ValidateCommand(AzureProviderRunnerCommand command)
    {
        if (command.Context.WorkspaceId == Guid.Empty || command.Context.OperationId == Guid.Empty)
            throw new ArgumentException("The Azure execution context identity is required.", nameof(command));
        if (command.AttemptNumber < 1)
            throw new ArgumentException("The Azure execution attempt is required.", nameof(command));
        if (!string.Equals(command.Plan.WorkloadName, command.Context.TargetKey, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Azure plan target does not match its execution context.", nameof(command));
        if (!IsSafeWorkloadName(command.Plan.WorkloadName) ||
            !IsFingerprint(command.Plan.Fingerprint) ||
            !string.Equals(command.Plan.Fingerprint, command.Context.PlanFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.Context.TemplateFingerprint, _options.ComputeTemplateAuthorityFingerprint(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Azure plan identity is not bound to the durable execution context.", nameof(command));
        if (!string.Equals(command.Plan.Location, _scope.Location, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Azure plan location is outside the configured target scope.", nameof(command));
        var scopeFingerprint = _options.ComputeProviderScopeFingerprint(_scope);
        if (string.IsNullOrWhiteSpace(command.Context.ProviderScopeFingerprint) ||
            !string.Equals(command.Context.ProviderScopeFingerprint, scopeFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Azure operation is not bound to the configured target scope.");
        if (!string.Equals(command.Plan.Topology, AzureWorkloadPlanTranslator.SupportedTopology, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(command.Plan.Isolation, AzureWorkloadPlanTranslator.SupportedIsolation, StringComparison.OrdinalIgnoreCase) ||
            !BelongsToReleaseLine(command.Plan.ReleaseLine, command.Plan.ElsaVersion) ||
            !string.Equals(command.Plan.ImageRepository, AzureWorkloadPlanTranslator.SupportedRepository, StringComparison.Ordinal) ||
            !AzureWorkloadPlanTranslator.IsSupportedLocation(command.Plan.Location) ||
            command.Plan.ImageDigest.Length != 64 || !command.Plan.ImageDigest.All(Uri.IsHexDigit) ||
            !AzureProviderOperationValidation.IsSafePackageVersion(command.Plan.SqlWorkflowPackageVersion) ||
            !AzureProviderOperationValidation.IsSafePackageVersion(command.Plan.SqlQuartzPackageVersion) ||
            !AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(command.Plan.ReleaseManifestReference, command.Plan.ReleaseManifestDigest) ||
            !AzureProviderOperationValidation.IsSafeImmutableEvidenceReference(command.Plan.ReleaseManifestSignatureReference, command.Plan.ReleaseManifestSignatureDigest) ||
            !AzureProviderOperationValidation.IsSafeSecretReferences(command.Plan.SecretReferences))
            throw new ArgumentException("The Azure workload plan is outside the governed provider profile.", nameof(command));
        AzureProviderOperationValidation.ValidateReferences(command.Resources);
        ValidateExactPersistedReferences(command);
    }

    /// <summary>
    /// Rebinds all mutable provider authority immediately before a remote mutation. This closes
    /// the gap between admission/observation and a later command if an executable, template or
    /// scope option is replaced while a durable operation is running.
    /// </summary>
    private void EnsureMutationAuthority(AzureProviderRunnerCommand command)
    {
        ValidateCommand(command);
        if (!string.Equals(command.Context.TemplateFingerprint, _options.ComputeTemplateAuthorityFingerprint(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The checked-in Azure template authority changed while the operation was running.");
    }

    private void ValidateExactPersistedReferences(AzureProviderRunnerCommand command)
    {
        var resources = command.Resources;
        if (resources.ResourceGroupName is not null && !string.Equals(resources.ResourceGroupName, ResourceGroupName(command), StringComparison.Ordinal))
            throw new ArgumentException("The persisted resource group is outside the exact configured scope.", nameof(command));
        if (resources.WorkloadIdentityResourceId is not null)
            ValidateExactResourceId(resources.WorkloadIdentityResourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.ManagedIdentity", "userAssignedIdentities", $"{command.Plan.WorkloadName}-identity");
        if (resources.KeyVaultResourceId is not null)
            ValidateExactResourceId(resources.KeyVaultResourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.KeyVault", "vaults", $"{command.Plan.WorkloadName}-kv");
        if (resources.SqlServerResourceId is not null)
            ValidateExactResourceId(resources.SqlServerResourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.Sql", "servers", $"{command.Plan.WorkloadName}-sql");
        if (resources.ContainerAppsEnvironmentResourceId is not null)
            ValidateExactResourceId(resources.ContainerAppsEnvironmentResourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.App", "managedEnvironments", $"{command.Plan.WorkloadName}-aca");
        if (resources.WorkloadResourceId is not null)
            ValidateExactResourceId(resources.WorkloadResourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.App", "containerApps", $"{command.Plan.WorkloadName}-app");
        if (resources.FoundationDeploymentId is not null)
            ValidateExactDeploymentId(resources.FoundationDeploymentId, _scope.SubscriptionId, ResourceGroupName(command));
        if (resources.WorkloadDeploymentId is not null)
            ValidateExactDeploymentId(resources.WorkloadDeploymentId, _scope.SubscriptionId, ResourceGroupName(command));
        if (resources.RegistryResourceId is not null)
            ValidateExactResourceId(resources.RegistryResourceId, _scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName, "Microsoft.ContainerRegistry", "registries", _scope.RegistryName);
        if (resources.AcrPullDeploymentId is not null)
        {
            ValidateExactDeploymentId(resources.AcrPullDeploymentId, _scope.RegistrySubscriptionId, _scope.RegistryResourceGroupName);
            if (resources.WorkloadIdentityPrincipalId is not null)
                ValidateExactAcrDeploymentId(resources.AcrPullDeploymentId, command, resources.WorkloadIdentityPrincipalId);
        }
        if (resources.AcrPullRoleAssignmentId is not null)
            ValidateExactRoleAssignmentId(resources.AcrPullRoleAssignmentId, resources.RegistryResourceId);
    }

    private AzureProviderResourceReferences ProjectFoundation(AzureProviderRunnerCommand command, DeploymentOutputs outputs, AzureWorkloadPlan plan, string deploymentName)
    {
        var resources = new AzureProviderResourceReferences(
            ResourceGroupName: Required(outputs.String("resourceGroupName"), "resourceGroupName"),
            FoundationDeploymentId: DeploymentId(_scope.SubscriptionId, ResourceGroupName(command), deploymentName),
            WorkloadIdentityResourceId: Required(outputs.String("workloadIdentityId"), "workloadIdentityId"),
            WorkloadIdentityClientId: NormalizeGuid(Required(outputs.String("workloadIdentityClientId"), "workloadIdentityClientId"), "workloadIdentityClientId"),
            WorkloadIdentityPrincipalId: NormalizeGuid(Required(outputs.String("workloadIdentityPrincipalId"), "workloadIdentityPrincipalId"), "workloadIdentityPrincipalId"),
            KeyVaultResourceId: Required(outputs.String("keyVaultId"), "keyVaultId"),
            KeyVaultUri: Required(outputs.String("keyVaultUri"), "keyVaultUri"),
            SqlServerResourceId: Required(outputs.String("sqlServerId"), "sqlServerId"),
            SqlServerFqdn: Required(outputs.String("sqlServerFqdn"), "sqlServerFqdn"),
            ContainerAppsEnvironmentResourceId: Required(outputs.String("containerAppsEnvironmentId"), "containerAppsEnvironmentId"));
        AzureProviderOperationValidation.ValidateReferences(resources);
        ValidateExactResourceId(resources.WorkloadIdentityResourceId!, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.ManagedIdentity", "userAssignedIdentities", $"{plan.WorkloadName}-identity");
        ValidateExactResourceId(resources.KeyVaultResourceId!, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.KeyVault", "vaults", $"{plan.WorkloadName}-kv");
        ValidateExactResourceId(resources.SqlServerResourceId!, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.Sql", "servers", $"{plan.WorkloadName}-sql");
        ValidateExactResourceId(resources.ContainerAppsEnvironmentResourceId!, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.App", "managedEnvironments", $"{plan.WorkloadName}-aca");
        if (!string.Equals(resources.ResourceGroupName, ResourceGroupName(command), StringComparison.Ordinal))
            throw new ArgumentException("The foundation returned an unexpected resource group.");
        return resources;
    }

    private AzureProviderResourceReferences ProjectWorkload(AzureProviderRunnerCommand command, DeploymentOutputs outputs, AzureProviderResourceReferences foundation, AzureWorkloadPlan plan, string deploymentName, string revision, string? stable)
    {
        var resourceId = Required(outputs.String("containerAppId"), "containerAppId");
        ValidateExactResourceId(resourceId, _scope.SubscriptionId, ResourceGroupName(command), "Microsoft.App", "containerApps", $"{plan.WorkloadName}-app");
        var endpoint = Required(outputs.String("containerAppEndpoint"), "containerAppEndpoint");
        AzureProviderOperationValidation.ValidateEndpoint(endpoint);
        var resources = foundation with
        {
            WorkloadDeploymentId = DeploymentId(_scope.SubscriptionId, ResourceGroupName(command), deploymentName),
            WorkloadResourceId = resourceId,
            WorkloadRevisionName = $"{plan.WorkloadName}-app--{revision}",
            StableTrafficRevisionName = stable,
        };
        AzureProviderOperationValidation.ValidateReferences(resources);
        return resources;
    }

    private bool IsExactInventory(AzureProviderRunnerCommand command, IReadOnlyList<AzureResource> resources, string workload)
    {
        var resourceGroupId = ResourceGroupId(command);
        var resourceGroupPrefix = resourceGroupId + "/providers/";
        var roots = new[]
        {
            $"{resourceGroupId}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{workload}-identity",
            $"{resourceGroupId}/providers/Microsoft.KeyVault/vaults/{workload}-kv",
            $"{resourceGroupId}/providers/Microsoft.Sql/servers/{workload}-sql",
            $"{resourceGroupId}/providers/Microsoft.OperationalInsights/workspaces/{workload}-logs",
            $"{resourceGroupId}/providers/Microsoft.App/managedEnvironments/{workload}-aca",
            $"{resourceGroupId}/providers/Microsoft.App/containerApps/{workload}-app"
        };
        foreach (var resource in resources)
        {
            var id = resource.Id?.ToLowerInvariant();
            if (id is null || string.IsNullOrWhiteSpace(resource.Type))
                return false;
            if (resource.Type?.Equals("Microsoft.Authorization/roleAssignments", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!id.StartsWith(resourceGroupPrefix.ToLowerInvariant(), StringComparison.Ordinal) ||
                    !id.Contains($"/providers/microsoft.keyvault/vaults/{workload}-kv/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(ResourceName(resource.Id), "D", out _))
                    return false;
                continue;
            }
            if (!id.StartsWith(resourceGroupPrefix.ToLowerInvariant(), StringComparison.Ordinal) ||
                !roots.Any(root => id.Equals(root.ToLowerInvariant(), StringComparison.Ordinal) || id.StartsWith(root.ToLowerInvariant() + "/", StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private bool HasSafeVaultAssignmentsForCleanup(AzureProviderRunnerCommand command, IReadOnlyList<RoleAssignment> assignments, AzureProviderResourceReferences resources, string workload)
    {
        var vault = resources.KeyVaultResourceId ?? ResourceId(command, "Microsoft.KeyVault", "vaults", $"{workload}-kv");
        var owned = assignments.Where(x => string.Equals(x.Scope, vault, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (owned.Length > 2)
            return false;
        var users = owned.Where(x => string.Equals(RoleDefinitionId(x.RoleDefinitionId), KeyVaultSecretsUserRoleDefinitionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var officers = owned.Where(x => string.Equals(RoleDefinitionId(x.RoleDefinitionId), KeyVaultSecretsOfficerRoleDefinitionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (users.Length + officers.Length != owned.Length || users.Length > 1 || officers.Length > 1)
            return false;
        if (users.Length == 1 && (resources.WorkloadIdentityPrincipalId is null ||
            !string.Equals(users[0].PrincipalId, resources.WorkloadIdentityPrincipalId, StringComparison.OrdinalIgnoreCase)))
            return false;
        return officers.Length == 0 || string.Equals(officers[0].PrincipalId, _options.SqlBootstrapObjectId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RequireFoundation(AzureProviderResourceReferences resources) =>
        resources.ResourceGroupName is null || resources.FoundationDeploymentId is null || resources.WorkloadIdentityResourceId is null ||
        resources.WorkloadIdentityClientId is null || resources.WorkloadIdentityPrincipalId is null || resources.KeyVaultResourceId is null ||
        resources.KeyVaultUri is null || resources.SqlServerResourceId is null || resources.SqlServerFqdn is null || resources.ContainerAppsEnvironmentResourceId is null
            ? "The persisted foundation resource references are incomplete."
            : null;

    private static string? RequireRegistry(AzureProviderResourceReferences resources) =>
        RequireFoundation(resources) ?? (resources.RegistryResourceId is null || resources.AcrPullDeploymentId is null || resources.AcrPullRoleAssignmentId is null
            ? "The persisted registry resource references are incomplete."
            : null);

    /// <summary>
    /// Maps a process result to the runner outcome. Cancellation and unproven termination are always
    /// Uncertain; a plain failure is Uncertain after a mutation and Failed before one, where callers may
    /// supply a more specific policy code for the Failed case.
    /// </summary>
    private static AzureProviderRunnerResult ProcessFailure<T>(AzureProviderRunnerCommand command, AzureProviderOperationPhase phase, AzureCommandProcessResult<T> result, AzureProviderResourceReferences resources, bool mutation,
        string failedCode = "azure.step.failed", string failedMessage = "The Azure lifecycle observation failed.")
        where T : AzureCommandSafeOutput =>
        result.Status is AzureCommandProcessStatus.TerminationUncertain || result.FailureKind is AzureCommandProcessFailureKind.TerminationUncertain
            ? Uncertain(command, phase, "azure.step.termination-uncertain", "The Azure lifecycle process could not be proven terminated, so the external result requires recovery.", resources, result.FailureKind)
            : result.Status == AzureCommandProcessStatus.Cancelled || result.FailureKind == AzureCommandProcessFailureKind.Cancelled
                ? Uncertain(command, phase, "azure.step.cancelled", "The Azure lifecycle step was interrupted before its result was confirmed.", resources, result.FailureKind)
            : mutation
                ? Uncertain(command, phase, "azure.step.uncertain", "The Azure lifecycle step failed before its external result was confirmed.", resources, result.FailureKind)
                : Failed(command, phase, failedCode, failedMessage, resources, result.FailureKind);

    private static AzureProviderRunnerResult Completed(AzureProviderRunnerCommand command, AzureProviderOperationPhase phase, AzureProviderResourceReferences resources, bool noOp = false, AzureProviderHealth health = AzureProviderHealth.Unknown, string? endpoint = null, bool stableTrafficRestored = false) =>
        new(noOp ? AzureProviderRunnerOutcome.NoOp : AzureProviderRunnerOutcome.Completed, phase, resources, health, endpoint, [], noOp ? "azure.step.no-op" : "azure.step.completed", noOp ? "The Azure lifecycle step was already converged." : "The Azure lifecycle step completed.", StableTrafficRestored: stableTrafficRestored);

    private static AzureProviderRunnerResult Failed(AzureProviderRunnerCommand command, AzureProviderOperationPhase phase, string code, string message, AzureProviderResourceReferences? resources = null, AzureCommandProcessFailureKind? processFailureKind = null) =>
        new(AzureProviderRunnerOutcome.Failed, phase, resources ?? command.Resources, AzureProviderHealth.Unknown, null,
            AzureProviderSafeDiagnostics.Failure(command.Step, AzureProviderRunnerOutcome.Failed, code, processFailureKind), code, message);

    private static AzureProviderRunnerResult Uncertain(AzureProviderRunnerCommand command, AzureProviderOperationPhase phase, string code, string message, AzureProviderResourceReferences? resources = null, AzureCommandProcessFailureKind? processFailureKind = null) =>
        new(AzureProviderRunnerOutcome.Uncertain, phase, resources ?? command.Resources, AzureProviderHealth.Unknown, null,
            AzureProviderSafeDiagnostics.Failure(command.Step, AzureProviderRunnerOutcome.Uncertain, code, processFailureKind), code, message);

    private static AzureProviderOperationPhase CurrentPhase(AzureProviderRunnerStep step) => step switch
    {
        AzureProviderRunnerStep.Foundation or AzureProviderRunnerStep.AcrPull or AzureProviderRunnerStep.SeedSecrets => AzureProviderOperationPhase.FoundationSubmitted,
        AzureProviderRunnerStep.SqlBootstrap => AzureProviderOperationPhase.FoundationReady,
        AzureProviderRunnerStep.SqlFirewallCreate => AzureProviderOperationPhase.SqlFirewallReady,
        AzureProviderRunnerStep.SqlBootstrapScript => AzureProviderOperationPhase.SqlBootstrapReady,
        AzureProviderRunnerStep.SqlFirewallCleanup => AzureProviderOperationPhase.FoundationReady,
        AzureProviderRunnerStep.Workload => AzureProviderOperationPhase.WorkloadReady,
        AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
        AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
        AzureProviderRunnerStep.RestoreStableTraffic => AzureProviderOperationPhase.HealthVerified,
        AzureProviderRunnerStep.Cleanup => AzureProviderOperationPhase.CleanupVerified,
        _ => AzureProviderOperationPhase.Planned
    };

    private string ResourceGroupId(AzureProviderRunnerCommand command) => $"/subscriptions/{_scope.SubscriptionId}/resourceGroups/{ResourceGroupName(command)}";

    private string ResourceId(AzureProviderRunnerCommand command, string provider, string type, string name) =>
        $"{ResourceGroupId(command)}/providers/{provider}/{type}/{name}";

    private string RegistryResourceId() =>
        $"/subscriptions/{_scope.RegistrySubscriptionId}/resourceGroups/{_scope.RegistryResourceGroupName}/providers/Microsoft.ContainerRegistry/registries/{_scope.RegistryName}";
    private static string AppName(AzureProviderRunnerCommand command) => $"{command.Plan.WorkloadName}-app";
    private static string SqlServerName(AzureProviderRunnerCommand command) => $"{command.Plan.WorkloadName}-sql";
    private string ResourceGroupName(AzureProviderRunnerCommand command)
    {
        if (command.Assignment is null)
            return _scope.ResourceGroupName;
        var assignment = command.Assignment;
        if (assignment.Id.ToString("D") != command.Context.ProviderAssignmentId ||
            assignment.WorkspaceId != command.Context.WorkspaceId ||
            assignment.OrganizationId != command.Context.OrganizationId ||
            assignment.InstanceId != command.Context.InstanceId ||
            !string.Equals(assignment.SubscriptionId, _scope.SubscriptionId, StringComparison.Ordinal) ||
            !string.Equals(assignment.ProviderScopeFingerprint, command.Context.ProviderScopeFingerprint, StringComparison.Ordinal) ||
            !string.Equals(assignment.WorkloadName, command.Plan.WorkloadName, StringComparison.OrdinalIgnoreCase) ||
            assignment.State == AzureProviderAssignmentState.Deleted)
            throw new InvalidOperationException("The Azure provider assignment does not authorize this runner command.");
        return assignment.ResourceGroupName;
    }
    private string FoundationDeploymentName(AzureProviderRunnerCommand command) => $"{DeploymentPrefix}-{command.Plan.WorkloadName}-{command.Plan.Fingerprint[..12]}-foundation";
    private string WorkloadDeploymentName(AzureProviderRunnerCommand command) => $"{DeploymentPrefix}-{command.Plan.WorkloadName}-{command.Plan.Fingerprint[..12]}-workload";
    private string AcrDeploymentName(AzureProviderRunnerCommand command, string principalId) => $"{DeploymentPrefix}-{command.Plan.WorkloadName}-{ShortHash($"{_scope.SubscriptionId}/{ResourceGroupName(command)}/{principalId}/{_scope.RegistrySubscriptionId}/{_scope.RegistryResourceGroupName}/{_scope.RegistryName}")}-acr";
    private string DeploymentPrefix => _options.DisposableProofMode ? "elsa108" : "elsa";
    // This deterministic name is reserved for provider-owned temporary bootstrap rules;
    // cleanup is authorized only for the exact assignment resource group.
    private string TemporaryFirewallRuleName => _options.DisposableProofMode ? "elsa108-bootstrap" : "elsa-bootstrap";
    private static string ShortHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
    private static string DeploymentId(string subscription, string resourceGroup, string name) => $"/subscriptions/{subscription}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/{name}";
    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"The Azure output {name} is missing.");
    private static string? ResourceName(string? resourceId) => resourceId?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    private static string? RoleDefinitionId(string? id) => id?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    private static bool IsRevisionSuffixForPlan(string? value, string baseSuffix)
    {
        if (value is null || string.Equals(value, baseSuffix, StringComparison.OrdinalIgnoreCase))
            return value is not null;
        if (!value.StartsWith(baseSuffix + "-r", StringComparison.OrdinalIgnoreCase))
            return false;
        var ordinal = value[(baseSuffix.Length + 2)..];
        return ordinal.Length > 0 && ordinal.All(char.IsAsciiDigit) &&
               int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 1 and < 1000;
    }
    private static bool IsFingerprint(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool BelongsToReleaseLine(string releaseLine, string version) =>
        string.Equals(version, releaseLine, StringComparison.OrdinalIgnoreCase) ||
        version.StartsWith(releaseLine + ".", StringComparison.OrdinalIgnoreCase) ||
        version.StartsWith(releaseLine + "-", StringComparison.OrdinalIgnoreCase);
    private static bool IsSafeWorkloadName(string? value) => value is not null && value.Length is >= 3 and <= 16 &&
        char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    private static void ValidateExactResourceId(string id, string subscription, string group, string provider, string type, string name)
    {
        var expected = $"/subscriptions/{subscription}/resourceGroups/{group}/providers/{provider}/{type}/{name}";
        if (!string.Equals(id, expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The Azure resource identity is outside the exact configured scope.");
    }

    private static void ValidateExactDeploymentId(string id, string subscription, string group)
    {
        var prefix = $"/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.Resources/deployments/";
        var name = ResourceName(id);
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("The Azure deployment identity is outside the exact configured scope.");
    }

    private void ValidateExactAcrDeploymentId(string id, AzureProviderRunnerCommand command, string? principalId)
    {
        if (principalId is null || !string.Equals(ResourceName(id), AcrDeploymentName(command, principalId), StringComparison.Ordinal))
            throw new ArgumentException("The ACR deployment identity is not the deterministic deployment for this workload identity.");
    }

    private static string NormalizeGuid(string value, string name) =>
        Guid.TryParseExact(value, "D", out var guid)
            ? guid.ToString("D", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"The Azure output {name} is not a canonical identity.");

    private bool IsRecoveryAssignmentAuthorityValid(
        AzureProviderOperation operation,
        AzureProviderResourceAssignment assignment,
        AzureWorkloadPlan plan)
    {
        var correlatedUnknownRecovery =
            operation.Status == AzureProviderOperationStatus.RecoveryRequired &&
            assignment.State == AzureProviderAssignmentState.Unknown &&
            assignment.LastOperationId == operation.Id &&
            operation.CheckpointSequence > 0 &&
            operation.AttemptedStep is not null &&
            operation.Resources == assignment.Resources;
        if (operation.ProviderAssignmentId != assignment.Id ||
            operation.WorkspaceId != assignment.WorkspaceId ||
            operation.OrganizationId != assignment.OrganizationId ||
            operation.InstanceId != assignment.InstanceId ||
            !string.Equals(operation.TargetKey, assignment.WorkloadName, StringComparison.Ordinal) ||
            !string.Equals(plan.WorkloadName, assignment.WorkloadName, StringComparison.Ordinal) ||
            !string.Equals(plan.Location, assignment.Location, StringComparison.OrdinalIgnoreCase) ||
            assignment.State == AzureProviderAssignmentState.Deleted ||
            assignment.State == AzureProviderAssignmentState.Unknown && !correlatedUnknownRecovery ||
            !string.Equals(operation.ProviderScopeFingerprint, assignment.ProviderScopeFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(operation.ProviderScopeFingerprint, _options.ComputeProviderScopeFingerprint(_scope), StringComparison.OrdinalIgnoreCase))
            return false;

        var operationResources = operation.Resources;
        var assignmentResources = assignment.Resources;
        return string.Equals(operationResources.ResourceGroupName, assignment.ResourceGroupName, StringComparison.Ordinal) &&
               string.Equals(operationResources.WorkloadIdentityResourceId, assignmentResources.WorkloadIdentityResourceId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(operationResources.WorkloadIdentityClientId, assignmentResources.WorkloadIdentityClientId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(operationResources.WorkloadIdentityPrincipalId, assignmentResources.WorkloadIdentityPrincipalId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpectedAcrPullRoleDefinitionId(string registryId)
    {
        var slash = registryId.IndexOf("/resourceGroups/", StringComparison.OrdinalIgnoreCase);
        if (slash < 0)
            throw new ArgumentException("The registry resource identity is invalid.", nameof(registryId));
        var subscription = registryId["/subscriptions/".Length..slash];
        return $"/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{AcrPullRoleDefinitionId}";
    }

    private static string ExpectedAcrPullRoleAssignmentId(string registryId, string workloadIdentityId)
    {
        var namespaceId = Guid.Parse("11fb06fb-712d-4ddd-98c7-e71bbd588830");
        var name = string.Join('-', registryId, workloadIdentityId, AcrPullRoleDefinitionId);
        var namespaceBytes = namespaceId.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);
        var hash = SHA1.HashData([.. namespaceBytes, .. Encoding.UTF8.GetBytes(name)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var guidBytes = hash[..16];
        SwapGuidByteOrder(guidBytes);
        return $"{registryId}/providers/Microsoft.Authorization/roleAssignments/{new Guid(guidBytes):D}";
    }

    private static void SwapGuidByteOrder(byte[] bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }

    private static void ValidateExactRoleAssignmentId(string id, string? registryId)
    {
        if (registryId is null || !id.StartsWith(registryId + "/providers/Microsoft.Authorization/roleAssignments/", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(ResourceName(id), "D", out _))
            throw new ArgumentException("The Azure role assignment is outside the exact registry scope.");
    }

    private bool OwnsGroup(IReadOnlyDictionary<string, string> tags, string workload) =>
        _options.DisposableProofMode
            ? tags.TryGetValue("proof", out var proof) && proof == ProofTag &&
              tags.TryGetValue("owner", out var proofOwner) && string.Equals(proofOwner, _options.Owner, StringComparison.Ordinal) &&
              tags.TryGetValue("proof-name", out var proofName) && string.Equals(proofName, workload, StringComparison.Ordinal) &&
              tags.TryGetValue("sqlBootstrapObjectId", out var proofBootstrap) && string.Equals(proofBootstrap, _options.SqlBootstrapObjectId, StringComparison.Ordinal) &&
              tags.TryGetValue("expiry", out var expiry) && string.Equals(expiry, _options.DisposableExpiryUtc!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : tags.TryGetValue("managed-by", out var managedBy) && managedBy == ManagedByTag &&
              tags.TryGetValue("owner", out var owner) && string.Equals(owner, _options.Owner, StringComparison.Ordinal) &&
              tags.TryGetValue("workload-name", out var name) && string.Equals(name, workload, StringComparison.Ordinal) &&
              tags.TryGetValue("sqlBootstrapObjectId", out var bootstrap) && string.Equals(bootstrap, _options.SqlBootstrapObjectId, StringComparison.Ordinal);

    private string[] ResourceGroupTags(string workload) => _options.DisposableProofMode
        ? [$"proof={ProofTag}", $"owner={_options.Owner}", $"proof-name={workload}",
            $"expiry={_options.DisposableExpiryUtc!.Value:yyyy-MM-dd}", $"sqlBootstrapObjectId={_options.SqlBootstrapObjectId}"]
        : [$"managed-by={ManagedByTag}", $"owner={_options.Owner}", $"workload-name={workload}",
            $"sqlBootstrapObjectId={_options.SqlBootstrapObjectId}"];

    private string[] TemplateIdentityArguments(AzureProviderRunnerCommand command) => _options.DisposableProofMode
        ? [$"proofName={command.Plan.WorkloadName}", $"expiryUtc={_options.DisposableExpiryUtc!.Value:yyyy-MM-dd}"]
        : [$"workloadName={command.Plan.WorkloadName}"];

    private string[] ReleaseIdentityArguments(AzureProviderRunnerCommand command) => _options.DisposableProofMode
        ? []
        : [$"releaseLine={command.Plan.ReleaseLine}", $"releaseFeedServiceIndex={_options.NormalizeReleaseFeedServiceIndex()}"];

    /// <summary>
    /// The production template sizes the workload from the plan alone and has no capacity
    /// defaults. A plan without an exact Container Apps mapping fails before any Azure call.
    /// The disposable-proof template keeps its own cost-boxed sizing and takes no capacity.
    /// </summary>
    private AzureProviderRunnerResult? CapacityFailure(AzureProviderRunnerCommand command, AzureProviderOperationPhase phase) =>
        _options.DisposableProofMode || AzureContainerAppsCapacity.Map(command.Plan.Capacity) is not null
            ? null
            : command.Plan.Capacity is null
                ? Failed(command, phase, "azure.capacity.required", "The plan carries no workload capacity for the production template.")
                : Failed(command, phase, "azure.capacity.unsupported", "The plan capacity has no exact Azure Container Apps consumption mapping.");

    private string[] CapacityArguments(AzureProviderRunnerCommand command)
    {
        if (_options.DisposableProofMode)
            return [];
        var capacity = command.Plan.Capacity;
        var size = AzureContainerAppsCapacity.Map(capacity)
            ?? throw new InvalidOperationException("The plan capacity has no exact Azure Container Apps consumption mapping.");
        return
        [
            $"workloadMinReplicas={capacity!.MinReplicas.ToString(CultureInfo.InvariantCulture)}",
            $"workloadMaxReplicas={capacity.MaxReplicas.ToString(CultureInfo.InvariantCulture)}",
            $"workloadCpu={size.Cpu}",
            $"workloadMemory={size.Memory}"
        ];
    }

    private string[] SqlAuthenticationArguments() => _options.DisposableProofMode
        ? ["--authentication-method", "ActiveDirectoryDefault"]
        : ["--authentication-method", "ActiveDirectoryManagedIdentity", "-U", _options.AzureCliClientId!];

    private static SafeValue<bool> ParseBooleanAsync(ReadOnlyMemory<char> output) =>
        bool.TryParse(output.ToString().Trim(), out var value) ? new SafeValue<bool>(value) : throw new FormatException();
    private static SafeValue<string> ParseStringAsync(ReadOnlyMemory<char> output) => new(output.ToString().Trim());

    private static SafeValue<T> ParseJson<T>(ReadOnlyMemory<char> output) => new(JsonSerializer.Deserialize<T>(output.Span, JsonOptions) ?? throw new JsonException());
    private static SafeValue<DeploymentOutputs> ParseDeploymentOutputsAsync(ReadOnlyMemory<char> output) => ParseJson<DeploymentOutputs>(output);
    private static SafeValue<IReadOnlyDictionary<string, string>> ParseTagsAsync(ReadOnlyMemory<char> output) => new(new ReadOnlyDictionary<string, string>(ParseJson<Dictionary<string, string>>(output).Value));
    private static SafeValue<IReadOnlyList<RoleAssignment>> ParseRoleAssignmentsAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<RoleAssignment>>(output).Value);
    private static SafeValue<IReadOnlyList<AzureResource>> ParseResourcesAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<AzureResource>>(output).Value);
    private static SafeValue<IReadOnlyList<FirewallRule>> ParseFirewallRulesAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<FirewallRule>>(output).Value);
    private static SafeValue<IReadOnlyList<DeploymentRecord>> ParseDeploymentsAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<DeploymentRecord>>(output).Value);
    private static SafeValue<IReadOnlyList<DeletedVault>> ParseDeletedVaultsAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<DeletedVault>>(output).Value);
    private static SafeValue<IReadOnlyList<AdminRecord>> ParseAdminsAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<AdminRecord>>(output).Value);
    private static SafeValue<IReadOnlyList<TrafficEntry>> ParseTrafficAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<TrafficEntry>>(output).Value);
    private static SafeValue<RevisionState> ParseRevisionStateAsync(ReadOnlyMemory<char> output) => ParseJson<RevisionState>(output);
    private static SafeValue<IReadOnlyList<string>> ParseStringArrayAsync(ReadOnlyMemory<char> output) => new(ParseJson<List<string>>(output).Value);
    private static SafeValue<IReadOnlyList<AzureSecretSeedMetadata?>> ParseSecretSeedMetadataCollectionAsync(ReadOnlyMemory<char> output) =>
        new(ParseJson<List<AzureSecretSeedMetadata?>>(output).Value);
    private static SafeValue<int> ParseIntegerAsync(ReadOnlyMemory<char> output) =>
        int.TryParse(output.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? new SafeValue<int>(value) : throw new FormatException();
    private static SafeValue<SqlBootstrapPostconditionState> ParseSqlBootstrapPostconditionAsync(ReadOnlyMemory<char> output) =>
        output.ToString().Trim() switch
        {
            "complete" => new(SqlBootstrapPostconditionState.Complete),
            "incomplete" => new(SqlBootstrapPostconditionState.Incomplete),
            "conflict" => new(SqlBootstrapPostconditionState.Conflict),
            _ => throw new FormatException()
        };
    private static bool AreWellFormedFirewallRules(IReadOnlyList<FirewallRule> rules) =>
        rules.All(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.Name) &&
            System.Net.IPAddress.TryParse(rule.StartIpAddress, out var start) &&
            start.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            System.Net.IPAddress.TryParse(rule.EndIpAddress, out var end) &&
            end.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    private static bool IsGeneratedProviderOwnedSecret(string key, string reference) =>
        AzureManagedSecretReferences.IsProviderOwned(key, reference) &&
        !AzureManagedSecretReferences.IsSqlConnection(key, reference);

    private static string[] OwnedSecretMetadataArguments(AzureProviderRunnerCommand command, string secretName) =>
    [
        $"managed-by={ManagedByTag}",
        $"provider-assignment={command.Context.ProviderAssignmentId}",
        $"instance={command.Context.InstanceId:D}",
        $"secret-slot={secretName}",
        "generation=provider-v1"
    ];

    private static bool IsOwnedSecretMetadata(
        AzureProviderRunnerCommand command,
        string secretName,
        AzureSecretSeedMetadata? metadata) =>
        metadata is not null &&
        string.Equals(metadata.ManagedBy, ManagedByTag, StringComparison.Ordinal) &&
        string.Equals(metadata.ProviderAssignment, command.Context.ProviderAssignmentId, StringComparison.Ordinal) &&
        string.Equals(metadata.Instance, command.Context.InstanceId.ToString("D"), StringComparison.Ordinal) &&
        string.Equals(metadata.SecretSlot, secretName, StringComparison.Ordinal) &&
        string.Equals(metadata.Generation, "provider-v1", StringComparison.Ordinal);

    private static async Task<(string Directory, string File)> WriteTransientSecretFileAsync(AzureSecretLease lease, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("elsa-azure-").FullName;
        var file = Path.Combine(directory, "value");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.SequentialScan
            });
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(lease.Value, cancellationToken);
            await writer.FlushAsync(cancellationToken);
            return (directory, file);
        }
        catch
        {
            DeleteTransientSecretFile(directory, file);
            throw;
        }
    }

    private async Task<(string Directory, string File)> WriteSqlBootstrapFileAsync(string clientId, string workload, CancellationToken cancellationToken)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(_options.TemplateRoot, "sql-bootstrap.sql"), cancellationToken);
        source = source.Replace("__WORKLOAD_IDENTITY_NAME__", $"{workload}-identity", StringComparison.Ordinal)
            .Replace("__WORKLOAD_IDENTITY_CLIENT_ID__", clientId, StringComparison.Ordinal);
        var directory = Directory.CreateTempSubdirectory("elsa-azure-").FullName;
        var file = Path.Combine(directory, "sql-bootstrap.sql");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(file, source, new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return (directory, file);
        }
        catch
        {
            DeleteTransientSecretFile(directory, file);
            throw;
        }
    }

    private static void DeleteTransientSecretFile(string directory, string file)
    {
        // Cleanup failure is not best-effort: a secret-bearing file that cannot be proven absent
        // makes the provider outcome uncertain and must reach durable recovery via RunAsync.
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
            File.Delete(file);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: false);
    }

    private sealed class DeploymentOutputs : Dictionary<string, OutputValue?>
    {
        public string? String(string name)
        {
            if (!TryGetValue(name, out var output) || output?.Value is not JsonElement value ||
                value.ValueKind != JsonValueKind.String)
                return null;

            return value.GetString();
        }
    }

    private sealed class OutputValue
    {
        [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    }

    private sealed class RoleAssignment
    {
        public string? Id { get; set; }
        public string? Scope { get; set; }
        public string? PrincipalId { get; set; }
        public string? RoleDefinitionId { get; set; }
    }

    private enum AcrRoleDiscoveryStatus { Absent, Exact, Ambiguous, Uncertain }
    private sealed record AcrRoleDiscovery(AcrRoleDiscoveryStatus Status, string? AssignmentId);

    private sealed class AzureResource
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
    }

    private sealed class FirewallRule
    {
        public string? Name { get; set; }
        public string? StartIpAddress { get; set; }
        public string? EndIpAddress { get; set; }
    }
    private enum SqlFirewallObservationState
    {
        Absent,
        ExactPresent,
        Ambiguous,
        Uncertain
    }

    private enum SqlBootstrapPostconditionState
    {
        Complete,
        Incomplete,
        Conflict,
        Uncertain
    }

    private sealed class DeploymentRecord { public string? Name { get; set; } }
    private sealed class DeletedVault
    {
        public string? Name { get; set; }
        public string? Location { get; set; }
        public string? VaultId { get; set; }
        [JsonPropertyName("properties")] public DeletedVaultProperties? Properties { get; set; }
        [JsonIgnore] public string? EffectiveLocation => Properties?.Location ?? Location;
        [JsonIgnore] public string? EffectiveVaultId => Properties?.VaultId ?? VaultId;
    }
    private sealed class DeletedVaultProperties { public string? Location { get; set; } public string? VaultId { get; set; } }
    private sealed class AdminRecord { public string? Login { get; set; } public string? Sid { get; set; } }
    private sealed class TrafficEntry { public string? RevisionName { get; set; } public int Weight { get; set; } }
    private sealed class RevisionState { public bool Active { get; set; } public string? Health { get; set; } public string? Fqdn { get; set; } }
    private sealed class AzureSecretSeedMetadata
    {
        public string? ManagedBy { get; set; }
        [JsonPropertyName("assignmentId")] public string? ProviderAssignment { get; set; }
        [JsonPropertyName("instanceId")] public string? Instance { get; set; }
        public string? SecretSlot { get; set; }
        public string? Generation { get; set; }
    }
    private sealed class SafeValue<T>(T value) : AzureCommandSafeOutput
    {
        public T Value { get; } = value;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyReferences = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}

/// <summary>Compatibility name for hosts that refer to the adapter as the Azure provider runner.</summary>
public sealed class AzureProviderRunner : IAzureProviderRunner, IAzureProviderRecoveryObserver
{
    private readonly AzureBicepProviderRunner _inner;

    public AzureProviderRunner(AzureProviderRunnerOptions options, AzureProviderTargetScope scope, IAzureSecretResolver? secretResolver = null) =>
        _inner = new AzureBicepProviderRunner(options, scope, secretResolver);

    internal AzureProviderRunner(AzureProviderRunnerOptions options, AzureProviderTargetScope scope, IAzureCommandProcess process, IAzureSecretResolver? secretResolver = null) =>
        _inner = new AzureBicepProviderRunner(options, scope, process, secretResolver);

    public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default) =>
        _inner.RunAsync(command, cancellationToken);

    public Task<AzureProviderRecoveryObservation> ObserveAsync(AzureProviderRecoveryRequest request, CancellationToken cancellationToken = default) =>
        _inner.ObserveAsync(request, cancellationToken);
}
