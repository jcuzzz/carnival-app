using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0056 §1-§3: the stored high-jump card, everything the server derives from
/// it, and the one write that appends a mark. Pinned here rather than only
/// end-to-end because the derivation decides house points — the countback is the
/// normal case in high jump, not an edge one — and because the capture PUT and
/// the correction replay share <see cref="JumpCard.TryAppend"/>, so a rule that
/// drifted would drift in both at once.
/// </summary>
public sealed class JumpCardTests
{
    private static List<JumpDto> Card(params (string Height, string Marks)[] heights) =>
        [.. heights.Select(h => new JumpDto(Height(h.Height), h.Marks))];

    private static decimal Height(string metres) =>
        decimal.Parse(metres, System.Globalization.CultureInfo.InvariantCulture);

    private static readonly decimal[] Ladder = [1.20m, 1.25m, 1.30m, 1.35m, 1.40m];

    // ---- Shape ------------------------------------------------------------

    [Fact]
    public void The_card_and_the_ordinal_series_share_a_column_and_are_told_apart_by_shape()
    {
        var card = JumpCard.Serialise(Card(("1.25", "O"), ("1.30", "XXO")));
        Assert.Equal("""[{"height":1.25,"marks":"O"},{"height":1.30,"marks":"XXO"}]""", card);
        Assert.True(JumpCard.TryParse(card, out _));

        // An ADR-0053 series is not a card, and must not parse as one — nor the
        // other way round, or every height would read as an untaken ordinal.
        var series = """[{"value":4.32},{"foul":true}]""";
        Assert.False(JumpCard.TryParse(series, out _));
        Assert.Empty(JumpCard.Parse(series));
        Assert.Empty(AttemptSeries.Parse(card));
        Assert.Equal(2, AttemptSeries.Parse(series).Count);
    }

    [Fact]
    public void Every_writer_serialises_a_height_at_two_decimal_places()
    {
        // A decimal is written at its own scale, so the editor's 1.2 and the
        // template's 1.20 would be different JSON text for one bar and the
        // template reconciler would read drift where there is none.
        Assert.Equal("""{"start":1.20,"step":0.05}""",
            JumpCard.SerialiseLadder(new LadderDto(1.2m, 0.05m)));
        Assert.Equal("[1.20,1.40]", JumpCard.SerialiseHeights([1.2m, 1.4m]));
        Assert.Equal("""[{"height":1.40,"marks":"O"}]""", JumpCard.Serialise(Card(("1.4", "O"))));
    }

    [Fact]
    public void An_empty_card_stores_as_null_and_an_unreadable_blob_degrades_to_nothing()
    {
        Assert.Null(JumpCard.Serialise([]));
        Assert.Empty(JumpCard.Parse(null));
        Assert.Empty(JumpCard.Parse("{ not json"));
        Assert.False(JumpCard.TryParse("[]", out _));
        // A height carrying no marks is not a card: Best would throw on it.
        Assert.False(JumpCard.TryParse("""[{"height":1.20,"marks":"O"},{"height":1.25}]""", out _));
        Assert.Null(JumpCard.ParseLadder("{ not json"));
        Assert.Empty(JumpCard.ParseHeights("nonsense"));
    }

    [Fact]
    public void Heights_and_the_ladder_round_trip_as_the_wire_spells_them()
    {
        Assert.Equal("[1.20,1.25,1.30]", JumpCard.SerialiseHeights([1.30m, 1.20m, 1.25m]));
        Assert.Equal([1.20m, 1.25m, 1.30m], JumpCard.ParseHeights("[1.30,1.20,1.25]"));
        Assert.Equal("""{"start":1.20,"step":0.05}""", JumpCard.SerialiseLadder(new LadderDto(1.20m, 0.05m)));
        Assert.Equal(new LadderDto(1.20m, 0.05m), JumpCard.ParseLadder("""{"start":1.2,"step":0.05}"""));
    }

    // ---- Grammar (§2) ------------------------------------------------------

