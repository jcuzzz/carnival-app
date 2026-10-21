using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Scoring;

// Shared so the results book, the deck badge and the board can never disagree about who won a heat.
public static class HeatPlacings
{
    // Series: an attempts event's countback key, best-first; null ranks on the mark alone.
    public readonly record struct Swim(
        Guid Id, int? Heat, ResultStatus Status, decimal? MeasuredValue,
        IReadOnlyList<decimal>? Series = null);

    public static bool ScoresPlaces(EventScoringMode? scoringMode, EventTier tier) =>
        (scoringMode ?? (tier == EventTier.Social
            ? EventScoringMode.Social
            : EventScoringMode.Places)) == EventScoringMode.Places;

    // Heat null is its own field, not part of heat 1 — the grouping ScoringService pays points in.
    public static Dictionary<Guid, int> Rank(
        IEnumerable<Swim> swims, MeasureType measureType, EventScoringMode? scoringMode, EventTier tier)
    {
        var places = new Dictionary<Guid, int>();
        if (!ScoresPlaces(scoringMode, tier)) return places;
        var higherBetter = Placement.HigherIsBetter(measureType);
        // A field division split across flights is one competition; only Time events rank per heat.
        var timed = measureType == MeasureType.Time;
        foreach (var heat in swims.GroupBy(s => timed ? s.Heat : null))
        {
            var ranked = heat.Where(s => s.Status == ResultStatus.Ok && s.MeasuredValue.HasValue).ToList();
            var ranks = Placement.Rank([.. ranked.Select(Key)], higherBetter);
            for (var i = 0; i < ranked.Count; i++) places[ranked[i].Id] = ranks[i];
        }
        return places;
    }

    private static IReadOnlyList<decimal> Key(Swim swim) => swim.Series ?? [swim.MeasuredValue!.Value];

    public static int? Of(Dictionary<Guid, int> places, Guid id) =>
        places.TryGetValue(id, out var place) ? place : null;
}
