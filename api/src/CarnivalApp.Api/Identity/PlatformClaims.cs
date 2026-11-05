using System.Security.Claims;

namespace CarnivalApp.Api.Identity;

public static class PlatformClaims
{
    public const string Role = ClaimTypes.Role;

    public const string Groups = "groups";

    /// <summary>The identity's authentication type for a local account's session (ADR-0065).</summary>
    public const string LocalAuthenticationType = "local";

    public const string LocalUsername = "carnival:username";

    /// <summary>The account's security stamp when the cookie was issued; a rotation ends the session.</summary>
    public const string LocalStamp = "carnival:stamp";

    /// <summary>One sign-in's id, so signing out can end that session on the server.</summary>
    public const string LocalSessionId = "carnival:sid";

    public static bool IsLocalSession(this ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, AuthenticationType: LocalAuthenticationType };

    public static string? GetSubject(this ClaimsPrincipal user) =>
        user.GetStableSubject() ?? user.Identity?.Name;

    /// <summary>Never the display name: two people may share one, and roles bind on this.</summary>
    public static string? GetStableSubject(this ClaimsPrincipal user) =>
        user.FindFirstValue("oid")
        ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
        ?? user.FindFirstValue("sub")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Never a fallback to the display name: a name is not an account identifier.
    /// </summary>
    public static string? GetUpn(this ClaimsPrincipal user) =>
        user.FindFirstValue("upn")
        ?? user.FindFirstValue(ClaimTypes.Upn)
        ?? user.FindFirstValue("preferred_username")
        ?? user.FindFirstValue("email")
        ?? user.FindFirstValue(ClaimTypes.Email);

    public static string? GetDisplayName(this ClaimsPrincipal user) =>
        user.Identity?.Name
        ?? user.FindFirstValue("preferred_username")
        ?? user.FindFirstValue("name");

    /// <summary>';'-joined when multiple.</summary>
    public static string? GetPlatformRoles(this ClaimsPrincipal user)
    {
        var roles = user.FindAll(Role).Select(c => c.Value).Distinct().ToList();
        return roles.Count == 0 ? null : string.Join(';', roles);
    }
}
