using System.Text.Json;
using System.Text.Json.Serialization;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// A null element of the series is an ordinal not yet taken: rounds arrive out of order.
public sealed record AttemptDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Foul = false)
{
    [JsonIgnore]
    public bool Taken => Value.HasValue || Foul;
}

// The series is the source: best mark, status and countback key are derived here, never taken
// from a client.
public static class AttemptSeries
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static List<AttemptDto?> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        // A jump card shares this column and would otherwise parse as untaken ordinals.
        if (JumpCard.TryParse(json, out _)) return [];
        try { return JsonSerializer.Deserialize<List<AttemptDto?>>(json, Options) ?? []; }
        catch (JsonException) { return []; }
    }

    // Nothing taken stores null, never "[]".
    public static string? Serialise(IReadOnlyList<AttemptDto?> series) =>
        series.Any(a => a is { Taken: true }) ? JsonSerializer.Serialize(Trimmed(series), Options) : null;

    // Ordinal is 1-based and may arrive out of order across devices.
    public static List<AttemptDto?> Upsert(
        IReadOnlyList<AttemptDto?> series, int ordinal, decimal? value, bool foul)
    {
        var updated = new List<AttemptDto?>(series);
        while (updated.Count < ordinal) updated.Add(null);
        updated[ordinal - 1] = new AttemptDto(value, foul);
        return updated;
    }

    public static AttemptDto? At(IReadOnlyList<AttemptDto?> series, int ordinal) =>
        ordinal >= 1 && ordinal <= series.Count ? series[ordinal - 1] : null;

    public static decimal? Best(IReadOnlyList<AttemptDto?> series, bool higherBetter)
    {
        var valid = Valid(series);
        if (valid.Count == 0) return null;
        return higherBetter ? valid.Max() : valid.Min();
    }

    // Every taken attempt a foul is NM; a series with nothing taken never reaches storage.
    public static ResultStatus DeriveStatus(IReadOnlyList<AttemptDto?> series) =>
        Valid(series).Count > 0 ? ResultStatus.Ok : ResultStatus.Nm;

    // Countback key: valid marks best-first, compared lexicographically.
    public static List<decimal> SortedKey(IReadOnlyList<AttemptDto?> series, bool higherBetter)
    {
        var valid = Valid(series);
        valid.Sort();
        if (higherBetter) valid.Reverse();
        return valid;
    }

    // Null where the result has no series and ranks on its single mark alone.
    public static IReadOnlyList<decimal>? KeyOf(string? attemptsJson, bool higherBetter)
    {
        if (string.IsNullOrWhiteSpace(attemptsJson)) return null;
        if (JumpCard.TryParse(attemptsJson, out var card)) return JumpCard.Key(card);
        var key = SortedKey(Parse(attemptsJson), higherBetter);
        return key.Count > 0 ? key : null;
    }

    private static List<decimal> Valid(IReadOnlyList<AttemptDto?> series) =>
        [.. series.Where(a => a is { Foul: false, Value: not null }).Select(a => a!.Value!.Value)];

    private static List<AttemptDto?> Trimmed(IReadOnlyList<AttemptDto?> series)
    {
        var last = series.Count;
        while (last > 0 && series[last - 1] is not { Taken: true }) last--;
        return [.. series.Take(last)];
    }
}
