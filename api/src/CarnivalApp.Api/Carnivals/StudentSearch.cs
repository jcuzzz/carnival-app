using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// Provider branch: ILIKE leaves the column bare so a trigram index can serve it on PostgreSQL;
// no predicate is case-insensitive and index-friendly on both Npgsql and SQLite.
public static class StudentSearch
{
    // LIKE metacharacters in an official-typed term are literals, not wildcards.
    private const char EscapeChar = '\\';

    private static string Contains(string term) => $"%{Escape(term)}%";

    private static string Escape(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");

    public static IQueryable<RosterSnapshot> WhereNameMatches(
        this IQueryable<RosterSnapshot> snapshots, CarnivalAppDbContext db, string term)
    {
        if (db.Database.IsNpgsql())
        {
            var pattern = Contains(term);
            return snapshots.Where(s =>
                EF.Functions.ILike(s.Student.Surname, pattern, EscapeChar.ToString())
                || EF.Functions.ILike(s.Student.FirstName, pattern, EscapeChar.ToString())
                || (s.Student.PreferredName != null
                    && EF.Functions.ILike(s.Student.PreferredName, pattern, EscapeChar.ToString())));
        }

        var lowered = term.ToLowerInvariant();
        return snapshots.Where(s =>
            s.Student.Surname.ToLower().Contains(lowered)
            || s.Student.FirstName.ToLower().Contains(lowered)
            || (s.Student.PreferredName != null && s.Student.PreferredName.ToLower().Contains(lowered)));
    }

    // Stored house codes keep whatever casing the source system exported.
    public static IQueryable<RosterSnapshot> WhereHouseMatches(
        this IQueryable<RosterSnapshot> snapshots, CarnivalAppDbContext db, string house)
    {
        if (db.Database.IsNpgsql())
        {
            // No wildcards: ILIKE without metacharacters is case-insensitive equality.
            var pattern = Escape(house);
            return snapshots.Where(s => EF.Functions.ILike(s.HouseCode, pattern, EscapeChar.ToString()));
        }

        var upper = house.ToUpperInvariant();
        return snapshots.Where(s => s.HouseCode.ToUpper() == upper);
    }
}
