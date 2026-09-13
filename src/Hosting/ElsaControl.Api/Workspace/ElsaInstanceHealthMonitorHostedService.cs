using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

/// <summary>
/// Composes the periodic Ready-instance health monitor (#394). Its options are always bound so
/// the startup validator can reject a half-enabled composition; the hosted service runs only when
/// its switch is on and the managed provider pipeline that supplies the probe port is composed.
/// </summary>
internal static class ElsaInstanceHealthMonitorComposition
{
    public static bool AddHealthMonitor(
        IServiceCollection services,
        IConfiguration configuration,
        bool providerPipelineComposed,
        bool runWorkers)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ElsaInstanceHealthMonitorOptions.ConfigurationSection);
        services.Configure<ElsaInstanceHealthMonitorOptions>(section);
        if (!section.GetValue<bool>(nameof(ElsaInstanceHealthMonitorOptions.Enabled)) || !providerPipelineComposed || !runWorkers)
            return false;

        services.AddSingleton(provider => new ElsaInstanceHealthMonitor(
            provider.GetRequiredService<IOptions<ElsaInstanceHealthMonitorOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<ElsaInstanceHealthMonitorHostedService>();
        return true;
    }
}

/// <summary>
/// Runs one <see cref="ElsaInstanceHealthMonitor"/> cycle per interval. Scheduling, hysteresis and
/// persistence live in the monitor; this service owns the timer, one DI scope per evaluation, and
/// value-free logging: opaque IDs, enum states, safe codes and exception type names only.
/// </summary>
public sealed class ElsaInstanceHealthMonitorHostedService(
    IServiceScopeFactory scopeFactory,
    ElsaInstanceHealthMonitor monitor,
    IOptions<ElsaInstanceHealthMonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<ElsaInstanceHealthMonitorHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval, timeProvider);
        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Managed Elsa instance health monitor cycle failed ({ExceptionType}).",
                    exception.GetType().Name);
            }
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    /// <summary>Runs and logs one cycle; the deterministic seam for tests.</summary>
    internal async Task<IReadOnlyList<ElsaInstanceHealthEvaluation>> RunCycleAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var evaluations = await monitor.RunCycleAsync(
            scope.ServiceProvider.GetRequiredService<IElsaInstanceHealthMonitorStore>(),
            OpenEvaluationScope,
            stoppingToken);
        foreach (var evaluation in evaluations)
            Log(evaluation);
        return evaluations;
    }

    private ElsaInstanceHealthMonitorScope OpenEvaluationScope()
    {
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            return new(
                scope.ServiceProvider.GetRequiredService<IElsaInstanceHealthMonitorStore>(),
                new ScopedProbe(scope.ServiceProvider),
                scope);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private void Log(ElsaInstanceHealthEvaluation evaluation)
    {
        var code = ManagedLifecycleOperationalHealthDiagnosticCodes.IsSafe(evaluation.DiagnosticCode)
            ? evaluation.DiagnosticCode
            : ManagedLifecycleOperationalHealthDiagnosticCodes.Unknown;
        if (evaluation.Outcome == ElsaInstanceHealthMonitorOutcome.Transitioned)
            logger.LogInformation(
                "Managed Elsa instance health changed for workspace {WorkspaceId}, instance {InstanceId}: {PriorHealth} to {Health}, diagnostic {DiagnosticCode}.",
                evaluation.WorkspaceId, evaluation.InstanceId, evaluation.PriorHealth, evaluation.Health, code);
        else if (evaluation.Outcome == ElsaInstanceHealthMonitorOutcome.Conflict)
            logger.LogInformation(
                "Managed Elsa instance health change for workspace {WorkspaceId}, instance {InstanceId} was dropped because the instance changed concurrently.",
                evaluation.WorkspaceId, evaluation.InstanceId);

        if (evaluation.ExceptionType is { } exceptionType)
            logger.LogWarning(
                "Managed Elsa instance health evaluation for workspace {WorkspaceId}, instance {InstanceId} failed ({ExceptionType}), diagnostic {DiagnosticCode}.",
                evaluation.WorkspaceId, evaluation.InstanceId, exceptionType, code);
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Composes the provider probe only when an instance is actually probed: a skipped instance
    /// composes no provider, and a probe that cannot be composed is an Unknown probe failure rather
    /// than an evaluation error that would leave a stale health in place.
    /// </summary>
    private sealed class ScopedProbe(IServiceProvider services) : IElsaInstanceProviderHealthProbePort
    {
        public Task<ElsaInstanceHealthProbeResult> ProbeAsync(
            ElsaInstanceHealthProbeRequest request,
            CancellationToken cancellationToken = default) =>
            services.GetRequiredService<IElsaInstanceProviderHealthProbePort>().ProbeAsync(request, cancellationToken);
    }
}
