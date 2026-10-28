using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Imports;

/// <summary>The competition year a roster import derives ages for.</summary>
public static class CompetitionYears
{
    /// <summary>How many years either side of the school's current year an import may name.</summary>
    public const int MaxDistance = 1;

    // The year turns at midnight in the school's zone, not UTC's, which is still in the old year
    // until 11am on 1 January in Melbourne.
    public static DateOnly SchoolToday(IConfiguration config, DateTimeOffset now)
    {
        var zoneId = SchoolTimezone.From(config);
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    }

    public static int Earliest(DateOnly today) => today.Year - MaxDistance;

    public static int Latest(DateOnly today) => today.Year + MaxDistance;

    public static bool InRange(int year, DateOnly today) =>
        year >= Earliest(today) && year <= Latest(today);

    // A November import ahead of a February carnival belongs to the carnival's year.
    public static async Task<int> DefaultAsync(CarnivalAppDbContext db, DateOnly today, CancellationToken ct)
    {
        var horizon = new DateOnly(Latest(today), 12, 31);
        var next = await db.Carnivals.AsNoTracking()
            .Where(c => c.Date >= today && c.Date <= horizon)
            .OrderBy(c => c.Date)
            .Select(c => (DateOnly?)c.Date)
            .FirstOrDefaultAsync(ct);
        return next?.Year ?? today.Year;
    }
}
