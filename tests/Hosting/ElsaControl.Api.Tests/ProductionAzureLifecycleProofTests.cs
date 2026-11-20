using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Opt-in live proof for the production Azure composition. The default test run must never
/// contact Azure: this test has no fallback or mock path when its gate is absent.
/// </summary>
public sealed partial class ProductionAzureLifecycleProofTests(ITestOutputHelper output)
{
    private const string Gate = "ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF";
    private const string ConfigurationPath = "ELSA_CONTROL_LIVE_AZURE_LIFECYCLE_PROOF_CONFIG";
    private const string DefaultActorIssuer = "https://elsa-control-live-proof.example.test";
    private const int MinimumTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 7_200;
    private const int MinimumPollSeconds = 1;
    private const int MaximumPollSeconds = 30;
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Catalog_projection_deserializes_camel_case()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": "2.0.0",
              "manifestReference": "oci://example/release-manifest@sha256:abc",
              "manifestDigest": "sha256:manifest",
              "payloadDigest": "sha256:payload",
              "signatureEvidenceReference": "oci://example/signature@sha256:def",
              "signatureEvidenceDigest": "sha256:signature",
              "registryClass": "paid",
              "distribution": {
                "id": "valence-runtime",
                "generation": "producer-2.0.0",
                "releaseLine": "3.8",
                "releaseVersion": "3.8.0-preview.5413",
                "channel": "preview",
                "producerLifecycle": "preview",
                "edition": "commercial",
                "sourceRepository": "https://github.com/example/runtime",
                "sourceCommit": "abc123",
                "sourceRunId": "run-1"
              },
              "topology": {
                "id": "combined",
                "packageManifestSchema": "producer-2.0.0",
                "runtimeKinds": [],
                "capabilities": [],
                "componentVersions": [],
                "components": [],
                "evidence": []
              },
              "catalogLifecycle": "supported",
              "admittedAt": "2026-09-05T00:00:00Z"
            }
            """));

        var entry = await JsonSerializer.DeserializeAsync<GovernedReleaseCatalogEntry>(stream, CatalogJsonOptions);

        Assert.NotNull(entry);
        Assert.Equal("valence-runtime", entry.Distribution.Id);
        Assert.Equal("combined", entry.Topology.Id);
    }

    [Fact]
    public void Failure_evidence_excludes_exception_payload()
    {
        var baseException = new InvalidOperationException("message-secret");
        baseException.Data["secret"] = "data-secret";
        var exception = new Exception("outer-secret", baseException);

        var json = JsonSerializer.Serialize(ExceptionEvidence(exception));

        Assert.DoesNotContain("message-secret", json);
        Assert.DoesNotContain("outer-secret", json);
        Assert.DoesNotContain("data-secret", json);
        Assert.Contains(nameof(InvalidOperationException), json);
    }

    [Fact]
    public void Production_packaging_guard_requires_early_host_inputs()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsa-control-live-proof-content"));
        var stagedConfiguration = Path.Combine(contentRoot, "appsettings.Production.json");

        Assert.True(HasProductionPackagingContract("Production", null, contentRoot, stagedConfiguration));
        Assert.True(HasProductionPackagingContract("production", "Production", contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Development", null, contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", "Development", contentRoot, stagedConfiguration));
        Assert.False(HasProductionPackagingContract("Production", string.Empty, contentRoot, stagedConfiguration));
    }

    [Fact]
    public void Production_packaging_guard_rejects_relative_or_unstaged_configuration()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsa-control-live-proof-content"));

        Assert.False(HasProductionPackagingContract(
            "Production",
            null,
            "elsa-control-live-proof-content",
            Path.Combine(contentRoot, "appsettings.Production.json")));
        Assert.False(HasProductionPackagingContract(
            "Production",
            null,
            contentRoot,
            Path.Combine(contentRoot, "live-proof.json")));
        Assert.False(HasProductionPackagingContract(
            "Production",
            null,
            contentRoot,
            Path.Combine(contentRoot, "staged-appsettings.Production.json")));
    }

    [Fact]
    public void Cleanup_scope_requires_correlated_local_or_provider_proof()
    {
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

        Assert.Equal(
            CleanupScope.Local,
            ClassifyCleanupScope(
                instanceDeleted: true,
                operationSucceeded: true,
                assignmentId: null,
                localDeletionAuditCorrelated: true,
                providerCleanupVerified: false));
        Assert.Equal(
            CleanupScope.Provider,
            ClassifyCleanupScope(
                instanceDeleted: true,
                operationSucceeded: true,
                assignmentId,
                localDeletionAuditCorrelated: false,
                providerCleanupVerified: true));
        Assert.Equal(
            CleanupScope.None,
            ClassifyCleanupScope(
                instanceDeleted: true,
                operationSucceeded: true,
                assignmentId: null,
                localDeletionAuditCorrelated: false,
                providerCleanupVerified: false));
        Assert.Equal(
            CleanupScope.None,
            ClassifyCleanupScope(
                instanceDeleted: false,
                operationSucceeded: true,
                assignmentId: null,
                localDeletionAuditCorrelated: true,
                providerCleanupVerified: false));
        Assert.Equal(
            CleanupScope.None,
            ClassifyCleanupScope(
                instanceDeleted: true,
                operationSucceeded: false,
                assignmentId,
                localDeletionAuditCorrelated: false,
                providerCleanupVerified: true));
    }

    [ProductionAzureFact]
    public async Task Production_composition_applies_reconciles_reloads_and_deletes_one_instance()
    {
        var inputs = LoadInputs();
        ProductionAzureLifecycleProofApplication? application = null;
        ProofState? state = null;
        var cleanupAttempted = false;
        var cleanupSucceeded = false;
        var cleanupScope = CleanupScope.None;
        var stage = "startup";
        using var runTimeout = new CancellationTokenSource(inputs.Timeout);
        var cancellationToken = runTimeout.Token;

        try
        {
            application = StartApplication(cancellationToken);
            stage = "seed";
            state = await SeedAsync(application.Services, inputs, cancellationToken);

            stage = "create";
            var created = await CreateAsync(application.Services, inputs, state, cancellationToken);
            state = state with { CreateOperationId = created.Operation.Id };

            stage = "apply";
            state = await WaitForReadyAsync(application.Services, inputs, state, created.Operation.Id, cancellationToken);

            var previousApplication = application ?? throw new ProofFailureException();
            application = null;
            await previousApplication.DisposeAsync();

            stage = "reload";
            application = StartApplication(cancellationToken);
            state = await ReconcileAsync(application.Services, inputs, state, cancellationToken);

            stage = "reconcile-after-reload";
            state = await WaitForReadyAsync(application.Services, inputs, state, state.ReconcileOperationId!.Value, cancellationToken);
            Assert.NotNull(state.AssignmentId);
            Assert.Equal(state.InitialAssignmentId, state.AssignmentId);

            stage = "delete";
            var deleted = await DeleteAsync(application.Services, inputs, state, cancellationToken);
            state = state with { DeleteOperationId = deleted.Operation.Id };

            stage = "cleanup";
            cleanupAttempted = true;
            cleanupScope = await WaitForDeletedAsync(application.Services, inputs, state, deleted.Operation.Id, cancellationToken);
            if (cleanupScope != CleanupScope.Provider)
                throw new ProofFailureException();
            cleanupSucceeded = true;
            await WriteEvidenceAsync(inputs, state, "succeeded", cleanupAttempted, cleanupSucceeded, cleanupScope, stage, null);
        }
        catch (Exception exception)
        {
            if (state is not null)
            {
                cleanupAttempted = true;
                var cleanup = await TryCleanupAsync(application, inputs, state);
                state = cleanup.State;
                cleanupSucceeded = cleanup.Succeeded;
                cleanupScope = cleanup.Scope;
            }

            await WriteEvidenceAsync(inputs, state, "failed", cleanupAttempted, cleanupSucceeded, cleanupScope, stage, exception);
            throw new XunitException(
                "The opt-in production Azure lifecycle proof failed. Safe proof identifiers were preserved for investigation.");
        }
        finally
        {
            if (application is not null)
            {
                try
                {
                    await application.DisposeAsync();
                }
                catch (Exception)
                {
                    // The product cleanup result is recorded separately. Do not surface host
                    // disposal diagnostics, which may contain provider or secret details.
                }
            }
        }
    }

    private static bool IsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable(Gate), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable(Gate), "true", StringComparison.OrdinalIgnoreCase);

    private static LiveProofInputs LoadInputs()
    {
        var aspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotNetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var contentRoot = Environment.GetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_ELSACONTROL_API");
        var configPath = Environment.GetEnvironmentVariable(ConfigurationPath);
        if (!HasProductionPackagingContract(aspNetCoreEnvironment, dotNetEnvironment, contentRoot, configPath))
            throw new ProofConfigurationException();

        configPath = Path.GetFullPath(configPath!);
        if (!File.Exists(configPath))
            throw new ProofConfigurationException();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        var baseDirectory = Path.GetDirectoryName(configPath)!;
        var fixturePath = configuration["LiveProof:CatalogEntryPath"];
        if (string.IsNullOrWhiteSpace(fixturePath))
            throw new ProofConfigurationException();
        fixturePath = Path.GetFullPath(fixturePath, baseDirectory);
        if (!File.Exists(fixturePath))
            throw new ProofConfigurationException();

        var instanceId = ParseGuid(configuration["LiveProof:InstanceId"])
            ?? throw new ProofConfigurationException();
        ValidateIsolatedSqliteDatabase(configuration, baseDirectory);
        var actorSubject = SafeToken(configuration["LiveProof:ActorSubject"]) ?? $"live-proof-{instanceId:N}";
        var slug = SafeToken(configuration["LiveProof:InstanceSlug"]) ?? $"live-proof-{instanceId:N}";
        var name = configuration["LiveProof:InstanceName"]?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "Live Azure lifecycle proof";

        var actorIssuer = configuration["LiveProof:ActorIssuer"]?.Trim();
        if (string.IsNullOrWhiteSpace(actorIssuer))
            actorIssuer = DefaultActorIssuer;
        var actorEmail = configuration["LiveProof:ActorEmail"]?.Trim();
        if (string.IsNullOrWhiteSpace(actorEmail))
            actorEmail = $"{actorSubject}@example.test";
        var evidenceDirectory = configuration["LiveProof:EvidenceDirectory"]?.Trim();
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
            evidenceDirectory = Path.Combine(Path.GetTempPath(), "elsa-control-live-proof");
        evidenceDirectory = Path.GetFullPath(evidenceDirectory);

        var timeout = BoundedSeconds(configuration["LiveProof:TimeoutSeconds"], 1_800, MinimumTimeoutSeconds, MaximumTimeoutSeconds);
        var poll = BoundedSeconds(configuration["LiveProof:PollSeconds"], 5, MinimumPollSeconds, MaximumPollSeconds);
        return new(
            configPath,
            fixturePath,
            instanceId,
            actorIssuer,
            actorSubject,
            actorEmail,
            name,
            slug,
            TimeSpan.FromSeconds(timeout),
            TimeSpan.FromSeconds(poll),
            evidenceDirectory);
    }

    private static ProductionAzureLifecycleProofApplication StartApplication(CancellationToken cancellationToken)
    {
        var application = new ProductionAzureLifecycleProofApplication();
        try
        {
            using var client = application.CreateClient();
            using var response = client.GetAsync("/health", cancellationToken).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new ProofFailureException();
            return application;
        }
        catch
        {
            application.Dispose();
            throw;
        }
    }

    private static async Task<ProofState> SeedAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var catalogEntry = await LoadCatalogEntryAsync(inputs.CatalogEntryPath, cancellationToken);
        var catalog = scope.ServiceProvider.GetRequiredService<IGovernedReleaseCatalogStore>();
        var stored = await catalog.StoreAsync([catalogEntry], cancellationToken);
        if (stored.Status == GovernedReleaseCatalogWriteStatus.Conflict)
            throw new ProofFailureException();

        var accounts = scope.ServiceProvider.GetRequiredService<AccountWorkspaceService>();
        var account = await accounts.GetOrCreateAsync(
            new TrustedWorkspaceIdentity(inputs.ActorIssuer, inputs.ActorSubject, inputs.ActorSubject, inputs.ActorEmail),
            cancellationToken);
        var workspace = account.Workspaces.SingleOrDefault();
        if (workspace is null || workspace.OrganizationId == Guid.Empty)
            throw new ProofFailureException();

        var entitlement = await db.OrganizationEntitlementSnapshots
            .Where(x => x.OrganizationId == workspace.OrganizationId)
            .OrderByDescending(x => x.SyncedAt)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (entitlement is null)
        {
            entitlement = new OrganizationEntitlementSnapshot
            {
                OrganizationId = workspace.OrganizationId
            };
            db.OrganizationEntitlementSnapshots.Add(entitlement);
        }

        entitlement.ManagedHostingEnabled = true;
        entitlement.DeploymentTargetsEnabled = true;
        entitlement.MaxInstances = int.MaxValue;
        entitlement.MaxSources = Math.Max(entitlement.MaxSources, 5);
        entitlement.MaxWorkspaces = Math.Max(entitlement.MaxWorkspaces, 1);
        entitlement.SubscriptionState = OrganizationSubscriptionState.Active;
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Use the application's actual permission composition and persistence, rather than
        // trusted headers or a test-only permission store.
        var permissions = scope.ServiceProvider.GetRequiredService<WorkspacePermissionService>();
        await permissions.BootstrapOwnerPermissionsAsync(workspace.Id, account.Account.Id, cancellationToken);

        return new(
            workspace.OrganizationId,
            workspace.Id,
            account.Account.Id,
            inputs.InstanceId,
            catalogEntry,
            InitialAssignmentId: null,
            CreateOperationId: null,
            ReconcileOperationId: null,
            DeleteOperationId: null,
            AssignmentId: null,
            ProviderOperationId: null);
    }

    private static async Task<ElsaInstanceLifecycleAcceptance> CreateAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        ProofState state,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
        return await lifecycle.CreateAsync(new ElsaInstanceCreateRequest(
            state.OrganizationId,
            state.WorkspaceId,
            inputs.Name,
            inputs.Slug,
            Intent(state.CatalogEntry),
            $"live-proof-create-{state.InstanceId:N}",
            state.InstanceId,
            state.AccountId), cancellationToken);
    }

    private static async Task<ProofState> ReconcileAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        ProofState state,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IElsaInstanceLifecycleStore>();
        var instance = await store.GetInstanceAsync(state.WorkspaceId, state.InstanceId, cancellationToken)
            ?? throw new ProofFailureException();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
        var accepted = await lifecycle.ReconcileAsync(new ElsaInstanceLifecycleRequest(
            state.WorkspaceId,
            state.InstanceId,
            instance.Version,
            $"live-proof-reconcile-{state.InstanceId:N}",
            ActorAccountId: state.AccountId), cancellationToken);
        return state with { ReconcileOperationId = accepted.Operation.Id };
    }

    private static async Task<ElsaInstanceLifecycleAcceptance> DeleteAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        ProofState state,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IElsaInstanceLifecycleStore>();
        var instance = await store.GetInstanceAsync(state.WorkspaceId, state.InstanceId, cancellationToken)
            ?? throw new ProofFailureException();
        var confirmations = scope.ServiceProvider.GetRequiredService<ConfirmationService>();
        var confirmation = await confirmations.CreateConfirmationAsync(
            state.WorkspaceId,
            new CreateActionConfirmationRequest(
                ConfirmationActionType.DeleteManagedInstance,
                state.InstanceId.ToString("D"),
                state.AccountId),
            cancellationToken);
        var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
        return await lifecycle.DeleteAsync(new ElsaInstanceLifecycleRequest(
            state.WorkspaceId,
            state.InstanceId,
            instance.Version,
            $"live-proof-delete-{state.InstanceId:N}",
            DeleteConfirmationId: confirmation.Id,
            ActorAccountId: state.AccountId), cancellationToken);
    }

    private static async Task<ProofState> WaitForReadyAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        ProofState state,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(inputs.Timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var lifecycleStore = scope.ServiceProvider.GetRequiredService<IElsaInstanceLifecycleStore>();
            var instance = await lifecycleStore.GetInstanceAsync(state.WorkspaceId, state.InstanceId, cancellationToken);
            var apiStore = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceApiStore>();
            var operation = await apiStore.GetOperationAsync(state.WorkspaceId, state.InstanceId, operationId, cancellationToken);
            if (instance is not null && operation is not null)
            {
                if (operation.InstanceId != state.InstanceId ||
                    operation.State is ElsaInstanceOperationState.Failed or ElsaInstanceOperationState.Cancelled)
                    throw new ProofFailureException();

                if (await HasCorrelatedProviderFailureAsync(
                        scope.ServiceProvider,
                        state,
                        instance,
                        operation,
                        AzureProviderOperationAction.Reconcile,
                        cancellationToken))
                    throw new ProofFailureException();

                if (operation.State == ElsaInstanceOperationState.Succeeded &&
                    instance.ObservedLifecycle == ElsaObservedLifecycle.Ready &&
                    instance.Health == ElsaInstanceHealth.Healthy)
                {
                    var assignmentReference = instance.PlacementAssignmentReference?.AssignmentId;
                    if (!Guid.TryParseExact(assignmentReference, "D", out var assignmentId))
                        throw new ProofFailureException();
                    var assignmentStore = scope.ServiceProvider.GetRequiredService<IAzureProviderResourceAssignmentStore>();
                    var assignment = await assignmentStore.GetAsync(state.WorkspaceId, assignmentId, cancellationToken);
                    if (assignment is null || assignment.State != AzureProviderAssignmentState.Active ||
                        assignment.NamingVersion != 1 || assignment.InstanceId != state.InstanceId ||
                        assignment.LastOperationId is null)
                        throw new ProofFailureException();
                    var providerOperations = scope.ServiceProvider.GetRequiredService<IAzureProviderOperationStore>();
                    var providerOperation = await providerOperations.GetAsync(
                        state.WorkspaceId, assignment.LastOperationId.Value, cancellationToken);
                    if (providerOperation is null || providerOperation.Status != AzureProviderOperationStatus.Succeeded ||
                        providerOperation.WorkspaceId != state.WorkspaceId ||
                        providerOperation.InstanceId != state.InstanceId ||
                        providerOperation.ProviderAssignmentId != assignmentId)
                        throw new ProofFailureException();

                    return state with
                    {
                        InitialAssignmentId = state.InitialAssignmentId ?? assignmentId,
                        AssignmentId = assignmentId,
                        ProviderOperationId = assignment.LastOperationId
                    };
                }
            }

            await Task.Delay(inputs.PollInterval, cancellationToken);
        }

        throw new ProofFailureException();
    }

    private static async Task<CleanupScope> WaitForDeletedAsync(
        IServiceProvider services,
        LiveProofInputs inputs,
        ProofState state,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(inputs.Timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            var lifecycleStore = scope.ServiceProvider.GetRequiredService<IElsaInstanceLifecycleStore>();
            var instance = await lifecycleStore.GetInstanceAsync(state.WorkspaceId, state.InstanceId, cancellationToken);
            var apiStore = scope.ServiceProvider.GetRequiredService<IManagedElsaInstanceApiStore>();
            var operation = await apiStore.GetOperationAsync(state.WorkspaceId, state.InstanceId, operationId, cancellationToken);
            if (instance is not null && operation is not null)
            {
                if (operation.InstanceId != state.InstanceId ||
                    operation.State is ElsaInstanceOperationState.Failed or ElsaInstanceOperationState.Cancelled)
                    throw new ProofFailureException();

                if (await HasCorrelatedProviderFailureAsync(
                        scope.ServiceProvider,
                        state,
                        instance,
                        operation,
                        AzureProviderOperationAction.Delete,
                        cancellationToken))
                    throw new ProofFailureException();

                if (operation.State == ElsaInstanceOperationState.Succeeded &&
                    instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
                {
                    var providerCleanupVerified = false;
                    if (state.AssignmentId is { } assignmentId)
                    {
                        var assignmentStore = scope.ServiceProvider.GetRequiredService<IAzureProviderResourceAssignmentStore>();
                        var assignment = await assignmentStore.GetAsync(state.WorkspaceId, assignmentId, cancellationToken);
                        providerCleanupVerified = assignment is not null &&
                            assignment.InstanceId == state.InstanceId &&
                            assignment.State == AzureProviderAssignmentState.Deleted &&
                            assignment.Resources == new AzureProviderResourceReferences(assignment.ResourceGroupName);
                    }

                    var localDeletionAuditCorrelated = state.AssignmentId is null &&
                        await HasCorrelatedLocalDeletionAuditAsync(
                            apiStore, state.WorkspaceId, state.InstanceId, operationId, cancellationToken);
                    var cleanupScope = ClassifyCleanupScope(
                        instanceDeleted: instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted,
                        operationSucceeded: operation.State == ElsaInstanceOperationState.Succeeded &&
                            operation.Action == ElsaInstanceOperationAction.Delete,
                        state.AssignmentId,
                        localDeletionAuditCorrelated,
                        providerCleanupVerified);
                    if (cleanupScope == CleanupScope.None)
                        throw new ProofFailureException();
                    return cleanupScope;
                }
            }

            await Task.Delay(inputs.PollInterval, cancellationToken);
        }

        throw new ProofFailureException();
    }

    private static async Task<bool> HasCorrelatedLocalDeletionAuditAsync(
        IManagedElsaInstanceApiStore apiStore,
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var audit = await apiStore.ListAuditAsync(workspaceId, instanceId, cancellationToken);
        return audit.Any(x =>
            x.EventType == "lifecycle.deleted" &&
            x.OperationId == operationId &&
            x.DiagnosticCode == "deletion.local.absent");
    }

    private static CleanupScope ClassifyCleanupScope(
        bool instanceDeleted,
        bool operationSucceeded,
        Guid? assignmentId,
        bool localDeletionAuditCorrelated,
        bool providerCleanupVerified) =>
        !instanceDeleted || !operationSucceeded
            ? CleanupScope.None
            : assignmentId is null
                ? localDeletionAuditCorrelated ? CleanupScope.Local : CleanupScope.None
                : providerCleanupVerified ? CleanupScope.Provider : CleanupScope.None;

    private static async Task<CleanupResult> TryCleanupAsync(
        ProductionAzureLifecycleProofApplication? existingApplication,
        LiveProofInputs inputs,
        ProofState state)
    {
        ProductionAzureLifecycleProofApplication? application = existingApplication;
        var ownsApplication = false;
        try
        {
            if (application is null)
            {
                using var cleanupTimeout = new CancellationTokenSource(inputs.Timeout);
                application = StartApplication(cleanupTimeout.Token);
                ownsApplication = true;
            }

            using var timeout = new CancellationTokenSource(inputs.Timeout);
            var cancellationToken = timeout.Token;

            await using var scope = application.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IElsaInstanceLifecycleStore>();
            var instance = await store.GetInstanceAsync(state.WorkspaceId, state.InstanceId, cancellationToken);
            if (instance?.PlacementAssignmentReference?.AssignmentId is { } assignmentReference &&
                Guid.TryParseExact(assignmentReference, "D", out var assignmentId))
            {
                state = state with
                {
                    InitialAssignmentId = state.InitialAssignmentId ?? assignmentId,
                    AssignmentId = state.AssignmentId ?? assignmentId
                };

                var assignmentStore = scope.ServiceProvider.GetRequiredService<IAzureProviderResourceAssignmentStore>();
                var assignment = await assignmentStore.GetAsync(state.WorkspaceId, assignmentId, cancellationToken);
                if (assignment?.LastOperationId is { } providerOperationId)
                    state = state with { ProviderOperationId = state.ProviderOperationId ?? providerOperationId };
            }

            if (instance is null)
                return new CleanupResult(state, false, CleanupScope.None);

            if (instance.ObservedLifecycle == ElsaObservedLifecycle.Deleted)
            {
                if (state.DeleteOperationId is not { } completedDeleteOperationId)
                    return new CleanupResult(state, false, CleanupScope.None);

                var completedScope = await WaitForDeletedAsync(
                    application.Services, inputs, state, completedDeleteOperationId, cancellationToken);
                return new CleanupResult(state, true, completedScope);
            }

            var active = await store.GetActiveOperationAsync(state.WorkspaceId, state.InstanceId, cancellationToken);
            var deleteOperation = active?.Action == ElsaInstanceOperationAction.Delete
                ? active.Id
                : (await DeleteAsync(application.Services, inputs, state, cancellationToken)).Operation.Id;
            state = state with { DeleteOperationId = deleteOperation };
            var cleanupScope = await WaitForDeletedAsync(application.Services, inputs, state, deleteOperation, cancellationToken);
            return new CleanupResult(state, true, cleanupScope);
        }
        catch (Exception)
        {
            return new CleanupResult(state, false, CleanupScope.None);
        }
        finally
        {
            if (ownsApplication && application is not null)
            {
                try
                {
                    await application.DisposeAsync();
                }
                catch (Exception)
                {
                    // Preserve the original safe failure result.
                }
            }
        }
    }

    private async Task WriteEvidenceAsync(
        LiveProofInputs inputs,
        ProofState? state,
        string outcome,
        bool cleanupAttempted,
        bool cleanupSucceeded,
        CleanupScope cleanupScope,
        string stage,
        Exception? failure)
    {
        try
        {
            Directory.CreateDirectory(inputs.EvidenceDirectory);
            var path = Path.Combine(
                inputs.EvidenceDirectory,
                $"live-azure-lifecycle-proof-{inputs.InstanceId:N}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json");
            var evidence = new
            {
                outcome,
                stage,
                cleanupAttempted,
                cleanupSucceeded,
                cleanupScope = cleanupScope.ToString().ToLowerInvariant(),
                organizationId = state?.OrganizationId,
                workspaceId = state?.WorkspaceId,
                accountId = state?.AccountId,
                instanceId = state?.InstanceId,
                createOperationId = state?.CreateOperationId,
                reconcileOperationId = state?.ReconcileOperationId,
                deleteOperationId = state?.DeleteOperationId,
                assignmentId = state?.AssignmentId,
                providerOperationId = state?.ProviderOperationId,
                exception = ExceptionEvidence(failure)
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence), CancellationToken.None);
            output.WriteLine($"Live Azure lifecycle proof safe evidence: {path}");
        }
        catch (Exception)
        {
            // Evidence persistence must never expose an exception or mask the lifecycle result.
        }
    }

    private static async Task<GovernedReleaseCatalogEntry> LoadCatalogEntryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<GovernedReleaseCatalogEntry>(stream, CatalogJsonOptions, cancellationToken)
                ?? throw new ProofFailureException();
        }
        catch (JsonException)
        {
            throw new ProofConfigurationException();
        }
    }

    private static ElsaInstanceIntent Intent(GovernedReleaseCatalogEntry entry) => new(
        new ElsaReleaseIntent(
            entry.Distribution.Id,
            entry.Distribution.ReleaseLine,
            entry.Distribution.ReleaseVersion,
            entry.Distribution.Channel),
        new ElsaApplicationIntent(entry.Topology.Id),
        new ElsaPlacementIntent(
            "managed",
            "westeurope",
            "dedicated",
            "standard-small",
            "public",
            "managed"));

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) ? parsed : null;

    private static SafeExceptionEvidence? ExceptionEvidence(Exception? exception)
    {
        if (exception is null)
            return null;

        var baseException = exception.GetBaseException();
        return new(
            SafeSymbol(baseException.GetType().FullName),
            SafeSymbol(baseException.TargetSite?.DeclaringType?.FullName),
            SafeSymbol(baseException.TargetSite?.Name),
            new StackTrace(baseException, true).GetFrame(0)?.GetFileLineNumber());
    }

    private static string? SafeSymbol(string? value) =>
        value is { Length: > 0 and <= 256 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '_' or '<' or '>' or '`' or '[' or ']' or ',' or '$')
            ? value
            : null;

    private static void ValidateIsolatedSqliteDatabase(IConfiguration configuration, string baseDirectory)
    {
        if (!string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ProofConfigurationException();

        var connectionString = configuration.GetConnectionString("Catalog");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ProofConfigurationException();

        SqliteConnectionStringBuilder connection;
        try
        {
            connection = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new ProofConfigurationException();
        }

        var dataSource = connection.DataSource?.Trim();
        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(dataSource))
            throw new ProofConfigurationException();

        string databasePath;
        try
        {
            databasePath = Path.GetFullPath(dataSource, baseDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProofConfigurationException();
        }

        var fileName = Path.GetFileName(databasePath);
        if (!fileName.Contains("live-proof", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(databasePath) || File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
            throw new ProofConfigurationException();
    }

    private static bool HasProductionPackagingContract(
        string? aspNetCoreEnvironment,
        string? dotNetEnvironment,
        string? contentRoot,
        string? configPath)
    {
        if (!string.Equals(aspNetCoreEnvironment, "Production", StringComparison.OrdinalIgnoreCase) ||
            (dotNetEnvironment is not null &&
             !string.Equals(dotNetEnvironment, "Production", StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(contentRoot) ||
            string.IsNullOrWhiteSpace(configPath) ||
            !Path.IsPathFullyQualified(contentRoot) ||
            !Path.IsPathFullyQualified(configPath))
            return false;

        try
        {
            var expectedPath = Path.GetFullPath(Path.Combine(contentRoot, "appsettings.Production.json"));
            var suppliedPath = Path.GetFullPath(configPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(expectedPath, suppliedPath, comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? SafeToken(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : null;
    }

    private static int BoundedSeconds(string? value, int fallback, int minimum, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
                ? parsed
                : throw new ProofConfigurationException();

    private sealed class ProductionAzureLifecycleProofApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
        }
    }

    private sealed record LiveProofInputs(
        string ConfigPath,
        string CatalogEntryPath,
        Guid InstanceId,
        string ActorIssuer,
        string ActorSubject,
        string ActorEmail,
        string Name,
        string Slug,
        TimeSpan Timeout,
        TimeSpan PollInterval,
        string EvidenceDirectory);

    private sealed record ProofState(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid AccountId,
        Guid InstanceId,
        GovernedReleaseCatalogEntry CatalogEntry,
        Guid? InitialAssignmentId,
        Guid? CreateOperationId,
        Guid? ReconcileOperationId,
        Guid? DeleteOperationId,
        Guid? AssignmentId,
        Guid? ProviderOperationId);

    private sealed record CleanupResult(ProofState State, bool Succeeded, CleanupScope Scope);

    private enum CleanupScope
    {
        None,
        Local,
        Provider
    }

    private sealed record SafeExceptionEvidence(
        string? Type,
        string? TargetDeclaringType,
        string? TargetMethod,
        int? SourceLine);

    private sealed class ProofConfigurationException : Exception;

    private sealed class ProofFailureException : Exception;

    private sealed class ProductionAzureFactAttribute : FactAttribute
    {
        public ProductionAzureFactAttribute()
        {
            if (!IsEnabled())
                Skip = $"{Gate} is not enabled; the live Azure proof was explicitly skipped.";
        }
    }
}
