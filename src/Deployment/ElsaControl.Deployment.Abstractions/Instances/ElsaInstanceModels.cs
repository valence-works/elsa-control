using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ElsaControl.Deployment.Abstractions.Instances;

/// <summary>
/// Customer-controlled lifecycle intent. The provider reports a separate observed state.
/// </summary>
public enum ElsaDesiredLifecycle
{
    Running,
    Stopped,
    Deleting
}

/// <summary>
/// Provider/reconciler projection of an Elsa instance. Unknown is deliberately not healthy.
/// </summary>
public enum ElsaObservedLifecycle
{
    Pending,
    Provisioning,
    Ready,
    Updating,
    Degraded,
    Stopping,
    Stopped,
    Deleting,
    Failed,
    Unknown,
    Deleted
}

public enum ElsaInstanceHealth
{
    Healthy,
    Degraded,
    Unreachable,
    Unknown
}

/// <summary>
/// Catalog and product policy values are strings by design. Adding a release line or
/// topology must not require a C# enum or a schema change.
/// </summary>
public sealed record ElsaReleaseIntent
{
    public ElsaReleaseIntent(
        string distributionId,
        string releaseLine,
        string? requestedVersion = null,
        string channel = "stable",
        string patchUpdates = "automatic-within-minor",
        string minorUpdates = "explicit-approval",
        string majorMigrations = "explicit-migration",
        string? previewManifestDigest = null)
    {
        DistributionId = ElsaInstanceValue.Catalog(distributionId, nameof(distributionId));
        ReleaseLine = ElsaInstanceValue.Catalog(releaseLine, nameof(releaseLine));
        RequestedVersion = ElsaInstanceValue.CatalogOptional(requestedVersion);
        if (RequestedVersion is not null && !ElsaReleaseVersions.BelongsToLine(ReleaseLine, RequestedVersion))
            throw new ArgumentException("Requested version must belong to the selected release line.", nameof(requestedVersion));

        Channel = ElsaInstanceValue.Catalog(channel, nameof(channel));
        PatchUpdates = ElsaInstanceValue.Catalog(patchUpdates, nameof(patchUpdates));
        MinorUpdates = ElsaInstanceValue.Catalog(minorUpdates, nameof(minorUpdates));
        MajorMigrations = ElsaInstanceValue.Catalog(majorMigrations, nameof(majorMigrations));
        PreviewManifestDigest = ElsaInstanceValue.OptionalPreviewManifestDigest(previewManifestDigest, nameof(previewManifestDigest));
        if (PreviewManifestDigest is not null && RequestedVersion is null)
            throw new ArgumentException("A preview manifest digest requires an explicit requested version.", nameof(previewManifestDigest));
    }

    public string DistributionId { get; }

    public string ReleaseLine { get; }

    public string? RequestedVersion { get; }

    public string Channel { get; }

    public string PatchUpdates { get; }

    public string MinorUpdates { get; }

    public string MajorMigrations { get; }

    /// <summary>
    /// Explicit per-intent consent for a governed Preview release. A null value
    /// retains the normal Supported-only catalog policy.
    /// </summary>
    public string? PreviewManifestDigest { get; }

    public ElsaReleaseSelection Selection => new(DistributionId, ReleaseLine, RequestedVersion, Channel);
}

/// <summary>
/// A release identity used by upgrade transition rules. Version values may contain
/// prerelease/build labels and are never parsed as a finite product enum.
/// </summary>
public sealed record ElsaReleaseSelection
{
    public ElsaReleaseSelection(string distributionId, string releaseLine, string? version = null, string channel = "stable")
    {
        DistributionId = ElsaInstanceValue.Catalog(distributionId, nameof(distributionId));
        ReleaseLine = ElsaInstanceValue.Catalog(releaseLine, nameof(releaseLine));
        Version = ElsaInstanceValue.CatalogOptional(version);
        if (Version is not null && !ElsaReleaseVersions.BelongsToLine(ReleaseLine, Version))
            throw new ArgumentException("Version must belong to the selected release line.", nameof(version));

        Channel = ElsaInstanceValue.Catalog(channel, nameof(channel));
    }

