using System.Globalization;

namespace CarnivalApp.Api.School;

/// <summary>
/// The client writes black or white text on it, whichever contrasts
/// more, so the colour must reach 4.5:1 against at least one.
/// </summary>
public static class BrandColour
{
    public const string ConfigKey = "School:BrandColour";
    public const double MinimumContrastRatio = 4.5;

    /// <summary>Lower-case <c>#rrggbb</c>, or null when absent or unparseable.</summary>
    public static string? Normalise(string? configured) =>
        TryParse(configured, out var r, out var g, out var b) ? $"#{r:x2}{g:x2}{b:x2}" : null;

    public static double? BestContrast(string? configured)
    {
        if (!TryParse(configured, out var r, out var g, out var b)) return null;
        var (white, black) = Contrasts(r, g, b);
        return Math.Max(white, black);
    }

    /// <summary>
    /// Null when absent or usable; otherwise a startup failure an administrator can act on.
    /// </summary>
    public static string? DescribeRefusal(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        if (!TryParse(configured, out var r, out var g, out var b))
            return $"{ConfigKey} is not a colour: '{configured}'. Write it as #rrggbb or #rgb "
                + "(for example #1f3a5f) in appsettings.School.json, or as School__BrandColour, "
                + "before starting the API.";

        var (white, black) = Contrasts(r, g, b);
        if (white >= MinimumContrastRatio || black >= MinimumContrastRatio) return null;

        return $"{ConfigKey} '{configured}' reaches only "
            + $"{white.ToString("0.00", CultureInfo.InvariantCulture)}:1 against white and "
            + $"{black.ToString("0.00", CultureInfo.InvariantCulture)}:1 against black; a brand "
            + $"colour must reach {MinimumContrastRatio.ToString("0.0", CultureInfo.InvariantCulture)}:1 "
            + "against one of them (ADR-0040). Choose a darker or lighter colour before starting the API.";
    }

    private static bool TryParse(string? configured, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var text = configured?.Trim();
        if (text is null || text.Length is not (4 or 7) || text[0] != '#') return false;

        var digits = text[1..];
        if (!digits.All(char.IsAsciiHexDigit)) return false;
        if (digits.Length == 3) digits = string.Concat(digits.Select(c => new string(c, 2)));

        r = byte.Parse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = byte.Parse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = byte.Parse(digits[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    private static (double White, double Black) Contrasts(byte r, byte g, byte b)
    {
        var luminance = 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        return ((1.0 + 0.05) / (luminance + 0.05), (luminance + 0.05) / 0.05);
    }

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
