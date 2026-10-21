using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The EVENT ladder (ADR-0015 §2, built by ADR-0028 §3): the other of ADR-0015's
/// two ladders. What is pinned here is exactly what makes it different from
/// <see cref="Placement"/>'s heat ladder — heats do not scope it — plus the
/// facts its two consumers depend on: ties share a rank, the measure decides the
/// direction, and only a ranked swim is on it at all.
/// </summary>
public sealed class EventLadderTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private Task<IReadOnlyList<EventLadder.LadderDivision>> LadderAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, db => EventLadder.BuildAsync(db, carnivalId, default));

    [Fact]
    public async Task The_event_ladder_ranks_across_heats_and_ignores_the_heat_entirely()
    {
        // Heat 2's swimmer is the fastest in the DIVISION. On the heat ladder she
        // is one of three firsts; on this one she is the only first.
        var seeded = await new RecordsSeed()
            .Definition("FREE", recordCategories: null)
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m, Heat: 1),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 31.8m, Heat: 1),
                new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 30.5m, Heat: 2),
                new RecordsSeed.Swim("Dale Egan", "GREVILLEA", 33.0m, Heat: null))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        Assert.Equal(
            ["Cora Diaz", "Aiden Brook", "Blake Chen", "Dale Egan"],
            division.Standings.Select(s => s.HolderDisplayName));
        Assert.Equal([1, 2, 3, 4], division.Standings.Select(s => s.Rank));
        // A null heat is NOT its own field here, unlike the results book.
        Assert.Equal(4, division.Standings.Count);
    }

    [Fact]
    public async Task Tied_times_share_a_rank_and_the_next_is_skipped()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", recordCategories: null)
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.0m),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 32.0m),
                new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 32.0m),
                new RecordsSeed.Swim("Dale Egan", "GREVILLEA", 33.0m))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        Assert.Equal([1, 2, 2, 4], division.Standings.Select(s => s.Rank));
        Assert.True(division.HigherBetter is false);
    }

    [Theory]
    [InlineData(MeasureType.Distance, "5.25 m")]
    [InlineData(MeasureType.Height, "5.25 m")]
    [InlineData(MeasureType.Count, "5")]
    public async Task A_non_time_measure_ranks_the_other_way(MeasureType measureType, string formatted)
    {
        // Built now rather than deferred: it is one boolean Placement already
        // computes, so an athletics field division ladders correctly the day
        // that sport lands.
        var seeded = await new RecordsSeed()
            .Definition("FIELD", recordCategories: null, measureType: measureType)
            .Division("FIELD", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 4.10m),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 5.25m))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        Assert.True(division.HigherBetter);
        Assert.Equal("Blake Chen", division.Standings[0].HolderDisplayName);
        Assert.Equal(formatted, division.Standings[0].FormattedValue);
    }

    [Fact]
    public async Task A_non_result_status_is_absent_from_the_ladder()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", recordCategories: null)
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m),
                new RecordsSeed.Swim("Dale Egan", "GREVILLEA", null, ResultStatus.Dns),
                new RecordsSeed.Swim("Emery Frost", "WATTLE", null, ResultStatus.Dnf),
                new RecordsSeed.Swim("Finn Gray", "MOUNTAIN ASH", null, ResultStatus.Dq))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        var standing = Assert.Single(division.Standings);
        Assert.Equal("Aiden Brook", standing.HolderDisplayName);
        Assert.Equal("31.20", standing.FormattedValue);
    }

    [Fact]
    public async Task Nm_is_off_the_ladder_and_therefore_never_a_record_candidate()
    {
        // ADR-0052 §4: NM carries no value, so it cannot be ranked and cannot be
        // detected as a record — the same gate every other non-result status meets.
        var seeded = await new RecordsSeed()
            .Definition("SHOT", recordCategories: null, measureType: MeasureType.Distance)
            .Division("SHOT", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 8.25m),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", null, ResultStatus.Nm))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        var standing = Assert.Single(division.Standings);
        Assert.Equal("Aiden Brook", standing.HolderDisplayName);
        Assert.Equal("8.25 m", standing.FormattedValue);
    }

    [Fact]
    public async Task The_event_ladder_breaks_a_tie_on_the_next_best_attempt()
    {
        // ADR-0053 §5: equal best marks, and the second-best splits them. The
        // ladder feeds record detection and the district list, so the countback
        // has to be the same one the deck shows.
        var seeded = await new RecordsSeed()
            .Definition("LONG", recordCategories: null, measureType: MeasureType.Distance, attemptCount: 3)
            .Division("LONG", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 4.32m,
                    AttemptsJson: """[{"value":4.32},{"value":4.10}]"""),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 4.32m,
                    AttemptsJson: """[{"value":4.20},{"value":4.32}]"""),
                new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 4.32m,
                    AttemptsJson: """[{"value":4.32},{"foul":true}]"""))
            .BuildAsync(_factory);

        var division = Assert.Single(await LadderAsync(seeded.CarnivalId));
        Assert.Equal(
            ["Blake Chen", "Aiden Brook", "Cora Diaz"],
            division.Standings.Select(s => s.HolderDisplayName));
        Assert.Equal([1, 2, 3], division.Standings.Select(s => s.Rank));
        // The best mark is what the ladder still PRINTS; only the order moved.
        Assert.All(division.Standings, s => Assert.Equal("4.32 m", s.FormattedValue));
    }

    [Theory]
    // ADR-0052 §1: the ONE server-side formatter, printed in the measure's
    // canonical unit — never the definition's free-text Units.
    [InlineData(MeasureType.Time, "62.34", "1:02.34")]
    [InlineData(MeasureType.Time, "34.5", "34.50")]
    [InlineData(MeasureType.Distance, "10.52", "10.52 m")]
    [InlineData(MeasureType.Distance, "4.3", "4.30 m")]
    [InlineData(MeasureType.Height, "1.35", "1.35 m")]
    [InlineData(MeasureType.Count, "12", "12")]
    [InlineData(MeasureType.Count, "12.000", "12")]
    public void Each_measure_kind_formats_in_its_canonical_unit(
        MeasureType measureType, string value, string expected) =>
        Assert.Equal(expected, EventLadder.FormatValue(measureType,
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task A_relay_team_is_marked_as_one_and_carries_the_team_name()
    {
        var seeded = await new RecordsSeed()
            .Definition("RELAY", recordCategories: null, format: EventFormat.Team)
            .Definition("FREE-SOLO", recordCategories: null)
            .Division("RELAY", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("BANKSIA team", "BANKSIA", 120.5m, Team: true))
            .Division("FREE-SOLO", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        var ladder = await LadderAsync(seeded.CarnivalId);
        var relay = Assert.Single(ladder, d => d.EventCode.StartsWith("RELAY", StringComparison.Ordinal));
        Assert.True(relay.Standings[0].IsTeam);
        Assert.Equal("BANKSIA team", relay.Standings[0].HolderDisplayName);

        var solo = Assert.Single(ladder, d => d.EventCode.StartsWith("FREE-SOLO", StringComparison.Ordinal));
        Assert.False(solo.Standings[0].IsTeam);
        Assert.Equal(7, solo.Standings[0].YearLevel);
    }
}
