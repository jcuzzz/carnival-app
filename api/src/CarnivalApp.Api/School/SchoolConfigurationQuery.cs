using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.School;

public static class SchoolConfigurationQuery
{
    // By Id, not CreatedAtUtc: a UUIDv7 orders the same,
    // and SQLite cannot ORDER BY a DateTimeOffset.
    public static IQueryable<SchoolConfiguration> TheSchool(this IQueryable<SchoolConfiguration> rows) =>
        rows.OrderBy(row => row.Id);

    // On the caller's context; SchoolSettingsProvider is the read that logs an unreadable blob.
    public static async Task<IReadOnlyList<string>> ConfiguredHouseCodesAsync(
        this CarnivalAppDbContext db, CancellationToken ct)
    {
        var row = await db.SchoolConfigurations.AsNoTracking().TheSchool().FirstOrDefaultAsync(ct);
        return row is null
            ? []
            : [.. SchoolSettings.From(row, NullLogger.Instance).Houses.Select(h => h.Code)];
    }
}
