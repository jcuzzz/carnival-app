using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarnivalApp.Api.Carnivals;

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
                OpenFromAge = rule.OpenFromAge,
                OpenLabel = ReadOpenLabel(rule.OpenLabel, sourceName),
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

    // "OPEN" is refused: an event scoped to it reads as all ages, so the band could never be its
    // own division. Uppercased because eligibility matches age codes by exact string equality.
    private static string ReadOpenLabel(string? declared, string sourceName)
    {
        var label = declared?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(label)) return AgeGroupRule.DefaultOpenLabel;
        if (label == AgeScope.OpenKey)
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': ageGroupRule.openLabel may not be '{declared!.Trim()}' — " +
                $"'{AgeScope.OpenKey}' is reserved for the event age scope meaning OPEN ACROSS the age " +
                "axis (every age eligible), so a derived band of that name could never be scheduled as a " +
                $"division of its own. Name the band something else (the default is " +
                $"'{AgeGroupRule.DefaultOpenLabel}').");
        if (AgeScope.Validate(label) is { } invalid)
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': ageGroupRule.openLabel '{declared!.Trim()}' is not a " +
                $"usable age-group code — {invalid}");
        // A numeric label collides with the plain ages the derivation produces below the cut-off.
        if (label.All(char.IsAsciiDigit))
            throw new InvalidDataException(
                $"Import mapping '{sourceName}': ageGroupRule.openLabel may not be '{declared!.Trim()}' — " +
                "a number is what the derivation produces for every age below the cut-off, so the band " +
                $"would be indistinguishable from that age group. Name it something else (the default is " +
                $"'{AgeGroupRule.DefaultOpenLabel}').");
        return label;
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
        public int? OpenFromAge { get; set; }

        public string? OpenLabel { get; set; }
    }
}

public sealed class ImportColumn
{
    public required string SourceColumn { get; init; }
    public required string MapsTo { get; init; }
    public bool Required { get; init; }
}

/// <summary>
/// Age as at the cut-off day of the competition year; ages at or above <see
/// cref="OpenFromAge"/> collapse into one senior band.
/// </summary>
public sealed class AgeGroupRule
{
    /// <summary>Never "OPEN", which is reserved for the event age scope meaning all ages.</summary>
    public const string DefaultOpenLabel = "SNR";

    public required int CutOffMonth { get; init; }
    public required int CutOffDay { get; init; }

    /// <summary>Absent ⇒ every student derives a plain age.</summary>
    public int? OpenFromAge { get; init; }

    public string OpenLabel { get; init; } = DefaultOpenLabel;

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
        if (OpenFromAge is int open && age >= open) return OpenLabel;
        return age.ToString();
    }
}
