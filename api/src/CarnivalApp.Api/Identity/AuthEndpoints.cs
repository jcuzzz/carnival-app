using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// No token ever reaches JavaScript: the session lives in an http-only same-origin cookie.
/// </summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group, bool oidcEnabled)
    {
        group.MapGet("/me", (ClaimsPrincipal user) =>
        {
            var subject = user.GetSubject() ?? "unknown";
            var displayName = user.GetDisplayName() ?? subject;
            var roles = user.FindAll(PlatformClaims.Role).Select(c => c.Value).Distinct().ToArray();
            return Results.Ok(new MeResponse(subject, displayName, roles));
        }).RequireAuthorization().Produces<MeResponse>();

        if (oidcEnabled)
        {
            group.MapGet("/auth/login", (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties { RedirectUri = LocalReturnUrl(returnUrl) },
                    [OpenIdConnectDefaults.AuthenticationScheme]))
                .AllowAnonymous();

            group.MapGet("/auth/logout", () =>
                Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
                .AllowAnonymous();
        }

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
}

public sealed record MeResponse(string Subject, string DisplayName, IReadOnlyList<string> Roles);
