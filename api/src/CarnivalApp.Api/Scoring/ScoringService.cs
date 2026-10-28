using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Scoring;

// Computed on read from current results; each Time-event heat is its own competition for points.
public class ScoringService(CarnivalAppDbContext db)
{
    // Rank: equal totals share a rank and the next skips (1, 1, 3); computed here so the Dashboard
    // and the reports table cannot disagree.
    public sealed record HouseStanding(
        string HouseCode, int Total, int PlacementPoints, int ParticipationPoints, int BonusPoints,
        int Rank);

    // A places-scored definition whose points are multiplied; weight 1 is not listed.
    public sealed record WeightedEvent(string DefinitionName, int Weight);

    public sealed record Standings(
        Guid CarnivalId,
        string RuleVersion,
        bool Provisional,
        IReadOnlyList<HouseStanding> Houses,
        int EventsCounted,
        int ResultsCounted,
        IReadOnlyList<WeightedEvent> WeightedEvents,
        bool HasSocialEvents);

    public async Task<Standings?> ComputeAsync(Guid carnivalId, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == carnivalId, ct);
        if (carnival is null) return null;

        var rules = CarnivalSettings.Parse(carnival.SettingsJson).Scoring;

        // Every scoring path below reaches houses only through this list, so an event with
        // ScoresPoints = false can never touch a total.
        var events = await db.CarnivalEvents.AsNoTracking()
            .Where(e => e.CarnivalId == carnivalId && e.ScoresPoints)
            .Select(e => new
            {
                e.Id,
                e.EventDefinition.Name,
                e.EventDefinition.MeasureType,
                e.EventDefinition.ScoringWeight,
                e.Tier,
                e.ScoringMode,
            })
            .ToListAsync(ct);
        var socialEventIds = events
            .Where(e => !HeatPlacings.ScoresPlaces(e.ScoringMode, e.Tier))
            .Select(e => e.Id)
            .ToList();
        var social = socialEventIds.ToHashSet();
        // Places-scored only: the results loop skips any event it cannot find here.
        var eventMeta = events.Where(e => !social.Contains(e.Id)).ToDictionary(e => e.Id);

        var rows = await db.Results.AsNoTracking()
            .Where(r => r.Entry.CarnivalEvent.CarnivalId == carnivalId)
            .Where(JumpCard.Recorded)
            .Select(r => new
            {
                EventId = r.Entry.CarnivalEventId,
                r.Entry.Heat,
                House = r.Entry.SnapshotHouseCode,
                r.Status,
                r.MeasuredValue,
                r.AttemptsJson,
            })
            .ToListAsync(ct);

        var placement = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var participation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bonus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventsWithResults = 0;
        var resultsCounted = 0;

        foreach (var eventGroup in rows.GroupBy(r => r.EventId))
        {
            if (!eventMeta.TryGetValue(eventGroup.Key, out var meta)) continue;
            // Per event, not per heat: splitting a field must not inflate events counted.
            eventsWithResults++;
            var weight = WeightOf(meta.ScoringWeight);
            var higherBetter = Placement.HigherIsBetter(meta.MeasureType);

            // Null heat is its own group, so a half-marshalled event places each entry once, never
            // against a swimmer outside its field. Only Time events split, as in HeatPlacings.
            var timed = meta.MeasureType == MeasureType.Time;
            foreach (var heat in eventGroup.GroupBy(r => timed ? r.Heat : null))
            {
                var ranked = heat
                    .Where(r => r.Status == ResultStatus.Ok && r.MeasuredValue.HasValue)
                    .Select(r => new
                    {
                        r.House,
                        Key = AttemptSeries.KeyOf(r.AttemptsJson, higherBetter)
                              ?? [r.MeasuredValue!.Value],
                    })
                    .ToList();

                var ranks = Placement.Rank([.. ranked.Select(r => r.Key)], higherBetter);
                for (var i = 0; i < ranked.Count; i++)
                {
                    Add(placement, ranked[i].House, rules.PointsForRank(ranks[i]) * weight);
                    resultsCounted++;
                }

                foreach (var r in heat.Where(r => r.Status != ResultStatus.Ok || !r.MeasuredValue.HasValue))
                {
                    var pts = r.Status switch
                    {
                        // NM competed without a valid mark, which is what DnfPoints prices.
                        ResultStatus.Dnf or ResultStatus.Nm => rules.DnfPoints,
                        ResultStatus.Dns => rules.DnsPoints,
                        ResultStatus.Dq => rules.DqPoints,
                        _ => 0,
                    };
                    if (pts != 0) Add(participation, r.House, pts * weight);
                    resultsCounted++;
                }
            }
        }

        // Social scoring is per entry, not per result, and not heat-aware: every entrant's house
        // takes the flat points unless the entry is a DNS. The event weight does not apply.
        if (socialEventIds.Count > 0 && rules.SocialPerEntryPoints != 0)
        {
            var socialEntries = await db.Entries.AsNoTracking()
                .Where(en => socialEventIds.Contains(en.CarnivalEventId))
                .Select(en => new
                {
                    en.CarnivalEventId,
                    House = en.SnapshotHouseCode,
                    NoShow = en.Results.Any(r =>
                        r.LifecycleState != ResultLifecycleState.Draft && r.Status == ResultStatus.Dns),
                })
                .ToListAsync(ct);

            foreach (var group in socialEntries.GroupBy(e => e.CarnivalEventId))
            {
                var scored = group.Where(e => !e.NoShow).ToList();
                if (scored.Count == 0) continue;
                eventsWithResults++;
                foreach (var entry in scored)
                {
                    Add(participation, entry.House, rules.SocialPerEntryPoints);
                    resultsCounted++;
                }
            }
        }

        var adjustments = await db.HousePointsAdjustments.AsNoTracking()
            .Where(a => a.CarnivalId == carnivalId && !a.Voided)
            .Select(a => new { a.HouseCode, a.Points })
            .ToListAsync(ct);
        foreach (var a in adjustments)
            Add(bonus, a.HouseCode, a.Points);

        // Every configured house stands from the start; a house since removed keeps its rows.
        var ordered = (await StandingHouses.ListAsync(db, carnivalId, carnival.Status, ct))
            .Union(placement.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(participation.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(bonus.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(h =>
            {
                var place = placement.GetValueOrDefault(h);
                var part = participation.GetValueOrDefault(h);
                var bon = bonus.GetValueOrDefault(h);
                return new HouseStanding(h, place + part + bon, place, part, bon, Rank: 0);
            })
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.HouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var houses = new List<HouseStanding>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var tied = i > 0 && ordered[i].Total == ordered[i - 1].Total;
            houses.Add(ordered[i] with { Rank = tied ? houses[i - 1].Rank : i + 1 });
        }

        return new Standings(
            carnivalId,
            rules.Version,
            Provisional: carnival.Status != CarnivalStatus.Finalised,
            houses,
            eventsWithResults,
            resultsCounted,
            [.. eventMeta.Values
                .Where(e => WeightOf(e.ScoringWeight) != 1)
                .Select(e => new WeightedEvent(e.Name, WeightOf(e.ScoringWeight)))
                .Distinct()
                .OrderByDescending(e => e.Weight)
                .ThenBy(e => e.DefinitionName, StringComparer.OrdinalIgnoreCase)],
            HasSocialEvents: socialEventIds.Count > 0);
    }

    private static int WeightOf(int scoringWeight) => scoringWeight <= 0 ? 1 : scoringWeight;

    private static void Add(Dictionary<string, int> map, string house, int points) =>
        map[house] = map.GetValueOrDefault(house) + points;
}
