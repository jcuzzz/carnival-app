using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// An /api fetch gets a 401, not a redirect: a cross-origin auth redirect is opaque to fetch() and
/// reads as "server unreachable". Navigations (Accept: text/html) still redirect.
/// </summary>
public static class ApiChallengeSuppression
{
    public static bool PrefersStatusOverRedirect(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && !request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    // Chains any handler the provider already installed
    // (e.g. Microsoft.Identity.Web's domain hint).
    public static void Apply(OpenIdConnectOptions options)
    {
        var inner = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            if (PrefersStatusOverRedirect(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.HandleResponse();
                return;
            }

            if (inner is not null) await inner(context);
        };
    }

    // Forbid is forwarded to the cookie scheme, whose default 302 to /Account/AccessDenied is
    // unmapped — fetch() would follow it into a 404 the outbox reads as a terminal rejection.
    public static void Apply(CookieAuthenticationOptions options)
    {
        options.Events.OnRedirectToAccessDenied = StatusInsteadOfRedirect(
            StatusCodes.Status403Forbidden, options.Events.OnRedirectToAccessDenied);
        options.Events.OnRedirectToLogin = StatusInsteadOfRedirect(
            StatusCodes.Status401Unauthorized, options.Events.OnRedirectToLogin);
    }

    private static Func<RedirectContext<CookieAuthenticationOptions>, Task> StatusInsteadOfRedirect(
        int statusCode, Func<RedirectContext<CookieAuthenticationOptions>, Task> inner) =>
        context =>
        {
            if (!PrefersStatusOverRedirect(context.Request)) return inner(context);
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        };
}