    public string DistributionId { get; }

    public string ReleaseLine { get; }

    public string? Version { get; }

    public string Channel { get; }
}

public enum ElsaFeatureOverrideKind
{
    Boolean,
    Number,
    Catalog
}

/// <summary>
/// A typed, safe feature override. Free-form JSON and arbitrary text are not part of
/// the instance intent contract; catalog values remain governed by the catalog.
/// </summary>
public sealed record ElsaFeatureOverride
{
    [JsonConstructor]
    private ElsaFeatureOverride(ElsaFeatureOverrideKind kind, string value)
    {
        Kind = ElsaInstanceValue.RequireEnum(kind, nameof(kind));
        Value = ElsaInstanceValue.Require(value, nameof(value));
    }

    public ElsaFeatureOverrideKind Kind { get; }

    public string Value { get; }

    public static ElsaFeatureOverride FromBoolean(bool value) => new(ElsaFeatureOverrideKind.Boolean, value ? "true" : "false");

    public static ElsaFeatureOverride FromNumber(decimal value) =>
        new(ElsaFeatureOverrideKind.Number, value.ToString("G29", CultureInfo.InvariantCulture));

    public static ElsaFeatureOverride FromNumber(string value)
    {
        var normalized = ElsaInstanceValue.Require(value, nameof(value));
        if (!decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("A valid invariant decimal number is required.", nameof(value));
        return FromNumber(number);
    }

    public static ElsaFeatureOverride FromCatalog(string value) =>
        new(ElsaFeatureOverrideKind.Catalog, ElsaInstanceValue.Catalog(value, nameof(value)));

    public static implicit operator ElsaFeatureOverride(string value) => FromCatalog(value);
}

/// <summary>Topology, feature and package choices expressed as governed catalog values.</summary>
public sealed record ElsaApplicationIntent
{
    [JsonConstructor]
    public ElsaApplicationIntent(
        string topologyId,
        string? featurePresetId = null,
        IReadOnlyDictionary<string, ElsaFeatureOverride>? featureOverrides = null,
        string? packagePolicy = null,
        string? configurationShapeRevisionId = null)
    {
        TopologyId = ElsaInstanceValue.Catalog(topologyId, nameof(topologyId));
        FeaturePresetId = ElsaInstanceValue.CatalogOptional(featurePresetId);
        PackagePolicy = ElsaInstanceValue.CatalogOptional(packagePolicy);
        ConfigurationShapeRevisionId = ElsaInstanceValue.CatalogOptional(configurationShapeRevisionId);

        FeatureOverrides = featureOverrides is null
            ? new Dictionary<string, ElsaFeatureOverride>()
            : featureOverrides.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    public ElsaApplicationIntent(
        string topologyId,
        string? featurePresetId,
        IEnumerable<KeyValuePair<string, ElsaFeatureOverride>>? featureOverrides,
        string? packagePolicy = null,
        string? configurationShapeRevisionId = null)
        : this(
            topologyId,
            featurePresetId,
            featureOverrides?.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            packagePolicy,
            configurationShapeRevisionId)
    {
    }

    private string _topologyId = null!;
    private string? _featurePresetId;
    private IReadOnlyDictionary<string, ElsaFeatureOverride> _featureOverrides = null!;
    private string? _packagePolicy;
    private string? _configurationShapeRevisionId;

    public string TopologyId
    {
        get => _topologyId;
        init => _topologyId = ElsaInstanceValue.Catalog(value, nameof(TopologyId));
    }

    public string? FeaturePresetId
    {
        get => _featurePresetId;
        init => _featurePresetId = ElsaInstanceValue.CatalogOptional(value);
    }

    public IReadOnlyDictionary<string, ElsaFeatureOverride> FeatureOverrides
    {
        get => _featureOverrides;
        init => _featureOverrides = ElsaInstanceValue.NormalizeFeatureOverrides(value);
    }

    public string? PackagePolicy
    {
        get => _packagePolicy;
        init => _packagePolicy = ElsaInstanceValue.CatalogOptional(value);
    }

    public string? ConfigurationShapeRevisionId
    {
        get => _configurationShapeRevisionId;
        init => _configurationShapeRevisionId = ElsaInstanceValue.CatalogOptional(value);
    }
}

/// <summary>
/// Provider-neutral placement outcomes. Provider resource identifiers and credentials
/// intentionally have no representation in this contract.
/// </summary>
public sealed record ElsaPlacementIntent
{
    public ElsaPlacementIntent(
        string targetMode,
        string regionCode,
        string isolationProfile,
        string capacityProfile,
        string networkOutcome,
        string domainOutcome)
    {
        TargetMode = ElsaInstanceValue.Catalog(targetMode, nameof(targetMode));
        RegionCode = ElsaInstanceValue.Catalog(regionCode, nameof(regionCode));
        IsolationProfile = ElsaInstanceValue.Catalog(isolationProfile, nameof(isolationProfile));
        CapacityProfile = ElsaInstanceValue.Catalog(capacityProfile, nameof(capacityProfile));
        NetworkOutcome = ElsaInstanceValue.Catalog(networkOutcome, nameof(networkOutcome));
        DomainOutcome = ElsaInstanceValue.Catalog(domainOutcome, nameof(domainOutcome));
    }

