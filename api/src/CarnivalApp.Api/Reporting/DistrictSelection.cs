using System.Globalization;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Reporting;

/// <summary>
/// Top 2 of each Competitive division's event ladder. A tie at second yields three rows: the school
/// breaks it, not the platform. No DOB, student id, category or house.
/// </summary>
public static class DistrictSelection
{
    private const int SelectionDepth = 2;

    public static readonly string[] CsvHeader =
    [
        "Event code", "Event", "Division", "Selection", "Name", "Year level", "Result",
    ];

    public sealed record SelectionRow(
        string EventCode, string EventName, string Division, int Selection,
        string Name, int YearLevel, string Result);

    public static async Task<IReadOnlyList<SelectionRow>> BuildAsync(
        CarnivalAppDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var ladder = await EventLadder.BuildAsync(db, carnivalId, ct);
        var rows = new List<SelectionRow>();
        // The ladder arrives in division order, so only selection → name is sorted here.
        foreach (var division in ladder.Where(d => d.Tier == EventTier.Competitive))
        {
            var name = DivisionNaming.Division(division.AgeGroup, division.Category);
            rows.AddRange(division.Standings
                .Where(s => !s.IsTeam && s.Rank <= SelectionDepth)
                .OrderBy(s => s.Rank)
                .ThenBy(s => s.HolderDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(s => new SelectionRow(
                    division.EventCode, division.EventName, name, s.Rank,
                    s.HolderDisplayName, s.YearLevel, s.FormattedValue)));
        }
        return rows;
    }

    public static IReadOnlyList<string?> CsvRow(SelectionRow r) =>
    [
        r.EventCode,
        r.EventName,
        r.Division,
        r.Selection.ToString(CultureInfo.InvariantCulture),
        r.Name,
        r.YearLevel.ToString(CultureInfo.InvariantCulture),
        r.Result,
    ];
}
