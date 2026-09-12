namespace ElsaControl.PackageCatalog.Core.Accounts;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ExternalIdentity> ExternalIdentities { get; set; } = [];
    public List<OrganizationMembership> OrganizationMemberships { get; set; } = [];
    public List<WorkspaceMembership> Memberships { get; set; } = [];
}

public sealed class ExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string Name { get; set; } = "";
    public WorkspaceKind Kind { get; set; } = WorkspaceKind.Personal;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SoftDeletedAt { get; set; }
    public List<WorkspaceMembership> Memberships { get; set; } = [];
    public List<WorkspaceEntitlementSnapshot> EntitlementSnapshots { get; set; } = [];
}

public sealed class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public string? CustomerReference { get; set; }
    public List<OrganizationMembership> Memberships { get; set; } = [];
    public List<Workspace> Workspaces { get; set; } = [];
    public List<OrganizationEntitlementSnapshot> EntitlementSnapshots { get; set; } = [];
    public List<OrganizationAuditRecord> AuditRecords { get; set; } = [];
    public List<OrganizationSubscription> Subscriptions { get; set; } = [];
    public List<BillingProviderEventInboxEntry> BillingProviderEvents { get; set; } = [];
    public List<OrganizationBillingLifecycleNotice> BillingLifecycleNotices { get; set; } = [];
    public List<OrganizationBillingCleanup> BillingCleanups { get; set; } = [];
}

public sealed class OrganizationMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public OrganizationRole Role { get; set; } = OrganizationRole.Member;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DisabledAt { get; set; }
    public Guid? InvitedByAccountId { get; set; }
}

public sealed class WorkspaceMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public WorkspaceRole Role { get; set; } = WorkspaceRole.Reader;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkspaceEntitlementSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }
    public bool CanCreateCustomSources { get; set; }
    public int MaxSources { get; set; }
    public int? MaxPackagesIndexed { get; set; }
    public int? MaxVersionsPerPackage { get; set; }
    public int? MaxSyncsPerDay { get; set; }
    public bool PrivateFeedsEnabled { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OrganizationEntitlementSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public bool CanCreateCustomSources { get; set; }
    public int MaxSources { get; set; }
    public int MaxWorkspaces { get; set; }
    /// <summary>Maximum number of retained managed instances for this organization.</summary>
    public int MaxInstances { get; set; } = int.MaxValue;
    public int? MaxPackagesIndexed { get; set; }
    public int? MaxVersionsPerPackage { get; set; }
    public int? MaxSyncsPerDay { get; set; }
    public bool PrivateFeedsEnabled { get; set; }
    public bool ManagedHostingEnabled { get; set; }
    /// <summary>
    /// UTC instant from which managed hosting no longer admits create or update
    /// work. Null means the capability does not expire. The commercial gate
    /// compares it with its clock on every decision, so expiry never depends on
    /// a background job having run.
    /// </summary>
    public DateTimeOffset? ManagedHostingExpiresAt { get; set; }
    public bool DeploymentTargetsEnabled { get; set; }
    /// <summary>
    /// The provider-neutral commercial lifecycle state used when the snapshot
    /// was projected. Capability and limit fields remain the authoritative
    /// product policy and are intentionally preserved by billing projection.
    /// Null means no commercial lifecycle has been projected for this legacy
    /// or otherwise uninitialized entitlement snapshot.
    /// </summary>
    public OrganizationSubscriptionState? SubscriptionState { get; set; }
    public Guid? SubscriptionId { get; set; }
    public OrganizationSubscription? Subscription { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OrganizationAuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid? ActorAccountId { get; set; }
    public string? OperatorSubject { get; set; }
    public OrganizationAuditAction Action { get; set; }
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum OrganizationStatus
{
    Active,
    Archived,
    Suspended
}

public enum OrganizationRole
{
    Reader,
    Member,
    WorkspaceCreator,
    BillingAdmin,
    Administrator,
    Owner
}

public enum OrganizationAuditAction
{
    OrganizationCreated,
    OrganizationArchived,
    MembershipChanged,
    WorkspaceCreated,
    WorkspaceArchived,
    EntitlementChanged,
    RoleChanged,
    SubscriptionChanged,
    BillingEventConsumed,
    BillingLifecycleNoticeIssued,
    BillingCleanupRequested,
    BillingCleanupCompleted
}

public enum WorkspaceKind
{
    Personal = 0,
    [Obsolete("Organization is now a root aggregate. Use Shared for organization-owned workspaces.")]
    Organization = 1,
    Shared = 2
}

public enum WorkspaceRole
{
    Reader,
    SourceAdmin,
    Owner
}

public enum PackageSourceVisibility
{
    Public,
    Workspace
}