    private string _targetMode = null!;
    private string _regionCode = null!;
    private string _isolationProfile = null!;
    private string _capacityProfile = null!;
    private string _networkOutcome = null!;
    private string _domainOutcome = null!;

    public string TargetMode
    {
        get => _targetMode;
        init => _targetMode = ElsaInstanceValue.Catalog(value, nameof(TargetMode));
    }

    public string RegionCode
    {
        get => _regionCode;
        init => _regionCode = ElsaInstanceValue.Catalog(value, nameof(RegionCode));
    }

    public string IsolationProfile
    {
        get => _isolationProfile;
        init => _isolationProfile = ElsaInstanceValue.Catalog(value, nameof(IsolationProfile));
    }

    public string CapacityProfile
    {
        get => _capacityProfile;
        init => _capacityProfile = ElsaInstanceValue.Catalog(value, nameof(CapacityProfile));
    }

    public string NetworkOutcome
    {
        get => _networkOutcome;
        init => _networkOutcome = ElsaInstanceValue.Catalog(value, nameof(NetworkOutcome));
    }

    public string DomainOutcome
    {
        get => _domainOutcome;
        init => _domainOutcome = ElsaInstanceValue.Catalog(value, nameof(DomainOutcome));
    }
}

/// <summary>
/// The immutable, provider-neutral customer intent source for an Elsa instance.
/// </summary>
public sealed record ElsaInstanceIntent
{
    public ElsaInstanceIntent(
        ElsaReleaseIntent release,
        ElsaApplicationIntent application,
        ElsaPlacementIntent placement,
        ElsaDesiredLifecycle desiredLifecycle = ElsaDesiredLifecycle.Running)
    {
        Release = release ?? throw new ArgumentNullException(nameof(release));
        Application = application ?? throw new ArgumentNullException(nameof(application));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        DesiredLifecycle = desiredLifecycle;
    }

    private ElsaReleaseIntent _release = null!;
    private ElsaApplicationIntent _application = null!;
    private ElsaPlacementIntent _placement = null!;
    private ElsaDesiredLifecycle _desiredLifecycle;

    public ElsaReleaseIntent Release
    {
        get => _release;
        init => _release = value ?? throw new ArgumentNullException(nameof(Release));
    }

    public ElsaApplicationIntent Application
    {
        get => _application;
        init => _application = value ?? throw new ArgumentNullException(nameof(Application));
    }

    public ElsaPlacementIntent Placement
    {
        get => _placement;
        init => _placement = value ?? throw new ArgumentNullException(nameof(Placement));
    }

    public ElsaDesiredLifecycle DesiredLifecycle
    {
        get => _desiredLifecycle;
        init => _desiredLifecycle = ElsaInstanceValue.RequireEnum(value, nameof(DesiredLifecycle));
    }

