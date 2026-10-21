using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Carnivals;

// Marks per height: O cleared, X failed, - passed.
public sealed record JumpDto(decimal Height, string Marks);

// Suggestions the deck pre-fills from; no rule reads them.
public sealed record LadderDto(decimal Start, decimal Step);

// Every refusal parks a queued mark for a person: rule refusals are 409, a body outside the
// grammar a 400 the client also parks.
public sealed record JumpRefusal(string Code, string Detail, bool BadRequest = false)
{
    public IResult Result() => BadRequest
        ? ApiError.BadRequest(Code, Detail)
        : ApiError.Conflict(Code, Detail);
}

public static class JumpCard
{
    // Metres, as is MaxStep.
    public const decimal MinHeight = 0.01m;
    public const decimal MaxHeight = 5.00m;

    public const decimal MaxStep = 1.00m;

    public const int MaxHeightsJsonLength = 2000;

    // A Height result whose last mark came off counts, scores and prints as no result;
    // a typed NM on other measures is recorded.
    public static readonly Expression<Func<Result, bool>> Recorded = r =>
        r.LifecycleState != ResultLifecycleState.Draft
        && !(r.Status == ResultStatus.Nm && r.AttemptsJson == null
             && r.Entry.CarnivalEvent.EventDefinition.MeasureType == MeasureType.Height);

