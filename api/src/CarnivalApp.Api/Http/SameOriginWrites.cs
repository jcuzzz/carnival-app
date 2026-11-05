namespace CarnivalApp.Api.Http;

/// <summary>
/// An unsafe request under <c>/api</c> must come from the app's own origin (ADR-0065 §9). A request
/// carrying neither header is not a browser's (curl, the deploy smoke checks) and passes. Outside
/// <c>/api</c> is exempt: the OIDC callback is a cross-site form post by design.
/// </summary>
public static class SameOriginWrites
{
    public static IApplicationBuilder UseSameOriginWrites(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsRefused(context.Request))
            {
                await ApiError.Forbidden(ErrorCodes.CrossSiteRequestRefused,
                        "This request came from another site, so it was refused. Use the app itself.")
                    .ExecuteAsync(context);
                return;
            }
            await next(context);
        });

    public static bool IsRefused(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api")
            || HttpMethods.IsGet(request.Method)
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method))
            return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
            return !fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                && !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase);

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0) return false;
        // Scheme and host are the client's, after UseForwardedHeaders.
        return !origin.Equals($"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);
    }
}
