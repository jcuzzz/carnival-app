using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CarnivalApp.Api.Identity.LocalAccounts;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// No token ever reaches JavaScript: the session lives in an http-only same-origin cookie.
/// </summary>
public static class AuthEndpoints
{
    public const int MaxLoginHintLength = 256;

    public static MeResponse Me(ClaimsPrincipal user)
    {
        var subject = user.GetSubject() ?? "unknown";
        return new MeResponse(
            subject,
            user.GetDisplayName() ?? subject,
            user.FindAll(PlatformClaims.Role).Select(c => c.Value).Distinct().ToArray(),
            user.IsLocalSession());
    }

    /// <param name="sessionCookie">False on a demo host, whose session is its own.</param>
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group, bool oidcEnabled, bool sessionCookie)
    {
        group.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(Me(user)))
            .RequireAuthorization().Produces<MeResponse>();

        if (oidcEnabled)
            group.MapGet("/auth/login", (string? returnUrl, string? loginHint) =>
                {
                    var properties = new AuthenticationProperties { RedirectUri = LocalReturnUrl(returnUrl) };
                    if (Hint(loginHint) is { } hint)
                        properties.SetParameter(OpenIdConnectParameterNames.LoginHint, hint);
                    return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
                })
                .AllowAnonymous();

        // A local session ends here, even one already expired or refused; anything else on a provider
        // host ends the provider's too, so an expired cookie on a shared device cannot leave the
        // provider signing the next person in.
        if (sessionCookie)
            group.MapGet("/auth/logout", async (HttpContext http) =>
                {
                    if (oidcEnabled && !http.User.IsLocalSession() && !LocalSessions.CookieIsLocal(http))
                        return Results.SignOut(
                            new AuthenticationProperties { RedirectUri = "/" },
                            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
                    await LocalSessions.SignOutAsync(http);
                    return Results.Redirect("/");
                })
                .AllowAnonymous();

        return group;
    }

    // Only an app-local path, so the login endpoint can never become an open redirect.
    public static string LocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)
            || returnUrl[0] != '/'
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            // Browsers strip tabs and newlines, so "/\t/evil" navigates as "//evil".
            || returnUrl.Any(c => c < ' ' || c == '\x7F'))
            return "/";
        return returnUrl;
    }

    private static string? Hint(string? loginHint)
    {
        var hint = loginHint?.Trim();
        return string.IsNullOrEmpty(hint) || hint.Length > MaxLoginHintLength || hint.Any(char.IsControl)
            ? null
            : hint;
    }
}

/// <summary><c>LocalAccount</c>: the session is a local account's, so My account applies.</summary>
public sealed record MeResponse(string Subject, string DisplayName, IReadOnlyList<string> Roles, bool LocalAccount);
