using System.Text;
using System.Text.Encodings.Web;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Completes the runtime-owned start redirect on Control's configured continuation route
/// (<c>/admin/runtimes</c>) without leaving the browser on that console page. The runtime
/// sends only <c>instanceId</c>, <c>state</c> and <c>codeChallenge</c>; Control issues from
/// its own store and form-POSTs <c>code</c> and <c>state</c> to the bound callback.
/// </summary>
public static class ManagedElsaHandoffContinuation
{
    public const string LoginPath = "/admin/login";

    public static IApplicationBuilder UseManagedElsaHandoffContinuation(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (!IsConsoleContinuationPath(context) ||
                (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
                !TryParse(context.Request.Query, out var continuation))
            {
                await next();
                return;
            }

            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }

            await ExecuteAsync(context, continuation, context.RequestAborted);
        });
    }

    public static bool TryParse(IQueryCollection query, out ManagedElsaHandoffContinuationRequest request)
    {
        request = default!;
        var instanceIdValue = First(query, "instance_id", "instanceId");
        var stateValue = First(query, "state");
        var codeChallengeValue = First(query, "code_challenge", "codeChallenge");
        var rawStatus = First(query, "handoff_status", "handoff_error", "error", "status");
        if (rawStatus is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(rawStatus, "^(401|403|409|503)$"))
            return false;
        if (!Guid.TryParse(instanceIdValue, out var instanceId) || instanceId == Guid.Empty)
            return false;
        if (stateValue is not { Length: >= 16 and <= 256 } ||
            !stateValue.All(IsUnreservedStateCharacter) ||
            codeChallengeValue is not { Length: 43 } ||
            !ManagedElsaHandoffIssuer.IsValidCodeChallenge(codeChallengeValue))
            return false;

        request = new ManagedElsaHandoffContinuationRequest(instanceId, stateValue, codeChallengeValue);
        return true;
    }

    public static async Task ExecuteAsync(
        HttpContext context,
        ManagedElsaHandoffContinuationRequest request,
        CancellationToken cancellationToken)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<ManagedElsaHandoffOptions>>();
        if (!options.Value.Enabled)
        {
            await Unavailable(context).ExecuteAsync(context);
            return;
        }

        var sessionReader = context.RequestServices.GetRequiredService<IAuthenticatedControlSessionReader>();
        var session = await sessionReader.ReadAsync(context, cancellationToken);
        if (session is null)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Redirect(LoginRedirect(context), permanent: false);
            return;
        }

        var accounts = context.RequestServices.GetRequiredService<AccountWorkspaceService>();
        var identities = context.RequestServices.GetRequiredService<IManagedElsaInstanceIdentityStore>();
        var handoff = context.RequestServices.GetRequiredService<ManagedElsaHandoffService>();
        var account = await accounts.GetOrCreateAsync(session.Identity, cancellationToken);
        ManagedElsaInstanceIdentity? target = null;
        foreach (var organizationId in account.Organizations.Select(organization => organization.Id).Distinct())
        {
            target = await identities.FindOpenableAsync(organizationId, request.InstanceId, cancellationToken);
            if (target is not null)
                break;
        }

        if (target is null)
        {
            await Denied(context).ExecuteAsync(context);
            return;
        }

        var issued = await handoff.IssueAsync(
            context,
            new ManagedElsaHandoffRequest(
                target.OrganizationId,
                target.InstanceId,
                target.Audience,
                target.CallbackUri,
                request.CodeChallenge),
            cancellationToken);
        if (issued is null)
        {
            await Denied(context).ExecuteAsync(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync(
            RenderAutoSubmitForm(issued.RedirectUri, issued.Token, request.State),
            cancellationToken);
    }

    internal static string LoginRedirect(HttpContext context)
    {
        var returnUrl = context.Request.PathBase.Add(context.Request.Path) + context.Request.QueryString;
        return $"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl.ToString())}";
    }

    internal static string RenderAutoSubmitForm(Uri callbackUri, string token, string state)
    {
        var encoder = HtmlEncoder.Default;
        var action = encoder.Encode(callbackUri.OriginalString);
        var code = encoder.Encode(token);
        var correlation = encoder.Encode(state);
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.Append("<title>Opening managed Elsa</title></head><body>");
        builder.Append("<p>Opening the managed instance…</p>");
        builder.Append("<form method=\"post\" action=\"").Append(action).Append("\">");
        builder.Append("<input type=\"hidden\" name=\"code\" value=\"").Append(code).Append("\">");
        builder.Append("<input type=\"hidden\" name=\"state\" value=\"").Append(correlation).Append("\">");
        builder.Append("<noscript><button type=\"submit\">Continue</button></noscript>");
        builder.Append("</form><script>document.forms[0].submit();</script></body></html>");
        return builder.ToString();
    }

    private static bool IsConsoleContinuationPath(HttpContext context)
    {
        var path = context.Request.PathBase.Add(context.Request.Path);
        return path.Equals(ManagedElsaHandoffDefaults.ConsoleContinuationPath, StringComparison.Ordinal);
    }

    private static string? First(IQueryCollection query, params string[] names)
    {
        foreach (var name in names)
        {
            if (query.TryGetValue(name, out var values) &&
                values is [{ Length: > 0 } value, ..])
                return value;
        }

        return null;
    }

    private static bool IsUnreservedStateCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';

    private static IResult Denied(HttpContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Managed Elsa identity handoff could not be completed.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "handoff.denied",
                ["correlationId"] = context.TraceIdentifier
            });

    private static IResult Unavailable(HttpContext context) =>
        Results.Problem(
            title: "Managed Elsa identity handoff is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["correlationId"] = context.TraceIdentifier
            });
}

public sealed record ManagedElsaHandoffContinuationRequest(
    Guid InstanceId,
    string State,
    string CodeChallenge);
