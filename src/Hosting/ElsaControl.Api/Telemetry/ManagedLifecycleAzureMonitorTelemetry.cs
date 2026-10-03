using System.Diagnostics;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ElsaControl.Api.Telemetry;

/// <summary>
/// Registers the opt-in managed lifecycle Azure Monitor sink. This is deliberately owned by
/// the API host so Azure Monitor does not become a dependency of the provider-neutral service
/// defaults or of other hosts.
/// </summary>
public static class ManagedLifecycleAzureMonitorTelemetryExtensions
{
    public static TBuilder AddManagedLifecycleAzureMonitorTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = ManagedLifecycleAzureMonitorTelemetryOptions.Read(builder.Configuration);
        ManagedLifecycleTelemetry.ConfigureAlertEnvironment(options.Environment);
        if (!options.Enabled)
            return builder;

        options.Validate();
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(ManagedLifecycleAzureMonitorTelemetryLifetime)))
            throw new InvalidOperationException("Managed lifecycle Azure Monitor telemetry is already registered.");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ManagedLifecycleAzureMonitorTelemetrySinkFactory>();
        builder.Services.AddSingleton(services =>
            services.GetRequiredService<ManagedLifecycleAzureMonitorTelemetrySinkFactory>().Create(options));
        builder.Services.AddSingleton<IRecoveryRequiredAlertTransportAck>(services =>
            services.GetRequiredService<ManagedLifecycleAzureMonitorTelemetrySink>());
        builder.Services.AddSingleton<ManagedLifecycleAzureMonitorTelemetryLifetime>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<ManagedLifecycleAzureMonitorTelemetryLifetime>());
        return builder;
    }
}

/// <summary>Configuration for the operator-controlled managed lifecycle sink.</summary>
public sealed class ManagedLifecycleAzureMonitorTelemetryOptions
{
    public const string ConfigurationSection = "ManagedLifecycleTelemetry:AzureMonitor";
    public const string EnabledConfigurationKey = ConfigurationSection + ":Enabled";
    public const string ConnectionStringConfigurationKey = ConfigurationSection + ":ConnectionString";
    public const string ManagedIdentityClientIdConfigurationKey = ConfigurationSection + ":ManagedIdentityClientId";
    public const string EnvironmentConfigurationKey = ConfigurationSection + ":Environment";

    // These bounds are code-owned deliberately. An operator can select the sink and its
    // metadata, but cannot turn it into an unbounded transport or queue through configuration.
    // Export, flush/await, and the lease send window share one budget so
    // export ≤ wait < lease.
    internal const int ExportIntervalMilliseconds = 60_000;
    internal static readonly int ExportTimeoutMilliseconds =
        (int)RecoveryRequiredAlertBackoff.ExportTimeout.TotalMilliseconds;
    internal static readonly int FlushTimeoutMilliseconds =
        (int)RecoveryRequiredAlertBackoff.WaitTimeout.TotalMilliseconds;
    internal const int TraceMaxQueueSize = 512;
    internal const int TraceMaxBatchSize = 128;
    internal const string CloudRoleName = "elsa-control-api";
    internal const string CloudRoleInstance = "managed-lifecycle";
    internal const string ServiceNamespace = "elsa-control";
    internal const string ServiceVersion = "1";

    public bool Enabled { get; init; }
    public string? ConnectionString { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public string? Environment { get; init; }

    internal static ManagedLifecycleAzureMonitorTelemetryOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigurationSection);
        var enabledValue = section[nameof(Enabled)];
        if (string.IsNullOrWhiteSpace(enabledValue))
        {
            return new ManagedLifecycleAzureMonitorTelemetryOptions
            {
                Enabled = false,
                Environment = ReadEnvironment(section)
            };
        }

        if (!bool.TryParse(enabledValue.Trim(), out var enabled))
            throw Invalid("enabled_invalid");

