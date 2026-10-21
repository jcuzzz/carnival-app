using System.Text.Json;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Records;

// Fails closed: an unreadable blob, or any unknown category or blank age in it, mints no records.
// An OPEN age is skipped rather than failing the blob: writes refuse it, but older rows may hold it.
public static class RecordCategories
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record Blob(List<string>? AgeGroups, List<string>? Categories);

    public static IReadOnlySet<(string AgeGroup, CompetitionCategory Category)>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        Blob? blob;
        try { blob = JsonSerializer.Deserialize<Blob>(json, Options); }
        catch (JsonException) { return null; }
        if (blob?.AgeGroups is not { Count: > 0 } ages || blob.Categories is not { Count: > 0 } categories)
            return null;

        var parsed = new HashSet<CompetitionCategory>();
        foreach (var category in categories)
        {
            if (!Enum.TryParse<CompetitionCategory>(category?.Trim(), ignoreCase: true, out var value))
                return null;
            parsed.Add(value);
        }

        var pairs = new HashSet<(string, CompetitionCategory)>();
        foreach (var age in ages)
        {
            var code = age?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code)) return null;
            if (code == AgeScope.OpenKey) continue;
            foreach (var category in parsed) pairs.Add((code, category));
        }
        return pairs.Count == 0 ? null : pairs;
    }

    // An Open division produces no school record, so the blob may not name it as an age.
    public static string? OpenAgeRefusal(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        Blob? blob;
        try { blob = JsonSerializer.Deserialize<Blob>(json, Options); }
        catch (JsonException) { return null; }
        return blob?.AgeGroups?.Any(a => a?.Trim().ToUpperInvariant() == AgeScope.OpenKey) == true
            ? $"recordCategoriesJson.ageGroups may not name '{AgeScope.OpenKey}': an all-ages "
              + "division produces no school record."
            : null;
    }
}