    [Theory]
    [InlineData("X")]
    [InlineData("XX")]
    [InlineData("XXX")]
    [InlineData("O")]
    [InlineData("XO")]
    [InlineData("XXO")]
    [InlineData("-")]
    [InlineData("X-")]
    [InlineData("XX-")]
    public void The_nine_legal_mark_strings_are_accepted(string marks) =>
        Assert.True(JumpCard.IsValidMarks(marks, 3));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("XXXX")]   // four marks at one height
    [InlineData("OX")]     // a clearance ends the height
    [InlineData("-X")]     // so does a pass
    [InlineData("OO")]
    [InlineData("O-")]
    [InlineData("x")]      // the notation is upper case
    [InlineData("P")]
    [InlineData("X X")]
    public void Anything_else_is_not_a_set_of_marks(string? marks) =>
        Assert.False(JumpCard.IsValidMarks(marks, 3));

    // ---- Derivation (§2) ---------------------------------------------------

    [Fact]
    public void Best_is_the_highest_clearance_and_the_fails_are_counted_around_it()
    {
        var card = Card(("1.20", "O"), ("1.25", "XO"), ("1.30", "XXO"), ("1.35", "XX"));
        Assert.Equal(1.30m, JumpCard.Best(card));
        Assert.Equal(2, JumpCard.FailsAtBest(card));
        // Five X on the card; the two at 1.35, above the best, are not countable.
        Assert.Equal(3, JumpCard.CountbackFails(card));
        Assert.Equal(ResultStatus.Ok, JumpCard.DeriveStatus(card));
    }

    [Fact]
    public void A_card_with_no_clearance_is_provisionally_nm_with_no_value()
    {
        var card = Card(("1.20", "XX"));
        Assert.Null(JumpCard.Best(card));
        Assert.Null(JumpCard.FailsAtBest(card));
        Assert.Equal(0, JumpCard.CountbackFails(card));
        Assert.Equal(ResultStatus.Nm, JumpCard.DeriveStatus(card));
        Assert.Null(JumpCard.Key(card));
    }

    [Fact]
    public void Three_consecutive_failures_eliminate_across_heights()
    {
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XXX")), 3));
        // The run crosses the bar: two failures at one height, one at the next.
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XX"), ("1.25", "X")), 3));
    }

    [Fact]
    public void A_pass_never_counts_and_a_clearance_breaks_the_run()
    {
        // "-" is removed before the run is read, so it neither fails nor rescues.
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XX-"), ("1.25", "X")), 3));
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XX"), ("1.25", "-"), ("1.30", "X")), 3));
        Assert.False(JumpCard.IsEliminated(Card(("1.20", "-"), ("1.25", "XX")), 3));
        // An O sits between the failures, so the run starts again after it.
        Assert.False(JumpCard.IsEliminated(Card(("1.20", "XXO"), ("1.25", "XX")), 3));
        // …but an O AFTER the third failure does not rescue it: the athlete was
        // already out, which is why TryAppend would never have accepted that O.
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XX"), ("1.25", "XO")), 3));
    }

    // ---- Countback (§3) ----------------------------------------------------

    [Fact]
    public void The_key_is_best_then_fewest_fails_at_it_then_fewest_overall()
    {
        var cleanest = Card(("1.30", "O"));
        var oneFail = Card(("1.25", "O"), ("1.30", "XO"));
        var sameAtBestMoreOverall = Card(("1.25", "XO"), ("1.30", "XO"));
        var lowerBest = Card(("1.25", "O"));

        Assert.Equal([1.30m, 0m, 0m], JumpCard.Key(cleanest));
        Assert.Equal([1.30m, -1m, -1m], JumpCard.Key(oneFail));
        Assert.Equal([1.30m, -1m, -2m], JumpCard.Key(sameAtBestMoreOverall));

        IReadOnlyList<IReadOnlyList<decimal>> keys =
        [
            JumpCard.Key(cleanest)!, JumpCard.Key(oneFail)!,
            JumpCard.Key(sameAtBestMoreOverall)!, JumpCard.Key(lowerBest)!,
        ];
        Assert.Equal([1, 2, 3, 4], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void Cards_equal_on_all_three_numbers_tie_and_the_next_place_is_skipped()
    {
        IReadOnlyList<IReadOnlyList<decimal>> keys =
        [
            JumpCard.Key(Card(("1.25", "XO"), ("1.30", "XO")))!,
            JumpCard.Key(Card(("1.25", "XO"), ("1.30", "XO")))!,
            JumpCard.Key(Card(("1.25", "O")))!,
        ];
        Assert.Equal([1, 1, 3], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void The_bar_an_athlete_went_out_on_costs_nothing_in_the_countback()
    {
        // ADR-0056 amendment 2026-09-20: A contested 1.30 and failed it out;
        // B stopped at 1.25. A's three failures above the best are not countable,
        // so the athlete who tried the next bar is not placed behind the one who did not.
        var contestedTheNextBar = Card(("1.20", "O"), ("1.25", "O"), ("1.30", "XXX"));
        var stoppedAtTheBest = Card(("1.20", "XO"), ("1.25", "O"));

        Assert.Equal(0, JumpCard.CountbackFails(contestedTheNextBar));
        Assert.Equal(1, JumpCard.CountbackFails(stoppedAtTheBest));
        Assert.Equal([1.25m, 0m, 0m], JumpCard.Key(contestedTheNextBar));
        Assert.Equal([1.25m, 0m, -1m], JumpCard.Key(stoppedAtTheBest));

        IReadOnlyList<IReadOnlyList<decimal>> keys =
            [JumpCard.Key(contestedTheNextBar)!, JumpCard.Key(stoppedAtTheBest)!];
        Assert.Equal([1, 2], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void Two_athletes_eliminated_at_the_same_next_bar_still_tie()
    {
        // Identical up to the best; both then went out at 1.30 on different
        // numbers of failures, which the countback no longer reads.
        IReadOnlyList<IReadOnlyList<decimal>> keys =
        [
            JumpCard.Key(Card(("1.20", "XO"), ("1.25", "O"), ("1.30", "XXX")))!,
            JumpCard.Key(Card(("1.20", "XO"), ("1.25", "O"), ("1.30", "X")))!,
        ];
        Assert.Equal([1, 1], Placement.Rank(keys, higherBetter: true));
    }

    [Fact]
    public void An_athlete_out_at_the_opening_height_has_no_countable_failure()
    {
        var card = Card(("1.20", "XXX"));
        Assert.Null(JumpCard.Best(card));
        Assert.Equal(0, JumpCard.CountbackFails(card));
        Assert.True(JumpCard.IsEliminated(card, 3));
        // No clearance, so the card does not rank at all — the third number never decides it.
        Assert.Null(JumpCard.Key(card));
        Assert.Equal(ResultStatus.Nm, JumpCard.DeriveStatus(card));
    }

    [Fact]
    public void The_stored_card_ranks_through_the_shared_countback_key()
    {
        // AttemptSeries.KeyOf is what the four projections call; a card must come
        // back out of it as the card's key, not as an empty series.
        var json = JumpCard.Serialise(Card(("1.25", "XO"), ("1.30", "O")))!;
        Assert.Equal([1.30m, 0m, -1m], AttemptSeries.KeyOf(json, higherBetter: true));
    }

    // ---- The one write (§4) ------------------------------------------------

    private static (JumpRefusal? Refusal, List<JumpDto> Card) Append(
        IReadOnlyList<JumpDto> card, decimal height, string? mark, int attemptsPerHeight = 3) =>
        (JumpCard.TryAppend(card, Ladder, attemptsPerHeight, height, mark, out var updated), updated);

    [Fact]
    public void A_mark_appends_to_its_height_and_creates_the_entry_when_absent()
    {
        var (first, card) = Append([], 1.25m, "X");
        Assert.Null(first);
        Assert.Equal(Card(("1.25", "X")), card);

        var (second, appended) = Append(card, 1.25m, "O");
        Assert.Null(second);
        Assert.Equal(Card(("1.25", "XO")), appended);

        // A height ABOVE the athlete's last, still on the ladder: ordinary.
        var (third, raised) = Append(appended, 1.35m, "X");
        Assert.Null(third);
        Assert.Equal(Card(("1.25", "XO"), ("1.35", "X")), raised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("XX")]
    [InlineData("o")]
    public void A_mark_outside_the_notation_is_a_bad_request_not_a_conflict(string? mark)
    {
        var refusal = Append([], 1.25m, mark).Refusal;
        Assert.Equal(ErrorCodes.ValidationFailed, refusal!.Code);
        Assert.True(refusal.BadRequest);
    }

    [Theory]
    [InlineData("1.234")]  // not a whole centimetre
    [InlineData("0.00")]
    [InlineData("5.01")]
    public void A_height_that_is_not_a_bar_height_is_a_bad_request(string height)
    {
        var refusal = Append([], Height(height), "O").Refusal;
        Assert.Equal(ErrorCodes.ValidationFailed, refusal!.Code);
        Assert.True(refusal.BadRequest);
    }

    [Fact]
    public void An_eliminated_athlete_takes_no_further_mark()
    {
        var refusal = Append(Card(("1.20", "XX-"), ("1.25", "X")), 1.30m, "O").Refusal;
        Assert.Equal(ErrorCodes.AthleteEliminated, refusal!.Code);
        Assert.False(refusal.BadRequest);
    }

    [Fact]
    public void A_height_the_bar_has_not_reached_is_refused()
    {
        Assert.Equal(ErrorCodes.HeightNotOnLadder, Append([], 1.45m, "O").Refusal!.Code);
    }

    [Fact]
    public void The_bar_never_comes_down_for_an_athlete_who_has_jumped_above_it()
    {
        Assert.Equal(ErrorCodes.BarNeverLowered,
            Append(Card(("1.30", "X")), 1.25m, "O").Refusal!.Code);
    }

    [Fact]
    public void A_mark_at_a_lower_height_with_attempts_outstanding_is_accepted()
    {
        // The bar is at 1.35 for the division and this athlete has one failure at
        // 1.25 and nothing above: their outstanding attempts there are still theirs.
        var (refusal, card) = Append(Card(("1.25", "X")), 1.25m, "-");
        Assert.Null(refusal);
        Assert.Equal(Card(("1.25", "X-")), card);
    }

    [Theory]
    [InlineData("O", ErrorCodes.HeightAlreadyCleared)]
    [InlineData("XO", ErrorCodes.HeightAlreadyCleared)]
    [InlineData("-", ErrorCodes.HeightPassed)]
    [InlineData("XX-", ErrorCodes.HeightPassed)]
    public void A_height_that_is_finished_takes_no_more_marks(string marks, string expected)
    {
        Assert.Equal(expected, Append(Card(("1.25", marks)), 1.25m, "X").Refusal!.Code);
    }

    [Fact]
    public void Three_marks_at_one_height_answer_elimination_rather_than_exhaustion()
    {
        // XXX is the only way to hold three marks without clearing or passing,
        // and it is also three consecutive failures — so the §4 order answers
        // ATHLETE_ELIMINATED, which is the more useful of the two.
        // HEIGHT_ATTEMPTS_EXHAUSTED stays behind it as defence in depth over a
        // card the server did not write.
        Assert.Equal(ErrorCodes.AthleteEliminated,
            Append(Card(("1.25", "XXX")), 1.25m, "X").Refusal!.Code);
    }

    // ---- Two attempts per height (ADR-0056 amendment 2026-09-25) ----------

    [Theory]
    [InlineData("X")]
    [InlineData("XX")]
    [InlineData("O")]
    [InlineData("XO")]
    [InlineData("-")]
    [InlineData("X-")]
    public void At_two_a_height_holds_at_most_two_marks(string marks) =>
        Assert.True(JumpCard.IsValidMarks(marks, 2));

    [Theory]
    [InlineData("XXX")]
    [InlineData("XXO")]
    [InlineData("XX-")]
    public void At_two_a_third_mark_is_not_a_set_of_marks(string marks) =>
        Assert.False(JumpCard.IsValidMarks(marks, 2));

    [Fact]
    public void At_two_both_attempts_failed_at_one_height_eliminates()
    {
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "XX")), 2));
        Assert.True(JumpCard.IsEliminated(Card(("1.20", "O"), ("1.25", "XX")), 2));
        Assert.False(JumpCard.IsEliminated(Card(("1.20", "XO"), ("1.25", "X")), 2));
    }

    [Fact]
    public void At_two_a_failure_carries_past_neither_a_pass_nor_the_next_height()
    {
        // Out at three, but at two no single height has had both attempts fail.
        var card = Card(("1.20", "X-"), ("1.25", "X-"), ("1.30", "X"));
        Assert.False(JumpCard.IsEliminated(card, 2));
        Assert.True(JumpCard.IsEliminated(card, 3));
        Assert.False(JumpCard.IsEliminated(Card(("1.20", "X-"), ("1.25", "-"), ("1.30", "X")), 2));
        Assert.Null(Append(card, 1.30m, "O", attemptsPerHeight: 2).Refusal);
    }

    [Fact]
    public void At_two_the_eliminated_refusal_does_not_speak_of_three_failures()
    {
        var refusal = Append(Card(("1.20", "XX")), 1.25m, "O", attemptsPerHeight: 2).Refusal;
        Assert.Equal(ErrorCodes.AthleteEliminated, refusal!.Code);
        Assert.DoesNotContain("three", refusal.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void At_two_two_failures_at_one_height_answer_elimination_rather_than_exhaustion()
    {
        Assert.Equal(ErrorCodes.AthleteEliminated,
            Append(Card(("1.25", "XX")), 1.25m, "X", attemptsPerHeight: 2).Refusal!.Code);
    }

    [Fact]
    public void Exhaustion_names_the_divisions_own_count()
    {
        // Only over a card the server did not write: XX is elimination first.
        var refusal = Append(Card(("1.25", "XXX")), 1.25m, "X", attemptsPerHeight: 2).Refusal;
        Assert.Equal(ErrorCodes.HeightAttemptsExhausted, refusal!.Code);
        Assert.Contains("All 2 attempts", refusal.Detail, StringComparison.Ordinal);
    }
}
