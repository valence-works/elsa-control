using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed partial class ElsaInstanceLifecycleStoreTests
{
    [Fact]
    public async Task Create_without_selector_records_bicep()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Backend default workspace");
        var created = await new ElsaInstanceLifecycleService(CreateStore(db), new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "backend-bicep-elsa",
                CreateIntent(), "create-backend-bicep"));

        var backend = await CreateStore(db).GetBackendAsync(workspace.Id, created.Instance.Id);
        Assert.Equal(ElsaInstanceProvisioningBackend.Bicep, backend);
        var row = await db.ElsaInstances.AsNoTracking().SingleAsync(x => x.Id == created.Instance.Id);
        Assert.Equal(ElsaInstanceProvisioningBackendText.Bicep, row.ProvisioningBackend);
    }

    [Fact]
    public async Task Create_records_pulumi_when_the_selector_pins_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Backend pulumi workspace");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, backendSelector: new StaticBackendSelector(ElsaInstanceProvisioningBackend.Pulumi));
        var created = await new ElsaInstanceLifecycleService(store, new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "backend-pulumi-elsa",
                CreateIntent(), "create-backend-pulumi"));

        Assert.Equal(
            ElsaInstanceProvisioningBackend.Pulumi,
            await store.GetBackendAsync(workspace.Id, created.Instance.Id));
    }

    [Fact]
    public async Task Later_operations_do_not_change_the_pinned_backend()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateMigratedContext(connection);
        await db.Database.MigrateAsync();
        var workspace = await CreateWorkspaceAsync(db, "Backend retry workspace");
        var store = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, backendSelector: new StaticBackendSelector(ElsaInstanceProvisioningBackend.Pulumi));
        var created = await new ElsaInstanceLifecycleService(store, new FixedTimeProvider(Now))
            .CreateAsync(new ElsaInstanceCreateRequest(
                workspace.OrganizationId, workspace.Id, "Managed Elsa", "backend-retry-elsa",
                CreateIntent(), "create-backend-retry"));
        await CompleteOperationAsync(db, created.Operation.Id);

        var flipped = new EfCoreElsaInstanceLifecycleStore(
            db, EmptyResolutionInputSource.Instance, backendSelector: new StaticBackendSelector(ElsaInstanceProvisioningBackend.Bicep));
        db.ChangeTracker.Clear();
        await new ElsaInstanceLifecycleService(flipped, new FixedTimeProvider(Now.AddMinutes(1)))
            .UpdateIntentAsync(new ElsaInstanceIntentUpdateRequest(
                workspace.Id, created.Instance.Id, CreateIntent(), created.Instance.Version, "update-backend",
                Reason: "keep owner", ActorAccountId: Guid.NewGuid()));

        Assert.Equal(
            ElsaInstanceProvisioningBackend.Pulumi,
            await flipped.GetBackendAsync(workspace.Id, created.Instance.Id));
    }

    private sealed class StaticBackendSelector(ElsaInstanceProvisioningBackend backend) : IElsaInstanceProvisioningBackendSelector
    {
        public ElsaInstanceProvisioningBackend SelectForCreate() => backend;
    }
}
