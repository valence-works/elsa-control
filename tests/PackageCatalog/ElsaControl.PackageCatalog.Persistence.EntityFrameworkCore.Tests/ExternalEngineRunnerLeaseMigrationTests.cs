using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class ExternalEngineRunnerLeaseMigrationTests : IAsyncDisposable
{
    private const string PreviousSqliteMigration = "20260917224000_CapStripeHostedInstances";
    private const string SqliteMigration = "20260928020000_AddExternalEngineRunnerLease";
    private const string PreviousSqlServerMigration = "20260917224001_CapStripeHostedInstances";
    private const string SqlServerMigration = "20260928020001_AddExternalEngineRunnerLease";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 2, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000635");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000635");
    private static readonly Guid NoneId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SelfReportedId = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly Guid SupportedReleaseId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid VerifiedManifestId = Guid.Parse("30000000-0000-0000-0000-000000000004");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CatalogDbContext _sqlite;

    public ExternalEngineRunnerLeaseMigrationTests()
    {
        _connection.Open();
        _sqlite = new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite(
                _connection,
                sqlite => sqlite.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);
    }

    [Fact]
    public async Task Backfill_caps_only_supported_and_verified_rows_and_is_idempotent()
    {
        await _sqlite.Database.MigrateAsync(PreviousSqliteMigration);
        var organization = new Organization
        {
            Id = OrganizationId,
            Name = "Lease backfill organization",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        _sqlite.Organizations.Add(organization);
        _sqlite.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId,
            OrganizationId = OrganizationId,
            Organization = organization,
            Name = "Lease backfill workspace",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        await _sqlite.SaveChangesAsync();

        await SeedConnectionAsync(NoneId, "none-engine", "None", null);
        await SeedConnectionAsync(SelfReportedId, "self-engine", "SelfReported", "self-ref");
        await SeedConnectionAsync(SupportedReleaseId, "supported-engine", "SupportedRelease", "supported-ref");
        await SeedConnectionAsync(VerifiedManifestId, "verified-engine", "VerifiedManifest", "verified-ref");

        await _sqlite.Database.MigrateAsync(SqliteMigration);
        _sqlite.ChangeTracker.Clear();

        await AssertEvidenceAsync(NoneId, "None", null);
        await AssertEvidenceAsync(SelfReportedId, "SelfReported", "self-ref");
        await AssertEvidenceAsync(SupportedReleaseId, "SelfReported", null);
        await AssertEvidenceAsync(VerifiedManifestId, "SelfReported", null);

        var secondPass = await _sqlite.Database.ExecuteSqlRawAsync("""
            UPDATE "ExternalEngineConnections"
            SET "ReleaseEvidenceLevel" = 'SelfReported',
                "ReleaseEvidenceReference" = NULL
            WHERE "ReleaseEvidenceLevel" IN ('SupportedRelease', 'VerifiedManifest');
            """);
        Assert.Equal(0, secondPass);
        await AssertEvidenceAsync(NoneId, "None", null);
        await AssertEvidenceAsync(SelfReportedId, "SelfReported", "self-ref");
        await AssertEvidenceAsync(SupportedReleaseId, "SelfReported", null);
        await AssertEvidenceAsync(VerifiedManifestId, "SelfReported", null);
    }

    [Fact]
    public void Provider_migrations_are_discoverable_and_sql_server_keeps_expanded_audit_actions_on_down()
    {
        using var sqlServer = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(
                "Server=127.0.0.1,1;Database=ElsaControlMigrationScriptTests;User ID=unused;Password=unused;Encrypt=False",
                options => options.MigrationsAssembly(CatalogDatabaseServiceCollectionExtensions.SqlServerMigrationsAssembly))
            .Options);

        Assert.Contains(SqliteMigration, _sqlite.GetService<IMigrationsAssembly>().Migrations.Keys);
        Assert.Contains(SqlServerMigration, sqlServer.GetService<IMigrationsAssembly>().Migrations.Keys);
        var up = sqlServer.GetService<IMigrator>().GenerateScript(PreviousSqlServerMigration, SqlServerMigration);
        Assert.Contains("ActiveRunnerId", up, StringComparison.Ordinal);
        Assert.Contains("SupportedRelease", up, StringComparison.Ordinal);
        Assert.Contains("VerifiedManifest", up, StringComparison.Ordinal);
        Assert.Contains("SelfReported", up, StringComparison.Ordinal);

        var down = sqlServer.GetService<IMigrator>().GenerateScript(SqlServerMigration, PreviousSqlServerMigration);
        Assert.Contains("ActiveRunnerId", down, StringComparison.Ordinal);
        Assert.Contains("RunnerLeaseExpiresAt", down, StringComparison.Ordinal);
        Assert.DoesNotContain("CK_ExternalEngineConnectionAuditEvents_Action", down, StringComparison.Ordinal);
        Assert.DoesNotContain("'Created', 'PairingIssued'", down, StringComparison.Ordinal);
    }

    private async Task SeedConnectionAsync(Guid id, string slug, string evidenceLevel, string? evidenceReference)
    {
        var ticks = Now.UtcTicks;
        await _sqlite.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ExternalEngineConnections" (
                "Id", "OrganizationId", "WorkspaceId", "DisplayName", "OwnershipMode", "Status",
                "RuntimeHealth", "ConnectorReachability", "ReleaseEvidenceLevel", "ReleaseEvidenceReference",
                "ConnectorCompatibilityStatus", "IdempotencyKey", "CreateRequestDigest",
                "CreatedAt", "UpdatedAt", "Version")
            VALUES (
                {id}, {OrganizationId}, {WorkspaceId}, {slug}, 'CustomerOperated', 'Pending',
                'Unknown', 'Unknown', {evidenceLevel}, {evidenceReference},
                'Unknown', {slug + "-key"}, {slug + "-digest"},
                {ticks}, {ticks}, 1);
            """);
    }

    private async Task AssertEvidenceAsync(Guid id, string expectedLevel, string? expectedReference)
    {
        var row = await _sqlite.ExternalEngineConnections.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(expectedLevel, row.ReleaseEvidenceLevel);
        Assert.Equal(expectedReference, row.ReleaseEvidenceReference);
    }

    public async ValueTask DisposeAsync()
    {
        await _sqlite.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
