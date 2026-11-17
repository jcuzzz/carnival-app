using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Carnivals;

public sealed record UncoveredAgeDto(string AgeGroup, int Students);

// A carnival's ages are its saved profile, else the school's range.
public static class CarnivalAges
{
    public const string AgesRequiredDetail =
        "This carnival has no ages yet. Set the school's ages in School settings first.";

    public static DivisionProfile? Default(SchoolAges? ages) =>
        ages is { IsValid: true } range ? DivisionProfile.FromSchoolAges(range.Youngest, range.Oldest) : null;

    public static async Task<DivisionProfile?> ReadSavedAsync(
        CarnivalAppDbContext db, Guid carnivalId, CancellationToken ct) =>
        DivisionProfile.FromRows(await db.CarnivalDivisions.AsNoTracking()
            .Where(d => d.CarnivalId == carnivalId)
            .ToListAsync(ct));

    public static IReadOnlyList<UncoveredAgeDto> Uncovered(DivisionProfile? profile, RosterFacts roster) =>
        profile is null
            ? []
            : [.. roster.StudentsByAge
                .Where(age => !profile.Covers(age.Key))
                .OrderBy(age => AgeScope.Order(age.Key))
                .ThenBy(age => age.Key, StringComparer.Ordinal)
                .Select(age => new UncoveredAgeDto(age.Key, age.Value))];

    public static IReadOnlyList<string> WithoutStudents(DivisionProfile? profile, RosterFacts roster) =>
        profile is null
            ? []
            : [.. profile.AgeGroups
                .Where(age => !roster.StudentsByAge.Keys.Any(student => AgeScope.Admits(age, student)))
                .OrderBy(AgeScope.Order)
                .ThenBy(age => age, StringComparer.Ordinal)];

    public static void MapCarnivalAgesEndpoints(this IEndpointRouteBuilder apiV1)
    {
        apiV1.MapPost("/carnivals/{id:guid}/divisions/extend-ages", ExtendAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ExtendAgesResponse>();
    }

    public sealed record ExtendAgesRequest(int? Youngest);

    public sealed record ExtendAgesResponse(IReadOnlyList<string> AddedAges, int DivisionsAdded);

    /// <summary>
    /// Lowers the carnival's youngest age, and adds the new ages' divisions to every event that has
    /// a division for the carnival's current youngest age. One write, one audit row.
    /// </summary>
    private static async Task<IResult> ExtendAsync(
        Guid id, ExtendAgesRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit,
        [FromServices] SchoolSettingsProvider school, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireEditable(carnival, "Ages cannot be added") is { } refusal)
            return refusal;
        if (request.Youngest is not { } youngest)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "youngest is required.");
        if (youngest < SchoolAges.Minimum)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"youngest must be at least {SchoolAges.Minimum}.");

        // Tracked: the profile is replaced, and a concurrent replace must conflict.
        var profileRows = await db.CarnivalDivisions.Where(d => d.CarnivalId == id).ToListAsync(ct);
        var current = DivisionProfile.FromRows(profileRows)
            ?? Default((await school.GetAsync(ct)).Ages);
        if (current is null)
            return ApiError.Conflict(ErrorCodes.AgesRequired, AgesRequiredDetail);
        if (current.YoungestAge() is not { } oldYoungest)
            return ApiError.Conflict(ErrorCodes.AgeAlreadyCovered,
                "This carnival's ages are not a range from a youngest age, so none can be added below it.");
        if (youngest >= oldYoungest)
            return ApiError.Conflict(ErrorCodes.AgeAlreadyCovered,
                $"This carnival already has age {youngest}; only a younger age can be added.");

        var added = DivisionProfile.AgesFrom(youngest, oldYoungest - 1).ToList();
        var requested = new DivisionProfileDto(
            [.. added, .. current.AgeGroups], current.Categories, current.Tiers);
        if (DivisionProfile.Validate(requested) is { } badProfile)
            return ApiError.BadRequest(badProfile.Code, badProfile.Detail);
        var updated = DivisionProfile.From(requested);

        var events = await db.CarnivalEvents.AsNoTracking()
            .Where(e => e.CarnivalId == id)
            .Select(e => new
            {
                e.EventDefinitionId, e.AgeGroup, e.Category, e.Tier,
                e.ScoringMode, e.ScoresPoints, e.ScheduleSlotId, e.SlotOrder,
            })
            .ToListAsync(ct);
        var alreadyScheduled = events
            .Select(e => new DivisionGenerator.DivisionKey(e.EventDefinitionId, e.AgeGroup, e.Category, e.Tier))
            .ToHashSet();
        // An event that starts above the youngest age (a 15 to 18+ event) is left alone.
        var youngestCode = current.AgeGroups.First(code =>
            AgeScope.TryLowerBound(code, out var age) && age == oldYoungest);
        // A new division copies the scoring and program place of the division it extends.
        var extended = events.Where(e => e.AgeGroup == youngestCode).ToList();
        var definitionIds = extended.Select(e => e.EventDefinitionId).Distinct().ToList();
        // Generation refuses an inactive definition, so extending skips one.
        var definitions = await db.EventDefinitions
            .Where(d => definitionIds.Contains(d.Id) && d.IsActive)
            .ToDictionaryAsync(d => d.Id, ct);

        var created = new List<CarnivalEvent>();
        foreach (var sibling in extended)
        {
            if (!definitions.TryGetValue(sibling.EventDefinitionId, out var definition)) continue;
            var outcome = DivisionGenerator.Generate(
                id, definition,
                added.Select(age => new DivisionSpec(age, sibling.Category, sibling.Tier)),
                alreadyScheduled, sibling.ScoringMode, sibling.ScoresPoints);
            foreach (var division in outcome.Created)
            {
                division.ScheduleSlotId = sibling.ScheduleSlotId;
                division.SlotOrder = sibling.SlotOrder;
            }
            created.AddRange(outcome.Created);
        }

        db.CarnivalDivisions.RemoveRange(profileRows);
        db.CarnivalDivisions.AddRange(updated.ToRows(id));
        db.CarnivalEvents.AddRange(created);
        // The profile is read-modify-written, as the settings PUT does.
        carnival.Version++;
        carnival.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalExtendAges, nameof(Carnival), id.ToString(),
            priorValues: new { ageGroups = current.AgeGroups },
            newValues: new
            {
                ageGroups = updated.AgeGroups,
                addedAges = added,
                divisions = created
                    .Select(e => new { e.Id, e.EventDefinitionId, e.AgeGroup, e.Category, e.Tier })
                    .ToList(),
            },
            ruleVersion: carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "carnival", ct) is { } conflict)
            return conflict;
        return Results.Ok(new ExtendAgesResponse(added, created.Count));
    }
}
