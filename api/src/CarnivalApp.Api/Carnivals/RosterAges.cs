using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;

namespace CarnivalApp.Api.Carnivals;

// Year is null when no roster is promoted.
public sealed record RosterFacts(IReadOnlyList<string> AgeGroups, int? Year);

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
        return new RosterFacts(
        [
            .. groups
                .Select(g => g.CompetitionAgeGroup)
                .Distinct()
                .Where(c => !string.IsNullOrWhiteSpace(c) && AgeScope.Validate(c) is null)
                .OrderBy(AgeScope.Order)
                .ThenBy(c => c, StringComparer.Ordinal),
        ], year);
    }
}