    /// <summary>
    /// Returns the exact canonical bytes used to identify this intent revision.
    /// </summary>
    public string ComputeCanonicalJson()
    {
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["configurationShapeRevisionId"] = Application.ConfigurationShapeRevisionId,
                ["featureOverrides"] = new SortedDictionary<string, object?>(
                    Application.FeatureOverrides.ToDictionary(x => x.Key, x => (object?)new SortedDictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["kind"] = x.Value.Kind.ToString(),
                        ["value"] = x.Value.Value
                    }, StringComparer.Ordinal),
                    StringComparer.Ordinal),
                ["featurePresetId"] = Application.FeaturePresetId,
                ["packagePolicy"] = Application.PackagePolicy,
                ["topologyId"] = Application.TopologyId
            },
            ["desiredLifecycle"] = DesiredLifecycle.ToString(),
            ["placement"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["capacityProfile"] = Placement.CapacityProfile,
                ["domainOutcome"] = Placement.DomainOutcome,
                ["isolationProfile"] = Placement.IsolationProfile,
                ["networkOutcome"] = Placement.NetworkOutcome,
                ["regionCode"] = Placement.RegionCode,
                ["targetMode"] = Placement.TargetMode
            },
            ["release"] = ReleaseCanonicalValues()
        };

        return JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
    }

    public string ComputeCanonicalHash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ComputeCanonicalJson()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string ComputeIntentHash() => ComputeCanonicalHash();

    private SortedDictionary<string, object?> ReleaseCanonicalValues()
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["channel"] = Release.Channel,
            ["distributionId"] = Release.DistributionId,
            ["majorMigrations"] = Release.MajorMigrations,
            ["minorUpdates"] = Release.MinorUpdates,
            ["patchUpdates"] = Release.PatchUpdates,
            ["releaseLine"] = Release.ReleaseLine,
            ["requestedVersion"] = Release.RequestedVersion
        };
        if (Release.PreviewManifestDigest is not null)
            values["previewManifestDigest"] = Release.PreviewManifestDigest;
        return values;
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>
/// Customer-facing instance aggregate. The safe observed fields are projections and
/// are intentionally separate from the desired intent source.
/// </summary>
public sealed record ElsaInstance
{
    public ElsaInstance(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string name,
        string slug,
        ElsaInstanceIntent intent)
        : this(id, organizationId, workspaceId, name, slug, intent, allowDeletingIntent: false)
    {
    }

