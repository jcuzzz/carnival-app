using System.Globalization;

namespace CarnivalApp.Api.Carnivals;

public static class AgeScope
{
    public const string OpenKey = "OPEN";

    // Sized to the columns declared in CarnivalConfigurations.
    public const int MaxCodeLength = 16;

    public static string Normalise(string? code) =>
        code?.Trim().ToUpperInvariant() is { Length: > 0 } normalised ? normalised : OpenKey;

    // A code is one age ("13", a legacy "SNR"), or "N+" for N and older. SQLite ignores
    // HasMaxLength, so the length bound must be checked here.
    public static string? Validate(string? code)
    {
        var value = Normalise(code);
        if (value.Length > MaxCodeLength)
            return $"Age group '{value}' is longer than {MaxCodeLength} characters.";
        return value.All(char.IsAsciiLetterOrDigit) || TryAndOver(value, out _)
            ? null
            : $"Age group '{value}' may contain only letters and digits, or be an age followed by '+' " +
              "(\"18+\") — a division contests one age.";
    }

    public static string AndOver(int age) => $"{age.ToString(CultureInfo.InvariantCulture)}+";

    public static bool TryAge(string? code, out int age) =>
        int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out age);

    public static bool TryAndOver(string? code, out int age)
    {
        age = 0;
        return code is { Length: > 1 } && code[^1] == '+' && TryAge(code[..^1], out age);
    }

    // The age a code starts at: "15" and "15+" are both 15.
    public static bool TryLowerBound(string? code, out int age) =>
        TryAge(code, out age) || TryAndOver(code, out age);

    // "N+" admits every derived age of at least N; any other code admits only itself.
    public static bool Admits(string divisionAge, string studentAge)
    {
        if (divisionAge == OpenKey) return true;
        if (TryAndOver(divisionAge, out var from))
            return TryAge(studentAge, out var age) && age >= from;
        return string.Equals(divisionAge, studentAge, StringComparison.Ordinal);
    }

    // A filter age at or above N selects the "N+" division; a filter "N+" selects every age from N.
    public static bool FilterSelects(string filterAge, string carnivalAge) =>
        string.Equals(filterAge, carnivalAge, StringComparison.Ordinal)
        || (TryAndOver(carnivalAge, out var from) && TryLowerBound(filterAge, out var age) && age >= from)
        || (TryAndOver(filterAge, out var min) && TryLowerBound(carnivalAge, out var lower) && lower >= min);

    // OPEN first, then by age with "N+" just after N, then everything else.
    public static long Order(string code) =>
        code == OpenKey ? -1
        : TryAge(code, out var age) ? age * 2L
        : TryAndOver(code, out var from) ? from * 2L + 1
        : long.MaxValue;
}
