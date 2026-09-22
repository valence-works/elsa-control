using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace ElsaControl.Api.OrganizationDeployments;

/// <summary>
/// Read-side store for sanitized org deployment audit records. Chosen because no
/// existing catalog table is a safe source: workspace deployment history carries
/// actors, artifacts, and other fields this contract forbids. Writers are out of
/// scope; an empty organization list is the valid production state until they exist.
/// </summary>
public sealed class InMemoryOrganizationDeploymentAuditStore : IOrganizationDeploymentAuditStore
{
    private readonly ConcurrentDictionary<Guid, ImmutableArray<OrganizationDeploymentAuditRecord>> _records = new();

    public void Seed(Guid organizationId, params OrganizationDeploymentAuditRecord[] records) =>
        _records[organizationId] = [.. records];

    public Task<IReadOnlyList<OrganizationDeploymentAuditRecord>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<OrganizationDeploymentAuditRecord>>(
            _records.TryGetValue(organizationId, out var records) ? records : []);
    }
}
