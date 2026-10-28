using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Records;

// Runs only inside carnival finalisation's transaction: finalisation is the ratifying act.
public static class RecordDetection
{
    // Concurrent finalisations can leave two standing rows (the invariant is service-layer only),
    // so the read is a total order and takes the first.
    public static SchoolRecord? Standing(IEnumerable<SchoolRecord> rows, bool higherBetter) =>
        rows.OrderBy(r => higherBetter ? -r.RecordValue : r.RecordValue)
            .ThenBy(r => r.EffectiveFrom ?? DateOnly.MaxValue)
            .ThenBy(r => r.Id)
            .FirstOrDefault();

    // Stages rows and audit events on the caller's DbContext; its transaction commits them.
    public static async Task<int> MintAsync(
        CarnivalAppDbContext db, AuditService audit, Carnival carnival, AuditActor actor,
        string? reason, DateTimeOffset finalisedAtUtc, CancellationToken ct)
    {
        var ladder = await EventLadder.BuildAsync(db, carnival.Id, ct);
        // Tier comes off the event instance, never the catalogue blob.
        var contested = ladder
            .Where(d => d.Tier == EventTier.Competitive
                        && d.Category is CompetitionCategory.M or CompetitionCategory.F)
            .ToList();
        if (contested.Count == 0) return 0;

        var definitionIds = contested.Select(d => d.EventDefinitionId).Distinct().ToList();
        var definitions = await db.EventDefinitions.AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Code, d.RecordCategoriesJson })
            .ToListAsync(ct);
        var blobs = definitions.ToDictionary(d => d.Id, d => RecordCategories.Parse(d.RecordCategoriesJson));
        var codes = definitions.ToDictionary(d => d.Id, d => d.Code);

        var eligible = contested
            .Where(d => blobs[d.EventDefinitionId] is { } categories
                        && categories.Contains((d.AgeGroup, d.Category!.Value))
                        && d.Standings.Count > 0)
            .ToList();
        if (eligible.Count == 0) return 0;

        // Tracked: a superseded row is closed, not replaced. A voided row cannot block a slower
        // legitimate time from seeding the category.
        var standingRows = await db.SchoolRecords
            .Where(r => definitionIds.Contains(r.EventDefinitionId)
                        && r.Status == SchoolRecordStatus.Ratified
                        && r.EffectiveTo == null
                        && r.VoidedAtUtc == null)
            .ToListAsync(ct);
        var book = standingRows
            .GroupBy(r => (r.EventDefinitionId, r.AgeGroup, r.Category))
            .ToDictionary(g => g.Key, g => g.ToList());

        var minted = 0;
        foreach (var division in eligible)
        {
            var category = division.Category!.Value;
            var best = division.Standings[0];
            var current = book.TryGetValue((division.EventDefinitionId, division.AgeGroup, category), out var rows)
                ? Standing(rows, division.HigherBetter)
                : null;
            if (current is not null && !Beats(best.Value, current.RecordValue, division.HigherBetter))
                continue;

            var record = new SchoolRecord
            {
                Id = Guid.CreateVersion7(),
                EventDefinitionId = division.EventDefinitionId,
                AgeGroup = division.AgeGroup,
                Category = category,
                ResultId = best.ResultId,
                RecordValue = best.Value,
                HolderDisplayName = best.HolderDisplayName,
                PreviousRecordId = current?.Id,
                Status = SchoolRecordStatus.Ratified,
                ApprovedBySubject = actor.Subject,
                ApprovedAtUtc = finalisedAtUtc,
                ApprovalNote = reason,
                EffectiveFrom = carnival.Date,
                CreatedAtUtc = finalisedAtUtc,
            };
            db.SchoolRecords.Add(record);
            if (current is not null)
            {
                current.Status = SchoolRecordStatus.Superseded;
                // Clamped: finalising after a later-dated carnival must not leave a negative span.
                current.EffectiveTo = current.EffectiveFrom is { } from && from > carnival.Date
                    ? from
                    : carnival.Date;
            }
            minted++;

            audit.Record(actor, AuditActions.SchoolRecordMint, nameof(SchoolRecord), record.Id.ToString(),
                priorValues: current is null
                    ? null
                    : new { value = current.RecordValue, holder = current.HolderDisplayName },
                newValues: new
                {
                    definitionCode = codes[division.EventDefinitionId],
                    ageGroup = division.AgeGroup,
                    category = category.ToString(),
                    value = best.Value,
                    formattedValue = best.FormattedValue,
                    holder = best.HolderDisplayName,
                    previousRecordId = current?.Id,
                },
                reason: reason,
                ruleVersion: carnival.RuleVersion);
        }
        return minted;
    }

    // Strict: equalling never supersedes the earlier holder.
    public static bool Beats(decimal candidate, decimal standing, bool higherBetter) =>
        higherBetter ? candidate > standing : candidate < standing;
}