    public const int DefaultAttemptsPerHeight = 3;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // A card and an ordinal attempt series share the column and are told apart by shape.
    public static bool TryParse(string? json, out List<JumpDto> card)
    {
        card = [];
        if (json is null) return false;
        // Only the first element is sniffed: a later series field could carry the word anywhere.
        var firstObject = json.IndexOf('}');
        if (firstObject < 0
            || !json.AsSpan(0, firstObject).Contains("\"height\"", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            // A height with no marks is not a card: derivation would throw on it.
            if (JsonSerializer.Deserialize<List<JumpDto?>>(json, Options) is not { Count: > 0 } parsed
                || parsed[0] is null || parsed.Any(j => j is { Marks: null }))
                return false;
            card = [.. parsed.Where(j => j is not null).Select(j => j!).OrderBy(j => j.Height)];
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static List<JumpDto> Parse(string? json) => TryParse(json, out var card) ? card : [];

    // An empty card stores null, never "[]".
    public static string? Serialise(IReadOnlyList<JumpDto> card) =>
        card.Count == 0
            ? null
            : JsonSerializer.Serialize(
                card.OrderBy(j => j.Height).Select(j => j with { Height = Canonical(j.Height) }), Options);

    public static List<decimal> ParseHeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return [.. (JsonSerializer.Deserialize<List<decimal>>(json, Options) ?? []).Order()]; }
        catch (JsonException) { return []; }
    }

    public static string SerialiseHeights(IReadOnlyList<decimal> heights) =>
        JsonSerializer.Serialize(heights.Order().Select(Canonical), Options);

    public static LadderDto? ParseLadder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LadderDto>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static string SerialiseLadder(LadderDto ladder) =>
        JsonSerializer.Serialize(new LadderDto(Canonical(ladder.Start), Canonical(ladder.Step)), Options);

    // A decimal serialises at its own scale, so 1.2 and 1.20 would read as reconciler drift.
    private static decimal Canonical(decimal value) => decimal.Round(value, 2) + 0.00m;

    // The server rounds nothing: a height that is not a whole centimetre is refused.
    public static bool IsWholeCentimetre(decimal value) => decimal.Truncate(value * 100) == value * 100;

    public static bool IsHeight(decimal value) =>
        value >= MinHeight && value <= MaxHeight && IsWholeCentimetre(value);

    public static bool IsStep(decimal value) =>
        value >= MinHeight && value <= MaxStep && IsWholeCentimetre(value);

    public static bool IsAttemptsPerHeight(int value) => value is 2 or 3;

    public static int? AttemptsPerHeightFor(EventDefinition definition) =>
        definition.MeasureType == MeasureType.Height
            ? definition.AttemptsPerHeight ?? DefaultAttemptsPerHeight
            : null;

    // A division created before the setting existed reads the default.
    public static int AttemptsPerHeightOf(CarnivalEvent carnivalEvent) =>
        carnivalEvent.AttemptsPerHeight ?? DefaultAttemptsPerHeight;

    // O and - end a height: at three the whole grammar is X, XX, XXX, O, XO, XXO, -, X-, XX-.
    public static bool IsValidMarks(string? marks, int attemptsPerHeight)
    {
        if (marks is null || marks.Length == 0 || marks.Length > attemptsPerHeight) return false;
        for (var i = 0; i < marks.Length; i++)
        {
            if (marks[i] == 'X') continue;
            return marks[i] is 'O' or '-' && i == marks.Length - 1;
        }
        return true;
    }

    public static decimal? Best(IReadOnlyList<JumpDto> card) => card
        .Where(j => j.Marks.Contains('O', StringComparison.Ordinal))
        .Select(j => (decimal?)j.Height)
        .Max();

    public static int? FailsAtBest(IReadOnlyList<JumpDto> card) =>
        Best(card) is { } best
            ? card.Where(j => j.Height == best).Sum(j => Fails(j.Marks))
            : null;

    // Failures up to and including the best height: the bar the athlete went out on costs nothing.
    public static int CountbackFails(IReadOnlyList<JumpDto> card) =>
        Best(card) is { } best
            ? card.Where(j => j.Height <= best).Sum(j => Fails(j.Marks))
            : 0;

    // At 3: three consecutive failures across the card; a pass keeps the run, a clearance ends it.
    // At 2: both attempts at one height failed; nothing carries past a pass or to the next height.
    public static bool IsEliminated(IReadOnlyList<JumpDto> card, int attemptsPerHeight) =>
        attemptsPerHeight == 2
            ? card.Any(j => j.Marks == "XX")
            : string.Concat(card.OrderBy(j => j.Height).Select(j => j.Marks))
                .Replace("-", "", StringComparison.Ordinal)
                .Contains("XXX", StringComparison.Ordinal);

    // A card in progress is provisionally NM until the first clearance lands.
    public static ResultStatus DeriveStatus(IReadOnlyList<JumpDto> card) =>
        Best(card) is not null ? ResultStatus.Ok : ResultStatus.Nm;

    // Higher-is-better throughout: best height, then fewest failures at it, then fewest up to and
    // including it. Null while nothing is cleared: such a card does not rank.
    public static IReadOnlyList<decimal>? Key(IReadOnlyList<JumpDto> card) =>
        Best(card) is { } best ? [best, -(FailsAtBest(card) ?? 0), -CountbackFails(card)] : null;

    // Capture and correction replay both append through here, so a correction cannot build a card
    // that capture would have refused.
    public static JumpRefusal? TryAppend(
        IReadOnlyList<JumpDto> card, IReadOnlyList<decimal> ladderHeights, int attemptsPerHeight,
        decimal height, string? mark, out List<JumpDto> updated)
    {
        updated = [.. card];
        if (mark is not ("O" or "X" or "-"))
            return new JumpRefusal(ErrorCodes.ValidationFailed,
                "A mark is O (cleared), X (failed) or - (passed).", BadRequest: true);
        if (!IsHeight(height))
            return new JumpRefusal(ErrorCodes.ValidationFailed,
                $"{height.ToString(CultureInfo.InvariantCulture)} is not a bar height: heights are " +
                $"whole centimetres between {Format(MinHeight)} and {Format(MaxHeight)} metres.",
                BadRequest: true);
        if (IsEliminated(card, attemptsPerHeight))
            return new JumpRefusal(ErrorCodes.AthleteEliminated, attemptsPerHeight == 2
                ? "This athlete failed both attempts at one height and is out of the competition."
                : "This athlete has three consecutive failures and is out of the competition.");
        if (!ladderHeights.Contains(height))
            return new JumpRefusal(ErrorCodes.HeightNotOnLadder,
                $"{Format(height)} m is not a height this event has contested. Raise the bar to it first.");
        if (card.Any(j => j.Height > height))
            return new JumpRefusal(ErrorCodes.BarNeverLowered,
                $"This athlete has already jumped above {Format(height)} m, and the bar never comes down.");

        var atHeight = card.FirstOrDefault(j => j.Height == height);
        var marks = atHeight?.Marks ?? "";
        if (marks.Contains('O', StringComparison.Ordinal))
            return new JumpRefusal(ErrorCodes.HeightAlreadyCleared,
                $"{Format(height)} m is already cleared, so there is nothing left to jump at it.");
        if (marks.EndsWith('-'))
            return new JumpRefusal(ErrorCodes.HeightPassed,
                $"This athlete passed at {Format(height)} m, which forfeits the rest of that height.");
        if (marks.Length >= attemptsPerHeight)
            return new JumpRefusal(ErrorCodes.HeightAttemptsExhausted,
                $"All {attemptsPerHeight} attempts at {Format(height)} m are recorded.");

        updated.RemoveAll(j => j.Height == height);
        updated.Add(new JumpDto(height, marks + mark));
        updated.Sort((a, b) => a.Height.CompareTo(b.Height));
        return null;
    }

    public static string Format(decimal height) => EventLadder.Metres(height);

    private static int Fails(string marks) => marks.Count(c => c == 'X');
}
