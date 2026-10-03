using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using ElsaControl.Api.Telemetry;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ElsaControl.Api.Tests;

[Collection("Managed Azure Monitor export")]
public sealed class ManagedLifecycleAzureMonitorTelemetryTests : IDisposable
{
    private const string StatsbeatVariable = "APPLICATIONINSIGHTS_STATSBEAT_DISABLED";
    private readonly string? _originalStatsbeat = Environment.GetEnvironmentVariable(StatsbeatVariable);
    private readonly string? _originalResourceAttributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");

    public ManagedLifecycleAzureMonitorTelemetryTests()
    {
        Environment.SetEnvironmentVariable(StatsbeatVariable, "true");
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "customer.name=do-not-export-customer,secret=do-not-export-secret");
    }

    public void Dispose()
    {
        ManagedLifecycleTelemetry.ConfigureAlertEnvironment(null);
        Environment.SetEnvironmentVariable(StatsbeatVariable, _originalStatsbeat);
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", _originalResourceAttributes);
    }

    [Fact]
    public void Disposal_shuts_down_once_without_an_additional_flush_budget()
    {
        var processor = new RecordingShutdownProcessor();
        var provider = Sdk.CreateTracerProviderBuilder().AddProcessor(processor).Build();
        var sink = new ManagedLifecycleAzureMonitorTelemetrySink(null, provider);

        sink.Dispose();
        sink.Dispose();

        Assert.Equal(0, processor.FlushCount);
        Assert.Equal(1, processor.ShutdownCount);
        Assert.InRange(processor.ShutdownTimeout, 0, 5000);
        Assert.False(sink.ForceFlush());
    }

    [Fact]
    public void Cancelled_shutdown_does_not_start_a_drain_budget()
    {
        var processor = new RecordingShutdownProcessor();
        var provider = Sdk.CreateTracerProviderBuilder().AddProcessor(processor).Build();
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySink(null, provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        sink.Shutdown(cancellation.Token);

        Assert.Equal(1, processor.ShutdownCount);
        Assert.Equal(0, processor.ShutdownTimeout);
        Assert.Equal(0, processor.FlushCount);
        Assert.False(sink.ForceFlush());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Auxiliary_sdk_statistics_must_be_disabled_before_exporter_construction(string? value)
    {
        Environment.SetEnvironmentVariable(StatsbeatVariable, value);
        var credential = new RecordingCredential();
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString,
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ManagedLifecycleAzureMonitorTelemetrySinkFactory().Create(options, credential));

        Assert.Equal("Managed lifecycle Azure Monitor telemetry configuration is invalid (sdk_statistics_must_be_disabled).", exception.Message);
        Assert.Equal(0, credential.Requests);
    }

    [Fact]
    public void Normal_component_connection_metadata_is_accepted()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString +
                ";LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;ApplicationId=00000000-0000-0000-0000-000000000003",
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };

        options.Validate();
        Assert.Equal(ValidConnectionString, options.GetExporterConnectionString());
    }

    [Fact]
    public void Guid_metadata_accepts_hex_casing_and_normalizes_the_exporter_key()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = "InstrumentationKey=ABCDEF12-3456-7890-ABCD-EF1234567890;IngestionEndpoint=https://westeurope-1.in.applicationinsights.azure.com/;ApplicationId=FEDCBA98-7654-3210-FEDC-BA9876543210",
            ManagedIdentityClientId = "ABCDEF12-3456-7890-abcd-ef1234567890"
        };

        options.Validate();

        Assert.Equal("InstrumentationKey=abcdef12-3456-7890-abcd-ef1234567890;IngestionEndpoint=https://westeurope-1.in.applicationinsights.azure.com/", options.GetExporterConnectionString());
        Assert.IsType<ManagedIdentityCredential>(ManagedLifecycleAzureMonitorTelemetryCredentialFactory.Create(options));
    }

    [Fact]
    public void Enabled_configuration_without_identity_fails_closed_without_echoing_values()
    {
        var secretLikeConnectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://westeurope-1.in.applicationinsights.azure.com/";
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [$"{ManagedLifecycleAzureMonitorTelemetryOptions.ConfigurationSection}:Enabled"] = "true",
            [$"{ManagedLifecycleAzureMonitorTelemetryOptions.ConfigurationSection}:ConnectionString"] = secretLikeConnectionString
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddManagedLifecycleAzureMonitorTelemetry());

        Assert.Equal(
            "Managed lifecycle Azure Monitor telemetry configuration is invalid (managed_identity_client_id_required).",
            exception.Message);
        Assert.DoesNotContain(secretLikeConnectionString, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_configuration_does_not_register_a_managed_export_provider()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [$"{ManagedLifecycleAzureMonitorTelemetryOptions.ConfigurationSection}:Enabled"] = "false",
            [ManagedLifecycleAzureMonitorTelemetryOptions.EnvironmentConfigurationKey] = "staging"
        });

        builder.AddManagedLifecycleAzureMonitorTelemetry();

        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(ManagedLifecycleAzureMonitorTelemetryLifetime));
        Assert.Equal(ManagedLifecycleTelemetry.StagingEnvironment, ManagedLifecycleTelemetry.AlertEnvironment);
    }

    [Fact]
    public void Invalid_alert_environment_fails_closed_before_the_exporter_is_built()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [ManagedLifecycleAzureMonitorTelemetryOptions.EnabledConfigurationKey] = "false",
            [ManagedLifecycleAzureMonitorTelemetryOptions.EnvironmentConfigurationKey] = "lab"
        });

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.AddManagedLifecycleAzureMonitorTelemetry());

        Assert.Contains("staging or production", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_invalid_environment_fails_closed_with_a_stable_code()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString,
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002",
            Environment = "lab"
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Equal(
            "Managed lifecycle Azure Monitor telemetry configuration is invalid (environment_invalid).",
            exception.Message);
    }

    [Fact]
    public void Duplicate_sink_registration_is_rejected_before_building_a_second_provider()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [ManagedLifecycleAzureMonitorTelemetryOptions.EnabledConfigurationKey] = "true",
            [ManagedLifecycleAzureMonitorTelemetryOptions.ConnectionStringConfigurationKey] = ValidConnectionString,
            [ManagedLifecycleAzureMonitorTelemetryOptions.ManagedIdentityClientIdConfigurationKey] = "00000000-0000-0000-0000-000000000002"
        });
        builder.AddManagedLifecycleAzureMonitorTelemetry();

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddManagedLifecycleAzureMonitorTelemetry());

        Assert.Equal("Managed lifecycle Azure Monitor telemetry is already registered.", exception.Message);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ManagedLifecycleAzureMonitorTelemetryLifetime));
    }

    [Fact]
    public void Hostile_ingestion_locator_is_rejected_without_echoing_the_locator()
    {
        var hostileLocator = "https://operator:secret@evil.example.test/ingest?token=do-not-log#fragment";
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString =
                $"InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint={hostileLocator}",
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.Equal(
            "Managed lifecycle Azure Monitor telemetry configuration is invalid (ingestion_endpoint_invalid).",
            exception.Message);
        Assert.DoesNotContain(hostileLocator, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://westeurope-1.in.applicationinsights.azure.com/")]
    [InlineData("https://westeurope-1.in.applicationinsights.azure.com/?token=do-not-export")]
    [InlineData("https://westeurope-1.in.applicationinsights.azure.com/#do-not-export")]
    [InlineData("https://user@westeurope-1.in.applicationinsights.azure.com/")]
    [InlineData("https://westeurope-1.in.applicationinsights.azure.com:8443/")]
    [InlineData("https://westeurope-1.in.applicationinsights.azure.com/custom")]
    [InlineData("https://westeurope-1.in.applicationinsights.azure.com.evil.test/")]
    [InlineData("https://in.applicationinsights.azure.com/")]
    [InlineData("/relative")]
    public void Each_unsafe_ingestion_component_is_rejected_independently(string endpoint)
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=" + endpoint,
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Equal("Managed lifecycle Azure Monitor telemetry configuration is invalid (ingestion_endpoint_invalid).", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Credential_factory_uses_only_the_explicit_user_assigned_identity()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString,
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };

        var credential = ManagedLifecycleAzureMonitorTelemetryCredentialFactory.Create(options);

        Assert.IsType<ManagedIdentityCredential>(credential);
        Assert.NotEqual(nameof(DefaultAzureCredential), credential.GetType().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_deadline_preserves_caller_cancellation(bool asynchronous)
    {
        using var credential = new StalledCredential();
        var bounded = new ManagedLifecycleBoundedCredential(credential);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new TokenRequestContext(["https://monitor.azure.com/.default"]);

        if (asynchronous)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.GetTokenAsync(context, cancellation.Token).AsTask());
        else
            Assert.ThrowsAny<OperationCanceledException>(() => bounded.GetToken(context, cancellation.Token));

        Assert.True(credential.Cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Exporter_failure_leaves_the_alert_unacked_until_a_later_batch_succeeds()
    {
        var results = new Queue<ExportResult>([ExportResult.Failure, ExportResult.Success]);
        var ack = new RecoveryRequiredAlertExportAck();
        var exporter = new RecoveryRequiredAlertExportAckExporter(
            new ScriptedExporter(() => results.Dequeue()),
            ack);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(ManagedLifecycleTelemetry.ActivitySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new BatchActivityExportProcessor(exporter, maxQueueSize: 16, scheduledDelayMilliseconds: 60_000, exporterTimeoutMilliseconds: 5_000, maxExportBatchSize: 8))
            .Build();
        IRecoveryRequiredAlertTransportAck transport = new ManagedLifecycleAzureMonitorTelemetrySink(null, provider, ack);
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa8");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb8");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc8");
        var item = new RecoveryRequiredAlertDispatch(
            workspaceId,
            instanceId,
            operationId,
            1,
            null,
            ManagedLifecycleRecoveryRequiredAlert.ComputeDedupeIdentity(
                workspaceId, instanceId, operationId, 1));
        var sender = new ActivityRecoveryRequiredAlertSender(transport);

        Assert.False(sender.Send(item));
        Assert.True(sender.Send(item));
        Assert.Empty(results);
    }

    [Fact]
    public void Recovery_required_entry_is_exported_as_app_dependencies_with_the_activity_name()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString.Replace(
                "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000008"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002",
            Environment = ManagedLifecycleTelemetry.StagingEnvironment
        };
        using var handler = new RecordingIngestionHandler();
        using var client = new HttpClient(handler);
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory()
            .Create(options, new RecordingCredential(), new HttpClientTransport(client));
        ManagedLifecycleTelemetry.ConfigureAlertEnvironment(ManagedLifecycleTelemetry.StagingEnvironment);
        var workspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5");
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb5");
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-ccccccccccc5");

        ManagedLifecycleRecoveryRequiredAlert.RecordEntered(workspaceId, instanceId, operationId, 1);
        Assert.True(sink.ForceFlush());

        var dependencies = handler.Payloads.SelectMany(ReadEnvelopeItems)
            .Where(item => item.GetProperty("data").GetProperty("baseType").GetString() == "RemoteDependencyData")
            .Select(item => item.GetProperty("data").GetProperty("baseData"))
            .Where(data => data.GetProperty("name").GetString() ==
                           ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName)
            .ToArray();
        var dependency = Assert.Single(dependencies);
        Assert.Equal(ManagedLifecycleTelemetry.AppDependenciesTableName, "AppDependencies");
        var properties = dependency.GetProperty("properties");
        Assert.Equal(workspaceId.ToString("D"), properties.GetProperty("workspace.id").GetString());
        Assert.Equal(instanceId.ToString("D"), properties.GetProperty("instance.id").GetString());
        Assert.Equal(operationId.ToString("D"), properties.GetProperty("operation.id").GetString());
        Assert.Equal(
            ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired,
            properties.GetProperty("diagnostic_code").GetString());
        Assert.Equal(ManagedLifecycleTelemetry.StagingEnvironment, properties.GetProperty("environment").GetString());
        var dedupe = properties.GetProperty("dedupe_identity").GetString();
        Assert.Equal(64, dedupe!.Length);
        Assert.All(dedupe, character => Assert.True(char.IsAsciiHexDigit(character)));
    }

    [Fact]
    public void Actual_exporter_sends_only_managed_signals_with_the_six_metric_labels()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString,
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };
        using var handler = new RecordingIngestionHandler();
        using var client = new HttpClient(handler);
        var credential = new RecordingCredential();
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory()
            .Create(options, credential, new HttpClientTransport(client));
        using var unrelatedMeter = new Meter("unrelated.source");
        using var unrelatedSource = new ActivitySource("unrelated.source");
        unrelatedMeter.CreateCounter<long>("unrelated.counter").Add(1);
        using (unrelatedSource.StartActivity("unrelated.activity")) { }
        using var parent = new Activity("unrelated.parent")
            .AddBaggage("customer.name", "do-not-export-baggage")
            .Start();
        var instanceId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        using (var operation = ManagedLifecycleTelemetry.StartOperation(
                   ManagedLifecycleTelemetry.WorkerActivityName, ElsaInstanceOperationAction.Create,
                   ElsaDesiredLifecycle.Running, ElsaObservedLifecycle.Ready,
                   ElsaInstanceHealth.Healthy, ElsaInstanceOperationState.Running,
                   instanceId: instanceId))
        {
            operation.Complete("succeeded", ElsaDesiredLifecycle.Running,
                ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy,
                ElsaInstanceOperationState.Succeeded);
        }

        Assert.True(sink.ForceFlush());
        Assert.True(credential.Requests > 0);
        Assert.NotEmpty(handler.Payloads);
        Assert.All(handler.Hosts, host => Assert.Equal("westeurope-1.in.applicationinsights.azure.com", host));
        Assert.All(handler.AuthenticationSchemes, scheme => Assert.Equal("Bearer", scheme));
        var payload = string.Join("\n", handler.Payloads);
        Assert.DoesNotContain("unrelated.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("test-export-token", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-export-", payload, StringComparison.Ordinal);
        Assert.Contains(ManagedLifecycleTelemetry.WorkerActivityName, payload, StringComparison.Ordinal);
        Assert.Contains(instanceId.ToString("D"), payload, StringComparison.Ordinal);
        Assert.Contains(ManagedLifecycleTelemetry.CompletionCounterName, payload, StringComparison.Ordinal);
        var metrics = handler.Payloads.SelectMany(ReadEnvelopeItems)
            .Where(item => item.GetProperty("data").GetProperty("baseType").GetString() == "MetricData")
            .ToArray();
        Assert.NotEmpty(metrics);
        foreach (var metric in metrics)
        {
            var data = metric.GetProperty("data").GetProperty("baseData");
            var properties = data.GetProperty("properties");
            if (data.GetProperty("metrics")[0].GetProperty("name").GetString() == "_OTELRESOURCE_")
            {
                // The SDK emits a separate resource descriptor, not extra lifecycle labels.
                Assert.Equal(new[] { "service.instance.id", "service.name", "service.namespace", "service.version" },
                    properties.EnumerateObject().Select(property => property.Name).Order().ToArray());
                Assert.Equal("managed-lifecycle", properties.GetProperty("service.instance.id").GetString());
                Assert.Equal("elsa-control-api", properties.GetProperty("service.name").GetString());
                Assert.Equal("elsa-control", properties.GetProperty("service.namespace").GetString());
                Assert.Equal("1", properties.GetProperty("service.version").GetString());
                continue;
            }
            Assert.Equal(new[] { "action", "desired_lifecycle", "health", "observed_lifecycle", "operation_state", "outcome" },
                properties.EnumerateObject().Select(property => property.Name).Order().ToArray());
        }
    }

    [Fact]
    public async Task Actual_exporter_cancels_stalled_ingestion_headers()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            // The pinned SDK caches transmitters by connection string; isolate this scenario.
            ConnectionString = ValidConnectionString.Replace(
                "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000005"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };
        using var handler = new StalledIngestionHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory()
            .Create(options, new RecordingCredential(), new HttpClientTransport(client));
        using var meter = new Meter(ManagedLifecycleTelemetry.MeterName);
        meter.CreateCounter<long>(ManagedLifecycleTelemetry.CompletionCounterName).Add(1);

        var flush = Task.Run(sink.ForceFlush);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Repeated_exports_send_only_new_operation_counts()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString.Replace(
                "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000007"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };
        using var handler = new RecordingIngestionHandler();
        using var client = new HttpClient(handler);
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory()
            .Create(options, new RecordingCredential(), new HttpClientTransport(client));
        using var meter = new Meter(ManagedLifecycleTelemetry.MeterName);
        var counter = meter.CreateCounter<long>(ManagedLifecycleTelemetry.CompletionCounterName);

        counter.Add(3);
        Assert.True(sink.ForceFlush());
        counter.Add(2);
        Assert.True(sink.ForceFlush());

        var values = handler.Payloads.SelectMany(ReadEnvelopeItems)
            .Where(item => item.GetProperty("data").GetProperty("baseType").GetString() == "MetricData")
            .SelectMany(item => item.GetProperty("data").GetProperty("baseData").GetProperty("metrics").EnumerateArray())
            .Where(point => point.GetProperty("name").GetString() == ManagedLifecycleTelemetry.CompletionCounterName)
            .Select(point => point.GetProperty("value").GetDouble())
            .ToArray();
        Assert.Equal(new double[] { 3, 2 }, values);
    }

    [Fact]
    public async Task Actual_exporter_cancels_stalled_token_acquisition_before_ingestion()
    {
        var options = new ManagedLifecycleAzureMonitorTelemetryOptions
        {
            Enabled = true,
            ConnectionString = ValidConnectionString.Replace(
                "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000006"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000002"
        };
        using var credential = new StalledCredential();
        using var handler = new RecordingIngestionHandler();
        using var client = new HttpClient(handler);
        using var sink = new ManagedLifecycleAzureMonitorTelemetrySinkFactory()
            .Create(options, credential, new HttpClientTransport(client));
        using var meter = new Meter(ManagedLifecycleTelemetry.MeterName);
        meter.CreateCounter<long>(ManagedLifecycleTelemetry.CompletionCounterName).Add(1);

        var flush = Task.Run(sink.ForceFlush);
        try
        {
            await credential.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await credential.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(handler.Hosts);
            Assert.Equal(1, credential.Requests);
        }
        finally
        {
            credential.Abort();
        }
    }

    private const string ValidConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://westeurope-1.in.applicationinsights.azure.com/";

    private static HostApplicationBuilder CreateBuilder(IReadOnlyDictionary<string, string?> values)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static IEnumerable<JsonElement> ReadEnvelopeItems(string payload)
    {
        // The real Azure ingestion exporter writes newline-delimited JSON.
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public int Requests { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests++;
            return new AccessToken("test-export-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ScriptedExporter(Func<ExportResult> next) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch) => next();
    }

    private sealed class RecordingShutdownProcessor : BaseProcessor<Activity>
    {
        public int FlushCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public int ShutdownTimeout { get; private set; }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            FlushCount++;
            return true;
        }

        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            ShutdownCount++;
            ShutdownTimeout = timeoutMilliseconds;
            return true;
        }
    }

    private sealed class StalledCredential : TokenCredential, IDisposable
    {
        private readonly CancellationTokenSource _cleanup = new();
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cleanup.Token);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
                throw new InvalidOperationException("Unexpected completion of stalled credential.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public void Abort() => _cleanup.Cancel();
        public void Dispose()
        {
            Abort();
            _cleanup.Dispose();
        }
    }

    private sealed class RecordingIngestionHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Payloads { get; } = new();
        public ConcurrentQueue<string> Hosts { get; } = new();
        public ConcurrentQueue<string?> AuthenticationSchemes { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Enqueue(request.RequestUri!.Host);
            AuthenticationSchemes.Enqueue(request.Headers.Authorization?.Scheme);
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var stream = new MemoryStream(bytes);
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                Payloads.Enqueue(await reader.ReadToEndAsync(cancellationToken));
            }
            else Payloads.Enqueue(Encoding.UTF8.GetString(bytes));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class StalledIngestionHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unexpected completion of stalled transport.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}

[CollectionDefinition("Managed Azure Monitor export", DisableParallelization = true)]
public sealed class ManagedAzureMonitorExportCollection;
