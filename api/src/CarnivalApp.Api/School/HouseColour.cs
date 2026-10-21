namespace CarnivalApp.Api.School;

/// <summary><c>Colour</c> is null whenever the house renders text-only.</summary>
public sealed record HouseMark(string Code, string Name, string? Colour);

/// <summary>
/// No contrast floor: every swatch carries a hairline border. A bad value degrades that house to
/// text-only with a warning and never refuses startup — the name is always rendered.
/// </summary>
public static class HouseColour
{
    public const string ConfigKey = "School:HouseColours";
    public const string HousesKey = "School:Houses";

    /// <summary>Used when <c>School:Houses</c> is unset.</summary>
    public static readonly string[] DefaultHouses = ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"];

    /// <summary>Lower-case <c>#rrggbb</c>, or null when absent or unparseable.</summary>
    public static string? Normalise(string? configured) => BrandColour.Normalise(configured);

    public static IReadOnlyList<HouseMark> Resolve(IConfiguration config, ILogger logger)
    {
        var houses = config.GetSection(HousesKey).Get<string[]>() is { Length: > 0 } configured
            ? configured
            : DefaultHouses;
        return Resolve(houses, config.GetSection(ConfigKey).Get<Dictionary<string, string?>>(), logger);
    }

    // An entry naming no configured house warns: silently dropping it hides a renamed house.
    public static IReadOnlyList<HouseMark> Resolve(
        IReadOnlyList<string> houses,
        IReadOnlyDictionary<string, string?>? configured,
        ILogger logger)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (configured is not null)
            foreach (var entry in configured)
                lookup[entry.Key] = entry.Value;

        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        var marks = new List<HouseMark>(houses.Count);

        foreach (var code in houses)
            marks.Add(new HouseMark(code, code, Accept(code, lookup, taken, logger)));

        var known = new HashSet<string>(houses, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in lookup)
        {
            if (known.Contains(key) || string.IsNullOrWhiteSpace(value)) continue;
            logger.LogWarning(
                "{ConfigKey}:{House} names no house in {HousesKey} ('{Houses}'), so it colours "
                + "nothing; the key must be the house CODE (ADR-0044 §1).",
                ConfigKey, key, HousesKey, string.Join(", ", houses));
        }

        return marks;
    }

    private static string? Accept(
        string code,
        Dictionary<string, string?> lookup,
        Dictionary<string, string> taken,
        ILogger logger)
    {
        if (!lookup.TryGetValue(code, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        if (Normalise(value) is not { } hex)
        {
            logger.LogWarning(
                "{ConfigKey}:{House} is not a colour ('{Value}'); the house renders text-only. "
                + "Write it as #rrggbb or #rgb (ADR-0044 §2).", ConfigKey, code, value);
            return null;
        }

        if (taken.TryGetValue(hex, out var owner))
        {
            logger.LogWarning(
                "{ConfigKey}:{House} '{Value}' repeats the colour already configured for "
                + "{OtherHouse}; the house renders text-only (ADR-0044 §2).",
                ConfigKey, code, value, owner);
            return null;
        }

        taken[hex] = code;
        return hex;
    }
}
