namespace ElsaControl.Api.Workspace;

/// <summary>
/// Marks a route or group so every response carries
/// <c>Cache-Control: private, no-store</c>. Use on /api/me reads that return
/// account details.
/// </summary>
public static class PrivateNoStoreCacheControl
{
    public static TBuilder PrivateNoStore<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
}
