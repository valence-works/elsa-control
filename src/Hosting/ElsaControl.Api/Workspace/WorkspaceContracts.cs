using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Packages;

namespace ElsaControl.Api.Workspace;

public sealed record AccountContextResponse(Guid Id, string? DisplayName, string? Email);

public sealed record OrganizationContextResponse(Guid Id, string Name, OrganizationRole Role);

public sealed record WorkspaceContextResponse(
    Guid Id,
    string Name,
    WorkspaceKind Kind,
    WorkspaceRole Role,
    Guid OrganizationId = default,
    string OrganizationName = "",
    OrganizationRole OrganizationRole = ElsaControl.PackageCatalog.Core.Accounts.OrganizationRole.Member);

public sealed record MeWorkspacesResponse(AccountContextResponse Account, IReadOnlyList<WorkspaceContextResponse> Workspaces)
{
    public IReadOnlyList<OrganizationContextResponse> Organizations { get; init; } = [];
}

public sealed record MeWorkspaceMembershipsResponse(
    AccountContextResponse? Account,
    IReadOnlyList<WorkspaceContextResponse> Workspaces,
    IReadOnlyList<OrganizationContextResponse> Organizations);

public sealed record OrganizationWorkspacesResponse(IReadOnlyList<WorkspaceContextResponse> Workspaces);

public sealed record OrganizationWorkspaceCreateRequest(string Name, IReadOnlyList<OrganizationWorkspaceInitialMemberRequest>? InitialMembers = null);

public sealed record OrganizationWorkspaceInitialMemberRequest(Guid AccountId, WorkspaceRole Role);

public sealed record OrganizationWorkspaceUpdateRequest(string Name, WorkspaceLifecycleStatus Status);

public sealed record OrganizationWorkspaceMembershipRequest(WorkspaceRole Role);

public sealed record WorkspaceSourceResponse(
    Guid Id,
    string Name,
    string Url,
    PackageSourceVisibility Ownership,
    int PackageCount);

public sealed record WorkspaceSourceRequest(
    string Name,
    string Url,
    bool Enabled,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string>? ExcludePatterns,
    PackageSourceVersionDiscoveryPolicy VersionDiscoveryPolicy = PackageSourceVersionDiscoveryPolicy.AllVersions);

public sealed record WorkspaceValidationErrorResponse(IReadOnlyList<string> Errors);

public sealed record WorkspaceEntitlementRequest(
    bool CanCreateCustomSources,
    int MaxSources,
    int? MaxPackagesIndexed,
    int? MaxVersionsPerPackage,
    int? MaxSyncsPerDay,
    bool PrivateFeedsEnabled);

public sealed record WorkspaceEntitlementResponse(
    Guid WorkspaceId,
    bool CanCreateCustomSources,
    int MaxSources,
    int? MaxPackagesIndexed,
    int? MaxVersionsPerPackage,
    int? MaxSyncsPerDay,
    bool PrivateFeedsEnabled,
    DateTimeOffset SyncedAt);
