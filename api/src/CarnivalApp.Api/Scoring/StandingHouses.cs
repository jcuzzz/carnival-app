using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Scoring;

/// <summary>The houses a carnival's standings and participation card both list.</summary>
public static class StandingHouses
{
    // A finalised carnival is history: a house configured after it did not take part.
    public static async Task<IReadOnlyList<string>> ListAsync(
        CarnivalAppDbContext db, Guid carnivalId, CarnivalStatus status, CancellationToken ct)
    {
        var configured = await db.ConfiguredHouseCodesAsync(ct);
        if (status != CarnivalStatus.Finalised) return configured;

        var entered = await db.Entries.AsNoTracking()
            .Where(e => e.CarnivalEvent.CarnivalId == carnivalId)
            .Select(e => e.HouseCode ?? e.SnapshotHouseCode)
            .Distinct()
            .ToListAsync(ct);
        var legged = await db.RelayLegs.AsNoTracking()
            .Where(l => l.Entry.CarnivalEvent.CarnivalId == carnivalId)
            .Select(l => l.SnapshotHouseCode)
            .Distinct()
            .ToListAsync(ct);
        var adjusted = await db.HousePointsAdjustments.AsNoTracking()
            .Where(a => a.CarnivalId == carnivalId)
            .Select(a => a.HouseCode)
            .Distinct()
            .ToListAsync(ct);
        var tookPart = entered.Concat(legged).Concat(adjusted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Configured spelling first, so a house's code reads as the school writes it.
        return [.. configured.Where(tookPart.Contains).Union(tookPart, StringComparer.OrdinalIgnoreCase)];
    }
}
