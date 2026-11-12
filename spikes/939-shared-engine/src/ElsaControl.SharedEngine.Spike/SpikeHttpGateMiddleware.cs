using Elsa.Http;
using Elsa.Http.Middleware;
using Elsa.Http.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElsaControl.SharedEngine.Spike;

/// <summary>
/// CShells 0.0.28 registers <c>IMiddlewareShellFeature</c> middleware on the host
/// <see cref="IApplicationBuilder"/> after the pipeline has already started, so Elsa's
/// <c>UseWorkflows()</c> never sees HTTP requests. This host-level gate runs after
/// <c>MapShells</c> (so <c>RequestServices</c> is the current shell) and forwards
/// <c>/workflows</c> into Elsa's HTTP middleware when that shell has it.
/// </summary>
public sealed class SpikeHttpGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<IHttpWorkflowLookupService>() is not { } lookup)
        {
            await next(context);
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<HttpActivityOptions>>();
        var middleware = new HttpWorkflowsMiddleware(next);
        await middleware.InvokeAsync(context, context.RequestServices, options, lookup);
    }
}
