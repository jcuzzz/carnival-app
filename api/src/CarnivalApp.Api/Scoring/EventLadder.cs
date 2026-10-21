using System.Globalization;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Scoring;

// One ranked list per division across every heat, built once so the district list and the
// record book cannot disagree. Callers filter.
public static class EventLadder
{
    public sealed record Standing(
        Guid ResultId,
        Guid EntryId,
        int Rank,
        decimal Value,
        string FormattedValue,
        string HolderDisplayName,
        string HouseCode,
        int YearLevel,
        bool IsTeam);

    public sealed record LadderDivision(
        Guid CarnivalEventId,
        Guid EventDefinitionId,
        string EventCode,
        string EventName,
        MeasureType MeasureType,
        string AgeGroup,
        CompetitionCategory? Category,
        EventTier Tier,
        bool HigherBetter,
        IReadOnlyList<Standing> Standings);

    private sealed record Swim(
        Guid ResultId, Guid EntryId,
        Guid CarnivalEventId, Guid EventDefinitionId, string EventCode, string EventName,
        MeasureType MeasureType, string AgeGroup,
        CompetitionCategory? Category, EventTier Tier,
        string DisplayName, string HouseCode, int YearLevel, bool IsTeam, decimal Value,
        string? AttemptsJson);

    // Safe inside the finalisation transaction: the sweep only moves LifecycleState to Final,
    // and Draft is the sole state excluded here.
    public static async Task<IReadOnlyList<LadderDivision>> BuildAsync(
        SportsDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var swims = await db.Results.AsNoTracking()
            .Where(r => r.Entry.CarnivalEvent.CarnivalId == carnivalId
                        && r.LifecycleState != ResultLifecycleState.Draft
                        && r.Status == ResultStatus.Ok
                        && r.MeasuredValue != null)
            .Select(r => new Swim(
                r.Id,
                r.EntryId,
                r.Entry.CarnivalEventId,
                r.Entry.CarnivalEvent.EventDefinitionId,
                r.Entry.CarnivalEvent.EventDefinition.Code,
                r.Entry.CarnivalEvent.EventDefinition.Name,
                r.Entry.CarnivalEvent.EventDefinition.MeasureType,
                r.Entry.CarnivalEvent.AgeGroup,
                r.Entry.CarnivalEvent.Category,
                r.Entry.CarnivalEvent.Tier,
                r.Entry.SnapshotDisplayName,
                r.Entry.SnapshotHouseCode,
                r.Entry.SnapshotYearLevel,
                r.Entry.StudentId == null,
                r.MeasuredValue!.Value,
                r.AttemptsJson))
            .ToListAsync(ct);

        return [.. swims
            .GroupBy(s => s.CarnivalEventId)
            .OrderBy(g => g.First().EventCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => AgeScope.Order(g.First().AgeGroup))
            .ThenBy(g => g.First().AgeGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.First().Category)
            .ThenBy(g => g.First().Tier)
            .Select(Ladder)];
    }

    // Always the measure's canonical unit, never the definition's free-text Units.
    public static string FormatValue(MeasureType measureType, decimal value) => measureType switch
    {
        MeasureType.Time => ResultValueParser.FormatTime(value),
        MeasureType.Distance or MeasureType.Height => $"{Metres(value)} m",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    public static string Metres(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static LadderDivision Ladder(IGrouping<Guid, Swim> division)
    {
        var meta = division.First();
        var higherBetter = Placement.HigherIsBetter(meta.MeasureType);
        var field = division.ToList();
        // In memory: the series is stored as text and countback is not expressible in SQL.
        var ranks = Placement.Rank(
            [.. field.Select(s => AttemptSeries.KeyOf(s.AttemptsJson, higherBetter) ?? [s.Value])],
            higherBetter);

        // Entry id last makes the order total: record detection takes the first row of rank 1.
        var standings = field
            .Select((s, i) => new Standing(
                s.ResultId, s.EntryId, ranks[i], s.Value, FormatValue(s.MeasureType, s.Value),
                s.DisplayName, s.HouseCode, s.YearLevel, s.IsTeam))
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.HolderDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.EntryId)
            .ToList();

        return new LadderDivision(
            meta.CarnivalEventId, meta.EventDefinitionId, meta.EventCode, meta.EventName,
            meta.MeasureType, meta.AgeGroup,
            meta.Category, meta.Tier, higherBetter, standings);
    }
}
