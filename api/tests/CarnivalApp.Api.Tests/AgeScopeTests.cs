using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0058 §1: the age axis of a division is ONE code the roster derives, or
/// the OPEN sentinel. The rules pinned here are the ones a division depends on —
/// a code eligibility can match by equality, a code the columns can hold, and a
/// display order the book and the event list share.
/// </summary>
public sealed class AgeScopeTests
{
    [Fact]
    public void A_code_is_normalised_so_two_spellings_are_one_division()
    {
        Assert.Equal("SNR", AgeScope.Normalise(" snr "));
        Assert.Equal("13", AgeScope.Normalise("13"));
    }

    [Fact]
    public void An_absent_or_blank_age_is_open_across_the_axis()
    {
        foreach (var requested in new string?[] { null, "", "   " })
            Assert.Equal(AgeScope.OpenKey, AgeScope.Normalise(requested));
        Assert.Equal(AgeScope.OpenKey, AgeScope.Normalise("open"));
    }

    [Fact]
    public void A_merged_scope_is_refused_because_no_division_can_represent_one()
    {
        // "13-14" matches no student at all (eligibility is equality), so it
        // would surface only as every entry being refused on the deck.
        Assert.NotNull(AgeScope.Validate("13-14"));
        Assert.Null(AgeScope.Validate("13"));
        Assert.Null(AgeScope.Validate("14"));
    }

    [Theory]
    [InlineData("15/16")]
    [InlineData("15 16")]
    [InlineData("13+")]
    [InlineData("U13")]  // legal: letters and digits only
    public void Only_a_plain_alphanumeric_age_code_is_accepted(string code)
    {
        var refused = AgeScope.Validate(code);
        if (code == "U13") Assert.Null(refused);
        else Assert.NotNull(refused);
    }

    [Fact]
    public void An_age_that_would_not_fit_the_column_is_refused_not_stored()
    {
        // EF does not enforce HasMaxLength client-side and SQLite ignores it, so
        // without this an over-long code passes every local test and then fails
        // on PostgreSQL alone — as a 500, not a 400.
        Assert.NotNull(AgeScope.Validate(new string('A', AgeScope.MaxCodeLength + 1)));
        Assert.Null(AgeScope.Validate(new string('A', AgeScope.MaxCodeLength)));
    }

    [Fact]
    public void Open_is_a_valid_age_for_an_event_though_never_for_a_profile_row()
    {
        // The sentinel is the all-ages house relay's scope; a profile row is a
        // CONTESTED age, so DivisionProfile refuses it separately.
        Assert.Null(AgeScope.Validate(AgeScope.OpenKey));
        Assert.NotNull(DivisionProfile.Validate(new DivisionProfileDto(
            [AgeScope.OpenKey], [CompetitionCategory.M], [EventTier.Competitive])));
    }

    [Fact]
    public void Open_sorts_first_numeric_ages_by_value_and_any_other_code_last()
    {
        // Ordinal ordering would put "10" before "9" in the results book.
        Assert.Equal(
            [AgeScope.OpenKey, "9", "10", "13", "SNR"],
            new[] { "13", "SNR", "10", AgeScope.OpenKey, "9" }
                .OrderBy(AgeScope.Order).ThenBy(c => c, StringComparer.Ordinal));
    }
}
