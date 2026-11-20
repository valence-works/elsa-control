using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class PartnerApiConfigurationValidatorTests
{
    [Fact]
    public void Unconfigured_Production_defaults_to_live()
    {
        var options = new PartnerApiOptions();
        options.ApplyEnvironmentDefault(new StubHostEnvironment(Environments.Production));
        Assert.Equal(PartnerApiOptions.LiveEnvironment, options.Environment);
    }

    [Fact]
    public void Unconfigured_non_Production_defaults_to_test()
    {
        var options = new PartnerApiOptions();
        options.ApplyEnvironmentDefault(new StubHostEnvironment(Environments.Staging));
        Assert.Equal(PartnerApiOptions.TestEnvironment, options.Environment);
    }

    [Fact]
    public void Production_rejects_a_non_live_environment()
    {
        var errors = Validate(
            Environments.Production,
            new PartnerApiOptions { Environment = PartnerApiOptions.TestEnvironment },
            SharedCache());

        Assert.Contains(errors, error => error.Contains("must be live", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_process_local_cache()
    {
        var errors = Validate(
            Environments.Production,
            new PartnerApiOptions { Environment = PartnerApiOptions.LiveEnvironment },
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        Assert.Contains(errors, error => error.Contains("shared IDistributedCache", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_accepts_live_environment_with_a_shared_cache()
    {
        var errors = Validate(
            Environments.Production,
            new PartnerApiOptions { Environment = PartnerApiOptions.LiveEnvironment },
            SharedCache());

        Assert.Empty(errors);
    }

    [Fact]
    public void Testing_may_use_process_local_cache()
    {
        var errors = Validate(
            "Testing",
            new PartnerApiOptions { Environment = PartnerApiOptions.TestEnvironment },
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        Assert.Empty(errors);
    }

    private static string[] Validate(string environmentName, PartnerApiOptions options, IDistributedCache cache) =>
        new PartnerApiConfigurationValidator(
                new StubHostEnvironment(environmentName),
                Options.Create(options),
                cache)
            .Validate()
            .ToArray();

    private static IDistributedCache SharedCache() => new StubDistributedCache();

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ElsaControl.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key)
        {
        }

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            Task.CompletedTask;
    }
}
