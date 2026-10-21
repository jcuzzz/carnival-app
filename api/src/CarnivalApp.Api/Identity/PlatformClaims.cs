using System.Security.Claims;

namespace CarnivalApp.Api.Identity;

public static class PlatformClaims
{
    public const string Role = ClaimTypes.Role;

    public const string Groups = "groups";

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
