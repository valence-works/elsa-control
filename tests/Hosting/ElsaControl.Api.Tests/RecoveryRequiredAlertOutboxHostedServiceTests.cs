using System.Diagnostics;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class RecoveryRequiredAlertOutboxHostedServiceTests
{
    [Fact]
    public async Task Nudge_wakes_the_loop_before_the_poll_interval()
    {
        var dispatcher = new RecordingDispatcher();
        var signal = new RecoveryRequiredAlertDispatchSignal();
        await using var services = new ServiceCollection()
            .AddSingleton<IRecoveryRequiredAlertOutboxDispatcher>(dispatcher)
            .BuildServiceProvider();
        var hosted = new RecoveryRequiredAlertOutboxHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ElsaInstanceLifecycleWorkerOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromMinutes(5)
            }),
            new RecordingLogger(),
            signal);

        using var stopping = new CancellationTokenSource();
        await hosted.StartAsync(stopping.Token);
        try
        {
            await dispatcher.WaitForCallsAsync(1, TimeSpan.FromSeconds(2));
            dispatcher.Reset();
            var started = Stopwatch.StartNew();
            signal.Notify();
            await dispatcher.WaitForCallsAsync(1, TimeSpan.FromSeconds(2));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            stopping.Cancel();
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_dispatch_does_not_stop_the_loop()
    {
        var dispatcher = new RecordingDispatcher { FailOnce = true };
        var logger = new RecordingLogger();
        await using var services = new ServiceCollection()
            .AddSingleton<IRecoveryRequiredAlertOutboxDispatcher>(dispatcher)
            .BuildServiceProvider();
        var hosted = new RecoveryRequiredAlertOutboxHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ElsaInstanceLifecycleWorkerOptions
            {
                Enabled = true,
                PollInterval = TimeSpan.FromSeconds(1)
            }),
            logger);

        using var stopping = new CancellationTokenSource();
        await hosted.StartAsync(stopping.Token);
        try
        {
            await dispatcher.WaitForCallsAsync(2, TimeSpan.FromSeconds(5));
            var error = Assert.Single(logger.Messages, message => message.Level == LogLevel.Error);
            Assert.Equal("RecoveryRequired alert outbox dispatch failed.", error.Message);
        }
        finally
        {
            stopping.Cancel();
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingDispatcher : IRecoveryRequiredAlertOutboxDispatcher
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public bool FailOnce { get; set; }

        public Task<int> DispatchPendingAsync(int limit = 32, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (FailOnce && call == 1)
                throw new InvalidOperationException("The RecoveryRequired alert dispatcher failed.");
            if (call >= 1)
                _gate.TrySetResult();
            return Task.FromResult(0);
        }

        public async Task WaitForCallsAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _calls) < expected)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"Dispatcher calls stayed at {Volatile.Read(ref _calls)}.");
                await Task.Delay(20);
            }
        }

        public void Reset() => Interlocked.Exchange(ref _calls, 0);
    }

    private sealed class RecordingLogger : ILogger<RecoveryRequiredAlertOutboxHostedService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception), exception));
    }
}
