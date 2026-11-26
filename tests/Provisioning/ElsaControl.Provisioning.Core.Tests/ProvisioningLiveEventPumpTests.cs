using ElsaControl.Provisioning.Abstractions;
using Microsoft.Extensions.Logging;

namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningLiveEventPumpTests
{
    [Fact]
    public async Task A_sink_error_does_not_fail_complete()
    {
        var sink = new ThrowingSink();
        var logger = new RecordingLogger();
        await using var pump = new ProvisioningLiveEventPump(Guid.NewGuid(), sink, logger);
        Assert.True(pump.TryEnqueue([Event(ProvisioningEventKind.ResourceStarted, "resource-group")]));

        await pump.CompleteAsync();

        Assert.Equal(1, sink.Calls);
        Assert.Contains(logger.Entries, item => item.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, item => item.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Entries, item => item.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Drain_coalesces_to_the_last_event_per_stage()
    {
        var sink = new RecordingSink();
        await using var pump = new ProvisioningLiveEventPump(Guid.NewGuid(), sink);
        Assert.True(pump.TryEnqueue(
        [
            Event(ProvisioningEventKind.ResourceStarted, "resource-group", "first"),
            Event(ProvisioningEventKind.ResourceStarted, "resource-group", "second"),
            Event(ProvisioningEventKind.ResourceStarted, ProvisioningCapability.ContainerRuntime, "runtime")
        ]));

        await pump.CompleteAsync();

        var published = Assert.Single(sink.Writes);
        Assert.Equal(2, published.Count);
        Assert.Equal("second", published[0].Message);
        Assert.Equal("runtime", published[1].Message);
    }

    [Fact]
    public void Coalesce_keeps_the_last_event_for_each_kind_and_resource()
    {
        var coalesced = ProvisioningLiveEventPump.Coalesce(
        [
            [Event(ProvisioningEventKind.ResourceStarted, "resource-group", "one")],
            [
                Event(ProvisioningEventKind.ResourceStarted, "resource-group", "two"),
                Event(ProvisioningEventKind.ResourceSucceeded, "resource-group", "done")
            ]
        ]);

        Assert.Equal(2, coalesced.Count);
        Assert.Equal("two", coalesced[0].Message);
        Assert.Equal("done", coalesced[1].Message);
    }

    private static ProvisioningEvent Event(
        ProvisioningEventKind kind,
        string resourceKind,
        string message = "Resource started.") =>
        new(Guid.NewGuid(), kind, resourceKind, message, DateTimeOffset.UtcNow, 1);

    private sealed class ThrowingSink : IProvisioningLiveEventSink
    {
        public int Calls { get; private set; }

        public IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request) =>
            throw new NotSupportedException();

        public Task PublishAsync(
            Guid operationId,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("password=hunter2");
        }

        public Task PublishTerminalAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task HeartbeatAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSink : IProvisioningLiveEventSink
    {
        public List<IReadOnlyList<ProvisioningEvent>> Writes { get; } = [];

        public IDisposable Bind(ProvisioningOperation operation, ProvisioningRequest request) =>
            throw new NotSupportedException();

        public Task PublishAsync(
            Guid operationId,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(events);
            return Task.CompletedTask;
        }

        public Task PublishTerminalAsync(
            ProvisioningOperation operation,
            ProvisioningRequest? request,
            IReadOnlyList<ProvisioningEvent> events,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task HeartbeatAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
