using System.Data;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class EfCoreElsaInstanceMigrationStore(CatalogDbContext dbContext) :
    IElsaInstanceMigrationStore, IElsaInstanceMigrationSourceReleaseStore
{
    public async Task<ElsaInstanceMigration?> GetAsync(
        Guid workspaceId, Guid migrationId, CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        var entity = await dbContext.ElsaInstanceMigrations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == workspaceId && x.MigrationId == migrationId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<ElsaInstanceMigrationWriteResult> CreateAsync(
        ElsaInstanceMigrationStartEnvelope envelope, ElsaInstanceMigrationAudit audit,
        CancellationToken cancellationToken = default)
    {
        var migration = envelope.Migration;
        dbContext.ChangeTracker.Clear();
        var scope = $"instance/{migration.InstanceId:D}/MajorMigration";
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var replayOperation = await dbContext.ElsaInstanceOperations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.WorkspaceId == migration.WorkspaceId && x.IdempotencyScope == scope &&
                    x.IdempotencyKey == envelope.IdempotencyKey, cancellationToken);
                if (replayOperation is not null)
                {
                    var replay = await dbContext.ElsaInstanceMigrations.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.OperationId == replayOperation.Id, cancellationToken);
                    if (replay is null || replayOperation.RequestHash != migration.StartRequestHash)
                        return Result(ElsaInstanceMigrationWriteOutcome.Conflict, replay is null ? null : Map(replay),
                            "migration.idempotency.conflict");
                    return Result(ElsaInstanceMigrationWriteOutcome.Replayed, Map(replay), "migration.replayed");
                }

                var instance = await dbContext.ElsaInstances.SingleOrDefaultAsync(x =>
                    x.Id == migration.InstanceId && x.WorkspaceId == migration.WorkspaceId &&
                    x.OrganizationId == migration.OrganizationId, cancellationToken);
                if (instance is null)
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.NotFound, null, "migration.instance.not-found");
                }
                if (instance.Version != envelope.ExpectedInstanceVersion)
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.instance.version-conflict");
                }

                var unsafeLifecycle = instance.DesiredLifecycle == ElsaDesiredLifecycle.Deleting ||
                    instance.ObservedLifecycle is ElsaObservedLifecycle.Deleting or ElsaObservedLifecycle.Deleted;
                var activeOperation = await dbContext.ElsaInstanceOperations.AsNoTracking().AnyAsync(x =>
                    x.InstanceId == migration.InstanceId &&
                    (x.State == ElsaInstanceOperationState.Accepted || x.State == ElsaInstanceOperationState.Queued ||
                     x.State == ElsaInstanceOperationState.Running || x.State == ElsaInstanceOperationState.RecoveryRequired ||
                     x.State == ElsaInstanceOperationState.WaitingForPriorOperation), cancellationToken);
                var uncertainRun = await dbContext.DeploymentRuns.AsNoTracking().AnyAsync(x =>
                    x.WorkspaceId == migration.WorkspaceId && x.ElsaInstanceId == migration.InstanceId &&
                    (x.Status == WorkspaceDeploymentRunStatus.Queued || x.Status == WorkspaceDeploymentRunStatus.Running ||
                     x.Status == WorkspaceDeploymentRunStatus.RecoveryRequired), cancellationToken);
                if (unsafeLifecycle || activeOperation || uncertainRun)
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.instance.busy");
                }
                if (!MatchesCurrentSource(instance, migration.Source))
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.source.stale");
                }
                if (!await IsPersistedTargetAsync(migration, cancellationToken))
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.target.unverified");
                }

                dbContext.ElsaInstanceOperations.Add(new ElsaInstanceOperationEntity
                {
                    Id = migration.OperationId,
                    InstanceId = migration.InstanceId,
                    OrganizationId = migration.OrganizationId,
                    WorkspaceId = migration.WorkspaceId,
                    Action = ElsaInstanceOperationAction.MajorMigration,
                    IdempotencyScope = scope,
                    IdempotencyKey = envelope.IdempotencyKey,
                    RequestHash = migration.StartRequestHash,
                    ExpectedVersion = envelope.ExpectedInstanceVersion,
                    State = ElsaInstanceOperationState.Running,
                    AttemptNumber = 1,
                    AcceptedAt = migration.CreatedAt,
                    StartedAt = migration.CreatedAt,
                    CreatedAt = migration.CreatedAt,
                    UpdatedAt = migration.UpdatedAt
                });
                dbContext.ElsaInstanceMigrations.Add(Map(migration));
                await AddAuditAsync(migration, audit, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return Result(ElsaInstanceMigrationWriteOutcome.Applied, migration, "migration.started");
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.concurrent.conflict");
        }
    }

    public async Task<ElsaInstanceMigrationWriteResult> SaveAsync(
        ElsaInstanceMigration migration, DateTimeOffset expectedUpdatedAt, ElsaInstanceMigrationAudit audit,
        CancellationToken cancellationToken = default)
    {
        dbContext.ChangeTracker.Clear();
        try
        {
            return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var entity = await dbContext.ElsaInstanceMigrations.SingleOrDefaultAsync(x =>
                    x.WorkspaceId == migration.WorkspaceId && x.MigrationId == migration.Id, cancellationToken);
                var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(x =>
                    x.Id == migration.OperationId && x.InstanceId == migration.InstanceId, cancellationToken);
                if (entity is null || operation is null)
                {
                    return Result(ElsaInstanceMigrationWriteOutcome.NotFound, null, "migration.not-found");
                }
                if (entity.UpdatedAt != expectedUpdatedAt.ToUniversalTime() || entity.LastRequestHash == migration.LastRequestHash)
                {
                    var current = Map(entity);
                    return entity.LastRequestHash == migration.LastRequestHash
                        ? Result(ElsaInstanceMigrationWriteOutcome.Replayed, current, "migration.replayed")
                        : Result(ElsaInstanceMigrationWriteOutcome.Conflict, current, "migration.version.conflict");
                }

                entity.Phase = migration.Phase.ToString();
                entity.SourceAccessMode = migration.SourceAccess.ToString();
                entity.CutoverAt = migration.CutoverAt;
                entity.SourceRetainUntil = migration.SourceRetainUntil;
                entity.EarlyReleaseApprovedByAccountId = migration.EarlyReleaseApprovedByAccountId;
                entity.EarlyReleaseApprovedAt = migration.EarlyReleaseApprovedAt;
                entity.SourceReleasedAt = migration.SourceReleasedAt;
                entity.LastRequestHash = migration.LastRequestHash;
                entity.UpdatedAt = migration.UpdatedAt;
                if (migration.IsTerminal)
                {
                    operation.State = migration.Phase == ElsaInstanceMigrationPhase.Failed
                        ? ElsaInstanceOperationState.Failed : ElsaInstanceOperationState.Succeeded;
                    operation.CompletedAt = migration.UpdatedAt;
                    operation.UpdatedAt = migration.UpdatedAt;
                }
                await AddAuditAsync(migration, audit, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return Result(ElsaInstanceMigrationWriteOutcome.Applied, migration, "migration.updated");
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return Result(ElsaInstanceMigrationWriteOutcome.Conflict, null, "migration.version.conflict");
        }
    }

    public async Task<ElsaInstanceMigrationSourceReleaseClaim?> TryClaimDueAsync(
        DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        now = now.ToUniversalTime();
        dbContext.ChangeTracker.Clear();
        try
        {
            return await dbContext.ExecuteInTransactionAsync<ElsaInstanceMigrationSourceReleaseClaim?>(IsolationLevel.Serializable, async () =>
            {
                var entity = await dbContext.ElsaInstanceMigrations
                    .Where(x =>
                        (x.Phase == nameof(ElsaInstanceMigrationPhase.RetiringSource) ||
                         x.Phase == nameof(ElsaInstanceMigrationPhase.RetainingSource) && x.SourceRetainUntil <= now) &&
                        (x.SourceReleaseClaimedUntil == null || x.SourceReleaseClaimedUntil <= now))
                    .OrderBy(x => x.SourceRetainUntil).ThenBy(x => x.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (entity is null)
                {
                    return null;
                }

                var changedAt = now > entity.UpdatedAt ? now : entity.UpdatedAt.AddTicks(1);
                entity.Phase = nameof(ElsaInstanceMigrationPhase.RetiringSource);
                entity.SourceAccessMode = nameof(ElsaInstanceMigrationSourceAccess.Stopped);
                entity.SourceReleaseClaimToken = Guid.NewGuid();
                entity.SourceReleaseClaimedUntil = now.Add(leaseDuration);
                entity.SourceReleaseAttemptCount++;
                entity.SourceReleaseDiagnosticCode = null;
                entity.UpdatedAt = changedAt;
                await dbContext.SaveChangesAsync(cancellationToken);
                var migration = Map(entity);
                return new ElsaInstanceMigrationSourceReleaseClaim(migration, entity.SourceReleaseClaimToken.Value,
                    entity.SourceReleaseAttemptCount, entity.SourceReleaseClaimedUntil.Value);
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<ElsaInstanceMigrationWriteResult> CompleteAsync(
        ElsaInstanceMigrationSourceReleaseClaim claim, ElsaInstanceSourceReleaseResult result,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        result.Validate();
        now = now.ToUniversalTime();
        dbContext.ChangeTracker.Clear();
        return await dbContext.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
        {
            var entity = await dbContext.ElsaInstanceMigrations.SingleOrDefaultAsync(x =>
                x.MigrationId == claim.Migration.Id && x.WorkspaceId == claim.Migration.WorkspaceId, cancellationToken);
            if (entity is null)
                return Result(ElsaInstanceMigrationWriteOutcome.NotFound, null, "migration.not-found");
            var operation = await dbContext.ElsaInstanceOperations.SingleOrDefaultAsync(x =>
                x.Id == claim.Migration.OperationId && x.InstanceId == claim.Migration.InstanceId, cancellationToken);
            if (entity.SourceReleaseClaimToken != claim.ClaimToken || entity.SourceReleaseClaimedUntil <= now ||
                entity.UpdatedAt != claim.Migration.UpdatedAt || entity.Phase != nameof(ElsaInstanceMigrationPhase.RetiringSource) ||
                entity.OperationId != claim.Migration.OperationId || operation?.State != ElsaInstanceOperationState.Running)
                return Result(ElsaInstanceMigrationWriteOutcome.Conflict, Map(entity), "migration.source-release.claim-conflict");

            entity.SourceReleaseClaimToken = null;
            entity.SourceReleaseClaimedUntil = null;
            entity.SourceReleaseDiagnosticCode = result.DiagnosticCode;
            entity.SourceReleaseProviderCorrelationId = result.ProviderCorrelationId;
            entity.SourceReleaseEvidenceReference = result.EvidenceReference;
            entity.SourceReleaseEvidenceDigest = result.EvidenceDigest;
            if (result.Outcome != ElsaInstanceSourceReleaseOutcome.Confirmed)
            {
                entity.UpdatedAt = now > entity.UpdatedAt ? now : entity.UpdatedAt.AddTicks(1);
                var attempted = Map(entity);
                await AddAuditAsync(attempted, new(attempted.Id, attempted.OperationId,
                    "MigrationSourceReleaseAttempted", attempted.Phase.ToString(), attempted.Phase.ToString(),
                    Guid.Empty, attempted.LastRequestHash, attempted.UpdatedAt), cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                return Result(ElsaInstanceMigrationWriteOutcome.Conflict, Map(entity), result.DiagnosticCode);
            }

            var released = Map(entity).ConfirmSourceReleased(now > entity.UpdatedAt ? now : entity.UpdatedAt.AddTicks(1));
            entity.Phase = released.Phase.ToString();
            entity.SourceReleasedAt = released.SourceReleasedAt;
            entity.UpdatedAt = released.UpdatedAt;
            operation = await dbContext.ElsaInstanceOperations.SingleAsync(x => x.Id == entity.OperationId, cancellationToken);
            operation.State = ElsaInstanceOperationState.Succeeded;
            operation.CompletedAt = released.UpdatedAt;
            operation.UpdatedAt = released.UpdatedAt;
            await AddAuditAsync(released, new(released.Id, released.OperationId, "MigrationSourceReleased",
                ElsaInstanceMigrationPhase.RetiringSource.ToString(), released.Phase.ToString(), Guid.Empty,
                released.LastRequestHash, released.UpdatedAt), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result(ElsaInstanceMigrationWriteOutcome.Applied, released, "migration.source-released");
        }, cancellationToken);
    }

    public async Task<bool> RenewAsync(
        ElsaInstanceMigrationSourceReleaseClaim claim, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        now = now.ToUniversalTime();
        dbContext.ChangeTracker.Clear();
        var entity = await dbContext.ElsaInstanceMigrations.SingleOrDefaultAsync(x =>
            x.MigrationId == claim.Migration.Id && x.WorkspaceId == claim.Migration.WorkspaceId, cancellationToken);
        if (entity is null || entity.SourceReleaseClaimToken != claim.ClaimToken ||
            entity.SourceReleaseClaimedUntil <= now || entity.UpdatedAt != claim.Migration.UpdatedAt ||
            entity.Phase != nameof(ElsaInstanceMigrationPhase.RetiringSource) ||
            entity.OperationId != claim.Migration.OperationId)
            return false;
        entity.SourceReleaseClaimedUntil = now.Add(leaseDuration);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<bool> IsPersistedTargetAsync(ElsaInstanceMigration migration, CancellationToken cancellationToken)
    {
        var entity = await dbContext.ElsaInstanceResolvedPlans.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkspaceId == migration.WorkspaceId && x.InstanceId == migration.InstanceId &&
            x.PlanId == migration.Target.PlanId && x.PlanUri == migration.Target.PlanUri, cancellationToken);
        if (entity is null)
            return false;
        try
        {
            var plan = ResolvedElsaApplicationPlanSerialization.Deserialize(entity.SerializedPlan);
            return plan.Release is not null &&
                plan.Release.ReleaseLine == migration.Target.ReleaseLine &&
                plan.Release.Version == migration.Target.Version &&
                plan.Release.ReleaseManifestDigest == migration.Target.ManifestDigest;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async Task AddAuditAsync(
        ElsaInstanceMigration migration, ElsaInstanceMigrationAudit audit, CancellationToken cancellationToken)
    {
        var sequence = (await dbContext.ElsaInstanceAuditEvents.Where(x => x.InstanceId == migration.InstanceId)
            .MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0) + 1;
        dbContext.ElsaInstanceAuditEvents.Add(new ElsaInstanceAuditEventEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = migration.OrganizationId,
            WorkspaceId = migration.WorkspaceId,
            InstanceId = migration.InstanceId,
            MigrationId = audit.MigrationId,
            OperationId = audit.OperationId,
            Sequence = sequence,
            EventType = audit.EventType,
            ActorAccountId = audit.ActorAccountId == Guid.Empty ? null : audit.ActorAccountId,
            PriorState = audit.PriorState,
            NewState = audit.NewState,
            DiagnosticCode = "migration.event",
            Summary = "migration.event",
            RequestKeyHash = audit.RequestHash,
            PlanReference = migration.Target.PlanUri,
            OccurredAt = audit.OccurredAt
        });
    }

    private static bool MatchesCurrentSource(ElsaInstanceEntity instance, ElsaInstanceMigrationReleaseReference source) =>
        instance.ResolvedPlanId == source.PlanId && instance.ResolvedPlanUri == source.PlanUri &&
        instance.CurrentReleaseLine == source.ReleaseLine && instance.CurrentReleaseVersion == source.Version &&
        instance.CurrentReleaseManifestDigest == source.ManifestDigest && instance.CurrentDeploymentId == source.DeploymentReference;

    private static ElsaInstanceMigration Map(ElsaInstanceMigrationEntity entity) =>
        ElsaInstanceMigration.Hydrate(entity.MigrationId, entity.OrganizationId, entity.WorkspaceId, entity.InstanceId,
            Reference(entity, true), Reference(entity, false), entity.OperationId, entity.StartRequestHash,
            entity.LastRequestHash, Enum.Parse<ElsaInstanceMigrationPhase>(entity.Phase),
            Enum.Parse<ElsaInstanceMigrationSourceAccess>(entity.SourceAccessMode), entity.CutoverAt,
            entity.SourceRetainUntil, entity.EarlyReleaseApprovedByAccountId, entity.EarlyReleaseApprovedAt,
            entity.SourceReleasedAt, entity.CreatedAt, entity.UpdatedAt);

    private static ElsaInstanceMigrationEntity Map(ElsaInstanceMigration migration) => new()
    {
        MigrationId = migration.Id,
        OperationId = migration.OperationId,
        OrganizationId = migration.OrganizationId,
        WorkspaceId = migration.WorkspaceId,
        InstanceId = migration.InstanceId,
        SourcePlanId = migration.Source.PlanId,
        SourcePlanUri = migration.Source.PlanUri,
        SourceReleaseLine = migration.Source.ReleaseLine,
        SourceVersion = migration.Source.Version,
        SourceManifestDigest = migration.Source.ManifestDigest,
        SourceDeploymentId = migration.Source.DeploymentReference,
        TargetPlanId = migration.Target.PlanId,
        TargetPlanUri = migration.Target.PlanUri,
        TargetReleaseLine = migration.Target.ReleaseLine,
        TargetVersion = migration.Target.Version,
        TargetManifestDigest = migration.Target.ManifestDigest,
        TargetDeploymentId = migration.Target.DeploymentReference,
        StartRequestHash = migration.StartRequestHash,
        LastRequestHash = migration.LastRequestHash,
        Phase = migration.Phase.ToString(),
        SourceAccessMode = migration.SourceAccess.ToString(),
        CutoverAt = migration.CutoverAt,
        SourceRetainUntil = migration.SourceRetainUntil,
        EarlyReleaseApprovedByAccountId = migration.EarlyReleaseApprovedByAccountId,
        EarlyReleaseApprovedAt = migration.EarlyReleaseApprovedAt,
        SourceReleasedAt = migration.SourceReleasedAt,
        CreatedAt = migration.CreatedAt,
        UpdatedAt = migration.UpdatedAt
    };

    private static ElsaInstanceMigrationReleaseReference Reference(ElsaInstanceMigrationEntity entity, bool source) => new(
        source ? entity.SourcePlanId! : entity.TargetPlanId!, source ? entity.SourcePlanUri! : entity.TargetPlanUri!,
        source ? entity.SourceReleaseLine! : entity.TargetReleaseLine!, source ? entity.SourceVersion! : entity.TargetVersion!,
        source ? entity.SourceManifestDigest! : entity.TargetManifestDigest!,
        source ? entity.SourceDeploymentId! : entity.TargetDeploymentId!);

    private static ElsaInstanceMigrationWriteResult Result(
        ElsaInstanceMigrationWriteOutcome outcome, ElsaInstanceMigration? migration, string code) => new(outcome, migration, code);
}
