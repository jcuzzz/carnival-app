using System.Text;
using Microsoft.AspNetCore.Identity;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// Length, the bundled common-password list and the school's own words; no composition rules.
/// Lengths count Unicode code points after NFKC normalisation.
/// </summary>
public sealed class LocalPasswordValidator(SchoolSettingsProvider settings) : IPasswordValidator<LocalAccount>
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 256;

    /// <summary>Longer input is refused before it is normalised or hashed.</summary>
    public const int MaximumInputLength = 4 * MaximumLength;
    private const int MinimumLettersBesideSchoolWords = 8;
    private const int MinimumSchoolWordLength = 3;
    private const int MinimumDistinctCharacters = 5;

    private const string BlocklistResource = "CarnivalApp.Api.Identity.LocalAccounts.common-passwords.txt";
    private static readonly Lazy<HashSet<string>> Blocklist = new(LoadBlocklist);

    public static string Normalise(string password) => password.Normalize(NormalizationForm.FormKC);

    public static bool IsOverLength(string? password) => password?.Length > MaximumInputLength;

    public async Task<IdentityResult> ValidateAsync(
        UserManager<LocalAccount> manager, LocalAccount user, string? password)
    {
        var school = await settings.GetAsync();
        var code = Check(password ?? "", SchoolWords(school, user.UserName, user.DisplayName));
        return code is null
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = code, Description = Describe(code) });
    }

    /// <summary>Every registered validator, Identity's own included; null when all pass.</summary>
    public static async Task<string?> FirstErrorAsync(
        UserManager<LocalAccount> manager, LocalAccount account, string password)
    {
        foreach (var validator in manager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(manager, account, password);
            if (result.Succeeded) continue;
            var code = result.Errors.First().Code;
            return code.StartsWith("PASSWORD_", StringComparison.Ordinal)
                ? code
                : code == nameof(IdentityErrorDescriber.PasswordTooShort)
                    ? ErrorCodes.PasswordTooShort
                    : ErrorCodes.PasswordTooPredictable;
        }
        return null;
    }

    public static string Describe(string code) => code switch
    {
        ErrorCodes.PasswordTooShort => $"Use at least {MinimumLength} characters. A few unrelated words make a good one.",
        ErrorCodes.PasswordTooLong => $"Use at most {MaximumLength} characters.",
        ErrorCodes.PasswordTooCommon => "That password is on a list of common or breached passwords. Choose another.",
        _ => "That password is too easy to guess from the school's name, its houses or your name. Choose another.",
    };

    public static IEnumerable<string?> SchoolWords(SchoolSettings school, params string?[] personal) =>
        school.Houses.SelectMany(house => new[] { house.Code, house.Name })
            .Prepend(school.Name)
            .Concat(personal);

    public static string? Check(string password, IEnumerable<string?> schoolWords)
    {
        if (IsOverLength(password)) return ErrorCodes.PasswordTooLong;
        var text = Normalise(password);
        var length = text.EnumerateRunes().Count();
        if (length < MinimumLength) return ErrorCodes.PasswordTooShort;
        if (length > MaximumLength) return ErrorCodes.PasswordTooLong;

        var lower = text.ToLowerInvariant();
        if (Blocklist.Value.Contains(lower) || Blocklist.Value.Contains(WithoutTrailingNonLetters(lower)))
            return ErrorCodes.PasswordTooCommon;

        if (lower.EnumerateRunes().Distinct().Count() < MinimumDistinctCharacters)
            return ErrorCodes.PasswordTooPredictable;

        foreach (var word in Words(schoolWords))
            lower = lower.Replace(word, "", StringComparison.Ordinal);
        return lower.Count(char.IsLetter) < MinimumLettersBesideSchoolWords
            ? ErrorCodes.PasswordTooPredictable
            : null;
    }

    private static string WithoutTrailingNonLetters(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetter(text[end - 1])) end--;
        return text[..end];
    }

    // Longest first, so a word containing a shorter one is removed whole.
    private static IEnumerable<string> Words(IEnumerable<string?> sources) =>
        sources
            .SelectMany(source => SplitLetters(Normalise(source ?? "").ToLowerInvariant()))
            .Where(word => word.Length >= MinimumSchoolWordLength)
            .Distinct()
            .OrderByDescending(word => word.Length);

    private static IEnumerable<string> SplitLetters(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                word.Append(c);
                continue;
            }
            if (word.Length > 0) yield return word.ToString();
            word.Clear();
        }
        if (word.Length > 0) yield return word.ToString();
    }

    private static HashSet<string> LoadBlocklist()
    {
        using var stream = typeof(LocalPasswordValidator).Assembly.GetManifestResourceStream(BlocklistResource)
            ?? throw new InvalidOperationException($"The embedded resource {BlocklistResource} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) words.Add(Normalise(line).ToLowerInvariant());
        return words;
    }
}
