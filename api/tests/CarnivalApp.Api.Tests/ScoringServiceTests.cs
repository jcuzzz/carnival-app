using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// House-points scoring rules (owner-ratified 2026-07-30): places 4/3/2/1
/// (4th onward = 1 participation floor), DNF = participation, DNS/DQ = 0, ties
/// share the higher place and skip the next, relays score ×2 (ADR-0008).
/// </summary>
public sealed class ScoringServiceTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    private ScoringService.Standings? _lastStandings;

    public void Dispose() => _factory.Dispose();

    private sealed record R(
        string House, ResultStatus Status, decimal? Value,
        /// <summary>The stored attempt series (ADR-0053 §2); null ranks on the mark alone.</summary>
        string? AttemptsJson = null);

    /// <summary>Seed one Time event of the given weight, add the results, and score.</summary>
    private Task<Dictionary<string, ScoringService.HouseStanding>> Score(
        int scoringWeight, params R[] results) =>
        Score(scoringWeight, MeasureType.Time, results);

    /// <summary>Seed one event of the given weight and measure, add the results, and score.</summary>
    private async Task<Dictionary<string, ScoringService.HouseStanding>> Score(
        int scoringWeight, MeasureType measureType, params R[] results)
    {
        Guid carnivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            var carnival = new Carnival
            {
                Id = Guid.CreateVersion7(),
                Name = "Scoring",
                Type = CarnivalType.Swimming,
                Date = new DateOnly(2027, 2, 25),
                Status = CarnivalStatus.Open,
                RuleVersion = "v1",
                OwnerSubject = "seed",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Carnivals.Add(carnival);
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"EV-{Guid.NewGuid():N}",
                Name = "Event",
                Sport = "Swimming",
                MeasureType = measureType,
                Units = measureType == MeasureType.Time ? "s" : "m",
                ScoringWeight = scoringWeight,
                CreatedAtUtc = DateTimeOffset.UtcNow,
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
            carnivalEvent.AgeGroup = "14";
            db.CarnivalEvents.Add(carnivalEvent);

            var now = DateTimeOffset.UtcNow;
            foreach (var r in results)
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"S-{Guid.NewGuid():N}",
                    Surname = "X",
                    FirstName = "Y",
                    IsActive = true,
                    CreatedAtUtc = now,
                };
                db.Students.Add(student);
                var entry = new Entry
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalEventId = carnivalEvent.Id,
                    StudentId = student.Id,
                    SnapshotDisplayName = "Y X",
                    SnapshotYearLevel = 9,
                    SnapshotHouseCode = r.House,
                    SnapshotAgeGroup = "14",
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = r.Status,
                    MeasuredValue = r.Value,
                    AttemptsJson = r.AttemptsJson,
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
        Assert.True(standings!.Provisional); // Open carnival
        _lastStandings = standings;
        return standings.Houses.ToDictionary(h => h.HouseCode);
    }

    [Fact]
    public async Task Places_score_4_3_2_1_by_time_ascending()
    {
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Ok, 30.00m),
            new R("WATTLE", ResultStatus.Ok, 31.00m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 32.00m),
            new R("GREVILLEA", ResultStatus.Ok, 33.00m));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
        Assert.Equal(1, s["GREVILLEA"].Total);
    }

    [Fact]
    public async Task Fourth_onward_is_the_participation_floor_of_one()
    {
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 31m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 32m),
            new R("GREVILLEA", ResultStatus.Ok, 33m),
            new R("BANKSIA", ResultStatus.Ok, 34m)); // 5th place → 1
        Assert.Equal(4 + 1, s["BANKSIA"].Total); // 1st + 5th
    }

    [Fact]
    public async Task Ties_share_the_higher_place_and_skip_the_next()
    {
        // Two tie for 1st (30.0) → each rank 1 (4 pts); next is rank 3 (2 pts).
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 30m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 31m));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(4, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total); // rank 3, not rank 2
    }

    [Fact]
    public async Task Houses_on_equal_totals_share_a_rank_and_the_next_rank_skips()
    {
        // ADR-0043, owner ruling 2026-09-20: standard competition ranking, 1, 1, 3.
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 30m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 31m));
        Assert.Equal(1, s["BANKSIA"].Rank);
        Assert.Equal(1, s["WATTLE"].Rank);
        Assert.Equal(3, s["MOUNTAIN ASH"].Rank);
    }

    [Fact]
    public async Task Houses_on_separated_totals_rank_one_two_three()
    {
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 31m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 32m));
        Assert.Equal(1, s["BANKSIA"].Rank);
        Assert.Equal(2, s["WATTLE"].Rank);
        Assert.Equal(3, s["MOUNTAIN ASH"].Rank);
    }

    [Fact]
    public async Task Dnf_earns_participation_but_dns_and_dq_earn_nothing()
    {
        var s = await Score(1,
            new R("BANKSIA", ResultStatus.Dnf, null),
            new R("WATTLE", ResultStatus.Dns, null),
            new R("MOUNTAIN ASH", ResultStatus.Dq, null));
        Assert.Equal(1, s["BANKSIA"].Total);
        Assert.Equal(1, s["BANKSIA"].ParticipationPoints);
        Assert.Equal(0, s["WATTLE"].Total);     // DNS scores nothing
        Assert.Equal(0, s["MOUNTAIN ASH"].Total); // DQ scores nothing
    }

    [Fact]
    public async Task A_countback_splits_two_equal_best_marks_and_pays_them_differently()
    {
        // ADR-0053 §5: the same two marks that tie at 4 points each on a time
        // event pay 4 and 3 here, because the second-best attempt separates them.
        var s = await Score(1, MeasureType.Distance,
            new R("BANKSIA", ResultStatus.Ok, 4.32m, """[{"value":4.32},{"value":4.10}]"""),
            new R("WATTLE", ResultStatus.Ok, 4.32m, """[{"value":4.20},{"value":4.32}]"""),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 4.00m, """[{"value":4.00}]"""));
        Assert.Equal(4, s["WATTLE"].Total);
        Assert.Equal(3, s["BANKSIA"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
    }

    [Fact]
    public async Task Series_equal_at_every_position_still_share_the_place()
    {
        var s = await Score(1, MeasureType.Distance,
            new R("BANKSIA", ResultStatus.Ok, 4.32m, """[{"value":4.32},{"value":4.10}]"""),
            new R("WATTLE", ResultStatus.Ok, 4.32m, """[{"value":4.10},{"value":4.32}]"""),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 4.00m, """[{"value":4.00}]"""));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(4, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total); // rank 3, not rank 2
    }

    [Fact]
    public async Task Nm_earns_the_same_participation_as_a_dnf()
    {
        // ADR-0052 §4: the entrant competed and produced no valid mark, which is
        // what DnfPoints already prices — no fourth status rule.
        var s = await Score(1, MeasureType.Distance,
            new R("BANKSIA", ResultStatus.Ok, 4.85m),
            new R("WATTLE", ResultStatus.Nm, null),
            new R("MOUNTAIN ASH", ResultStatus.Dnf, null),
            new R("GREVILLEA", ResultStatus.Dns, null));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(s["MOUNTAIN ASH"].Total, s["WATTLE"].Total);
        Assert.Equal(1, s["WATTLE"].ParticipationPoints);
        Assert.Equal(0, s["WATTLE"].PlacementPoints);
        Assert.Equal(0, s["GREVILLEA"].Total);
    }

    [Fact]
    public async Task Relay_placings_score_double()
    {
        var s = await Score(2, // relay weight
            new R("BANKSIA", ResultStatus.Ok, 120m),
            new R("WATTLE", ResultStatus.Ok, 121m),
            new R("MOUNTAIN ASH", ResultStatus.Dnf, null));
        Assert.Equal(8, s["BANKSIA"].Total);        // 1st × 2
        Assert.Equal(6, s["WATTLE"].Total);           // 2nd × 2
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);       // participation × 2
    }

    [Theory]
    [InlineData(MeasureType.Distance)] // long jump / shot put — metres
    [InlineData(MeasureType.Height)]   // high jump — metres
    [InlineData(MeasureType.Count)]    // e.g. laps swum in a set time
    public async Task Higher_is_better_measures_rank_descending(MeasureType measureType)
    {
        // Athletics is the next sport (ADR-0003 catalogue is sport-agnostic), and
        // every non-Time measure inverts the comparison: the BIGGEST value wins.
        // Ranking these ascending would hand 1st place to the worst performer.
        var s = await Score(1, measureType,
            new R("BANKSIA", ResultStatus.Ok, 4.85m),
            new R("WATTLE", ResultStatus.Ok, 4.20m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 3.95m),
            new R("GREVILLEA", ResultStatus.Ok, 3.10m),
            new R("BANKSIA", ResultStatus.Ok, 2.75m)); // 5th → participation floor
        Assert.Equal(4 + 1, s["BANKSIA"].Total); // longest attempt wins, shortest floors
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
        Assert.Equal(1, s["GREVILLEA"].Total);
    }

    [Fact]
    public async Task Higher_is_better_ties_still_share_the_higher_place()
    {
        var s = await Score(1, MeasureType.Distance,
            new R("BANKSIA", ResultStatus.Ok, 4.20m),
            new R("WATTLE", ResultStatus.Ok, 4.20m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 3.95m));
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(4, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total); // rank 3, not rank 2
    }

    [Theory]
    [InlineData(MeasureType.Time)]
    [InlineData(MeasureType.Distance)]
    public async Task Every_entry_tied_takes_first_place(MeasureType measureType)
    {
        // A whole heat touching on the same time is standard competition ranking
        // taken to its limit: nobody is strictly better, so everyone is rank 1.
        // Places 2..4 are simply never awarded — the engine must not fall back to
        // an ordinal or the scoreboard invents a winner.
        var s = await Score(1, measureType,
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 30m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 30m),
            new R("GREVILLEA", ResultStatus.Ok, 30m));
        Assert.Equal(4, s.Count);
        Assert.All(s.Values, h =>
        {
            Assert.Equal(4, h.Total);
            Assert.Equal(4, h.PlacementPoints);
            Assert.Equal(0, h.ParticipationPoints);
        });
    }

    [Fact]
    public async Task A_carnival_with_no_results_lists_every_configured_house_at_zero_sharing_first()
    {
        // The state every carnival opens in: the board lists the houses the
        // participation card lists, level at 0 and sharing first (ADR-0063).
        var standings = await ScoreScenario(settingsJson: null, results: [], adjustments: []);
        Assert.Equal(["BANKSIA", "GREVILLEA", "MOUNTAIN ASH", "WATTLE"], standings.Houses.Select(h => h.HouseCode));
        Assert.All(standings.Houses, h =>
        {
            Assert.Equal(0, h.Total);
            Assert.Equal(1, h.Rank);
        });
        Assert.Equal(0, standings.EventsCounted);
        Assert.Equal(0, standings.ResultsCounted);
        Assert.True(standings.Provisional);
        Assert.Equal(ScoringRules.Default.Version, standings.RuleVersion);
    }

    [Fact]
    public async Task A_house_no_longer_configured_keeps_its_rows_and_unscored_houses_share_the_last_rank()
    {
        var standings = await ScoreScenario(settingsJson: null,
            [new R("RETIRED", ResultStatus.Ok, 30m), new R("BANKSIA", ResultStatus.Ok, 31m)], []);
        Assert.Equal(
            [("RETIRED", 4, 1), ("BANKSIA", 3, 2), ("GREVILLEA", 0, 3), ("MOUNTAIN ASH", 0, 3), ("WATTLE", 0, 3)],
            standings.Houses.Select(h => (h.HouseCode, h.Total, h.Rank)));
    }

    [Fact]
    public async Task A_finalised_carnival_lists_only_the_houses_that_took_part()
    {
        // GREVILLEA has no entry or adjustment here: in a finalised carnival it reads as a house
        // configured since, not one that scored nothing.
        var standings = await ScoreScenario(settingsJson: null,
            [new R("BANKSIA", ResultStatus.Ok, 30m), new R("WATTLE", ResultStatus.Dns, null)],
            [("MOUNTAIN ASH", 5, true)], CarnivalStatus.Finalised);
        Assert.Equal(
            [("BANKSIA", 4), ("MOUNTAIN ASH", 0), ("WATTLE", 0)],
            standings.Houses.Select(h => (h.HouseCode, h.Total)));
        Assert.False(standings.Provisional);
    }

    [Theory]
    [InlineData(CarnivalStatus.Draft)]
    [InlineData(CarnivalStatus.Open)]
    [InlineData(CarnivalStatus.Closed)]
    public async Task A_carnival_not_yet_finalised_lists_every_configured_house(CarnivalStatus status)
    {
        var standings = await ScoreScenario(settingsJson: null,
            [new R("BANKSIA", ResultStatus.Ok, 30m)], [], status);
        Assert.Equal(
            [("BANKSIA", 4), ("GREVILLEA", 0), ("MOUNTAIN ASH", 0), ("WATTLE", 0)],
            standings.Houses.Select(h => (h.HouseCode, h.Total)));
    }

    [Fact]
    public async Task Standings_name_the_weighted_definitions_and_whether_social_events_exist()
    {
        await Score(2, new R("BANKSIA", ResultStatus.Ok, 120m));
        var weighted = Assert.Single(_lastStandings!.WeightedEvents);
        Assert.Equal(new ScoringService.WeightedEvent("Event", 2), weighted);
        Assert.False(_lastStandings.HasSocialEvents);

        await Score(1, new R("BANKSIA", ResultStatus.Ok, 30m));
        Assert.Empty(_lastStandings!.WeightedEvents);
    }

    [Fact]
    public async Task Unknown_carnival_scores_null()
    {
        using var scope = _factory.Services.CreateScope();
        var scoring = new ScoringService(scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
        Assert.Null(await scoring.ComputeAsync(Guid.NewGuid(), default));
    }

    // --- Per-carnival configurable scoring (ADR-0007) + bonus fold-in (ADR-0005) ---

    /// <summary>Seed a carnival with optional custom SettingsJson, one weighted
    /// event's results and any house-point adjustments, then score it.</summary>
    private async Task<ScoringService.Standings> ScoreScenario(
        string? settingsJson, R[] results, (string House, int Points, bool Voided)[] adjustments,
        CarnivalStatus status = CarnivalStatus.Open)
    {
        Guid carnivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var carnival = new Carnival
            {
                Id = Guid.CreateVersion7(),
                Name = "Configured",
                Type = CarnivalType.Swimming,
                Date = new DateOnly(2027, 2, 25),
                Status = status,
                RuleVersion = "v1",
                OwnerSubject = "seed",
                SettingsJson = settingsJson,
                CreatedAtUtc = now,
            };
            db.Carnivals.Add(carnival);
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"EV-{Guid.NewGuid():N}",
                Name = "Event",
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
            carnivalEvent.AgeGroup = "14";
            db.CarnivalEvents.Add(carnivalEvent);
            foreach (var r in results)
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
                    SnapshotHouseCode = r.House, SnapshotAgeGroup = "14",
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now, CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(), EntryId = entry.Id,
                    Status = r.Status, MeasuredValue = r.Value,
                    LifecycleState = ResultLifecycleState.Provisional, CurrentRevision = 1,
                    RecordedBySubject = "seed", RecordedAtUtc = now, CreatedAtUtc = now,
                });
            }
            foreach (var a in adjustments)
                db.HousePointsAdjustments.Add(new HousePointsAdjustment
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalId = carnival.Id,
                    HouseCode = a.House,
                    Points = a.Points,
                    Category = "Best Dressed House",
                    Reason = "test",
                    RecordedBySubject = "seed",
                    RecordedAtUtc = now,
                    Voided = a.Voided,
                });
            await db.SaveChangesAsync();
            carnivalId = carnival.Id;
        }

        using var readScope = _factory.Services.CreateScope();
        var scoring = new ScoringService(readScope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
        var standings = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(standings);
        return standings!;
    }

    [Fact]
    public async Task Custom_place_points_and_floor_are_honoured()
    {
        // Override: 1st=10, 2nd=6, and 3rd-onward = floor 3.
        var settings = """
            {"scoring":{"version":"house-cup","placePoints":[10,6],"participationFloor":3}}
            """;
        var s = (await ScoreScenario(settings,
        [
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 31m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 32m),
        ], [])).Houses.ToDictionary(h => h.HouseCode);
        Assert.Equal(10, s["BANKSIA"].Total);
        Assert.Equal(6, s["WATTLE"].Total);
        Assert.Equal(3, s["MOUNTAIN ASH"].Total); // 3rd → floor
    }

    [Fact]
    public async Task Custom_rule_version_surfaces_in_standings()
    {
        var settings = """{"scoring":{"version":"house-cup","placePoints":[5]}}""";
        var standings = await ScoreScenario(settings,
            [new R("BANKSIA", ResultStatus.Ok, 30m)], []);
        Assert.Equal("house-cup", standings.RuleVersion);
        Assert.Equal(5, standings.Houses.First().Total);
    }

    [Theory]
    // Negative place points would penalise winning.
    [InlineData("""{"scoring":{"version":"broken","placePoints":[-5,-3,-2]}}""")]
    // A negative floor would dock every 4th-and-later swimmer.
    [InlineData("""{"scoring":{"version":"broken","placePoints":[4,3,2],"participationFloor":-2}}""")]
    // Not JSON at all (the pre-existing tolerant path).
    [InlineData("{ not json")]
    // Structurally valid JSON of the wrong shape — an array, a bare null, a
    // string where the scoring object belongs, a number where the place list
    // belongs. A hand-edited or half-migrated blob must never take the
    // scoreboard down mid-carnival.
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"scoring":"v1"}""")]
    [InlineData("""{"scoring":{"placePoints":7}}""")]
    public async Task Malformed_or_out_of_bounds_stored_settings_score_on_the_defaults(string settingsJson)
    {
        // The blob is applied on READ, so a row written before the bounds check
        // existed (or edited straight in the database) must fall back, not score
        // garbage.
        var standings = await ScoreScenario(settingsJson,
        [
            new R("BANKSIA", ResultStatus.Ok, 30m),
            new R("WATTLE", ResultStatus.Ok, 31m),
            new R("MOUNTAIN ASH", ResultStatus.Ok, 32m),
            new R("GREVILLEA", ResultStatus.Ok, 33m),
        ], []);
        var s = standings.Houses.ToDictionary(h => h.HouseCode);
        Assert.Equal("v1", standings.RuleVersion);
        Assert.Equal(4, s["BANKSIA"].Total);
        Assert.Equal(3, s["WATTLE"].Total);
        Assert.Equal(2, s["MOUNTAIN ASH"].Total);
        Assert.Equal(1, s["GREVILLEA"].Total);
    }

    [Fact]
    public async Task Bonus_adjustments_add_a_separate_visible_component()
    {
        var s = (await ScoreScenario(null,
            [new R("BANKSIA", ResultStatus.Ok, 30m)], // BANKSIA 4 placement
            [
                ("BANKSIA", 10, false),  // +10 bonus
                ("WATTLE", -5, false),     // deduction, WATTLE has no results
                ("BANKSIA", 99, true),   // voided → ignored
            ])).Houses.ToDictionary(x => x.HouseCode, x => x);
        Assert.Equal(14, s["BANKSIA"].Total);      // 4 placement + 10 bonus
        Assert.Equal(4, s["BANKSIA"].PlacementPoints);
        Assert.Equal(10, s["BANKSIA"].BonusPoints);
        Assert.Equal(-5, s["WATTLE"].Total);         // bonus-only house appears, may be negative
        Assert.Equal(-5, s["WATTLE"].BonusPoints);
    }
}

/// <summary>
/// <see cref="CarnivalSettings.Parse"/> applies stored blobs at scoring time, so
/// it owns the same bounds the settings endpoint enforces on write: valid
/// overrides apply, anything out of bounds falls back whole to the ratified
/// defaults (tolerant, never throwing).
/// </summary>
public sealed class CarnivalSettingsParseTests
{
    [Fact]
    public void Valid_overrides_apply()
    {
        var settings = CarnivalSettings.Parse(
            """{"scoring":{"version":"house-cup","placePoints":[10,6],"participationFloor":3,"dnfPoints":2}}""");
        Assert.Equal("house-cup", settings.Scoring.Version);
        Assert.Equal([10, 6], settings.Scoring.PlacePoints);
        Assert.Equal(3, settings.Scoring.ParticipationFloor);
        Assert.Equal(2, settings.Scoring.DnfPoints);
    }

    [Theory]
    [InlineData("""{"scoring":{"placePoints":[4,-1,2]}}""")]
    [InlineData("""{"scoring":{"participationFloor":-1}}""")]
    [InlineData("""{"scoring":{"dnfPoints":-1}}""")]
    [InlineData("""{"scoring":{"dnsPoints":-1}}""")]
    [InlineData("""{"scoring":{"dqPoints":-1}}""")]
    // ADR-0014 §4: the social per-entry award is an award like the others, so a
    // negative value would dock a house for turning up.
    [InlineData("""{"scoring":{"socialPerEntryPoints":-1}}""")]
    public void Out_of_bounds_scoring_falls_back_to_the_ratified_defaults(string json)
    {
        var scoring = CarnivalSettings.Parse(json).Scoring;
        Assert.Equal(ScoringRules.Default.Version, scoring.Version);
        Assert.Equal(ScoringRules.Default.PlacePoints, scoring.PlacePoints);
        Assert.Equal(ScoringRules.Default.ParticipationFloor, scoring.ParticipationFloor);
        Assert.Equal(ScoringRules.Default.DnfPoints, scoring.DnfPoints);
        Assert.Equal(ScoringRules.Default.DnsPoints, scoring.DnsPoints);
        Assert.Equal(ScoringRules.Default.DqPoints, scoring.DqPoints);
        Assert.Equal(ScoringRules.Default.SocialPerEntryPoints, scoring.SocialPerEntryPoints);
    }

    [Fact]
    public void Social_per_entry_points_default_to_one_and_survive_a_round_trip()
    {
        // The default is the owner's "generally 1 per entry"; a blob written
        // before the field existed must read as that default rather than zero,
        // or every social event in an existing carnival would silently stop
        // scoring.
        Assert.Equal(1, CarnivalSettings.Parse("""{"scoring":{"version":"v1"}}""").Scoring.SocialPerEntryPoints);
        Assert.Equal(1, ScoringRules.Default.SocialPerEntryPoints);

        var serialised = new CarnivalSettings
        {
            Scoring = new ScoringRules { Version = "social", SocialPerEntryPoints = 5 },
        }.Serialise();
        Assert.Equal(5, CarnivalSettings.Parse(serialised).Scoring.SocialPerEntryPoints);
    }

    [Fact]
    public void Zero_social_per_entry_points_are_a_legitimate_configuration()
    {
        // Zero is "social events do not score", which is a choice a school may
        // make; only a NEGATIVE value is out of bounds.
        Assert.Equal(0,
            CarnivalSettings.Parse("""{"scoring":{"version":"c","socialPerEntryPoints":0}}""")
                .Scoring.SocialPerEntryPoints);
    }

    [Fact]
    public void Lane_count_is_absent_by_default_and_survives_a_round_trip()
    {
        // ADR-0015 §6. Null means "not configured" — never zero, and never a
        // guessed pool: an invented default that later refuses a valid lane is
        // worse than no limit at all.
        Assert.Null(CarnivalSettings.Parse(null).LaneCount);
        Assert.Null(CarnivalSettings.Parse("""{"scoring":{"version":"v1"}}""").LaneCount);
        Assert.Equal(8, CarnivalSettings.Parse("""{"laneCount":8}""").LaneCount);
        Assert.Equal(6, CarnivalSettings.Parse(
            new CarnivalSettings { LaneCount = 6 }.Serialise()).LaneCount);
    }

    [Theory]
    [InlineData("""{"laneCount":0}""")]
    [InlineData("""{"laneCount":-2}""")]
    public void An_impossible_stored_lane_count_reads_as_unconfigured(string json)
    {
        // Same tolerance the scoring block gets: a hand-edited or half-migrated
        // blob degrades to "no configured limit" rather than becoming a limit
        // that refuses every lane.
        var settings = CarnivalSettings.Parse(json);
        Assert.Null(settings.LaneCount);
        Assert.Equal(ScoringRules.Default.PlacePoints, settings.Scoring.PlacePoints);
    }

    [Fact]
    public void A_bad_scoring_section_does_not_discard_the_adjustment_categories()
    {
        var settings = CarnivalSettings.Parse(
            """{"scoring":{"placePoints":[-1]},"adjustmentCategories":["Spirit"]}""");
        Assert.Equal(ScoringRules.Default.PlacePoints, settings.Scoring.PlacePoints);
        Assert.Equal(["Spirit"], settings.AdjustmentCategories);
    }
}
