using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0015 §1: the heat is a subdivision of ONE event and it is the PLACING
/// UNIT. Each heat is its own competition for house points and ribbons — a
/// 24-swimmer age group run as three heats produces three winners — while
/// <c>Heat = null</c> keeps the old meaning, "place across the whole event".
///
/// Nothing else moves: the place points, the participation floor, standard
/// competition tie ranking, DNF/DNS/DQ and the relay weight are the ratified
/// ladder (ADR-0007 config) and are exercised by <see cref="ScoringServiceTests"/>.
/// Only the scope those rules resolve in has changed, which is why
/// <see cref="StandingsRegressionTests"/> stays byte-identical.
/// </summary>
public sealed class HeatScoringTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly IEnumerable<(string, int)> AllHousesAtZero =
        Fixtures.Houses.Select(h => (h, 0)).Order().ToList();

    /// <summary>One swim: which heat of the event, whose house, what happened.</summary>
    private sealed record Swim(int? Heat, string House, ResultStatus Status, decimal? Value);

    /// <summary>Seed one Time event of the given weight with these swims, and score it.</summary>
    private Task<ScoringService.Standings> Score(int scoringWeight, params Swim[] swims) =>
        Score(scoringWeight, EventTier.Competitive, scoresPoints: true, MeasureType.Time, swims);

    /// <summary>
    /// As above, but with the two per-event classifications ADR-0014 §4 adds on
    /// top of the ladder — the tier (which decides the effective scoring mode) and
    /// the ScoresPoints switch. Both interact with heats only by NOT interacting
    /// with them, which is the thing worth pinning.
    /// </summary>
    private async Task<ScoringService.Standings> Score(
        int scoringWeight, EventTier tier, bool scoresPoints,
        MeasureType measureType = MeasureType.Time, params Swim[] swims)
    {
        Guid carnivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var carnival = new Carnival
            {
                Id = Guid.CreateVersion7(),
                Name = "Heats",
                Type = CarnivalType.Swimming,
                Date = new DateOnly(2027, 2, 25),
                Status = CarnivalStatus.Open,
                RuleVersion = "v1",
                OwnerSubject = "seed",
                CreatedAtUtc = now,
            };
            db.Carnivals.Add(carnival);
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"EV-{Guid.NewGuid():N}",
                Name = "Freestyle 50m",
                Sport = "Swimming",
                MeasureType = measureType,
                Units = measureType == MeasureType.Time ? "s" : "m",
                ScoringWeight = scoringWeight,
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnival.Id,
                EventDefinitionId = definition.Id,
                Category = CompetitionCategory.M,
                Status = CarnivalEventStatus.Open,
                Tier = tier,
                ScoresPoints = scoresPoints,
            };
            carnivalEvent.AgeGroup = "13";
            db.CarnivalEvents.Add(carnivalEvent);

            foreach (var swim in swims)
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"S-{Guid.NewGuid():N}",
                    Surname = "X", FirstName = "Y", IsActive = true, CreatedAtUtc = now,
                };
                db.Students.Add(student);
                var entry = new Entry
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalEventId = carnivalEvent.Id,
                    StudentId = student.Id,
                    Heat = swim.Heat,
                    SnapshotDisplayName = "Y X",
                    SnapshotYearLevel = 9,
                    SnapshotHouseCode = swim.House,
                    SnapshotAgeGroup = "13",
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = swim.Status,
                    MeasuredValue = swim.Value,
                    LifecycleState = ResultLifecycleState.Provisional,
                    CurrentRevision = 1,
                    RecordedBySubject = "seed",
                    RecordedAtUtc = now,
                    CreatedAtUtc = now,
                });
            }

            await db.SaveChangesAsync();
            carnivalId = carnival.Id;
        }

        using var readScope = _factory.Services.CreateScope();
        var scoring = new ScoringService(readScope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
        var standings = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(standings);
        return standings!;
    }

    private static Dictionary<string, ScoringService.HouseStanding> ByHouse(
        ScoringService.Standings s) => s.Houses.ToDictionary(h => h.HouseCode);

    [Fact]
    public async Task Every_heat_has_its_own_first_second_and_third()
    {
        // The ADR's worked example, in miniature. Heat 1 draws the fast swimmers
        // and heat 3's winner is slower than heat 1's third — and still takes 4
        // points, because each heat is its own competition. This is the school's
        // knowingly-accepted trade-off (ADR-0015 "Consequences"), not a defect:
        // it is what gives every heat something to race for.
        var s = ByHouse(await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 31.2m),   // heat 1, 1st → 4
            new Swim(1, "WATTLE", ResultStatus.Ok, 31.8m),      // heat 1, 2nd → 3
            new Swim(1, "MOUNTAIN ASH", ResultStatus.Ok, 32.1m),  // heat 1, 3rd → 2
            new Swim(3, "GREVILLEA", ResultStatus.Ok, 35.0m),   // heat 3, 1st → 4
            new Swim(3, "MOUNTAIN ASH", ResultStatus.Ok, 36.0m))); // heat 3, 2nd → 3

        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(4, s["GREVILLEA"].Total);        // slower than WATTLE, scores more
        Assert.Equal(2 + 3, s["MOUNTAIN ASH"].Total);   // 3rd of heat 1 + 2nd of heat 3
    }

    [Fact]
    public async Task Scoring_an_event_as_one_field_would_have_scored_differently()
    {
        // The pin for the change itself: the same five swims scored across the
        // whole event give a different board, so the heat scope is doing real
        // work rather than being an inert extra key.
        var heated = ByHouse(await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 31.2m),
            new Swim(1, "WATTLE", ResultStatus.Ok, 31.8m),
            new Swim(1, "MOUNTAIN ASH", ResultStatus.Ok, 32.1m),
            new Swim(3, "GREVILLEA", ResultStatus.Ok, 35.0m),
            new Swim(3, "MOUNTAIN ASH", ResultStatus.Ok, 36.0m)));
        var oneField = ByHouse(await Score(1,
            new Swim(null, "BANKSIA", ResultStatus.Ok, 31.2m),
            new Swim(null, "WATTLE", ResultStatus.Ok, 31.8m),
            new Swim(null, "MOUNTAIN ASH", ResultStatus.Ok, 32.1m),
            new Swim(null, "GREVILLEA", ResultStatus.Ok, 35.0m),
            new Swim(null, "MOUNTAIN ASH", ResultStatus.Ok, 36.0m)));

        Assert.Equal(4, heated["GREVILLEA"].Total);   // heat winner
        Assert.Equal(1, oneField["GREVILLEA"].Total); // 4th of the whole field → floor
    }

    [Fact]
    public async Task A_null_heat_still_places_across_the_whole_event()
    {
        // ADR-0015 §1 explicitly: Heat = null is today's behaviour. Field events,
        // relays, Social events and every carnival recorded so far rely on it.
        var s = ByHouse(await Score(1,
            new Swim(null, "BANKSIA", ResultStatus.Ok, 30m),
            new Swim(null, "WATTLE", ResultStatus.Ok, 31m),
            new Swim(null, "MOUNTAIN ASH", ResultStatus.Ok, 32m),
            new Swim(null, "GREVILLEA", ResultStatus.Ok, 33m)));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
        Assert.Equal(1, s["GREVILLEA"].Total);
    }

    [Fact]
    public async Task Heats_of_different_events_never_share_a_ladder()
    {
        // Heat is scoped to its EVENT (ADR-0015 §1). "Heat 1" of 13 Boys and
        // "heat 1" of 14 Boys are different fields that happen to share a number,
        // so each must produce its own winner — eight entrants across two events
        // is two 1st places, not one.
        Guid carnivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var carnival = new Carnival
            {
                Id = Guid.CreateVersion7(),
                Name = "Two events, one heat number",
                Type = CarnivalType.Swimming,
                Date = new DateOnly(2027, 2, 25),
                Status = CarnivalStatus.Open,
                RuleVersion = "v1",
                OwnerSubject = "seed",
                CreatedAtUtc = now,
            };
            db.Carnivals.Add(carnival);

            void AddEvent(string ageGroup, params Swim[] swims)
            {
                var definition = new EventDefinition
                {
                    Id = Guid.CreateVersion7(),
                    Code = $"EV-{Guid.NewGuid():N}",
                    Name = $"{ageGroup} Freestyle",
                    Sport = "Swimming",
                    MeasureType = MeasureType.Time,
                    Units = "s",
                    ScoringWeight = 1,
                    CreatedAtUtc = now,
                };
                db.EventDefinitions.Add(definition);
                var carnivalEvent = new CarnivalEvent
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalId = carnival.Id,
                    EventDefinitionId = definition.Id,
                    Category = CompetitionCategory.M,
                    Status = CarnivalEventStatus.Open,
                };
                carnivalEvent.AgeGroup = ageGroup;
                db.CarnivalEvents.Add(carnivalEvent);
                foreach (var swim in swims)
                {
                    var student = new Student
                    {
                        Id = Guid.CreateVersion7(),
                        ExternalStudentId = $"S-{Guid.NewGuid():N}",
                        Surname = "X", FirstName = "Y", IsActive = true, CreatedAtUtc = now,
                    };
                    db.Students.Add(student);
                    var entry = new Entry
                    {
                        Id = Guid.CreateVersion7(),
                        CarnivalEventId = carnivalEvent.Id,
                        StudentId = student.Id,
                        Heat = swim.Heat,
                        SnapshotDisplayName = "Y X",
                        SnapshotYearLevel = 9,
                        SnapshotHouseCode = swim.House,
                        SnapshotAgeGroup = ageGroup,
                        SnapshotCategory = CompetitionCategory.M,
                        CreatedAtUtc = now,
                        CreatedBySubject = "seed",
                    };
                    db.Entries.Add(entry);
                    db.Results.Add(new Result
                    {
                        Id = Guid.CreateVersion7(),
                        EntryId = entry.Id,
                        Status = swim.Status,
                        MeasuredValue = swim.Value,
                        LifecycleState = ResultLifecycleState.Provisional,
                        CurrentRevision = 1,
                        RecordedBySubject = "seed",
                        RecordedAtUtc = now,
                        CreatedAtUtc = now,
                    });
                }
            }

            // 13 Boys heat 1: BANKSIA wins on 30.0.
            AddEvent("13",
                new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),
                new Swim(1, "WATTLE", ResultStatus.Ok, 31.0m));
            // 14 Boys heat 1: everyone is slower than the 13s, and GREVILLEA still
            // wins its own event. If the numbers were compared across events,
            // GREVILLEA would place 3rd of four and score 2.
            AddEvent("14",
                new Swim(1, "GREVILLEA", ResultStatus.Ok, 32.0m),
                new Swim(1, "MOUNTAIN ASH", ResultStatus.Ok, 33.0m));

            await db.SaveChangesAsync();
            carnivalId = carnival.Id;
        }

        using var readScope = _factory.Services.CreateScope();
        var scoring = new ScoringService(readScope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
        var standings = (await scoring.ComputeAsync(carnivalId, default))!;
        var s = ByHouse(standings);

        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(4, s["GREVILLEA"].Total);  // 1st of its own event
        Assert.Equal(3, s["MOUNTAIN ASH"].Total);
        Assert.Equal(2, standings.EventsCounted);
    }

    // --- The mixed case: some entries heated, some not --------------------

    [Fact]
    public async Task An_unheated_remainder_is_placed_among_itself_not_refused()
    {
        // THE DECISION (ADR-0015 leaves it open; the implementation plan requires
        // it be made deliberately): in an event where some entries carry a heat
        // and some do not, NULL IS ITS OWN GROUP.
        //
        // Refusing the mixture was the alternative, and it is wrong here. This is
        // not corrupt data — it is the ordinary transient state of every large
        // event on carnival morning, because heats are assigned live at
        // marshalling as entrants present themselves (ADR-0015 §5), offline, over
        // minutes. Standings are computed live on read, so refusing would mean
        // failing the scoreboard, or silently dropping an event's points, at
        // precisely the moment staff are watching it — and a read-only
        // computation has neither a way to report the problem nor the authority
        // to fix it. Assignment validation belongs on the write path.
        var standings = await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),   // heat 1, 1st → 4
            new Swim(1, "WATTLE", ResultStatus.Ok, 31.0m),      // heat 1, 2nd → 3
            new Swim(null, "MOUNTAIN ASH", ResultStatus.Ok, 34.0m), // unheated, 1st of → 4
            new Swim(null, "GREVILLEA", ResultStatus.Ok, 35.0m)); // unheated, 2nd of → 3
        var s = ByHouse(standings);

        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        // Ranked among themselves — NOT against heat 1's swimmers, who would
        // otherwise be counted twice, in their own heat and again in a whole-event
        // ladder they are not being placed in.
        Assert.Equal(4, s["MOUNTAIN ASH"].Total);
        Assert.Equal(3, s["GREVILLEA"].Total);

        // Still ONE event on the board, however many heats it was split into.
        Assert.Equal(1, standings.EventsCounted);
        Assert.Equal(4, standings.ResultsCounted);
    }

    [Fact]
    public async Task The_unheated_remainder_is_ranked_among_itself_and_the_times_interleave()
    {
        // The discriminating form of the mixed case. The four times INTERLEAVE —
        // the two un-heated swimmers are 2nd and 3rd fastest of the event — so the
        // three candidate readings give three different boards and no consistent
        // grouping can accidentally satisfy this:
        //
        //   times      30.0 BANKSIA(h1)  31.0 WATTLE(-)  32.0 MOUNTAIN ASH(-)  33.0 GREVILLEA(h1)
        //   heats ignored entirely        4            3          2            1
        //   remainder ranked event-wide   4            3          2            3
        //   NULL IS ITS OWN GROUP (ours)  4            4          3            3
        //
        // Every house's total differs from at least one wrong reading, and WATTLE,
        // MOUNTAIN ASH and GREVILLEA differ from BOTH.
        var mixed = ByHouse(await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),      // heat 1, 1st → 4
            new Swim(null, "WATTLE", ResultStatus.Ok, 31.0m),      // unheated, 1st of → 4
            new Swim(null, "MOUNTAIN ASH", ResultStatus.Ok, 32.0m),  // unheated, 2nd of → 3
            new Swim(1, "GREVILLEA", ResultStatus.Ok, 33.0m)));    // heat 1, 2nd → 3

        Assert.Equal(4, mixed["BANKSIA"].Total);
        Assert.Equal(4, mixed["WATTLE"].Total);      // 3 if ranked against the heat
        Assert.Equal(3, mixed["MOUNTAIN ASH"].Total);  // 2 under either wrong reading
        Assert.Equal(3, mixed["GREVILLEA"].Total);   // 1 if heats were ignored

        // And it resolves: marshalling the remainder into heat 2 is the same board
        // the mixture already showed, which is why live standings do not jump as
        // the last swimmers are marshalled. Asserted with the SAME literal values,
        // not by comparing the two runs — comparing them would pass under any
        // consistent grouping, including one that ignores heats altogether.
        var settled = ByHouse(await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),
            new Swim(2, "WATTLE", ResultStatus.Ok, 31.0m),
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 32.0m),
            new Swim(1, "GREVILLEA", ResultStatus.Ok, 33.0m)));

        Assert.Equal(4, settled["BANKSIA"].Total);
        Assert.Equal(4, settled["WATTLE"].Total);
        Assert.Equal(3, settled["MOUNTAIN ASH"].Total);
        Assert.Equal(3, settled["GREVILLEA"].Total);
    }

    // --- The two classifications that must stay indifferent to heats ------

    [Fact]
    public async Task A_social_event_split_across_heats_is_still_flat_per_entry()
    {
        // ADR-0015 §8: a Social event may be split across heats for the same venue
        // reasons as any other, but its heats are LOGISTICS ONLY. Scoring is
        // unchanged from ADR-0014 — flat per-entry points, no placings at all — so
        // there is nothing for a heat to scope, and ADR-0014 is not amended.
        //
        // The times below are deliberately ranked and deliberately ignored: under
        // the places ladder heat 1 alone would pay 4 + 3, and each heat would mint
        // its own winner. Flat scoring pays every entrant the same 1 regardless of
        // heat, time or order, so MOUNTAIN ASH's two entrants earn 2 and nothing else.
        var s = ByHouse(await Score(1, EventTier.Social, scoresPoints: true, MeasureType.Time,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),
            new Swim(1, "WATTLE", ResultStatus.Ok, 31.0m),
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 40.0m),
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 41.0m),
            new Swim(3, "GREVILLEA", ResultStatus.Dns, null)));   // a no-show earns nothing

        Assert.Equal(1, s["BANKSIA"].Total);
        Assert.Equal(1, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
        Assert.Equal(0, s["GREVILLEA"].Total);

        // Flat points are participation, never placement — no heat produced a 1st.
        Assert.Equal(0, s["BANKSIA"].PlacementPoints);
        Assert.Equal(1, s["BANKSIA"].ParticipationPoints);
    }

    [Fact]
    public async Task A_field_event_split_across_flights_pays_one_set_of_placings()
    {
        // ADR-0053 §6 amendment: heats are a Time event's own. Splitting a field
        // for venue reasons is not two competitions, so it pays one 1st — the
        // number the deck badge and the results book both print.
        var s = ByHouse(await Score(1, EventTier.Competitive, scoresPoints: true,
            MeasureType.Distance,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 4.55m),   // 1st of the field → 4
            new Swim(2, "WATTLE", ResultStatus.Ok, 4.10m),      // 2nd → 3
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 3.90m))); // 3rd → 2

        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
    }

    [Fact]
    public async Task An_event_that_scores_no_points_scores_none_however_many_heats_it_has()
    {
        // ADR-0014 §4: ScoresPoints = false drops the event out of scoring
        // entirely. ADR-0015 narrows the placing SCOPE and must not widen the set
        // of things that reach a house total — an excluded event's heats are still
        // excluded, so a heat is never a way back in.
        var standings = await Score(1, EventTier.Competitive, scoresPoints: false, MeasureType.Time,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30.0m),
            new Swim(1, "WATTLE", ResultStatus.Ok, 31.0m),
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 40.0m),
            new Swim(2, "GREVILLEA", ResultStatus.Dnf, null));

        Assert.Equal(AllHousesAtZero, standings.Houses.Select(h => (h.HouseCode, h.Total)).Order());
        Assert.Equal(0, standings.EventsCounted);
        Assert.Equal(0, standings.ResultsCounted);
    }

    [Fact]
    public async Task Non_placing_statuses_and_the_relay_weight_still_apply_per_heat()
    {
        // The ladder is unchanged (ADR-0015 §1: the heat narrows the scope, it
        // does not introduce a second scoring path), so DNF/DNS/DQ and the
        // ADR-0008 relay weight behave inside a heat exactly as they did inside
        // an event.
        var s = ByHouse(await Score(2, // relay weight
            new Swim(1, "BANKSIA", ResultStatus.Ok, 120m),   // heat 1, 1st ×2 → 8
            new Swim(1, "WATTLE", ResultStatus.Dnf, null),     // participation ×2 → 2
            new Swim(2, "MOUNTAIN ASH", ResultStatus.Ok, 130m),  // heat 2, 1st ×2 → 8
            new Swim(2, "GREVILLEA", ResultStatus.Dns, null))); // 0

        Assert.Equal(8, s["BANKSIA"].Total);
        Assert.Equal(2, s["WATTLE"].ParticipationPoints);
        Assert.Equal(8, s["MOUNTAIN ASH"].Total);   // slower, but its own heat's winner
        Assert.Equal(0, s["GREVILLEA"].Total); // DNS scores nothing
    }

    [Fact]
    public async Task A_tie_inside_a_heat_shares_the_higher_place()
    {
        // Standard competition ranking is per heat now, so the skipped place is
        // skipped inside the heat and nowhere else.
        var s = ByHouse(await Score(1,
            new Swim(1, "BANKSIA", ResultStatus.Ok, 30m),
            new Swim(1, "WATTLE", ResultStatus.Ok, 30m),
            new Swim(1, "MOUNTAIN ASH", ResultStatus.Ok, 31m),
            new Swim(2, "GREVILLEA", ResultStatus.Ok, 40m)));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(4, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total); // rank 3, not rank 2
        Assert.Equal(4, s["GREVILLEA"].Total);  // alone in heat 2 → 1st
    }
}
