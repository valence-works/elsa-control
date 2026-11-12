using CShells.Lifecycle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ElsaControl.SharedEngine.Spike.Tests;

public sealed class SpikeWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _mode;
    private readonly string _contentRoot;
    private readonly int _probeShellCount;

    private SpikeWebApplicationFactory(string mode, string contentRoot, int probeShellCount)
    {
        _mode = mode;
        _contentRoot = contentRoot;
        _probeShellCount = probeShellCount;
    }

    public string ContentRoot => _contentRoot;

    public static SpikeWebApplicationFactory CreateShard() => Create("shard");

    public static SpikeWebApplicationFactory CreateFallback() => Create("fallback");

    public static SpikeWebApplicationFactory CreateLookup(int probeShellCount = 64) => Create("lookup", probeShellCount);

    private static SpikeWebApplicationFactory Create(string mode, int probeShellCount = 64)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "elsa-939-spike", mode, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "App_Data"));
        return new SpikeWebApplicationFactory(mode, contentRoot, probeShellCount);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_contentRoot);
        builder.UseEnvironment("Development");
        builder.UseSetting("Spike:Mode", _mode);
        builder.UseSetting("Spike:DataDirectory", Path.Combine(_contentRoot, "App_Data"));
        builder.UseSetting("Spike:ProbeShellCount", _probeShellCount.ToString());
        var sql = Environment.GetEnvironmentVariable("SPIKE_SQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(sql))
            builder.UseSetting("Spike:SqlConnection", sql);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _contentRoot;
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    public async Task ActivateNamedShellsAsync(params string[] names)
    {
        var registry = Services.GetRequiredService<IShellRegistry>();
        foreach (var name in names)
            await registry.GetOrActivateAsync(name);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(_contentRoot))
                Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

[CollectionDefinition("ShardHost")]
public sealed class ShardHostCollection : ICollectionFixture<ShardHostFixture>;

public sealed class ShardHostFixture : IAsyncLifetime
{
    public SpikeWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = SpikeWebApplicationFactory.CreateShard();
        await Factory.ActivateNamedShellsAsync(SpikeConstants.DefaultShell, SpikeConstants.TenantA, SpikeConstants.TenantB);
    }

    public Task DisposeAsync() => Factory.DisposeAsync().AsTask();
}

[CollectionDefinition("FallbackHost")]
public sealed class FallbackHostCollection : ICollectionFixture<FallbackHostFixture>;

public sealed class FallbackHostFixture : IAsyncLifetime
{
    public SpikeWebApplicationFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = SpikeWebApplicationFactory.CreateFallback();
        await Factory.ActivateNamedShellsAsync(SpikeConstants.DefaultShell);
    }

    public Task DisposeAsync() => Factory.DisposeAsync().AsTask();
}
