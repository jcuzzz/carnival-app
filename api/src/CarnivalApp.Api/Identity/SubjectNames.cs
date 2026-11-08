using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Today's name of the person behind a stored subject: a local account's display name, else the
/// name on that subject's newest role grant. A subject with neither is left out.
/// </summary>
public static class SubjectNames
{
    public static async Task<Dictionary<string, string>> ResolveAsync(
        CarnivalAppDbContext db, IEnumerable<string?> subjects, CancellationToken ct)
    {
        var wanted = subjects.OfType<string>().Distinct().ToList();
        var names = new Dictionary<string, string>();
        if (wanted.Count == 0) return names;

        var accountIds = wanted
            .Select(s => LocalAccount.TryParseSubject(s, out var id) ? id : (Guid?)null)
            .OfType<Guid>().ToList();
        if (accountIds.Count > 0)
            foreach (var account in await db.LocalAccounts.AsNoTracking()
                         .Where(a => accountIds.Contains(a.Id))
                         .Select(a => new { a.Id, a.DisplayName }).ToListAsync(ct))
                if (!string.IsNullOrWhiteSpace(account.DisplayName))
                    names[LocalAccount.SubjectFor(account.Id)] = account.DisplayName;

        var rows = await db.RoleAssignments.AsNoTracking()
            .Where(r => r.SubjectId != null && wanted.Contains(r.SubjectId)
                && r.SubjectDisplayName != null)
            .Select(r => new { r.SubjectId, r.SubjectDisplayName, r.CreatedAtUtc }).ToListAsync(ct);
        // Newest grant first, in memory: SQLite cannot ORDER BY a DateTimeOffset.
        foreach (var row in rows.OrderByDescending(r => r.CreatedAtUtc))
            names.TryAdd(row.SubjectId!, row.SubjectDisplayName!);
        return names;
    }
}
