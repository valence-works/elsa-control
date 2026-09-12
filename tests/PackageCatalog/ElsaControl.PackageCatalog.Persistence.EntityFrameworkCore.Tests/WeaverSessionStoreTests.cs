using System.Text.Json;
using ElsaControl.Weaver.Core.Configuration;
using ElsaControl.Weaver.Core.Sessions;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class WeaverSessionStoreTests : IAsyncDisposable
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-06-07T12:00:00Z");
    private readonly CatalogDbContext _db = CreateDbContext();
    private readonly WeaverSessionStore _store;

    public WeaverSessionStoreTests()
    {
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _store = new WeaverSessionStore(_db);
    }

    [Fact]
    public async Task Stores_session_messages_tool_calls_and_plans()
    {
        var session = await _store.CreateSessionAsync(NewSession());
        await _store.AddMessageAsync(_workspaceId, new WeaverMessage(
            Guid.NewGuid(),
            session.Id,
            WeaverMessageRole.User,
            "What is wrong here?",
            WeaverRedactionState.None,
            1,
            _now));
        await _store.AddToolCallAsync(_workspaceId, new WeaverToolCall(
            Guid.NewGuid(),
            session.Id,
            "get_environment_detail",
            """{"environmentId":"env-1"}""",
            null,
            """{"summary":"Environment is blocked."}""",
            WeaverToolAuthorizationResult.Allowed,
            WeaverToolCallStatus.Succeeded,
            12,
            "trace-1",
            _now,
            _now.AddMilliseconds(12)));
        await _store.AddPlanAsync(_workspaceId, new WeaverPlan(
            Guid.NewGuid(),
            session.Id,
            1,
            WeaverPlanType.Promotion,
            "Promote Test to Production",
            "Promote the validated Test revision.",
            """{"environment":"Production"}""",
            """{"deployments":1}""",
            """{"blocked":false}""",
            """{"rollback":"previous revision"}""",
            WeaverPlanRisk.Medium,
            WeaverPlanStatus.ReadyForApproval,
            _accountId,
            _now,
            _now));

        var reloaded = await _store.GetSessionAsync(_workspaceId, session.Id);
        var messages = await _store.ListMessagesAsync(_workspaceId, session.Id);
        var plans = await _store.ListPlansAsync(_workspaceId, session.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("deployment", reloaded!.Context!.RootElement.GetProperty("route").GetString());
        Assert.Equal("What is wrong here?", Assert.Single(messages).Content);
        Assert.Equal("Promote Test to Production", Assert.Single(plans).Title);
        var toolCallCount = await CountRowsAsync("WeaverToolCalls");
        Assert.Equal(1, toolCallCount);
    }

    [Fact]
    public async Task Workspace_mismatch_cannot_append_to_session()
    {
        var session = await _store.CreateSessionAsync(NewSession());
        var otherWorkspaceId = Guid.NewGuid();
        var message = new WeaverMessage(
            Guid.NewGuid(),
            session.Id,
            WeaverMessageRole.User,
            "Read another workspace",
            WeaverRedactionState.None,
            1,
            _now);

        var act = () => _store.AddMessageAsync(otherWorkspaceId, message);

        await Assert.ThrowsAsync<KeyNotFoundException>(act);
        var messageCount = await CountRowsAsync("WeaverMessages");
        Assert.Equal(0, messageCount);
    }

    private WeaverSession NewSession()
    {
        var context = JsonDocument.Parse("""{"route":"deployment"}""");
        return new WeaverSession(
            Guid.NewGuid(),
            _workspaceId,
            null,
            _accountId,
            "copilot-session-1",
            "/admin/deployments",
            context,
            WeaverMode.Inspect,
            WeaverProviderMode.Fake,
            "gpt-5",
            "medium",
            WeaverSessionStatus.Active,
            _now,
            _now,
            null);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private async Task<long> CountRowsAsync(string table)
    {
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    private static CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseRetryingSqlite("Data Source=:memory:")
            .Options;

        return new CatalogDbContext(options);
    }
}