    private ElsaInstance(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string name,
        string slug,
        ElsaInstanceIntent intent,
        bool allowDeletingIntent)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Instance ID is required.", nameof(id));
        if (organizationId == Guid.Empty)
            throw new ArgumentException("Organization ID is required.", nameof(organizationId));
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (intent is null)
            throw new ArgumentNullException(nameof(intent));
        if (!allowDeletingIntent && intent.DesiredLifecycle == ElsaDesiredLifecycle.Deleting)
            throw new ArgumentException("A new instance cannot start with deletion intent.", nameof(intent));

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        _name = ElsaInstanceValue.DisplayName(name, nameof(name));
        Slug = ElsaInstanceSlug.Normalize(slug);
        _intent = intent;
    }

    /// <summary>
    /// Rehydrates a persisted aggregate after validating its complete tombstone and
    /// projection invariants. New mutations must go through the state machine.
    /// </summary>
    public static ElsaInstance Hydrate(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string name,
        string slug,
        ElsaInstanceIntent intent,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        int version,
        ElsaInstanceIdentityBinding? identityBinding = null,
        ElsaDesiredStateRevisionId? desiredStateRevisionId = null,
        ElsaResolvedPlanReference? resolvedPlanReference = null,
        ElsaCurrentResolvedRelease? currentResolvedRelease = null,
        ElsaCurrentDeploymentReference? currentDeploymentReference = null,
        ElsaPlacementAssignmentReference? placementAssignmentReference = null,
        ElsaTenantReference? elsaTenantReference = null,
        ElsaLastOperationId? lastOperationId = null,
        DateTimeOffset? deletedAt = null)
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        ElsaInstanceValue.RequireEnum(health, nameof(health));
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive.");
        if (observedLifecycle == ElsaObservedLifecycle.Deleted &&
            (intent?.DesiredLifecycle != ElsaDesiredLifecycle.Deleting || deletedAt is null))
            throw new ArgumentException("A deleted aggregate must have deletion intent and a tombstone timestamp.", nameof(observedLifecycle));
        if (observedLifecycle != ElsaObservedLifecycle.Deleted && deletedAt is not null)
            throw new ArgumentException("Only a deleted aggregate can have a tombstone timestamp.", nameof(deletedAt));
        if (identityBinding is not null && identityBinding.InstanceId != id)
            throw new ArgumentException("Identity binding belongs to a different instance.", nameof(identityBinding));
        if (currentResolvedRelease is not null &&
            (resolvedPlanReference is null || !Equals(currentResolvedRelease.PlanReference, resolvedPlanReference)))
            throw new ArgumentException("Current release and plan projections must identify the same immutable plan.", nameof(currentResolvedRelease));

        return new ElsaInstance(
            id, organizationId, workspaceId, name, slug, intent!, observedLifecycle, health, version,
            identityBinding, desiredStateRevisionId, resolvedPlanReference, currentResolvedRelease,
            currentDeploymentReference, placementAssignmentReference, elsaTenantReference, lastOperationId, deletedAt);
    }

    private ElsaInstance(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string name,
        string slug,
        ElsaInstanceIntent intent,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        int version,
        ElsaInstanceIdentityBinding? identityBinding,
        ElsaDesiredStateRevisionId? desiredStateRevisionId,
        ElsaResolvedPlanReference? resolvedPlanReference,
        ElsaCurrentResolvedRelease? currentResolvedRelease,
        ElsaCurrentDeploymentReference? currentDeploymentReference,
        ElsaPlacementAssignmentReference? placementAssignmentReference,
        ElsaTenantReference? elsaTenantReference,
        ElsaLastOperationId? lastOperationId,
        DateTimeOffset? deletedAt)
        : this(id, organizationId, workspaceId, name, slug, intent, allowDeletingIntent: true)
    {
        ElsaInstanceValue.RequireEnum(observedLifecycle, nameof(observedLifecycle));
        ElsaInstanceValue.RequireEnum(health, nameof(health));
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be positive.");
        if (identityBinding is not null && identityBinding.InstanceId != id)
            throw new ArgumentException("Identity binding belongs to a different instance.", nameof(identityBinding));
        if (currentResolvedRelease is not null &&
            (resolvedPlanReference is null || !Equals(currentResolvedRelease.PlanReference, resolvedPlanReference)))
            throw new ArgumentException("Current release and plan projections must identify the same immutable plan.", nameof(currentResolvedRelease));
        if (observedLifecycle == ElsaObservedLifecycle.Deleted &&
            (Intent.DesiredLifecycle != ElsaDesiredLifecycle.Deleting || deletedAt is null))
            throw new ArgumentException("A deleted aggregate must have deletion intent and a tombstone timestamp.", nameof(observedLifecycle));
        if (observedLifecycle != ElsaObservedLifecycle.Deleted && deletedAt is not null)
            throw new ArgumentException("Only a deleted aggregate can have a tombstone timestamp.", nameof(deletedAt));

        _observedLifecycle = observedLifecycle;
        _health = health;
        _version = version;
        _identityBinding = identityBinding;
        _desiredStateRevisionId = ValidateReference(desiredStateRevisionId, nameof(desiredStateRevisionId));
        _resolvedPlanReference = resolvedPlanReference;
        _currentResolvedRelease = currentResolvedRelease;
        _currentDeploymentReference = currentDeploymentReference;
        _placementAssignmentReference = placementAssignmentReference;
        _elsaTenantReference = elsaTenantReference;
        _lastOperationId = ValidateReference(lastOperationId, nameof(lastOperationId));
        _deletedAt = deletedAt?.ToUniversalTime();
    }

    private ElsaInstance(
        ElsaInstance source,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceHealth health,
        DateTimeOffset? deletedAt)
    {
        Id = source.Id;
        OrganizationId = source.OrganizationId;
        WorkspaceId = source.WorkspaceId;
        Slug = source.Slug;
        _name = source._name;
        _intent = source._intent;
        _observedLifecycle = observedLifecycle;
        _health = health;
        _version = source._version;
        _identityBinding = source._identityBinding;
        _desiredStateRevisionId = source._desiredStateRevisionId;
        _resolvedPlanReference = source._resolvedPlanReference;
        _currentResolvedRelease = source._currentResolvedRelease;
        _currentDeploymentReference = source._currentDeploymentReference;
        _placementAssignmentReference = source._placementAssignmentReference;
        _elsaTenantReference = source._elsaTenantReference;
        _lastOperationId = source._lastOperationId;
        _deletedAt = deletedAt;
    }

    public Guid Id { get; }

    public Guid OrganizationId { get; }

    public Guid WorkspaceId { get; }

    private string _name = null!;
    private ElsaInstanceIntent _intent = null!;
    private ElsaObservedLifecycle _observedLifecycle = ElsaObservedLifecycle.Pending;
    private ElsaInstanceHealth _health = ElsaInstanceHealth.Unknown;
    private int _version = 1;
    private ElsaInstanceIdentityBinding? _identityBinding;
    private ElsaDesiredStateRevisionId? _desiredStateRevisionId;
    private ElsaResolvedPlanReference? _resolvedPlanReference;
    private ElsaCurrentResolvedRelease? _currentResolvedRelease;
    private ElsaCurrentDeploymentReference? _currentDeploymentReference;
    private ElsaPlacementAssignmentReference? _placementAssignmentReference;
    private ElsaTenantReference? _elsaTenantReference;
    private ElsaLastOperationId? _lastOperationId;

    public string Name
    {
        get => _name;
        internal init => _name = ElsaInstanceValue.DisplayName(value, nameof(Name));
    }

    public string Slug { get; }

    public ElsaInstanceIntent Intent
    {
        get => _intent;
        internal init => _intent = value ?? throw new ArgumentNullException(nameof(Intent));
    }

    public ElsaReleaseIntent ReleaseIntent => Intent.Release;

    public ElsaApplicationIntent ApplicationIntent => Intent.Application;

    public ElsaPlacementIntent PlacementIntent => Intent.Placement;

    public ElsaDesiredLifecycle DesiredLifecycle => Intent.DesiredLifecycle;

    public ElsaObservedLifecycle ObservedLifecycle
    {
        get => _observedLifecycle;
        internal init
        {
            ElsaInstanceValue.RequireEnum(value, nameof(ObservedLifecycle));
            _observedLifecycle = value;
        }
    }

    public ElsaInstanceHealth Health
    {
        get => _health;
        internal init => _health = ElsaInstanceValue.RequireEnum(value, nameof(Health));
    }

    public int Version
    {
        get => _version;
        internal init => _version = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Version), "Version must be positive.");
    }

    public ElsaInstanceIdentityBinding? IdentityBinding
    {
        get => _identityBinding;
        internal init
        {
            if (value is not null && value.InstanceId != Id)
                throw new ArgumentException("Identity binding belongs to a different instance.", nameof(IdentityBinding));
            _identityBinding = value;
        }
    }

    public ElsaDesiredStateRevisionId? DesiredStateRevisionId
    {
        get => _desiredStateRevisionId;
        internal init => _desiredStateRevisionId = ValidateReference(value, nameof(DesiredStateRevisionId));
    }

    public ElsaResolvedPlanReference? ResolvedPlanReference
    {
        get => _resolvedPlanReference;
        internal init
        {
            if (value is null && _currentResolvedRelease is not null)
                throw new ArgumentException("A current release must retain its resolved plan reference.", nameof(ResolvedPlanReference));
            if (value is not null && _currentResolvedRelease is not null &&
                !Equals(value, _currentResolvedRelease.PlanReference))
                throw new ArgumentException("Resolved plan projections must identify the same immutable plan.", nameof(ResolvedPlanReference));
            _resolvedPlanReference = value;
        }
    }

    public ElsaCurrentResolvedRelease? CurrentResolvedRelease
    {
        get => _currentResolvedRelease;
        internal init
        {
            if (value is not null && (_resolvedPlanReference is null ||
                !Equals(value.PlanReference, _resolvedPlanReference)))
                throw new ArgumentException("Current release must identify the aggregate's resolved plan.", nameof(CurrentResolvedRelease));
            _currentResolvedRelease = value;
        }
    }

    public ElsaCurrentDeploymentReference? CurrentDeploymentReference
    {
        get => _currentDeploymentReference;
        internal init => _currentDeploymentReference = value;
    }

    public ElsaPlacementAssignmentReference? PlacementAssignmentReference
    {
        get => _placementAssignmentReference;
        internal init => _placementAssignmentReference = value;
    }

    public ElsaTenantReference? ElsaTenantReference
    {
        get => _elsaTenantReference;
        internal init => _elsaTenantReference = value;
    }

    public ElsaLastOperationId? LastOperationId
    {
        get => _lastOperationId;
        internal init => _lastOperationId = ValidateReference(value, nameof(LastOperationId));
    }

    private DateTimeOffset? _deletedAt;

    public DateTimeOffset? DeletedAt
    {
        get => _deletedAt;
        internal init
        {
            if (value is not null || _observedLifecycle == ElsaObservedLifecycle.Deleted)
                throw new InvalidOperationException("Deletion timestamps can only be projected by the deletion reconciler.");
            _deletedAt = null;
        }
    }

    public string ComputeCanonicalIntentHash() => Intent.ComputeCanonicalHash();

    public bool BelongsTo(Guid organizationId, Guid workspaceId) =>
        OrganizationId == organizationId && WorkspaceId == workspaceId;

    public ElsaInstance AttachIdentityBinding(ElsaInstanceIdentityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.InstanceId != Id)
            throw new ArgumentException("Identity binding belongs to a different instance.", nameof(binding));
        return this with { IdentityBinding = binding };
    }

    public ElsaInstance Rename(string name) => this with { Name = ElsaInstanceValue.DisplayName(name, nameof(name)) };

    /// <summary>
    /// Projects the immutable plan selected by the asynchronous lifecycle worker.
    /// The plan reference and current release are accepted together so they cannot
    /// diverge at the aggregate boundary.
    /// </summary>
    public ElsaInstance AttachResolvedPlan(
        ElsaResolvedPlanReference reference,
        ElsaCurrentResolvedRelease currentRelease)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(currentRelease);
        if (!Equals(reference, currentRelease.PlanReference))
            throw new ArgumentException("Current release must identify the resolved plan.", nameof(currentRelease));

        // Record `with` applies init setters one field at a time. An Apply that
        // replaces an already-projected pin plan would otherwise validate the
        // new reference against the still-attached pin release and throw
        // ArgumentException — the Dogfood2 residual after #413.
        return new ElsaInstance(
            Id,
            OrganizationId,
            WorkspaceId,
            Name,
            Slug,
            Intent,
            ObservedLifecycle,
            Health,
            Version,
            IdentityBinding,
            DesiredStateRevisionId,
            reference,
            currentRelease,
            CurrentDeploymentReference,
            PlacementAssignmentReference,
            ElsaTenantReference,
            LastOperationId,
            DeletedAt);
    }

    internal ElsaInstance ProjectObservation(
        ElsaObservedLifecycle observed,
        ElsaInstanceHealth health,
        DateTimeOffset? deletedAt)
    {
        ElsaInstanceValue.RequireEnum(observed, nameof(observed));
        ElsaInstanceValue.RequireEnum(health, nameof(health));
        if (observed == ElsaObservedLifecycle.Deleted && Intent.DesiredLifecycle != ElsaDesiredLifecycle.Deleting)
            throw new InvalidOperationException("Only a deleting instance can be projected as deleted.");
        if (observed == ElsaObservedLifecycle.Deleted && deletedAt is null)
            throw new InvalidOperationException("A deleted tombstone must retain its deletion timestamp.");
        if (observed != ElsaObservedLifecycle.Deleted && deletedAt is not null)
            throw new InvalidOperationException("Only a deleted tombstone can have a deletion timestamp.");
        return new ElsaInstance(this, observed, health, deletedAt?.ToUniversalTime());
    }

    private static ElsaDesiredStateRevisionId? ValidateReference(ElsaDesiredStateRevisionId? value, string parameterName) =>
        value is null || !value.Value.IsEmpty
            ? value
            : throw new ArgumentException("An empty reference is not allowed.", parameterName);

    private static ElsaLastOperationId? ValidateReference(ElsaLastOperationId? value, string parameterName) =>
        value is null || !value.Value.IsEmpty
            ? value
            : throw new ArgumentException("An empty reference is not allowed.", parameterName);
}

