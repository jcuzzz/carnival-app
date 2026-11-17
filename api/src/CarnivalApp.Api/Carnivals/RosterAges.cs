using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;

namespace CarnivalApp.Api.Carnivals;

// Year is null when no roster is promoted. StudentsByAge counts current, active students per code.
public sealed record RosterFacts(
    IReadOnlyList<string> AgeGroups, int? Year, IReadOnlyDictionary<string, int> StudentsByAge);

public static class RosterAges
{
    // Drops codes no division may carry (a birthdate past the cut-off derives "-12"), so the editor
    // cannot offer an age the PUT would refuse.
    public static async Task<RosterFacts> ReadAsync(CarnivalAppDbContext db, CancellationToken ct)
    {
        var groups = await db.RosterSnapshots.AsNoTracking()
            .Where(s => s.IsCurrent && s.Student.IsActive)
            .GroupBy(s => new { s.CompetitionAgeGroup, s.SchoolYear })
            .Select(g => new { g.Key.CompetitionAgeGroup, g.Key.SchoolYear, Count = g.Count() })
            .ToListAsync(ct);
        // A student missing from the latest file keeps an older snapshot, so the roster's year is
        // the one most of it carries.
        var year = groups
            .GroupBy(g => g.SchoolYear)
            .OrderByDescending(y => y.Sum(g => g.Count))
            .ThenByDescending(y => y.Key)
            .Select(y => (int?)y.Key)
            .FirstOrDefault();
        var byAge = groups
            .Where(g => !string.IsNullOrWhiteSpace(g.CompetitionAgeGroup)
                && AgeScope.Validate(g.CompetitionAgeGroup) is null)
            .GroupBy(g => g.CompetitionAgeGroup, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.Ordinal);
        return new RosterFacts(
            [.. byAge.Keys.OrderBy(AgeScope.Order).ThenBy(c => c, StringComparer.Ordinal)], year, byAge);
    }
}
