using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The ADR-0003 division rules, tested at the rule rather than at the four
/// endpoints that share them: age group matches exactly (or the event is open on
/// that axis), category matches the EFFECTIVE category (override ?? snapshot
/// default), and a relay leg must additionally swim for its team's house
/// (ADR-0008). The HTTP suites cover the sentences and status codes; these cover
/// the decision.
/// </summary>
public sealed class EligibilityServiceTests
{
    /// <summary>An event contesting one age, or (null) open across the axis.</summary>
    private static CarnivalEvent Event(string? ageGroup, CompetitionCategory? category) => new()
    {
        Id = Guid.CreateVersion7(),
        CarnivalId = Guid.CreateVersion7(),
        EventDefinitionId = Guid.CreateVersion7(),
        AgeGroup = ageGroup ?? AgeScope.OpenKey,
        Category = category,
    };

    private static RosterSnapshot Roster(
        string ageGroup, CompetitionCategory category, string houseCode = "BLU") => new()
    {
        Id = Guid.CreateVersion7(),
        StudentId = Guid.CreateVersion7(),
        SchoolYear = 2026,
        YearLevel = 8,
        HouseCode = houseCode,
        CompetitionAgeGroup = ageGroup,
        CompetitionCategory = category,
        EffectiveDate = new DateOnly(2026, 2, 1),
        IsCurrent = true,
    };

