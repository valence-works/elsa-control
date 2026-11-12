using CShells;
using CShells.AspNetCore.Routing;
using CShells.Lifecycle;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.SharedEngine.Spike;

/// <summary>
/// CShells 0.0.28 stamps shell endpoints without Host metadata and reuses FastEndpoints
/// names across shells. ASP.NET Core then refuses to build the matcher ("endpoint names
/// must be globally unique"). This subscriber prefixes names with the shell id and adds
/// a Host constraint from <c>WebRouting:Host</c>. Spike workaround, not an elsa-core change.
/// </summary>
public sealed class ShellEndpointHostMetadataSubscriber(DynamicShellEndpointDataSource dataSource)
    : IShellLifecycleSubscriber
{
    public Task OnStateChangedAsync(
        IShell shell,
        ShellLifecycleState previous,
        ShellLifecycleState current,
        CancellationToken cancellationToken = default)
    {
        if (previous != ShellLifecycleState.Initializing || current != ShellLifecycleState.Active)
            return Task.CompletedTask;

        var shellId = new ShellId(shell.Descriptor.Name);
        var endpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ShellEndpointMetadata>()?.ShellId.Equals(shellId) == true)
            .ToArray();
        if (endpoints.Length == 0)
            return Task.CompletedTask;

        var host = shell.ServiceProvider.GetRequiredService<ShellSettings>().GetConfiguration("WebRouting:Host");
        dataSource.RemoveEndpoints(shellId);
        dataSource.AddEndpoints(endpoints.Select(endpoint => Rewrite(endpoint, shell.Descriptor.Name, host)));
        return Task.CompletedTask;
    }

    private static Endpoint Rewrite(RouteEndpoint endpoint, string shellName, string? host)
    {
        var metadata = endpoint.Metadata
            .Where(item => item is not IEndpointNameMetadata and not IRouteNameMetadata)
            .ToList();

        var originalName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? endpoint.DisplayName
            ?? endpoint.RoutePattern.RawText
            ?? "endpoint";
        metadata.Add(new EndpointNameMetadata($"{shellName}:{originalName}"));

        if (!string.IsNullOrWhiteSpace(host) && metadata.All(item => item is not IHostMetadata))
            metadata.Add(new HostAttribute(host));

        return new RouteEndpoint(
            endpoint.RequestDelegate!,
            endpoint.RoutePattern,
            endpoint.Order,
            new EndpointMetadataCollection(metadata),
            $"{shellName}:{endpoint.DisplayName}");
    }
}
