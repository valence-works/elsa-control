using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Core.Packages;
using ElsaControl.PackageCatalog.Core.Sync;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

/// <summary>
/// A save runs the persistence guards over the changes detected once at its start, and the base save detects again
/// what the guards changed. The schema is created without the migrations' guard triggers, so only the guards can
/// reject a write here.
/// </summary>
public sealed class CatalogDbContextChangeDetectionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogDbContext _db = null!;
    private int _scans;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = CreateContext();
        await _db.Database.EnsureCreatedAsync();
        _db.ChangeTracker.DetectingAllChanges += (_, _) => _scans++;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_scans_the_tracked_entities_exactly_twice_however_many_are_tracked(bool async)
    {
        var runs = Enumerable.Range(0, 500).Select(_ => new SyncRun { Trigger = SyncRunTrigger.Scheduled }).ToArray();
        _db.SyncRuns.AddRange(runs);
        await _db.SaveChangesAsync();
        runs[0].Error = "Changed while tracked.";
        _scans = 0;

        if (async)
            await _db.SaveChangesAsync();
        else
            _db.SaveChanges();

        // One scan before the guards run, one inside the base save for what the guards changed.
        Assert.Equal(2, _scans);
        Assert.Equal("Changed while tracked.", await ReadAsync(db => db.SyncRuns.Where(x => x.Id == runs[0].Id).Select(x => x.Error).SingleAsync()));
    }

    [Fact]
    public async Task Save_leaves_change_detection_to_a_caller_that_turned_it_off()
    {
        var run = new SyncRun { Trigger = SyncRunTrigger.Scheduled };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        run.Error = "Never detected.";
        _scans = 0;

        await _db.SaveChangesAsync();

        Assert.False(_db.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal(0, _scans);
        Assert.Null(await ReadAsync(db => db.SyncRuns.Where(x => x.Id == run.Id).Select(x => x.Error).SingleAsync()));
    }

    [Fact]
    public async Task Guard_mutation_of_a_property_the_caller_left_alone_is_persisted()
    {
        var instance = await SeedInstanceAsync();
        instance.Name = "Renamed instance";

        await _db.SaveChangesAsync();

        // The guards advance Version after the rename was detected; only the base save's detection marks it modified.
        Assert.Equal(2, await ReadAsync(db => db.ElsaInstances.Where(x => x.Id == instance.Id).Select(x => x.Version).SingleAsync()));
    }

    [Fact]
    public async Task Guard_added_organization_is_persisted_as_the_owner_of_its_new_workspace()
    {
        _db.Workspaces.Add(new Workspace { Name = "Guarded workspace" });

        await _db.SaveChangesAsync();

        await using var reader = CreateContext();
        var organization = await reader.Organizations.SingleAsync();
        Assert.Equal("Guarded workspace", organization.Name);
        Assert.Equal(organization.Id, (await reader.Workspaces.SingleAsync()).OrganizationId);
    }

    [Fact]
    public async Task Append_only_guard_rejects_a_change_made_through_a_tracked_property()
    {
        var replay = new ManagedElsaHandoffReplayEntity { Jti = "handoff-1", ConsumedAt = Now, ExpiresAt = Now.AddMinutes(5) };
        _db.ManagedElsaHandoffReplays.Add(replay);
        await _db.SaveChangesAsync();
        replay.ExpiresAt = Now.AddHours(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());

        Assert.Equal("Managed Elsa handoff replay records are append-only.", exception.Message);
        Assert.True(_db.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal(Now.AddMinutes(5), await ReadAsync(db => db.ManagedElsaHandoffReplays.Select(x => x.ExpiresAt).SingleAsync()));
    }

    [Fact]
    public async Task No_delete_guard_rejects_removing_a_durable_row()
    {
        _db.ElsaInstances.Remove(await SeedInstanceAsync());

        var exception = Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());

        Assert.Equal("Elsa instances are tombstoned and cannot be deleted.", exception.Message);
        Assert.True(_db.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal(1, await ReadAsync(db => db.ElsaInstances.CountAsync()));
    }

    private CatalogDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);

    private async Task<T> ReadAsync<T>(Func<CatalogDbContext, Task<T>> read)
    {
        await using var reader = CreateContext();
        return await read(reader);
    }

    private async Task<ElsaInstanceEntity> SeedInstanceAsync()
    {
        var workspace = new Workspace { Name = "Instance workspace" };
        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();
        var instance = ElsaInstancePersistenceTests.NewInstance(workspace.OrganizationId, workspace.Id);
        _db.ElsaInstances.Add(instance);
        await _db.SaveChangesAsync();
        return instance;
    }
}
