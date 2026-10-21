using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

// Every member is nullable because the same shape is also the input.
public sealed record DivisionProfileDto(
    IReadOnlyList<string>? AgeGroups,
    IReadOnlyList<CompetitionCategory>? Categories,
    IReadOnlyList<EventTier>? Tiers);

// No rows means not configured — an ordinary state, never an empty matrix.
public sealed class DivisionProfile
{
    public required IReadOnlyList<string> AgeGroups { get; init; }
    public required IReadOnlyList<CompetitionCategory> Categories { get; init; }
    public required IReadOnlyList<EventTier> Tiers { get; init; }

    // 32 ages × 3 categories × 2 tiers = 192, just under the 200 one generate call may create.
    public const int MaxAgeGroups = 32;

    // OPEN is refused: a profile row is a contested age.
    public static (string Code, string Detail)? Validate(DivisionProfileDto? requested)
    {
        if (requested is null) return null;

        var ages = Clean(requested.AgeGroups);
        if (ages.Count == 0)
            return (ErrorCodes.ValidationFailed, "divisions.ageGroups must name at least one age.");
        if (ages.Count > MaxAgeGroups)
            return (ErrorCodes.ValidationFailed,
                $"A carnival may contest at most {MaxAgeGroups} ages.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var age in ages)
        {
            if (AgeScope.Validate(age) is { } invalid)
                return (ErrorCodes.AgeScopeInvalid, invalid);
            var code = AgeScope.Normalise(age);
            if (code == AgeScope.OpenKey)
                return (ErrorCodes.AgeScopeInvalid,
                    $"'{AgeScope.OpenKey}' is not an age a carnival contests; it is the age scope of " +
                    "an all-ages event.");
            if (!seen.Add(code))
                return (ErrorCodes.ValidationFailed,
                    "Two ages in divisions.ageGroups are the same; each must be distinct.");
        }

        if (requested.Categories is not { Count: > 0 })
            return (ErrorCodes.ValidationFailed, "divisions.categories must name at least one category.");
        if (requested.Tiers is not { Count: > 0 })
            return (ErrorCodes.ValidationFailed, "divisions.tiers must name at least one tier.");
        return null;
    }

    // Only for a request that has passed Validate.
    public static DivisionProfile From(DivisionProfileDto requested) => new()
    {
        AgeGroups = Clean(requested.AgeGroups).Select(AgeScope.Normalise).ToList(),
        Categories = (requested.Categories ?? []).Distinct().ToList(),
        Tiers = (requested.Tiers ?? []).Distinct().ToList(),
    };

    public static DivisionProfile FromTemplate(EventTemplate template) => new()
    {
        AgeGroups = [.. template.AgeGroups],
        Categories = [.. template.Categories],
        Tiers = [.. template.Tiers],
    };

    // Computed, never stored; the caller supplies codes in display order. An unusable code
    // (an implausible birthdate derives "-9") is skipped rather than refusing the whole default.
    public static DivisionProfile? FromRoster(IEnumerable<string?> ageGroups)
    {
        var requested = new DivisionProfileDto(
            Clean(ageGroups)
                .Select(AgeScope.Normalise)
                .Where(code => AgeScope.Validate(code) is null && code != AgeScope.OpenKey)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            [CompetitionCategory.M, CompetitionCategory.F],
            [EventTier.Competitive]);
        return Validate(requested) is null ? From(requested) : null;
    }

    public DivisionProfileDto ToDto() => new(AgeGroups, Categories, Tiers);

    // Rows are written age-major; FromRows relies on that order to recover the axes.
    public IEnumerable<CarnivalDivision> ToRows(Guid carnivalId) =>
        DivisionGenerator
            .Matrix(AgeGroups.Select(age => (string?)age), Categories, Tiers)
            .Select((spec, index) => new CarnivalDivision
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnivalId,
                AgeGroup = AgeScope.Normalise(spec.AgeGroup),
                Category = spec.Category!.Value,
                Tier = spec.Tier,
                SortOrder = index,
            });

    // Null rather than a half-read matrix, which would offer divisions the school never chose.
    public static DivisionProfile? FromRows(IEnumerable<CarnivalDivision> rows)
    {
        var ordered = rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToList();
        if (ordered.Count == 0) return null;

        var requested = new DivisionProfileDto(
            ordered.Select(r => r.AgeGroup).Distinct(StringComparer.Ordinal).ToList(),
            ordered.Select(r => r.Category).Distinct().ToList(),
            ordered.Select(r => r.Tier).Distinct().ToList());
        return Validate(requested) is null ? From(requested) : null;
    }

    private static List<string> Clean(IEnumerable<string?>? ageGroups) =>
        (ageGroups ?? [])
        .Select(a => a?.Trim())
        .Where(a => !string.IsNullOrEmpty(a))
        .Select(a => a!)
        .ToList();
}