        return new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = enabled,
            ConnectionString = section[nameof(ConnectionString)],
            ManagedIdentityClientId = section[nameof(ManagedIdentityClientId)],
            Environment = ReadEnvironment(section)
        };
    }

    private static string? ReadEnvironment(IConfiguration section)
    {
        var environment = section[nameof(Environment)]?.Trim();
        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }

    public void Validate()
    {
        if (Environment is { Length: > 0 } &&
            Environment is not (ManagedLifecycleTelemetry.StagingEnvironment or ManagedLifecycleTelemetry.ProductionEnvironment))
            throw Invalid("environment_invalid");
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw Invalid("connection_string_required");
        if (ConnectionString.Length > 4096 || ConnectionString.Any(char.IsControl))
            throw Invalid("connection_string_invalid");
        if (!Guid.TryParseExact(ManagedIdentityClientId, "D", out var clientId) ||
            clientId == Guid.Empty ||
            !string.Equals(ManagedIdentityClientId, clientId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw Invalid("managed_identity_client_id_required");

        var values = ParseConnectionString(ConnectionString);
        if (!values.TryGetValue("InstrumentationKey", out var instrumentationKey) ||
            !Guid.TryParseExact(instrumentationKey, "D", out var key) ||
            key == Guid.Empty ||
            !string.Equals(instrumentationKey, key.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw Invalid("instrumentation_key_invalid");

        if (!values.TryGetValue("IngestionEndpoint", out var ingestionEndpoint) ||
            !IsSafeIngestionEndpoint(ingestionEndpoint))
            throw Invalid("ingestion_endpoint_invalid");

        // Azure's component metadata includes these fields even when live metrics
        // are disabled. Validate them, but never pass them to the exporter.
        if (values.TryGetValue("LiveEndpoint", out var liveEndpoint) &&
            (!IsSafeHttpsOrigin(liveEndpoint, out var liveUri) ||
             !IsAzureMonitorHost(liveUri!.DnsSafeHost, ".livediagnostics.monitor.azure.com")))
            throw Invalid("connection_string_invalid");
        if (values.TryGetValue("ApplicationId", out var applicationId) &&
            (!Guid.TryParseExact(applicationId, "D", out var parsedApplicationId) || parsedApplicationId == Guid.Empty))
            throw Invalid("connection_string_invalid");
    }

    internal string GetExporterConnectionString()
    {
        Validate();
        var values = ParseConnectionString(ConnectionString!);
        var key = Guid.ParseExact(values["InstrumentationKey"], "D").ToString("D");
        return $"InstrumentationKey={key};IngestionEndpoint={values["IngestionEndpoint"]}";
    }

    private static Dictionary<string, string> ParseConnectionString(string value)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
                throw Invalid("connection_string_invalid");

            var key = segment[..separator].Trim();
            var segmentValue = segment[(separator + 1)..].Trim();
            if (key.Length == 0 || segmentValue.Length == 0 ||
                !IsKnownKey(key) || !values.TryAdd(key, segmentValue))
                throw Invalid("connection_string_invalid");
        }

        return values;
    }

    private static bool IsKnownKey(string key) => key.Equals("InstrumentationKey", StringComparison.OrdinalIgnoreCase) ||
                                                   key.Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase) ||
                                                   key.Equals("LiveEndpoint", StringComparison.OrdinalIgnoreCase) ||
                                                   key.Equals("ApplicationId", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeIngestionEndpoint(string value)
    {
        if (!IsSafeHttpsOrigin(value, out var uri))
            return false;

        var host = uri!.DnsSafeHost.ToLowerInvariant();
        return host == "dc.services.visualstudio.com" ||
               IsAzureMonitorHost(host, ".in.applicationinsights.azure.com");
    }

    private static bool IsSafeHttpsOrigin(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && uri.Port == 443 && uri.AbsolutePath == "/";

    private static bool IsAzureMonitorHost(string host, string suffix) =>
        host.EndsWith(suffix, StringComparison.Ordinal) &&
        host.Length > suffix.Length;

    private static InvalidOperationException Invalid(string code) =>
        new($"Managed lifecycle Azure Monitor telemetry configuration is invalid ({code}).");
}

internal static class ManagedLifecycleAzureMonitorTelemetryCredentialFactory
{
    internal static TokenCredential Create(ManagedLifecycleAzureMonitorTelemetryOptions options)
    {
        options.Validate();
        return new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(Guid.ParseExact(options.ManagedIdentityClientId!, "D").ToString("D")));
    }
}

// The Azure authentication policy runs before the ingestion network-timeout
// policy. Give token acquisition its own deadline without adding credential
// fallback or changing the explicitly selected managed identity.
internal sealed class ManagedLifecycleBoundedCredential(TokenCredential inner) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ManagedLifecycleAzureMonitorTelemetryOptions.ExportTimeoutMilliseconds);
        try
        {
            return inner.GetToken(requestContext, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationFailedException("Managed lifecycle telemetry token acquisition timed out.");
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ManagedLifecycleAzureMonitorTelemetryOptions.ExportTimeoutMilliseconds);
        try
        {
            return await inner.GetTokenAsync(requestContext, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Azure's token cache retries cancellation if its caller was not
            // cancelled. A local deadline is a terminal authentication failure.
            throw new AuthenticationFailedException("Managed lifecycle telemetry token acquisition timed out.");
        }
    }
}