    [Fact]
    public void Matching_division_is_eligible()
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", CompetitionCategory.F), Roster("13", CompetitionCategory.F), null);
        Assert.True(verdict.IsEligible);
    }

    [Fact]
    public void Null_event_axes_are_open()
    {
        var verdict = EligibilityService.CheckDivision(
            Event(null, null), Roster("OPEN", CompetitionCategory.S), CompetitionCategory.M);
        Assert.True(verdict.IsEligible);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("130")]  // no prefix or substring matching
    [InlineData("1")]
    [InlineData("13 ")]  // the roster's code is compared as stored, untrimmed
    [InlineData("j13")]
    public void Age_group_must_match_exactly(string snapshotAgeGroup)
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", null), Roster(snapshotAgeGroup, CompetitionCategory.M), null);
        Assert.Equal(EligibilityAxis.AgeGroup, verdict.Axis);
        Assert.Equal(snapshotAgeGroup, verdict.Actual);
        Assert.Equal("13", verdict.Required);
    }

    [Fact]
    public void Age_group_is_reported_before_category()
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", CompetitionCategory.F), Roster("14", CompetitionCategory.M), null);
        Assert.Equal(EligibilityAxis.AgeGroup, verdict.Axis);
    }

    [Fact]
    public void Category_override_shadows_the_snapshot_default()
    {
        var carnivalEvent = Event(null, CompetitionCategory.M);
        var snapshot = Roster("13", CompetitionCategory.F);

        Assert.Equal(EligibilityAxis.Category,
            EligibilityService.CheckDivision(carnivalEvent, snapshot, null).Axis);
        Assert.True(EligibilityService.CheckDivision(carnivalEvent, snapshot, CompetitionCategory.M).IsEligible);
    }

    [Fact]
    public void Category_override_can_also_make_an_otherwise_eligible_student_ineligible()
    {
        var verdict = EligibilityService.CheckDivision(
            Event(null, CompetitionCategory.M), Roster("13", CompetitionCategory.M), CompetitionCategory.F);
        Assert.Equal(EligibilityAxis.Category, verdict.Axis);
        Assert.Equal("F", verdict.Actual);
        Assert.Equal("M", verdict.Required);
    }

    // --- The S widening (ADR-0023 §1) --------------------------------------
    // An S student is listed in Boys, Girls AND Open. A Boys or Girls division
    // stays closed to the opposite category: the whole value of
    // CATEGORY_INELIGIBLE is that a mistap on a browsable roster of several
    // hundred names still lands somewhere it cannot swim.

    [Theory]
    [InlineData(CompetitionCategory.M)]
    [InlineData(CompetitionCategory.F)]
    [InlineData(CompetitionCategory.S)]
    [InlineData(null)]
    public void An_S_student_clears_every_category_division(CompetitionCategory? division)
    {
        Assert.True(EligibilityService.CheckDivision(
            Event("13", division), Roster("13", CompetitionCategory.S), null).IsEligible);
    }

    [Theory]
    [InlineData(CompetitionCategory.M, CompetitionCategory.F)]
    [InlineData(CompetitionCategory.F, CompetitionCategory.M)]
    public void An_M_or_F_student_is_still_refused_by_the_opposite_division(
        CompetitionCategory student, CompetitionCategory division)
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", division), Roster("13", student), null);
        Assert.Equal(EligibilityAxis.Category, verdict.Axis);
        Assert.Equal(student.ToString(), verdict.Actual);
        Assert.Equal(division.ToString(), verdict.Required);
    }

    // --- An Open division is open to everyone (ADR-0026 §1) -----------------

    [Theory]
    [InlineData(CompetitionCategory.M)]
    [InlineData(CompetitionCategory.F)]
    [InlineData(CompetitionCategory.S)]
    public void An_open_division_admits_every_student_so_its_entry_list_is_not_a_roster_of_gender_diverse_students(
        CompetitionCategory student)
    {
        Assert.True(EligibilityService.CheckDivision(
            Event("13", CompetitionCategory.S), Roster("13", student), null).IsEligible);
    }

    [Fact]
    public void An_open_division_still_refuses_on_age_group()
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", CompetitionCategory.S), Roster("14", CompetitionCategory.M), null);
        Assert.Equal(EligibilityAxis.AgeGroup, verdict.Axis);
    }

    [Fact]
    public void An_override_to_S_now_widens_rather_than_moves()
    {
        // ADR-0023 §1 states this consequence rather than leaving it to be
        // discovered: the same rule applied uniformly, and §3 caps the student at
        // one entry regardless, so it buys no extra swims.
        var snapshot = Roster("13", CompetitionCategory.M);
        foreach (var division in new[] { CompetitionCategory.M, CompetitionCategory.F, CompetitionCategory.S })
            Assert.True(EligibilityService.CheckDivision(
                Event("13", division), snapshot, CompetitionCategory.S).IsEligible);
    }

    [Fact]
    public void The_widening_reaches_relay_legs_through_the_same_rule()
    {
        // ADR-0023 §5: CheckLeg calls CheckDivision, so an S student may swim a
        // leg for a Boys or a Girls house team with no extra code — and the
        // ADR-0008 house rule is untouched.
        Assert.True(EligibilityService.CheckLeg(
            Event("13", CompetitionCategory.F), "BLU", Roster("13", CompetitionCategory.S)).IsEligible);
        Assert.Equal(EligibilityAxis.House, EligibilityService.CheckLeg(
            Event("13", CompetitionCategory.M), "RED", Roster("13", CompetitionCategory.S)).Axis);
    }

    [Fact]
    public void Effective_category_is_the_override_where_one_exists()
    {
        Assert.Equal(CompetitionCategory.F,
            EligibilityService.Effective(CompetitionCategory.M, CompetitionCategory.F));
        Assert.Equal(CompetitionCategory.M,
            EligibilityService.Effective(CompetitionCategory.M, null));
    }

    [Theory]
    [InlineData("BLU")]
    [InlineData("blu")] // officials type house codes; the ADR-0008 rule is case-insensitive
    public void Leg_house_matches_case_insensitively(string teamHouse)
    {
        var verdict = EligibilityService.CheckLeg(
            Event("13", CompetitionCategory.M), teamHouse, Roster("13", CompetitionCategory.M));
        Assert.True(verdict.IsEligible);
    }

    [Fact]
    public void Leg_in_another_house_is_refused_before_the_division_axes()
    {
        var verdict = EligibilityService.CheckLeg(
            Event("13", CompetitionCategory.M), "RED", Roster("14", CompetitionCategory.F, "GRN"));
        Assert.Equal(EligibilityAxis.House, verdict.Axis);
        Assert.Equal("GRN", verdict.Actual);
        Assert.Equal("RED", verdict.Required);
    }

    [Fact]
    public void Leg_still_has_to_satisfy_the_relay_division()
    {
        var carnivalEvent = Event("13", CompetitionCategory.M);
        Assert.Equal(EligibilityAxis.AgeGroup,
            EligibilityService.CheckLeg(carnivalEvent, "BLU", Roster("14", CompetitionCategory.M)).Axis);
        Assert.Equal(EligibilityAxis.Category,
            EligibilityService.CheckLeg(carnivalEvent, "BLU", Roster("13", CompetitionCategory.F)).Axis);
    }

    /// <summary>
    /// The search filter and the per-entry check are the same rule in two forms —
    /// the whole point of the extraction, so they are asserted against each other.
    /// </summary>
    [Theory]
    [InlineData("13", CompetitionCategory.M)]
    [InlineData("13", null)]
    [InlineData(null, CompetitionCategory.M)]
    [InlineData(null, null)]
    public void Search_filter_admits_exactly_the_snapshots_the_entry_check_accepts(
        string? ageGroup, CompetitionCategory? category)
    {
        var carnivalEvent = Event(ageGroup, category);
        var snapshots = new[]
        {
            Roster("13", CompetitionCategory.M),
            Roster("13", CompetitionCategory.F),
            Roster("13", CompetitionCategory.S), // the ADR-0023 widening, on both sides of the rule
            Roster("14", CompetitionCategory.M),
            Roster("OPEN", CompetitionCategory.S),
        };

        var admitted = EligibilityService.WhereDivisionMatches(snapshots.AsQueryable(), carnivalEvent)
            .Select(s => s.Id)
            .ToHashSet();
        var eligible = snapshots
            .Where(s => EligibilityService.CheckDivision(carnivalEvent, s, null).IsEligible)
            .Select(s => s.Id)
            .ToHashSet();

        Assert.Equal(eligible, admitted);
    }

    // --- One age per division (ADR-0058 §1) -------------------------------

    [Fact]
    public void The_refusal_names_the_one_age_the_division_contests()
    {
        var verdict = EligibilityService.CheckDivision(
            Event("13", null), Roster("14", CompetitionCategory.M), null);
        Assert.Equal(EligibilityAxis.AgeGroup, verdict.Axis);
        Assert.Equal("14", verdict.Actual);
        Assert.Equal("13", verdict.Required);
    }

    [Fact]
    public void A_non_numeric_age_matches_by_the_same_equality()
    {
        // The senior merge happens at roster import (ADR-0058 §"Context" 2), so
        // SNR reaches eligibility as an ordinary age.
        var carnivalEvent = Event("SNR", CompetitionCategory.M);
        Assert.True(EligibilityService.CheckDivision(
            carnivalEvent, Roster("SNR", CompetitionCategory.M), null).IsEligible);
        var refused = EligibilityService.CheckDivision(
            carnivalEvent, Roster("16", CompetitionCategory.M), null);
        Assert.Equal(EligibilityAxis.AgeGroup, refused.Axis);
        Assert.Equal("SNR", refused.Required);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("18")]
    [InlineData("SNR")]
    public void An_open_scope_admits_any_age(string snapshotAgeGroup)
    {
        // The all-ages house relay (ADR-0008): the age axis does not participate.
        foreach (var carnivalEvent in new[] { Event(null, null), Event(AgeScope.OpenKey, null) })
            Assert.True(EligibilityService.CheckDivision(
                carnivalEvent, Roster(snapshotAgeGroup, CompetitionCategory.M), null).IsEligible);
    }

    [Fact]
    public void Search_filter_and_entry_check_agree_on_the_age()
    {
        // The search and the per-entry check are the same rule in two forms.
        var carnivalEvent = Event("13", CompetitionCategory.M);
        var snapshots = new[]
        {
            Roster("13", CompetitionCategory.M),
            Roster("14", CompetitionCategory.M),
            Roster("13", CompetitionCategory.F),
        };

        var admitted = EligibilityService.WhereDivisionMatches(snapshots.AsQueryable(), carnivalEvent)
            .Select(s => s.Id)
            .ToHashSet();
        var eligible = snapshots
            .Where(s => EligibilityService.CheckDivision(carnivalEvent, s, null).IsEligible)
            .Select(s => s.Id)
            .ToHashSet();

        Assert.Single(admitted); // the 13/M
        Assert.Equal(eligible, admitted);
    }

    [Fact]
    public void A_relay_leg_is_held_to_the_same_age()
    {
        // ADR-0008 house rule first, then the division.
        var carnivalEvent = Event("13", CompetitionCategory.M);
        Assert.True(EligibilityService.CheckLeg(
            carnivalEvent, "BLU", Roster("13", CompetitionCategory.M)).IsEligible);
        Assert.Equal(EligibilityAxis.AgeGroup, EligibilityService.CheckLeg(
            carnivalEvent, "BLU", Roster("14", CompetitionCategory.M)).Axis);
    }
}
