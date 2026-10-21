using System.Globalization;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Imports;

/// <summary>
/// The birthdate is used transiently to derive the age group and is never stored.
/// </summary>
public static class CompetitionDerivation
{
    // Excel's 1900 leap-year bug makes day 0 = 1899-12-30
    // for serials >= 61, which all birthdates are.
    private static readonly DateOnly ExcelEpoch = new(1899, 12, 30);

    /// <summary>
    /// Excel serial, d/m/Y with optional time, or ISO — the encodings seen in CASES21 exports.
    /// </summary>
    public static DateOnly? ParseBirthdate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var serial))
        {
            if (serial is < 61 or > 80000) return null; // implausible as a birthdate
            return ExcelEpoch.AddDays(serial);
        }

        string[] isoFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];
        if (DateTime.TryParseExact(value, isoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var iso))
            return DateOnly.FromDateTime(iso);

        string[] dmyFormats =
        [
            "d/M/yyyy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss",
            "d/M/yyyy h:mm tt", "d/M/yyyy h:mm:ss tt",
        ];
        if (DateTime.TryParseExact(value, dmyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dmy))
            return DateOnly.FromDateTime(dmy);

        return null;
    }

    /// <summary>A competition grouping default, not a gender record. Blank returns null.</summary>
    public static CompetitionCategory? DeriveCategory(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return null;
        return gender.Trim().ToUpperInvariant() switch
        {
            "M" => CompetitionCategory.M,
            "F" => CompetitionCategory.F,
            _ => CompetitionCategory.S,
        };
    }
}
