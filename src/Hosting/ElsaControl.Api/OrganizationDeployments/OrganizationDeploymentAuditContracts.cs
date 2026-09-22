using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.OrganizationDeployments;

/// <summary>
/// Sanitized Cloud function-deploy audit record. This type is the only shape the
/// read API will persist or return: no actor, email, IP, Stripe, secret, or log fields.
/// Persistence does not exist yet; <see cref="InMemoryOrganizationDeploymentAuditStore"/>
/// is the read-side seam until writers land. An empty list is valid.
/// </summary>
public sealed record OrganizationDeploymentAuditRecord(
    Guid OrganizationId,
    string Id,
    string FunctionName,
    string SourceRevision,
    string TargetEnvironment,
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> ApprovedScope,
    string Outcome);

public sealed record OrganizationDeploymentAuditItemResponse(
    string Id,
    string FunctionName,
    string SourceRevision,
    string TargetEnvironment,
    string OccurredAt,
    IReadOnlyList<string> ApprovedScope,
    string Outcome);

public sealed record OrganizationDeploymentAuditPageResponse(
    IReadOnlyList<OrganizationDeploymentAuditItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public interface IOrganizationDeploymentAuditStore
{
    Task<IReadOnlyList<OrganizationDeploymentAuditRecord>> ListAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);
}

public sealed record OrganizationDeploymentAuditApiResult(
    OrganizationDeploymentAuditPageResponse? Page,
    OrganizationWorkspaceFailure? Failure,
    bool Unavailable)
{
    public bool Succeeded => Page is not null && Failure is null && !Unavailable;

    public static OrganizationDeploymentAuditApiResult Success(OrganizationDeploymentAuditPageResponse page) =>
        new(page, null, false);

    public static OrganizationDeploymentAuditApiResult Denied(OrganizationWorkspaceFailure failure) =>
        new(null, failure, false);

    public static OrganizationDeploymentAuditApiResult StoreUnavailable() =>
        new(null, null, true);
}
