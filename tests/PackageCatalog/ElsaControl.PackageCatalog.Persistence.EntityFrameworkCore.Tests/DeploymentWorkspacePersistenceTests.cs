using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class DeploymentWorkspacePersistenceTests : IDisposable
{
    private readonly CatalogDbContext _db;
    private readonly DeploymentWorkspaceStore _store;
    private readonly Guid _workspaceId;
    private readonly Guid _accountId;

    public DeploymentWorkspacePersistenceTests()
    {
        _db = CreateDbContext();
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        var workspace = new Workspace { Name = "Deployment Workspace" };
        var account = new Account { DisplayName = "Deployment User", Email = "deployment@example.test" };
        _db.Accounts.Add(account);
        _db.Workspaces.Add(workspace);
        _db.SaveChanges();
        _workspaceId = workspace.Id;
        _accountId = account.Id;
        _store = new DeploymentWorkspaceStore(_db);
    }

    [Fact]
    public async Task Persists_workspace_deployment_cockpit_records()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", "Claims workflows", null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                "westeurope",
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null));

        var revision = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "Baseline", "abc123", "{\"records\":[]}", null));

        _db.ChangeTracker.Clear();
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Single(cockpit.Applications, x => x.Id == application.Id.ToString("D"));
        Assert.Single(cockpit.Applications.Single().Environments, x =>
            x.Id == environment.Id.ToString("D")
            && x.DesiredRevision.Revision == revision.RevisionNumber
            && x.DeploymentStatus == DeploymentStatus.Blocked);
        Assert.Single(cockpit.Engines, x =>
            x.Name == "claims-prod"
            && x.CredentialReference.Reference == "kv://claims/prod/elsa-api"
            && x.CredentialReference.VerificationStatus == CredentialVerificationStatus.Unverified);
    }

    [Fact]
    public async Task Persists_environment_tier_reassignment_and_archived_tier_reads()
    {
        var defaults = await _store.EnsureDefaultTiersAsync(_workspaceId);
        var production = defaults.Single(x => x.Name == EnvironmentTier.Production.ToString());
        var uat = await _store.CreateTierAsync(
            _workspaceId,
            new CreateDeploymentTierRequest(
                "UAT",
                null,
                25,
                [DeploymentTierCapabilities.PreproductionLike, DeploymentTierCapabilities.PromotionTarget],
                _accountId));
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(
            _workspaceId,
            new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production, production.Id));

        var reassigned = await _store.UpdateEnvironmentAsync(
            _workspaceId,
            environment.Id,
            new UpdateDeploymentEnvironmentRequest(application.Id, "UAT", EnvironmentTier.Stage, uat.Id));
        await _store.ArchiveTierAsync(_workspaceId, uat.Id, new ArchiveDeploymentTierRequest(_accountId));
        _db.ChangeTracker.Clear();
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Equal(uat.Id, reassigned.TierId);
        Assert.Single(cockpit.Applications.Single().Environments, x =>
            x.Id == environment.Id.ToString("D")
            && x.TierName == "UAT"
            && x.TierStatus == DeploymentTierStatus.Archived.ToString()
            && x.TierCapabilities != null
            && x.TierCapabilities.Contains(DeploymentTierCapabilities.PreproductionLike));
    }

    [Fact]
    public async Task Persists_secret_store_references_and_derives_engine_credentials()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Test", EnvironmentTier.Test));
        var store = await _store.CreateSecretStoreAsync(
            _workspaceId,
            new CreateDeploymentSecretStoreRequest(
                "Control Key Vault",
                "Azure Key Vault",
                null,
                _accountId,
                DeploymentSecretStoreType.AzureKeyVault));
        var reference = await _store.CreateCredentialReferenceAsync(
            _workspaceId,
            new CreateDeploymentCredentialReferenceRequest(store.Id, "Test engine API", "kv://claims/test/elsa-api", null, _accountId));

        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-test",
                "https://workflows.example.test/elsa",
                "westeurope",
                null,
                null,
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null,
                reference.Id));
        _db.ChangeTracker.Clear();

        var stores = await _store.ListSecretStoresAsync(_workspaceId);
        var references = await _store.ListCredentialReferencesAsync(_workspaceId);
        var usage = await _store.ListCredentialReferenceUsageAsync(_workspaceId, reference.Id);
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Single(stores, x => x.Id == store.Id && x.Provider == "Azure Key Vault" && x.Type == DeploymentSecretStoreType.AzureKeyVault);
        Assert.Single(references, x =>
            x.Id == reference.Id
            && x.SecretStoreId == store.Id
            && x.SecretStoreType == DeploymentSecretStoreType.AzureKeyVault
            && x.UsageCount == 1);
        Assert.Single(usage, x =>
            x.EngineId == engine.Id
            && x.EngineName == "claims-test"
            && x.ApplicationName == "Claims"
            && x.EnvironmentName == "Test");
        Assert.Equal("Azure Key Vault", engine.CredentialProvider);
        Assert.Equal("kv://claims/test/elsa-api", engine.CredentialReference);
        Assert.Equal(reference.Id, engine.CredentialReferenceId);
        Assert.Single(cockpit.Engines, x => x.CredentialReference.Reference == "kv://claims/test/elsa-api");
    }

    [Fact]
    public async Task Persists_local_protected_engine_credential_metadata_and_deferred_engine_registration()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Dev", EnvironmentTier.Dev));
        var store = await _store.CreateSecretStoreAsync(
            _workspaceId,
            new CreateDeploymentSecretStoreRequest(
                "Local engine credentials",
                "Local encrypted database",
                null,
                _accountId,
                DeploymentSecretStoreType.LocalEncryptedDatabase));
        var reference = await _store.CreateCredentialReferenceAsync(
            _workspaceId,
            new CreateDeploymentCredentialReferenceRequest(
                store.Id,
                "Dev engine API",
                "local://engine-credentials/dev-engine-api",
                null,
                _accountId,
                "protected:v1"));

        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-dev",
                "https://workflows-dev.example.test/elsa",
                null,
                null,
                null,
                [],
                [],
                null,
                null,
                EngineCredentialAssignmentStatus.Deferred));
        _db.ChangeTracker.Clear();

        var stores = await _store.ListSecretStoresAsync(_workspaceId);
        var references = await _store.ListCredentialReferencesAsync(_workspaceId);
        var secret = await _store.GetCredentialSecretAsync(_workspaceId, reference.Id);
        Assert.Single(stores, x =>
            x.Id == store.Id
            && x.Type == DeploymentSecretStoreType.LocalEncryptedDatabase
            && x.Provider == "Local encrypted database");
        Assert.Single(references, x => x.Id == reference.Id && x.HasProtectedSecret && x.UsageCount == 0);
        Assert.Equal(new WorkspaceDeploymentCredentialSecret(
            reference.Id,
            DeploymentSecretStoreStatus.Active,
            DeploymentSecretStoreStatus.Active,
            DeploymentSecretStoreType.LocalEncryptedDatabase,
            "protected:v1"), secret);
        Assert.Equal(EngineCredentialAssignmentStatus.Deferred, engine.CredentialAssignmentStatus);
        Assert.Empty(engine.CredentialProvider);
        Assert.Empty(engine.CredentialReference);
        Assert.Null(engine.CredentialReferenceId);
    }

    [Fact]
    public async Task Archived_secret_metadata_is_hidden_from_active_lists_and_new_engine_registration()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Test", EnvironmentTier.Test));
        var store = await _store.CreateSecretStoreAsync(
            _workspaceId,
            new CreateDeploymentSecretStoreRequest("Control Key Vault", "Azure Key Vault", null, _accountId));
        var reference = await _store.CreateCredentialReferenceAsync(
            _workspaceId,
            new CreateDeploymentCredentialReferenceRequest(store.Id, "Test engine API", "kv://claims/test/elsa-api", null, _accountId));

        await _store.ArchiveCredentialReferenceAsync(_workspaceId, reference.Id, _accountId);

        var activeReferences = await _store.ListCredentialReferencesAsync(_workspaceId);
        var allReferences = await _store.ListCredentialReferencesAsync(_workspaceId, includeArchived: true);
        var register = async () => await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-test",
                "https://workflows.example.test/elsa",
                "westeurope",
                null,
                null,
                [],
                [],
                null,
                reference.Id));

        Assert.Empty(activeReferences);
        Assert.Single(allReferences, x => x.Id == reference.Id && x.Status == DeploymentSecretStoreStatus.Archived);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(register);
        Assert.Equal("Archived deployment credential references cannot be assigned to engines.", exception.Message);
    }

    [Fact]
    public async Task Archived_secret_metadata_names_can_be_reused_and_archived_again()
    {
        var firstStore = await _store.CreateSecretStoreAsync(
            _workspaceId,
            new CreateDeploymentSecretStoreRequest("Control Key Vault", "Azure Key Vault", null, _accountId));
        var firstReference = await _store.CreateCredentialReferenceAsync(
            _workspaceId,
            new CreateDeploymentCredentialReferenceRequest(firstStore.Id, "Test engine API", "kv://claims/test/elsa-api", null, _accountId));
        await _store.ArchiveCredentialReferenceAsync(_workspaceId, firstReference.Id, _accountId);
        await _store.ArchiveSecretStoreAsync(_workspaceId, firstStore.Id, _accountId);

        var secondStore = await _store.CreateSecretStoreAsync(
            _workspaceId,
            new CreateDeploymentSecretStoreRequest("Control Key Vault", "Azure Key Vault", null, _accountId));
        var secondReference = await _store.CreateCredentialReferenceAsync(
            _workspaceId,
            new CreateDeploymentCredentialReferenceRequest(secondStore.Id, "Test engine API", "kv://claims/test/elsa-api-v2", null, _accountId));

        await _store.ArchiveCredentialReferenceAsync(_workspaceId, secondReference.Id, _accountId);
        await _store.ArchiveSecretStoreAsync(_workspaceId, secondStore.Id, _accountId);
        var allStores = await _store.ListSecretStoresAsync(_workspaceId, includeArchived: true);
        var allReferences = await _store.ListCredentialReferencesAsync(_workspaceId, includeArchived: true);

        Assert.Equal(2, allStores.Count());
        Assert.All(allStores, x => Assert.Equal(DeploymentSecretStoreStatus.Archived, x.Status));
        Assert.Equal(2, allReferences.Count());
        Assert.All(allReferences, x => Assert.Equal(DeploymentSecretStoreStatus.Archived, x.Status));
    }

    [Fact]
    public async Task Persists_workspace_permission_grants()
    {
        await _store.GrantPermissionAsync(_workspaceId, new GrantWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, null));
        var grants = await _store.GetPermissionGrantsAsync(_workspaceId, _accountId);

        Assert.Single(grants, x => x.Permission == WorkspaceDeploymentPermissions.Read && x.RevokedAt == null);
    }

    [Fact]
    public async Task Permission_grant_and_revoke_are_idempotent_and_audited_with_actor_provenance()
    {
        var actorId = _accountId;
        var first = await _store.GrantPermissionAsync(
            _workspaceId,
            new GrantWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, actorId));
        var replay = await _store.GrantPermissionAsync(
            _workspaceId,
            new GrantWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, actorId));

        var revoked = await _store.RevokePermissionAsync(
            _workspaceId,
            new RevokeWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, actorId));
        var revokeReplay = await _store.RevokePermissionAsync(
            _workspaceId,
            new RevokeWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, actorId));
        var audit = await _store.ListPermissionAuditRecordsAsync(_workspaceId, _accountId);

        Assert.Equal(first.Id, replay.Id);
        Assert.True(revoked.Changed);
        Assert.Single(revoked.Grants, x => x.Id == first.Id && x.RevokedByAccountId == actorId);
        Assert.False(revokeReplay.Changed);
        Assert.Equal(2, audit.Count());
        Assert.Equal(new[] {
            WorkspacePermissionAuditAction.Granted,
            WorkspacePermissionAuditAction.Revoked
        }.Order(), audit.Select(x => x.Action).Order());
        Assert.All(audit, x => {
            Assert.Equal(first.Id, x.GrantId);
            Assert.Equal(actorId, x.ActorAccountId);
        });
    }

    [Fact]
    public async Task Revoking_permission_clears_all_duplicate_active_grants()
    {
        var firstGrantId = Guid.NewGuid();
        var secondGrantId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.UtcTicks;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspacePermissionGrants (Id, WorkspaceId, AccountId, Permission, GrantedByAccountId, CreatedAt, UpdatedAt, RevokedAt, RevokedByAccountId)
            VALUES ({firstGrantId}, {_workspaceId}, {_accountId}, {WorkspaceDeploymentPermissions.Read}, NULL, {createdAt}, {createdAt}, NULL, NULL)
            """);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspacePermissionGrants (Id, WorkspaceId, AccountId, Permission, GrantedByAccountId, CreatedAt, UpdatedAt, RevokedAt, RevokedByAccountId)
            VALUES ({secondGrantId}, {_workspaceId}, {_accountId}, {WorkspaceDeploymentPermissions.Read}, NULL, {createdAt + 1}, {createdAt + 1}, NULL, NULL)
            """);

        var result = await _store.RevokePermissionAsync(
            _workspaceId,
            new RevokeWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, _accountId));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Grants.Count());
        Assert.All(result.Grants, x => Assert.True(x.RevokedAt.HasValue));
        Assert.DoesNotContain((await _store.GetPermissionGrantsAsync(_workspaceId, _accountId)), x => !x.RevokedAt.HasValue);
        var audit = await _store.ListPermissionAuditRecordsAsync(_workspaceId, _accountId);
        Assert.Equal(2, audit.Count());
        Assert.All(audit, x => Assert.Equal(WorkspacePermissionAuditAction.Revoked, x.Action));
    }

    [Fact]
    public async Task Grant_permission_tolerates_duplicate_active_grants()
    {
        var firstGrantId = Guid.NewGuid();
        var secondGrantId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.UtcTicks;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspacePermissionGrants (Id, WorkspaceId, AccountId, Permission, GrantedByAccountId, CreatedAt, UpdatedAt, RevokedAt)
            VALUES ({firstGrantId}, {_workspaceId}, {_accountId}, {WorkspaceDeploymentPermissions.Read}, NULL, {createdAt}, {createdAt}, NULL)
            """);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WorkspacePermissionGrants (Id, WorkspaceId, AccountId, Permission, GrantedByAccountId, CreatedAt, UpdatedAt, RevokedAt)
            VALUES ({secondGrantId}, {_workspaceId}, {_accountId}, {WorkspaceDeploymentPermissions.Read}, NULL, {createdAt + 1}, {createdAt + 1}, NULL)
            """);

        var grant = await _store.GrantPermissionAsync(_workspaceId, new GrantWorkspacePermissionRequest(_accountId, WorkspaceDeploymentPermissions.Read, null));

        Assert.Equal(firstGrantId, grant.Id);
    }

    [Fact]
    public async Task Persists_engine_health_verification_metadata()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                "westeurope",
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null));
        var verifiedAt = DateTimeOffset.Parse("2026-05-26T10:00:00Z");

        await _store.UpdateEngineHealthAsync(
            _workspaceId,
            new EngineHealthUpdate(
                engine.Id,
                environment.Id,
                DeploymentHealth.Healthy,
                "Elsa 4.1.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                verifiedAt,
                verifiedAt,
                verifiedAt,
                "Endpoint responded successfully."));

        _db.ChangeTracker.Clear();
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Single(cockpit.Engines, x =>
            x.Id == engine.Id.ToString("D")
            && x.Health == DeploymentHealth.Healthy
            && x.Endpoint.Version == "Elsa 4.1.0"
            && x.CredentialReference.LastVerifiedAt == verifiedAt
            && x.LastHeartbeatAt == verifiedAt
            && x.LastVerificationAt == verifiedAt
            && x.VerificationMessage == "Endpoint responded successfully.");
    }

    [Fact]
    public async Task Heartbeat_updates_health_without_replacing_controls_when_capabilities_are_omitted()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                "westeurope",
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null));
        var heartbeatAt = DateTimeOffset.Parse("2026-05-26T10:00:00Z");

        await _store.ApplyEngineHeartbeatAsync(
            _workspaceId,
            new EngineHealthUpdate(
                engine.Id,
                environment.Id,
                DeploymentHealth.Healthy,
                "Elsa 4.1.0",
                CertificateStatus.Trusted,
                CredentialVerificationStatus.Verified,
                heartbeatAt,
                heartbeatAt,
                null,
                "Heartbeat accepted."));

        _db.ChangeTracker.Clear();
        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        var registration = cockpit.Engines.Single(x => x.Id == engine.Id.ToString("D"));
        Assert.Single(registration.Controls, x => x.Id == "reload-configuration");
        Assert.Single(registration.Capabilities, x => x.Id == "engine.reload-configuration");
        Assert.Equal(heartbeatAt, registration.LastHeartbeatAt);
    }

    [Fact]
    public async Task Persists_structured_desired_state_records_and_keeps_revisions_immutable()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var first = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "Baseline", "abc123", """
                {"records":[{"kind":"Workflow","name":"Payment Retry","payload":{"version":1}}]}
                """, null));
        var second = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "Update", "def456", """
                {"records":[{"kind":"Workflow","name":"Payment Retry","payload":{"version":2}}]}
                """, null));

        _db.ChangeTracker.Clear();
        var loadedFirst = await _store.GetRevisionAsync(_workspaceId, first.Id);
        var latest = await _store.GetLatestRevisionAsync(_workspaceId, environment.Id);
        var recordCount = await CountStructuredDesiredStateRecordsAsync();

        Assert.Equal(1, loadedFirst!.RevisionNumber);
        Assert.Contains("\"version\":1", loadedFirst.DesiredStateJson);
        Assert.Equal(second.Id, latest!.Id);
        Assert.Equal(2, latest.RevisionNumber);
        Assert.Equal(2, recordCount);
    }

    [Fact]
    public async Task Projects_artifact_references_into_structured_desired_state_records()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var artifactRecordId = Guid.NewGuid();
        var desiredStateJson = """
            {"records":[{
              "kind":"ArtifactReference",
              "name":"Payment Retry",
              "payload":{
                "artifactRecordId":"__artifactRecordId__",
                "artifactId":"workflow:payment-retry:v1",
                "artifactTypeId":"elsa.workflow-definition",
                "contentDigest":{"algorithm":"sha256","value":"digest-v1"}
              }}]}
            """.Replace("__artifactRecordId__", artifactRecordId.ToString("D"), StringComparison.Ordinal);

        await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "Artifact baseline", "abc123", desiredStateJson, null));

        var projection = await LoadArtifactReferenceProjectionAsync();

        Assert.Equal(artifactRecordId, projection.ArtifactRecordId);
        Assert.Equal("workflow:payment-retry:v1", projection.ArtifactId);
        Assert.Equal("elsa.workflow-definition", projection.ArtifactTypeId);
        Assert.Equal("sha256", projection.ArtifactDigestAlgorithm);
        Assert.Equal("digest-v1", projection.ArtifactDigest);
    }

    [Fact]
    public async Task Reads_legacy_artifact_records_without_projected_reference_columns()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var revision = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, environment.Id, "Legacy artifact", "abc123", """
                {"records":[{
                  "kind":"ArtifactReference",
                  "name":"Payment Retry",
                  "payload":{
                    "artifactId":"workflow:payment-retry:v1",
                    "artifactTypeId":"elsa.workflow-definition",
                    "contentDigest":{"algorithm":"sha256","value":"digest-v1"}
                  }}]}
                """, null));
        await _db.Database.ExecuteSqlRawAsync("""
            UPDATE StructuredDesiredStateRecords
            SET ArtifactRecordId = NULL,
                ArtifactId = NULL,
                ArtifactTypeId = NULL,
                ArtifactDigestAlgorithm = NULL,
                ArtifactDigest = NULL
            """);

        _db.ChangeTracker.Clear();
        var loaded = await _store.GetRevisionAsync(_workspaceId, revision.Id);

        Assert.NotNull(loaded);
        Assert.Contains("\"ArtifactReference\"", loaded!.DesiredStateJson);
        Assert.Contains("workflow:payment-retry:v1", loaded.DesiredStateJson);
    }

    [Fact]
    public async Task Persists_confirmations_runs_and_append_only_history()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var sourceEnvironment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Stage", EnvironmentTier.Stage));
        var targetEnvironment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                targetEnvironment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                null,
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [],
                [],
                null));
        var revision = await _store.CreateRevisionAsync(
            _workspaceId,
            new CreateDesiredStateRevisionRequest(application.Id, sourceEnvironment.Id, "Candidate", "abc123", "{\"records\":[]}", null));
        var mutationStore = (IWorkspaceDeploymentMutationStore)_store;
        var now = DateTimeOffset.UtcNow;
        var confirmation = await mutationStore.CreateConfirmationAsync(
            _workspaceId,
            new CreateActionConfirmationRequest(ConfirmationActionType.Deploy, revision.Id.ToString("D"), _accountId),
            now);
        var useAttempt = await mutationStore.TryMarkConfirmationUsedAsync(_workspaceId, confirmation.Id, now.AddSeconds(1));
        var run = await mutationStore.CreateRunAsync(
            _workspaceId,
            new QueueWorkspaceDeploymentRunRequest(revision.Id, targetEnvironment.Id, engine.Id, confirmation.Id, _accountId),
            now.AddSeconds(2));

        var claimed = await mutationStore.ClaimNextQueuedRunAsync("worker-1", now.AddSeconds(3));
        var completed = await mutationStore.UpdateRunStatusAsync(_workspaceId, run.Id, WorkspaceDeploymentRunStatus.Succeeded, "Deployment run completed.", now.AddSeconds(4));
        var loaded = await mutationStore.GetRunAsync(_workspaceId, run.Id);
        var history = await mutationStore.GetRunHistoryAsync(_workspaceId, run.Id);

        Assert.True(useAttempt!.Consumed);
        Assert.Equal(now.AddSeconds(1), useAttempt.Confirmation.UsedAt);
        Assert.Equal(run.Id, claimed!.Id);
        Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, completed.Status);
        Assert.Equal(WorkspaceDeploymentRunStatus.Succeeded, loaded!.Status);
        Assert.Equal(new[] {
            WorkspaceDeploymentRunStatus.Queued,
            WorkspaceDeploymentRunStatus.Running,
            WorkspaceDeploymentRunStatus.Succeeded
        }, history.Select(x => x.Status));
    }

    [Fact]
    public async Task Persists_runtime_control_audit_records()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                null,
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [new EngineCapability("engine.reload-configuration", "Reload engine configuration", CapabilityBoundary.EngineApi)],
                [new RuntimeControl("reload-configuration", "Reload Configuration", CapabilityBoundary.EngineApi, "engine.reload-configuration", "Reloads engine API configuration.")],
                null));
        var mutationStore = (IWorkspaceDeploymentMutationStore)_store;

        var execution = await mutationStore.RecordRuntimeControlExecutionAsync(
            _workspaceId,
            new RuntimeControlExecution(
                Guid.NewGuid(),
                _workspaceId,
                engine.Id,
                environment.Id,
                "reload-configuration",
                "Reload Configuration",
                CapabilityBoundary.EngineApi,
                "engine.reload-configuration",
                Guid.NewGuid(),
                _accountId,
                RuntimeControlExecutionStatus.Succeeded,
                DateTimeOffset.UtcNow,
                "Reload Configuration executed for claims-prod."));

        Assert.Equal(RuntimeControlExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, (await CountRuntimeControlExecutionsAsync()));
    }

    [Fact]
    public async Task Projects_persisted_observability_and_drift_metadata()
    {
        var application = await _store.CreateApplicationAsync(_workspaceId, new CreateWorkflowApplicationRequest("Claims", null, null));
        var environment = await _store.CreateEnvironmentAsync(_workspaceId, new CreateDeploymentEnvironmentRequest(application.Id, "Prod", EnvironmentTier.Production));
        var engine = await _store.RegisterEngineAsync(
            _workspaceId,
            new RegisterWorkflowEngineRequest(
                environment.Id,
                "claims-prod",
                "https://workflows.example.test/elsa",
                null,
                "Azure Key Vault",
                "kv://claims/prod/elsa-api",
                [],
                [],
                null));
        Guid? correlatedRevisionId = null;
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ObservabilityBindings (Id, WorkspaceId, EnvironmentId, EngineId, Kind, Provider, Status, Scope, CorrelatedRevisionId, Sample)
            VALUES ({Guid.NewGuid()}, {_workspaceId}, {environment.Id}, {engine.Id}, {"Logs"}, {"Azure Monitor"}, {"Connected"}, {"workspace:/prod"}, {correlatedRevisionId}, {"Imported status"});
            """);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO DriftReportItems (Id, WorkspaceId, EnvironmentId, EngineId, Area, Desired, Observed, Action, DetectedAt)
            VALUES ({Guid.NewGuid()}, {_workspaceId}, {environment.Id}, {engine.Id}, {"RuntimeConfiguration"}, {"Concurrency 32"}, {"Concurrency 16"}, {"Review"}, {DateTimeOffset.UtcNow.UtcTicks});
            """);

        var cockpit = await _store.GetCockpitAsync(_workspaceId);

        Assert.Single(cockpit.ObservabilityBindings, x =>
            x.Kind == ObservabilityBindingKind.Logs
            && x.Provider == "Azure Monitor"
            && x.Sample == "Imported status");
        Assert.Single(cockpit.DriftReport, x =>
            x.Area == "RuntimeConfiguration"
            && x.Desired == "Concurrency 32"
            && x.Observed == "Concurrency 16"
            && x.Action == DriftAction.Review);
    }

    public void Dispose() => _db.Dispose();

    private async Task<long> CountStructuredDesiredStateRecordsAsync()
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM StructuredDesiredStateRecords";
        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    private async Task<ArtifactReferenceProjection> LoadArtifactReferenceProjectionAsync()
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT ArtifactRecordId, ArtifactId, ArtifactTypeId, ArtifactDigestAlgorithm, ArtifactDigest
            FROM StructuredDesiredStateRecords
            WHERE Kind = 'ArtifactReference'
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True((await reader.ReadAsync()));
        return new ArtifactReferenceProjection(
            reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private async Task<long> CountRuntimeControlExecutionsAsync()
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RuntimeControlExecutions";
        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    private static CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed record ArtifactReferenceProjection(
        Guid? ArtifactRecordId,
        string? ArtifactId,
        string? ArtifactTypeId,
        string? ArtifactDigestAlgorithm,
        string? ArtifactDigest);
}
