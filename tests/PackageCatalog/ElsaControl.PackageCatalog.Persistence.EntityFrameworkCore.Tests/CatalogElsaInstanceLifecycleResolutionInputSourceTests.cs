using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Abstractions.Catalog;
using ElsaControl.PackageCatalog.Abstractions.Compatibility;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Core.Plans;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class CatalogElsaInstanceLifecycleResolutionInputSourceTests : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, string> GovernedSecrets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = "secret://vault/database-connection",
            ["identity:signingkey"] = "secret://vault/identity-signing-key",
            ["admin:password"] = "secret://vault/admin-password"
        };
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<CatalogDbContext> _dbOptions = null!;
    private CatalogDbContext _db = null!;
    private Workspace _workspace = null!;
    private ElsaInstanceLifecycleAcceptance _accepted = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(_connection)
            .Options;
        _db = new CatalogDbContext(_dbOptions);
        await _db.Database.EnsureCreatedAsync();

        var organization = new Organization { Id = Guid.NewGuid(), Name = "Resolution test organization" };
        _workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Name = "Resolution test workspace",
            Kind = WorkspaceKind.Shared
        };
        _db.Organizations.Add(organization);
        _db.Workspaces.Add(_workspace);
        _db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
        {
            OrganizationId = organization.Id,
            ManagedHostingEnabled = true,
            SubscriptionState = OrganizationSubscriptionState.Active,
            MaxInstances = int.MaxValue,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var store = new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance);
        _accepted = await new ElsaInstanceLifecycleService(store).CreateAsync(new ElsaInstanceCreateRequest(
            organization.Id,
            _workspace.Id,
            "Managed Elsa",
            "resolution-test-elsa",
            new ElsaInstanceIntent(
                new ElsaReleaseIntent("future-runtime", "5.0", "5.0.0"),
                new ElsaApplicationIntent("combined"),
                new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
            "resolution-create",
            ActorAccountId: Guid.NewGuid()));
        _db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void Reviewed_provisioning_context_requires_a_resolved_plan_digest()
    {
        var context = new ElsaInstanceProvisioningContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "{}",
            Hash("{}"),
            PreviewDigest: Digest('a'),
            RequestDigest: Digest('b'));

        var error = Assert.Throws<ArgumentException>(() => context.Normalize());
        Assert.Equal(nameof(ElsaInstanceProvisioningContext.ResolvedPlanDigest), error.ParamName);
    }

    [Fact]
    public async Task Provisioning_context_is_bound_once_and_survives_context_reload()
    {
        var provisioned = await CreateProvisionedAsync("provisioning-context-create");

        var environment = await _db.DeploymentEnvironments
            .Include(x => x.Engines)
            .SingleAsync(x => x.Id == provisioned.EnvironmentId);
        Assert.Equal(provisioned.Accepted.Instance.Id, environment.ElsaInstanceId);
        Assert.Single(environment.Engines);
        Assert.Equal(provisioned.Accepted.Instance.Name, environment.Engines[0].Name);
        Assert.Empty(environment.Engines[0].BaseUrl);
        Assert.Equal(DeploymentHealth.Unreachable, environment.Engines[0].Health);
        Assert.Equal("pending", environment.Engines[0].CredentialProvider);
        Assert.Equal("managed", environment.Engines[0].HostingProvider);

        var resolution = await CreateSource("https://control.example.test")
            .GetAsync(provisioned.Accepted.Instance, provisioned.Accepted.Operation);
        Assert.NotNull(resolution);
        Assert.Equal("snapshot-image", resolution!.PlanRequest.BuilderIntent.Image.Slug);
        Assert.Equal(provisioned.Context.ResolvedPlanDigest, resolution!.ExpectedPlanDigest);

        await using (var restarted = new CatalogDbContext(_dbOptions))
        {
            var store = new EfCoreElsaInstanceLifecycleStore(restarted, EmptyResolutionInputSource.Instance);
            var reloaded = await store.GetProvisioningContextAsync(_workspace.Id, provisioned.Accepted.Instance.Id);
            Assert.Equal(provisioned.Context.Normalize(), reloaded);
        }

        var snapshot = await _db.ElsaInstanceProvisioningContexts
            .SingleAsync(x => x.InstanceId == provisioned.Accepted.Instance.Id);
        snapshot.ConfigurationName = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        _db.ChangeTracker.Clear();

        var mismatch = provisioned.Context with { ConfigurationName = "different" };
        var replayRequest = new ElsaInstanceCreateRequest(
            _workspace.OrganizationId,
            _workspace.Id,
            provisioned.Accepted.Instance.Name,
            provisioned.Accepted.Instance.Slug,
            provisioned.Accepted.Instance.Intent,
            "provisioning-context-create",
            provisioned.Accepted.Instance.Id,
            ProvisioningContext: mismatch);
        var conflict = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance))
                .CreateAsync(replayRequest));
        Assert.Equal(ElsaInstanceLifecycleConflictReason.IdempotencyConflict, conflict.Reason);
    }

    [Fact]
    public async Task Available_provisioning_targets_exclude_every_occupied_environment_state()
    {
        var now = DateTimeOffset.UtcNow;
        var owner = await new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance))
            .CreateAsync(new ElsaInstanceCreateRequest(
                _workspace.OrganizationId,
                _workspace.Id,
                "Availability owner",
                "availability-owner",
                _accepted.Instance.Intent,
                "availability-owner-create",
                ActorAccountId: null));
        _db.ChangeTracker.Clear();

        var targetIds = Enumerable.Range(0, 8)
            .Select(_ => (ApplicationId: Guid.NewGuid(), EnvironmentId: Guid.NewGuid()))
            .ToArray();
        _db.DeploymentApplications.AddRange(targetIds.Select((target, index) => new DeploymentApplicationEntity
        {
            Id = target.ApplicationId,
            WorkspaceId = _workspace.Id,
            Name = $"Availability application {index}",
            CreatedAt = now,
            UpdatedAt = now
        }));
        var environments = targetIds.Select((target, index) => new DeploymentEnvironmentEntity
        {
            Id = target.EnvironmentId,
            WorkspaceId = _workspace.Id,
            ApplicationId = target.ApplicationId,
            Name = $"Availability environment {index}",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        _db.DeploymentEnvironments.AddRange(environments);

        // Each of these rows is independently ineligible. Keep the setup in one
        // fixture so the query cannot accidentally omit one of the acceptance
        // guards while still passing a single occupied-state example.
        environments[0].ElsaInstanceId = owner.Instance.Id;
        environments[1].DesiredRevisionId = Guid.NewGuid();
        environments[2].DeployedRevisionId = Guid.NewGuid();
        _db.DesiredStateRevisions.Add(new DesiredStateRevisionEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspace.Id,
            ApplicationId = environments[3].ApplicationId,
            EnvironmentId = environments[3].Id,
            RevisionNumber = 1,
            Label = "occupied revision",
            ContentHash = new string('a', 64),
            DesiredStateJson = "{}",
            AuthoredAt = now,
            CreatedAt = now
        });
        _db.ObservabilityBindings.Add(new ObservabilityBindingEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspace.Id,
            EnvironmentId = environments[4].Id,
            Kind = ObservabilityBindingKind.Logs,
            Provider = "test",
            Status = ObservabilityBindingStatus.Connected,
            Scope = "*"
        });
        _db.DriftReportItems.Add(new DriftReportItemEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspace.Id,
            EnvironmentId = environments[5].Id,
            EngineId = Guid.NewGuid(),
            Area = "engine",
            Desired = "healthy",
            Observed = "unknown",
            Action = DriftAction.Review,
            DetectedAt = now
        });
        _db.WorkflowEngines.Add(new WorkflowEngineEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspace.Id,
            EnvironmentId = environments[6].Id,
            Name = "occupied engine",
            BaseUrl = "",
            CertificateStatus = CertificateStatus.Untrusted,
            CredentialProvider = "pending",
            CredentialReference = "pending",
            CredentialAssignmentStatus = EngineCredentialAssignmentStatus.Deferred,
            CredentialVerificationStatus = CredentialVerificationStatus.NotVerifiable,
            Health = DeploymentHealth.Unreachable,
            VerificationMessage = "Awaiting provider endpoint observation.",
            HostingProvider = "managed",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var targets = await new EfCoreEngineProvisioningTargetStore(_db)
            .GetAvailableTargetsAsync(_workspace.Id);
        var occupiedEnvironmentIds = targetIds
            .Take(7)
            .Select(x => x.EnvironmentId)
            .ToHashSet();

        Assert.Contains(targets, x => x.ApplicationId == targetIds[7].ApplicationId &&
                                      x.EnvironmentId == targetIds[7].EnvironmentId);
        Assert.DoesNotContain(targets, x => occupiedEnvironmentIds.Contains(x.EnvironmentId));
    }

    [Fact]
    public async Task Available_provisioning_targets_are_scoped_to_the_requested_workspace()
    {
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Foreign target organization" };
        var foreignWorkspace = new Workspace
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Name = "Foreign target workspace",
            Kind = WorkspaceKind.Shared
        };
        var applicationId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _db.Organizations.Add(organization);
        _db.Workspaces.Add(foreignWorkspace);
        _db.DeploymentApplications.Add(new DeploymentApplicationEntity
        {
            Id = applicationId,
            WorkspaceId = foreignWorkspace.Id,
            Name = "Foreign application",
            CreatedAt = now,
            UpdatedAt = now
        });
        _db.DeploymentEnvironments.Add(new DeploymentEnvironmentEntity
        {
            Id = environmentId,
            WorkspaceId = foreignWorkspace.Id,
            ApplicationId = applicationId,
            Name = "Foreign environment",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var localTargets = await new EfCoreEngineProvisioningTargetStore(_db)
            .GetAvailableTargetsAsync(_workspace.Id);
        var foreignTargets = await new EfCoreEngineProvisioningTargetStore(_db)
            .GetAvailableTargetsAsync(foreignWorkspace.Id);

        Assert.DoesNotContain(localTargets, x => x.EnvironmentId == environmentId);
        Assert.Contains(foreignTargets, x => x.ApplicationId == applicationId && x.EnvironmentId == environmentId);
    }

    [Fact]
    public async Task Missing_provisioning_snapshot_for_marked_instance_fails_closed()
    {
        var provisioned = await CreateProvisionedAsync("provisioning-context-missing");
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM ElsaInstanceProvisioningContexts WHERE InstanceId = {provisioned.Accepted.Instance.Id}");
        _db.ChangeTracker.Clear();

        var result = await CreateSource("https://control.example.test")
            .GetAsync(provisioned.Accepted.Instance, provisioned.Accepted.Operation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Occupied_target_rolls_back_instance_and_snapshot_creation()
    {
        var applicationId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var occupiedInstance = await new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance))
            .CreateAsync(new ElsaInstanceCreateRequest(
                _workspace.OrganizationId,
                _workspace.Id,
                "Occupied target owner",
                "occupied-target-owner",
                _accepted.Instance.Intent,
                "occupied-target-owner-create",
                ActorAccountId: null));
        _db.ChangeTracker.Clear();
        _db.DeploymentApplications.Add(new DeploymentApplicationEntity
        {
            Id = applicationId,
            WorkspaceId = _workspace.Id,
            Name = "Occupied target application",
            CreatedAt = now,
            UpdatedAt = now
        });
        var occupiedInstanceId = occupiedInstance.Instance.Id;
        _db.DeploymentEnvironments.Add(new DeploymentEnvironmentEntity
        {
            Id = environmentId,
            WorkspaceId = _workspace.Id,
            ApplicationId = applicationId,
            ElsaInstanceId = occupiedInstanceId,
            Name = "Occupied target",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var before = await _db.ElsaInstances.CountAsync();
        var context = ProvisioningContext(applicationId, environmentId, "occupied");
        var request = new ElsaInstanceCreateRequest(
            _workspace.OrganizationId,
            _workspace.Id,
            "Occupied target instance",
            "occupied-target-instance",
            _accepted.Instance.Intent,
            "occupied-target-create",
            ProvisioningContext: context);

        await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(() =>
            new ElsaInstanceLifecycleService(new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance))
                .CreateAsync(request));

        Assert.Equal(before, await _db.ElsaInstances.CountAsync());
        Assert.Equal(0, await _db.ElsaInstanceProvisioningContexts.CountAsync(
            x => x.EnvironmentId == environmentId));
        var environment = await _db.DeploymentEnvironments
            .Include(x => x.Engines)
            .SingleAsync(x => x.Id == environmentId);
        Assert.Equal(occupiedInstanceId, environment.ElsaInstanceId);
        Assert.Empty(environment.Engines);
    }

    [Fact]
    public async Task Multiple_engines_for_a_managed_environment_fail_closed()
    {
        var environment = await _db.DeploymentEnvironments
            .Include(x => x.Engines)
            .SingleAsync(x => x.ElsaInstanceId == _accepted.Instance.Id);
        var now = DateTimeOffset.UtcNow;
        _db.WorkflowEngines.Add(new WorkflowEngineEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspace.Id,
            EnvironmentId = environment.Id,
            Name = "ambiguous",
            BaseUrl = "https://managed.invalid/",
            CertificateStatus = CertificateStatus.Untrusted,
            CredentialProvider = "provider-managed",
            CredentialReference = "managed-instance:ambiguous",
            CredentialAssignmentStatus = EngineCredentialAssignmentStatus.Deferred,
            CredentialVerificationStatus = CredentialVerificationStatus.NotVerifiable,
            Health = DeploymentHealth.Unreachable,
            VerificationMessage = "The managed provider has not established a healthy endpoint.",
            HostingProvider = "managed",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();

        var source = CreateSource("https://control.example.test");
        var result = await source.GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Multiple_acceptance_audits_for_an_operation_fail_closed()
    {
        var accepted = await _db.ElsaInstanceAuditEvents
            .AsNoTracking()
            .SingleAsync(x => x.OperationId == _accepted.Operation.Id && x.EventType == "lifecycle.accepted");
        _db.ElsaInstanceAuditEvents.Add(new ElsaInstanceAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = accepted.OrganizationId,
            WorkspaceId = accepted.WorkspaceId,
            InstanceId = accepted.InstanceId,
            Sequence = accepted.Sequence + 1,
            EventType = accepted.EventType,
            ActorAccountId = Guid.NewGuid(),
            OperationId = accepted.OperationId,
            PriorState = accepted.PriorState,
            NewState = accepted.NewState,
            DesiredStateRevisionId = accepted.DesiredStateRevisionId,
            PlanReference = accepted.PlanReference,
            DiagnosticCode = accepted.DiagnosticCode,
            Summary = accepted.Summary,
            RequestKeyHash = accepted.RequestKeyHash,
            OccurredAt = accepted.OccurredAt.AddSeconds(1)
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await CreateSource("https://control.example.test").GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Missing_environment_desired_revision_uses_the_managed_shell_revision_purpose()
    {
        var environment = await _db.DeploymentEnvironments
            .SingleAsync(x => x.ElsaInstanceId == _accepted.Instance.Id);
        environment.DesiredRevisionId = null;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await CreateSource("https://control.example.test").GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.NotNull(result);
        Assert.Equal(
            DeterministicGuid(_accepted.Instance.Id, "desired-revision"),
            result!.DeploymentTarget.SourceRevisionId);
        Assert.NotEqual(DeterministicGuid(_accepted.Instance.Id, "revision"), result.DeploymentTarget.SourceRevisionId);
    }

    [Fact]
    public async Task Resolved_plan_uri_uses_the_configured_control_plane_origin()
    {
        var source = CreateSource("https://control.example.test/");

        var result = await source.GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.NotNull(result);
        Assert.Equal(
            $"https://control.example.test/api/workspaces/{_workspace.Id:D}/instances/{_accepted.Instance.Id:D}/resolved-plans/release-cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            result!.PlanRequest.PlanUri);
    }

    [Fact]
    public async Task Missing_control_plane_origin_fails_closed_before_resolution()
    {
        var source = CreateSource(null);

        var result = await source.GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Catalog_entries_outside_supported_lifecycle_fail_closed()
    {
        var source = new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("preview")),
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);

        var result = await source.GetAsync(_accepted.Instance, _accepted.Operation);

        Assert.Null(result);
    }

    [Fact]
    public async Task Explicit_preview_consent_resolves_a_matching_preview_or_promoted_entry()
    {
        var consented = ConsentInstance();
        var source = new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("preview")),
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);

        var result = await source.GetAsync(consented, _accepted.Operation);

        Assert.NotNull(result);
        Assert.Equal("sha256:" + new string('c', 64), result!.PlanRequest.ReleaseManifest.Digest);

        var promoted = new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("supported")),
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);
        Assert.NotNull(await promoted.GetAsync(consented, _accepted.Operation));
    }

    [Fact]
    public async Task Preview_consent_rejects_digest_mismatch_ambiguity_and_maintenance_lifecycle()
    {
        var consented = ConsentInstance();
        var authority = new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" };

        var mismatch = await new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("preview") with { ManifestDigest = "sha256:" + new string('d', 64) }),
            authority,
            GovernedSecrets).GetAsync(consented, _accepted.Operation);
        Assert.Null(mismatch);

        var ambiguity = await new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("preview"), CreateEntry("preview") with { ManifestDigest = "sha256:" + new string('d', 64) }),
            authority,
            GovernedSecrets).GetAsync(consented, _accepted.Operation);
        Assert.Null(ambiguity);

        var maintenance = await new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("maintenance")),
            authority,
            GovernedSecrets).GetAsync(consented, _accepted.Operation);
        Assert.Null(maintenance);

        var crossLifecycleAmbiguity = await new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            new StaticCatalog(CreateEntry("supported"), CreateEntry("preview")),
            authority,
            GovernedSecrets).GetAsync(consented, _accepted.Operation);
        Assert.Null(crossLifecycleAmbiguity);
    }

    [Fact]
    public async Task Preview_build_suffix_matches_the_exact_catalog_row_and_resolves_a_plan()
    {
        const string pinVersion = "3.8.0-preview.5567";
        const string buildVersion = "3.8.0-preview.5567-build.153";
        var pin = CreateEntry("preview", "3.8", pinVersion, "preview", digestSeed: 'c', componentVersion: pinVersion);
        var build = CreateEntry("preview", "3.8", buildVersion, "preview", digestSeed: 'd', componentVersion: pinVersion);
        var catalog = new GovernedReleaseCatalogStore(_dbOptions);
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, (await catalog.StoreAsync([pin])).Status);
        Assert.Equal(GovernedReleaseCatalogWriteStatus.Stored, (await catalog.StoreAsync([build])).Status);

        var pinMatches = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: "future-runtime",
            ReleaseLine: "3.8",
            ReleaseVersion: pinVersion,
            Channel: "preview",
            RegistryClass: "paid",
            TopologyId: "combined"));
        var buildMatches = await catalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: "future-runtime",
            ReleaseLine: "3.8",
            ReleaseVersion: buildVersion,
            Channel: "preview",
            RegistryClass: "paid",
            TopologyId: "combined"));
        Assert.Equal(pinVersion, Assert.Single(pinMatches).Distribution.ReleaseVersion);
        Assert.Equal(buildVersion, Assert.Single(buildMatches).Distribution.ReleaseVersion);
        Assert.NotEqual(pinMatches[0].ManifestDigest, buildMatches[0].ManifestDigest);

        var instance = new ElsaInstance(
            _accepted.Instance.Id,
            _accepted.Instance.OrganizationId,
            _accepted.Instance.WorkspaceId,
            _accepted.Instance.Name,
            _accepted.Instance.Slug,
            _accepted.Instance.Intent with
            {
                Release = new ElsaReleaseIntent(
                    "future-runtime",
                    "3.8",
                    buildVersion,
                    "preview",
                    previewManifestDigest: "sha256:" + new string('d', 64))
            });
        var source = new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            catalog,
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);

        var input = await source.GetAsync(instance, _accepted.Operation);

        Assert.NotNull(input);
        Assert.Equal(buildVersion, input!.PlanRequest.ReleaseManifest.Manifest!.Distribution.ReleaseVersion);
        Assert.Equal(buildVersion, input.PlanRequest.InstanceIntent.Release.RequestedVersion);
        Assert.NotEqual(pinVersion, input.PlanRequest.ReleaseManifest.Manifest.Distribution.ReleaseVersion);

        var resolved = await new ElsaInstancePlanResolver(
                new EmptyCatalog(),
                new CompatibleCatalog(),
                new(DefaultEgress: "unrestricted"))
            .ResolveAsync(input.PlanRequest);

        Assert.True(resolved.Succeeded, string.Join("; ", resolved.Findings.Select(x => x.Code)));
        Assert.Equal(buildVersion, resolved.Plan!.Release.Version);
        Assert.Equal(buildVersion, resolved.CurrentResolvedRelease!.Version);
        Assert.NotEqual(pinVersion, resolved.Plan.Release.Version);
        Assert.Equal(pinVersion, resolved.Plan.Release.ComponentDeclarations!.Packages[0].Version);
    }

    [Fact]
    public async Task Catalog_release_resolves_and_creates_a_durable_Azure_provider_operation()
    {
        var catalog = new GovernedReleaseCatalogStore(_dbOptions);
        Assert.Equal(
            GovernedReleaseCatalogWriteStatus.Stored,
            (await catalog.StoreAsync([CreateEntry()])).Status);
        var source = new CatalogElsaInstanceLifecycleResolutionInputSource(
            _db,
            catalog,
            new ElsaInstancePlanAuthorityOptions { Origin = "https://control.example.test" },
            GovernedSecrets);
        var input = await source.GetAsync(_accepted.Instance, _accepted.Operation);
        Assert.NotNull(input);
        Assert.Equal(
            ["Elsa.Persistence.EFCore.SqlServer", "Elsa.Scheduling.Quartz.EFCore.SqlServer"],
            input!.PlanRequest.ReleaseManifest.Manifest!.ComponentDeclarations!.Packages.Select(package => package.Id).ToArray());

        var resolved = await new ElsaInstancePlanResolver(
                new EmptyCatalog(),
                new CompatibleCatalog(),
                new(DefaultEgress: "unrestricted"))
            .ResolveAsync(input.PlanRequest);
        Assert.True(resolved.Succeeded, string.Join("; ", resolved.Findings.Select(x => x.Code)));

        var operationStore = new AzureProviderOperationStore(_db);
        var provider = new AzureElsaInstanceProvider(
            new AzureProviderOperationService(operationStore),
            operationStore,
            operationStore,
            options:
            new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('b', 64),
                ProviderScopeFingerprint = new string('a', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            });

        var submission = await provider.SubmitAsync(new(
            _workspace.Id,
            _accepted.Instance.Id,
            _accepted.Operation.Id,
            1,
            ElsaDesiredLifecycle.Running,
            resolved.Plan!,
            input.DeploymentTarget,
            "westeurope",
            _accepted.Instance.OrganizationId,
            ElsaInstanceOperationAction.Reconcile));

        var persisted = await _db.AzureProviderOperations.AsNoTracking().SingleAsync();
        Assert.False(submission.Replayed);
        Assert.Equal("5.0.0", persisted.SqlWorkflowPackageVersion);
        Assert.Equal("5.0.0", persisted.SqlQuartzPackageVersion);
        Assert.Equal("valenceruntimeimages.azurecr.io/runtime-combined", persisted.ImageRepository);
        Assert.Equal(3, resolved.Plan!.Configuration.Entries.Count(entry => entry.Secret));
        Assert.Equal(2, resolved.Plan.Release.ComponentDeclarations!.Packages.Count);
    }

    private async Task<(ElsaInstanceLifecycleAcceptance Accepted, ElsaInstanceProvisioningContext Context,
        Guid ApplicationId, Guid EnvironmentId)> CreateProvisionedAsync(string idempotencyKey)
    {
        var applicationId = Guid.NewGuid();
        var environmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _db.DeploymentApplications.Add(new DeploymentApplicationEntity
        {
            Id = applicationId,
            WorkspaceId = _workspace.Id,
            Name = $"Provisioning application {applicationId:N}",
            CreatedAt = now,
            UpdatedAt = now
        });
        _db.DeploymentEnvironments.Add(new DeploymentEnvironmentEntity
        {
            Id = environmentId,
            WorkspaceId = _workspace.Id,
            ApplicationId = applicationId,
            Name = "Provisioning environment",
            Tier = EnvironmentTier.Production,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var context = ProvisioningContext(applicationId, environmentId, idempotencyKey);
        var accepted = await new ElsaInstanceLifecycleService(
                new EfCoreElsaInstanceLifecycleStore(_db, EmptyResolutionInputSource.Instance))
            .CreateAsync(new ElsaInstanceCreateRequest(
                _workspace.OrganizationId,
                _workspace.Id,
                "Provisioned Elsa",
                $"provisioned-{idempotencyKey}",
                _accepted.Instance.Intent,
                idempotencyKey,
                ActorAccountId: Guid.NewGuid(),
                ProvisioningContext: context));
        _db.ChangeTracker.Clear();
        return (accepted, context, applicationId, environmentId);
    }

    private static ElsaInstanceProvisioningContext ProvisioningContext(
        Guid applicationId,
        Guid environmentId,
        string seed)
    {
        var builderIntent = new RuntimeBuilderIntent(
            new RuntimeImageSelection("snapshot-image", null, null, null),
            [], [], [], null);
        var json = RuntimeConfigurationService.SerializeIntent(builderIntent);
        return new ElsaInstanceProvisioningContext(
            applicationId,
            environmentId,
            json,
            Hash(json),
            ConfigurationName: $"Configuration {seed}",
            PreviewDigest: Digest('a'),
            RequestDigest: Digest('b'),
            ResolvedPlanDigest: Digest('d'));
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private CatalogElsaInstanceLifecycleResolutionInputSource CreateSource(string? origin) =>
        new(_db, new StaticCatalog(CreateEntry()), new ElsaInstancePlanAuthorityOptions { Origin = origin }, GovernedSecrets);

    private ElsaInstance ConsentInstance() => new(
        _accepted.Instance.Id,
        _accepted.Instance.OrganizationId,
        _accepted.Instance.WorkspaceId,
        _accepted.Instance.Name,
        _accepted.Instance.Slug,
        _accepted.Instance.Intent with
        {
            Release = new ElsaReleaseIntent(
                "future-runtime", "5.0", "5.0.0", previewManifestDigest: "sha256:" + new string('c', 64))
        });

    private GovernedReleaseCatalogEntry CreateEntry(
        string catalogLifecycle = "supported",
        string releaseLine = "5.0",
        string releaseVersion = "5.0.0",
        string channel = "stable",
        char digestSeed = 'c',
        string? componentVersion = null)
    {
        var bakedVersion = componentVersion ?? releaseVersion;
        return new(
            "2.0.0",
            $"https://catalog.example.test/manifests/{releaseVersion}.json",
            "sha256:" + new string(digestSeed, 64),
            "sha256:" + new string((char)(digestSeed + 1), 64),
            $"https://catalog.example.test/signatures/{releaseVersion}.sig",
            "sha256:" + new string((char)(digestSeed + 2), 64),
            "paid",
            new(
                "future-runtime",
                "commercial",
                releaseLine,
                releaseVersion,
                channel,
                channel,
                "commercial",
                "https://github.com/example/runtime",
                new string('a', 40),
                "run-1"),
            new(
                "combined",
                "1",
                ["elsa.server"],
                [],
                [new GovernedReleaseComponentVersion("server", bakedVersion)],
                [new GovernedReleaseComponent(
                    "server",
                    "valenceruntimeimages.azurecr.io/runtime-combined@sha256:" + new string('a', 64),
                    "sha256:" + new string('a', 64),
                    new Dictionary<string, string>(),
                    ["server"],
                    [],
                    [],
                    null)],
                [new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.Sbom, "https://catalog.example.test/evidence/sbom", "sha256:" + new string('1', 64)),
                 new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.Provenance, "https://catalog.example.test/evidence/provenance", "sha256:" + new string('2', 64)),
                 new GovernedReleaseEvidence(ReleaseManifestEvidenceKinds.VulnerabilityScan, "https://catalog.example.test/evidence/scan", "sha256:" + new string('3', 64))]),
            catalogLifecycle,
            DateTimeOffset.UtcNow,
            new(
                "central-package-declarations-v1",
                "sha256:" + new string('f', 64),
                [new("Elsa.Persistence.EFCore.SqlServer", bakedVersion),
                 new("Elsa.Scheduling.Quartz.EFCore.SqlServer", bakedVersion)]));
    }

    private static Guid DeterministicGuid(Guid seed, string purpose)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"elsa-control:{purpose}:{seed:D}"));
        return new Guid(bytes[..16]);
    }

    private sealed class StaticCatalog(params GovernedReleaseCatalogEntry[] entries) : IGovernedReleaseCatalogStore
    {
        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>(
                entries
                    .Where(entry => query.CatalogLifecycle is null ||
                                    string.Equals(query.CatalogLifecycle, entry.CatalogLifecycle, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
    }

    private sealed class EmptyResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public static EmptyResolutionInputSource Instance { get; } = new();

        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
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
}
