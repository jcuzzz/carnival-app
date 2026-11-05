using System.Text.RegularExpressions;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Plain names, never email addresses, so a local account can never capture a provider user's
/// sign-in or role assignment.
/// </summary>
public static partial class LocalUsernames
{
    public const int MaxLength = 64;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string? Normalise(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();

    /// <summary>Null when <paramref name="normalised"/> may be used as a username.</summary>
    public static string? Validate(string? username, IConfiguration configuration, out string normalised)
    {
        normalised = Normalise(username) ?? "";
        if (!Pattern().IsMatch(normalised)) return ErrorCodes.AccountUsernameInvalid;
        var candidate = normalised;
        return GroupToRoleClaimsTransformation.BootstrapAdministrators(configuration)
            .Any(entry => entry.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            ? ErrorCodes.AccountUsernameReserved
            : null;
    }

    public static string? NormaliseDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim() ?? "";
        return trimmed.Length is 0 or > LocalAccountLimits.MaxDisplayNameLength
            || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }
}
