using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.School;

/// <summary>A typed house code resolved against the school's configured houses.</summary>
public static class ConfiguredHouse
{
    /// <summary>The configured code as stored, matched ignoring case, or a refusal.</summary>
    public static async Task<(string? Code, IResult? Refusal)> ResolveAsync(
        SchoolSettingsProvider school, string typed, CancellationToken ct)
    {
        var houses = (await school.GetAsync(ct)).Houses;
        var match = houses.FirstOrDefault(
            h => string.Equals(h.Code, typed, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return (match.Code, null);
        var configured = houses.Select(h => h.Code).ToList();
        return (null, ApiError.BadRequest(ErrorCodes.HouseUnknown,
            $"'{typed}' is not one of the school's houses. "
            + $"Configured: {string.Join(", ", configured)}.",
            new() { ["configuredHouses"] = configured }));
    }
}
