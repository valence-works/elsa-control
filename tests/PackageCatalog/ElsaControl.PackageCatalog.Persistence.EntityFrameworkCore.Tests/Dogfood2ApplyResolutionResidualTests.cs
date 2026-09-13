using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.Plans;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// Residual after #413: live Dogfood2 Apply of admitted
/// <c>3.8.0-preview.5567-build.153</c> still failed with opaque
/// <c>resolution.invalid</c> even though catalog reconstruct succeeded.
/// This exercises the claimed worker path (claim → Validate → Resolve → Commit)
/// against a pin row plus the unique <c>*-build.N</c> sibling.
/// </summary>
public sealed class Dogfood2ApplyResolutionResidualTests
{
    private const string PinVersion = "3.8.0-preview.5567";
    private const string BuildVersion = "3.8.0-preview.5567-build.153";
    private const string PinDigest = "sha256:61a8b8179b13f1b9b0560c3eb8723c4a98f69930b9fff02c0b7c5c3f0d56f4d7";
    private const string BuildDigest = "sha256:3935507cf7ef56b895bb6c32d0fb7cd2d607981470c06e15cf1d01f10d529837";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T00:00:00Z");
    private static readonly IReadOnlyDictionary<string, string> GovernedSecrets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://vault/database-connection",
            ["identity:signingkey"] = "secret://vault/identity-signing-key",
            ["admin:password"] = "secret://vault/admin-password"
        };

    [Fact]
    public async Task UpdateIntent_to_admitted_preview_build_suffix_queues_without_opaque_resolution_invalid()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(connection)
            .Options;
        await using var db = new CatalogDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var organization = new Organization { Id = Guid.NewGuid(), Name = "Dogfood2 residual organization" };
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Name = "Dogfood2 residual workspace",
            Kind = WorkspaceKind.Shared
        };
        db.Organizations.Add(organization);
        db.Workspaces.Add(workspace);
        db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = int.MaxValue,
            SyncedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await db.SaveChangesAsync();

        var catalog = new GovernedReleaseCatalogStore(options);
        Assert.Equal(
            GovernedReleaseCatalogWriteStatus.Stored,
            (await catalog.StoreAsync(CreateRelease(PinVersion, PinDigest, 'a'))).Status);
        Assert.Equal(
            GovernedReleaseCatalogWriteStatus.Stored,
            (await catalog.StoreAsync(CreateRelease(BuildVersion, BuildDigest, 'b'))).Status);

        var pinMatches = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: "valence-runtime",
            ReleaseLine: "3.8",
            ReleaseVersion: PinVersion,
            Channel: "preview",
            RegistryClass: "paid",
            TopologyId: "combined"));
        var buildMatches = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: "valence-runtime",
            ReleaseLine: "3.8",
            ReleaseVersion: BuildVersion,
            Channel: "preview",
            RegistryClass: "paid",
            TopologyId: "combined"));
        Assert.Equal(PinVersion, Assert.Single(pinMatches).Distribution.ReleaseVersion);
        Assert.Equal(BuildVersion, Assert.Single(buildMatches).Distribution.ReleaseVersion);
        Assert.Equal(PinDigest, pinMatches[0].ManifestDigest);
        Assert.Equal(BuildDigest, buildMatches[0].ManifestDigest);

        var time = new FixedTimeProvider(Now);
        var source = new CatalogElsaInstanceLifecycleResolutionInputSource(
            db,
            catalog,
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);
        var store = new EfCoreElsaInstanceLifecycleStore(db, source, time);
        var service = new ElsaInstanceLifecycleService(store, time);
        var resolver = new ElsaInstancePlanResolver(
            new EmptyCatalog(),
            new CompatibleCatalog(),
            new ElsaInstancePlanResolutionOptions(DefaultEgress: "unrestricted"));
        var worker = new ElsaInstanceLifecycleWorker(store, resolver, time);

        var created = await service.CreateAsync(new ElsaInstanceCreateRequest(
            organization.Id,
            workspace.Id,
            "Dogfood2",
            "dogfood2",
            PinIntent(),
            "dogfood2-create",
            ActorAccountId: Guid.NewGuid()));
        db.ChangeTracker.Clear();

        var createResult = Assert.Single((await worker.ProcessAvailableAsync("dogfood2-create-worker")).Results);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, createResult.Outcome);
        Assert.Equal(PinVersion, createResult.Instance.CurrentResolvedRelease!.Version);
        await CompleteManagedRunAsync(db, created.Operation.Id, created.Instance.Id);

        var instance = await store.GetInstanceAsync(workspace.Id, created.Instance.Id);
        Assert.NotNull(instance);
        var updated = await service.UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
            workspace.Id,
            created.Instance.Id,
            BuildIntent(),
            instance!.Version,
            "dogfood2-apply-153",
            ActorAccountId: Guid.NewGuid()));
        Assert.False(updated.Replayed);
        Assert.Equal(BuildVersion, updated.Instance.ReleaseIntent.RequestedVersion);
        Assert.Equal(BuildDigest, updated.Instance.ReleaseIntent.PreviewManifestDigest);
        db.ChangeTracker.Clear();

        var reconstructed = await source.GetAsync(updated.Instance, updated.Operation);
        Assert.NotNull(reconstructed);
        Assert.Equal(BuildVersion, reconstructed!.PlanRequest.ReleaseManifest.Manifest!.Distribution.ReleaseVersion);
        Assert.Equal(BuildVersion, reconstructed.PlanRequest.InstanceIntent.Release.RequestedVersion);

        var applyResult = Assert.Single((await worker.ProcessAvailableAsync("dogfood2-apply-worker")).Results);
        Assert.NotEqual(
            "resolution.invalid",
            applyResult.FailureCode);
        Assert.Equal(
            ElsaInstanceLifecycleWorkerOutcome.Queued,
            applyResult.Outcome);
        Assert.Equal(ElsaInstanceOperationState.Queued, applyResult.Operation.State);
        Assert.Equal(BuildVersion, applyResult.Instance.CurrentResolvedRelease!.Version);
        Assert.Equal(BuildDigest, applyResult.Instance.CurrentResolvedRelease.ManifestDigest);
        Assert.NotEqual(PinVersion, applyResult.Instance.CurrentResolvedRelease.Version);
        Assert.NotNull(applyResult.Instance.ResolvedPlanReference);
        Assert.NotNull(applyResult.Run);
    }

    private static ElsaInstanceIntent PinIntent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", PinVersion, "preview", previewManifestDigest: PinDigest),
        new ElsaApplicationIntent("combined"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static ElsaInstanceIntent BuildIntent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", BuildVersion, "preview", previewManifestDigest: BuildDigest),
        new ElsaApplicationIntent("combined"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static IReadOnlyList<GovernedReleaseCatalogEntry> CreateRelease(
        string releaseVersion,
        string manifestDigest,
        char digestSeed)
    {
        return
        [
            CreateEntry(releaseVersion, manifestDigest, digestSeed, "combined", "runtime-combined", ["elsa.server", "elsa.studio"]),
            CreateEntry(releaseVersion, manifestDigest, digestSeed, "server", "runtime-server", ["elsa.server"]),
            CreateEntry(releaseVersion, manifestDigest, digestSeed, "studio", "runtime-studio", ["elsa.studio"])
        ];
    }

    private static GovernedReleaseCatalogEntry CreateEntry(
        string releaseVersion,
        string manifestDigest,
        char digestSeed,
        string topologyId,
        string imageName,
        IReadOnlyList<string> runtimeKinds)
    {
        var imageDigest = "sha256:" + new string(digestSeed, 64);
        return new(
            "2.0.0",
            $"https://catalog.example.test/manifests/{releaseVersion}.json",
            manifestDigest,
            "sha256:" + new string((char)(digestSeed + 1), 64),
            $"https://catalog.example.test/signatures/{releaseVersion}.sig",
            "sha256:" + new string((char)(digestSeed + 2), 64),
            "paid",
            new(
                "valence-runtime",
                "commercial",
                "3.8",
                releaseVersion,
                "preview",
                "preview",
                "commercial",
                "https://github.com/valence-works/elsa-production-image",
                new string('a', 40),
                "run-153"),
            new(
                topologyId,
                "1",
                runtimeKinds.ToArray(),
                [],
                [new GovernedReleaseComponentVersion(topologyId, PinVersion)],
                [new GovernedReleaseComponent(
                    topologyId,
                    $"valenceruntimeimages.azurecr.io/{imageName}@{imageDigest}",
                    imageDigest,
                    new Dictionary<string, string>(),
                    runtimeKinds.Select(kind => kind.Replace("elsa.", "", StringComparison.Ordinal)).ToArray(),
                    [],
                    [],
                    null)],
                [new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.Sbom, "https://catalog.example.test/evidence/sbom", "sha256:" + new string('1', 64)),
                 new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.Provenance, "https://catalog.example.test/evidence/provenance", "sha256:" + new string('2', 64)),
                 new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.VulnerabilityScan, "https://catalog.example.test/evidence/scan", "sha256:" + new string('3', 64))]),
            "preview",
            Now,
            new(
                "central-package-declarations-v1",
                "sha256:" + new string('f', 64),
                [new("Elsa.Persistence.EFCore.SqlServer", PinVersion),
                 new("Elsa.Scheduling.Quartz.EFCore.SqlServer", PinVersion)]));
    }

    private static async Task CompleteManagedRunAsync(CatalogDbContext db, Guid operationId, Guid instanceId)
    {
        db.ChangeTracker.Clear();
        var operation = await db.ElsaInstanceOperations.SingleAsync(x => x.Id == operationId);
        operation.State = ElsaInstanceOperationState.Running;
        await db.SaveChangesAsync();
        operation.State = ElsaInstanceOperationState.Succeeded;
        operation.CompletedAt = Now.AddMinutes(1);
        var run = await db.DeploymentRuns.SingleAsync(x => x.ElsaInstanceId == instanceId);
        run.Status = WorkspaceDeploymentRunStatus.Succeeded;
        run.CompletedAt = Now.AddMinutes(1);
        var instance = await db.ElsaInstances.SingleAsync(x => x.Id == instanceId);
        instance.CurrentDeploymentId = "deployment-safe";
        instance.PlacementAssignmentId = "placement-safe";
        instance.ObservedLifecycle = ElsaObservedLifecycle.Ready;
        instance.Health = ElsaInstanceHealth.Healthy;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class EmptyCatalog : IPublicCatalogQueries
    {
        public Task<IReadOnlyList<PublicPackageProjection>> ListPackagesAsync(IReadOnlyList<Guid> sourceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicPackageProjection>> ListPackagesForWorkspaceAsync(Guid workspaceId, IReadOnlyList<Guid> sourceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicPackageProjection?> GetPackageAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicPackageProjection?> GetPackageForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublicPackageVersionProjection>> ListVersionsAsync(Guid sourceId, string packageId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PublicPackageVersionProjection>>([]);
        public Task<IReadOnlyList<PublicPackageVersionProjection>> ListVersionsForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PublicPackageVersionProjection>>([]);
        public Task<PublicPackageVersionProjection?> GetVersionAsync(Guid sourceId, string packageId, string version, CancellationToken cancellationToken = default) => Task.FromResult<PublicPackageVersionProjection?>(null);
        public Task<PublicPackageVersionProjection?> GetVersionForWorkspaceAsync(Guid workspaceId, Guid sourceId, string packageId, string version, CancellationToken cancellationToken = default) => Task.FromResult<PublicPackageVersionProjection?>(null);
        public Task<IReadOnlyList<PublicFeatureProjection>> ListFeaturesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PublicFeatureProjection?> GetFeatureAsync(string featureId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CompatibleCatalog : IPackageCompatibilityService
    {
        public Task<CompatibilityCheckResult> CheckAsync(CompatibilityCheckRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompatibilityCheckResult(true, []));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
