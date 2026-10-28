using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Records;

// /records sits outside /reports/ so the service worker caches it for the deck badge offline;
// the cache predicate is anchored, so the administration routes under /records/ are not cached.
public static class RecordEndpoints
{
    // Matches the VoidReason column, so a long reason is a 400 rather than a database error.
    private const int MaxVoidReasonLength = 1000;

    public static void MapRecordEndpoints(this IEndpointRouteBuilder apiV1)
    {
        apiV1.MapGet("/records", ListRecordsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<SchoolRecordDto>>();

        apiV1.MapGet("/records/administration", ListForAdministrationAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<IReadOnlyList<SchoolRecordDto>>();

        apiV1.MapPost("/records/{id:guid}/void", VoidRecordAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<SchoolRecordDto>();
    }

    // Category is the division's, never a student's. The book route excludes voided rows, so a
    // Viewer never sees who voided what.
    public sealed record SchoolRecordDto(
        Guid Id,
        Guid EventDefinitionId,
        string EventCode,
        string EventName,
        string AgeGroup,
        CompetitionCategory Category,
        string Division,
        decimal Value,
        string FormattedValue,
        bool HigherBetter,
        string HolderDisplayName,
        DateOnly? SetOn,
        string? CarnivalName,
        bool Voided,
        DateTimeOffset? VoidedAtUtc,
        string? VoidedBySubject,
        string? VoidReason);

    private sealed record Standing(
        Guid Id, Guid EventDefinitionId, string EventCode, string EventName, MeasureType MeasureType,
        string AgeGroup, CompetitionCategory Category, decimal RecordValue, string HolderDisplayName,
        DateOnly? EffectiveFrom, string? CarnivalName,
        DateTimeOffset? VoidedAtUtc, string? VoidedBySubject, string? VoidReason);

    private static IQueryable<Standing> Project(IQueryable<SchoolRecord> query) =>
        query.Select(r => new Standing(
            r.Id,
            r.EventDefinitionId,
            r.EventDefinition.Code,
            r.EventDefinition.Name,
            r.EventDefinition.MeasureType,
            r.AgeGroup,
            r.Category,
            r.RecordValue,
            r.HolderDisplayName,
            r.EffectiveFrom,
            r.Result.Entry.CarnivalEvent.Carnival.Name,
            r.VoidedAtUtc,
            r.VoidedBySubject,
            r.VoidReason));

    private static async Task<IResult> ListRecordsAsync(
        [FromQuery] string? sport, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        // The same standing predicate record detection applies.
        var query = db.SchoolRecords.AsNoTracking()
            .Where(r => r.Status == SchoolRecordStatus.Ratified
                        && r.EffectiveTo == null
                        && r.VoidedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(sport))
        {
            var wanted = sport.Trim();
            query = query.Where(r => r.EventDefinition.Sport.ToLower() == wanted.ToLower());
        }

        var standing = await Project(query).ToListAsync(ct);

        var book = standing
            .GroupBy(r => (r.EventDefinitionId, r.AgeGroup, r.Category))
            .Select(g => Pick(g.ToList()))
            .OrderBy(r => r.EventCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => AgeScope.Order(r.AgeGroup))
            .ThenBy(r => r.AgeGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Category)
            .ToList();
        return Results.Ok(book);
    }

    // What stands plus every voided row with its reason; superseded rows left the ordinary way.
    private static async Task<IResult> ListForAdministrationAsync(
        [FromQuery] string? sport, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var query = db.SchoolRecords.AsNoTracking()
            .Where(r => (r.Status == SchoolRecordStatus.Ratified && r.EffectiveTo == null)
                        || r.VoidedAtUtc != null);
        if (!string.IsNullOrWhiteSpace(sport))
        {
            var wanted = sport.Trim();
            query = query.Where(r => r.EventDefinition.Sport.ToLower() == wanted.ToLower());
        }

        var rows = await Project(query).ToListAsync(ct);
        var listed = rows
            .Select(Describe)
            .OrderBy(r => r.EventCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => AgeScope.Order(r.AgeGroup))
            .ThenBy(r => r.AgeGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Voided)
            .ThenBy(r => r.SetOn ?? DateOnly.MaxValue)
            .ThenBy(r => r.Id)
            .ToList();
        return Results.Ok(listed);
    }

    public sealed record VoidRecordRequest(string? Reason);

    // Void, never delete: the row and its evidence stay.
    private static async Task<IResult> VoidRecordAsync(
        Guid id, VoidRecordRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired,
                "A reason is required to void a school record — it is the only account of why "
                + "the book changed.");
        if (reason.Length > MaxVoidReasonLength)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"reason is limited to {MaxVoidReasonLength} characters.");

        // Reinstatement reads the whole category then writes into it; without one transaction two
        // voids in a category can each see the other's predecessor as standing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var record = await db.SchoolRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null)
            return ApiError.NotFound(ErrorCodes.SchoolRecordNotFound,
                "No school record exists with that id.");
        if (record.VoidedAtUtc is not null)
            return ApiError.Conflict(ErrorCodes.SchoolRecordAlreadyVoided,
                "That record is already voided. Voiding is one-way — read the list again.");

        var actor = SessionActor.From(ctx);
        var prior = new
        {
            voided = false,
            status = record.Status.ToString(),
            value = record.RecordValue,
            holder = record.HolderDisplayName,
            effectiveTo = record.EffectiveTo,
        };
        record.VoidedAtUtc = DateTimeOffset.UtcNow;
        record.VoidedBySubject = actor.Subject;
        record.VoidReason = reason;

        var reinstated = await ReinstateAsync(db, record, ct);
        audit.Record(actor, AuditActions.SchoolRecordVoid, nameof(SchoolRecord), id.ToString(),
            priorValues: prior,
            newValues: new
            {
                voided = true,
                voidedAtUtc = record.VoidedAtUtc,
                reinstatedRecordId = reinstated?.Id,
                reinstatedValue = reinstated?.RecordValue,
                reinstatedHolder = reinstated?.HolderDisplayName,
            },
            reason: reason);

        // Immediately before the save, while the mutations above are unwritten: catches a competing
        // void that reinstated into this category behind the guard.
        if (reinstated is not null && await StandingElsewhereAsync(db, record, reinstated, ct))
            return CarnivalGuards.ConcurrencyConflict(
                "Void refused: another record began standing in that category while this void was "
                + "running, so nothing was voided. Re-read the list and try again.");
        await db.SaveChangesAsync(ct);

        var voided = await Project(db.SchoolRecords.AsNoTracking().Where(r => r.Id == id))
            .SingleAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(Describe(voided));
    }

    // Reads the database, not the tracked graph.
    private static Task<bool> StandingElsewhereAsync(
        CarnivalAppDbContext db, SchoolRecord voided, SchoolRecord reinstated, CancellationToken ct) =>
        db.SchoolRecords.AsNoTracking().AnyAsync(
            r => r.EventDefinitionId == voided.EventDefinitionId
                 && r.AgeGroup == voided.AgeGroup
                 && r.Category == voided.Category
                 && r.Id != voided.Id
                 && r.Id != reinstated.Id
                 && r.Status == SchoolRecordStatus.Ratified
                 && r.EffectiveTo == null
                 && r.VoidedAtUtc == null, ct);

    // Puts back the nearest live record the voided one superseded; an empty category would let the
    // next carnival seed a record slower than one that legitimately stood.
    private static async Task<SchoolRecord?> ReinstateAsync(
        CarnivalAppDbContext db, SchoolRecord voided, CancellationToken ct)
    {
        if (voided.Status != SchoolRecordStatus.Ratified || voided.EffectiveTo is not null)
            return null;

        var category = await db.SchoolRecords
            .Where(r => r.EventDefinitionId == voided.EventDefinitionId
                        && r.AgeGroup == voided.AgeGroup
                        && r.Category == voided.Category)
            .ToListAsync(ct);
        // If a second row already stands here, reinstating would make a third.
        if (category.Any(r => r.Id != voided.Id
                              && r.Status == SchoolRecordStatus.Ratified
                              && r.EffectiveTo is null
                              && r.VoidedAtUtc is null))
            return null;

        var byId = category.ToDictionary(r => r.Id);
        var seen = new HashSet<Guid>();
        var cursor = voided.PreviousRecordId;
        while (cursor is Guid previousId && seen.Add(previousId)
               && byId.TryGetValue(previousId, out var previous))
        {
            if (previous.VoidedAtUtc is null)
            {
                previous.Status = SchoolRecordStatus.Ratified;
                previous.EffectiveTo = null;
                return previous;
            }
            cursor = previous.PreviousRecordId;
        }
        return null;
    }

    // Must order exactly as RecordDetection.Standing does.
    private static SchoolRecordDto Pick(List<Standing> rows)
    {
        var higherBetter = Placement.HigherIsBetter(rows[0].MeasureType);
        return Describe(rows
            .OrderBy(x => higherBetter ? -x.RecordValue : x.RecordValue)
            .ThenBy(x => x.EffectiveFrom ?? DateOnly.MaxValue)
            .ThenBy(x => x.Id)
            .First());
    }

    private static SchoolRecordDto Describe(Standing r) => new(
        r.Id, r.EventDefinitionId, r.EventCode, r.EventName, r.AgeGroup, r.Category,
        DivisionNaming.Division(r.AgeGroup, r.Category),
        r.RecordValue, EventLadder.FormatValue(r.MeasureType, r.RecordValue),
        Placement.HigherIsBetter(r.MeasureType), r.HolderDisplayName, r.EffectiveFrom, r.CarnivalName,
        r.VoidedAtUtc is not null, r.VoidedAtUtc, r.VoidedBySubject, r.VoidReason);
}
