using System.Data;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class PartnerApiKeyStore(CatalogDbContext dbContext) : IPartnerApiKeyStore
{
    private const string ProvisionerDisplayName = "Partner provisioner";

    public async Task<PartnerApiKeyRecord?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PartnerApiKeys.AsNoTracking()
            .SingleOrDefaultAsync(x => x.KeyId == keyId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<PartnerApiKeyRecord>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var keys = await dbContext.PartnerApiKeys.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.KeyId)
            .ToListAsync(cancellationToken);
        return keys.Select(ToRecord).ToList();
    }

    public Task<PartnerApiKeyCreateResult> CreateAsync(
        CreatePartnerApiKeyRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!PartnerApiKeyFormat.IsEnvironment(request.Environment))
            return Task.FromResult(PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.InvalidEnvironment));
        if (!PartnerApiKeyScopes.TryNormalize(request.Scopes, out var scopes, out _))
            return Task.FromResult(PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.InvalidScopes));
        var name = NormalizeName(request.Name, out var nameError);
        if (nameError)
            return Task.FromResult(PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.InvalidName));
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now)
            return Task.FromResult(PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.InvalidExpiry));

        var credential = PartnerApiKeyFormat.Issue(request.Environment);
        return dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            async () =>
            {
                var organization = await dbContext.Organizations
                    .SingleOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken);
                if (organization is null)
                    return PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.OrganizationNotFound);

                var activeCount = await CountActiveAsync(request.OrganizationId, now, cancellationToken);
                if (activeCount >= PartnerApiKeyFormat.MaxActiveKeysPerPartner)
                    return PartnerApiKeyCreateResult.Denied(PartnerApiKeyCreateFailure.ActiveKeyLimitReached);

                var provisioner = await EnsureProvisionerAsync(organization, now, cancellationToken);
                var entity = new PartnerApiKey
                {
                    KeyId = credential.KeyId,
                    OrganizationId = organization.Id,
                    WorkspaceId = provisioner.WorkspaceId,
                    ProvisionerAccountId = provisioner.AccountId,
                    SecretHash = PartnerApiKeyFormat.HashSecret(credential.Secret),
                    Name = name,
                    Scopes = scopes.ToList(),
                    CreatedAt = now,
                    CreatedByAccountId = request.ActorAccountId,
                    CreatedByOperatorSubject = OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(request.OperatorSubject),
                    ExpiresAt = request.ExpiresAt
                };
                dbContext.PartnerApiKeys.Add(entity);
                dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
                {
                    OrganizationId = organization.Id,
                    ActorAccountId = request.ActorAccountId,
                    OperatorSubject = entity.CreatedByOperatorSubject,
                    Action = OrganizationAuditAction.PartnerApiKeyCreated,
                    TargetType = "PartnerApiKey",
                    TargetId = entity.KeyId,
                    Summary = "Partner API key created.",
                    CreatedAt = now
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                return PartnerApiKeyCreateResult.Success(new CreatedPartnerApiKey(ToRecord(entity), credential.Token));
            },
            cancellationToken);
    }

    public Task<PartnerApiKeyRevokeResult> RevokeAsync(
        Guid organizationId,
        string keyId,
        Guid? actorAccountId,
        string? operatorSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!PartnerApiKeyFormat.IsKeyId(keyId))
            return Task.FromResult(PartnerApiKeyRevokeResult.Denied(PartnerApiKeyRevokeFailure.KeyNotFound));

        return dbContext.ExecuteInTransactionAsync(
            IsolationLevel.Serializable,
            async () =>
            {
                if (!await dbContext.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
                    return PartnerApiKeyRevokeResult.Denied(PartnerApiKeyRevokeFailure.OrganizationNotFound);

                var entity = await dbContext.PartnerApiKeys
                    .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.KeyId == keyId, cancellationToken);
                if (entity is null)
                    return PartnerApiKeyRevokeResult.Denied(PartnerApiKeyRevokeFailure.KeyNotFound);
                if (entity.RevokedAt.HasValue)
                    return PartnerApiKeyRevokeResult.Denied(PartnerApiKeyRevokeFailure.AlreadyRevoked);

                entity.RevokedAt = now;
                entity.RevokedByAccountId = actorAccountId;
                entity.RevokedByOperatorSubject = OrganizationInternalEntitlementPolicy.FingerprintOperatorSubject(operatorSubject);
                dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
                {
                    OrganizationId = organizationId,
                    ActorAccountId = actorAccountId,
                    OperatorSubject = entity.RevokedByOperatorSubject,
                    Action = OrganizationAuditAction.PartnerApiKeyRevoked,
                    TargetType = "PartnerApiKey",
                    TargetId = entity.KeyId,
                    Summary = "Partner API key revoked.",
                    CreatedAt = now
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                return PartnerApiKeyRevokeResult.Success(ToRecord(entity));
            },
            cancellationToken);
    }

    public async Task TouchLastUsedAsync(string keyId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await dbContext.PartnerApiKeys
            .Where(x => x.KeyId == keyId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.LastUsedAt, now),
                cancellationToken);
    }

    private Task<int> CountActiveAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.PartnerApiKeys.CountAsync(
            x => x.OrganizationId == organizationId &&
                 x.RevokedAt == null &&
                 (x.ExpiresAt == null || x.ExpiresAt > now),
            cancellationToken);

    private async Task<(Guid AccountId, Guid WorkspaceId)> EnsureProvisionerAsync(
        Organization organization,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.WorkspaceMemberships
            .AsNoTracking()
            .Where(x => x.Role == WorkspaceRole.PartnerProvisioner &&
                        x.Workspace!.OrganizationId == organization.Id &&
                        x.Workspace.SoftDeletedAt == null)
            .Select(x => new { x.AccountId, x.WorkspaceId })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return (existing.AccountId, existing.WorkspaceId);

        var workspace = await dbContext.Workspaces
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organization.Id &&
                     x.Kind == WorkspaceKind.Shared &&
                     x.SoftDeletedAt == null,
                cancellationToken);
        if (workspace is null)
        {
            workspace = new Workspace
            {
                OrganizationId = organization.Id,
                Name = organization.Name,
                Kind = WorkspaceKind.Shared,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.Workspaces.Add(workspace);
            dbContext.OrganizationAuditRecords.Add(new OrganizationAuditRecord
            {
                OrganizationId = organization.Id,
                Action = OrganizationAuditAction.WorkspaceCreated,
                TargetType = "Workspace",
                TargetId = workspace.Id.ToString("D"),
                Summary = "Partner provisioner workspace created.",
                CreatedAt = now
            });
        }

        var account = new Account
        {
            DisplayName = ProvisionerDisplayName,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.Accounts.Add(account);
        dbContext.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Workspace = workspace,
            Account = account,
            Role = WorkspaceRole.PartnerProvisioner,
            CreatedAt = now,
            UpdatedAt = now
        });
        return (account.Id, workspace.Id);
    }

    private static PartnerApiKeyRecord ToRecord(PartnerApiKey entity) =>
        new(
            entity.KeyId,
            entity.OrganizationId,
            entity.WorkspaceId,
            entity.ProvisionerAccountId,
            entity.SecretHash,
            entity.Name,
            entity.Scopes.ToArray(),
            entity.CreatedAt,
            entity.ExpiresAt,
            entity.LastUsedAt,
            entity.RevokedAt);

    private static string? NormalizeName(string? name, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        if (trimmed.Length > PartnerApiKeyFormat.MaxNameLength)
        {
            invalid = true;
            return null;
        }

        return trimmed;
    }
}
