using CShells.AspNetCore.Extensions;
using CShells.AspNetCore.Routing;
using CShells.Resolution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;

namespace ElsaControl.SharedEngine.Spike;

/// <summary>
/// CShells 0.0.28 registers every shell's endpoints in one data source without Host
/// metadata, so identical paths (for example <c>/spike/whoami</c> or <c>/elsa/api/*</c>)
/// become an ambiguous match. This host-level selector keeps only the endpoints that
/// belong to the shell CShells host routing resolved. It is a spike workaround, not an
/// elsa-core change.
/// </summary>
public sealed class ShellHostEndpointSelectorPolicy(IShellResolver resolver) : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 10_000;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        var shellCount = 0;
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].Metadata.GetMetadata<ShellEndpointMetadata>() is not null)
                shellCount++;
            if (shellCount > 1)
                return true;
        }

        return false;
    }

    public async Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        var resolutionContext = httpContext.ToShellResolutionContext();
        var shellId = await resolver.ResolveAsync(resolutionContext, httpContext.RequestAborted);
        var shellName = shellId?.Name;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
                continue;

            var metadata = candidates[i].Endpoint.Metadata.GetMetadata<ShellEndpointMetadata>();
            if (metadata is null)
                continue;

            if (shellName is null || !string.Equals(metadata.ShellId.Name, shellName, StringComparison.OrdinalIgnoreCase))
                candidates.SetValidity(i, false);
        }
    }
}
