using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarnivalApp.Api.Imports;

/// <summary>
/// Only columns listed here are read from the source export; everything else is never stored.
/// </summary>
public sealed class ImportMapping
{
    public required IReadOnlyDictionary<string, ImportColumn> Columns { get; init; }
    public required AgeGroupRule AgeGroupRule { get; init; }

    public string Version { get; init; } = "import-mapping.example.json@v1";

    public static ImportMapping LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileName(path));
    }

    public static ImportMapping Load(Stream json, string sourceName)
    {
        var doc = JsonSerializer.Deserialize<MappingDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Import mapping '{sourceName}' is empty.");
        if (doc.Columns is null or { Count: 0 })
            throw new InvalidDataException($"Import mapping '{sourceName}' defines no columns.");
        if (doc.Columns.FirstOrDefault(kv => kv.Value is null).Key is { } empty)
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': column '{empty}' has no settings (write {{}} for none).");
        if (doc.Columns.Keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidDataException(
                $"Import mapping '{sourceName}' names column '{twice.Key}' more than once " +
                "(column names are matched ignoring case).");
        var rule = doc.AgeGroupRule ?? new AgeGroupRuleDocument();
        if (RetiredKeyRefusal(rule, sourceName) is { } retired)
            throw new InvalidDataException(retired);
        var cutOff = ReadCutOff(rule, sourceName);

        return new ImportMapping
        {
            Columns = doc.Columns.ToDictionary(
                kv => kv.Key,
                kv => new ImportColumn
                {
                    SourceColumn = kv.Key,
                    MapsTo = kv.Value.MapsTo ?? kv.Key.ToLowerInvariant(),
                    Required = kv.Value.Required,
                },
                StringComparer.OrdinalIgnoreCase),
            AgeGroupRule = new AgeGroupRule
            {
                CutOffMonth = cutOff.Month,
                CutOffDay = cutOff.Day,
            },
            Version = sourceName,
        };
    }

    // The year of a legacy cutOffDate is ignored: each import names its own competition year.
    private static DateOnly ReadCutOff(AgeGroupRuleDocument rule, string sourceName)
    {
        if (rule.CutOff is { } monthDay)
        {
            // A leap year, so a 29 February cut-off parses.
            if (DateOnly.TryParseExact($"2000-{monthDay.Trim()}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': ageGroupRule.cutOff '{monthDay}' is not a month " +
                "and day written MM-DD (e.g. \"12-31\").");
        }
        if (rule.CutOffDate is { } legacy)
        {
            if (DateOnly.TryParseExact(legacy.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': ageGroupRule.cutOffDate '{legacy}' is not a " +
                "date written YYYY-MM-DD. Prefer ageGroupRule.cutOff as MM-DD (e.g. \"12-31\").");
        }
        throw new InvalidDataException(
            $"Import mapping '{sourceName}' must define ageGroupRule.cutOff (MM-DD).");
    }

    /// <summary>
    /// Throws when the mapping still sets the removed senior band, so a deployment fails at startup
    /// rather than on its first import. A missing or unreadable file is left to the import path.
    /// </summary>
    public static void RefuseRetiredKeys(string path)
    {
        if (!File.Exists(path)) return;
        MappingDocument? doc;
        try
        {
            using var stream = File.OpenRead(path);
            doc = JsonSerializer.Deserialize<MappingDocument>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (doc?.AgeGroupRule is { } rule && RetiredKeyRefusal(rule, Path.GetFileName(path)) is { } retired)
            throw new InvalidOperationException(retired);
    }

    private static string? RetiredKeyRefusal(AgeGroupRuleDocument rule, string sourceName)
    {
        var key = rule.OpenFromAge.HasValue ? "openFromAge" : rule.OpenLabel.HasValue ? "openLabel" : null;
        return key is null
            ? null
            : $"Import mapping '{sourceName}': ageGroupRule.{key} is no longer supported. Remove it: the "
              + "oldest age now takes everyone older, set in School settings → Ages.";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class MappingDocument
    {
        public Dictionary<string, ColumnDocument>? Columns { get; set; }
        public AgeGroupRuleDocument? AgeGroupRule { get; set; }
    }

    private sealed class ColumnDocument
    {
        [JsonPropertyName("maps_to")]
        public string? MapsTo { get; set; }
        public bool Required { get; set; }
    }

    private sealed class AgeGroupRuleDocument
    {
        public string? CutOff { get; set; }
        public string? CutOffDate { get; set; }

        // Removed keys, read only to refuse them.
        public JsonElement? OpenFromAge { get; set; }
        public JsonElement? OpenLabel { get; set; }
    }
}

public sealed class ImportColumn
{
    public required string SourceColumn { get; init; }
    public required string MapsTo { get; init; }
    public bool Required { get; init; }
}

/// <summary>Age as at the cut-off day of the competition year.</summary>
public sealed class AgeGroupRule
{
    public required int CutOffMonth { get; init; }
    public required int CutOffDay { get; init; }

    /// <summary>MM-DD, as the mapping and the API write it.</summary>
    public string CutOff => $"{CutOffMonth:00}-{CutOffDay:00}";

    // A 29 February cut-off falls on the 28th in a common year.
    public DateOnly CutOffIn(int competitionYear) => new(competitionYear, CutOffMonth,
        Math.Min(CutOffDay, DateTime.DaysInMonth(competitionYear, CutOffMonth)));

    public string DeriveAgeGroup(DateOnly birthdate, int competitionYear)
    {
        var cutOff = CutOffIn(competitionYear);
        var age = cutOff.Year - birthdate.Year;
        if (birthdate > cutOff.AddYears(-age)) age--;
        return age.ToString();
    }
}
