using System.Globalization;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Reporting;

/// <summary>
/// Placed in the same (event, heat) pools <see cref="ScoringService"/> pays points in, so the book
/// and the board cannot disagree about who won a heat.
/// </summary>
public static class ResultsBook
{
    /// <summary>
    /// No student category: the division label is the only category-shaped value.
    /// </summary>
    public sealed record ResultRow(
        Guid CarnivalEventId,
        string EventCode,
        string EventName,
        string Division,
        EventTier Tier,
        int? Heat,
        // Null for a non-Ok status and for every row of a Social-scored event.
        int? Place,
        string DisplayName,
        string HouseCode,
        string? Value,
        // The record badge compares this, not the rounded printed value.
        decimal? MeasuredValue,
        ResultStatus Status,
        IReadOnlyList<JumpDto>? Jumps = null,
        int? FailsAtBest = null,
        int? TotalFails = null,
        int? AttemptsPerHeight = null);

    public sealed record ResultsReport(
        Guid CarnivalId,
        string CarnivalName,
        DateOnly CarnivalDate,
        bool Provisional,
        IReadOnlyList<ResultRow> Rows);

    private sealed record Recorded(
        Guid ResultId,
        Guid EntryId,
        Guid CarnivalEventId, string EventCode, string EventName, MeasureType MeasureType,
        string AgeGroup,
        CompetitionCategory? Category, EventTier Tier, EventScoringMode? ScoringMode,
        int? Heat, string DisplayName, string HouseCode, ResultStatus Status, decimal? MeasuredValue,
        string? AttemptsJson, int? AttemptsPerHeight);

    public static async Task<ResultsReport?> BuildAsync(
        CarnivalAppDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == carnivalId, ct);
        return carnival is null ? null : await BuildAsync(db, carnival, ct);
    }

    public static async Task<ResultsReport> BuildAsync(
        CarnivalAppDbContext db, Carnival carnival, CancellationToken ct)
    {
        var carnivalId = carnival.Id;
        // A cleared high-jump card prints as the entry with no result it is: not at all.
        var recorded = await db.Results.AsNoTracking()
            .Where(r => r.Entry.CarnivalEvent.CarnivalId == carnivalId)
            .Where(JumpCard.Recorded)
            .Select(r => new Recorded(
                r.Id,
                r.EntryId,
                r.Entry.CarnivalEventId,
                r.Entry.CarnivalEvent.EventDefinition.Code,
                r.Entry.CarnivalEvent.EventDefinition.Name,
                r.Entry.CarnivalEvent.EventDefinition.MeasureType,
                r.Entry.CarnivalEvent.AgeGroup,
                r.Entry.CarnivalEvent.Category,
                r.Entry.CarnivalEvent.Tier,
                r.Entry.CarnivalEvent.ScoringMode,
                r.Entry.Heat,
                r.Entry.SnapshotDisplayName,
                r.Entry.SnapshotHouseCode,
                r.Status,
                r.MeasuredValue,
                r.AttemptsJson,
                r.Entry.CarnivalEvent.AttemptsPerHeight))
            .ToListAsync(ct);

        var rows = new List<ResultRow>();
        foreach (var eventGroup in recorded
            .GroupBy(r => r.CarnivalEventId)
            .OrderBy(g => g.First().EventCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => AgeScope.Order(g.First().AgeGroup))
            .ThenBy(g => g.First().AgeGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.First().Category)
            .ThenBy(g => g.First().Tier))
        {
            var meta = eventGroup.First();
            var division = DivisionNaming.Division(meta.AgeGroup, meta.Category);
            var higherBetter = Placement.HigherIsBetter(meta.MeasureType);
            var places = HeatPlacings.Rank(
                eventGroup.Select(r => new HeatPlacings.Swim(
                    r.ResultId, r.Heat, r.Status, r.MeasuredValue,
                    AttemptSeries.KeyOf(r.AttemptsJson, higherBetter))),
                meta.MeasureType, meta.ScoringMode, meta.Tier);

            // Null is its own field, sorted last. A non-Time
            // division is one field however many flights it ran in.
            var timed = meta.MeasureType == MeasureType.Time;
            foreach (var heat in eventGroup
                .GroupBy(r => timed ? r.Heat : null)
                .OrderBy(h => h.Key ?? int.MaxValue))
            {
                // Entry id last, or two identical names order
                // arbitrarily and the JSON and CSV disagree.
                rows.AddRange(heat
                    .Select(r => (Row: Row(r, division, HeatPlacings.Of(places, r.ResultId)), r.EntryId))
                    .OrderBy(f => f.Row.Place ?? int.MaxValue)
                    .ThenBy(f => f.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.EntryId)
                    .Select(f => f.Row));
            }
        }

        return new ResultsReport(
            carnival.Id, carnival.Name, carnival.Date,
            carnival.Status != CarnivalStatus.Finalised, rows);
    }

    public static readonly string[] CsvHeader =
    [
        "Event code", "Event", "Division", "Tier", "Heat", "Place", "Name", "House", "Result", "Status",
        "Series", "Fails at best", "Total fails",
    ];

    public static IReadOnlyList<string?> CsvRow(ResultRow r) =>
    [
        r.EventCode,
        r.EventName,
        r.Division,
        r.Tier.ToString(),
        r.Heat?.ToString(CultureInfo.InvariantCulture),
        r.Place?.ToString(CultureInfo.InvariantCulture),
        r.DisplayName,
        r.HouseCode,
        r.Value,
        r.Status.ToString().ToUpperInvariant(),
        Series(r.Jumps),
        r.FailsAtBest?.ToString(CultureInfo.InvariantCulture),
        r.TotalFails?.ToString(CultureInfo.InvariantCulture),
    ];

    // "1.25 O · 1.30 XXO".
    private static string? Series(IReadOnlyList<JumpDto>? jumps) =>
        jumps is { Count: > 0 }
            ? string.Join(" · ", jumps.Select(j => $"{JumpCard.Format(j.Height)} {j.Marks}"))
            : null;

    private static ResultRow Row(Recorded r, string division, int? place)
    {
        var card = JumpCard.Parse(r.AttemptsJson);
        return new ResultRow(
            r.CarnivalEventId, r.EventCode, r.EventName, division, r.Tier, r.Heat, place,
            r.DisplayName, r.HouseCode, FormatValue(r.MeasureType, r.MeasuredValue), r.MeasuredValue,
            r.Status,
            card.Count > 0 ? card : null,
            card.Count > 0 ? JumpCard.FailsAtBest(card) : null,
            card.Count > 0 ? JumpCard.CountbackFails(card) : null,
            r.MeasureType == MeasureType.Height
                ? r.AttemptsPerHeight ?? JumpCard.DefaultAttemptsPerHeight
                : null);
    }

    private static string? FormatValue(MeasureType measureType, decimal? value) =>
        value is { } measured ? EventLadder.FormatValue(measureType, measured) : null;
}
