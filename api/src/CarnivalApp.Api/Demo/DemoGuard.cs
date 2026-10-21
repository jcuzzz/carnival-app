using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Demo;

/// <summary>
/// ADR-0061 §4. By path prefix rather than per endpoint, so an import route added later is refused
/// without anyone remembering to; before authentication, so the answer never depends on who asks.
/// </summary>
public static class DemoGuard
{
    public const string ImportsPrefix = "/api/v1/imports";

    public static IApplicationBuilder UseDemoGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        if (context.Request.Path.StartsWithSegments(ImportsPrefix)
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method))
        {
            await ApiError.Forbidden(ErrorCodes.DemoImportsDisabled,
                    "Roster imports are disabled on the public demo; it holds synthetic data only.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    });
}
