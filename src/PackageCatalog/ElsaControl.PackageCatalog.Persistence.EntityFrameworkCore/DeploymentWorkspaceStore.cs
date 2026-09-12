using System.Data;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Artifacts;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class DeploymentWorkspaceStore(CatalogDbContext dbContext) : IWorkspaceDeploymentStore, IWorkspacePermissionStore, IWorkspaceDeploymentMutationStore, IWorkspaceDeploymentCommandStore, IWorkspaceArtifactStore, IWorkspaceArtifactUploadStore, IWorkspaceDeploymentTierStore
{
    public async Task<DeploymentCockpit> GetCockpitAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);

        var workspaceName = await dbContext.Workspaces
            .AsNoTracking()
            .Where(x => x.Id == workspaceId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken) ?? $"Workspace {workspaceId:N}";

        var applications = await dbContext.DeploymentApplications
            .AsNoTracking()
            .Include(x => x.Environments)
                .ThenInclude(x => x.Revisions)
            .Include(x => x.Environments)
                .ThenInclude(x => x.TierDefinition)
                    .ThenInclude(x => x!.Capabilities)
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var engines = await dbContext.WorkflowEngines
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .Include(x => x.Controls)
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var observabilityBindings = await dbContext.ObservabilityBindings
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.Provider)
            .ThenBy(x => x.Kind)
            .ToListAsync(cancellationToken);

        var driftReport = await dbContext.DriftReportItems
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.DetectedAt)
            .ToListAsync(cancellationToken);

        var deploymentRuns = await dbContext.DeploymentRuns
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        var runRevisionIds = deploymentRuns
            .SelectMany(x => new[] { x.SourceRevisionId, x.PreviousDeployedRevisionId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var runIds = deploymentRuns.Select(x => x.Id).ToList();
        var runRevisions = runRevisionIds.Count == 0
            ? new Dictionary<Guid, DesiredStateRevisionEntity>()
            : await dbContext.DesiredStateRevisions
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && runRevisionIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
        var runCommands = runIds.Count == 0
            ? new Dictionary<Guid, List<DeploymentRunCommandSummary>>()
            : (await dbContext.DeploymentCommands
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && runIds.Contains(x.RunId))
                .OrderBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken))
            .Select(ToDeploymentRunCommandSummary)
            .GroupBy(x => x.RunId)
            .ToDictionary(x => x.Key, x => x.ToList());

        var cockpitApplications = applications
            .Select(application => new WorkflowApplication(
                application.Id.ToString("D"),
                application.Name,
                workspaceName,
                application.Environments
                    .OrderBy(x => x.Tier)
                    .ThenBy(x => x.Name)
                    .Select(environment => ToEnvironmentSummary(environment, engines))
                    .ToList()))
            .ToList();

        return new DeploymentCockpit(
            cockpitApplications,
            engines.Select(ToEngineRegistration).ToList(),
            [],
            observabilityBindings.Select(ToObservabilityBinding).ToList(),
            deploymentRuns.Select(run => ToDeploymentHistoryEvent(run, runRevisions, runCommands)).ToList(),
            driftReport.Select(ToDriftReportItem).ToList(),
            []);
    }

    public async Task<IReadOnlyList<WorkspaceDeploymentTier>> ListTiersAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        var tiers = await dbContext.DeploymentTierDefinitions
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .Where(x => x.WorkspaceId == workspaceId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var counts = await dbContext.DeploymentEnvironments
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.TierId != null)
            .GroupBy(x => x.TierId!.Value)
            .Select(x => new { TierId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.TierId, x => x.Count, cancellationToken);

        return tiers.Select(x => ToWorkspaceDeploymentTier(x, counts.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<WorkspaceDeploymentTier?> GetTierAsync(
        Guid workspaceId,
        Guid tierId,
        CancellationToken cancellationToken = default)
    {
        var tier = await dbContext.DeploymentTierDefinitions
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == tierId, cancellationToken);
        if (tier is null)
            return null;

        var count = await dbContext.DeploymentEnvironments.CountAsync(x => x.WorkspaceId == workspaceId && x.TierId == tierId, cancellationToken);
        return ToWorkspaceDeploymentTier(tier, count);
    }

    public async Task<WorkspaceDeploymentTier> CreateTierAsync(
        Guid workspaceId,
        CreateDeploymentTierRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        if (await ActiveTierNameExistsAsync(workspaceId, request.Name, null, cancellationToken))
            throw new InvalidOperationException("An active deployment tier with the same name already exists in this workspace.");

        var now = DateTimeOffset.UtcNow;
        var tierId = Guid.NewGuid();
        var tier = new DeploymentTierDefinitionEntity
        {
            Id = tierId,
            WorkspaceId = workspaceId,
            Name = request.Name.Trim(),
            Description = request.Description,
            SortOrder = request.SortOrder,
            IsDefault = false,
            Status = DeploymentTierStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByAccountId = request.ActorAccountId,
            UpdatedByAccountId = request.ActorAccountId,
            Capabilities = CreateCapabilityAssignments(workspaceId, request.Capabilities, request.ActorAccountId, now, tierId)
        };
        tier.Changes.Add(Change(workspaceId, tier.Id, request.ActorAccountId, "Created", $"Created deployment tier '{tier.Name}'.", now, 0));

        await dbContext.DeploymentTierDefinitions.AddAsync(tier, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentTier(tier, 0);
    }

    public async Task<WorkspaceDeploymentTier> UpdateTierAsync(
        Guid workspaceId,
        Guid tierId,
        UpdateDeploymentTierRequest request,
        DeploymentTierImpactSummary impact,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        var tier = await dbContext.DeploymentTierDefinitions
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == tierId, cancellationToken);
        if (tier is null)
            throw new KeyNotFoundException("Deployment tier does not exist in the workspace.");
        if (tier.Status == DeploymentTierStatus.Active && await ActiveTierNameExistsAsync(workspaceId, request.Name, tierId, cancellationToken))
            throw new InvalidOperationException("An active deployment tier with the same name already exists in this workspace.");

        var now = DateTimeOffset.UtcNow;
        tier.Name = request.Name.Trim();
        tier.Description = request.Description;
        tier.SortOrder = request.SortOrder;
        tier.UpdatedAt = now;
        tier.UpdatedByAccountId = request.ActorAccountId;

        await dbContext.DeploymentTierCapabilityAssignments
            .Where(x => x.WorkspaceId == workspaceId && x.TierId == tier.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.DeploymentTierCapabilityAssignments.AddRangeAsync(
            CreateCapabilityAssignments(workspaceId, request.Capabilities, request.ActorAccountId, now, tier.Id),
            cancellationToken);
        await dbContext.DeploymentTierChangeRecords.AddAsync(Change(
            workspaceId,
            tier.Id,
            request.ActorAccountId,
            "Updated",
            $"Updated deployment tier '{tier.Name}'.",
            now,
            impact.AffectedEnvironmentCount), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetTierAsync(workspaceId, tier.Id, cancellationToken)
            ?? throw new KeyNotFoundException("Deployment tier does not exist in the workspace.");
    }

    public async Task<WorkspaceDeploymentTier> ArchiveTierAsync(
        Guid workspaceId,
        Guid tierId,
        ArchiveDeploymentTierRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        var tier = await dbContext.DeploymentTierDefinitions
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == tierId, cancellationToken);
        if (tier is null)
            throw new KeyNotFoundException("Deployment tier does not exist in the workspace.");

        var activeCount = await dbContext.DeploymentTierDefinitions.CountAsync(x => x.WorkspaceId == workspaceId && x.Status == DeploymentTierStatus.Active, cancellationToken);
        if (tier.Status == DeploymentTierStatus.Active && activeCount <= 1)
            throw new InvalidOperationException("At least one active deployment tier is required.");

        var now = DateTimeOffset.UtcNow;
        tier.Status = DeploymentTierStatus.Archived;
        tier.ArchivedAt = now;
        tier.ArchivedByAccountId = request.ActorAccountId;
        tier.UpdatedAt = now;
        tier.UpdatedByAccountId = request.ActorAccountId;
        var environmentCount = await dbContext.DeploymentEnvironments.CountAsync(x => x.WorkspaceId == workspaceId && x.TierId == tierId, cancellationToken);
        await dbContext.DeploymentTierChangeRecords.AddAsync(Change(workspaceId, tier.Id, request.ActorAccountId, "Archived", $"Archived deployment tier '{tier.Name}'.", now, environmentCount), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentTier(tier, environmentCount);
    }

    public async Task<WorkspaceDeploymentTier> RestoreTierAsync(
        Guid workspaceId,
        Guid tierId,
        RestoreDeploymentTierRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        var tier = await dbContext.DeploymentTierDefinitions
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == tierId, cancellationToken);
        if (tier is null)
            throw new KeyNotFoundException("Deployment tier does not exist in the workspace.");
        if (await ActiveTierNameExistsAsync(workspaceId, tier.Name, tierId, cancellationToken))
            throw new InvalidOperationException("Another active deployment tier with the same name already exists in this workspace.");

        var now = DateTimeOffset.UtcNow;
        tier.Status = DeploymentTierStatus.Active;
        tier.ArchivedAt = null;
        tier.ArchivedByAccountId = null;
        tier.UpdatedAt = now;
        tier.UpdatedByAccountId = request.ActorAccountId;
        var environmentCount = await dbContext.DeploymentEnvironments.CountAsync(x => x.WorkspaceId == workspaceId && x.TierId == tierId, cancellationToken);
        await dbContext.DeploymentTierChangeRecords.AddAsync(Change(workspaceId, tier.Id, request.ActorAccountId, "Restored", $"Restored deployment tier '{tier.Name}'.", now, environmentCount), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentTier(tier, environmentCount);
    }

    public async Task<DeploymentTierImpactSummary> PreviewTierImpactAsync(
        Guid workspaceId,
        Guid tierId,
        IReadOnlyList<string> proposedCapabilities,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, cancellationToken: cancellationToken);
        var tier = await dbContext.DeploymentTierDefinitions
            .AsNoTracking()
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == tierId, cancellationToken);
        if (tier is null)
            throw new KeyNotFoundException("Deployment tier does not exist in the workspace.");

        var currentCapabilities = tier.Capabilities.Select(x => x.CapabilityId).Order(StringComparer.Ordinal).ToList();
        var proposed = DeploymentTierService.NormalizeCapabilities(proposedCapabilities);
        var added = proposed.Except(currentCapabilities, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var removed = currentCapabilities.Except(proposed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var samples = await dbContext.DeploymentEnvironments
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.TierId == tierId)
            .OrderBy(x => x.Name)
            .Take(5)
            .Select(x => new DeploymentTierEnvironmentSample(x.ApplicationId, x.Application!.Name, x.Id, x.Name))
            .ToListAsync(cancellationToken);
        var environmentCount = await dbContext.DeploymentEnvironments.CountAsync(x => x.WorkspaceId == workspaceId && x.TierId == tierId, cancellationToken);

        return new DeploymentTierImpactSummary(
            tierId,
            currentCapabilities,
            proposed,
            added,
            removed,
            environmentCount,
            samples,
            DeploymentTierService.ChangedSafeguards(added, removed));
    }

    public async Task<IReadOnlyList<WorkspaceDeploymentTier>> EnsureDefaultTiersAsync(
        Guid workspaceId,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        await NormalizeSqliteDeploymentTierIdsAsync(workspaceId, cancellationToken);

        var existing = await dbContext.DeploymentTierDefinitions
            .Include(x => x.Capabilities)
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        if (existing.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            var defaults = Enum.GetValues<EnvironmentTier>().Select((tier, index) =>
            {
                var tierId = Guid.NewGuid();
                var entity = new DeploymentTierDefinitionEntity
                {
                    Id = tierId,
                    WorkspaceId = workspaceId,
                    Name = tier.ToString(),
                    Description = $"Default {tier} deployment tier.",
                    SortOrder = (index + 1) * 10,
                    IsDefault = true,
                    Status = DeploymentTierStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedByAccountId = actorAccountId,
                    UpdatedByAccountId = actorAccountId,
                    Capabilities = CreateCapabilityAssignments(workspaceId, DeploymentTierService.DefaultCapabilitiesByLegacyTier[tier], actorAccountId, now, tierId)
                };
                entity.Changes.Add(Change(workspaceId, entity.Id, actorAccountId, "Created", $"Created default deployment tier '{entity.Name}'.", now, 0));
                return entity;
            }).ToList();

            await dbContext.DeploymentTierDefinitions.AddRangeAsync(defaults, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            existing = defaults;
        }

        await AssignMissingEnvironmentTierIdsAsync(workspaceId, existing, cancellationToken);
        return existing
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => ToWorkspaceDeploymentTier(x, x.Environments.Count))
            .ToList();
    }

    private async Task NormalizeSqliteDeploymentTierIdsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            return;

        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE DeploymentTierCapabilityAssignments
                SET TierId = upper(TierId)
                WHERE lower(WorkspaceId) = lower({workspaceId})
                  AND TierId <> upper(TierId)
                """, cancellationToken);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE DeploymentTierChangeRecords
                SET TierId = upper(TierId)
                WHERE lower(WorkspaceId) = lower({workspaceId})
                  AND TierId <> upper(TierId)
                """, cancellationToken);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE DeploymentEnvironments
                SET TierId = upper(TierId)
                WHERE lower(WorkspaceId) = lower({workspaceId})
                  AND TierId IS NOT NULL
                  AND TierId <> upper(TierId)
                """, cancellationToken);
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE DeploymentTierDefinitions
                SET Id = upper(Id)
                WHERE lower(WorkspaceId) = lower({workspaceId})
                  AND Id <> upper(Id)
                """, cancellationToken);
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);
        }
    }

    public Task<DateTimeOffset?> GetWorkspaceMembershipCreatedAtAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        dbContext.WorkspaceMemberships.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.AccountId == accountId)
            .Select(x => (DateTimeOffset?)x.CreatedAt)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkspacePermissionGrant>> GetPermissionGrantsAsync(
        Guid workspaceId,
        Guid accountId,
        CancellationToken cancellationToken = default)
        => await ListPermissionGrantsAsync(workspaceId, accountId, cancellationToken);

    public async Task<IReadOnlyList<WorkspacePermissionGrant>> ListPermissionGrantsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.WorkspacePermissionGrants
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (!accountId.HasValue || x.AccountId == accountId.Value))
            .OrderBy(x => x.AccountId)
            .ThenBy(x => x.Permission)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new WorkspacePermissionGrant(
                x.Id,
                x.WorkspaceId,
                x.AccountId,
                x.Permission,
                x.GrantedByAccountId,
                x.CreatedAt,
                x.UpdatedAt,
                x.RevokedAt,
                x.RevokedByAccountId))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspacePermissionAuditRecord>> ListPermissionAuditRecordsAsync(
        Guid workspaceId,
        Guid? accountId = null,
        CancellationToken cancellationToken = default) =>
        await dbContext.WorkspacePermissionAuditRecords
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && (!accountId.HasValue || x.AccountId == accountId.Value))
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new WorkspacePermissionAuditRecord(
                x.Id,
                x.WorkspaceId,
                x.GrantId,
                x.AccountId,
                x.Permission,
                x.Action,
                x.ActorAccountId,
                x.OccurredAt))
            .ToListAsync(cancellationToken);

    public async Task<WorkspacePermissionGrant> GrantPermissionAsync(
        Guid workspaceId,
        GrantWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var membershipCreatedAt = await GetWorkspaceMembershipCreatedAtAsync(workspaceId, request.AccountId, cancellationToken);
        var existing = await dbContext.WorkspacePermissionGrants
            .Where(x => x.WorkspaceId == workspaceId
                    && x.AccountId == request.AccountId
                    && x.Permission == request.Permission
                    && x.RevokedAt == null
                    && (!membershipCreatedAt.HasValue || x.CreatedAt >= membershipCreatedAt.Value))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
            return ToPermissionGrant(existing);

        var entity = new WorkspacePermissionGrantEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AccountId = request.AccountId,
            Permission = request.Permission,
            GrantedByAccountId = request.GrantedByAccountId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await dbContext.WorkspacePermissionGrants.AddAsync(entity, cancellationToken);
        await dbContext.WorkspacePermissionAuditRecords.AddAsync(
            PermissionAuditRecord(entity, WorkspacePermissionAuditAction.Granted, request.GrantedByAccountId, now),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToPermissionGrant(entity);
    }

    public async Task<RevokeWorkspacePermissionResult> RevokePermissionAsync(
        Guid workspaceId,
        RevokeWorkspacePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var active = await dbContext.WorkspacePermissionGrants
            .Where(x => x.WorkspaceId == workspaceId
                        && x.AccountId == request.AccountId
                        && x.Permission == request.Permission
                        && x.RevokedAt == null)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (active.Count == 0)
        {
            var latest = await dbContext.WorkspacePermissionGrants
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId
                            && x.AccountId == request.AccountId
                            && x.Permission == request.Permission)
                .OrderByDescending(x => x.UpdatedAt)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return new RevokeWorkspacePermissionResult(latest is null ? [] : [ToPermissionGrant(latest)], false);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var entity in active)
        {
            entity.RevokedAt = now;
            entity.RevokedByAccountId = request.RevokedByAccountId;
            entity.UpdatedAt = now;
            await dbContext.WorkspacePermissionAuditRecords.AddAsync(
                PermissionAuditRecord(entity, WorkspacePermissionAuditAction.Revoked, request.RevokedByAccountId, now),
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new RevokeWorkspacePermissionResult(active.Select(ToPermissionGrant).ToList(), true);
    }

    public async Task<ActionConfirmation> CreateConfirmationAsync(
        Guid workspaceId,
        CreateActionConfirmationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var entity = new ActionConfirmationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ActionType = request.ActionType,
            TargetId = request.TargetId,
            ConfirmedByAccountId = request.ConfirmedByAccountId,
            ConfirmedAt = now,
            ExpiresAt = now.Add(request.Lifetime ?? TimeSpan.FromMinutes(5))
        };

        await dbContext.ActionConfirmations.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToActionConfirmation(entity);
    }

    public async Task<ActionConfirmation?> GetConfirmationAsync(
        Guid workspaceId,
        Guid confirmationId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ActionConfirmations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == confirmationId, cancellationToken);
        return entity is null ? null : ToActionConfirmation(entity);
    }

    public async Task<ConfirmationUseAttempt?> TryMarkConfirmationUsedAsync(
        Guid workspaceId,
        Guid confirmationId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken = default)
    {
        var affectedRows = await dbContext.ActionConfirmations
            .Where(x => x.WorkspaceId == workspaceId && x.Id == confirmationId && x.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.UsedAt, usedAt),
                cancellationToken);
        var confirmation = await GetConfirmationAsync(workspaceId, confirmationId, cancellationToken);
        return confirmation is null ? null : new ConfirmationUseAttempt(confirmation, affectedRows == 1);
    }

    public Task<bool> HasActiveRunAsync(
        Guid workspaceId,
        Guid environmentId,
        CancellationToken cancellationToken = default) =>
        dbContext.DeploymentRuns.AnyAsync(
            x => x.WorkspaceId == workspaceId
                && x.EnvironmentId == environmentId
                && (x.Status == WorkspaceDeploymentRunStatus.Queued ||
                    x.Status == WorkspaceDeploymentRunStatus.Running ||
                    x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired),
            cancellationToken);

    public async Task<WorkspaceDeploymentRun> CreateRunAsync(
        Guid workspaceId,
        QueueWorkspaceDeploymentRunRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var sourceRevision = await dbContext.DesiredStateRevisions
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == request.SourceRevisionId, cancellationToken);
        if (sourceRevision is null)
            throw new InvalidOperationException("Source revision does not exist in the workspace.");

        var environment = await dbContext.DeploymentEnvironments
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == request.TargetEnvironmentId, cancellationToken);
        if (environment is null)
            throw new InvalidOperationException("Target environment does not exist in the workspace.");

        var engineExists = await dbContext.WorkflowEngines
            .AnyAsync(x => x.WorkspaceId == workspaceId && x.Id == request.TargetEngineId && x.EnvironmentId == request.TargetEnvironmentId, cancellationToken);
        if (!engineExists)
            throw new InvalidOperationException("Target engine does not exist in the target environment.");

        var artifacts = await ResolveArtifactItemsAsync(workspaceId, sourceRevision.DesiredStateJson, cancellationToken);
        var artifactReference = artifacts.FirstOrDefault() is { } firstArtifact
            ? new DeploymentCommandArtifactReference(firstArtifact.ArtifactRecordId, firstArtifact.ArtifactId, firstArtifact.ArtifactTypeId, firstArtifact.ContentDigest)
            : null;
        var runId = Guid.NewGuid();
        var run = new DeploymentRunEntity
        {
            Id = runId,
            WorkspaceId = workspaceId,
            ElsaInstanceId = environment.ElsaInstanceId,
            ApplicationId = sourceRevision.ApplicationId,
            EnvironmentId = request.TargetEnvironmentId,
            EngineId = request.TargetEngineId,
            SourceRevisionId = request.SourceRevisionId,
            PreviousDeployedRevisionId = environment.DeployedRevisionId,
            RollbackSourceRunId = request.RollbackSourceRunId,
            Status = WorkspaceDeploymentRunStatus.Queued,
            ValidationOutcome = DeploymentValidationOutcome.Passed,
            ConfirmationId = request.ConfirmationId,
            ActorAccountId = request.ActorAccountId,
            QueuedAt = now,
            CreatedAt = now,
            AttemptNumber = 1,
            History =
            [
                new DeploymentRunHistoryEventEntity
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    RunId = runId,
                    Status = WorkspaceDeploymentRunStatus.Queued,
                    Message = request.RollbackSourceRunId is null ? "Deployment run queued." : "Rollback run queued.",
                    CreatedAt = now
                }
            ]
        };
        var command = CreateCommandEntity(
            workspaceId,
            new CreateDeploymentCommandRequest(
                runId,
                request.TargetEnvironmentId,
                request.TargetEngineId,
                request.RollbackSourceRunId is null ? DeploymentCommandAction.Deploy : DeploymentCommandAction.Rollback,
                artifactReference,
                new DeploymentCommandRevisionReference(request.SourceRevisionId),
                BuildDeploymentCommandIdempotencyKey(workspaceId, runId, request.TargetEnvironmentId, request.TargetEngineId, request.SourceRevisionId, request.RollbackSourceRunId),
                now,
                null,
                artifacts),
            now);
        await AddCommandEventAsync(command, DeploymentCommandStatus.Pending, "Deployment command created.", now, cancellationToken);

        environment.DeploymentStatus = DeploymentStatus.Running;
        environment.UpdatedAt = now;
        await dbContext.DeploymentRuns.AddAsync(run, cancellationToken);
        await dbContext.DeploymentCommands.AddAsync(command, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentRun(run);
    }

    public async Task<DeploymentCommand> CreateCommandAsync(
        Guid workspaceId,
        CreateDeploymentCommandRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.DeploymentRuns
            .AnyAsync(x => x.WorkspaceId == workspaceId && x.Id == request.RunId, cancellationToken);
        if (!exists)
            throw new InvalidOperationException("Deployment run does not exist in the workspace.");

        var existing = await dbContext.DeploymentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
            return ToDeploymentCommand(existing);

        var entity = CreateCommandEntity(workspaceId, request, now);
        await dbContext.DeploymentCommands.AddAsync(entity, cancellationToken);
        await AddCommandAndRunEventAsync(entity, DeploymentCommandStatus.Pending, "Deployment command created.", now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDeploymentCommand(entity);
    }

    public async Task<IReadOnlyList<DeploymentCommand>> PollPendingCommandsAsync(
        Guid workspaceId,
        Guid engineId,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var commands = await dbContext.DeploymentCommands
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId
                && x.EngineId == engineId
                && x.Status == DeploymentCommandStatus.Pending
                && (x.AvailableAt == null || x.AvailableAt <= now)
                && (x.ExpiresAt == null || x.ExpiresAt > now))
            .OrderBy(x => x.AvailableAt ?? x.CreatedAt)
            .ThenBy(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return commands.Select(ToDeploymentCommand).ToList();
    }

    public async Task<DeploymentCommand?> GetCommandAsync(
        Guid workspaceId,
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        var command = await dbContext.DeploymentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == commandId, cancellationToken);
        return command is null ? null : ToDeploymentCommand(command);
    }

    public async Task<DeploymentCommand> ClaimCommandAsync(
        Guid workspaceId,
        Guid commandId,
        ClaimDeploymentCommandRequest request,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var leaseExpiresAt = now.Add(request.LeaseDuration);
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Unspecified, async () =>
        {
            var updated = await dbContext.DeploymentCommands
                .Where(x => x.WorkspaceId == workspaceId
                    && x.Id == commandId
                    && x.EngineId == request.EngineId
                    && x.Status == DeploymentCommandStatus.Pending
                    && (x.AvailableAt == null || x.AvailableAt <= now)
                    && (x.ExpiresAt == null || x.ExpiresAt > now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Status, DeploymentCommandStatus.Claimed)
                        .SetProperty(x => x.WorkerId, request.WorkerId)
                        .SetProperty(x => x.LeaseToken, leaseToken)
                        .SetProperty(x => x.ClaimedAt, now)
                        .SetProperty(x => x.HeartbeatAt, now)
                        .SetProperty(x => x.LeaseExpiresAt, leaseExpiresAt)
                        .SetProperty(x => x.AttemptNumber, x => x.AttemptNumber + 1)
                        .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);
            if (updated == 0)
                await ThrowClaimConflictAsync(workspaceId, commandId, request.EngineId, now, cancellationToken);

            DetachTrackedCommand(commandId);
            var command = await LoadCommandForUpdateAsync(workspaceId, commandId, cancellationToken);

            await TouchCommandRunHeartbeatAsync(command, request.WorkerId, now, cancellationToken);

            await AddCommandAndRunEventAsync(command, DeploymentCommandStatus.Claimed, "Deployment command claimed by runtime worker.", now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDeploymentCommand(command);
        }, cancellationToken);
    }

    public async Task<DeploymentCommand> HeartbeatCommandAsync(
        Guid workspaceId,
        Guid commandId,
        DeploymentCommandHeartbeatRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var command = await LoadCommandForLeaseMutationAsync(workspaceId, commandId, request.LeaseToken, now, cancellationToken);
        command.WorkerId = request.WorkerId;
        command.HeartbeatAt = now;
        command.UpdatedAt = now;
        await TouchCommandRunHeartbeatAsync(command, request.WorkerId, now, cancellationToken);
        await AddCommandAndRunEventAsync(command, command.Status, "Runtime command heartbeat received.", now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDeploymentCommand(command);
    }

    public async Task<DeploymentCommand> RecordCommandProgressAsync(
        Guid workspaceId,
        Guid commandId,
        DeploymentCommandProgressRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var command = await LoadCommandForLeaseMutationAsync(workspaceId, commandId, request.LeaseToken, now, cancellationToken);
        command.Status = DeploymentCommandStatus.Running;
        command.PercentComplete = request.PercentComplete;
        command.ProgressMessage = request.Message;
        command.ArtifactJson = ApplyArtifactOutcomes(command.ArtifactJson, request.Artifacts);
        command.HeartbeatAt = now;
        command.UpdatedAt = now;
        await TouchCommandRunHeartbeatAsync(command, command.WorkerId, now, cancellationToken);
        await AddCommandAndRunEventAsync(command, DeploymentCommandStatus.Running, request.Message, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDeploymentCommand(command);
    }

    public Task<DeploymentCommand> CompleteCommandAsync(
        Guid workspaceId,
        Guid commandId,
        CompleteDeploymentCommandRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        // The command and its run reach their terminal states in one transaction, and a retried
        // attempt re-reads and revalidates the command instead of reusing an earlier load.
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var command = await LoadCommandForUpdateAsync(workspaceId, commandId, cancellationToken);
            if (command.Status == DeploymentCommandStatus.Completed)
            {
                ValidateFinalLease(command, request.LeaseToken);
                return ToDeploymentCommand(command);
            }
            ValidateLease(command, request.LeaseToken, now);
            ValidateObservedArtifactDigest(command, request.ObservedArtifactDigest);

            command.Status = DeploymentCommandStatus.Completed;
            command.ObservedArtifactDigestAlgorithm = request.ObservedArtifactDigest?.Algorithm;
            command.ObservedArtifactDigest = request.ObservedArtifactDigest?.Value;
            command.RuntimeReference = request.RuntimeReference;
            command.DiagnosticsJson = JsonSerializer.Serialize(request.Diagnostics);
            command.ArtifactJson = ApplyArtifactOutcomes(command.ArtifactJson, request.Artifacts);
            command.CompletedAt = now;
            command.UpdatedAt = now;
            await AddCommandAndRunEventAsync(command, DeploymentCommandStatus.Completed, "Runtime command completed.", now, cancellationToken);
            await UpdateRunStatusInTransactionAsync(
                workspaceId,
                command.RunId,
                command.Action == DeploymentCommandAction.Rollback ? WorkspaceDeploymentRunStatus.RolledBack : WorkspaceDeploymentRunStatus.Succeeded,
                "Deployment command completed by runtime.",
                now,
                cancellationToken: cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDeploymentCommand(command);
        }, cancellationToken);

    public Task<DeploymentCommand> FailCommandAsync(
        Guid workspaceId,
        Guid commandId,
        FailDeploymentCommandRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinalizeCommandAsync(workspaceId, commandId, request.LeaseToken, DeploymentCommandStatus.Failed, request.Diagnostics, request.Artifacts, "Runtime command failed.", now, cancellationToken);

    public Task<DeploymentCommand> RejectCommandAsync(
        Guid workspaceId,
        Guid commandId,
        RejectDeploymentCommandRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        FinalizeCommandAsync(workspaceId, commandId, request.LeaseToken, DeploymentCommandStatus.Rejected, request.Diagnostics, request.Artifacts, "Runtime command rejected.", now, cancellationToken);

    public async Task<int> MarkStaleCommandsRecoveryRequiredAsync(
        DateTimeOffset now,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        var staleBefore = now.Subtract(staleAfter);
        var staleCommandIds = await dbContext.DeploymentCommands
            .AsNoTracking()
            .Where(x => (x.Status == DeploymentCommandStatus.Claimed || x.Status == DeploymentCommandStatus.Running)
                && (x.HeartbeatAt ?? x.ClaimedAt ?? x.CreatedAt) < staleBefore)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var commandId in staleCommandIds)
        {
            // Each command moves to recovery together with its run. The unit re-reads the command,
            // so a retried attempt, or a command that completed after the scan, is not overwritten.
            var marked = await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var command = await dbContext.DeploymentCommands
                    .SingleOrDefaultAsync(x => x.Id == commandId
                        && (x.Status == DeploymentCommandStatus.Claimed || x.Status == DeploymentCommandStatus.Running)
                        && (x.HeartbeatAt ?? x.ClaimedAt ?? x.CreatedAt) < staleBefore, cancellationToken);
                if (command is null)
                    return false;

                command.Status = DeploymentCommandStatus.RecoveryRequired;
                command.UpdatedAt = now;
                command.CompletedAt = now;
                await AddCommandAndRunEventAsync(command, DeploymentCommandStatus.RecoveryRequired, "Runtime command requires recovery after stale heartbeat.", now, cancellationToken);
                await UpdateRunStatusInTransactionAsync(
                    command.WorkspaceId,
                    command.RunId,
                    WorkspaceDeploymentRunStatus.RecoveryRequired,
                    "Deployment command requires recovery after stale runtime heartbeat.",
                    now,
                    cancellationToken: cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }, cancellationToken);
            if (marked)
                recovered++;
        }

        return recovered;
    }

    public async Task<DeploymentCommandWebhookNotification> CreateWebhookNotificationAsync(
        Guid workspaceId,
        Guid engineId,
        Guid commandId,
        string safePayloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var command = await dbContext.DeploymentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == commandId, cancellationToken)
            ?? throw new KeyNotFoundException("Deployment command does not exist in the workspace.");
        if (command.EngineId != engineId)
            throw new InvalidOperationException("Command does not target the requested runtime engine.");
        if (command.Status != DeploymentCommandStatus.Pending)
            throw new InvalidOperationException("Command is not pending.");
        if (command.AvailableAt is not null && command.AvailableAt > now)
            throw new InvalidOperationException("Command is not available.");
        if (command.ExpiresAt is not null && command.ExpiresAt <= now)
            throw new InvalidOperationException("Command is expired.");

        var notification = new DeploymentCommandWebhookNotificationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            EngineId = engineId,
            CommandId = commandId,
            Status = WebhookNotificationStatus.Pending,
            SafePayloadJson = safePayloadJson,
            CreatedAt = now
        };
        await dbContext.DeploymentCommandWebhookNotifications.AddAsync(notification, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDeploymentCommandWebhookNotification(notification);
    }

    public async Task<IReadOnlyList<DeploymentWebhookNotificationDispatchTarget>> ListPendingWebhookNotificationTargetsAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 100);
        return await (
            from notification in dbContext.DeploymentCommandWebhookNotifications.AsNoTracking()
            join engine in dbContext.WorkflowEngines.AsNoTracking()
                on new { notification.WorkspaceId, notification.EngineId }
                equals new { engine.WorkspaceId, EngineId = engine.Id }
                into engines
            from engine in engines.DefaultIfEmpty()
            where notification.Status == WebhookNotificationStatus.Pending
            orderby notification.CreatedAt, notification.Id
            select new DeploymentWebhookNotificationDispatchTarget(
                notification.Id,
                notification.WorkspaceId,
                notification.EngineId,
                notification.CommandId,
                notification.SafePayloadJson,
                engine == null ? null : engine.BaseUrl,
                notification.CreatedAt))
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);
    }

    public Task<DeploymentCommandWebhookNotification> MarkWebhookNotificationSentAsync(
        Guid workspaceId,
        Guid notificationId,
        DateTimeOffset sentAt,
        CancellationToken cancellationToken = default) =>
        MarkWebhookNotificationStatusAsync(workspaceId, notificationId, WebhookNotificationStatus.Sent, sentAt, cancellationToken);

    public Task<DeploymentCommandWebhookNotification> MarkWebhookNotificationFailedAsync(
        Guid workspaceId,
        Guid notificationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        MarkWebhookNotificationStatusAsync(workspaceId, notificationId, WebhookNotificationStatus.Failed, null, cancellationToken);

    public Task<DeploymentCommandWebhookNotification> MarkWebhookNotificationSkippedAsync(
        Guid workspaceId,
        Guid notificationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        MarkWebhookNotificationStatusAsync(workspaceId, notificationId, WebhookNotificationStatus.Skipped, null, cancellationToken);

    private async Task<DeploymentCommandWebhookNotification> MarkWebhookNotificationStatusAsync(
        Guid workspaceId,
        Guid notificationId,
        WebhookNotificationStatus status,
        DateTimeOffset? sentAt,
        CancellationToken cancellationToken)
    {
        var notification = await dbContext.DeploymentCommandWebhookNotifications
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == notificationId, cancellationToken)
            ?? throw new KeyNotFoundException("Webhook notification does not exist in the workspace.");
        if (notification.Status != WebhookNotificationStatus.Pending)
            return ToDeploymentCommandWebhookNotification(notification);

        notification.Status = status;
        notification.SentAt = sentAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDeploymentCommandWebhookNotification(notification);
    }

    public async Task<WorkspaceDeploymentRun?> GetRunAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == runId, cancellationToken);
        return entity is null ? null : ToWorkspaceDeploymentRun(entity);
    }

    public async Task<IReadOnlyList<DeploymentRunHistoryEvent>> GetRunHistoryAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.DeploymentRunHistoryEvents
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.RunId == runId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => ToDeploymentRunHistoryEvent(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeploymentRunCommandSummary>> GetRunCommandSummariesAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var commands = await dbContext.DeploymentCommands
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.RunId == runId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return commands.Select(ToDeploymentRunCommandSummary).ToList();
    }

    public async Task<WorkspaceDeploymentRun?> ClaimNextQueuedRunAsync(
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var run = await dbContext.DeploymentRuns
            .Where(x => x.Status == WorkspaceDeploymentRunStatus.Queued)
            .OrderBy(x => x.QueuedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
            return null;

        run.Status = WorkspaceDeploymentRunStatus.Running;
        run.StartedAt = now;
        run.WorkerId = workerId;
        run.WorkerHeartbeatAt = now;
        await dbContext.DeploymentRunHistoryEvents.AddAsync(new DeploymentRunHistoryEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = run.WorkspaceId,
            RunId = run.Id,
            Status = WorkspaceDeploymentRunStatus.Running,
            Message = "Deployment run claimed by worker.",
            CreatedAt = now
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentRun(run);
    }

    public Task<WorkspaceDeploymentRun> UpdateRunStatusAsync(
        Guid workspaceId,
        Guid runId,
        WorkspaceDeploymentRunStatus status,
        string message,
        DateTimeOffset now,
        string? failureMessage = null,
        CancellationToken cancellationToken = default) =>
        dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            () => UpdateRunStatusInTransactionAsync(workspaceId, runId, status, message, now, failureMessage, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Applies a run status inside the caller's serializable catalog transaction.
    /// </summary>
    private async Task<WorkspaceDeploymentRun> UpdateRunStatusInTransactionAsync(
        Guid workspaceId,
        Guid runId,
        WorkspaceDeploymentRunStatus status,
        string message,
        DateTimeOffset now,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        // Persist any correlated command mutation inside this transaction before
        // detaching a previously tracked run for the set-based terminal CAS. EF
        // relationship fix-up can otherwise detach the pending command and events
        // together with their run principal.
        await dbContext.SaveChangesAsync(cancellationToken);
        DetachTrackedRun(runId);
        var currentStatus = await dbContext.DeploymentRuns
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.Id == runId)
            .Select(x => x.Status)
            .SingleAsync(cancellationToken);

        if (status == WorkspaceDeploymentRunStatus.RecoveryRequired && IsTerminalRunStatus(currentStatus))
            throw new InvalidOperationException("A terminal deployment run cannot be moved into recovery.");

        DeploymentRunEntity run;
        if (IsTerminalRunStatus(status))
        {
            var updated = await dbContext.DeploymentRuns
                .Where(x => x.WorkspaceId == workspaceId
                    && x.Id == runId
                    && x.Status != WorkspaceDeploymentRunStatus.RecoveryRequired)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.FailureMessage, failureMessage)
                    .SetProperty(x => x.CompletedAt, now), cancellationToken);
            if (updated == 0)
                throw new InvalidOperationException("Deployment run requires recovery before a terminal update can be applied.");

            run = await dbContext.DeploymentRuns
                .Include(x => x.Environment)
                .SingleAsync(x => x.WorkspaceId == workspaceId && x.Id == runId, cancellationToken);
        }
        else
        {
            run = await dbContext.DeploymentRuns
                .Include(x => x.Environment)
                .SingleAsync(x => x.WorkspaceId == workspaceId && x.Id == runId, cancellationToken);
            run.Status = status;
            run.FailureMessage = failureMessage;
        }

        if (IsTerminalRunStatus(status))
            run.CompletedAt = now;
        else if (status == WorkspaceDeploymentRunStatus.RecoveryRequired)
        {
            run.CompletedAt = null;
            run.RecoveryReason = failureMessage ?? message;
            await ProjectManagedRecoveryRequiredAsync(run, now, cancellationToken);
        }

        if (run.Environment is not null)
        {
            run.Environment.UpdatedAt = now;
            run.Environment.DeploymentStatus = status is WorkspaceDeploymentRunStatus.Succeeded or WorkspaceDeploymentRunStatus.RolledBack
                ? DeploymentStatus.Succeeded
                : status == WorkspaceDeploymentRunStatus.Running
                    ? DeploymentStatus.Running
                    : DeploymentStatus.Blocked;
            if (status is WorkspaceDeploymentRunStatus.Succeeded or WorkspaceDeploymentRunStatus.RolledBack)
                run.Environment.DeployedRevisionId = run.SourceRevisionId;
        }

        await dbContext.DeploymentRunHistoryEvents.AddAsync(new DeploymentRunHistoryEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RunId = run.Id,
            Status = status,
            Message = message,
            CreatedAt = now
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentRun(run);
    }

    public async Task<int> MarkStaleRunningRunsRecoveryRequiredAsync(
        DateTimeOffset now,
        TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        var staleBefore = now.Subtract(staleAfter);
        var staleRunIds = await dbContext.DeploymentRuns
            .AsNoTracking()
            .Where(x => x.Status == WorkspaceDeploymentRunStatus.Running
                && (x.WorkerHeartbeatAt ?? x.StartedAt ?? x.QueuedAt) < staleBefore)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (staleRunIds.Count == 0)
            return 0;

        // The candidate ids are only a pre-read: every write below repeats the staleness
        // predicate, so a retried attempt re-decides each run from current state.
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var markedCount = 0;
            const string recoveryReason = "Worker heartbeat became stale.";

            foreach (var runId in staleRunIds)
            {
                var updated = await dbContext.DeploymentRuns
                    .Where(x => x.Id == runId
                        && x.Status == WorkspaceDeploymentRunStatus.Running
                        && (x.WorkerHeartbeatAt ?? x.StartedAt ?? x.QueuedAt) < staleBefore)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, WorkspaceDeploymentRunStatus.RecoveryRequired)
                        .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.RecoveryReason, recoveryReason), cancellationToken);
                if (updated == 0)
                    continue;

                DetachTrackedRun(runId);
                var run = await dbContext.DeploymentRuns
                    .Include(x => x.Environment)
                    .SingleAsync(x => x.Id == runId, cancellationToken);
                await dbContext.DeploymentRunHistoryEvents.AddAsync(new DeploymentRunHistoryEventEntity
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = run.WorkspaceId,
                    RunId = run.Id,
                    Status = WorkspaceDeploymentRunStatus.RecoveryRequired,
                    Message = "Deployment run requires recovery after stale worker heartbeat.",
                    CreatedAt = now
                }, cancellationToken);

                await ProjectManagedRecoveryRequiredAsync(run, now, cancellationToken);
                if (run.Environment is not null)
                {
                    run.Environment.UpdatedAt = now;
                    run.Environment.DeploymentStatus = DeploymentStatus.Blocked;
                }
                markedCount++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return markedCount;
        }, cancellationToken);
    }

    private async Task ProjectManagedRecoveryRequiredAsync(
        DeploymentRunEntity run,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (run.ElsaInstanceId is not { } instanceId)
            return;

        var operation = await dbContext.ElsaInstanceOperations
            .SingleOrDefaultAsync(x => x.WorkspaceId == run.WorkspaceId
                && x.DeploymentRunId == run.Id
                && x.InstanceId == instanceId,
                cancellationToken);
        var instance = await dbContext.ElsaInstances
            .SingleOrDefaultAsync(x => x.Id == instanceId && x.WorkspaceId == run.WorkspaceId,
                cancellationToken);
        if (operation is null || instance is null)
            throw new InvalidOperationException("Managed lifecycle recovery linkage is inconsistent.");
        if (operation.State == ElsaInstanceOperationState.RecoveryRequired &&
            instance.ObservedLifecycle == ElsaObservedLifecycle.Unknown &&
            instance.Health == ElsaInstanceHealth.Unknown)
            return;
        if (operation.State is not (ElsaInstanceOperationState.Queued or ElsaInstanceOperationState.Running))
            throw new InvalidOperationException("Managed lifecycle recovery linkage is inconsistent.");

        var priorState = instance.ObservedLifecycle;
        operation.State = ElsaInstanceOperationState.RecoveryRequired;
        operation.CompletedAt = null;
        operation.WorkerId = null;
        operation.LeaseTokenHash = null;
        operation.LeaseExpiresAt = null;
        operation.HeartbeatAt = null;
        operation.UpdatedAt = now;
        instance.ObservedLifecycle = ElsaObservedLifecycle.Unknown;
        instance.Health = ElsaInstanceHealth.Unknown;
        instance.UpdatedAt = now;
        var sequence = await dbContext.ElsaInstanceAuditEvents
            .Where(x => x.InstanceId == instanceId)
            .Select(x => (long?)x.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        await dbContext.ElsaInstanceAuditEvents.AddAsync(new()
        {
            Id = Guid.NewGuid(), OrganizationId = instance.OrganizationId,
            WorkspaceId = instance.WorkspaceId, InstanceId = instance.Id,
            Sequence = checked(sequence + 1), EventType = "lifecycle.recovery-required",
            OperationId = operation.Id, DeploymentRunId = run.Id,
            PriorState = priorState.ToString(), NewState = ElsaObservedLifecycle.Unknown.ToString(),
            DesiredStateRevisionId = instance.DesiredStateRevisionId,
            PlanReference = instance.ResolvedPlanUri,
            DiagnosticCode = "provider.reconciliation.required",
            Summary = "Provider state must be reconciled before retry.",
            RequestKeyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(operation.IdempotencyKey))),
            OccurredAt = now
        }, cancellationToken);
    }

    public async Task<RuntimeControlExecution> RecordRuntimeControlExecutionAsync(
        Guid workspaceId,
        RuntimeControlExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.WorkspaceId != workspaceId)
            throw new InvalidOperationException("Runtime control execution workspace does not match the request workspace.");

        var entity = new RuntimeControlExecutionEntity
        {
            Id = execution.Id,
            WorkspaceId = execution.WorkspaceId,
            EngineId = execution.EngineId,
            EnvironmentId = execution.EnvironmentId,
            ControlId = execution.ControlId,
            ControlLabel = execution.ControlLabel,
            Boundary = execution.Boundary,
            RequiredCapabilityId = execution.RequiredCapabilityId,
            ConfirmationId = execution.ConfirmationId,
            ActorAccountId = execution.ActorAccountId,
            Status = execution.Status,
            CreatedAt = execution.CreatedAt,
            Message = execution.Message
        };

        await dbContext.RuntimeControlExecutions.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRuntimeControlExecution(entity);
    }

    public async Task<WorkspaceDeploymentApplication> CreateApplicationAsync(
        Guid workspaceId,
        CreateWorkflowApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new DeploymentApplicationEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = request.Name,
            Description = request.Description,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByAccountId = request.ActorAccountId,
            UpdatedByAccountId = request.ActorAccountId
        };

        await dbContext.DeploymentApplications.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WorkspaceDeploymentApplication(entity.Id, entity.WorkspaceId, entity.Name, entity.Description, entity.CreatedAt, entity.UpdatedAt, entity.CreatedByAccountId, entity.UpdatedByAccountId);
    }

    public async Task<WorkspaceDeploymentApplication> UpdateApplicationAsync(
        Guid workspaceId,
        Guid applicationId,
        UpdateWorkflowApplicationRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentApplications
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == applicationId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment application does not exist in the workspace.");

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedByAccountId = request.ActorAccountId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WorkspaceDeploymentApplication(entity.Id, entity.WorkspaceId, entity.Name, entity.Description, entity.CreatedAt, entity.UpdatedAt, entity.CreatedByAccountId, entity.UpdatedByAccountId);
    }

    public Task<WorkspaceDeploymentEnvironment> CreateEnvironmentAsync(
        Guid workspaceId,
        CreateDeploymentEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        return CreateEnvironmentCoreAsync(workspaceId, request, cancellationToken);
    }

    public async Task<WorkspaceDeploymentEnvironment> UpdateEnvironmentAsync(
        Guid workspaceId,
        Guid environmentId,
        UpdateDeploymentEnvironmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentEnvironments
            .Include(x => x.TierDefinition)
                .ThenInclude(x => x!.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == environmentId && x.ApplicationId == request.ApplicationId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment environment does not exist in the workspace.");

        var tier = await ResolveTierForEnvironmentAsync(workspaceId, request.Tier, request.TierId, requireActive: true, cancellationToken);
        entity.Name = request.Name;
        entity.Tier = request.Tier;
        entity.TierId = tier.Id;
        entity.TierDefinition = tier;
        entity.TierRequiresReview = false;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentEnvironment(entity);
    }

    public async Task<WorkspaceWorkflowEngine> RegisterEngineAsync(
        Guid workspaceId,
        RegisterWorkflowEngineRequest request,
        CancellationToken cancellationToken = default)
    {
        var environmentExists = await dbContext.DeploymentEnvironments
            .AnyAsync(x => x.WorkspaceId == workspaceId && x.Id == request.EnvironmentId, cancellationToken);
        if (!environmentExists)
            throw new InvalidOperationException("Deployment environment does not exist in the workspace.");

        var now = DateTimeOffset.UtcNow;
        var credential = await ResolveCredentialReferenceAsync(
            workspaceId,
            request.CredentialReferenceId,
            request.CredentialProvider,
            request.CredentialReference,
            request.CredentialAssignmentStatus,
            cancellationToken);
        var engine = new WorkflowEngineEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            EnvironmentId = request.EnvironmentId,
            CredentialReferenceId = credential.Id,
            Name = request.Name,
            BaseUrl = request.BaseUrl,
            Region = request.Region,
            Version = "",
            CertificateStatus = CertificateStatus.Trusted,
            CredentialProvider = credential.Provider,
            CredentialReference = credential.Reference,
            CredentialAssignmentStatus = credential.AssignmentStatus,
            CredentialVerificationStatus = CredentialVerificationStatus.Unverified,
            Health = DeploymentHealth.Unreachable,
            VerificationMessage = "Engine has not been verified.",
            HostingProvider = request.HostingProvider,
            CreatedAt = now,
            UpdatedAt = now,
            Capabilities = request.Capabilities.Select(capability => new EngineCapabilityEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                CapabilityId = capability.Id,
                Label = capability.Label,
                Boundary = capability.Boundary
            }).ToList(),
            Controls = request.Controls.Select(control => new RuntimeControlEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                ControlId = control.Id,
                Label = control.Label,
                Boundary = control.Boundary,
                RequiredCapabilityId = control.CapabilityId,
                Description = control.Description
            }).ToList()
        };

        await dbContext.WorkflowEngines.AddAsync(engine, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceWorkflowEngine(engine);
    }

    public async Task<IReadOnlyList<WorkspaceDeploymentSecretStore>> ListSecretStoresAsync(
        Guid workspaceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.DeploymentSecretStores
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);
        if (!includeArchived)
            query = query.Where(x => x.Status == DeploymentSecretStoreStatus.Active);

        var stores = await query
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return stores.Select(ToWorkspaceDeploymentSecretStore).ToList();
    }

    public async Task<WorkspaceDeploymentSecretStore> CreateSecretStoreAsync(
        Guid workspaceId,
        CreateDeploymentSecretStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (await ActiveSecretStoreNameExistsAsync(workspaceId, request.Name, null, cancellationToken))
            throw new InvalidOperationException("An active deployment secret store with this name already exists in the workspace.");

        var now = DateTimeOffset.UtcNow;
        var entity = new DeploymentSecretStoreEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Name = request.Name,
            Provider = request.Provider ?? "",
            Type = request.Type,
            Description = request.Description,
            Status = DeploymentSecretStoreStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByAccountId = request.ActorAccountId,
            UpdatedByAccountId = request.ActorAccountId
        };

        await dbContext.DeploymentSecretStores.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentSecretStore(entity);
    }

    public async Task<WorkspaceDeploymentSecretStore> UpdateSecretStoreAsync(
        Guid workspaceId,
        Guid secretStoreId,
        UpdateDeploymentSecretStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentSecretStores
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == secretStoreId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment secret store does not exist in the workspace.");
        if (entity.Status == DeploymentSecretStoreStatus.Active && await ActiveSecretStoreNameExistsAsync(workspaceId, request.Name, secretStoreId, cancellationToken))
            throw new InvalidOperationException("An active deployment secret store with this name already exists in the workspace.");

        entity.Name = request.Name;
        entity.Provider = request.Provider ?? "";
        entity.Type = request.Type;
        entity.Description = request.Description;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedByAccountId = request.ActorAccountId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentSecretStore(entity);
    }

    public async Task<WorkspaceDeploymentSecretStore> ArchiveSecretStoreAsync(
        Guid workspaceId,
        Guid secretStoreId,
        Guid? actorAccountId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentSecretStores
            .Include(x => x.CredentialReferences)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == secretStoreId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment secret store does not exist in the workspace.");
        if (entity.Status == DeploymentSecretStoreStatus.Archived)
            return ToWorkspaceDeploymentSecretStore(entity);

        var now = DateTimeOffset.UtcNow;
        entity.Status = DeploymentSecretStoreStatus.Archived;
        entity.ArchivedAt = now;
        entity.ArchivedByAccountId = actorAccountId;
        entity.UpdatedAt = now;
        entity.UpdatedByAccountId = actorAccountId;
        foreach (var reference in entity.CredentialReferences.Where(x => x.Status == DeploymentSecretStoreStatus.Active))
        {
            reference.Status = DeploymentSecretStoreStatus.Archived;
            reference.ArchivedAt = now;
            reference.ArchivedByAccountId = actorAccountId;
            reference.UpdatedAt = now;
            reference.UpdatedByAccountId = actorAccountId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentSecretStore(entity);
    }

    public async Task<IReadOnlyList<WorkspaceDeploymentCredentialReference>> ListCredentialReferencesAsync(
        Guid workspaceId,
        Guid? secretStoreId = null,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.DeploymentCredentialReferences
            .AsNoTracking()
            .Include(x => x.SecretStore)
            .Where(x => x.WorkspaceId == workspaceId);
        if (secretStoreId.HasValue)
            query = query.Where(x => x.SecretStoreId == secretStoreId.Value);
        if (!includeArchived)
            query = query.Where(x => x.Status == DeploymentSecretStoreStatus.Active && x.SecretStore!.Status == DeploymentSecretStoreStatus.Active);

        var references = await query
            .OrderBy(x => x.SecretStore!.Name)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var referenceIds = references.Select(x => x.Id).ToList();
        var usageCounts = referenceIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await dbContext.WorkflowEngines
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && x.CredentialReferenceId.HasValue && referenceIds.Contains(x.CredentialReferenceId.Value))
                .GroupBy(x => x.CredentialReferenceId!.Value)
                .Select(x => new { CredentialReferenceId = x.Key, Count = x.Count() })
                .ToDictionaryAsync(x => x.CredentialReferenceId, x => x.Count, cancellationToken);
        return references.Select(reference => ToWorkspaceDeploymentCredentialReference(reference, usageCounts.GetValueOrDefault(reference.Id))).ToList();
    }

    public async Task<WorkspaceDeploymentCredentialReference> CreateCredentialReferenceAsync(
        Guid workspaceId,
        CreateDeploymentCredentialReferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var store = await dbContext.DeploymentSecretStores
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == request.SecretStoreId, cancellationToken);
        if (store is null)
            throw new KeyNotFoundException("Deployment secret store does not exist in the workspace.");
        if (store.Status != DeploymentSecretStoreStatus.Active)
            throw new InvalidOperationException("Credential references can only be added to active secret stores.");
        if (await ActiveCredentialReferenceNameExistsAsync(workspaceId, request.SecretStoreId, request.Name, null, cancellationToken))
            throw new InvalidOperationException("An active credential reference with this name already exists in the secret store.");

        var now = DateTimeOffset.UtcNow;
        var entity = new DeploymentCredentialReferenceEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            SecretStoreId = request.SecretStoreId,
            SecretStore = store,
            Name = request.Name,
            Reference = request.Reference,
            ProtectedSecret = request.ProtectedSecret,
            ProtectedSecretUpdatedAt = string.IsNullOrWhiteSpace(request.ProtectedSecret) ? null : now,
            Description = request.Description,
            Status = DeploymentSecretStoreStatus.Active,
            VerificationStatus = CredentialVerificationStatus.Unverified,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByAccountId = request.ActorAccountId,
            UpdatedByAccountId = request.ActorAccountId
        };

        await dbContext.DeploymentCredentialReferences.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentCredentialReference(entity);
    }

    public async Task<WorkspaceDeploymentCredentialReference> UpdateCredentialReferenceAsync(
        Guid workspaceId,
        Guid credentialReferenceId,
        UpdateDeploymentCredentialReferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentCredentialReferences
            .Include(x => x.SecretStore)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment credential reference does not exist in the workspace.");
        if (entity.Status == DeploymentSecretStoreStatus.Active && await ActiveCredentialReferenceNameExistsAsync(workspaceId, entity.SecretStoreId, request.Name, credentialReferenceId, cancellationToken))
            throw new InvalidOperationException("An active credential reference with this name already exists in the secret store.");

        entity.Name = request.Name;
        entity.Reference = request.Reference;
        if (!string.IsNullOrWhiteSpace(request.ProtectedSecret))
        {
            entity.ProtectedSecret = request.ProtectedSecret;
            entity.ProtectedSecretUpdatedAt = DateTimeOffset.UtcNow;
        }

        entity.Description = request.Description;
        entity.VerificationStatus = CredentialVerificationStatus.Unverified;
        entity.LastVerifiedAt = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        entity.UpdatedByAccountId = request.ActorAccountId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentCredentialReference(entity);
    }

    public async Task<WorkspaceDeploymentCredentialReference> RotateCredentialReferenceAsync(
        Guid workspaceId,
        Guid credentialReferenceId,
        RotateDeploymentCredentialReferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentCredentialReferences
            .Include(x => x.SecretStore)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment credential reference does not exist in the workspace.");

        var now = DateTimeOffset.UtcNow;
        entity.ProtectedSecret = request.ProtectedSecret;
        entity.ProtectedSecretUpdatedAt = now;
        entity.VerificationStatus = CredentialVerificationStatus.Unverified;
        entity.LastVerifiedAt = null;
        entity.UpdatedAt = now;
        entity.UpdatedByAccountId = request.ActorAccountId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentCredentialReference(entity);
    }

    public async Task<WorkspaceDeploymentCredentialReference> ArchiveCredentialReferenceAsync(
        Guid workspaceId,
        Guid credentialReferenceId,
        Guid? actorAccountId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DeploymentCredentialReferences
            .Include(x => x.SecretStore)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Deployment credential reference does not exist in the workspace.");
        if (entity.Status == DeploymentSecretStoreStatus.Archived)
            return ToWorkspaceDeploymentCredentialReference(entity);

        var now = DateTimeOffset.UtcNow;
        entity.Status = DeploymentSecretStoreStatus.Archived;
        entity.ArchivedAt = now;
        entity.ArchivedByAccountId = actorAccountId;
        entity.UpdatedAt = now;
        entity.UpdatedByAccountId = actorAccountId;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentCredentialReference(entity);
    }

    public async Task<IReadOnlyList<WorkspaceDeploymentCredentialUsage>> ListCredentialReferenceUsageAsync(
        Guid workspaceId,
        Guid credentialReferenceId,
        CancellationToken cancellationToken = default)
    {
        var referenceExists = await dbContext.DeploymentCredentialReferences
            .AnyAsync(x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId, cancellationToken);
        if (!referenceExists)
            throw new KeyNotFoundException("Deployment credential reference does not exist in the workspace.");

        return await dbContext.WorkflowEngines
            .AsNoTracking()
            .Include(x => x.Environment)
            .ThenInclude(x => x!.Application)
            .Where(x => x.WorkspaceId == workspaceId && x.CredentialReferenceId == credentialReferenceId)
            .OrderBy(x => x.Environment!.Application!.Name)
            .ThenBy(x => x.Environment!.Name)
            .ThenBy(x => x.Name)
            .Select(x => new WorkspaceDeploymentCredentialUsage(
                x.Id,
                x.Name,
                x.Environment!.ApplicationId,
                x.Environment.Application!.Name,
                x.EnvironmentId,
                x.Environment.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkspaceEngineCredentialSecret?> GetEngineCredentialSecretAsync(
        Guid workspaceId,
        Guid engineId,
        CancellationToken cancellationToken = default)
    {
        var engine = await dbContext.WorkflowEngines
            .AsNoTracking()
            .Include(x => x.CredentialReferenceMetadata)
                .ThenInclude(x => x!.SecretStore)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == engineId, cancellationToken);
        var credential = engine?.CredentialReferenceMetadata;
        var store = credential?.SecretStore;
        return engine is null || credential is null || store is null
            ? null
            : new WorkspaceEngineCredentialSecret(
                engine.Id,
                credential.Id,
                store.Status,
                credential.Status,
                store.Type,
                credential.ProtectedSecret);
    }

    public async Task<WorkspaceDeploymentCredentialSecret?> GetCredentialSecretAsync(
        Guid workspaceId,
        Guid credentialReferenceId,
        CancellationToken cancellationToken = default)
    {
        var credential = await dbContext.DeploymentCredentialReferences
            .AsNoTracking()
            .Include(x => x.SecretStore)
            .SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId,
                cancellationToken);
        var store = credential?.SecretStore;
        return credential is null || store is null
            ? null
            : new WorkspaceDeploymentCredentialSecret(
                credential.Id,
                store.Status,
                credential.Status,
                store.Type,
                credential.ProtectedSecret);
    }

    public async Task<WorkspaceWorkflowEngine> UpdateEngineAsync(
        Guid workspaceId,
        Guid engineId,
        UpdateWorkflowEngineRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = await dbContext.WorkflowEngines
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == engineId, cancellationToken);
        if (engine is null)
            throw new KeyNotFoundException("Workflow engine does not exist in the workspace.");

        engine.Name = request.Name;
        engine.BaseUrl = request.BaseUrl;
        engine.Region = request.Region;
        var credential = await ResolveCredentialReferenceAsync(
            workspaceId,
            request.CredentialReferenceId,
            request.CredentialProvider,
            request.CredentialReference,
            request.CredentialAssignmentStatus,
            cancellationToken);
        engine.CredentialReferenceId = credential.Id;
        engine.CredentialProvider = credential.Provider;
        engine.CredentialReference = credential.Reference;
        engine.CredentialAssignmentStatus = credential.AssignmentStatus;
        engine.Version = "";
        engine.CertificateStatus = CertificateStatus.Trusted;
        engine.CredentialVerificationStatus = CredentialVerificationStatus.Unverified;
        engine.CredentialLastVerifiedAt = null;
        engine.Health = DeploymentHealth.Unreachable;
        engine.LastHeartbeatAt = null;
        engine.LastVerificationAt = null;
        engine.VerificationMessage = "Engine settings changed; verification is required.";
        engine.HostingProvider = request.HostingProvider;
        engine.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.EngineCapabilities
            .Where(x => x.WorkspaceId == workspaceId && x.EngineId == engine.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.RuntimeControls
            .Where(x => x.WorkspaceId == workspaceId && x.EngineId == engine.Id)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.EngineCapabilities.AddRangeAsync(request.Capabilities.Select(capability => new EngineCapabilityEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            EngineId = engine.Id,
            CapabilityId = capability.Id,
            Label = capability.Label,
            Boundary = capability.Boundary
        }), cancellationToken);
        await dbContext.RuntimeControls.AddRangeAsync(request.Controls.Select(control => new RuntimeControlEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            EngineId = engine.Id,
            ControlId = control.Id,
            Label = control.Label,
            Boundary = control.Boundary,
            RequiredCapabilityId = control.CapabilityId,
            Description = control.Description
        }), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceWorkflowEngine(engine);
    }

    public async Task<WorkspaceDesiredStateRevision> CreateRevisionAsync(
        Guid workspaceId,
        CreateDesiredStateRevisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var environment = await dbContext.DeploymentEnvironments
            .SingleOrDefaultAsync(x =>
                x.WorkspaceId == workspaceId
                && x.Id == request.EnvironmentId
                && x.ApplicationId == request.ApplicationId,
                cancellationToken);
        if (environment is null)
            throw new InvalidOperationException("Deployment environment does not exist in the workspace.");

        var nextRevision = await dbContext.DesiredStateRevisions
            .Where(x => x.WorkspaceId == workspaceId && x.EnvironmentId == request.EnvironmentId)
            .Select(x => (int?)x.RevisionNumber)
            .MaxAsync(cancellationToken) + 1 ?? 1;
        var now = DateTimeOffset.UtcNow;
        var revisionId = Guid.NewGuid();
        var entity = new DesiredStateRevisionEntity
        {
            Id = revisionId,
            WorkspaceId = workspaceId,
            ApplicationId = request.ApplicationId,
            EnvironmentId = request.EnvironmentId,
            RevisionNumber = nextRevision,
            Label = request.Label,
            Commit = request.Commit,
            ContentHash = WorkspaceDeploymentService.ComputeDesiredStateHash(request.DesiredStateJson),
            DesiredStateJson = request.DesiredStateJson,
            AuthoredAt = now,
            CreatedAt = now,
            CreatedByAccountId = request.ActorAccountId,
            Records = ParseStructuredRecords(workspaceId, revisionId, request.DesiredStateJson)
        };

        await dbContext.DesiredStateRevisions.AddAsync(entity, cancellationToken);
        environment.DesiredRevisionId = entity.Id;
        environment.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDesiredStateRevision(entity);
    }

    public async Task<WorkspaceDesiredStateRevision?> GetRevisionAsync(
        Guid workspaceId,
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DesiredStateRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == revisionId, cancellationToken);
        return entity is null ? null : ToWorkspaceDesiredStateRevision(entity);
    }

    public async Task<WorkspaceDesiredStateRevision?> GetLatestRevisionAsync(
        Guid workspaceId,
        Guid environmentId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DesiredStateRevisions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.EnvironmentId == environmentId)
            .OrderByDescending(x => x.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToWorkspaceDesiredStateRevision(entity);
    }

    public async Task<IReadOnlyList<WorkspaceDesiredStateRevisionSummary>> ListApplicationRevisionsAsync(
        Guid workspaceId,
        Guid applicationId,
        CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.DesiredStateRevisions
            .AsNoTracking()
            .Include(x => x.Environment)
                .ThenInclude(x => x!.TierDefinition)
            .Where(x => x.WorkspaceId == workspaceId && x.ApplicationId == applicationId)
            .OrderByDescending(x => x.AuthoredAt)
            .ThenByDescending(x => x.RevisionNumber)
            .ToListAsync(cancellationToken);

        var revisionIds = revisions.Select(x => x.Id).ToHashSet();
        IReadOnlyList<DeploymentRunEntity> runs = revisionIds.Count == 0
            ? []
            : await dbContext.DeploymentRuns
                .AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && revisionIds.Contains(x.SourceRevisionId))
                .OrderByDescending(x => x.QueuedAt)
                .ToListAsync(cancellationToken);
        var latestRuns = runs
            .GroupBy(x => x.SourceRevisionId)
            .ToDictionary(x => x.Key, x => x.First());

        return revisions.Select(x => ToWorkspaceDesiredStateRevisionSummary(x, latestRuns.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<WorkspaceDesiredStateRevisionDetail?> GetRevisionDetailAsync(
        Guid workspaceId,
        Guid revisionId,
        CancellationToken cancellationToken = default)
    {
        var revision = await dbContext.DesiredStateRevisions
            .AsNoTracking()
            .Include(x => x.Environment)
                .ThenInclude(x => x!.TierDefinition)
            .Include(x => x.Records)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == revisionId, cancellationToken);
        if (revision is null)
            return null;

        var runs = await dbContext.DeploymentRuns
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.SourceRevisionId == revisionId)
            .OrderByDescending(x => x.QueuedAt)
            .ToListAsync(cancellationToken);
        var latestRun = runs.FirstOrDefault();

        return new WorkspaceDesiredStateRevisionDetail(
            ToWorkspaceDesiredStateRevisionSummary(revision, latestRun),
            revision.Records.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.Ordinal).Select(ToWorkspaceDesiredStateRevisionRecord).ToList(),
            runs.Select(ToWorkspaceDesiredStateRevisionRunSummary).ToList());
    }

    public async Task<WorkspaceWorkflowEngine?> GetEngineAsync(
        Guid workspaceId,
        Guid engineId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WorkflowEngines
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == engineId, cancellationToken);
        return entity is null ? null : ToWorkspaceWorkflowEngine(entity);
    }

    public async Task<IReadOnlyList<WorkspaceWorkflowEngine>> ListEnginesDueForVerificationAsync(
        DateTimeOffset verifyBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            return [];

        var entities = await dbContext.WorkflowEngines
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return entities
            .Where(x => !x.LastVerificationAt.HasValue || x.LastVerificationAt <= verifyBefore)
            .OrderBy(x => x.LastVerificationAt.HasValue)
            .ThenBy(x => x.LastVerificationAt)
            .ThenBy(x => x.Id)
            .Take(limit)
            .Select(ToWorkspaceWorkflowEngine)
            .ToList();
    }

    public async Task<EngineHealthResult> UpdateEngineHealthAsync(
        Guid workspaceId,
        EngineHealthUpdate update,
        CancellationToken cancellationToken = default)
    {
        var engine = await dbContext.WorkflowEngines
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == update.EngineId, cancellationToken);
        if (engine is null)
            throw new KeyNotFoundException("Workflow engine does not exist in the workspace.");
        if (engine.EnvironmentId != update.EnvironmentId)
            throw new InvalidOperationException("Health update environment does not match the registered engine.");

        ApplyHealthUpdate(engine, update);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToEngineHealthResult(engine);
    }

    public async Task<IReadOnlyList<WorkspaceArtifact>> ListArtifactsAsync(
        Guid workspaceId,
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.WorkspaceDeploymentArtifacts
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);
        if (!includeArchived)
            query = query.Where(x => x.Status == WorkspaceArtifactLifecycleStatus.Active);

        var artifacts = await query
            .OrderByDescending(x => x.RegisteredAt)
            .ThenBy(x => x.ArtifactId)
            .ToListAsync(cancellationToken);
        return artifacts.Select(ToWorkspaceArtifact).ToList();
    }

    public async Task<WorkspaceArtifact?> GetArtifactAsync(
        Guid workspaceId,
        Guid artifactRecordId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await dbContext.WorkspaceDeploymentArtifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == artifactRecordId, cancellationToken);
        return artifact is null ? null : ToWorkspaceArtifact(artifact);
    }

    public async Task<WorkspaceArtifact?> FindArtifactByIdentityAsync(
        Guid workspaceId,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await dbContext.WorkspaceDeploymentArtifacts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.ArtifactId == artifactId, cancellationToken);
        return artifact is null ? null : ToWorkspaceArtifact(artifact);
    }

    public async Task<WorkspaceArtifact> RegisterArtifactAsync(
        Guid workspaceId,
        RegisterWorkspaceArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var artifact = new WorkspaceDeploymentArtifactEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ArtifactId = request.ArtifactId,
            LayoutVersion = request.LayoutVersion,
            ContentDigestAlgorithm = request.ContentDigest.Algorithm,
            ContentDigest = request.ContentDigest.Value,
            EnvelopeVersion = NormalizeEnvelopeVersion(request.EnvelopeVersion),
            ArtifactTypeId = NormalizeArtifactTypeId(request.ArtifactTypeId),
            ArtifactSchemaVersion = NormalizeArtifactSchemaVersion(request.ArtifactSchemaVersion),
            ManifestDigestAlgorithm = request.ManifestDigest?.Algorithm,
            ManifestDigest = request.ManifestDigest?.Value,
            PayloadReferenceJson = JsonSerializer.Serialize(NormalizePayloadReference(request)),
            ProducerJson = JsonSerializer.Serialize(NormalizeProducer(request.Producer)),
            DisplayMetadataJson = JsonSerializer.Serialize(NormalizeDisplayMetadata(request)),
            CompatibilityHintsJson = JsonSerializer.Serialize(NormalizeCompatibilityHints(request.ArtifactTypeId, request.CompatibilityHints)),
            Format = request.Format,
            ReferenceProvider = request.ReferenceProvider,
            Reference = request.Reference,
            ManifestName = request.Manifest.Name,
            ManifestVersion = request.Manifest.Version,
            ManifestEnvironment = request.Manifest.Environment,
            ResourceCount = request.Resources.Count,
            ResourceSummaryJson = JsonSerializer.Serialize(request.Resources),
            ChecksumStatus = WorkspaceArtifactChecksumStatus.Unverified,
            InspectionStatus = WorkspaceArtifactInspectionStatus.NeverInspected,
            DiagnosticsJson = JsonSerializer.Serialize(request.Diagnostics),
            RegisteredAt = now,
            RegisteredByAccountId = request.ActorAccountId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await dbContext.WorkspaceDeploymentArtifacts.AddAsync(artifact, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceArtifact(artifact);
    }

    public async Task<WorkspaceArtifact> ArchiveArtifactAsync(
        Guid workspaceId,
        Guid artifactRecordId,
        Guid actorAccountId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await dbContext.WorkspaceDeploymentArtifacts
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == artifactRecordId, cancellationToken);
        if (artifact is null)
            throw new KeyNotFoundException("Artifact does not exist in the workspace.");
        if (artifact.Status == WorkspaceArtifactLifecycleStatus.Archived)
            return ToWorkspaceArtifact(artifact);

        var now = DateTimeOffset.UtcNow;
        artifact.Status = WorkspaceArtifactLifecycleStatus.Archived;
        artifact.ArchivedAt = now;
        artifact.ArchivedByAccountId = actorAccountId;
        artifact.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceArtifact(artifact);
    }

    public async Task<WorkspaceArtifact> RestoreArtifactAsync(
        Guid workspaceId,
        Guid artifactRecordId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await dbContext.WorkspaceDeploymentArtifacts
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == artifactRecordId, cancellationToken);
        if (artifact is null)
            throw new KeyNotFoundException("Artifact does not exist in the workspace.");
        if (artifact.Status == WorkspaceArtifactLifecycleStatus.Active)
            return ToWorkspaceArtifact(artifact);

        artifact.Status = WorkspaceArtifactLifecycleStatus.Active;
        artifact.ArchivedAt = null;
        artifact.ArchivedByAccountId = null;
        artifact.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceArtifact(artifact);
    }

    public async Task<WorkspaceArtifactInspectionResult> UpdateArtifactInspectionAsync(
        Guid workspaceId,
        WorkspaceArtifactInspectionUpdate update,
        CancellationToken cancellationToken = default)
    {
        var artifact = await dbContext.WorkspaceDeploymentArtifacts
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == update.ArtifactRecordId, cancellationToken);
        if (artifact is null)
            throw new KeyNotFoundException("Artifact does not exist in the workspace.");
        if (!artifact.ArtifactId.Equals(update.ArtifactId, StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact inspection update cannot change the registered artifact identity.");

        artifact.ChecksumStatus = update.ChecksumStatus;
        artifact.InspectionStatus = update.InspectionStatus;
        artifact.LastInspectedAt = update.LastInspectedAt;
        artifact.ResourceCount = update.Resources.Count;
        artifact.ResourceSummaryJson = JsonSerializer.Serialize(update.Resources);
        artifact.DiagnosticsJson = JsonSerializer.Serialize(update.Diagnostics);
        artifact.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new WorkspaceArtifactInspectionResult(
            artifact.Id,
            artifact.ArtifactId,
            artifact.ChecksumStatus,
            artifact.InspectionStatus,
            artifact.LastInspectedAt,
            artifact.ResourceCount,
            DeserializeArtifactResources(artifact.ResourceSummaryJson),
            DeserializeArtifactDiagnostics(artifact.DiagnosticsJson));
    }

    public async Task<WorkspaceArtifactUploadSession> CreateArtifactUploadSessionAsync(
        WorkspaceArtifactUploadSession session,
        CancellationToken cancellationToken = default)
    {
        var entity = ToWorkspaceArtifactUploadSessionEntity(session);
        await dbContext.WorkspaceArtifactUploadSessions.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceArtifactUploadSession(entity);
    }

    public async Task<WorkspaceArtifactUploadSession?> GetArtifactUploadSessionAsync(
        Guid workspaceId,
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WorkspaceArtifactUploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == uploadId, cancellationToken);
        return entity is null ? null : ToWorkspaceArtifactUploadSession(entity);
    }

    public async Task<WorkspaceArtifactUploadSession?> FindArtifactUploadByIdempotencyKeyAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WorkspaceArtifactUploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.IdempotencyKey == idempotencyKey, cancellationToken);
        return entity is null ? null : ToWorkspaceArtifactUploadSession(entity);
    }

    public async Task<WorkspaceArtifactUploadSession> UpdateArtifactUploadSessionAsync(
        WorkspaceArtifactUploadSession session,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.WorkspaceArtifactUploadSessions
            .SingleOrDefaultAsync(x => x.WorkspaceId == session.WorkspaceId && x.Id == session.Id, cancellationToken);
        if (entity is null)
            throw new KeyNotFoundException("Artifact upload session does not exist in the workspace.");

        entity.Status = session.Status;
        entity.FileName = session.FileName;
        entity.ContentType = session.ContentType;
        entity.DeclaredSizeBytes = session.DeclaredSizeBytes;
        entity.UploadedSizeBytes = session.UploadedSizeBytes;
        entity.StagedFilePath = session.StagedFilePath;
        entity.IdempotencyKey = session.IdempotencyKey;
        entity.DiagnosticsJson = JsonSerializer.Serialize(session.Diagnostics);
        entity.ExpiresAt = session.ExpiresAt;
        entity.CompletedArtifactRecordId = session.CompletedArtifactRecordId;
        entity.CreatedByAccountId = session.CreatedByAccountId;
        entity.UpdatedAt = session.UpdatedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceArtifactUploadSession(entity);
    }

    public async Task<EngineHealthResult> ApplyEngineHeartbeatAsync(
        Guid workspaceId,
        EngineHealthUpdate update,
        CancellationToken cancellationToken = default)
    {
        var engine = await dbContext.WorkflowEngines
            .Include(x => x.Capabilities)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == update.EngineId, cancellationToken);
        if (engine is null)
            throw new KeyNotFoundException("Workflow engine does not exist in the workspace.");
        if (engine.EnvironmentId != update.EnvironmentId)
            throw new InvalidOperationException("Heartbeat environment does not match the registered engine.");
        if (engine.LastHeartbeatAt.HasValue && update.LastHeartbeatAt.HasValue && update.LastHeartbeatAt <= engine.LastHeartbeatAt)
            throw new InvalidOperationException("Heartbeat is stale.");

        ApplyHealthUpdate(engine, update);
        if (update.Capabilities is not null)
        {
            await dbContext.EngineCapabilities
                .Where(x => x.WorkspaceId == workspaceId && x.EngineId == engine.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await dbContext.EngineCapabilities.AddRangeAsync(update.Capabilities.Select(capability => new EngineCapabilityEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                EngineId = engine.Id,
                CapabilityId = capability.Id,
                Label = capability.Label,
                Boundary = capability.Boundary
            }), cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToEngineHealthResult(engine);
    }

    private async Task<WorkspaceDeploymentEnvironment> CreateEnvironmentCoreAsync(
        Guid workspaceId,
        CreateDeploymentEnvironmentRequest request,
        CancellationToken cancellationToken)
    {
        var applicationExists = await dbContext.DeploymentApplications
            .AnyAsync(x => x.WorkspaceId == workspaceId && x.Id == request.ApplicationId, cancellationToken);
        if (!applicationExists)
            throw new InvalidOperationException("Deployment application does not exist in the workspace.");

        var now = DateTimeOffset.UtcNow;
        var tier = await ResolveTierForEnvironmentAsync(workspaceId, request.Tier, request.TierId, requireActive: true, cancellationToken);
        var entity = new DeploymentEnvironmentEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            ApplicationId = request.ApplicationId,
            Name = request.Name,
            Tier = request.Tier,
            TierId = tier.Id,
            TierDefinition = tier,
            DeploymentStatus = DeploymentStatus.Blocked,
            DriftStatus = DriftStatus.Unknown,
            CreatedAt = now,
            UpdatedAt = now
        };

        await dbContext.DeploymentEnvironments.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToWorkspaceDeploymentEnvironment(entity);
    }

    private static EnvironmentSummary ToEnvironmentSummary(DeploymentEnvironmentEntity environment, IReadOnlyList<WorkflowEngineEntity> engines)
    {
        var desiredRevision = environment.Revisions
            .SingleOrDefault(x => x.Id == environment.DesiredRevisionId)
            ?? environment.Revisions.OrderByDescending(x => x.RevisionNumber).FirstOrDefault();
        var deployedRevision = environment.Revisions.SingleOrDefault(x => x.Id == environment.DeployedRevisionId);

        return new EnvironmentSummary(
            environment.Id.ToString("D"),
            environment.Name,
            environment.Tier,
            EnvironmentHealth(environment, engines),
            desiredRevision is null
                ? new DesiredStateRevision("", 0, "", "No desired revision", environment.CreatedAt)
                : new DesiredStateRevision(desiredRevision.Id.ToString("D"), desiredRevision.RevisionNumber, desiredRevision.Commit ?? "", desiredRevision.Label, desiredRevision.AuthoredAt),
            deployedRevision?.RevisionNumber,
            environment.DeploymentStatus,
            environment.DriftStatus,
            engines.Where(x => x.EnvironmentId == environment.Id).Select(x => x.Id.ToString("D")).ToList(),
            environment.TierDefinition?.Name ?? environment.Tier.ToString(),
            environment.TierDefinition?.Status.ToString() ?? DeploymentTierStatus.Active.ToString(),
            environment.TierDefinition?.Capabilities.OrderBy(x => x.CapabilityId).Select(x => x.CapabilityId).ToList() ?? DeploymentTierService.DefaultCapabilitiesByLegacyTier[environment.Tier]);
    }

    private static DeploymentHistoryEvent ToDeploymentHistoryEvent(
        DeploymentRunEntity run,
        IReadOnlyDictionary<Guid, DesiredStateRevisionEntity> revisions,
        IReadOnlyDictionary<Guid, List<DeploymentRunCommandSummary>> commandsByRunId)
    {
        revisions.TryGetValue(run.SourceRevisionId, out var sourceRevision);
        DesiredStateRevisionEntity? rollbackSourceRevision = null;
        if (run.PreviousDeployedRevisionId.HasValue)
            revisions.TryGetValue(run.PreviousDeployedRevisionId.Value, out rollbackSourceRevision);
        commandsByRunId.TryGetValue(run.Id, out var commands);

        return new DeploymentHistoryEvent(
            run.Id.ToString("D"),
            run.Status.ToString(),
            sourceRevision?.RevisionNumber ?? 0,
            run.ActorAccountId.ToString("N")[..8],
            run.EnvironmentId.ToString("D"),
            run.EngineId.ToString("D"),
            run.ValidationOutcome,
            run.CompletedAt ?? run.StartedAt ?? run.QueuedAt,
            rollbackSourceRevision?.RevisionNumber,
            commands ?? []);
    }

    private static WorkspaceDeploymentEnvironment ToWorkspaceDeploymentEnvironment(DeploymentEnvironmentEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.ApplicationId,
            entity.Name,
            entity.Tier,
            entity.DesiredRevisionId,
            entity.DeployedRevisionId,
            entity.DeploymentStatus,
            entity.DriftStatus,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.TierId,
            ToDeploymentTierProfile(entity.TierDefinition, entity.Tier, entity.TierRequiresReview));

    private static DeploymentTierProfile? ToDeploymentTierProfile(DeploymentTierDefinitionEntity? tier, EnvironmentTier legacyTier, bool requiresReview)
    {
        if (tier is null)
            return new DeploymentTierProfile(Guid.Empty, legacyTier.ToString(), DeploymentTierStatus.Active, DeploymentTierService.DefaultCapabilitiesByLegacyTier[legacyTier], requiresReview);

        return new DeploymentTierProfile(
            tier.Id,
            tier.Name,
            tier.Status,
            tier.Capabilities.OrderBy(x => x.CapabilityId).Select(x => x.CapabilityId).ToList(),
            requiresReview);
    }

    private static WorkspaceDeploymentTier ToWorkspaceDeploymentTier(DeploymentTierDefinitionEntity tier, int environmentCount) =>
        new(
            tier.Id,
            tier.WorkspaceId,
            tier.Name,
            tier.Description,
            tier.SortOrder,
            tier.IsDefault,
            tier.Status,
            tier.Capabilities.OrderBy(x => x.CapabilityId).Select(x => x.CapabilityId).ToList(),
            environmentCount,
            tier.CreatedAt,
            tier.UpdatedAt,
            tier.CreatedByAccountId,
            tier.UpdatedByAccountId,
            tier.ArchivedAt,
            tier.ArchivedByAccountId);

    private static WorkspaceDeploymentSecretStore ToWorkspaceDeploymentSecretStore(DeploymentSecretStoreEntity store) =>
        new(
            store.Id,
            store.WorkspaceId,
            store.Name,
            store.Provider,
            store.Type,
            store.Description,
            store.Status,
            store.CreatedAt,
            store.UpdatedAt,
            store.CreatedByAccountId,
            store.UpdatedByAccountId,
            store.ArchivedAt,
            store.ArchivedByAccountId);

    private static WorkspaceDeploymentCredentialReference ToWorkspaceDeploymentCredentialReference(DeploymentCredentialReferenceEntity reference, int? usageCount = null) =>
        new(
            reference.Id,
            reference.WorkspaceId,
            reference.SecretStoreId,
            reference.SecretStore?.Name ?? "",
            reference.SecretStore?.Provider ?? "",
            reference.SecretStore?.Type ?? DeploymentSecretStoreType.GenericExternalReference,
            reference.Name,
            reference.Reference,
            reference.Description,
            reference.Status,
            reference.VerificationStatus,
            reference.LastVerifiedAt,
            reference.CreatedAt,
            reference.UpdatedAt,
            reference.CreatedByAccountId,
            reference.UpdatedByAccountId,
            reference.ArchivedAt,
            reference.ArchivedByAccountId,
            !string.IsNullOrEmpty(reference.ProtectedSecret),
            usageCount ?? reference.Engines.Count);

    private static WorkflowEngineRegistration ToEngineRegistration(WorkflowEngineEntity engine) =>
        new(
            engine.Id.ToString("D"),
            engine.Name,
            engine.EnvironmentId.ToString("D"),
            new EngineEndpointMetadata(engine.BaseUrl, engine.Region ?? "", engine.Version ?? "", engine.CertificateStatus),
            new EngineCredentialReference(engine.CredentialProvider, engine.CredentialReference, engine.CredentialVerificationStatus, engine.CredentialLastVerifiedAt),
            engine.Health,
            engine.LastHeartbeatAt,
            engine.Capabilities.OrderBy(x => x.CapabilityId).Select(x => new EngineCapability(x.CapabilityId, x.Label, x.Boundary)).ToList(),
            engine.Controls.OrderBy(x => x.ControlId).Select(x => new RuntimeControl(x.ControlId, x.Label, x.Boundary, x.RequiredCapabilityId, x.Description)).ToList(),
            engine.HostingProvider,
            engine.LastVerificationAt,
            engine.VerificationMessage,
            engine.CredentialAssignmentStatus);

    private static ObservabilityBinding ToObservabilityBinding(ObservabilityBindingEntity binding) =>
        new(
            binding.Id.ToString("D"),
            binding.Kind,
            binding.Provider,
            binding.Status,
            binding.Scope,
            0,
            binding.Sample ?? "");

    private static DriftReportItem ToDriftReportItem(DriftReportItemEntity item) =>
        new(
            item.Id.ToString("D"),
            item.EnvironmentId.ToString("D"),
            item.EngineId.ToString("D"),
            item.Area,
            item.Desired,
            item.Observed,
            item.Action);

    private static WorkspaceWorkflowEngine ToWorkspaceWorkflowEngine(WorkflowEngineEntity engine) =>
        new(
            engine.Id,
            engine.WorkspaceId,
            engine.EnvironmentId,
            engine.Name,
            engine.BaseUrl,
            engine.Region,
            engine.Version,
            engine.CertificateStatus,
            engine.CredentialProvider,
            engine.CredentialReference,
            engine.CredentialVerificationStatus,
            engine.CredentialLastVerifiedAt,
            engine.Health,
            engine.LastHeartbeatAt,
            engine.HostingProvider,
            engine.CreatedAt,
            engine.UpdatedAt,
            engine.LastVerificationAt,
            engine.VerificationMessage,
            engine.CredentialReferenceId,
            engine.CredentialAssignmentStatus);

    private static WorkspaceArtifact ToWorkspaceArtifact(WorkspaceDeploymentArtifactEntity artifact) =>
        new(
            artifact.Id,
            artifact.WorkspaceId,
            artifact.ArtifactId,
            artifact.LayoutVersion,
            new WorkspaceArtifactDigest(artifact.ContentDigestAlgorithm, artifact.ContentDigest),
            artifact.Format,
            artifact.ReferenceProvider,
            artifact.Reference,
            new WorkspaceArtifactManifestSummary(artifact.ManifestName, artifact.ManifestVersion, artifact.ManifestEnvironment),
            DeserializeArtifactResources(artifact.ResourceSummaryJson),
            artifact.ChecksumStatus,
            artifact.InspectionStatus,
            DeserializeArtifactDiagnostics(artifact.DiagnosticsJson),
            artifact.RegisteredAt,
            artifact.RegisteredByAccountId,
            artifact.LastInspectedAt,
            artifact.CreatedAt,
            artifact.UpdatedAt,
            NormalizeEnvelopeVersion(artifact.EnvelopeVersion),
            NormalizeArtifactTypeId(artifact.ArtifactTypeId),
            NormalizeArtifactSchemaVersion(artifact.ArtifactSchemaVersion),
            artifact.ManifestDigestAlgorithm is null || artifact.ManifestDigest is null
                ? null
                : new WorkspaceArtifactDigest(artifact.ManifestDigestAlgorithm, artifact.ManifestDigest),
            DeserializePayloadReference(artifact.PayloadReferenceJson, artifact.ReferenceProvider, artifact.Reference),
            DeserializeProducer(artifact.ProducerJson),
            DeserializeDisplayMetadata(artifact.DisplayMetadataJson, artifact.ManifestName, artifact.ManifestVersion, artifact.ManifestEnvironment),
            DeserializeCompatibilityHints(artifact.CompatibilityHintsJson, artifact.ArtifactTypeId),
            artifact.Status,
            artifact.ArchivedAt,
            artifact.ArchivedByAccountId);

    private static IReadOnlyList<WorkspaceArtifactResourceSummary> DeserializeArtifactResources(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<WorkspaceArtifactResourceSummary>>(json) ?? [];

    private static IReadOnlyList<WorkspaceArtifactDiagnostic> DeserializeArtifactDiagnostics(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<WorkspaceArtifactDiagnostic>>(json) ?? [];

    private static WorkspaceArtifactUploadSession ToWorkspaceArtifactUploadSession(WorkspaceArtifactUploadSessionEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.Status,
            entity.FileName,
            entity.ContentType,
            entity.DeclaredSizeBytes,
            entity.UploadedSizeBytes,
            entity.StagedFilePath,
            entity.IdempotencyKey,
            DeserializeArtifactDiagnostics(entity.DiagnosticsJson),
            entity.ExpiresAt,
            entity.CompletedArtifactRecordId,
            entity.CreatedByAccountId,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static WorkspaceArtifactUploadSessionEntity ToWorkspaceArtifactUploadSessionEntity(WorkspaceArtifactUploadSession session) =>
        new()
        {
            Id = session.Id,
            WorkspaceId = session.WorkspaceId,
            Status = session.Status,
            FileName = session.FileName,
            ContentType = session.ContentType,
            DeclaredSizeBytes = session.DeclaredSizeBytes,
            UploadedSizeBytes = session.UploadedSizeBytes,
            StagedFilePath = session.StagedFilePath,
            IdempotencyKey = session.IdempotencyKey,
            DiagnosticsJson = JsonSerializer.Serialize(session.Diagnostics),
            ExpiresAt = session.ExpiresAt,
            CompletedArtifactRecordId = session.CompletedArtifactRecordId,
            CreatedByAccountId = session.CreatedByAccountId,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt
        };

    private static string NormalizeEnvelopeVersion(string? envelopeVersion) =>
        string.IsNullOrWhiteSpace(envelopeVersion) ? ArtifactEnvelopeConstants.EnvelopeVersion : envelopeVersion;

    private static string NormalizeArtifactTypeId(string? artifactTypeId) =>
        ArtifactEnvelopeDefaults.ArtifactTypeIdOrDefault(artifactTypeId);

    private static string NormalizeArtifactSchemaVersion(string? artifactSchemaVersion) =>
        string.IsNullOrWhiteSpace(artifactSchemaVersion) ? ArtifactEnvelopeConstants.DefaultArtifactSchemaVersion : artifactSchemaVersion;

    private static ArtifactPayloadReference NormalizePayloadReference(RegisterWorkspaceArtifactRequest request) =>
        request.PayloadReference ?? ArtifactEnvelopeDefaults.DefaultPayloadReference(request.ReferenceProvider, request.Reference);

    private static ArtifactProducer NormalizeProducer(ArtifactProducer? producer) =>
        producer ?? ArtifactEnvelopeDefaults.DefaultProducer();

    private static ArtifactDisplayMetadata NormalizeDisplayMetadata(RegisterWorkspaceArtifactRequest request) =>
        request.DisplayMetadata ?? ArtifactEnvelopeDefaults.DefaultDisplayMetadata(
            request.Manifest.Name,
            request.Manifest.Version,
            request.Manifest.Environment);

    private static IReadOnlyList<ArtifactCompatibilityHint> NormalizeCompatibilityHints(string? artifactTypeId, IReadOnlyList<ArtifactCompatibilityHint>? compatibilityHints) =>
        compatibilityHints ?? ArtifactEnvelopeDefaults.DefaultCompatibilityHints(artifactTypeId);

    private static ArtifactPayloadReference DeserializePayloadReference(string json, string referenceProvider, string reference) =>
        string.IsNullOrWhiteSpace(json)
            ? ArtifactEnvelopeDefaults.DefaultPayloadReference(referenceProvider, reference)
            : JsonSerializer.Deserialize<ArtifactPayloadReference>(json) ?? ArtifactEnvelopeDefaults.DefaultPayloadReference(referenceProvider, reference);

    private static ArtifactProducer DeserializeProducer(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? ArtifactEnvelopeDefaults.DefaultProducer()
            : JsonSerializer.Deserialize<ArtifactProducer>(json) ?? ArtifactEnvelopeDefaults.DefaultProducer();

    private static ArtifactDisplayMetadata DeserializeDisplayMetadata(string json, string? manifestName, string? manifestVersion, string? manifestEnvironment) =>
        string.IsNullOrWhiteSpace(json)
            ? ArtifactEnvelopeDefaults.DefaultDisplayMetadata(manifestName, manifestVersion, manifestEnvironment)
            : JsonSerializer.Deserialize<ArtifactDisplayMetadata>(json) ?? ArtifactEnvelopeDefaults.DefaultDisplayMetadata(manifestName, manifestVersion, manifestEnvironment);

    private static IReadOnlyList<ArtifactCompatibilityHint> DeserializeCompatibilityHints(string json, string? artifactTypeId) =>
        string.IsNullOrWhiteSpace(json)
            ? NormalizeCompatibilityHints(artifactTypeId, null)
            : JsonSerializer.Deserialize<IReadOnlyList<ArtifactCompatibilityHint>>(json) ?? NormalizeCompatibilityHints(artifactTypeId, null);

    private static void ApplyHealthUpdate(WorkflowEngineEntity engine, EngineHealthUpdate update)
    {
        engine.Version = update.Version;
        engine.CertificateStatus = update.CertificateStatus;
        engine.CredentialVerificationStatus = update.CredentialVerificationStatus;
        engine.CredentialLastVerifiedAt = update.CredentialLastVerifiedAt;
        engine.Health = update.Health;
        engine.LastHeartbeatAt = update.LastHeartbeatAt;
        engine.LastVerificationAt = update.LastVerificationAt;
        engine.VerificationMessage = update.VerificationMessage;
        engine.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static EngineHealthResult ToEngineHealthResult(WorkflowEngineEntity engine) =>
        new(
            engine.Id,
            engine.EnvironmentId,
            engine.Health,
            engine.Version,
            engine.CertificateStatus,
            engine.CredentialVerificationStatus,
            engine.CredentialLastVerifiedAt,
            engine.LastHeartbeatAt,
            engine.LastVerificationAt,
            engine.VerificationMessage);

    private static WorkspaceDesiredStateRevision ToWorkspaceDesiredStateRevision(DesiredStateRevisionEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.ApplicationId,
            entity.EnvironmentId,
            entity.RevisionNumber,
            entity.Label,
            entity.Commit,
            entity.ContentHash,
            entity.DesiredStateJson,
            entity.AuthoredAt,
            entity.CreatedAt,
            entity.CreatedByAccountId);

    private static WorkspaceDesiredStateRevisionSummary ToWorkspaceDesiredStateRevisionSummary(DesiredStateRevisionEntity entity, DeploymentRunEntity? latestRun) =>
        new(
            ToWorkspaceDesiredStateRevision(entity),
            entity.Environment?.Name ?? "",
            entity.Environment?.Tier ?? EnvironmentTier.Production,
            entity.Environment?.TierId,
            entity.Environment?.TierDefinition?.Name,
            entity.Environment?.DesiredRevisionId == entity.Id,
            entity.Environment?.DeployedRevisionId == entity.Id,
            latestRun?.Status,
            latestRun?.QueuedAt);

    private static WorkspaceDesiredStateRevisionRecord ToWorkspaceDesiredStateRevisionRecord(StructuredDesiredStateRecordEntity entity) =>
        new(
            entity.Id,
            entity.Kind,
            entity.Name,
            entity.PayloadJson,
            entity.ContentHash,
            entity.ArtifactRecordId,
            entity.ArtifactId,
            entity.ArtifactTypeId,
            entity.ArtifactDigestAlgorithm is null || entity.ArtifactDigest is null
                ? null
                : new WorkspaceArtifactDigest(entity.ArtifactDigestAlgorithm, entity.ArtifactDigest));

    private static WorkspaceDesiredStateRevisionRunSummary ToWorkspaceDesiredStateRevisionRunSummary(DeploymentRunEntity entity) =>
        new(
            entity.Id,
            entity.EnvironmentId,
            entity.EngineId,
            entity.Status,
            entity.ValidationOutcome,
            entity.QueuedAt,
            entity.CompletedAt,
            entity.FailureMessage);

    private static List<StructuredDesiredStateRecordEntity> ParseStructuredRecords(Guid workspaceId, Guid revisionId, string desiredStateJson)
    {
        using var document = JsonDocument.Parse(desiredStateJson);
        if (!document.RootElement.TryGetProperty("records", out var recordsElement) || recordsElement.ValueKind != JsonValueKind.Array)
            return [];

        return recordsElement.EnumerateArray()
            .Select(record =>
            {
                var kindName = record.TryGetProperty("kind", out var kindElement) ? GetString(kindElement) : null;
                var name = record.TryGetProperty("name", out var nameElement) ? GetString(nameElement) : null;
                if (!Enum.TryParse<DesiredStateRecordKind>(kindName, true, out var kind) || string.IsNullOrWhiteSpace(name))
                    return null;

                var payload = record.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                    ? payloadElement
                    : record;
                var payloadJson = record.TryGetProperty("payload", out var payloadValue) ? payloadValue.GetRawText() : "{}";
                var artifactReference = kind == DesiredStateRecordKind.ArtifactReference ? ParseArtifactReference(payload) : null;
                return new StructuredDesiredStateRecordEntity
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    RevisionId = revisionId,
                    Kind = kind,
                    Name = name,
                    PayloadJson = payloadJson,
                    ContentHash = WorkspaceDeploymentService.ComputeDesiredStateHash(payloadJson),
                    ArtifactRecordId = artifactReference?.ArtifactRecordId,
                    ArtifactId = artifactReference?.ArtifactId,
                    ArtifactTypeId = artifactReference?.ArtifactTypeId,
                    ArtifactDigestAlgorithm = artifactReference?.ContentDigest?.Algorithm,
                    ArtifactDigest = artifactReference?.ContentDigest?.Value
                };
            })
            .Where(record => record is not null)
            .Cast<StructuredDesiredStateRecordEntity>()
            .ToList();
    }

    private async Task<IReadOnlyList<DeploymentCommandArtifactItem>> ResolveArtifactItemsAsync(
        Guid workspaceId,
        string desiredStateJson,
        CancellationToken cancellationToken)
    {
        var artifactReferences = ParseArtifactReferences(desiredStateJson);
        var items = new List<DeploymentCommandArtifactItem>();
        foreach (var artifactReference in artifactReferences)
        {
            WorkspaceDeploymentArtifactEntity? artifact = null;
            if (artifactReference.ArtifactRecordId is not null)
            {
                artifact = await dbContext.WorkspaceDeploymentArtifacts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == artifactReference.ArtifactRecordId.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(artifactReference.ArtifactId))
            {
                artifact = await dbContext.WorkspaceDeploymentArtifacts
                    .AsNoTracking()
                    .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.ArtifactId == artifactReference.ArtifactId, cancellationToken);
            }

            if (artifact is null)
                throw new InvalidOperationException("Artifact-backed revision references an artifact that is not visible in the workspace.");
            ValidateArtifactReference(artifactReference, artifact);

            items.Add(new DeploymentCommandArtifactItem(
                artifact.Id,
                artifact.ArtifactId,
                artifact.ArtifactTypeId,
                artifact.ArtifactSchemaVersion,
                new WorkspaceArtifactDigest(artifact.ContentDigestAlgorithm, artifact.ContentDigest),
                SafeArtifactDisplayName(artifact),
                null));
        }

        return items;
    }

    private static void ValidateArtifactReference(
        DeploymentCommandArtifactReference reference,
        WorkspaceDeploymentArtifactEntity artifact)
    {
        if (reference.ArtifactRecordId is not null && reference.ArtifactRecordId != artifact.Id)
            throw new InvalidOperationException("Artifact-backed revision artifact record does not match the registered artifact.");
        if (!string.IsNullOrWhiteSpace(reference.ArtifactId) && !string.Equals(reference.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact-backed revision artifact identity does not match the registered artifact.");
        if (string.IsNullOrWhiteSpace(reference.ArtifactTypeId))
            throw new InvalidOperationException("Artifact-backed revision artifact type is missing.");
        if (!string.Equals(reference.ArtifactTypeId, artifact.ArtifactTypeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Artifact-backed revision artifact type does not match the registered artifact.");
        if (reference.ContentDigest is null)
            throw new InvalidOperationException("Artifact-backed revision artifact digest is missing.");
        if (!string.Equals(reference.ContentDigest.Algorithm, artifact.ContentDigestAlgorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reference.ContentDigest.Value, artifact.ContentDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact-backed revision artifact digest does not match the registered artifact.");
    }

    private static IReadOnlyList<DeploymentCommandArtifactReference> ParseArtifactReferences(string desiredStateJson)
    {
        try
        {
            using var document = JsonDocument.Parse(desiredStateJson);
            var records = document.RootElement.TryGetProperty("records", out var recordsElement) && recordsElement.ValueKind == JsonValueKind.Array
                ? recordsElement
                : document.RootElement;
            if (records.ValueKind != JsonValueKind.Array)
                return [];

            return records.EnumerateArray()
                .Where(record =>
                {
                    var kind = record.TryGetProperty("kind", out var kindElement) ? GetString(kindElement) : null;
                    return string.Equals(kind, DesiredStateRecordKind.ArtifactReference.ToString(), StringComparison.OrdinalIgnoreCase);
                })
                .Select(record =>
                {
                    var payload = record.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                        ? payloadElement
                        : record;
                    return ParseArtifactReference(payload);
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DeploymentCommandArtifactReference ParseArtifactReference(JsonElement payload)
    {
        var artifactRecordId = payload.TryGetProperty("artifactRecordId", out var artifactRecordIdElement)
            && artifactRecordIdElement.ValueKind == JsonValueKind.String
            && Guid.TryParse(artifactRecordIdElement.GetString(), out var parsedArtifactRecordId)
                ? parsedArtifactRecordId
                : (Guid?)null;
        var artifactId = payload.TryGetProperty("artifactId", out var artifactIdElement) ? GetString(artifactIdElement) : null;
        var artifactTypeId = payload.TryGetProperty("artifactTypeId", out var artifactTypeIdElement) ? GetString(artifactTypeIdElement) : null;
        var contentDigest = payload.TryGetProperty("contentDigest", out var digestElement) && digestElement.ValueKind == JsonValueKind.Object
            ? ParseArtifactDigest(digestElement)
            : null;
        return new DeploymentCommandArtifactReference(artifactRecordId, artifactId, artifactTypeId, contentDigest);
    }

    private static WorkspaceArtifactDigest? ParseArtifactDigest(JsonElement digestElement)
    {
        var algorithm = digestElement.TryGetProperty("algorithm", out var algorithmElement) ? GetString(algorithmElement) : null;
        var value = digestElement.TryGetProperty("value", out var valueElement) ? GetString(valueElement) : null;
        return string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(value)
            ? null
            : new WorkspaceArtifactDigest(algorithm, value);
    }

    private static string? GetString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static WorkspacePermissionGrant ToPermissionGrant(WorkspacePermissionGrantEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.AccountId,
            entity.Permission,
            entity.GrantedByAccountId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.RevokedAt,
            entity.RevokedByAccountId);

    private static WorkspacePermissionAuditRecordEntity PermissionAuditRecord(
        WorkspacePermissionGrantEntity grant,
        WorkspacePermissionAuditAction action,
        Guid? actorAccountId,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = grant.WorkspaceId,
            GrantId = grant.Id,
            AccountId = grant.AccountId,
            Permission = grant.Permission,
            Action = action,
            ActorAccountId = actorAccountId,
            OccurredAt = occurredAt
        };

    private static ActionConfirmation ToActionConfirmation(ActionConfirmationEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.ActionType,
            entity.TargetId,
            entity.ConfirmedByAccountId,
            entity.ConfirmedAt,
            entity.ExpiresAt,
            entity.UsedAt);

    private static WorkspaceDeploymentRun ToWorkspaceDeploymentRun(DeploymentRunEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.ApplicationId,
            entity.EnvironmentId,
            entity.EngineId,
            entity.SourceRevisionId,
            entity.PreviousDeployedRevisionId,
            entity.RollbackSourceRunId,
            entity.Status,
            entity.ValidationOutcome,
            entity.ConfirmationId,
            entity.ActorAccountId,
            entity.QueuedAt,
            entity.StartedAt,
            entity.CompletedAt,
            entity.CreatedAt,
            entity.WorkerId,
            entity.WorkerHeartbeatAt,
            entity.AttemptNumber,
            entity.RecoveryReason,
            entity.FailureMessage);

    private static DeploymentRunHistoryEvent ToDeploymentRunHistoryEvent(DeploymentRunHistoryEventEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.RunId,
            entity.Status,
            entity.Message,
            entity.CreatedAt);

    private static DeploymentRunCommandSummary ToDeploymentRunCommandSummary(DeploymentCommandEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.RunId,
            entity.EnvironmentId,
            entity.EngineId,
            entity.Action,
            entity.Status,
            DeserializeArtifactReference(entity.ArtifactJson),
            entity.WorkerId,
            entity.ClaimedAt,
            entity.LeaseExpiresAt,
            entity.HeartbeatAt,
            entity.AttemptNumber,
            entity.PercentComplete,
            entity.ProgressMessage,
            GetObservedArtifactDigest(entity),
            entity.RuntimeReference,
            DeserializeDiagnostics(entity.DiagnosticsJson),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt,
            DeserializeArtifactItems(entity.ArtifactJson));

    private static DeploymentCommandEntity CreateCommandEntity(
        Guid workspaceId,
        CreateDeploymentCommandRequest request,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            RunId = request.RunId,
            EnvironmentId = request.EnvironmentId,
            EngineId = request.EngineId,
            Action = request.Action,
            Status = DeploymentCommandStatus.Pending,
            ArtifactJson = SerializeArtifactPayload(request.Artifact, request.Artifacts),
            RevisionId = request.Revision?.RevisionId,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            AttemptNumber = 0,
            DiagnosticsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = request.AvailableAt,
            ExpiresAt = request.ExpiresAt
        };

    private Task<DeploymentCommand> FinalizeCommandAsync(
        Guid workspaceId,
        Guid commandId,
        string leaseToken,
        DeploymentCommandStatus status,
        IReadOnlyList<DeploymentCommandDiagnostic> diagnostics,
        IReadOnlyList<DeploymentCommandArtifactOutcome>? artifacts,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        // Same unit boundary as CompleteCommandAsync: command and run fail together.
        dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var command = await LoadCommandForUpdateAsync(workspaceId, commandId, cancellationToken);
            if (command.Status == status)
            {
                ValidateFinalLease(command, leaseToken);
                return ToDeploymentCommand(command);
            }
            if (IsFinal(command.Status))
                throw new InvalidOperationException("Command is already final.");

            ValidateLease(command, leaseToken, now);

            command.Status = status;
            command.DiagnosticsJson = JsonSerializer.Serialize(diagnostics);
            command.ArtifactJson = ApplyArtifactOutcomes(command.ArtifactJson, artifacts);
            command.CompletedAt = now;
            command.UpdatedAt = now;

            await AddCommandAndRunEventAsync(command, status, message, now, cancellationToken);
            await UpdateRunStatusInTransactionAsync(
                workspaceId,
                command.RunId,
                WorkspaceDeploymentRunStatus.Failed,
                message,
                now,
                diagnostics.FirstOrDefault(x => x.Severity == DeploymentCommandDiagnosticSeverity.Error)?.Message,
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToDeploymentCommand(command);
        }, cancellationToken);

    private async Task ThrowClaimConflictAsync(
        Guid workspaceId,
        Guid commandId,
        Guid engineId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var command = await dbContext.DeploymentCommands
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == commandId, cancellationToken);
        if (command is null)
            throw new KeyNotFoundException("Deployment command does not exist in the workspace.");
        if (command.EngineId != engineId)
            throw new InvalidOperationException("Command does not target the requested runtime engine.");
        if (IsFinal(command.Status))
            throw new InvalidOperationException("Command is already final.");
        if (command.AvailableAt is not null && command.AvailableAt > now)
            throw new InvalidOperationException("Command is not available.");
        if (command.ExpiresAt is not null && command.ExpiresAt <= now)
            throw new InvalidOperationException("Command is expired.");
        throw new InvalidOperationException("Command is already leased.");
    }

    private async Task<DeploymentCommandEntity> LoadCommandForUpdateAsync(
        Guid workspaceId,
        Guid commandId,
        CancellationToken cancellationToken) =>
        await dbContext.DeploymentCommands
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == commandId, cancellationToken)
        ?? throw new KeyNotFoundException("Deployment command does not exist in the workspace.");

    private void DetachTrackedCommand(Guid commandId)
    {
        var tracked = dbContext.DeploymentCommands.Local.FirstOrDefault(x => x.Id == commandId);
        if (tracked is not null)
            dbContext.Entry(tracked).State = EntityState.Detached;
    }

    private async Task<DeploymentCommandEntity> LoadCommandForLeaseMutationAsync(
        Guid workspaceId,
        Guid commandId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var command = await LoadCommandForUpdateAsync(workspaceId, commandId, cancellationToken);
        if (IsFinal(command.Status))
            throw new InvalidOperationException("Command is already final.");
        ValidateLease(command, leaseToken, now);
        return command;
    }

    private static void ValidateLease(DeploymentCommandEntity command, string leaseToken, DateTimeOffset now)
    {
        if (!string.Equals(command.LeaseToken, leaseToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Command lease token is invalid.");
        if (command.LeaseExpiresAt is not null && command.LeaseExpiresAt <= now)
            throw new InvalidOperationException("Command lease has expired.");
        if (command.Status is not DeploymentCommandStatus.Claimed and not DeploymentCommandStatus.Running)
            throw new InvalidOperationException("Command is not currently leased.");
    }

    private static void ValidateFinalLease(DeploymentCommandEntity command, string leaseToken)
    {
        if (!string.Equals(command.LeaseToken, leaseToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Command lease token is invalid.");
    }

    private static void ValidateObservedArtifactDigest(DeploymentCommandEntity command, WorkspaceArtifactDigest? observed)
    {
        var artifact = DeserializeArtifactReference(command.ArtifactJson);
        if (artifact?.ContentDigest is null)
            return;

        if (observed is null
            || !string.Equals(observed.Algorithm, artifact.ContentDigest.Algorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(observed.Value, artifact.ContentDigest.Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Observed artifact digest does not match command artifact digest.");
    }

    private async Task TouchCommandRunHeartbeatAsync(
        DeploymentCommandEntity command,
        string? workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.DeploymentRuns.SingleAsync(x => x.WorkspaceId == command.WorkspaceId && x.Id == command.RunId, cancellationToken);
        if (run.Status == WorkspaceDeploymentRunStatus.Queued)
        {
            run.Status = WorkspaceDeploymentRunStatus.Running;
            run.StartedAt = now;
        }

        if (run.Status == WorkspaceDeploymentRunStatus.Running)
        {
            if (!string.IsNullOrWhiteSpace(workerId))
                run.WorkerId = workerId;
            run.WorkerHeartbeatAt = now;
        }
    }

    private async Task AddCommandAndRunEventAsync(
        DeploymentCommandEntity command,
        DeploymentCommandStatus commandStatus,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await AddCommandEventAsync(command, commandStatus, message, now, cancellationToken);
        await dbContext.DeploymentRunHistoryEvents.AddAsync(new DeploymentRunHistoryEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = command.WorkspaceId,
            RunId = command.RunId,
            Status = ToRunStatus(commandStatus),
            Message = message,
            CreatedAt = now
        }, cancellationToken);
    }

    private async Task AddCommandEventAsync(
        DeploymentCommandEntity command,
        DeploymentCommandStatus commandStatus,
        string message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await dbContext.DeploymentCommandEvents.AddAsync(new DeploymentCommandEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = command.WorkspaceId,
            CommandId = command.Id,
            RunId = command.RunId,
            Status = commandStatus,
            Message = message,
            CreatedAt = now
        }, cancellationToken);
    }

    private static string BuildDeploymentCommandIdempotencyKey(
        Guid workspaceId,
        Guid runId,
        Guid environmentId,
        Guid engineId,
        Guid sourceRevisionId,
        Guid? rollbackSourceRunId) =>
        string.Join(
            ':',
            "deployment-command",
            workspaceId.ToString("D"),
            runId.ToString("D"),
            environmentId.ToString("D"),
            engineId.ToString("D"),
            sourceRevisionId.ToString("D"),
            rollbackSourceRunId?.ToString("D") ?? "deploy");

    private static bool IsFinal(DeploymentCommandStatus status) =>
        status is DeploymentCommandStatus.Completed
            or DeploymentCommandStatus.Failed
            or DeploymentCommandStatus.Rejected
            or DeploymentCommandStatus.Cancelled
            or DeploymentCommandStatus.RecoveryRequired
            or DeploymentCommandStatus.Expired;

    private static bool IsTerminalRunStatus(WorkspaceDeploymentRunStatus status) =>
        status is WorkspaceDeploymentRunStatus.Succeeded
            or WorkspaceDeploymentRunStatus.Failed
            or WorkspaceDeploymentRunStatus.Blocked
            or WorkspaceDeploymentRunStatus.Cancelled
            or WorkspaceDeploymentRunStatus.RolledBack;

    private void DetachTrackedRun(Guid runId)
    {
        var tracked = dbContext.DeploymentRuns.Local.FirstOrDefault(x => x.Id == runId);
        if (tracked is not null)
            dbContext.Entry(tracked).State = EntityState.Detached;
    }

    private static WorkspaceDeploymentRunStatus ToRunStatus(DeploymentCommandStatus status) =>
        status switch
        {
            DeploymentCommandStatus.Pending => WorkspaceDeploymentRunStatus.Queued,
            DeploymentCommandStatus.Claimed or DeploymentCommandStatus.Running => WorkspaceDeploymentRunStatus.Running,
            DeploymentCommandStatus.Completed => WorkspaceDeploymentRunStatus.Succeeded,
            DeploymentCommandStatus.RecoveryRequired => WorkspaceDeploymentRunStatus.RecoveryRequired,
            DeploymentCommandStatus.Cancelled => WorkspaceDeploymentRunStatus.Cancelled,
            _ => WorkspaceDeploymentRunStatus.Failed
        };

    private static DeploymentCommand ToDeploymentCommand(DeploymentCommandEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.RunId,
            entity.EnvironmentId,
            entity.EngineId,
            entity.Action,
            entity.Status,
            DeserializeArtifactReference(entity.ArtifactJson),
            new DeploymentCommandRevisionReference(entity.RevisionId),
            entity.IdempotencyKey,
            entity.WorkerId,
            entity.LeaseToken,
            entity.ClaimedAt,
            entity.LeaseExpiresAt,
            entity.HeartbeatAt,
            entity.AttemptNumber,
            entity.PercentComplete,
            entity.ProgressMessage,
            GetObservedArtifactDigest(entity),
            entity.RuntimeReference,
            DeserializeDiagnostics(entity.DiagnosticsJson),
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.AvailableAt,
            entity.ExpiresAt,
            entity.CompletedAt,
            DeserializeArtifactItems(entity.ArtifactJson));

    private static WorkspaceArtifactDigest? GetObservedArtifactDigest(DeploymentCommandEntity entity) =>
        entity.ObservedArtifactDigestAlgorithm is null || entity.ObservedArtifactDigest is null
            ? null
            : new WorkspaceArtifactDigest(entity.ObservedArtifactDigestAlgorithm, entity.ObservedArtifactDigest);

    private static DeploymentCommandWebhookNotification ToDeploymentCommandWebhookNotification(DeploymentCommandWebhookNotificationEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.EngineId,
            entity.CommandId,
            entity.Status,
            entity.SafePayloadJson,
            entity.CreatedAt,
            entity.SentAt);

    private static string SerializeArtifactPayload(
        DeploymentCommandArtifactReference? artifact,
        IReadOnlyList<DeploymentCommandArtifactItem>? artifacts) =>
        JsonSerializer.Serialize(new DeploymentCommandArtifactPayload(artifact, artifacts ?? []));

    private static DeploymentCommandArtifactReference? DeserializeArtifactReference(string artifactJson)
    {
        var payload = DeserializeArtifactPayload(artifactJson);
        if (payload?.Artifact is not null)
            return payload.Artifact;
        if (payload?.Artifacts.FirstOrDefault() is { } first)
            return new DeploymentCommandArtifactReference(first.ArtifactRecordId, first.ArtifactId, first.ArtifactTypeId, first.ContentDigest);

        return string.IsNullOrWhiteSpace(artifactJson) || artifactJson == "null"
            ? null
            : JsonSerializer.Deserialize<DeploymentCommandArtifactReference>(artifactJson);
    }

    private static IReadOnlyList<DeploymentCommandArtifactItem> DeserializeArtifactItems(string artifactJson)
    {
        var payload = DeserializeArtifactPayload(artifactJson);
        if (payload is not null)
            return payload.Artifacts;

        var artifact = DeserializeArtifactReference(artifactJson);
        return artifact?.ArtifactRecordId is null || artifact.ContentDigest is null
            ? []
            : [new DeploymentCommandArtifactItem(artifact.ArtifactRecordId.Value, artifact.ArtifactId ?? "", artifact.ArtifactTypeId ?? "", null, artifact.ContentDigest, artifact.ArtifactId ?? "Artifact", null)];
    }

    private static DeploymentCommandArtifactPayload? DeserializeArtifactPayload(string artifactJson)
    {
        if (string.IsNullOrWhiteSpace(artifactJson) || artifactJson == "null")
            return null;

        try
        {
            using var document = JsonDocument.Parse(artifactJson);
            return document.RootElement.TryGetProperty(nameof(DeploymentCommandArtifactPayload.Artifacts), out _)
                ? JsonSerializer.Deserialize<DeploymentCommandArtifactPayload>(artifactJson)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ApplyArtifactOutcomes(
        string artifactJson,
        IReadOnlyList<DeploymentCommandArtifactOutcome>? outcomes)
    {
        if (outcomes is null || outcomes.Count == 0)
            return artifactJson;

        var payload = DeserializeArtifactPayload(artifactJson);
        if (payload is null)
            return artifactJson;

        var byArtifactId = outcomes.ToDictionary(x => x.ArtifactRecordId);
        var artifacts = payload.Artifacts.Select(item =>
        {
            if (!byArtifactId.TryGetValue(item.ArtifactRecordId, out var outcome))
                return item;

            return item with
            {
                Status = outcome.Status,
                ObservedDigest = outcome.ObservedDigest,
                RuntimeReference = outcome.RuntimeReference,
                Diagnostics = outcome.Diagnostics ?? []
            };
        }).ToList();

        return JsonSerializer.Serialize(payload with { Artifacts = artifacts });
    }

    private static IReadOnlyList<DeploymentCommandDiagnostic> DeserializeDiagnostics(string diagnosticsJson) =>
        string.IsNullOrWhiteSpace(diagnosticsJson)
            ? []
            : JsonSerializer.Deserialize<List<DeploymentCommandDiagnostic>>(diagnosticsJson) ?? [];

    private static string SafeArtifactDisplayName(WorkspaceDeploymentArtifactEntity artifact)
    {
        var metadata = DeserializeDisplayMetadata(artifact.DisplayMetadataJson, artifact.ManifestName, artifact.ManifestVersion, artifact.ManifestEnvironment);
        return string.IsNullOrWhiteSpace(metadata.Name)
            ? artifact.ArtifactId
            : string.IsNullOrWhiteSpace(metadata.Version)
                ? metadata.Name
                : $"{metadata.Name} {metadata.Version}";
    }

    private sealed record DeploymentCommandArtifactPayload(
        DeploymentCommandArtifactReference? Artifact,
        IReadOnlyList<DeploymentCommandArtifactItem> Artifacts);

    private static RuntimeControlExecution ToRuntimeControlExecution(RuntimeControlExecutionEntity entity) =>
        new(
            entity.Id,
            entity.WorkspaceId,
            entity.EngineId,
            entity.EnvironmentId,
            entity.ControlId,
            entity.ControlLabel,
            entity.Boundary,
            entity.RequiredCapabilityId,
            entity.ConfirmationId,
            entity.ActorAccountId,
            entity.Status,
            entity.CreatedAt,
            entity.Message);

    private async Task<DeploymentTierDefinitionEntity> ResolveTierForEnvironmentAsync(
        Guid workspaceId,
        EnvironmentTier legacyTier,
        Guid? tierId,
        bool requireActive,
        CancellationToken cancellationToken)
    {
        var tiers = await EnsureDefaultTierEntitiesAsync(workspaceId, cancellationToken: cancellationToken);
        var tier = tierId.HasValue
            ? tiers.SingleOrDefault(x => x.Id == tierId.Value)
            : tiers
                .Where(x => x.Name == legacyTier.ToString())
                .OrderByDescending(x => x.IsDefault)
                .FirstOrDefault();

        if (tier is null)
            throw new InvalidOperationException("Deployment tier does not exist in the workspace.");
        if (requireActive && tier.Status != DeploymentTierStatus.Active)
            throw new InvalidOperationException("Archived deployment tiers cannot be assigned to environments.");

        return tier;
    }

    private async Task<IReadOnlyList<DeploymentTierDefinitionEntity>> EnsureDefaultTierEntitiesAsync(
        Guid workspaceId,
        Guid? actorAccountId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTiersAsync(workspaceId, actorAccountId, cancellationToken);
        return await dbContext.DeploymentTierDefinitions
            .Include(x => x.Capabilities)
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);
    }

    private async Task AssignMissingEnvironmentTierIdsAsync(
        Guid workspaceId,
        IReadOnlyList<DeploymentTierDefinitionEntity> tiers,
        CancellationToken cancellationToken)
    {
        var environments = await dbContext.DeploymentEnvironments
            .Where(x => x.WorkspaceId == workspaceId && x.TierId == null)
            .ToListAsync(cancellationToken);
        if (environments.Count == 0)
            return;

        var tiersByName = tiers
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(tier => tier.IsDefault).First(), StringComparer.OrdinalIgnoreCase);

        foreach (var environment in environments)
        {
            if (tiersByName.TryGetValue(environment.Tier.ToString(), out var tier))
                environment.TierId = tier.Id;
            else
            {
                var fallbackTier = tiers.OrderByDescending(x => x.Status == DeploymentTierStatus.Active).ThenBy(x => x.SortOrder).First();
                environment.TierId = fallbackTier.Id;
                environment.TierRequiresReview = true;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<DeploymentTierCapabilityAssignmentEntity> CreateCapabilityAssignments(
        Guid workspaceId,
        IReadOnlyList<string> capabilities,
        Guid? actorAccountId,
        DateTimeOffset now,
        Guid tierId) =>
        capabilities
            .Select(capability => new DeploymentTierCapabilityAssignmentEntity
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                TierId = tierId,
                CapabilityId = capability,
                CreatedAt = now,
                CreatedByAccountId = actorAccountId
            })
            .ToList();

    private static DeploymentTierChangeRecordEntity Change(
        Guid workspaceId,
        Guid tierId,
        Guid? actorAccountId,
        string changeType,
        string summary,
        DateTimeOffset changedAt,
        int affectedEnvironmentCount) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            TierId = tierId,
            ActorAccountId = actorAccountId,
            ChangeType = changeType,
            Summary = summary,
            ChangedAt = changedAt,
            AffectedEnvironmentCount = affectedEnvironmentCount
        };

    private Task<bool> ActiveTierNameExistsAsync(
        Guid workspaceId,
        string name,
        Guid? excludedTierId,
        CancellationToken cancellationToken) =>
        dbContext.DeploymentTierDefinitions.AnyAsync(
            x => x.WorkspaceId == workspaceId
                && x.Status == DeploymentTierStatus.Active
                && x.Name == name.Trim()
                && (!excludedTierId.HasValue || x.Id != excludedTierId.Value),
            cancellationToken);

    private Task<bool> ActiveSecretStoreNameExistsAsync(
        Guid workspaceId,
        string name,
        Guid? excludedSecretStoreId,
        CancellationToken cancellationToken) =>
        dbContext.DeploymentSecretStores.AnyAsync(
            x => x.WorkspaceId == workspaceId
                && x.Status == DeploymentSecretStoreStatus.Active
                && x.Name == name.Trim()
                && (!excludedSecretStoreId.HasValue || x.Id != excludedSecretStoreId.Value),
            cancellationToken);

    private Task<bool> ActiveCredentialReferenceNameExistsAsync(
        Guid workspaceId,
        Guid secretStoreId,
        string name,
        Guid? excludedCredentialReferenceId,
        CancellationToken cancellationToken) =>
        dbContext.DeploymentCredentialReferences.AnyAsync(
            x => x.WorkspaceId == workspaceId
                && x.SecretStoreId == secretStoreId
                && x.Status == DeploymentSecretStoreStatus.Active
                && x.Name == name.Trim()
                && (!excludedCredentialReferenceId.HasValue || x.Id != excludedCredentialReferenceId.Value),
            cancellationToken);

    private async Task<ResolvedEngineCredential> ResolveCredentialReferenceAsync(
        Guid workspaceId,
        Guid? credentialReferenceId,
        string? credentialProvider,
        string? credentialReference,
        EngineCredentialAssignmentStatus assignmentStatus,
        CancellationToken cancellationToken)
    {
        if (assignmentStatus == EngineCredentialAssignmentStatus.Deferred)
            return new ResolvedEngineCredential(null, "", "", EngineCredentialAssignmentStatus.Deferred);

        if (!credentialReferenceId.HasValue)
            return new ResolvedEngineCredential(null, credentialProvider!.Trim(), credentialReference!.Trim(), EngineCredentialAssignmentStatus.Assigned);

        var reference = await dbContext.DeploymentCredentialReferences
            .AsNoTracking()
            .Include(x => x.SecretStore)
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.Id == credentialReferenceId.Value, cancellationToken);
        if (reference is null)
            throw new InvalidOperationException("Deployment credential reference does not exist in the workspace.");
        if (reference.Status != DeploymentSecretStoreStatus.Active || reference.SecretStore?.Status != DeploymentSecretStoreStatus.Active)
            throw new InvalidOperationException("Archived deployment credential references cannot be assigned to engines.");

        return new ResolvedEngineCredential(reference.Id, reference.SecretStore.Provider, reference.Reference, EngineCredentialAssignmentStatus.Assigned);
    }

    private sealed record ResolvedEngineCredential(Guid? Id, string Provider, string Reference, EngineCredentialAssignmentStatus AssignmentStatus);

    private static DeploymentHealth EnvironmentHealth(DeploymentEnvironmentEntity environment, IReadOnlyList<WorkflowEngineEntity> engines)
    {
        var environmentEngines = engines.Where(x => x.EnvironmentId == environment.Id).ToList();
        if (environmentEngines.Count == 0)
            return DeploymentHealth.Unreachable;
        if (environmentEngines.Any(x => x.Health == DeploymentHealth.Unreachable))
            return DeploymentHealth.Unreachable;
        if (environmentEngines.Any(x => x.Health == DeploymentHealth.Degraded))
            return DeploymentHealth.Degraded;
        return DeploymentHealth.Healthy;
    }
}
