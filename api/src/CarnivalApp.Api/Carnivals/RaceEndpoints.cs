using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Carnivals;

/// <summary>
/// Heat is event-scoped and is the placing unit; lane is race-scoped. A wrong heat is wrong house
/// points, so placement stays correctable after a result lands.
/// </summary>
public static class RaceEndpoints
{
    public static void MapRaceEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var races = apiV1.MapGroup("/carnivals/{id:guid}/races");
        races.MapGet("/", ListRacesAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<RaceDto>>();
        races.MapPost("/", CreateRaceAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<RaceDto>(StatusCodes.Status201Created);
        races.MapPut("/{raceId:guid}", UpdateRaceAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<RaceDto>();
        races.MapDelete("/{raceId:guid}", DeleteRaceAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);

        // Marshal, not administer: races are minted lazily, so without this a marshal could not
        // place the first swimmer of any division.
        races.MapPost("/materialise", MaterialiseAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces<MaterialiseResponse>(StatusCodes.Status201Created);

        // PUT replaces the whole placement, so reassign and clear are the same verb.
        apiV1.MapPut("/carnivals/{id:guid}/events/{eventId:guid}/entries/{entryId:guid}/placement",
                AssignPlacementAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces<EntryEndpoints.EntryDto>();
    }

    // ---- Projections -------------------------------------------------------

    public sealed record RaceDto(
        Guid Id,
        /// <summary>
        /// The division the race was minted for, or null. Not exclusive: a race may still hold
        /// heats of more than one event.
        /// </summary>
        Guid? EventId,
        Guid? ScheduleSlotId,
        int SortOrder,
        string? Label,
        /// <summary>Lane order; unlaned entries last.</summary>
        IReadOnlyList<RaceEntryDto> Entries);

    public sealed record RaceEntryDto(
        Guid EntryId,
        Guid CarnivalEventId,
        int? Heat,
        int? Lane,
        string DisplayName,
        string HouseCode,
        bool IsTeam);

    private static RaceDto Project(Race race, IEnumerable<Entry> entries) => new(
        race.Id,
        race.CarnivalEventId,
        race.ScheduleSlotId,
        race.SortOrder,
        race.Label,
        entries
            .OrderBy(e => e.Lane ?? int.MaxValue).ThenBy(e => e.SnapshotDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new RaceEntryDto(
                e.Id, e.CarnivalEventId, e.Heat, e.Lane,
                e.SnapshotDisplayName, e.SnapshotHouseCode, e.StudentId == null))
            .ToList());

    private static IResult RaceNotFound() =>
        ApiError.NotFound(ErrorCodes.RaceNotFound, "No race exists with that id in this carnival.");

    /// <summary>Without its entries: the placement path calls this on every tap.</summary>
    private static Task<Race?> FindRaceAsync(
        SportsDbContext db, Guid carnivalId, Guid raceId, CancellationToken ct) =>
        db.Races.FirstOrDefaultAsync(r => r.CarnivalId == carnivalId && r.Id == raceId, ct);

    /// <summary>Finalised is the only refusal: races are still tidied once Closed.</summary>
    private static async Task<(Carnival? Carnival, IResult? Refusal)> EditableCarnivalAsync(
        SportsDbContext db, Guid id, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return (null, CarnivalGuards.CarnivalNotFound());
        return CarnivalGuards.RequireNotFinalised(carnival, "Races cannot be changed") is { } refusal
            ? (null, refusal)
            : (carnival, null);
    }

    private static object Audited(Race race) => new
    {
        race.CarnivalEventId,
        race.ScheduleSlotId,
        race.SortOrder,
        race.Label,
    };

    // ---- Read ---------------------------------------------------------------

    private static async Task<IResult> ListRacesAsync(
        Guid id, [FromServices] SportsDbContext db, CancellationToken ct)
    {
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct)) return CarnivalGuards.CarnivalNotFound();

        var races = await db.Races.AsNoTracking()
            .Where(r => r.CarnivalId == id)
            .ToListAsync(ct);
        var assigned = await db.Entries.AsNoTracking()
            .Where(e => e.RaceId != null && e.CarnivalEvent.CarnivalId == id)
            .ToListAsync(ct);
        var byRace = assigned.ToLookup(e => e.RaceId!.Value);

        // Ordered in memory to keep the query provider-clean; a carnival has tens of races.
        return Results.Ok(races
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(r => Project(r, byRace[r.Id]))
            .ToList());
    }

    // ---- Race CRUD ------------------------------------------------------------

    /// <summary>
    /// PUT replaces the slot and label (omitted clears them); an omitted sort order keeps the
    /// race where it is.
    /// </summary>
    public sealed record RaceRequest(Guid? ScheduleSlotId, int? SortOrder, string? Label);

    /// <summary>
    /// The slot's carnival is two joins from the race, so only this check stops a race pointing
    /// at another carnival's program.
    /// </summary>
    private static async Task<IResult?> ValidateSlotAsync(
        SportsDbContext db, Guid carnivalId, Guid? slotId, CancellationToken ct)
    {
        if (slotId is null) return null;
        var slot = await db.ScheduleSlots
            .FirstOrDefaultAsync(sl => sl.Id == slotId && sl.Stream.CarnivalId == carnivalId, ct);
        if (slot is null)
            return ApiError.NotFound(ErrorCodes.ScheduleSlotNotFound,
                "No schedule slot exists with that id in this carnival.");
        if (slot.Kind != ScheduleSlotKind.Events)
            return ApiError.Conflict(ErrorCodes.ScheduleSlotKindMismatch,
                "This slot is a non-competition item (novelty, logistics), so no race runs under it.");
        return null;
    }

    /// <summary>Audit prose for a person reading the trail; never branch on it.</summary>
    private const string SlotVanishedReason =
        "the requested schedule slot was gone or no longer a competition slot when this queued create drained; "
        + "the schedule is advisory (ADR-0014 §1), so the race was created unscheduled rather than refused";

    /// <summary>The optional idempotency key becomes the race id.</summary>
    private static Task<IResult> CreateRaceAsync(
        Guid id, RaceRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (clientRaceId, innerCt) =>
                CreateRaceCoreAsync(id, clientRaceId, request, ctx, db, audit, innerCt),
            ct);

    private static async Task<IResult> CreateRaceCoreAsync(
        Guid id, Guid? clientRaceId, RaceRequest request, HttpContext ctx,
        SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        var (carnival, refusal) = await EditableCarnivalAsync(db, id, ct);
        if (refusal is not null) return refusal;

        // A keyed (queued) create lands unscheduled rather than refused when its slot went while it
        // queued: refusing would strand every queued assignment naming this race id.
        var requestedSlotId = request.ScheduleSlotId;
        var slotVanished = false;
        if (await ValidateSlotAsync(db, id, requestedSlotId, ct) is { } slotRefusal)
        {
            if (clientRaceId is null) return slotRefusal;
            slotVanished = true;
        }

        // A same-payload replay was already served by the idempotency filter.
        if (clientRaceId is Guid provided && await db.Races.AnyAsync(r => r.Id == provided, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different race already exists with this submission id.");

        var race = new Race
        {
            Id = clientRaceId ?? Guid.CreateVersion7(),
            CarnivalId = id,
            ScheduleSlotId = slotVanished ? null : requestedSlotId,
            SortOrder = request.SortOrder
                ?? await ScheduleEndpoints.NextOrderAsync(
                    db.Races.Where(r => r.CarnivalId == id).Select(r => r.SortOrder), ct),
            Label = Trimmed(request.Label),
        };
        db.Races.Add(race);
        var sessionActor = SessionActor.From(ctx);
        audit.Record(CapturedActor.Resolve(ctx, sessionActor), AuditActions.RaceCreate,
            nameof(Race), race.Id.ToString(),
            newValues: new
            {
                race.ScheduleSlotId,
                race.SortOrder,
                race.Label,
                // Without these, a degraded race reads back as one nobody ever scheduled.
                requestedScheduleSlotId = slotVanished ? requestedSlotId : null,
                unscheduledReason = slotVanished ? SlotVanishedReason : null,
                syncSessionSubject = sessionActor.Subject,
                deviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
            },
            ruleVersion: carnival!.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/races/{race.Id}", Project(race, []));
    }

    private static async Task<IResult> UpdateRaceAsync(
        Guid id, Guid raceId, RaceRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, ct);
        if (refusal is not null) return refusal;

        var race = await db.Races
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.CarnivalId == id && r.Id == raceId, ct);
        if (race is null) return RaceNotFound();

        if (await ValidateSlotAsync(db, id, request.ScheduleSlotId, ct) is { } slotRefusal)
            return slotRefusal;

        var prior = Audited(race);
        race.ScheduleSlotId = request.ScheduleSlotId;
        race.SortOrder = request.SortOrder ?? race.SortOrder;
        race.Label = Trimmed(request.Label);
        audit.Record(SessionActor.From(ctx), AuditActions.RaceUpdate, nameof(Race), raceId.ToString(),
            priorValues: prior, newValues: Audited(race));
        await db.SaveChangesAsync(ct);
        return Results.Ok(Project(race, race.Entries));
    }

    private static async Task<IResult> DeleteRaceAsync(
        Guid id, Guid raceId, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var (_, refusal) = await EditableCarnivalAsync(db, id, ct);
        if (refusal is not null) return refusal;

        var race = await db.Races
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.CarnivalId == id && r.Id == raceId, ct);
        if (race is null) return RaceNotFound();

        // Heat is kept: clearing it would move recorded results into the event-wide placing pool.
        // Copied first because clearing the FK makes EF mutate race.Entries mid-enumeration.
        var assigned = race.Entries.ToList();
        foreach (var entry in assigned)
        {
            entry.RaceId = null;
            entry.Lane = null;
        }
        var prior = Audited(race);
        db.Races.Remove(race);
        audit.Record(SessionActor.From(ctx), AuditActions.RaceDelete, nameof(Race), raceId.ToString(),
            priorValues: prior,
            newValues: new { deleted = true, entriesDetached = assigned.Count });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        // An entry placed in the race after it was read holds the foreign key.
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || DbErrors.IsForeignKeyViolation(ex))
        {
            db.ChangeTracker.Clear();
            return CarnivalGuards.ConcurrencyConflict(
                "Concurrent change conflict: an entry was placed in this race while it was being deleted. "
                + "Re-read the race and retry.");
        }
        return Results.NoContent();
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- Placement ------------------------------------------------------------

    /// <summary>
    /// A null heat means derive from the race, never "clear my heat while staying in the race".
    /// </summary>
    public sealed record PlacementRequest(Guid? RaceId, int? Lane, int? Heat);

    private static Task<IResult> AssignPlacementAsync(
        Guid id, Guid eventId, Guid entryId, PlacementRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (_, innerCt) =>
                AssignPlacementCoreAsync(id, eventId, entryId, request, ctx, db, audit, innerCt),
            ct);

    /// <summary>
    /// Contention is bounded by the marshals working one event at once, so exhausting this means
    /// something other than a race is wrong.
    /// </summary>
    private const int MintAttempts = 4;

    /// <summary>Test interleave point: after the heat is derived, before it is written.</summary>
    public const string CohortMintTag = "placement cohort heat mint";

    /// <summary>One per attempt, so a test can count the attempts a refusal cost.</summary>
    public const string AutoFillPickTag = "auto-fill latest race";

    /// <summary>
    /// A losing mint is re-derived, not refused. Clearing the tracker discards the failed batch's
    /// edits and audit row.
    /// </summary>
    private static async Task<IResult> AssignPlacementCoreAsync(
        Guid id, Guid eventId, Guid entryId, PlacementRequest request, HttpContext ctx,
        SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await PlaceOnceAsync(
                    id, eventId, entryId, request, ctx, db, audit, autoFilled: false, ct)).Result;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                if (attempt >= MintAttempts)
                    // INTERNAL_ERROR is retryable client-side: the outbox holds the good capture.
                    return ApiError.Internal(ErrorCodes.InternalError,
                        "The heat for this placement could not be derived: other marshals kept " +
                        "opening fields in this event while it was being worked out. Try again.");
            }
        }
    }

    /// <summary><paramref name="autoFilled"/> only labels the audit row.</summary>
    private static async Task<PlacementOutcome> PlaceOnceAsync(
        Guid id, Guid eventId, Guid entryId, PlacementRequest request, HttpContext ctx,
        SportsDbContext db, AuditService audit, bool autoFilled, CancellationToken ct,
        bool contextRace = false)
    {
        var entry = await db.Entries
            .Include(e => e.CarnivalEvent).ThenInclude(ce => ce.Carnival)
            .Include(e => e.RelayLegs)
            .FirstOrDefaultAsync(
                e => e.Id == entryId && e.CarnivalEventId == eventId && e.CarnivalEvent.CarnivalId == id, ct);
        if (entry is null) return new(EntryEndpoints.EntryNotFound());

        var carnival = entry.CarnivalEvent.Carnival;
        // Capture gates (retryable), not result gates: an entry with a result stays assignable.
        if (CarnivalGuards.RequireCapturable(carnival, "Entries cannot be marshalled") is { } closed)
            return new(closed);
        if (CarnivalGuards.RequireEventCapturable(entry.CarnivalEvent, "Entries cannot be marshalled") is { } refusal)
            return new(refusal);

        // Stored alone, the lane would silently bind to whichever race the entry joins later.
        if (request.Lane is not null && request.RaceId is null)
            return new(ApiError.BadRequest(ErrorCodes.EntryLaneInvalid,
                "A lane is a position IN a race (ADR-0015 §3); supply raceId with lane, or omit both."));

        Race? race = null;
        if (request.RaceId is Guid raceId)
        {
            race = await FindRaceAsync(db, id, raceId, ct);
            // Retryable for auto-fill only: the race it just picked was deleted underneath it.
            if (race is null) return new(RaceNotFound(), Retryable: true);
        }

        if (request.Heat is < 1 or > EntryEndpoints.MaxHeatNumber)
            return new(ApiError.BadRequest(ErrorCodes.EntryHeatInvalid,
                $"heat must be between 1 and {EntryEndpoints.MaxHeatNumber}; omit it to derive from the race."));

        // One read for every neighbour check: a reconnecting device replays placements serially,
        // so each round trip here is wall-clock on carnival morning.
        IReadOnlyList<MarshalledRow> context = [];
        if (race is not null)
            context = await db.Entries
                .Where(e => e.Id != entry.Id
                            && (e.RaceId == race!.Id
                                || (e.CarnivalEventId == entry.CarnivalEventId && e.Heat != null)))
                .Select(e => new MarshalledRow(e.CarnivalEventId, e.RaceId, e.Heat, e.Lane))
                .ToListAsync(ct);

        var heat = request.Heat;
        var opensNewField = false;
        if (heat is null && race is not null)
        {
            var (derived, mintsNewField, heatRefusal) =
                await DeriveHeatAsync(entry, race, context, db, id, ct);
            if (heatRefusal is not null) return new(heatRefusal);
            heat = derived;
            opensNewField = mintsNewField;
        }

        if (request.Lane is int lane)
        {
            var laneCount = CarnivalSettings.Parse(carnival.SettingsJson).LaneCount;
            var ceiling = laneCount ?? CarnivalSettings.MaxLaneCount;
            if (lane < 1)
                return new(ApiError.BadRequest(ErrorCodes.EntryLaneInvalid,
                    "lane must be a positive lane number."));
            if (lane > ceiling)
                return new(ApiError.BadRequest(ErrorCodes.EntryLaneInvalid, laneCount is null
                    ? $"lane must be no more than {ceiling}."
                    : $"lane must be no more than {ceiling} — this venue has {ceiling} lanes (carnival settings)."));
            // Friendly pre-check; the unique (RaceId, Lane) index decides under concurrency.
            if (context.Any(r => r.RaceId == race!.Id && r.Lane == lane))
                return new(LaneTakenConflict(lane), Retryable: true);
        }

        var prior = Placement(entry);
        entry.RaceId = race?.Id;
        entry.Lane = request.Lane;
        entry.Heat = heat;

        // A new heat is written to every heatless same-event entry of the race: heating only the
        // tapped entry would split one field into two placing pools with two winners.
        List<Entry> cohort = [];
        if (race is not null && opensNewField && heat is int mintedHeat)
        {
            // Serialises minting on the event row: two writers can compute the same next heat while
            // touching disjoint entry rows.
            entry.CarnivalEvent.Version++;
            cohort = await db.Entries
                .TagWith(CohortMintTag)
                .Where(e => e.Id != entry.Id
                            && e.RaceId == race.Id
                            && e.CarnivalEventId == entry.CarnivalEventId
                            && e.Heat == null)
                .ToListAsync(ct);
            foreach (var member in cohort) member.Heat = mintedHeat;
        }

        var sessionActor = SessionActor.From(ctx);
        var marshal = CapturedActor.Resolve(ctx, sessionActor);
        audit.Record(marshal, AuditActions.EntryMarshal, nameof(Entry), entryId.ToString(),
            priorValues: prior,
            newValues: new
            {
                raceId = entry.RaceId,
                lane = entry.Lane,
                heat = entry.Heat,
                // Counts everyone the mint covered, this entry included.
                cohortHeatAssigned = opensNewField
                    ? new { heat, entries = cohort.Count + 1 }
                    : null,
                autoFilled = autoFilled ? true : (bool?)null,
                contextRace = contextRace ? true : (bool?)null,
                capturedActorDisplayName = CapturedActor.DisplayName(ctx),
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
                deviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
            });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        // A clear-placement can violate neither constraint, so its failures stay INTERNAL_ERROR.
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && race is not null)
        {
            // Lost to a lane claim (unique index) or a race delete (FK). Safe to query here: EF's
            // savepoint rollback clears PostgreSQL's aborted-transaction state (25P02).
            if (!await db.Races.AnyAsync(r => r.Id == race.Id, ct))
                return new(RaceNotFound(), Retryable: true);
            if (request.Lane is not null)
                return new(LaneTakenConflict(request.Lane), Retryable: true);
            throw;
        }
        return new(Results.Ok(EntryEndpoints.Project(entry)), entry);
    }

    /// <summary><c>Placed</c> is null on every refusal; only a lost race is retryable.</summary>
    private readonly record struct PlacementOutcome(
        IResult Result, Entry? Placed = null, bool Retryable = false);

    private static object Placement(Entry entry) =>
        new { raceId = entry.RaceId, lane = entry.Lane, heat = entry.Heat };

    private static IResult LaneTakenConflict(int? lane) =>
        ApiError.Conflict(ErrorCodes.LaneTaken, lane is int taken
            ? $"Lane {taken} is already taken in this race by another entry."
            : "That lane is already taken in this race by another entry.");

    private sealed record MarshalledRow(Guid CarnivalEventId, Guid? RaceId, int? Heat, int? Lane);

    /// <summary>
    /// Same-event entries sharing a race are one heat. Keeping an existing heat in the same race
    /// makes an unkeyed replay a no-op instead of a fresh mint.
    /// </summary>
    private static async Task<(int? Heat, bool OpensNewField, IResult? Refusal)> DeriveHeatAsync(
        Entry entry, Race race, IReadOnlyList<MarshalledRow> context,
        SportsDbContext db, Guid carnivalId, CancellationToken ct)
    {
        var coHeats = context
            .Where(r => r.RaceId == race.Id && r.CarnivalEventId == entry.CarnivalEventId && r.Heat != null)
            .Select(r => r.Heat!.Value)
            .Distinct()
            .ToList();
        if (coHeats.Count == 1) return (coHeats[0], false, null);
        if (coHeats.Count > 1)
            return (null, false, ApiError.Unprocessable(ErrorCodes.HeatAmbiguous,
                $"Entries of this event in this race carry different heat numbers ({string.Join(", ", coHeats.OrderBy(h => h))}); supply heat explicitly."));

        if (entry.RaceId == race.Id && entry.Heat is not null) return (entry.Heat, false, null);

        var maxHeat = context
            .Where(r => r.CarnivalEventId == entry.CarnivalEventId)
            .Max(r => r.Heat) ?? 0;
        // The race's ordinal is the number the deck shows, so the scoring heat matches it;
        // maxHeat + 1 only where that ordinal is already another race's heat.
        var ordinal = await DivisionOrdinalAsync(db, carnivalId, entry.CarnivalEventId, race.Id, ct);
        var next = ordinal >= 1
                   && !context.Any(r => r.CarnivalEventId == entry.CarnivalEventId && r.Heat == ordinal)
            ? ordinal
            : maxHeat + 1;
        if (next > EntryEndpoints.MaxHeatNumber)
            return (null, false, ApiError.Unprocessable(ErrorCodes.EntryHeatInvalid,
                $"This event already has heat {maxHeat}; no heat past {EntryEndpoints.MaxHeatNumber} can be opened."));
        return (next, true, null);
    }

    /// <summary>Must order exactly as the deck orders a division's races.</summary>
    private static async Task<int> DivisionOrdinalAsync(
        SportsDbContext db, Guid carnivalId, Guid eventId, Guid raceId, CancellationToken ct)
    {
        var ordered = await db.Races.AsNoTracking()
            .Where(r => r.CarnivalId == carnivalId
                        && (r.Id == raceId
                            || r.CarnivalEventId == eventId
                            || r.Entries.Any(e => e.CarnivalEventId == eventId)))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);
        return ordered.IndexOf(raceId) + 1;
    }

    // ---- Materialisation ------------------------------------------------------

    public sealed record MaterialiseRequest(Guid EventId, Guid? ScheduleSlotId, string? Label);

    /// <summary>
    /// An oversized division is warned, not refused (a mass start is legitimate). Warnings are
    /// free copy; never branch on them.
    /// </summary>
    public sealed record MaterialiseResponse(RaceDto Race, IReadOnlyList<string> Warnings);

    /// <summary>
    /// One server operation, not a client batch, so a partial materialisation cannot exist. It
    /// never touches <see cref="Entry.Heat"/>.
    /// </summary>
    private static Task<IResult> MaterialiseAsync(
        Guid id, MaterialiseRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (clientRaceId, innerCt) => MaterialiseCoreAsync(id, clientRaceId, request, ctx, db, audit, innerCt),
            ct);

    private static async Task<IResult> MaterialiseCoreAsync(
        Guid id, Guid? clientRaceId, MaterialiseRequest request, HttpContext ctx,
        SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        if (request.EventId == Guid.Empty)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "eventId must name an event.");

        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireCapturable(carnival, "Races cannot be materialised") is { } closed)
            return closed;

        var carnivalEvent = await CarnivalGuards.FindEventAsync(db, id, request.EventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireEventCapturable(carnivalEvent, "Races cannot be materialised") is { } refusal)
            return refusal;

        // Same keyed degrade to unscheduled as CreateRaceCoreAsync.
        var requestedSlotId = request.ScheduleSlotId;
        var slotVanished = false;
        if (await ValidateSlotAsync(db, id, requestedSlotId, ct) is { } slotRefusal)
        {
            if (clientRaceId is null) return slotRefusal;
            slotVanished = true;
        }

        if (clientRaceId is Guid provided && await db.Races.AnyAsync(r => r.Id == provided, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different race already exists with this submission id.");

        var (race, joining, warnings) = await MintRaceCoreAsync(
            id, carnival, request.EventId, clientRaceId,
            slotVanished ? null : requestedSlotId, slotVanished ? requestedSlotId : null,
            Trimmed(request.Label), sweep: true, autoFilled: false, ctx, db, audit, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/races/{race.Id}",
            new MaterialiseResponse(Project(race, joining), warnings));
    }

    /// <summary>
    /// Does not save; the caller has run the guards. Auto-fill passes no <paramref name="sweep"/>:
    /// a sweep would drag a scratched swimmer back onto the deck.
    /// </summary>
    private static async Task<(Race Race, List<Entry> Joining, List<string> Warnings)> MintRaceCoreAsync(
        Guid id, Carnival carnival, Guid eventId, Guid? clientRaceId,
        Guid? scheduleSlotId, Guid? requestedSlotId, string? label, bool sweep, bool autoFilled,
        HttpContext ctx, SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        // Already-raced entries stay put, which makes a replay safe.
        var joining = sweep
            ? await db.Entries
                .Where(e => e.CarnivalEventId == eventId && e.RaceId == null)
                .ToListAsync(ct)
            : [];

        var race = new Race
        {
            Id = clientRaceId ?? Guid.CreateVersion7(),
            CarnivalId = id,
            // The stamp is the only thing that finds an empty race's division.
            CarnivalEventId = eventId,
            ScheduleSlotId = scheduleSlotId,
            SortOrder = await ScheduleEndpoints.NextOrderAsync(
                db.Races.Where(r => r.CarnivalId == id).Select(r => r.SortOrder), ct),
            Label = label,
        };
        db.Races.Add(race);

        // Membership only: Heat is left untouched so a mint can never move a placing pool.
        foreach (var entry in joining)
        {
            entry.RaceId = race.Id;
            entry.Lane = null;
        }

        var laneCount = CarnivalSettings.Parse(carnival.SettingsJson).LaneCount;
        var ceiling = laneCount ?? CarnivalSettings.MaxLaneCount;
        var warnings = new List<string>();
        if (joining.Count > ceiling)
        {
            var lanes = ceiling == 1 ? "lane" : "lanes";
            warnings.Add(laneCount is null
                ? $"This race holds {joining.Count} entries, more than the {ceiling} {lanes} any venue is assumed to have."
                : $"This race holds {joining.Count} entries but this venue has {ceiling} {lanes} (carnival settings).");
        }

        var sessionActor = SessionActor.From(ctx);
        audit.Record(CapturedActor.Resolve(ctx, sessionActor), AuditActions.RaceMaterialise,
            nameof(Race), race.Id.ToString(),
            newValues: new
            {
                race.ScheduleSlotId,
                race.SortOrder,
                race.Label,
                carnivalEventId = eventId,
                // No heat field: an audited heat would read as one this operation had set.
                entriesJoined = joining.Count,
                warnings,
                requestedScheduleSlotId = requestedSlotId,
                unscheduledReason = requestedSlotId is null ? null : SlotVanishedReason,
                autoFilled = autoFilled ? true : (bool?)null,
                capturedActorDisplayName = CapturedActor.DisplayName(ctx),
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
                deviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
            },
            ruleVersion: carnival.RuleVersion);
        return (race, joining, warnings);
    }

    // ---- Auto-fill ------------------------------------------------------------

    /// <summary>Null leaves the entry unlaned. Only Time events have lanes.</summary>
    internal static int? AutoFillLaneCount(Carnival carnival, EventDefinition definition)
    {
        var settings = CarnivalSettings.Parse(carnival.SettingsJson);
        return settings.AutoFillLanes && definition.MeasureType == MeasureType.Time
            ? settings.LaneCount
            : null;
    }

    /// <summary>
    /// The placement is a convenience and the entry is the record: on any failure the entry stays
    /// as created rather than losing the swimmer.
    /// </summary>
    internal static async Task<Entry?> PlaceUnrequestedAsync(
        Guid id, Guid eventId, Guid entryId, Carnival carnival, int? laneCount, int? heat,
        HttpContext ctx, SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        List<Guid> mintedIds = [];
        Entry? placed = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var target = await NextUnrequestedSlotAsync(db, id, eventId, laneCount, ct);
                if (target is null)
                {
                    // Saved before placing: the placement's lookups are DB queries and cannot see
                    // an unsaved race.
                    var minted = (await MintRaceCoreAsync(
                        id, carnival, eventId, null, null, null, null, sweep: false,
                        autoFilled: true, ctx, db, audit, ct)).Race;
                    await db.SaveChangesAsync(ct);
                    mintedIds.Add(minted.Id);
                    target = (minted.Id, laneCount is null ? null : 1);
                }

                var (_, landed, retryable) = await PlaceOnceAsync(id, eventId, entryId,
                    new PlacementRequest(target.Value.RaceId, target.Value.Lane, heat),
                    ctx, db, audit, autoFilled: target.Value.Lane is not null, ct);
                if (landed is not null)
                {
                    placed = landed;
                    break;
                }
                // A permanent refusal answers the same every time, so it must not burn retries.
                db.ChangeTracker.Clear();
                if (!retryable || attempt >= MintAttempts) break;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (attempt >= MintAttempts) break;
            }
        }
        // A retry can land in a race other than the one an earlier attempt minted.
        await DiscardEmptyMintsAsync(mintedIds, ctx, db, audit, ct);
        return placed ?? await ReadEntryAsync(db, entryId, ct);
    }

    /// <summary>
    /// A race this request minted that is still empty would be an empty heat on the deck. One a
    /// competitor has since joined is kept.
    /// </summary>
    private static async Task DiscardEmptyMintsAsync(
        List<Guid> mintedIds, HttpContext ctx, SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        if (mintedIds.Count == 0) return;
        var empty = await db.Races
            .Where(r => mintedIds.Contains(r.Id) && !r.Entries.Any())
            .ToListAsync(ct);
        if (empty.Count == 0) return;
        var actor = CapturedActor.Resolve(ctx, SessionActor.From(ctx));
        foreach (var race in empty)
        {
            db.Races.Remove(race);
            audit.Record(actor, AuditActions.RaceDelete, nameof(Race), race.Id.ToString(),
                priorValues: Audited(race),
                newValues: new { deleted = true, entriesDetached = 0, autoFillAbandoned = true });
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Null means mint a new race. Earlier races are never considered: a gap there may be in a
    /// heat that already swam.
    /// </summary>
    private static async Task<(Guid RaceId, int? Lane)?> NextUnrequestedSlotAsync(
        SportsDbContext db, Guid carnivalId, Guid eventId, int? laneCount, CancellationToken ct)
    {
        // Stamp or membership: unstamped races (legacy, multi-event admin create) still count.
        var latest = await db.Races
            .TagWith(AutoFillPickTag)
            .Where(r => r.CarnivalId == carnivalId
                        && (r.CarnivalEventId == eventId
                            || r.Entries.Any(e => e.CarnivalEventId == eventId)))
            .OrderByDescending(r => r.SortOrder).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (latest is null) return null;

        // Any result (a DNS included) means the heat ran; a late swimmer would re-rank it.
        if (await db.Results.AnyAsync(r => r.Entry.RaceId == latest.Id, ct)) return null;

        if (laneCount is not int count) return (latest.Id, null);
        return await FreeLaneAsync(db, latest.Id, count, ct) is int free ? (latest.Id, free) : null;
    }

    private static async Task<int?> FreeLaneAsync(
        SportsDbContext db, Guid raceId, int laneCount, CancellationToken ct)
    {
        var taken = await db.Entries
            .Where(e => e.RaceId == raceId && e.Lane != null)
            .Select(e => e.Lane!.Value)
            .ToListAsync(ct);
        for (var lane = 1; lane <= laneCount; lane++)
            if (!taken.Contains(lane)) return lane;
        return null;
    }

    // ---- The context heat -----------------------------------------------------

    /// <summary>
    /// An unknown race id is refused too: a create that overtook its client-minted race must park
    /// rather than land elsewhere.
    /// </summary>
    internal static async Task<IResult?> RefuseContextRaceAsync(
        SportsDbContext db, Guid carnivalId, Guid eventId, Guid raceId, CancellationToken ct)
    {
        var found = await db.Races.AsNoTracking()
            .Where(r => r.CarnivalId == carnivalId && r.Id == raceId)
            .Select(r => new
            {
                Stamped = r.CarnivalEventId == eventId,
                Holds = r.Entries.Any(e => e.CarnivalEventId == eventId),
            })
            .FirstOrDefaultAsync(ct);
        if (found is null)
            return ApiError.Unprocessable(ErrorCodes.RaceNotInEvent,
                "raceId names no race of this carnival — the heat is unknown here. "
                + "Add the heat first, then create the entry in it (ADR-0039 §2).");
        return found.Stamped || found.Holds
            ? null
            : ApiError.Unprocessable(ErrorCodes.RaceNotInEvent,
                "raceId names a race of another division; an entry may only be created in a heat "
                + "of its own event (ADR-0039 §2).");
    }

    /// <summary>
    /// No fullness or swum-race gate: the marshal picked this heat. Giving up falls back to the
    /// unrequested placement so the entry is not left raceless.
    /// </summary>
    internal static async Task<Entry?> PlaceInContextRaceAsync(
        Guid id, Guid eventId, Guid entryId, Guid raceId, Carnival carnival, int? laneCount, int? heat,
        HttpContext ctx, SportsDbContext db, AuditService audit, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var lane = laneCount is int count
                    ? await FreeLaneAsync(db, raceId, count, ct)
                    : null;
                var (_, placed, retryable) = await PlaceOnceAsync(id, eventId, entryId,
                    new PlacementRequest(raceId, lane, heat),
                    ctx, db, audit, autoFilled: lane is not null, ct, contextRace: true);
                if (placed is not null) return placed;
                db.ChangeTracker.Clear();
                // A retry only recovers a stolen lane; a deleted named race is permanent.
                if (!retryable || attempt >= MintAttempts
                    || !await db.Races.AnyAsync(r => r.Id == raceId, ct))
                    break;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (attempt >= MintAttempts) break;
            }
        }
        return await PlaceUnrequestedAsync(
            id, eventId, entryId, carnival, laneCount, heat, ctx, db, audit, ct);
    }

    private static Task<Entry?> ReadEntryAsync(SportsDbContext db, Guid entryId, CancellationToken ct) =>
        db.Entries.Include(e => e.RelayLegs).FirstOrDefaultAsync(e => e.Id == entryId, ct);

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
