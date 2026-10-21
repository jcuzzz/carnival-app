using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0053 §2/§5: the stored attempt series, the two values the server derives
/// from it (best mark and status), and the countback key it produces. The
/// countback rules are pinned here rather than only end-to-end because
/// <see cref="Placement.Rank"/> is the ONE ranking implementation and every
/// swimming result now flows through the widened signature.
/// </summary>
public sealed class AttemptSeriesTests
{
    private static string Serialise(params AttemptDto?[] attempts) =>
        AttemptSeries.Serialise(attempts)!;

    [Fact]
    public void The_stored_shape_is_marks_fouls_and_untaken_ordinals()
    {
        var json = Serialise(new AttemptDto(4.32m, false), new AttemptDto(null, true), null);
        // A trailing untaken ordinal carries nothing and is not stored; a GAP is.
        Assert.Equal("""[{"value":4.32},{"foul":true}]""", json);

        var round3 = AttemptSeries.Upsert(AttemptSeries.Parse(json), 4, 4.10m, foul: false);
        Assert.Equal("""[{"value":4.32},{"foul":true},null,{"value":4.10}]""",
            AttemptSeries.Serialise(round3));
    }

    [Fact]
    public void An_empty_series_stores_as_null_rather_than_an_empty_array()
    {
        Assert.Null(AttemptSeries.Serialise([null, null]));
        Assert.Empty(AttemptSeries.Parse(null));
        // A blob nothing can read degrades to no series rather than 500-ing a read.
        Assert.Empty(AttemptSeries.Parse("{ not json"));
    }

    [Fact]
    public void Best_and_status_are_derived_from_the_marks_that_are_valid()
    {
        var mixed = AttemptSeries.Parse(
            Serialise(new AttemptDto(4.32m, false), new AttemptDto(null, true),
                new AttemptDto(4.55m, false)));
        Assert.Equal(4.55m, AttemptSeries.Best(mixed, higherBetter: true));
        Assert.Equal(ResultStatus.Ok, AttemptSeries.DeriveStatus(mixed));

        var allFouled = AttemptSeries.Parse(
            Serialise(new AttemptDto(null, true), new AttemptDto(null, true)));
        Assert.Null(AttemptSeries.Best(allFouled, higherBetter: true));
        Assert.Equal(ResultStatus.Nm, AttemptSeries.DeriveStatus(allFouled));
    }

    [Fact]
    public void The_countback_key_is_the_valid_marks_best_first()
    {
        var json = Serialise(
            new AttemptDto(4.10m, false), new AttemptDto(null, true), new AttemptDto(4.32m, false));
        Assert.Equal([4.32m, 4.10m], AttemptSeries.KeyOf(json, higherBetter: true));
        // Lower-is-better reverses it; a series of fouls yields no key at all.
        Assert.Equal([4.10m, 4.32m], AttemptSeries.KeyOf(json, higherBetter: false));
        Assert.Null(AttemptSeries.KeyOf(Serialise(new AttemptDto(null, true)), higherBetter: true));
    }

    // ---- Countback (§5) ---------------------------------------------------

    [Fact]
    public void A_tie_on_the_best_mark_breaks_on_the_next_best_attempt()
    {
        IReadOnlyList<IReadOnlyList<decimal>> keys =
        [
            [4.32m, 4.10m],
            [4.32m, 4.20m],
            [4.30m, 4.29m],
        ];
        Assert.Equal([2, 1, 3], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void Series_equal_at_every_position_tie_and_the_next_place_is_skipped()
    {
        IReadOnlyList<IReadOnlyList<decimal>> keys =
        [
            [4.32m, 4.10m],
            [4.32m, 4.10m],
            [4.00m],
        ];
        Assert.Equal([1, 1, 3], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void A_remaining_valid_mark_beats_none_where_one_key_runs_out()
    {
        IReadOnlyList<IReadOnlyList<decimal>> keys = [[4.32m], [4.32m, 3.90m]];
        Assert.Equal([2, 1], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void Single_mark_keys_rank_exactly_as_bare_values_always_have()
    {
        decimal[] times = [31.2m, 30.5m, 31.2m, 33.0m];
        Assert.Equal([2, 1, 2, 4], Placement.Rank(times, higherBetter: false));
    }
}
