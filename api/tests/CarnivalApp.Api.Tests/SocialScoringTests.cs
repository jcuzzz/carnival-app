using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The ADR-0014 §4 scoring classifications, on top of the ratified place-points
/// engine (ScoringServiceTests) rather than instead of it.
///
/// Two owner rules are being pinned. SOCIAL scoring makes the scored unit the
/// ENTRY, not the result: a social swim is run to get everyone in the water, so
/// it earns its house a flat point with no times taken and no ranking — the one
/// exception being a DNS, because a no-show did not take part. And SCORESPOINTS
/// is an orthogonal exclusion: an event marked not to score contributes nothing
/// at all, whatever its tier and whatever was recorded on it, which is how a
/// washed-out or demonstration event is kept off the scoreboard without deleting
/// anybody's entries or results.
/// </summary>
public sealed class SocialScoringTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly IEnumerable<(string, int)> AllHousesAtZero =
        Fixtures.Houses.Select(h => (h, 0)).Order().ToList();

    /// <summary>One entry: its house, and the result (if any) recorded against it.</summary>
    private sealed record En(string House, ResultStatus? Status = null, decimal? Value = null);

    /// <summary>One scheduled event with its ADR-0014 classification and its entries.</summary>
    private sealed record Ev(
        EventTier Tier,
        EventScoringMode? Mode,
        bool ScoresPoints,
        int Weight,
        params En[] Entries);

    private static Ev Social(params En[] entries) =>
        new(EventTier.Social, null, true, 1, entries);

    private static Ev Competitive(params En[] entries) =>
        new(EventTier.Competitive, null, true, 1, entries);

    /// <summary>Seed a carnival of classified events and score it.</summary>
    private async Task<ScoringService.Standings> ScoreAsync(string? settingsJson, params Ev[] events)
    {
        Guid carnivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            var now = DateTimeOffset.UtcNow;
            var carnival = new Carnival
            {
                Id = Guid.CreateVersion7(),
                Name = "Social",
                Type = CarnivalType.Swimming,
                Date = new DateOnly(2027, 2, 25),
                Status = CarnivalStatus.Open,
                RuleVersion = "v1",
                OwnerSubject = "seed",
                SettingsJson = settingsJson,
                CreatedAtUtc = now,
            };
            db.Carnivals.Add(carnival);

            foreach (var ev in events)
            {
                var definition = new EventDefinition
                {
                    Id = Guid.CreateVersion7(),
                    Code = $"EV-{Guid.NewGuid():N}",
                    Name = "Event",
                    Sport = "Swimming",
                    MeasureType = MeasureType.Time,
                    Units = "s",
                    ScoringWeight = ev.Weight,
                    CreatedAtUtc = now,
                };
                db.EventDefinitions.Add(definition);
                var carnivalEvent = new CarnivalEvent
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalId = carnival.Id,
                    EventDefinitionId = definition.Id,
                    Category = CompetitionCategory.M,
                    Tier = ev.Tier,
                    ScoringMode = ev.Mode,
                    ScoresPoints = ev.ScoresPoints,
                    Status = CarnivalEventStatus.Open,
                };
                carnivalEvent.AgeGroup = "14";
                db.CarnivalEvents.Add(carnivalEvent);

                foreach (var en in ev.Entries)
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
                        SnapshotDisplayName = "Y X", SnapshotYearLevel = 9,
                        SnapshotHouseCode = en.House, SnapshotAgeGroup = "14",
                        SnapshotCategory = CompetitionCategory.M,
                        CreatedAtUtc = now, CreatedBySubject = "seed",
                    };
                    db.Entries.Add(entry);
                    // A null status is an entry with NO result at all — the
                    // ordinary state of a social event, which is never timed.
                    if (en.Status is { } status)
                        db.Results.Add(new Result
                        {
                            Id = Guid.CreateVersion7(), EntryId = entry.Id,
                            Status = status, MeasuredValue = en.Value,
                            LifecycleState = ResultLifecycleState.Provisional, CurrentRevision = 1,
                            RecordedBySubject = "seed", RecordedAtUtc = now, CreatedAtUtc = now,
                        });
                }
            }

            await db.SaveChangesAsync();
            carnivalId = carnival.Id;
        }

        using var readScope = _factory.Services.CreateScope();
        var scoring = new ScoringService(readScope.ServiceProvider.GetRequiredService<SportsDbContext>());
        var standings = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(standings);
        return standings!;
    }

    private static Dictionary<string, ScoringService.HouseStanding> Houses(ScoringService.Standings s) =>
        s.Houses.ToDictionary(h => h.HouseCode);

    // ---- Social scoring --------------------------------------------------

    [Fact]
    public async Task A_social_event_scores_a_flat_point_per_entry_with_no_results_recorded()
    {
        // The defining case: a social event is run without a stopwatch, so a
        // scoreboard that waited for results would never move for it.
        var standings = await ScoreAsync(null,
            Social(new En("BANKSIA"), new En("BANKSIA"), new En("WATTLE")));
        var s = Houses(standings);

        Assert.Equal(2, s["BANKSIA"].Total);
        Assert.Equal(1, s["WATTLE"].Total);
        // Folded into the existing participation component — the standings
        // document keeps its three columns (StandingsRegressionTests pins it).
        Assert.Equal(2, s["BANKSIA"].ParticipationPoints);
        Assert.Equal(0, s["BANKSIA"].PlacementPoints);
        Assert.Equal(1, standings.EventsCounted);
        Assert.Equal(3, standings.ResultsCounted);
    }

    [Fact]
    public async Task A_no_show_earns_nothing_while_every_other_result_state_still_earns_the_flat_point()
    {
        // Owner decision: a DNS did not take part, so the participation point it
        // would otherwise get is exactly what social scoring must withhold. A
        // DNF, a DQ and an ordinary time all still count — the social point is
        // for turning up, not for placing.
        var standings = await ScoreAsync(null, Social(
            new En("BANKSIA", ResultStatus.Dns),
            new En("WATTLE", ResultStatus.Dnf),
            new En("MOUNTAIN ASH", ResultStatus.Dq),
            new En("GREVILLEA", ResultStatus.Ok, 30m)));
        var s = Houses(standings);

        Assert.Equal(0, s["BANKSIA"].Total); // the no-show scores nothing at all
        Assert.Equal(1, s["WATTLE"].Total);
        Assert.Equal(1, s["MOUNTAIN ASH"].Total);
        Assert.Equal(1, s["GREVILLEA"].Total);
        // A time on a social event is still not a placing: nobody is ranked.
        Assert.All(s.Values, h => Assert.Equal(0, h.PlacementPoints));
    }

    [Fact]
    public async Task The_event_weight_never_multiplies_social_points()
    {
        // A social relay is still one point per entry: the points are flat by
        // definition, so the ADR-0008 relay doubling deliberately does not apply.
        var standings = await ScoreAsync(null,
            new Ev(EventTier.Social, null, true, 2, new En("BANKSIA"), new En("WATTLE")));
        Assert.Equal(1, Houses(standings)["BANKSIA"].Total);
    }

    [Fact]
    public async Task A_social_event_with_no_entries_is_not_counted()
    {
        // Scheduled but unentered: the board must not claim an event was run.
        var standings = await ScoreAsync(null, Social());
        Assert.Equal(AllHousesAtZero, standings.Houses.Select(h => (h.HouseCode, h.Total)).Order());
        Assert.Equal(0, standings.EventsCounted);
        // Still a Social event, so the legend owes its per-entry points.
        Assert.True(standings.HasSocialEvents);
    }

    [Fact]
    public async Task An_all_no_show_social_event_is_not_counted_either()
    {
        var standings = await ScoreAsync(null,
            Social(new En("BANKSIA", ResultStatus.Dns), new En("WATTLE", ResultStatus.Dns)));
        Assert.Equal(AllHousesAtZero, standings.Houses.Select(h => (h.HouseCode, h.Total)).Order());
        Assert.Equal(0, standings.EventsCounted);
    }

    [Fact]
    public async Task Social_per_entry_points_are_configurable_per_carnival()
    {
        // ADR-0014 §4: "generally 1 per entry, customisable per carnival" — a
        // school that wants social swims to matter more says so in settings,
        // never in code.
        var standings = await ScoreAsync("""{"scoring":{"version":"social-3","socialPerEntryPoints":3}}""",
            Social(new En("BANKSIA"), new En("BANKSIA"), new En("WATTLE")));
        var s = Houses(standings);
        Assert.Equal(6, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal("social-3", standings.RuleVersion);
    }

    [Fact]
    public async Task Zero_social_points_configured_turns_social_events_into_pure_participation()
    {
        // A school that wants the C/S split for the PROGRAM but not for the
        // scoreboard sets the value to zero; nothing is scored and no event is
        // counted.
        var standings = await ScoreAsync("""{"scoring":{"socialPerEntryPoints":0}}""",
            Social(new En("BANKSIA"), new En("WATTLE")));
        Assert.Equal(AllHousesAtZero, standings.Houses.Select(h => (h.HouseCode, h.Total)).Order());
        Assert.Equal(0, standings.EventsCounted);
    }

    // ---- Mode overrides --------------------------------------------------

    [Fact]
    public async Task An_explicit_social_mode_overrides_the_competitive_tier()
    {
        // The tier is what the printed program says; the mode is how it scores.
        // A Competitive event scored socially still ranks nobody, even though
        // every entry carries a time.
        var standings = await ScoreAsync(null,
            new Ev(EventTier.Competitive, EventScoringMode.Social, true, 1,
                new En("BANKSIA", ResultStatus.Ok, 30m),
                new En("WATTLE", ResultStatus.Ok, 31m)));
        var s = Houses(standings);
        Assert.Equal(1, s["BANKSIA"].Total); // not 4 — no placings were awarded
        Assert.Equal(1, s["WATTLE"].Total);    // not 3
        Assert.All(s.Values, h => Assert.Equal(0, h.PlacementPoints));
    }

    [Fact]
    public async Task An_explicit_places_mode_overrides_the_social_tier()
    {
        // The other direction: a Social-tier event a school does place and score
        // normally (the house novelty race everyone actually races).
        var standings = await ScoreAsync(null,
            new Ev(EventTier.Social, EventScoringMode.Places, true, 1,
                new En("BANKSIA", ResultStatus.Ok, 30m),
                new En("WATTLE", ResultStatus.Ok, 31m),
                new En("MOUNTAIN ASH", ResultStatus.Dns)));
        var s = Houses(standings);
        Assert.Equal(4, s["BANKSIA"].PlacementPoints);
        Assert.Equal(3, s["WATTLE"].PlacementPoints);
        Assert.Equal(0, s["MOUNTAIN ASH"].Total); // DNS on the places table is 0
    }

    // ---- The points exclusion --------------------------------------------

    [Theory]
    [InlineData(EventTier.Competitive)]
    [InlineData(EventTier.Social)]
    public async Task An_event_that_does_not_score_points_contributes_nothing(EventTier tier)
    {
        // Orthogonal to tier and mode: the results still exist, are still
        // verifiable and still count for school records — they simply never
        // reach a house total.
        var standings = await ScoreAsync(null,
            new Ev(tier, null, ScoresPoints: false, 1,
                new En("BANKSIA", ResultStatus.Ok, 30m),
                new En("WATTLE", ResultStatus.Ok, 31m),
                new En("MOUNTAIN ASH", ResultStatus.Dnf)));
        Assert.Equal(AllHousesAtZero, standings.Houses.Select(h => (h.HouseCode, h.Total)).Order());
        Assert.Equal(0, standings.EventsCounted);
        Assert.Equal(0, standings.ResultsCounted);
        Assert.False(standings.HasSocialEvents);
    }

    [Fact]
    public async Task Excluding_one_event_leaves_the_rest_of_the_board_untouched()
    {
        // The exclusion is per event, so the carnival keeps scoring around it.
        var standings = await ScoreAsync(null,
            Competitive(new En("BANKSIA", ResultStatus.Ok, 30m), new En("WATTLE", ResultStatus.Ok, 31m)),
            new Ev(EventTier.Competitive, null, ScoresPoints: false, 1,
                new En("WATTLE", ResultStatus.Ok, 20m)),
            Social(new En("MOUNTAIN ASH")));
        var s = Houses(standings);
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total); // the excluded event's win adds nothing
        Assert.Equal(1, s["MOUNTAIN ASH"].Total);
        Assert.Equal(2, standings.EventsCounted); // the excluded event is not one of them
    }

    [Fact]
    public async Task Competitive_and_social_totals_add_up_on_one_board()
    {
        // The end state a school actually sees: both tiers feed one set of house
        // totals, with the social contribution visible as participation.
        var standings = await ScoreAsync(null,
            Competitive(
                new En("BANKSIA", ResultStatus.Ok, 30m),
                new En("WATTLE", ResultStatus.Ok, 31m)),
            Social(new En("WATTLE"), new En("WATTLE"), new En("BANKSIA")));
        var s = Houses(standings);
        Assert.Equal(5, s["BANKSIA"].Total); // 4 placement + 1 social
        Assert.Equal(5, s["WATTLE"].Total);    // 3 placement + 2 social
        Assert.Equal(2, s["WATTLE"].ParticipationPoints);
        Assert.Equal(2, standings.EventsCounted);
    }
}
