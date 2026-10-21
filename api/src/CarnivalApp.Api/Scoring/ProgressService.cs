using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Records;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Scoring;

// Progress is about recording, not points: every event counts, and DNS/DNF/DQ is recorded.
public static class ProgressService
{
    public const string NotStarted = "notStarted";
    public const string InProgress = "inProgress";
    public const string Complete = "complete";

    public const string New = "new";
    public const string Equalled = "equalled";

    public sealed record ProgressTotals(
        int Events, int EventsComplete, int EventsInProgress, int EventsNotStarted,
        int Expected, int Recorded);

    // Slot fields are null for an unscheduled event, which sorts last and is never hidden.
    public sealed record EventProgress(
        Guid CarnivalEventId,
        string DefinitionName,
        string DivisionLabel,
        Guid? SlotId,
        string? SlotLabel,
        string? Stream,
        TimeOnly? StartTime,
        int SortOrder,
        int Expected,
        int Recorded,
        string Status);

    // No per-student payload: counts about a house only.
    public sealed record HouseParticipation(string HouseCode, int Competitors, int Entered, int Recorded);

    // The only row here with a name: a record holder is public by nature. It carries no category.
    public sealed record RecordToday(
        Guid CarnivalEventId,
        string DefinitionName,
        string DivisionLabel,
        Guid EntryId,
        string DisplayName,
        string HouseCode,
        string Value,
        string Kind,
        string? Standing);

    public sealed record CarnivalProgress(
        Guid CarnivalId,
        bool Provisional,
        ProgressTotals Totals,
        IReadOnlyList<EventProgress> Events,
        IReadOnlyList<HouseParticipation> Participation,
        IReadOnlyList<RecordToday> RecordsToday);

    public static async Task<CarnivalProgress?> ComputeAsync(
        SportsDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == carnivalId, ct);
        if (carnival is null) return null;

        var events = await EventsAsync(db, carnivalId, ct);
        var totals = new ProgressTotals(
            events.Count,
            events.Count(e => e.Status == Complete),
            events.Count(e => e.Status == InProgress),
            events.Count(e => e.Status == NotStarted),
            events.Sum(e => e.Expected),
            events.Sum(e => e.Recorded));

