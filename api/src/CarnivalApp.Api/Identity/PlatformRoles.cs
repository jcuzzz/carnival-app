using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Accepts retired role names as aliases: the code deploy and the per-school mapping change land
/// in either order, and an unmapped name silently 403s everything.
/// </summary>
public static class PlatformRoles
{
    private static readonly IReadOnlyDictionary<string, PlatformRole> Aliases =
        new Dictionary<string, PlatformRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventAdministrator"] = PlatformRole.CarnivalAdministrator,
            ["Verifier"] = PlatformRole.CarnivalAdministrator,
            ["RecordsManager"] = PlatformRole.CarnivalAdministrator,
            ["ResultsOfficial"] = PlatformRole.Recorder,
            ["ReadOnlyViewer"] = PlatformRole.Viewer,
        };

    /// <summary>
    /// <paramref name="viaAlias"/> is logged so remaining dependence on old names is observable.
    /// </summary>
    public static bool TryParse(string? name, out PlatformRole role, out bool viaAlias)
    {
        viaAlias = false;
        if (string.IsNullOrWhiteSpace(name))
        {
            role = default;
            return false;
        }

        // Enum.TryParse accepts numbers, whose meaning shifted across the role rename — e.g. "3"
        // now grants SystemAdministrator.
        if (char.IsAsciiDigit(name.Trim()[0]) || name.Trim()[0] is '-' or '+')
        {
            role = default;
            return false;
        }

        // Current names win, so an alias can never shadow a future role.
        if (Enum.TryParse(name, ignoreCase: true, out role) && Enum.IsDefined(role))
            return true;

        if (!Aliases.TryGetValue(name.Trim(), out role)) return false;
        viaAlias = true;
        return true;
    }
}
