using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

public sealed class GovernedReleaseCatalogStore(DbContextOptions<CatalogDbContext> dbOptions) : IGovernedReleaseCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GovernedReleaseCatalogWriteResult> StoreAsync(
        IReadOnlyList<GovernedReleaseCatalogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("At least one catalog topology is required.", nameof(entries));

        foreach (var entry in entries)
            Validate(entry);
        ValidateBatch(entries);

        var identity = CatalogIdentity(entries[0]);
        var digest = Normalize(entries[0].ManifestDigest);
        var fingerprint = ProjectionFingerprint(entries);
        await using var db = new CatalogDbContext(dbOptions);
        try
        {
            return await db.ExecuteInTransactionAsync(IsolationLevel.Serializable, async () =>
            {
                var existing = await FindExistingCandidatesAsync(db, identity, digest, entries[0].RegistryClass, cancellationToken);
                if (existing.Count != 0)
                    return Existing(existing, fingerprint);

                var entity = ToEntity(entries, identity, fingerprint);
                db.GovernedReleaseCatalog.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return new GovernedReleaseCatalogWriteResult(GovernedReleaseCatalogWriteStatus.Stored, ToEntries(entity));
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The losing writer's transaction has rolled back; reread the winner's catalog row.
            var existing = await FindExistingCandidatesAsync(db, identity, digest, entries[0].RegistryClass, cancellationToken);
            if (existing.Count == 0)
                throw;
            return Existing(existing, fingerprint);
        }
    }

    public async Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
        GovernedReleaseCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = NormalizeQuery(query);
        await using var db = new CatalogDbContext(dbOptions);
        var roots = db.GovernedReleaseCatalog.AsNoTracking();
        roots = Exact(roots, query.DistributionId, x => x.DistributionId);
        roots = Exact(roots, query.ReleaseLine, x => x.ReleaseLine);
        roots = Exact(roots, query.ReleaseVersion, x => x.ReleaseVersion);
        roots = Exact(roots, query.Channel, x => x.Channel);
        roots = Exact(roots, query.ProducerLifecycle, x => x.ProducerLifecycle);
        roots = Exact(roots, query.CatalogLifecycle, x => x.CatalogLifecycle);
        roots = Exact(roots, query.RegistryClass, x => x.RegistryClass);

        if (query.TopologyId is not null)
        {
            var topologyId = query.TopologyId;
            roots = roots.Where(x => x.Topologies.Any(topology => topology.TopologyId == topologyId));
        }

        if (query.RuntimeKind is not null)
        {
            var runtimeKind = query.RuntimeKind;
            roots = roots.Where(x => x.Topologies.Any(topology =>
                topology.RuntimeKinds.Any(kind => kind.RuntimeKind == runtimeKind)));
        }

        if (query.Capability is not null)
        {
            var capability = query.Capability;
            roots = roots.Where(x => x.Topologies.Any(topology =>
                topology.Capabilities.Any(item => item.Capability == capability)
                || topology.Components.Any(component => component.Capabilities.Any(item => item.Capability == capability))));
        }

        var materialized = await AggregateQuery(roots)
            .OrderBy(x => x.DistributionId)
            .ThenBy(x => x.ReleaseLine)
            .ThenBy(x => x.ReleaseVersion)
            .ThenBy(x => x.Generation)
            .ThenBy(x => x.RegistryClass)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return materialized
            .SelectMany(ToEntries)
            .Where(entry => Matches(entry, query))
            .OrderBy(entry => entry.Distribution.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Distribution.ReleaseLine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Distribution.ReleaseVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Distribution.Generation, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RegistryClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Topology.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<GovernedReleaseCatalogEntity>> FindExistingCandidatesAsync(
        CatalogDbContext db,
        string identity,
        string manifestDigest,
        string registryClass,
        CancellationToken cancellationToken) =>
        await AggregateQuery(db.GovernedReleaseCatalog.AsNoTracking())
            .Where(x => x.CatalogIdentityHash == identity
                        || (x.ManifestDigest == manifestDigest
                            && x.RegistryClass == Normalize(registryClass)))
            .OrderBy(x => x.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

    private static IQueryable<GovernedReleaseCatalogEntity> AggregateQuery(
        IQueryable<GovernedReleaseCatalogEntity> roots) =>
        roots
            .AsSplitQuery()
            .Include(x => x.PackageDeclarations)
            .Include(x => x.Topologies).ThenInclude(x => x.RuntimeKinds)
            .Include(x => x.Topologies).ThenInclude(x => x.Capabilities)
            .Include(x => x.Topologies).ThenInclude(x => x.ComponentVersions)
            .Include(x => x.Topologies).ThenInclude(x => x.Evidence)
            .Include(x => x.Topologies).ThenInclude(x => x.Components).ThenInclude(x => x.PlatformDigests)
            .Include(x => x.Topologies).ThenInclude(x => x.Components).ThenInclude(x => x.Roles)
            .Include(x => x.Topologies).ThenInclude(x => x.Components).ThenInclude(x => x.Capabilities)
            .Include(x => x.Topologies).ThenInclude(x => x.Components).ThenInclude(x => x.Endpoints);

    private static GovernedReleaseCatalogWriteResult Existing(
        IReadOnlyList<GovernedReleaseCatalogEntity> existing,
        string fingerprint) =>
        existing.Count == 1
        && string.Equals(existing[0].ProjectionFingerprint, fingerprint, StringComparison.Ordinal)
            ? new(GovernedReleaseCatalogWriteStatus.Unchanged, ToEntries(existing[0]))
            : new(GovernedReleaseCatalogWriteStatus.Conflict, [], "catalog.identity.conflict");

    private static GovernedReleaseCatalogEntity ToEntity(
        IReadOnlyList<GovernedReleaseCatalogEntry> entries,
        string identity,
        string fingerprint)
    {
        var first = entries[0];
        var root = new GovernedReleaseCatalogEntity
        {
            Id = Guid.NewGuid(),
            CatalogIdentityHash = identity,
            ProjectionFingerprint = fingerprint,
            SchemaVersion = first.SchemaVersion,
            ManifestReference = first.ManifestReference,
            ManifestDigest = Normalize(first.ManifestDigest),
            PayloadDigest = Normalize(first.PayloadDigest),
            SignatureEvidenceReference = first.SignatureEvidenceReference,
            SignatureEvidenceDigest = Normalize(first.SignatureEvidenceDigest),
            RegistryClass = Normalize(first.RegistryClass),
            DistributionId = Normalize(first.Distribution.Id),
            Generation = Normalize(first.Distribution.Generation),
            ReleaseLine = Normalize(first.Distribution.ReleaseLine),
            ReleaseVersion = Normalize(first.Distribution.ReleaseVersion),
            Channel = Normalize(first.Distribution.Channel),
            ProducerLifecycle = Normalize(first.Distribution.ProducerLifecycle),
            Edition = first.Distribution.Edition,
            SourceRepository = first.Distribution.SourceRepository,
            SourceCommit = first.Distribution.SourceCommit,
            SourceRunId = first.Distribution.SourceRunId,
            // The application supplies immutable Control policy for this admitted
            // catalog identity; a policy change requires a separate transition path.
            CatalogLifecycle = Normalize(first.CatalogLifecycle),
            AdmittedAtUtcTicks = first.AdmittedAt.UtcTicks,
            ComponentDeclarationsFormat = first.ComponentDeclarations?.Format,
            ComponentDeclarationsDigest = first.ComponentDeclarations?.Digest is { } declarationsDigest
                ? Normalize(declarationsDigest)
                : null
        };

        foreach (var package in first.ComponentDeclarations?.Packages ?? [])
            root.PackageDeclarations.Add(new()
            {
                Id = Guid.NewGuid(),
                PackageId = package.Id,
                Version = package.Version
            });

        foreach (var entry in entries)
        {
            var topology = new GovernedReleaseCatalogTopologyEntity
            {
                Id = Guid.NewGuid(),
                TopologyId = Normalize(entry.Topology.Id),
                PackageManifestSchema = entry.Topology.PackageManifestSchema
            };
            foreach (var value in entry.Topology.RuntimeKinds)
                topology.RuntimeKinds.Add(new() { Id = Guid.NewGuid(), RuntimeKind = Normalize(value) });
            foreach (var value in entry.Topology.Capabilities)
                topology.Capabilities.Add(new() { Id = Guid.NewGuid(), Capability = Normalize(value) });
            foreach (var component in entry.Topology.ComponentVersions)
                topology.ComponentVersions.Add(new() { Id = Guid.NewGuid(), ComponentId = component.Id, Version = component.Version });
            foreach (var evidence in entry.Topology.Evidence)
                topology.Evidence.Add(new() { Id = Guid.NewGuid(), Kind = evidence.Kind, Reference = evidence.Reference, Digest = Normalize(evidence.Digest) });

            foreach (var component in entry.Topology.Components)
            {
                var image = new GovernedReleaseCatalogComponentEntity
                {
                    Id = Guid.NewGuid(),
                    ComponentId = component.Id,
                    ImageReference = component.ImageReference,
                    ImageDigest = Normalize(component.ImageDigest),
                    CompanionComponentId = component.CompanionComponentId
                };
                foreach (var platform in component.PlatformDigests)
                    image.PlatformDigests.Add(new() { Id = Guid.NewGuid(), Platform = platform.Key, Digest = Normalize(platform.Value) });
                foreach (var role in component.Roles)
                    image.Roles.Add(new() { Id = Guid.NewGuid(), Role = role });
                foreach (var capability in component.Capabilities)
                    image.Capabilities.Add(new() { Id = Guid.NewGuid(), Capability = Normalize(capability) });
                foreach (var endpoint in component.Endpoints)
                    image.Endpoints.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        Name = endpoint.Name,
                        Protocol = endpoint.Protocol,
                        Port = endpoint.Port,
                        Visibility = endpoint.Visibility,
                        RequiresTls = endpoint.RequiresTls,
                        Path = endpoint.Path
                    });
                topology.Components.Add(image);
            }
            root.Topologies.Add(topology);
        }

        return root;
    }

    private static IReadOnlyList<GovernedReleaseCatalogEntry> ToEntries(GovernedReleaseCatalogEntity root) =>
        root.Topologies
            .OrderBy(x => x.TopologyId, StringComparer.OrdinalIgnoreCase)
            .Select(topology => new GovernedReleaseCatalogEntry(
                root.SchemaVersion,
                root.ManifestReference,
                root.ManifestDigest,
                root.PayloadDigest,
                root.SignatureEvidenceReference,
                root.SignatureEvidenceDigest,
                root.RegistryClass,
                new(
                    root.DistributionId,
                    root.Generation,
                    root.ReleaseLine,
                    root.ReleaseVersion,
                    root.Channel,
                    root.ProducerLifecycle,
                    root.Edition,
                    root.SourceRepository,
                    root.SourceCommit,
                    root.SourceRunId),
                new(
                    topology.TopologyId,
                    topology.PackageManifestSchema,
                    topology.RuntimeKinds.Select(x => x.RuntimeKind).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    topology.Capabilities.Select(x => x.Capability).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    topology.ComponentVersions
                        .OrderBy(x => x.ComponentId, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new GovernedReleaseComponentVersion(x.ComponentId, x.Version))
                        .ToArray(),
                    topology.Components
                        .OrderBy(x => x.ComponentId, StringComparer.OrdinalIgnoreCase)
                        .Select(component => new GovernedReleaseComponent(
                            component.ComponentId,
                            component.ImageReference,
                            component.ImageDigest,
                            component.PlatformDigests
                                .OrderBy(x => x.Platform, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(x => x.Platform, x => x.Digest, StringComparer.OrdinalIgnoreCase),
                            component.Roles.Select(x => x.Role).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                            component.Capabilities.Select(x => x.Capability).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                            component.Endpoints
                                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                                .Select(x => new GovernedReleaseEndpoint(x.Name, x.Protocol, x.Port, x.Visibility, x.RequiresTls, x.Path))
                                .ToArray(),
                            component.CompanionComponentId))
                        .ToArray(),
                    topology.Evidence
                        .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new GovernedReleaseEvidence(x.Kind, x.Reference, x.Digest))
                        .ToArray()),
                root.CatalogLifecycle,
                new DateTimeOffset(root.AdmittedAtUtcTicks, TimeSpan.Zero),
                ToComponentDeclarations(root)))
            .ToArray();

    private static GovernedReleaseComponentDeclarations? ToComponentDeclarations(GovernedReleaseCatalogEntity root)
    {
        var hasFormat = !string.IsNullOrWhiteSpace(root.ComponentDeclarationsFormat);
        var hasDigest = !string.IsNullOrWhiteSpace(root.ComponentDeclarationsDigest);
        var hasPackages = root.PackageDeclarations.Count > 0;
        if (!hasFormat && !hasDigest && !hasPackages)
            return null;
        if (!hasFormat || !hasDigest || !hasPackages)
            throw new InvalidOperationException("Persisted component declarations are incomplete.");

        return new(
            root.ComponentDeclarationsFormat!,
            root.ComponentDeclarationsDigest!,
            root.PackageDeclarations
                .OrderBy(x => x.PackageId, StringComparer.OrdinalIgnoreCase)
                .Select(x => new GovernedReleasePackageDeclaration(x.PackageId, x.Version))
                .ToArray());
    }

    private static bool Matches(GovernedReleaseCatalogEntry entry, GovernedReleaseCatalogQuery query) =>
        (query.DistributionId is null || string.Equals(entry.Distribution.Id, query.DistributionId, StringComparison.OrdinalIgnoreCase))
        && (query.ReleaseLine is null || string.Equals(entry.Distribution.ReleaseLine, query.ReleaseLine, StringComparison.OrdinalIgnoreCase))
        && (query.ReleaseVersion is null || string.Equals(entry.Distribution.ReleaseVersion, query.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
        && (query.Channel is null || string.Equals(entry.Distribution.Channel, query.Channel, StringComparison.OrdinalIgnoreCase))
        && (query.RegistryClass is null || string.Equals(entry.RegistryClass, query.RegistryClass, StringComparison.OrdinalIgnoreCase))
        && (query.TopologyId is null || string.Equals(entry.Topology.Id, query.TopologyId, StringComparison.Ordinal))
        && (query.CatalogLifecycle is null || string.Equals(entry.CatalogLifecycle, query.CatalogLifecycle, StringComparison.OrdinalIgnoreCase))
        && (query.ProducerLifecycle is null || string.Equals(entry.Distribution.ProducerLifecycle, query.ProducerLifecycle, StringComparison.OrdinalIgnoreCase))
        && (query.RuntimeKind is null || entry.Topology.RuntimeKinds.Contains(query.RuntimeKind, StringComparer.Ordinal))
        && (query.Capability is null || entry.Topology.Capabilities
            .Concat(entry.Topology.Components.SelectMany(x => x.Capabilities))
            .Contains(query.Capability, StringComparer.Ordinal));

    private static GovernedReleaseCatalogQuery NormalizeQuery(GovernedReleaseCatalogQuery query) => query with
    {
        DistributionId = NormalizeOptional(query.DistributionId),
        ReleaseLine = NormalizeOptional(query.ReleaseLine),
        ReleaseVersion = NormalizeOptional(query.ReleaseVersion),
        Channel = NormalizeOptional(query.Channel),
        ProducerLifecycle = NormalizeOptional(query.ProducerLifecycle),
        CatalogLifecycle = NormalizeOptional(query.CatalogLifecycle),
        RegistryClass = NormalizeOptional(query.RegistryClass),
        TopologyId = NormalizeOptional(query.TopologyId),
        RuntimeKind = NormalizeOptional(query.RuntimeKind),
        Capability = NormalizeOptional(query.Capability)
    };

    private static IQueryable<GovernedReleaseCatalogEntity> Exact(
        IQueryable<GovernedReleaseCatalogEntity> rows,
        string? value,
        Expression<Func<GovernedReleaseCatalogEntity, string>> selector)
    {
        if (value is null)
            return rows;
        return rows.Where(Expression.Lambda<Func<GovernedReleaseCatalogEntity, bool>>(
            Expression.Equal(selector.Body, Expression.Constant(value)), selector.Parameters));
    }

    private static string CatalogIdentity(GovernedReleaseCatalogEntry entry) => Fingerprint(string.Join('\n',
        Normalize(entry.Distribution.Id),
        Normalize(entry.Distribution.Generation),
        Normalize(entry.Distribution.ReleaseLine),
        Normalize(entry.Distribution.ReleaseVersion),
        Normalize(entry.RegistryClass)));

    private static string ProjectionFingerprint(IReadOnlyList<GovernedReleaseCatalogEntry> entries)
    {
        var canonical = entries
            .Select(Canonicalize)
            .OrderBy(x => x.Topology.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Fingerprint(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    private static GovernedReleaseCatalogEntry Canonicalize(GovernedReleaseCatalogEntry entry) => entry with
    {
        ManifestDigest = Normalize(entry.ManifestDigest),
        PayloadDigest = Normalize(entry.PayloadDigest),
        SignatureEvidenceDigest = Normalize(entry.SignatureEvidenceDigest),
        RegistryClass = Normalize(entry.RegistryClass),
        Distribution = entry.Distribution with
        {
            Id = Normalize(entry.Distribution.Id),
            Generation = Normalize(entry.Distribution.Generation),
            ReleaseLine = Normalize(entry.Distribution.ReleaseLine),
            ReleaseVersion = Normalize(entry.Distribution.ReleaseVersion),
            Channel = Normalize(entry.Distribution.Channel),
            ProducerLifecycle = Normalize(entry.Distribution.ProducerLifecycle)
        },
        CatalogLifecycle = Normalize(entry.CatalogLifecycle),
        AdmittedAt = DateTimeOffset.UnixEpoch,
        ComponentDeclarations = entry.ComponentDeclarations is null
            ? null
            : entry.ComponentDeclarations with
            {
                Format = entry.ComponentDeclarations.Format,
                Digest = Normalize(entry.ComponentDeclarations.Digest),
                Packages = entry.ComponentDeclarations.Packages
                    .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
        Topology = entry.Topology with
        {
            Id = Normalize(entry.Topology.Id),
            RuntimeKinds = entry.Topology.RuntimeKinds.Select(Normalize).Order(StringComparer.Ordinal).ToArray(),
            Capabilities = entry.Topology.Capabilities.Select(Normalize).Order(StringComparer.Ordinal).ToArray(),
            ComponentVersions = entry.Topology.ComponentVersions
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Components = entry.Topology.Components
                .Select(component => component with
                {
                    ImageDigest = Normalize(component.ImageDigest),
                    PlatformDigests = new SortedDictionary<string, string>(
                        component.PlatformDigests.ToDictionary(x => x.Key, x => Normalize(x.Value), StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase),
                    Roles = component.Roles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Capabilities = component.Capabilities.Select(Normalize).Order(StringComparer.Ordinal).ToArray(),
                    Endpoints = component.Endpoints.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                })
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Evidence = entry.Topology.Evidence
                .Select(evidence => evidence with { Digest = Normalize(evidence.Digest) })
                .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Reference, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Digest, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        }
    };

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Normalize(value);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Validate(GovernedReleaseCatalogEntry entry)
    {
        if (entry.Distribution is null || entry.Topology is null)
            throw new ArgumentException("Catalog release and topology are required.", nameof(entry));
        GovernedReleaseCatalogStorageContract.ValidateLengths(entry);
        Required(entry.SchemaVersion, nameof(entry.SchemaVersion));
        Required(entry.ManifestReference, nameof(entry.ManifestReference));
        Digest(entry.ManifestDigest, nameof(entry.ManifestDigest));
        Digest(entry.PayloadDigest, nameof(entry.PayloadDigest));
        Required(entry.SignatureEvidenceReference, nameof(entry.SignatureEvidenceReference));
        Digest(entry.SignatureEvidenceDigest, nameof(entry.SignatureEvidenceDigest));
        SafeEvidenceReference(entry.ManifestReference, entry.ManifestDigest, nameof(entry.ManifestReference));
        SafeEvidenceReference(entry.SignatureEvidenceReference, entry.SignatureEvidenceDigest, nameof(entry.SignatureEvidenceReference));
        Required(entry.RegistryClass, nameof(entry.RegistryClass));
        Required(entry.CatalogLifecycle, nameof(entry.CatalogLifecycle));
        if (entry.ComponentDeclarations is { } declarations)
        {
            RequiredSafeIdentity(declarations.Format, "componentDeclarations.format");
            Digest(declarations.Digest, "componentDeclarations.digest");
            if (declarations.Packages is null || declarations.Packages.Count is 0 or > 2048)
                throw new ArgumentException("Catalog component declarations must contain packages.", nameof(entry));
            if (declarations.Packages.Any(package => package is null))
                throw new ArgumentException("Catalog component declarations cannot contain null packages.", nameof(entry));
            EnsureUnique(declarations.Packages.Select(x => x.Id), "componentDeclarations.packages");
            foreach (var package in declarations.Packages)
            {
                RequiredSafeIdentity(package.Id, "componentDeclarations.package.id");
                RequiredSafeIdentity(package.Version, "componentDeclarations.package.version");
            }
        }
        Required(entry.Distribution.Id, "distribution.id");
        Required(entry.Distribution.Generation, "distribution.generation");
        Required(entry.Distribution.ReleaseLine, "distribution.releaseLine");
        Required(entry.Distribution.ReleaseVersion, "distribution.releaseVersion");
        Required(entry.Distribution.Channel, "distribution.channel");
        Required(entry.Distribution.ProducerLifecycle, "distribution.producerLifecycle");
        Required(entry.Distribution.SourceRepository, "distribution.sourceRepository");
        Required(entry.Distribution.SourceCommit, "distribution.sourceCommit");
        Required(entry.Distribution.SourceRunId, "distribution.sourceRunId");
        Required(entry.Topology.Id, "topology.id");
        Required(entry.Topology.PackageManifestSchema, "topology.packageManifestSchema");
        EnsureUnique(entry.Topology.RuntimeKinds, "topology.runtimeKinds");
        EnsureUnique(entry.Topology.Capabilities, "topology.capabilities");
        EnsureUnique(entry.Topology.ComponentVersions.Select(x => x.Id), "topology.componentVersions");
        EnsureUnique(entry.Topology.Components.Select(x => x.Id), "topology.components");
        EnsureUnique(entry.Topology.Evidence.Select(x => x.Kind), "topology.evidence");
        foreach (var componentVersion in entry.Topology.ComponentVersions)
        {
            Required(componentVersion.Id, "componentVersion.id");
            Required(componentVersion.Version, "componentVersion.version");
        }
        foreach (var component in entry.Topology.Components)
        {
            Required(component.Id, "component.id");
            Required(component.ImageReference, "component.imageReference");
            Digest(component.ImageDigest, "component.imageDigest");
            if (!IsSafeImageReference(component.ImageReference, component.ImageDigest))
                throw new ArgumentException("Catalog image references must be safe immutable OCI locators.", nameof(entry));
            foreach (var platformDigest in component.PlatformDigests)
            {
                Required(platformDigest.Key, "component.platform");
                Digest(platformDigest.Value, "component.platformDigest");
            }
            EnsureUnique(component.PlatformDigests.Keys, "component.platformDigests");
            EnsureUnique(component.Roles, "component.roles");
            EnsureUnique(component.Capabilities, "component.capabilities");
            EnsureUnique(component.Endpoints.Select(x => x.Name), "component.endpoints");
            foreach (var endpoint in component.Endpoints)
            {
                Required(endpoint.Name, "endpoint.name");
                Required(endpoint.Protocol, "endpoint.protocol");
                Required(endpoint.Visibility, "endpoint.visibility");
                if (endpoint.Port is < 1 or > 65535)
                    throw new ArgumentException("Endpoint ports must be between 1 and 65535.", nameof(entry));
            }
        }
        foreach (var evidence in entry.Topology.Evidence)
        {
            Required(evidence.Kind, "evidence.kind");
            Required(evidence.Reference, "evidence.reference");
            Digest(evidence.Digest, "evidence.digest");
            if (!ReleaseManifestEvidenceContract.IsSafe(
                    evidence.Kind,
                    evidence.Reference,
                    evidence.Digest,
                    ReleaseManifestEvidenceContract.DescriptionFor(evidence.Kind)))
                throw new ArgumentException("Catalog evidence must use the safe retained-evidence contract.", nameof(entry));
        }
    }

    private static void ValidateBatch(IReadOnlyList<GovernedReleaseCatalogEntry> entries)
    {
        var first = entries[0];
        foreach (var entry in entries.Skip(1))
        {
            if (!string.Equals(entry.ManifestReference, first.ManifestReference, StringComparison.Ordinal)
                || entry.SchemaVersion != first.SchemaVersion
                || !string.Equals(entry.ManifestDigest, first.ManifestDigest, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.PayloadDigest, first.PayloadDigest, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.SignatureEvidenceReference, first.SignatureEvidenceReference, StringComparison.Ordinal)
                || !string.Equals(entry.SignatureEvidenceDigest, first.SignatureEvidenceDigest, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.RegistryClass, first.RegistryClass, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.CatalogLifecycle, first.CatalogLifecycle, StringComparison.OrdinalIgnoreCase)
                || !EquivalentComponentDeclarations(entry.ComponentDeclarations, first.ComponentDeclarations)
                || !EquivalentDistribution(entry.Distribution, first.Distribution))
                throw new ArgumentException("A catalog write must contain one immutable release projection.", nameof(entries));
        }
        EnsureUnique(entries.Select(x => x.Topology.Id), "topology.identities");
    }

    private static bool EquivalentComponentDeclarations(
        GovernedReleaseComponentDeclarations? left,
        GovernedReleaseComponentDeclarations? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (!string.Equals(left.Format, right.Format, StringComparison.Ordinal)
            || !string.Equals(left.Digest, right.Digest, StringComparison.OrdinalIgnoreCase)
            || left.Packages.Count != right.Packages.Count)
            return false;

        return left.Packages
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .Zip(right.Packages
                    .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase),
                (a, b) => string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(a.Version, b.Version, StringComparison.Ordinal))
            .All(x => x);
    }

    private static bool EquivalentDistribution(GovernedReleaseDistribution left, GovernedReleaseDistribution right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Generation, right.Generation, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ReleaseLine, right.ReleaseLine, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ReleaseVersion, right.ReleaseVersion, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Channel, right.Channel, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ProducerLifecycle, right.ProducerLifecycle, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Edition, right.Edition, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.SourceRepository, right.SourceRepository, StringComparison.Ordinal)
        && string.Equals(left.SourceCommit, right.SourceCommit, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.SourceRunId, right.SourceRunId, StringComparison.Ordinal);

    private static void EnsureUnique(IEnumerable<string> values, string name)
    {
        var normalized = values.Select(value => Normalize(value)).ToArray();
        if (normalized.Any(string.IsNullOrWhiteSpace) || normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Catalog identities must be non-empty and unique.", name);
    }

    private static void Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A required catalog value is missing.", name);
    }

    private static void RequiredSafeIdentity(string? value, string name)
    {
        Required(value, name);
        if (value!.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("A catalog identity contains unsafe characters.", name);
    }

    private static void Digest(string? value, string name)
    {
        if (value is null
            || value.Length != 71
            || !value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || !value[7..].All(Uri.IsHexDigit))
            throw new ArgumentException("Catalog digests must use sha256.", name);
    }

    private static void SafeEvidenceReference(string reference, string digest, string name)
    {
        if (!ReleaseManifestEvidenceContract.IsSafeReference(reference, digest))
            throw new ArgumentException("Catalog evidence references must be safe immutable locators.", name);
    }

    private static bool IsSafeImageReference(string reference, string digest)
    {
        var marker = reference.IndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return false;
        var name = reference[..marker];
        var lastSegment = name[(name.LastIndexOf('/') + 1)..];
        if (lastSegment.Contains(':', StringComparison.Ordinal))
            return false;

        var absolute = reference.Contains("://", StringComparison.Ordinal)
            ? reference
            : $"oci://{reference}";
        return absolute.StartsWith("oci://", StringComparison.OrdinalIgnoreCase)
               && ReleaseManifestEvidenceContract.IsSafeReference(absolute, digest);
    }
}
