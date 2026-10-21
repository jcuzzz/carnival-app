using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

public enum EligibilityAxis
{
    Eligible,
    AgeGroup,
    Category,
    House,
}

// On the Category axis, Actual is the student's own category and must never reach a response.
public readonly record struct EligibilityVerdict(EligibilityAxis Axis, string Actual, string Required)
{
    public static readonly EligibilityVerdict Eligible = new(EligibilityAxis.Eligible, "", "");

    public bool IsEligible => Axis == EligibilityAxis.Eligible;
}

// The one implementation of the eligibility rules, shared by every entry path.
// An S division admits every student, and an effective S student also clears M and F divisions.
public static class EligibilityService
{
    // The override shadows the snapshot default; it never rewrites it.
    public static CompetitionCategory Effective(
        CompetitionCategory snapshotDefault, CompetitionCategory? categoryOverride) =>
        categoryOverride ?? snapshotDefault;

    // CheckDivision as a query filter; matches on the snapshot default, never an override.
    public static IQueryable<RosterSnapshot> WhereDivisionMatches(
        IQueryable<RosterSnapshot> snapshots, CarnivalEvent carnivalEvent)
    {
        var age = carnivalEvent.AgeGroup;
        if (age != AgeScope.OpenKey)
            snapshots = snapshots.Where(s => s.CompetitionAgeGroup == age);
        // Must stay the same rule as CheckCategory.
        if (carnivalEvent.Category is { } required && required != CompetitionCategory.S)
            snapshots = snapshots.Where(s =>
                s.CompetitionCategory == required
                || s.CompetitionCategory == CompetitionCategory.S);
        return snapshots;
    }

    // Age group first, then effective category: the refusal order the entry paths report.
    public static EligibilityVerdict CheckDivision(
        CarnivalEvent carnivalEvent, RosterSnapshot snapshot, CompetitionCategory? categoryOverride)
    {
        var age = carnivalEvent.AgeGroup;
        if (age != AgeScope.OpenKey
            && !string.Equals(age, snapshot.CompetitionAgeGroup, StringComparison.Ordinal))
            return new EligibilityVerdict(
                EligibilityAxis.AgeGroup, snapshot.CompetitionAgeGroup, age);
        return CheckCategory(carnivalEvent, snapshot.CompetitionCategory, categoryOverride);
    }

    public static EligibilityVerdict CheckCategory(
        CarnivalEvent carnivalEvent, CompetitionCategory snapshotDefault, CompetitionCategory? categoryOverride)
    {
        var effective = Effective(snapshotDefault, categoryOverride);
        return carnivalEvent.Category is { } required
            && required != CompetitionCategory.S
            && effective != required
            && effective != CompetitionCategory.S
            ? new EligibilityVerdict(
                EligibilityAxis.Category, effective.ToString(), required.ToString())
            : EligibilityVerdict.Eligible;
    }

    // House compare ignores case: snapshot codes keep the source system's casing.
    public static EligibilityVerdict CheckLeg(
        CarnivalEvent carnivalEvent, string houseCode, RosterSnapshot snapshot)
    {
        if (!string.Equals(snapshot.HouseCode, houseCode, StringComparison.OrdinalIgnoreCase))
            return new EligibilityVerdict(EligibilityAxis.House, snapshot.HouseCode, houseCode);
        return CheckDivision(carnivalEvent, snapshot, categoryOverride: null);
    }

    // The sentence is the caller's: only it knows if the subject is a student, leg or override.
    public static IResult Refuse(
        EligibilityVerdict verdict, string detail, Dictionary<string, object?>? context = null) =>
        ApiError.Unprocessable(Code(verdict.Axis), detail, context);

    private static string Code(EligibilityAxis axis) => axis switch
    {
        EligibilityAxis.AgeGroup => ErrorCodes.AgeGroupIneligible,
        EligibilityAxis.Category => ErrorCodes.CategoryIneligible,
        EligibilityAxis.House => ErrorCodes.HouseMismatch,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "An eligible verdict has no refusal code."),
    };
}
