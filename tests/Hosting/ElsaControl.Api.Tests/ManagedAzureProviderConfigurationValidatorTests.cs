using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class ManagedAzureProviderConfigurationValidatorTests
{
    [Fact]
    public async Task Fully_disabled_pipeline_is_valid()
    {
        var validator = Validator(false, false, enabledProvider: false);
        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fully_enabled_pipeline_is_valid()
    {
        var validator = Validator(true, true, enabledProvider: true);
        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enabled_pipeline_requires_a_successful_authority_preflight()
    {
        var validator = Validator(true, true, enabledProvider: true, configurePreflight: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));

        Assert.Contains("authority preflight", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_authority_preflight_fails_startup_with_only_its_safe_code()
    {
        var validator = Validator(true, true, enabledProvider: true, authorityPreflight: new FailingPreflight());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));

        Assert.Contains("azure.preflight.rbac-insufficient", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Partial_pipeline_configuration_fails_startup(
        bool lifecycle,
        bool worker,
        bool provider)
    {
        var validator = Validator(lifecycle, worker, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(101, 5)]
    [InlineData(10, 0)]
    [InlineData(10, 61)]
    public async Task Enabled_pipeline_rejects_unsafe_worker_bounds(int batchSize, int pollSeconds)
    {
        var validator = Validator(true, true, enabledProvider: true, batchSize, pollSeconds);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Health_monitor_is_valid_with_the_full_pipeline()
    {
        var validator = Validator(true, true, enabledProvider: true, healthMonitor: new() { Enabled = true });
        await validator.StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Health_monitor_without_the_full_pipeline_fails_startup(bool lifecycle, bool worker, bool provider)
    {
        var validator = Validator(lifecycle, worker, provider, healthMonitor: new() { Enabled = true });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("health monitor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_health_monitor_with_out_of_bounds_options_fails_startup()
    {
        var validator = Validator(true, true, enabledProvider: true,
            healthMonitor: new() { Enabled = true, Interval = TimeSpan.FromSeconds(5) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => validator.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_health_monitor_is_not_validated()
    {
        var validator = Validator(false, false, enabledProvider: false,
            healthMonitor: new() { Enabled = false, Interval = TimeSpan.FromSeconds(5) });
        await validator.StartAsync(CancellationToken.None);
    }

    private static ManagedAzureProviderConfigurationValidator Validator(
        bool lifecycle,
        bool worker,
        bool enabledProvider,
        int batchSize = 10,
        int pollSeconds = 5,
        IAzureProviderAuthorityPreflight? authorityPreflight = null,
        bool configurePreflight = true,
        ElsaInstanceHealthMonitorOptions? healthMonitor = null) => new(
        Options.Create(new ElsaInstanceLifecycleWorkerOptions { Enabled = lifecycle }),
        Options.Create(new AzureProviderOperationOptions
        {
            WorkerEnabled = worker,
            BatchSize = batchSize,
            PollInterval = TimeSpan.FromSeconds(pollSeconds)
        }),
        enabledProvider
            ? new AzureElsaInstanceProviderOptions
            {
                Enabled = true,
                TemplateFingerprint = new string('a', 64),
                ProviderScopeFingerprint = new string('b', 64),
                SubscriptionId = "11111111-1111-1111-1111-111111111111",
                ResourceGroupNamePrefix = "rg-elsa"
            }
            : null,
        authorityPreflight ?? (configurePreflight && enabledProvider ? new PassingPreflight() : null),
        Options.Create(healthMonitor ?? new ElsaInstanceHealthMonitorOptions()));

    private sealed class PassingPreflight : IAzureProviderAuthorityPreflight
    {
        public Task<AzureProviderAuthorityPreflightResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureProviderAuthorityPreflightResult(true, "azure.preflight.succeeded", "ok"));
    }

    private sealed class FailingPreflight : IAzureProviderAuthorityPreflight
    {
        public Task<AzureProviderAuthorityPreflightResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureProviderAuthorityPreflightResult(false, "azure.preflight.rbac-insufficient", "value-free"));
    }
}
