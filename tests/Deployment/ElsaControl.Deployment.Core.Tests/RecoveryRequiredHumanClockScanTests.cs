using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using ElsaControl.Deployment.Core.Workspace;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class RecoveryRequiredHumanClockScanTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T03:00:00Z");

    [Fact]
    public void SelectCandidates_orders_by_reason_entered_then_id_and_caps_candidates()
    {
        var late = Item(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3"), Now.AddMinutes(2));
        var early = Item(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"), Now);
        var middle = Item(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2"), Now.AddMinutes(1));
        var flagged = Item(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4"), Now, requiresHumanAt: Now);
        var notDue = Item(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5"), Now.AddMinutes(5));

        var selected = RecoveryRequiredHumanClockScan.SelectCandidates(
                [late, early, middle, flagged, notDue],
                item => item.RequiresHumanAt,
                item => item.ReasonEnteredAt,
                item => item.Id,
                Now.AddMinutes(2) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter,
                limit: 2)
            .Select(item => item.Id)
            .ToArray();

        Assert.Equal([early.Id, middle.Id], selected);
    }

    [Fact]
    public void RecordFailure_logs_the_shared_message_and_counts()
    {
        Exception? logged = null;
        Guid loggedId = Guid.Empty;
        var operationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa6");
        var fault = new InvalidOperationException("scan fault");

        RecoveryRequiredHumanClockScan.RecordFailure(
            operationId,
            fault,
            (exception, id) =>
            {
                logged = exception;
                loggedId = id;
            });

        Assert.Same(fault, logged);
        Assert.Equal(operationId, loggedId);
        Assert.Contains("{OperationId}", RecoveryRequiredHumanClockScan.FailureLogMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_memory_scan_matches_shared_order_limit_and_logging()
    {
        var faults = new List<(Exception Exception, Guid OperationId)>();
        var clock = new MutableTimeProvider(Now);
        var store = new InMemoryElsaInstanceLifecycleStore(clock, clockScanFailed: (exception, id) => faults.Add((exception, id)));
        var service = new ElsaInstanceLifecycleService(store, clock);
        var organizationId = Guid.Parse("20000000-0000-0000-0000-000000000021");
        var workspaceId = Guid.Parse("10000000-0000-0000-0000-000000000021");
        var first = await CreateAsync(service, organizationId, workspaceId, "scan-one");
        store.MarkRecoveryRequired(first.Operation.Id, ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await CreateAsync(service, organizationId, workspaceId, "scan-two");
        store.MarkRecoveryRequired(second.Operation.Id, ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain);
        clock.Advance(TimeSpan.FromMinutes(1));
        var third = await CreateAsync(service, organizationId, workspaceId, "scan-three");
        store.MarkRecoveryRequired(third.Operation.Id, ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain);
        store.ClockScanProbe = id =>
        {
            if (id == first.Operation.Id)
                throw new InvalidOperationException("first candidate failed.");
        };

        var dueAt = Now + TimeSpan.FromMinutes(2) + ManagedElsaReasonCodeCatalog.HumanRequiredAfter;
        Assert.Equal(1, await store.AdvanceDueHumanRequiredClocksAsync(dueAt, limit: 2));
        Assert.Null(store.GetReasonClock(first.Operation.Id).RequiresHumanAt);
        Assert.Equal(dueAt, store.GetReasonClock(second.Operation.Id).RequiresHumanAt);
        Assert.Null(store.GetReasonClock(third.Operation.Id).RequiresHumanAt);
        var fault = Assert.Single(faults);
        Assert.Equal(first.Operation.Id, fault.OperationId);
        Assert.Equal("first candidate failed.", fault.Exception.Message);
    }

    private static async Task<ElsaInstanceLifecycleAcceptance> CreateAsync(
        ElsaInstanceLifecycleService service,
        Guid organizationId,
        Guid workspaceId,
        string slug) =>
        await service.CreateAsync(new ElsaInstanceCreateRequest(
            organizationId,
            workspaceId,
            slug,
            slug,
            new ElsaInstanceIntent(
                new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
                new ElsaApplicationIntent("combined", "starter", packagePolicy: "approved"),
                new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"),
                ElsaDesiredLifecycle.Running),
            $"create-{slug}"));

    private static Candidate Item(
        Guid id,
        DateTimeOffset reasonEnteredAt,
        DateTimeOffset? requiresHumanAt = null) =>
        new(id, reasonEnteredAt, requiresHumanAt);

    private sealed record Candidate(Guid Id, DateTimeOffset? ReasonEnteredAt, DateTimeOffset? RequiresHumanAt);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