        var finalised = carnival.Status == CarnivalStatus.Finalised;
        return new CarnivalProgress(
            carnivalId,
            Provisional: !finalised,
            totals,
            events,
            await ParticipationAsync(db, carnivalId, carnival.Status, ct),
            await RecordsTodayAsync(db, carnivalId, finalised, ct));
    }

    private sealed record EventRow(
        Guid Id, string DefinitionCode, string DefinitionName,
        string AgeGroup,
        CompetitionCategory? Category, EventTier Tier, EventScoringMode EffectiveScoringMode,
        Guid? SlotId, string? SlotLabel, string? StreamName, TimeOnly? StartTime,
        int? StreamOrder, int? SlotSortOrder, int? SlotOrder,
        int Expected, int Recorded);

    private static async Task<List<EventProgress>> EventsAsync(
        SportsDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var rows = await db.CarnivalEvents.AsNoTracking()
            .Where(e => e.CarnivalId == carnivalId)
            .Select(e => new EventRow(
                e.Id,
                e.EventDefinition.Code,
                e.EventDefinition.Name,
                e.AgeGroup,
                e.Category,
                e.Tier,
                e.EffectiveScoringMode,
                e.ScheduleSlotId,
                e.ScheduleSlot == null ? null : e.ScheduleSlot.Label,
                e.ScheduleSlot == null ? null : e.ScheduleSlot.Stream.Name,
                e.ScheduleSlot == null ? null : e.ScheduleSlot.StartTime,
                e.ScheduleSlot == null ? null : (int?)e.ScheduleSlot.Stream.SortOrder,
                e.ScheduleSlot == null ? null : (int?)e.ScheduleSlot.SortOrder,
                e.SlotOrder,
                e.Entries.Count,
                // Entries with a result, not results, so recorded never exceeds expected.
                e.Entries.Count(en => en.Results.AsQueryable().Any(JumpCard.Recorded))))
            .ToListAsync(ct);

        // Ordered in memory to keep the query provider-clean; a carnival's event list is small.
        return [.. rows
            .OrderBy(e => e.StreamOrder ?? int.MaxValue)
            .ThenBy(e => e.SlotSortOrder ?? int.MaxValue)
            .ThenBy(e => e.SlotOrder ?? int.MaxValue)
            .ThenBy(e => e.DefinitionCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.AgeGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.Tier)
            .Select(Project)];
    }

    private static EventProgress Project(EventRow e) => new(
        e.Id,
        e.DefinitionName,
        DivisionNaming.Division(e.AgeGroup, e.Category),
        e.SlotId, e.SlotLabel, e.StreamName, e.StartTime,
        e.SlotOrder ?? 0,
        e.Expected,
        e.Recorded,
        StatusOf(e));

    private static string StatusOf(EventRow e)
    {
        // An event nobody entered is not done; the same rule as CarnivalEventEndpoints.IsComplete.
        if (e.Expected == 0) return NotStarted;
        // A Social-scored event owes no results, so its entries alone complete it.
        if (e.EffectiveScoringMode == EventScoringMode.Social || e.Recorded >= e.Expected)
            return Complete;
        return e.Recorded == 0 ? NotStarted : InProgress;
    }

    private sealed record ParticipantRow(string HouseCode, Guid StudentId, bool Recorded);

    private static async Task<List<HouseParticipation>> ParticipationAsync(
        SportsDbContext db, Guid carnivalId, CarnivalStatus status, CancellationToken ct)
    {
        // There is no per-carnival roster: every active student's current snapshot.
        var roster = await db.RosterSnapshots.AsNoTracking()
            .Where(s => s.IsCurrent && s.Student.IsActive)
            .GroupBy(s => s.HouseCode)
            .Select(g => new { HouseCode = g.Key, Competitors = g.Count() })
            .ToListAsync(ct);

        var individuals = await db.Entries.AsNoTracking()
            .Where(e => e.CarnivalEvent.CarnivalId == carnivalId && e.StudentId != null)
            .Select(e => new ParticipantRow(
                e.SnapshotHouseCode,
                e.StudentId!.Value,
                e.Results.AsQueryable().Any(JumpCard.Recorded)))
            .ToListAsync(ct);

        // A relay's swimmers are on its legs; the team's single result is the parent entry's.
        var legs = await db.RelayLegs.AsNoTracking()
            .Where(l => l.Entry.CarnivalEvent.CarnivalId == carnivalId)
            .Select(l => new ParticipantRow(
                l.SnapshotHouseCode,
                l.StudentId,
                l.Entry.Results.AsQueryable().Any(JumpCard.Recorded)))
            .ToListAsync(ct);

        var entered = new Dictionary<Guid, (string HouseCode, bool Recorded)>();
        foreach (var row in individuals.Concat(legs))
        {
            var seen = entered.TryGetValue(row.StudentId, out var prior);
            entered[row.StudentId] = (seen ? prior.HouseCode : row.HouseCode,
                (seen && prior.Recorded) || row.Recorded);
        }

        var byHouse = entered.Values
            .GroupBy(p => p.HouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Entered: g.Count(), Recorded: g.Count(p => p.Recorded)),
                StringComparer.OrdinalIgnoreCase);

        // The database groups by its own collation, so 'BANKSIA' and 'Banksia' can be two rows.
        var competitors = roster
            .GroupBy(r => r.HouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Competitors), StringComparer.OrdinalIgnoreCase);

        // The houses the standings list, plus any still on the roster unless the carnival is finalised.
        var listed = await StandingHouses.ListAsync(db, carnivalId, status, ct);
        var houses = (status == CarnivalStatus.Finalised
                ? listed
                : listed.Union(competitors.Keys, StringComparer.OrdinalIgnoreCase))
            .Union(byHouse.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase);

        return [.. houses.Select(house =>
        {
            var counts = byHouse.GetValueOrDefault(house);
            return new HouseParticipation(
                house, competitors.GetValueOrDefault(house), counts.Entered, counts.Recorded);
        })];
    }

    private sealed record Minted(
        Guid EventDefinitionId, string AgeGroup, CompetitionCategory Category,
        bool Voided, decimal? PreviousValue);

    // The comparison finalisation mints with, writing nothing: records mint only at finalisation.
    // Once finalised the book holds this carnival's own records, so those are read back instead.
    private static async Task<List<RecordToday>> RecordsTodayAsync(
        SportsDbContext db, Guid carnivalId, bool finalised, CancellationToken ct)
    {
        var ladder = await EventLadder.BuildAsync(db, carnivalId, ct);
        var contested = ladder
            .Where(d => d.Tier == EventTier.Competitive
                        && d.Category is CompetitionCategory.M or CompetitionCategory.F
                        && d.Standings.Count > 0)
            .ToList();
        if (contested.Count == 0) return [];

        var definitionIds = contested.Select(d => d.EventDefinitionId).Distinct().ToList();
        var blobs = (await db.EventDefinitions.AsNoTracking()
                .Where(d => definitionIds.Contains(d.Id))
                .Select(d => new { d.Id, d.RecordCategoriesJson })
                .ToListAsync(ct))
            .ToDictionary(d => d.Id, d => RecordCategories.Parse(d.RecordCategoriesJson));

        var eligible = contested
            .Where(d => blobs[d.EventDefinitionId] is { } categories
                        && categories.Contains((d.AgeGroup, d.Category!.Value)))
            .ToList();
        if (eligible.Count == 0) return [];

        var standingRows = await db.SchoolRecords.AsNoTracking()
            .Where(r => definitionIds.Contains(r.EventDefinitionId)
                        && r.Status == SchoolRecordStatus.Ratified
                        && r.EffectiveTo == null
                        && r.VoidedAtUtc == null)
            .ToListAsync(ct);
        var book = standingRows
            .GroupBy(r => (r.EventDefinitionId, r.AgeGroup, r.Category))
            .ToDictionary(g => g.Key, g => g.ToList());

        var minted = finalised
            ? (await db.SchoolRecords.AsNoTracking()
                    .Where(r => r.Result.Entry.CarnivalEvent.CarnivalId == carnivalId)
                    .Select(r => new Minted(
                        r.EventDefinitionId, r.AgeGroup, r.Category,
                        r.VoidedAtUtc != null,
                        r.PreviousRecord == null ? null : r.PreviousRecord.RecordValue))
                    .ToListAsync(ct))
                .GroupBy(m => (m.EventDefinitionId, m.AgeGroup, m.Category))
                .ToDictionary(g => g.Key, g => g.First())
            : [];

        var candidates = new List<RecordToday>();
        foreach (var division in eligible)
        {
            var category = division.Category!.Value;
            var best = division.Standings[0];
            var key = (division.EventDefinitionId, division.AgeGroup, category);

            string kind;
            decimal standing;
            if (minted.TryGetValue(key, out var own))
            {
                // A seed stood above nothing, and a voided record was never one.
                if (own.Voided || own.PreviousValue is not { } previous) continue;
                kind = New;
                standing = previous;
            }
            else
            {
                var current = book.TryGetValue(key, out var rows)
                    ? RecordDetection.Standing(rows, division.HigherBetter)
                    : null;

                // An empty book is seeded at finalisation, not shown as a candidate.
                if (current is null) continue;
                standing = current.RecordValue;

                // Finalised without minting here, a faster swim set no record.
                if (!finalised && RecordDetection.Beats(best.Value, standing, division.HigherBetter))
                    kind = New;
                else if (best.Value == standing)
                    kind = Equalled;
                else
                    continue;
            }

            candidates.Add(new RecordToday(
                division.CarnivalEventId,
                division.EventName,
                DivisionNaming.Division(division.AgeGroup, division.Category),
                best.EntryId,
                best.HolderDisplayName,
                best.HouseCode,
                best.FormattedValue,
                kind,
                EventLadder.FormatValue(division.MeasureType, standing)));
        }
        return candidates;
    }
}
