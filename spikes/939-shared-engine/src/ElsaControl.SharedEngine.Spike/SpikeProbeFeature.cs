using CShells;
using CShells.AspNetCore.Features;
using CShells.Features;
using Elsa.Common.DistributedHosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElsaControl.SharedEngine.Spike;

[ShellFeature(DisplayName = "Spike probe")]
public sealed class SpikeProbeFeature : IWebShellFeature
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddAuthentication();
        services.AddAuthorization();
        services.Configure<DistributedLockingOptions>(options => options.AllowLocalLockProviderInDistributedRuntime = true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, IHostEnvironment? environment)
    {
        endpoints.MapGet("/spike/whoami", (ShellId shellId) => Results.Json(new
        {
            shell = shellId.Name
        }));
    }
}
