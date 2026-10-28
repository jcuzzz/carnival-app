using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Carnivals;

/// <summary>
/// The schedule is advisory and never gating: entry, result, eligibility and lifecycle paths must
/// not consult it, because events and students run out of order as a matter of course.
/// </summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var schedule = apiV1.MapGroup("/carnivals/{id:guid}/schedule");

        schedule.MapGet("/", GetScheduleAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<ScheduleDto>();

        schedule.MapPost("/streams", CreateStreamAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleStreamDto>(StatusCodes.Status201Created);
        schedule.MapPut("/streams/{streamId:guid}", UpdateStreamAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleStreamDto>();
        schedule.MapDelete("/streams/{streamId:guid}", DeleteStreamAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);

        schedule.MapPost("/streams/{streamId:guid}/slots", CreateSlotAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleSlotDto>(StatusCodes.Status201Created);
        schedule.MapPut("/slots/{slotId:guid}", UpdateSlotAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleSlotDto>();
        schedule.MapDelete("/slots/{slotId:guid}", DeleteSlotAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);

        schedule.MapPost("/slots/{slotId:guid}/events", PlaceEventAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleSlotDto>();
        schedule.MapDelete("/slots/{slotId:guid}/events/{eventId:guid}", RemoveEventAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<ScheduleSlotDto>();
    }

    // ---- Projections -----------------------------------------------------

    /// <summary>Event ids only: the client already caches the event list.</summary>
    public sealed record ScheduleDto(Guid CarnivalId, IReadOnlyList<ScheduleStreamDto> Streams);

    public sealed record ScheduleStreamDto(
        Guid Id, string Name, int SortOrder, IReadOnlyList<ScheduleSlotDto> Slots);

    /// <summary>
    /// Times are local clock times, not instants: the carnival owns the date and the school the
    /// timezone.
    /// </summary>
    public sealed record ScheduleSlotDto(
        Guid Id,
        Guid StreamId,
        int SortOrder,
        TimeOnly? StartTime,
        TimeOnly? EndTime,
        ScheduleSlotKind Kind,
        string? Label,
        /// <summary>Cosmetic; never a result link.</summary>
        string? AdjustmentCategory,
        IReadOnlyList<Guid> EventIds);

    private sealed record SlotRow(
        Guid Id, Guid StreamId, int SortOrder, TimeOnly? StartTime, TimeOnly? EndTime,
        ScheduleSlotKind Kind, string? Label, string? AdjustmentCategory,
        List<PlacementRow> Events);

    private sealed record PlacementRow(Guid EventId, int? SlotOrder);

    private sealed record StreamRow(Guid Id, string Name, int SortOrder, List<SlotRow> Slots);

    /// <summary>
    /// Ordered in memory throughout: SQLite's <c>TimeOnly</c> ordering does not translate the way
    /// Npgsql's does, and a schedule is tiny.
    /// </summary>
    private static ScheduleDto Project(Guid carnivalId, List<StreamRow> streams) => new(
        carnivalId,
        streams
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new ScheduleStreamDto(s.Id, s.Name, s.SortOrder, ProjectSlots(s.Slots)))
            .ToList());

    private static IReadOnlyList<ScheduleSlotDto> ProjectSlots(List<SlotRow> slots) =>
        slots.OrderBy(sl => sl.SortOrder).ThenBy(sl => sl.StartTime ?? TimeOnly.MaxValue)
            .Select(Project)
            .ToList();

    private static ScheduleSlotDto Project(SlotRow slot) => new(
        slot.Id, slot.StreamId, slot.SortOrder, slot.StartTime, slot.EndTime,
        slot.Kind, slot.Label, slot.AdjustmentCategory,
        slot.Events
            .OrderBy(e => e.SlotOrder ?? int.MaxValue).ThenBy(e => e.EventId)
            .Select(e => e.EventId)
            .ToList());

    private static IQueryable<StreamRow> StreamRows(CarnivalAppDbContext db, Guid carnivalId) =>
        db.ScheduleStreams.AsNoTracking()
            .Where(s => s.CarnivalId == carnivalId)
            .Select(s => new StreamRow(s.Id, s.Name, s.SortOrder, s.Slots
                .Select(sl => new SlotRow(
                    sl.Id, sl.StreamId, sl.SortOrder, sl.StartTime, sl.EndTime,
                    sl.Kind, sl.Label, sl.AdjustmentCategory,
                    sl.Events.Select(e => new PlacementRow(e.Id, e.SlotOrder)).ToList()))
                .ToList()));

    private static async Task<IResult> SlotResponseAsync(
        CarnivalAppDbContext db, Guid slotId, CancellationToken ct)
    {
        var slot = await db.ScheduleSlots.AsNoTracking()
            .Where(sl => sl.Id == slotId)
            .Select(sl => new SlotRow(
                sl.Id, sl.StreamId, sl.SortOrder, sl.StartTime, sl.EndTime,
                sl.Kind, sl.Label, sl.AdjustmentCategory,
                sl.Events.Select(e => new PlacementRow(e.Id, e.SlotOrder)).ToList()))
            .FirstAsync(ct);
        return Results.Ok(Project(slot));
    }

    // ---- Read ------------------------------------------------------------

    private static async Task<IResult> GetScheduleAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct)) return CarnivalGuards.CarnivalNotFound();
        return Results.Ok(Project(id, await StreamRows(db, id).ToListAsync(ct)));
    }

    // ---- Guards ----------------------------------------------------------

    /// <summary>Finalised is the only refusal: a Closed program is still being tidied.</summary>
    private static async Task<(Carnival? Carnival, IResult? Refusal)> EditableCarnivalAsync(
        CarnivalAppDbContext db, Guid id, string attempt, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return (null, CarnivalGuards.CarnivalNotFound());
        return CarnivalGuards.RequireNotFinalised(carnival, attempt) is { } refusal
            ? (null, refusal)
            : (carnival, null);
    }

    private static IResult StreamNotFound() =>
        ApiError.NotFound(ErrorCodes.ScheduleStreamNotFound,
            "No schedule stream exists with that id in this carnival.");

    private static IResult SlotNotFound() =>
        ApiError.NotFound(ErrorCodes.ScheduleSlotNotFound,
            "No schedule slot exists with that id in this carnival.");

    private static Task<ScheduleStream?> FindStreamAsync(
        CarnivalAppDbContext db, Guid carnivalId, Guid streamId, CancellationToken ct) =>
        db.ScheduleStreams.FirstOrDefaultAsync(s => s.CarnivalId == carnivalId && s.Id == streamId, ct);

    private static Task<ScheduleSlot?> FindSlotAsync(
        CarnivalAppDbContext db, Guid carnivalId, Guid slotId, CancellationToken ct) =>
        db.ScheduleSlots
            .Include(sl => sl.Stream)
            .FirstOrDefaultAsync(sl => sl.Id == slotId && sl.Stream.CarnivalId == carnivalId, ct);

    internal static async Task<int> NextOrderAsync(IQueryable<int> existing, CancellationToken ct) =>
        await existing.AnyAsync(ct) ? await existing.MaxAsync(ct) + 1 : 0;

    // ---- Streams ---------------------------------------------------------

    public sealed record StreamRequest(string? Name, int? SortOrder);

    /// <summary>
    /// Case-insensitive because template re-application matches streams that way; compared in
    /// memory to stay provider-clean.
    /// </summary>
    private static async Task<bool> StreamNameTakenAsync(
        CarnivalAppDbContext db, Guid carnivalId, string name, Guid? exceptId, CancellationToken ct)
    {
        var names = await db.ScheduleStreams.AsNoTracking()
            .Where(s => s.CarnivalId == carnivalId && (exceptId == null || s.Id != exceptId))
            .Select(s => s.Name)
            .ToListAsync(ct);
        return names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IResult> CreateStreamAsync(
        Guid id, StreamRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (carnival, refusal) = await EditableCarnivalAsync(db, id, "Schedule streams cannot be changed", ct);
        if (refusal is not null) return refusal;

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "name is required (e.g. \"Pool\", \"Track\").");
        if (await StreamNameTakenAsync(db, id, name, exceptId: null, ct))
            return ApiError.Conflict(ErrorCodes.ScheduleStreamNameExists,
                $"A schedule stream named '{name}' already exists in this carnival.");

        var stream = new ScheduleStream
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = id,
            Name = name,
            SortOrder = request.SortOrder
                ?? await NextOrderAsync(db.ScheduleStreams.Where(s => s.CarnivalId == id).Select(s => s.SortOrder), ct),
        };
        db.ScheduleStreams.Add(stream);
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleStreamCreate, nameof(ScheduleStream),
            stream.Id.ToString(),
            newValues: new { stream.Name, stream.SortOrder },
            ruleVersion: carnival!.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/schedule/streams/{stream.Id}",
            new ScheduleStreamDto(stream.Id, stream.Name, stream.SortOrder, []));
    }

    private static async Task<IResult> UpdateStreamAsync(
        Guid id, Guid streamId, StreamRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, "Schedule streams cannot be changed", ct);
        if (refusal is not null) return refusal;

        var stream = await FindStreamAsync(db, id, streamId, ct);
        if (stream is null) return StreamNotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "name is required.");
        if (await StreamNameTakenAsync(db, id, name, streamId, ct))
            return ApiError.Conflict(ErrorCodes.ScheduleStreamNameExists,
                $"A schedule stream named '{name}' already exists in this carnival.");

        var prior = new { stream.Name, stream.SortOrder };
        stream.Name = name;
        stream.SortOrder = request.SortOrder ?? stream.SortOrder;
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleStreamUpdate, nameof(ScheduleStream),
            streamId.ToString(),
            priorValues: prior, newValues: new { stream.Name, stream.SortOrder });
        await db.SaveChangesAsync(ct);

        var slots = await db.ScheduleSlots.AsNoTracking()
            .Where(sl => sl.StreamId == streamId)
            .Select(sl => new SlotRow(
                sl.Id, sl.StreamId, sl.SortOrder, sl.StartTime, sl.EndTime,
                sl.Kind, sl.Label, sl.AdjustmentCategory,
                sl.Events.Select(e => new PlacementRow(e.Id, e.SlotOrder)).ToList()))
            .ToListAsync(ct);
        return Results.Ok(new ScheduleStreamDto(stream.Id, stream.Name, stream.SortOrder, ProjectSlots(slots)));
    }

    private static async Task<IResult> DeleteStreamAsync(
        Guid id, Guid streamId, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, "Schedule streams cannot be changed", ct);
        if (refusal is not null) return refusal;

        var stream = await db.ScheduleStreams
            .Include(s => s.Slots)
            .FirstOrDefaultAsync(s => s.CarnivalId == id && s.Id == streamId, ct);
        if (stream is null) return StreamNotFound();

        var slotIds = stream.Slots.Select(sl => sl.Id).ToList();
        // The slot FK takes no database action, so events are unscheduled here or the delete fails.
        var unscheduled = await UnscheduleAsync(db, slotIds, ct);
        var racesDetached = await DetachRacesAsync(db, slotIds, ct);
        db.ScheduleStreams.Remove(stream);
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleStreamDelete, nameof(ScheduleStream),
            streamId.ToString(),
            priorValues: new { stream.Name, stream.SortOrder, slots = slotIds.Count },
            newValues: new { deleted = true, eventsUnscheduled = unscheduled, racesDetached });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<int> UnscheduleAsync(
        CarnivalAppDbContext db, IReadOnlyCollection<Guid> slotIds, CancellationToken ct)
    {
        if (slotIds.Count == 0) return 0;
        var placed = await db.CarnivalEvents
            .Where(e => e.ScheduleSlotId != null && slotIds.Contains(e.ScheduleSlotId.Value))
            .ToListAsync(ct);
        foreach (var carnivalEvent in placed)
        {
            carnivalEvent.ScheduleSlotId = null;
            carnivalEvent.SlotOrder = null;
        }
        return placed.Count;
    }

    /// <summary>
    /// The race→slot FK is ClientSetNull, so without this deleting a slot with races fails at the
    /// database.
    /// </summary>
    private static async Task<int> DetachRacesAsync(
        CarnivalAppDbContext db, IReadOnlyCollection<Guid> slotIds, CancellationToken ct)
    {
        if (slotIds.Count == 0) return 0;
        var placed = await db.Races
            .Where(r => r.ScheduleSlotId != null && slotIds.Contains(r.ScheduleSlotId.Value))
            .ToListAsync(ct);
        foreach (var race in placed) race.ScheduleSlotId = null;
        return placed.Count;
    }

    // ---- Slots -----------------------------------------------------------

    /// <summary>
    /// PUT replaces (omitted time or category clears it); Label is required so a partial PUT is a
    /// 400, not a slot that loses its name. Omitted SortOrder/Kind are kept.
    /// </summary>
    public sealed record SlotRequest(
        TimeOnly? StartTime, TimeOnly? EndTime, ScheduleSlotKind? Kind,
        string? Label, string? AdjustmentCategory, int? SortOrder);

    private static IResult? ValidateSlot(SlotRequest request, ScheduleSlotKind kind, Carnival carnival)
    {
        if (request.EndTime is not null && request.StartTime is null)
            return ApiError.BadRequest(ErrorCodes.ScheduleTimesInvalid,
                "endTime needs a startTime — a slot cannot end without beginning.");
        if (request.StartTime is { } start && request.EndTime is { } end && end < start)
            return ApiError.BadRequest(ErrorCodes.ScheduleTimesInvalid,
                "endTime is before startTime.");
        if (string.IsNullOrWhiteSpace(request.Label))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "label is required — it is the line printed on the program " +
                "(\"FREESTYLE EVENTS\", \"Buses depart\"), for a block of events as much as for an item.");

        var category = request.AdjustmentCategory?.Trim();
        if (!string.IsNullOrEmpty(category))
        {
            // Cosmetic, but an unknown category would silently never match.
            var configured = CarnivalSettings.Parse(carnival.SettingsJson).AdjustmentCategories;
            if (!configured.Contains(category, StringComparer.OrdinalIgnoreCase))
                return ApiError.BadRequest(ErrorCodes.AdjustmentCategoryUnknown,
                    $"'{category}' is not a configured adjustment category. " +
                    $"Configured: {string.Join(", ", configured)}.",
                    new() { ["configuredCategories"] = configured });
        }
        return null;
    }

    private static async Task<IResult> CreateSlotAsync(
        Guid id, Guid streamId, SlotRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (carnival, refusal) = await EditableCarnivalAsync(db, id, "Schedule slots cannot be changed", ct);
        if (refusal is not null) return refusal;

        var stream = await FindStreamAsync(db, id, streamId, ct);
        if (stream is null) return StreamNotFound();

        var kind = request.Kind ?? ScheduleSlotKind.Events;
        if (ValidateSlot(request, kind, carnival!) is { } invalid) return invalid;

        var slot = new ScheduleSlot
        {
            Id = Guid.CreateVersion7(),
            StreamId = streamId,
            SortOrder = request.SortOrder
                ?? await NextOrderAsync(db.ScheduleSlots.Where(sl => sl.StreamId == streamId).Select(sl => sl.SortOrder), ct),
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Kind = kind,
            Label = request.Label?.Trim(),
            AdjustmentCategory = request.AdjustmentCategory?.Trim(),
        };
        db.ScheduleSlots.Add(slot);
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleSlotCreate, nameof(ScheduleSlot),
            slot.Id.ToString(),
            newValues: Audited(slot), ruleVersion: carnival!.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/schedule/slots/{slot.Id}",
            new ScheduleSlotDto(slot.Id, slot.StreamId, slot.SortOrder, slot.StartTime, slot.EndTime,
                slot.Kind, slot.Label, slot.AdjustmentCategory, []));
    }

    private static object Audited(ScheduleSlot slot) => new
    {
        slot.StreamId,
        slot.SortOrder,
        StartTime = slot.StartTime?.ToString("HH:mm"),
        EndTime = slot.EndTime?.ToString("HH:mm"),
        Kind = slot.Kind.ToString(),
        slot.Label,
        slot.AdjustmentCategory,
    };

    private static async Task<IResult> UpdateSlotAsync(
        Guid id, Guid slotId, SlotRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (carnival, refusal) = await EditableCarnivalAsync(db, id, "Schedule slots cannot be changed", ct);
        if (refusal is not null) return refusal;

        var slot = await FindSlotAsync(db, id, slotId, ct);
        if (slot is null) return SlotNotFound();

        var kind = request.Kind ?? slot.Kind;
        if (ValidateSlot(request, kind, carnival!) is { } invalid) return invalid;
        if (kind == ScheduleSlotKind.Item
            && await db.CarnivalEvents.AnyAsync(e => e.ScheduleSlotId == slotId, ct))
            return ApiError.Conflict(ErrorCodes.ScheduleSlotKindMismatch,
                "This slot lists event instances, so it cannot become a non-competition item. " +
                "Remove the events from it first.");
        // Checked separately: a slot can hold races while listing no events.
        if (kind == ScheduleSlotKind.Item
            && await db.Races.AnyAsync(r => r.ScheduleSlotId == slotId, ct))
            return ApiError.Conflict(ErrorCodes.ScheduleSlotKindMismatch,
                "Races run under this slot, so it cannot become a non-competition item. " +
                "Move or delete the races first.");

        var prior = Audited(slot);
        slot.SortOrder = request.SortOrder ?? slot.SortOrder;
        slot.StartTime = request.StartTime;
        slot.EndTime = request.EndTime;
        slot.Kind = kind;
        slot.Label = request.Label?.Trim();
        slot.AdjustmentCategory = request.AdjustmentCategory?.Trim();
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleSlotUpdate, nameof(ScheduleSlot),
            slotId.ToString(), priorValues: prior, newValues: Audited(slot));
        await db.SaveChangesAsync(ct);
        return await SlotResponseAsync(db, slotId, ct);
    }

    private static async Task<IResult> DeleteSlotAsync(
        Guid id, Guid slotId, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, "Schedule slots cannot be changed", ct);
        if (refusal is not null) return refusal;

        var slot = await FindSlotAsync(db, id, slotId, ct);
        if (slot is null) return SlotNotFound();

        var unscheduled = await UnscheduleAsync(db, [slotId], ct);
        var racesDetached = await DetachRacesAsync(db, [slotId], ct);
        var prior = Audited(slot);
        db.ScheduleSlots.Remove(slot);
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleSlotDelete, nameof(ScheduleSlot),
            slotId.ToString(),
            priorValues: prior,
            newValues: new { deleted = true, eventsUnscheduled = unscheduled, racesDetached });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---- Event placement -------------------------------------------------

    public sealed record PlaceEventRequest(Guid EventId, int? SlotOrder);

    /// <summary>Placing an event already listed elsewhere moves it; that is not an error.</summary>
    private static async Task<IResult> PlaceEventAsync(
        Guid id, Guid slotId, PlaceEventRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, "The schedule cannot be changed", ct);
        if (refusal is not null) return refusal;

        var slot = await FindSlotAsync(db, id, slotId, ct);
        if (slot is null) return SlotNotFound();
        if (slot.Kind != ScheduleSlotKind.Events)
            return ApiError.Conflict(ErrorCodes.ScheduleSlotKindMismatch,
                "This slot is a non-competition item (novelty, logistics), so it holds no events. " +
                "Novelty house points are recorded as ADR-0005 adjustments, not as results.");

        var carnivalEvent = await CarnivalGuards.FindEventAsync(db, id, request.EventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();

        var prior = new { slotId = carnivalEvent.ScheduleSlotId, order = carnivalEvent.SlotOrder };
        carnivalEvent.ScheduleSlotId = slotId;
        carnivalEvent.SlotOrder = request.SlotOrder
            ?? await NextOrderAsync(
                db.CarnivalEvents
                    .Where(e => e.ScheduleSlotId == slotId && e.Id != request.EventId && e.SlotOrder != null)
                    .Select(e => e.SlotOrder!.Value),
                ct);
        audit.Record(SessionActor.From(ctx), AuditActions.ScheduleEventPlace, nameof(CarnivalEvent),
            carnivalEvent.Id.ToString(),
            priorValues: prior,
            newValues: new { slotId = carnivalEvent.ScheduleSlotId, order = carnivalEvent.SlotOrder });
        await db.SaveChangesAsync(ct);
        return await SlotResponseAsync(db, slotId, ct);
    }

    private static async Task<IResult> RemoveEventAsync(
        Guid id, Guid slotId, Guid eventId, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, "The schedule cannot be changed", ct);
        if (refusal is not null) return refusal;

        var slot = await FindSlotAsync(db, id, slotId, ct);
        if (slot is null) return SlotNotFound();

        var carnivalEvent = await CarnivalGuards.FindEventAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        // Not in this slot is a no-op, not a conflict: the intent already holds.
        if (carnivalEvent.ScheduleSlotId == slotId)
        {
            var prior = new { slotId = carnivalEvent.ScheduleSlotId, order = carnivalEvent.SlotOrder };
            carnivalEvent.ScheduleSlotId = null;
            carnivalEvent.SlotOrder = null;
            audit.Record(SessionActor.From(ctx), AuditActions.ScheduleEventRemove, nameof(CarnivalEvent),
                eventId.ToString(),
                priorValues: prior,
                newValues: new { slotId = (Guid?)null, order = (int?)null });
            await db.SaveChangesAsync(ct);
        }
        return await SlotResponseAsync(db, slotId, ct);
    }
}
