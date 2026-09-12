using System.Diagnostics;
using ElsaControl.Deployment.Artifacts;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class DeploymentWorkspaceArtifactPersistenceTests : IDisposable
{
    private readonly CatalogDbContext _db;
    private readonly DeploymentWorkspaceStore _store;
    private readonly Guid _workspaceId;
    private readonly Guid _accountId;

    public DeploymentWorkspaceArtifactPersistenceTests()
    {
        _db = CreateDbContext();
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        var workspace = new Workspace { Name = "Artifact Workspace" };
        var account = new Account { DisplayName = "Artifact User", Email = "artifact@example.test" };
        _db.Workspaces.Add(workspace);
        _db.Accounts.Add(account);
        _db.SaveChanges();

        _workspaceId = workspace.Id;
        _accountId = account.Id;
        _store = new DeploymentWorkspaceStore(_db);
    }

    [Fact]
    public async Task Persists_artifact_metadata_without_payload_content()
    {
        var artifact = await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration());
        _db.ChangeTracker.Clear();

        var loaded = (await _store.GetArtifactAsync(_workspaceId, artifact.Id))!;
        var listed = await _store.ListArtifactsAsync(_workspaceId);
        var rawJson = await ReadScalarAsync<string>("SELECT ResourceSummaryJson || DiagnosticsJson FROM WorkspaceDeploymentArtifacts LIMIT 1");

        Assert.NotNull(loaded);
        Assert.Equal("sha256:claims-prod", loaded.ArtifactId);
        Assert.Equal(ArtifactTypeIds.ElsaLoomRecipe, loaded.ArtifactTypeId);
        Assert.Equal("manual", loaded.Producer!.ProducerType);
        Assert.Equal("claims", loaded.DisplayMetadata!.Name);
        Assert.Single(loaded.CompatibilityHints!, x => x.RequiredArtifactType == ArtifactTypeIds.ElsaLoomRecipe);
        Assert.Equal("claims", loaded.Manifest.Name);
        Assert.Single(loaded.Resources, x => x.LogicalId == "payment-retry");
        Assert.Single(listed, x => x.Id == artifact.Id);
        Assert.DoesNotContain("workflow definition payload", rawJson);
        Assert.DoesNotContain("secret value", rawJson);
        Assert.DoesNotContain("token", rawJson);
    }

    [Fact]
    public async Task Persists_envelope_metadata_without_payload_or_secrets()
    {
        var artifact = await _store.RegisterArtifactAsync(
            _workspaceId,
            ArtifactRegistration() with
            {
                Producer = new ArtifactProducer("studio", "Elsa Studio", "4.0.0", "workflow:claims"),
                DisplayMetadata = new ArtifactDisplayMetadata(
                    "Claims",
                    "1.0.0",
                    "Claims workflow",
                    new Dictionary<string, string> { ["domain"] = "claims" },
                    new Dictionary<string, string> { ["studio.workflowId"] = "claims" }),
                CompatibilityHints =
                [
                    new ArtifactCompatibilityHint(
                        ArtifactTypeIds.ElsaLoomRecipe,
                        "elsa-workflows",
                        ">=4.0.0",
                        ["loom.recipe.apply"],
                        new Dictionary<string, string>())
                ]
            });
        _db.ChangeTracker.Clear();

        var loaded = (await _store.GetArtifactAsync(_workspaceId, artifact.Id))!;
        var rawJson = await ReadScalarAsync<string>("SELECT PayloadReferenceJson || ProducerJson || DisplayMetadataJson || CompatibilityHintsJson FROM WorkspaceDeploymentArtifacts LIMIT 1");

        Assert.Equal("studio", loaded.Producer!.ProducerType);
        Assert.Contains("domain", loaded.DisplayMetadata!.Labels.Keys);
        Assert.Single(loaded.CompatibilityHints!, x => x.RuntimeFamily == "elsa-workflows");
        Assert.DoesNotContain("workflow definition payload", rawJson);
        Assert.DoesNotContain("secret value", rawJson);
        Assert.DoesNotContain("token", rawJson);
        Assert.DoesNotContain("password", rawJson);
    }

    [Fact]
    public async Task Enforces_unique_artifact_identity_per_workspace()
    {
        await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration());

        var act = () => _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration(reference: "/tmp/other"));

        await Assert.ThrowsAsync<DbUpdateException>(act);
    }

    [Fact]
    public async Task Allows_same_artifact_identity_in_different_workspaces()
    {
        var otherWorkspaceId = await CreateWorkspaceAsync("Other Artifact Workspace");
        var first = await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration());
        var second = await _store.RegisterArtifactAsync(otherWorkspaceId, ArtifactRegistration());

        Assert.Equal(second.ArtifactId, first.ArtifactId);
        Assert.NotEqual(second.WorkspaceId, first.WorkspaceId);
    }

    [Fact]
    public async Task Updates_inspection_status_without_changing_identity()
    {
        var artifact = await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration());
        var inspectedAt = DateTimeOffset.Parse("2026-05-28T10:00:00Z");

        var update = await _store.UpdateArtifactInspectionAsync(
            _workspaceId,
            new WorkspaceArtifactInspectionUpdate(
                artifact.Id,
                artifact.ArtifactId,
                WorkspaceArtifactChecksumStatus.Mismatched,
                WorkspaceArtifactInspectionStatus.Invalid,
                inspectedAt,
                [ArtifactResource(ArtifactTypeIds.ElsaLoomRecipe, "payment-retry-v2")],
                [new WorkspaceArtifactDiagnostic("artifact.identity.mismatch", WorkspaceArtifactDiagnosticSeverity.Error, "Referenced artifact identity does not match.")]));
        _db.ChangeTracker.Clear();
        var loaded = await _store.GetArtifactAsync(_workspaceId, artifact.Id);

        Assert.Equal(artifact.ArtifactId, update.ArtifactId);
        Assert.Equal(WorkspaceArtifactChecksumStatus.Mismatched, update.ChecksumStatus);
        Assert.Equal(artifact.ArtifactId, loaded!.ArtifactId);
        Assert.Single(loaded.Resources, x => x.LogicalId == "payment-retry-v2");
        Assert.Single(loaded.Diagnostics, x => x.Code == "artifact.identity.mismatch");
    }

    [Fact]
    public async Task Persists_artifact_upload_session_lifecycle_and_workspace_scope()
    {
        var otherWorkspaceId = await CreateWorkspaceAsync("Other Upload Workspace");
        var now = DateTimeOffset.Parse("2026-06-04T12:00:00Z");
        var session = new WorkspaceArtifactUploadSession(
            Guid.NewGuid(),
            _workspaceId,
            WorkspaceArtifactUploadStatus.Pending,
            "claims-prod.zip",
            "application/zip",
            1024,
            null,
            "/tmp/staged.zip",
            "upload-key",
            [],
            now.AddMinutes(30),
            null,
            _accountId,
            now,
            now);

        var created = await _store.CreateArtifactUploadSessionAsync(session);
        var updated = await _store.UpdateArtifactUploadSessionAsync(created with
        {
            Status = WorkspaceArtifactUploadStatus.Completed,
            UploadedSizeBytes = 1024,
            Diagnostics = [new WorkspaceArtifactDiagnostic("artifact.upload.completed", WorkspaceArtifactDiagnosticSeverity.Info, "Upload completed.")],
            UpdatedAt = now.AddMinutes(1)
        });
        _db.ChangeTracker.Clear();

        var loaded = await _store.GetArtifactUploadSessionAsync(_workspaceId, session.Id);
        var byIdempotency = await _store.FindArtifactUploadByIdempotencyKeyAsync(_workspaceId, "upload-key");
        var crossWorkspace = await _store.GetArtifactUploadSessionAsync(otherWorkspaceId, session.Id);

        Assert.Equal(WorkspaceArtifactUploadStatus.Completed, updated.Status);
        Assert.Equal(1024, loaded!.UploadedSizeBytes);
        Assert.Single(loaded.Diagnostics, x => x.Code == "artifact.upload.completed");
        Assert.Equal(session.Id, byIdempotency!.Id);
        Assert.Null(crossWorkspace);
    }

    [Fact]
    public async Task Rejects_inspection_update_that_changes_identity()
    {
        var artifact = await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration());

        var act = () => _store.UpdateArtifactInspectionAsync(
            _workspaceId,
            new WorkspaceArtifactInspectionUpdate(
                artifact.Id,
                "sha256:other",
                WorkspaceArtifactChecksumStatus.Verified,
                WorkspaceArtifactInspectionStatus.Valid,
                DateTimeOffset.UtcNow,
                artifact.Resources,
                []));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal("Artifact inspection update cannot change the registered artifact identity.", exception.Message);
    }

    [Fact]
    public async Task Lists_normal_artifact_dataset_under_three_seconds()
    {
        for (var i = 0; i < 250; i++)
            await _store.RegisterArtifactAsync(_workspaceId, ArtifactRegistration($"sha256:artifact-{i:000}", $"/tmp/artifact-{i:000}"));

        var stopwatch = Stopwatch.StartNew();
        var artifacts = await _store.ListArtifactsAsync(_workspaceId);
        stopwatch.Stop();

        Assert.Equal(250, artifacts.Count());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> CreateWorkspaceAsync(string name)
    {
        var workspace = new Workspace { Name = name };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();
        return workspace.Id;
    }

    private async Task<T> ReadScalarAsync<T>(string sql)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private RegisterWorkspaceArtifactRequest ArtifactRegistration(
        string artifactId = "sha256:claims-prod",
        string reference = "/tmp/claims-prod") =>
        new(
            artifactId,
            "elsa-control/deployment-artifact/v1alpha1",
            new WorkspaceArtifactDigest("sha256", artifactId.Replace("sha256:", "", StringComparison.Ordinal)),
            WorkspaceArtifactFormat.Folder,
            "local",
            reference,
            new WorkspaceArtifactManifestSummary("claims", "1.0.0", "prod"),
            [ArtifactResource()],
            [],
            _accountId);

    private static WorkspaceArtifactResourceSummary ArtifactResource(
        string type = ArtifactTypeIds.ElsaLoomRecipe,
        string logicalId = "payment-retry") =>
        new(type, logicalId, null, "8", new WorkspaceArtifactDigest("sha256", "workflow-hash"));

    private static CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;
        return new CatalogDbContext(options);
    }
}