internal sealed class ManagedLifecycleAzureMonitorTelemetrySinkFactory
{
    public ManagedLifecycleAzureMonitorTelemetrySink Create(ManagedLifecycleAzureMonitorTelemetryOptions options)
        => Create(options, ManagedLifecycleAzureMonitorTelemetryCredentialFactory.Create(options));

    internal ManagedLifecycleAzureMonitorTelemetrySink Create(
        ManagedLifecycleAzureMonitorTelemetryOptions options,
        TokenCredential credential,
        HttpPipelineTransport? transport = null)
    {
        options.Validate();
        // The pinned SDK exposes this control only through the process environment.
        // Require the operator's explicit opt-out before constructing any exporter;
        // IConfiguration alone cannot disable the SDK's auxiliary transmitter.
        if (!string.Equals(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED"),
                "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Managed lifecycle Azure Monitor telemetry configuration is invalid (sdk_statistics_must_be_disabled).");
        var metricExporter = default(AzureMonitorMetricExporter);
        var traceExporter = default(AzureMonitorTraceExporter);
        MeterProvider? meterProvider = null;
        TracerProvider? tracerProvider = null;
        TracerProvider? traceResourceHost = null;
        try
        {
            credential = new ManagedLifecycleBoundedCredential(credential);
            metricExporter = new AzureMonitorMetricExporter(CreateExporterOptions(options, credential, transport));
            traceExporter = new AzureMonitorTraceExporter(CreateExporterOptions(options, credential, transport));
            // Register the inner Azure exporter with the SDK so SetParentProvider
            // assigns the same resource the wrapper's provider uses. No reflection.
            // This host never adds a source, so it does not export.
            traceResourceHost = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(CreateResourceBuilder())
                .AddProcessor(new SimpleActivityExportProcessor(traceExporter))
                .Build();
            meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(CreateResourceBuilder())
                .AddMeter(ManagedLifecycleTelemetry.MeterName)
                .AddReader(new PeriodicExportingMetricReader(
                    metricExporter,
                    ManagedLifecycleAzureMonitorTelemetryOptions.ExportIntervalMilliseconds,
                    ManagedLifecycleAzureMonitorTelemetryOptions.ExportTimeoutMilliseconds)
                {
                    TemporalityPreference = MetricReaderTemporalityPreference.Delta
                })
                .Build();
            var exportAck = new RecoveryRequiredAlertExportAck();
            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(CreateResourceBuilder())
                .AddSource(ManagedLifecycleTelemetry.ActivitySourceName)
                // Lifecycle evidence must survive an unsampled inbound/request parent.
                // This provider subscribes only to the dedicated lifecycle source.
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(new BatchActivityExportProcessor(
                    new RecoveryRequiredAlertExportAckExporter(traceExporter, exportAck, ownsInner: false),
                    ManagedLifecycleAzureMonitorTelemetryOptions.TraceMaxQueueSize,
                    ManagedLifecycleAzureMonitorTelemetryOptions.ExportIntervalMilliseconds,
                    ManagedLifecycleAzureMonitorTelemetryOptions.ExportTimeoutMilliseconds,
                    ManagedLifecycleAzureMonitorTelemetryOptions.TraceMaxBatchSize))
                .Build();
            return new ManagedLifecycleAzureMonitorTelemetrySink(
                meterProvider, tracerProvider, exportAck, traceResourceHost);
        }
        catch (Exception)
        {
            if (meterProvider is not null) meterProvider.Dispose();
            else metricExporter?.Dispose();
            if (tracerProvider is not null) tracerProvider.Dispose();
            if (traceResourceHost is not null) traceResourceHost.Dispose();
            else traceExporter?.Dispose();
            // Exporter constructors can include configuration details in their exceptions. The
            // process must fail closed with a stable, value-free diagnostic instead.
            throw new InvalidOperationException(
                "Managed lifecycle Azure Monitor telemetry configuration is invalid (exporter_initialization_failed).");
        }
    }

    private static AzureMonitorExporterOptions CreateExporterOptions(
        ManagedLifecycleAzureMonitorTelemetryOptions options,
        TokenCredential credential,
        HttpPipelineTransport? transport)
    {
        var exporterOptions = new AzureMonitorExporterOptions
        {
            ConnectionString = options.GetExporterConnectionString(),
            Credential = credential,
            SamplingRatio = 1.0f,
            TracesPerSecond = null,
            DisableOfflineStorage = true,
            EnableLiveMetrics = false,
            EnableStandardMetrics = false,
            EnablePerformanceCounters = false
        };
        if (transport is not null)
            exporterOptions.Transport = transport;
        exporterOptions.Retry.MaxRetries = 0;
        exporterOptions.Retry.NetworkTimeout = TimeSpan.FromMilliseconds(
            ManagedLifecycleAzureMonitorTelemetryOptions.ExportTimeoutMilliseconds);
        return exporterOptions;
    }

    private static ResourceBuilder CreateResourceBuilder() =>
        ResourceBuilder.CreateEmpty().AddService(
            serviceName: ManagedLifecycleAzureMonitorTelemetryOptions.CloudRoleName,
            serviceNamespace: ManagedLifecycleAzureMonitorTelemetryOptions.ServiceNamespace,
            serviceInstanceId: ManagedLifecycleAzureMonitorTelemetryOptions.CloudRoleInstance,
            serviceVersion: ManagedLifecycleAzureMonitorTelemetryOptions.ServiceVersion);
}

internal sealed class ManagedLifecycleAzureMonitorTelemetryLifetime(
    ManagedLifecycleAzureMonitorTelemetrySink sink) : IHostedService, IDisposable
{
    private readonly ManagedLifecycleAzureMonitorTelemetrySink _sink = sink;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sink.Shutdown(cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose() => _sink.Dispose();
}

internal sealed class ManagedLifecycleAzureMonitorTelemetrySink : IDisposable, IRecoveryRequiredAlertTransportAck
{
    private readonly MeterProvider? _meterProvider;
    private readonly TracerProvider? _tracerProvider;
    private readonly RecoveryRequiredAlertExportAck? _exportAck;
    private readonly IDisposable? _traceResourceHost;
    private int _disposed;

    internal ManagedLifecycleAzureMonitorTelemetrySink(
        MeterProvider? meterProvider,
        TracerProvider? tracerProvider,
        RecoveryRequiredAlertExportAck? exportAck = null,
        IDisposable? traceResourceHost = null)
    {
        _meterProvider = meterProvider;
        _tracerProvider = tracerProvider;
        _exportAck = exportAck;
        _traceResourceHost = traceResourceHost;
    }

    void IRecoveryRequiredAlertTransportAck.Watch(string dedupeIdentity) =>
        _exportAck?.Watch(dedupeIdentity);

    bool IRecoveryRequiredAlertTransportAck.TryAcknowledge(string dedupeIdentity)
    {
        if (string.IsNullOrWhiteSpace(dedupeIdentity) || _exportAck is null)
            return false;

        var deadline = Stopwatch.GetTimestamp() +
                       (long)(ManagedLifecycleAzureMonitorTelemetryOptions.FlushTimeoutMilliseconds / 1000.0 * Stopwatch.Frequency);
        try
        {
            ForceFlush(deadline);
        }
        catch
        {
            return false;
        }

        return _exportAck.WaitForSuccess(dedupeIdentity, RemainingTime(deadline));
    }

    internal bool ForceFlush()
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(ManagedLifecycleAzureMonitorTelemetryOptions.FlushTimeoutMilliseconds / 1000.0 * Stopwatch.Frequency);
        return ForceFlush(deadline);
    }

    private bool ForceFlush(long deadline)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        var traceFlushed = _tracerProvider?.ForceFlush(RemainingMilliseconds(deadline)) ?? true;
        var meterFlushed = _meterProvider?.ForceFlush(RemainingMilliseconds(deadline)) ?? true;
        return traceFlushed && meterFlushed;
    }

    public void Dispose() => Shutdown(CancellationToken.None);

    internal void Shutdown(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var deadline = Stopwatch.GetTimestamp() +
                       (long)(ManagedLifecycleAzureMonitorTelemetryOptions.FlushTimeoutMilliseconds / 1000.0 * Stopwatch.Frequency);
        // Shutdown is one-shot. Dispose then releases resources without starting
        // another provider grace period after a separate ForceFlush budget.
        // A cancelled host must not start another drain wait. The SDK's synchronous
        // calls cannot be interrupted, so recheck cancellation between providers.
        _tracerProvider?.Shutdown(cancellationToken.IsCancellationRequested ? 0 : RemainingMilliseconds(deadline));
        _meterProvider?.Shutdown(cancellationToken.IsCancellationRequested ? 0 : RemainingMilliseconds(deadline));
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
        _traceResourceHost?.Dispose();
    }

    private static int RemainingMilliseconds(long deadline)
    {
        var remaining = RemainingTime(deadline);
        return (int)Math.Clamp(remaining.TotalMilliseconds, 1, ManagedLifecycleAzureMonitorTelemetryOptions.FlushTimeoutMilliseconds);
    }

    private static TimeSpan RemainingTime(long deadline)
    {
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
