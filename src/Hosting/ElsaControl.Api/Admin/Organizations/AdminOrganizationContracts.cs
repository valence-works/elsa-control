namespace ElsaControl.Api.Admin.Organizations;

public sealed record AdminOrganizationCreateRequest(string? Name, Guid? OwnerAccountId);

public sealed record AdminOrganizationCreateResponse(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid OwnerAccountId);
