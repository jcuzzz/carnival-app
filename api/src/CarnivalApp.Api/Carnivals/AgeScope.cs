namespace CarnivalApp.Api.Carnivals;

public static class AgeScope
{
    public const string OpenKey = "OPEN";

    // Sized to the columns declared in CarnivalConfigurations.
    public const int MaxCodeLength = 16;

    public static string Normalise(string? code) =>
        code?.Trim().ToUpperInvariant() is { Length: > 0 } normalised ? normalised : OpenKey;

    // Eligibility matches a division to a snapshot by string equality, so "13-14" would match
    // no student. SQLite ignores HasMaxLength, so the length bound must be checked here.
    public static string? Validate(string? code)
    {
        var value = Normalise(code);
        if (value.Length > MaxCodeLength)
            return $"Age group '{value}' is longer than {MaxCodeLength} characters.";
        return value.All(char.IsAsciiLetterOrDigit)
            ? null
            : $"Age group '{value}' may contain only letters and digits — a division contests one age.";
    }

    public static int Order(string code) =>
        code == OpenKey ? -1 : int.TryParse(code, out var age) ? age : int.MaxValue;
}
