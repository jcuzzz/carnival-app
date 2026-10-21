using System.Globalization;
using System.Text.RegularExpressions;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// Canonical stored values: seconds for Time, metres for Distance/Height, a whole count for Count.
public static partial class ResultValueParser
{
    [GeneratedRegex(@"^(?<min>\d{1,3}):(?<sec>[0-5]\d)(?:\.(?<frac>\d{1,3}))?$")]
    private static partial Regex MinutesSeconds();

    [GeneratedRegex(@"^(?<sec>\d{1,4})(?:\.(?<frac>\d{1,3}))?$")]
    private static partial Regex SecondsOnly();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,2})?$")]
    private static partial Regex Metres();

    [GeneratedRegex(@"^\d(\.\d{1,2})?$")]
    private static partial Regex HeightMetres();

    [GeneratedRegex(@"^\d{1,6}$")]
    private static partial Regex WholeCount();

    public static bool TryParse(MeasureType measureType, string raw, out decimal value, out string? error)
    {
        value = 0m;
        error = null;
        var input = raw.Trim();
        if (input.Length == 0)
        {
            error = "A measured value is required.";
            return false;
        }

        if (measureType == MeasureType.Time)
        {
            var m = MinutesSeconds().Match(input);
            if (m.Success)
            {
                value = int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture) * 60m
                        + ParseSeconds(m.Groups["sec"].Value, m.Groups["frac"].Value);
            }
            else if ((m = SecondsOnly().Match(input)).Success)
            {
                value = ParseSeconds(m.Groups["sec"].Value, m.Groups["frac"].Value);
            }
            else
            {
                error = $"'{raw}' is not a valid time. Use mm:ss.ff (e.g. 1:23.45) or ss.ff (e.g. 34.56).";
                return false;
            }

            if (value <= 0m)
            {
                error = "A time must be greater than zero.";
                return false;
            }
            return true;
        }

        var (syntax, hint) = measureType switch
        {
            MeasureType.Distance => (Metres(), "Use metres to the centimetre (e.g. 4.32)."),
            MeasureType.Height => (HeightMetres(), "Use metres to the centimetre (e.g. 1.35)."),
            _ => (WholeCount(), "Use a whole number."),
        };
        if (!syntax.IsMatch(input)
            || !decimal.TryParse(input, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{raw}' is not a valid measurement. {hint}";
            return false;
        }
        if (value <= 0m)
        {
            error = "A measurement must be greater than zero.";
            return false;
        }
        return true;
    }

    private static decimal ParseSeconds(string seconds, string fraction) =>
        int.Parse(seconds, CultureInfo.InvariantCulture)
        + (fraction.Length == 0
            ? 0m
            : int.Parse(fraction, CultureInfo.InvariantCulture) / (decimal)Math.Pow(10, fraction.Length));

    public static string FormatTime(decimal seconds)
    {
        var minutes = (int)(seconds / 60m);
        var remainder = seconds - minutes * 60m;
        return minutes > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes}:{remainder:00.00}")
            : string.Create(CultureInfo.InvariantCulture, $"{remainder:0.00}");
    }
}