public static class ElsaInstanceValue
{
    public static string DisplayName(string value, string parameterName)
    {
        var normalized = Require(value, parameterName);
        if (normalized.Length > 256)
            throw new ArgumentException("Display name cannot exceed 256 characters.", parameterName);
        return normalized;
    }

    public static string Require(string value, string parameterName)
    {
        if (value is null)
            throw new ArgumentException("Value cannot be empty.", parameterName);
        if (value.Any(char.IsControl))
            throw new ArgumentException("Value cannot contain control characters.", parameterName);
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Value cannot be empty.", parameterName);
        return normalized;
    }

    public static string? Optional(string? value)
    {
        if (value is not null && value.Any(char.IsControl))
            throw new ArgumentException("Value cannot contain control characters.", nameof(value));
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
    }

    public static string Catalog(string value, string parameterName)
    {
        var normalized = Require(value, parameterName);
        if (normalized.Length > 128 ||
            !IsAsciiAlphaNumeric(normalized[0]) ||
            normalized.Any(x => !(IsAsciiAlphaNumeric(x) || x is '.' or '_' or '-' or '+')))
            throw new ArgumentException("Catalog values must be bounded safe tokens.", parameterName);
        return normalized.ToLowerInvariant();
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static string? CatalogOptional(string? value)
    {
        if (value is not null && value.Any(char.IsControl))
            throw new ArgumentException("Value cannot contain control characters.", nameof(value));
        return string.IsNullOrWhiteSpace(value) ? null : Catalog(value, nameof(value));
    }

    public static string? OptionalPreviewManifestDigest(string? value, string parameterName)
    {
        if (value is null)
            return null;
        if (value.Length != 71 ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value[7..].Any(x => !((x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'))))
            throw new ArgumentException("A canonical lowercase SHA-256 digest is required.", parameterName);
        return value;
    }

    public static TEnum RequireEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Unknown enum value.");

    public static IReadOnlyDictionary<string, ElsaFeatureOverride> NormalizeFeatureOverrides(
        IReadOnlyDictionary<string, ElsaFeatureOverride> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values
            .Select(x => new KeyValuePair<string, ElsaFeatureOverride>(
                Catalog(x.Key, "featureOverrides key"),
                x.Value ?? throw new ArgumentException("Feature override values cannot be null.", nameof(values))))
            .ToArray();
        if (normalized.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new ArgumentException("Feature override keys must be unique.", nameof(values));
        return new ReadOnlyDictionary<string, ElsaFeatureOverride>(new SortedDictionary<string, ElsaFeatureOverride>(
            normalized.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            StringComparer.Ordinal));
    }

}

public static class ElsaInstanceSlug
{
    public static string Normalize(string value)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("Slug cannot be empty or contain control characters.", nameof(value));
        var trimmed = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException("Slug cannot be empty.", nameof(value));

        var builder = new StringBuilder(trimmed.Length);
        var separator = false;
        foreach (var character in trimmed)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator)
            {
                builder.Append('-');
                separator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length is 0 or > 96)
            throw new ArgumentException("Slug must contain between 1 and 96 normalized characters.", nameof(value));
        return slug;
    }
}

internal static class ElsaReleaseVersions
{
    public static bool BelongsToLine(string releaseLine, string version) =>
        string.Equals(releaseLine, version, StringComparison.OrdinalIgnoreCase) ||
        version.StartsWith(releaseLine + ".", StringComparison.OrdinalIgnoreCase);
}
