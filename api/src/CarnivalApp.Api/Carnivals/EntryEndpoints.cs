using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Carnivals;

/// <summary>
/// Eligibility reads the current roster snapshot; the entry then snapshots display fields
/// immutably so later imports never rewrite history.
/// </summary>
public static class EntryEndpoints
{
    public static void MapEntryEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var events = apiV1.MapGroup("/carnivals/{id:guid}/events/{eventId:guid}");
        // Whole-roster search is for roles that create entries, not every carnival reader.
        events.MapGet("/eligible-students", SearchEligibleStudentsAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces<EligibleStudentPage>();
        events.MapGet("/entries", ListEntriesAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<EntryDto>>();
        events.MapPost("/entries", CreateEntryAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces<EntryDto>(StatusCodes.Status201Created);
        events.MapPost("/team-entries", CreateTeamEntryAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces<EntryDto>(StatusCodes.Status201Created);
        events.MapDelete("/entries/{entryId:guid}", RemoveEntryAsync)
            .RequireAuthorization(CarnivalAuthorization.MarshalCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);
        // The category override is an eligibility control, not marshalling.
        events.MapPost("/entries/{entryId:guid}/category-override", SetCategoryOverrideAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<EntryDto>();
    }

    /// <summary>
    /// <paramref name="RaceId"/> is the heat the marshal is standing at; null falls back to
    /// auto-fill.
    /// </summary>
    public sealed record CreateEntryRequest(
        Guid StudentId, int? Heat, int? Lane,
        CompetitionCategory? CategoryOverride, string? CategoryOverrideReason,
        Guid? RaceId = null);

    public sealed record TeamEntryRequest(
        string? HouseCode, Guid[]? StudentIds, int? Heat, int? Lane, Guid? RaceId = null);

    public sealed record CategoryOverrideRequest(CompetitionCategory? Category, string? Reason);

    public sealed record EntryDto(
        Guid Id,
        Guid CarnivalEventId,
        /// <summary>Null for a team entry; its members are the legs.</summary>
        Guid? StudentId,
        Guid? RaceId,
        int? Heat,
        int? Lane,
        string DisplayName,
        int YearLevel,
        string HouseCode,
        string AgeGroup,
        /// <summary>A bare flag: the category itself never leaves the server.</summary>
        bool OverrideApplied,
        bool IsTeam,
        string? TeamHouseCode,
        IReadOnlyList<RelayLegDto> Legs);

    public sealed record RelayLegDto(
        int LegOrder, string? Stroke, Guid StudentId, string DisplayName, int YearLevel);

    internal static EntryDto Project(Entry e) => new(
        e.Id,
        e.CarnivalEventId,
        e.StudentId,
        e.RaceId,
        e.Heat,
        e.Lane,
        e.SnapshotDisplayName,
        e.SnapshotYearLevel,
        e.SnapshotHouseCode,
        e.SnapshotAgeGroup,
        e.CategoryOverride.HasValue,
        e.StudentId == null,
        e.HouseCode,
        e.RelayLegs
            .OrderBy(l => l.LegOrder)
            .Select(l => new RelayLegDto(
                l.LegOrder, l.Stroke, l.StudentId, l.SnapshotDisplayName, l.SnapshotYearLevel))
            .ToList());

    internal static IResult EntryNotFound() =>
        ApiError.NotFound(ErrorCodes.EntryNotFound, "No entry exists with that id in this event.");

    /// <summary>Legs share the individual codes, so the leg travels as context.</summary>
    private static Dictionary<string, object?> LegContext(int legOrder) => new() { ["leg"] = legOrder };

    private sealed record SiblingEntry(
        Guid EntryId, Guid CarnivalEventId, string AgeGroup,
        CompetitionCategory? Category, EventTier Tier);

    /// <summary>
    /// Earliest (UUIDv7) first, so a residual concurrent duplicate sends the operator to the same
    /// division on every retry.
    /// </summary>
    private static Task<SiblingEntry?> FindSiblingEntryAsync(
        CarnivalAppDbContext db, CarnivalEvent carnivalEvent, Guid studentId, CancellationToken ct) =>
        db.Entries.AsNoTracking()
            .Where(e => e.StudentId == studentId
                && e.CarnivalEventId != carnivalEvent.Id
                && e.CarnivalEvent.CarnivalId == carnivalEvent.CarnivalId
                && e.CarnivalEvent.EventDefinitionId == carnivalEvent.EventDefinitionId
                && e.CarnivalEvent.Tier == carnivalEvent.Tier)
            .OrderBy(e => e.Id)
            .Select(e => new SiblingEntry(
                e.Id,
                e.CarnivalEventId,
                e.CarnivalEvent.AgeGroup,
                e.CarnivalEvent.Category,
                e.CarnivalEvent.Tier))
            .FirstOrDefaultAsync(ct);

    private static string DivisionName(SiblingEntry sibling) =>
        DivisionNaming.Division(sibling.AgeGroup, sibling.Category);

    /// <summary>The standing entry travels as context: the operator must find it first.</summary>
    private static IResult RefuseSiblingDivision(SiblingEntry sibling, string detail, int? legOrder = null)
    {
        var context = new Dictionary<string, object?>
        {
            ["entryId"] = sibling.EntryId,
            ["carnivalEventId"] = sibling.CarnivalEventId,
            ["ageGroup"] = DivisionNaming.AgeScopeName(sibling.AgeGroup),
            ["category"] = sibling.Category?.ToString(),
            ["tier"] = sibling.Tier.ToString(),
        };
        if (legOrder is not null) context["leg"] = legOrder;
        return ApiError.Conflict(ErrorCodes.EntryInOtherDivision, detail, context);
    }

    /// <summary>
    /// Every distinct heat is a placing pool with its own 1st place, so an unbounded heat number
    /// is an unbounded supply of winners' points.
    /// </summary>
    internal const int MaxHeatNumber = 99;

    /// <summary>
    /// A create may state a heat but never a lane: a lane without a race would bind to whichever
    /// race the entry joins later. The field stays so an older client gets a parkable refusal.
    /// </summary>
    private static IResult? RefuseHeatOrLane(int? heat, int? lane) => (heat, lane) switch
    {
        ( < 1, _) => ApiError.BadRequest(ErrorCodes.EntryHeatInvalid,
            "heat must be a positive heat number; omit it for an entry that has not been marshalled yet."),
        ( > MaxHeatNumber, _) => ApiError.BadRequest(ErrorCodes.EntryHeatInvalid,
            $"heat must be no more than {MaxHeatNumber}."),
        (_, not null) => ApiError.BadRequest(ErrorCodes.EntryLaneInvalid,
            "A lane is a position IN a race (ADR-0015 §3) and a create never claims one; omit lane "
            + "and name the heat with raceId, or assign it with PUT .../entries/{entryId}/placement."),
        _ => null,
    };

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private const int SearchLimit = 50;

    /// <summary>No category: the list is already filtered to this division.</summary>
    public sealed record EligibleStudentDto(
        Guid StudentId, string DisplayName, int YearLevel,
        string HouseCode, string AgeGroup);

    /// <summary>Without <c>Truncated</c> a full page looks like a complete result.</summary>
    public sealed record EligibleStudentPage(
        IReadOnlyList<EligibleStudentDto> Students, bool Truncated, int Limit);

    private static async Task<IResult> SearchEligibleStudentsAsync(
        Guid id, Guid eventId, string? search, string? house, int? yearLevel,
        [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();

        var query = db.RosterSnapshots.AsNoTracking()
            .Where(s => s.IsCurrent && s.Student.IsActive);

        query = EligibilityService.WhereDivisionMatches(query, carnivalEvent);

        if (!string.IsNullOrWhiteSpace(house))
            query = query.WhereHouseMatches(db, house.Trim());
        if (yearLevel is not null)
            query = query.Where(s => s.YearLevel == yearLevel);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.WhereNameMatches(db, search.Trim());

        // Every division of this definition at this tier, so a dual-listed student is offered once.
        var siblingEvents = db.CarnivalEvents
            .Where(e => e.CarnivalId == id
                && e.EventDefinitionId == carnivalEvent.EventDefinitionId
                && e.Tier == carnivalEvent.Tier)
            .Select(e => e.Id);
        // Nulls kept out: NOT IN over a null-bearing set is never true, so one relay would empty
        // the search.
        var alreadyEntered = db.Entries
            .Where(e => siblingEvents.Contains(e.CarnivalEventId) && e.StudentId != null)
            .Select(e => e.StudentId!.Value);
        query = query.Where(s => !alreadyEntered.Contains(s.StudentId));

        // Legs are invisible to the set above. Age is fixed so an age + all-ages pair survives.
        var alreadyLegged = db.RelayLegs
            .Where(l => l.Entry.CarnivalEventId != eventId
                && l.Entry.CarnivalEvent.CarnivalId == id
                && l.Entry.CarnivalEvent.EventDefinitionId == carnivalEvent.EventDefinitionId
                && l.Entry.CarnivalEvent.Tier == carnivalEvent.Tier
                && l.Entry.CarnivalEvent.AgeGroup == carnivalEvent.AgeGroup)
            .Select(l => l.StudentId);
        query = query.Where(s => !alreadyLegged.Contains(s.StudentId));

        // One row past the limit is the truncation signal.
        var students = await query
            .OrderBy(s => s.Student.Surname).ThenBy(s => s.Student.FirstName)
            .Take(SearchLimit + 1)
            .Select(s => new
            {
                s.StudentId,
                s.Student.PreferredName,
                s.Student.FirstName,
                s.Student.Surname,
                s.YearLevel,
                s.HouseCode,
                s.CompetitionAgeGroup,
            })
            .ToListAsync(ct);

        var truncated = students.Count > SearchLimit;
        if (truncated) students.RemoveAt(SearchLimit);
        return Results.Ok(new EligibleStudentPage(
            students.Select(s => new EligibleStudentDto(
                s.StudentId,
                Snapshot.NameOf(s.PreferredName, s.FirstName, s.Surname),
                s.YearLevel,
                s.HouseCode,
                s.CompetitionAgeGroup)).ToList(),
            truncated,
            SearchLimit));
    }

    private static async Task<IResult> ListEntriesAsync(
        Guid id, Guid eventId, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        var entries = await db.Entries.AsNoTracking()
            .Include(e => e.RelayLegs)
            .Where(e => e.CarnivalEventId == eventId)
            .OrderBy(e => e.Heat).ThenBy(e => e.Lane).ThenBy(e => e.SnapshotDisplayName)
            .ToListAsync(ct);
        return Results.Ok(entries.Select(Project));
    }

    /// <summary>
    /// The optional idempotency key becomes the entry id, so a result queued against it lines up
    /// when both drain.
    /// </summary>
    private static Task<IResult> CreateEntryAsync(
        Guid id, Guid eventId, CreateEntryRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (clientEntryId, innerCt) =>
                CreateEntryCoreAsync(id, eventId, clientEntryId, request, ctx, db, audit, innerCt),
            ct);

    private static async Task<IResult> CreateEntryCoreAsync(
        Guid id, Guid eventId, Guid? clientEntryId, CreateEntryRequest request, HttpContext ctx,
        CarnivalAppDbContext db, AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await db.CarnivalEvents
            .Include(e => e.Carnival)
            .Include(e => e.EventDefinition)
            .FirstOrDefaultAsync(e => e.CarnivalId == id && e.Id == eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (carnivalEvent.EventDefinition.Format == EventFormat.Team)
            return ApiError.BadRequest(ErrorCodes.NotAnIndividualEvent,
                "This is a relay; enter a house team with POST /team-entries, "
                + "not a single student.");
        if (CarnivalGuards.RequireCapturable(carnivalEvent.Carnival, "Entries cannot be created") is { } closed)
            return closed;
        if (CarnivalGuards.RequireEventCapturable(carnivalEvent, "Entries cannot be created") is { } refusal)
            return refusal;
        if (RefuseHeatOrLane(request.Heat, request.Lane) is { } badPlacing) return badPlacing;
        // Before the entry exists: an unknown heat must park the create, not land it elsewhere.
        if (request.RaceId is Guid contextRaceId
            && await RaceEndpoints.RefuseContextRaceAsync(db, id, eventId, contextRaceId, ct) is { } badRace)
            return badRace;

        var student = await db.Students.FirstOrDefaultAsync(s => s.Id == request.StudentId, ct);
        if (student is null)
            return ApiError.BadRequest(ErrorCodes.StudentNotFound,
                "studentId does not reference a known student.");
        if (!student.IsActive)
            return ApiError.Unprocessable(ErrorCodes.StudentInactive,
                "The student is not active on the current roster.");

        var snapshot = await db.RosterSnapshots
            .FirstOrDefaultAsync(s => s.StudentId == student.Id && s.IsCurrent, ct);
        if (snapshot is null)
            return ApiError.Unprocessable(ErrorCodes.RosterSnapshotMissing,
                "The student has no current roster snapshot; promote a roster import first.");

        // A marshal may create entries, so the override needs its own administrator check here too.
        if (request.CategoryOverride is not null
            && !CarnivalAuthorization.MayAdminister(ctx.User))
            return ApiError.Forbidden(ErrorCodes.CategoryOverrideForbidden,
                "A category override may only be applied by a carnival administrator (ADR-0003). " +
                "Create the entry without one, or ask an administrator to apply it.");

        if (request.CategoryOverride is not null && string.IsNullOrWhiteSpace(request.CategoryOverrideReason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired,
                "categoryOverrideReason is required when applying a category override (ADR-0003).");

        if (EligibilityService.CheckDivision(carnivalEvent, snapshot, request.CategoryOverride)
            is { IsEligible: false } refused)
            return EligibilityService.Refuse(refused, refused.Axis switch
            {
                EligibilityAxis.AgeGroup =>
                    $"Student is in age group {refused.Actual}; this event is for {refused.Required}.",
                _ =>
                    $"This event is for {DivisionNaming.Category(carnivalEvent.Category)}. " +
                    "A carnival administrator may apply an audited per-entry category override (ADR-0003).",
            });

        if (await db.Entries.AnyAsync(e => e.CarnivalEventId == eventId && e.StudentId == student.Id, ct))
            return ApiError.Conflict(ErrorCodes.EntryExists, "The student is already entered in this event.");

        // Before the sibling check: once retention deletes a key, removing the sibling would not clear the id.
        if (clientEntryId is Guid provided && await db.Entries.AnyAsync(e => e.Id == provided, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different entry already exists with this submission id.");

        if (await FindSiblingEntryAsync(db, carnivalEvent, student.Id, ct) is { } sibling)
            return RefuseSiblingDivision(sibling,
                $"The student is already entered in the {DivisionName(sibling)} division of this event. "
                + "One entry per student per event at each tier (ADR-0023 §3) — remove that entry first.");

        var sessionActor = SessionActor.From(ctx);
        var creator = CapturedActor.Resolve(ctx, sessionActor);
        var now = DateTimeOffset.UtcNow;
        var captured = Snapshot.From(student, snapshot);
        var entry = new Entry
        {
            Id = clientEntryId ?? Guid.CreateVersion7(),
            CarnivalEventId = eventId,
            StudentId = student.Id,
            Heat = request.Heat,
            Lane = null,
            RosterSnapshotId = captured.RosterSnapshotId,
            SnapshotDisplayName = captured.DisplayName,
            SnapshotYearLevel = captured.YearLevel,
            SnapshotHouseCode = captured.HouseCode,
            SnapshotAgeGroup = captured.AgeGroup,
            SnapshotCategory = captured.Category,
            CategoryOverride = request.CategoryOverride,
            // The session user, not the captured one: the administrator check read the session.
            CategoryOverrideActorSubject = request.CategoryOverride is null ? null : sessionActor.Subject,
            CategoryOverrideAtUtc = request.CategoryOverride is null ? null : now,
            CategoryOverrideReason = request.CategoryOverride is null ? null : request.CategoryOverrideReason,
            CreatedAtUtc = now,
            CreatedBySubject = creator.Subject,
        };
        db.Entries.Add(entry);
        audit.Record(creator, AuditActions.EntryCreate, nameof(Entry), entry.Id.ToString(),
            newValues: new
            {
                entry = Project(entry),
                capturedActorDisplayName = CapturedActor.DisplayName(ctx),
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
                deviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
            });
        if (request.CategoryOverride is not null)
            audit.Record(sessionActor, AuditActions.EntryCategoryOverride, nameof(Entry), entry.Id.ToString(),
                priorValues: new { category = snapshot.CompetitionCategory.ToString() },
                newValues: new { category = request.CategoryOverride.Value.ToString() },
                reason: request.CategoryOverrideReason);
        if (await SaveGuardingDuplicateAsync(db, ErrorCodes.EntryExists,
                "The student was entered in this event by another device while this create was in "
                + "flight. Re-read the entries; the entry already exists.", ct) is { } lost)
            return lost;
        return Results.Created($"/api/v1/carnivals/{id}/events/{eventId}/entries/{entry.Id}",
            await PlacedAsync(id, eventId, request.RaceId, entry, carnivalEvent, ctx, db, audit, ct));
    }

    /// <summary>
    /// Callers pre-check every unique condition, so a violation is a concurrent create that won:
    /// a 409 the outbox parks, not a retryable 500. No re-read: PostgreSQL aborted the transaction.
    /// </summary>
    private static async Task<IResult?> SaveGuardingDuplicateAsync(
        CarnivalAppDbContext db, string code, string detail, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            return ApiError.Conflict(code, detail);
        }
    }

    /// <summary>
    /// Placed inside the create so the stored idempotent response replays the placement, not an
    /// unplaced entry.
    /// </summary>
    private static async Task<EntryDto> PlacedAsync(
        Guid id, Guid eventId, Guid? contextRaceId, Entry entry, CarnivalEvent carnivalEvent,
        HttpContext ctx, CarnivalAppDbContext db, AuditService audit, CancellationToken ct)
    {
        // Projected first: a failed attempt's edits stay on the detached instance.
        var created = Project(entry);
        var laneCount = RaceEndpoints.AutoFillLaneCount(
            carnivalEvent.Carnival, carnivalEvent.EventDefinition);
        var placed = contextRaceId is Guid raceId
            ? await RaceEndpoints.PlaceInContextRaceAsync(
                id, eventId, entry.Id, raceId, carnivalEvent.Carnival, laneCount, entry.Heat,
                ctx, db, audit, ct)
            : await RaceEndpoints.PlaceUnrequestedAsync(
                id, eventId, entry.Id, carnivalEvent.Carnival, laneCount, entry.Heat,
                ctx, db, audit, ct);
        return placed is null ? created : Project(placed);
    }

    private static Task<IResult> CreateTeamEntryAsync(
        Guid id, Guid eventId, TeamEntryRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, [FromServices] SchoolSettingsProvider school,
        CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (clientEntryId, innerCt) => CreateTeamEntryCoreAsync(
                id, eventId, clientEntryId, request, ctx, db, audit, school, innerCt),
            ct);

    private static async Task<IResult> CreateTeamEntryCoreAsync(
        Guid id, Guid eventId, Guid? clientEntryId, TeamEntryRequest request, HttpContext ctx,
        CarnivalAppDbContext db, AuditService audit, SchoolSettingsProvider school, CancellationToken ct)
    {
        var carnivalEvent = await db.CarnivalEvents
            .Include(e => e.Carnival)
            .Include(e => e.EventDefinition)
            .FirstOrDefaultAsync(e => e.CarnivalId == id && e.Id == eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();

        var def = carnivalEvent.EventDefinition;
        if (def.Format != EventFormat.Team)
            return ApiError.BadRequest(ErrorCodes.NotATeamEvent,
                "This is not a team event; use POST /entries for individual entries.");
        if (CarnivalGuards.RequireCapturable(carnivalEvent.Carnival, "Entries cannot be created") is { } closed)
            return closed;
        if (CarnivalGuards.RequireEventCapturable(carnivalEvent, "Entries cannot be created") is { } refusal)
            return refusal;
        if (RefuseHeatOrLane(request.Heat, request.Lane) is { } badPlacing) return badPlacing;
        if (request.RaceId is Guid contextRaceId
            && await RaceEndpoints.RefuseContextRaceAsync(db, id, eventId, contextRaceId, ct) is { } badRace)
            return badRace;

        var typedHouse = request.HouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(typedHouse))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "houseCode is required for a team entry.");
        var (houseCode, unknownHouse) = await ConfiguredHouse.ResolveAsync(school, typedHouse, ct);
        if (houseCode is null) return unknownHouse!;

        var teamSize = def.TeamSize ?? 0;
        var studentIds = request.StudentIds ?? [];
        if (studentIds.Length != teamSize)
            return ApiError.BadRequest(ErrorCodes.TeamSizeMismatch,
                $"This relay needs exactly {teamSize} swimmers listed in leg order; received {studentIds.Length}.");
        if (studentIds.Distinct().Count() != studentIds.Length)
            return ApiError.BadRequest(ErrorCodes.DuplicateTeamMember,
                "A swimmer cannot swim more than one leg of the same relay.");

        // Both sides upper-cased: a legacy row may carry another casing of the same house.
        var houseKey = houseCode.ToUpperInvariant();
        if (await db.Entries.AnyAsync(e => e.CarnivalEventId == eventId
                && e.HouseCode != null && e.HouseCode.ToUpper() == houseKey, ct))
            return ApiError.Conflict(ErrorCodes.TeamExists,
                $"House {houseCode} already has a team entered in this event.");
        if (clientEntryId is Guid provided && await db.Entries.AnyAsync(e => e.Id == provided, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different entry already exists with this submission id.");

        // Tolerant: an unreadable legs blob leaves strokes unlabelled rather than refusing.
        var strokes = JsonBlob.StringList(def.LegsJson);

        // Batched: the loop runs inside the open idempotency transaction.
        var studentsById = await db.Students
            .Where(s => studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var snapshotsByStudent = (await db.RosterSnapshots
                .Where(s => studentIds.Contains(s.StudentId) && s.IsCurrent)
                .ToListAsync(ct))
            .GroupBy(s => s.StudentId)
            .ToDictionary(g => g.Key, g => g.First());
        // Age held fixed, so only teams differing by category are refused. With four fields fixed a
        // different event id is a different category (ScheduleEventAsync's check, not the index).
        var siblingTeams = (await db.RelayLegs.AsNoTracking()
                .Where(l => studentIds.Contains(l.StudentId)
                    && l.Entry.CarnivalEventId != eventId
                    && l.Entry.CarnivalEvent.CarnivalId == id
                    && l.Entry.CarnivalEvent.EventDefinitionId == carnivalEvent.EventDefinitionId
                    && l.Entry.CarnivalEvent.Tier == carnivalEvent.Tier
                    && l.Entry.CarnivalEvent.AgeGroup == carnivalEvent.AgeGroup)
                .OrderBy(l => l.EntryId)
                .Select(l => new
                {
                    l.StudentId,
                    Team = new SiblingEntry(
                        l.EntryId,
                        l.Entry.CarnivalEventId,
                        l.Entry.CarnivalEvent.AgeGroup,
                        l.Entry.CarnivalEvent.Category,
                        l.Entry.CarnivalEvent.Tier),
                })
                .ToListAsync(ct))
            .GroupBy(l => l.StudentId)
            .ToDictionary(g => g.Key, g => g.First().Team);

        var legs = new List<RelayLeg>();
        for (var i = 0; i < studentIds.Length; i++)
        {
            var legNo = i + 1;
            var sid = studentIds[i];
            if (!studentsById.TryGetValue(sid, out var student))
                return ApiError.BadRequest(ErrorCodes.StudentNotFound,
                    $"Leg {legNo}: studentId does not reference a known student.", LegContext(legNo));
            var name = Snapshot.NameOf(student);
            if (!student.IsActive)
                return ApiError.Unprocessable(ErrorCodes.StudentInactive,
                    $"Leg {legNo} ({name}) is not active on the current roster.", LegContext(legNo));
            if (!snapshotsByStudent.TryGetValue(sid, out var snapshot))
                return ApiError.Unprocessable(ErrorCodes.RosterSnapshotMissing,
                    $"Leg {legNo} ({name}) has no current roster snapshot.", LegContext(legNo));
            if (EligibilityService.CheckLeg(carnivalEvent, houseCode, snapshot)
                is { IsEligible: false } refused)
                return EligibilityService.Refuse(refused, refused.Axis switch
                {
                    EligibilityAxis.House =>
                        $"Leg {legNo} ({name}) is in house {refused.Actual}, not {refused.Required}.",
                    EligibilityAxis.AgeGroup =>
                        $"Leg {legNo} ({name}) is age group {refused.Actual}; this relay is for {refused.Required}.",
                    _ =>
                        $"Leg {legNo} ({name}) is not eligible for this relay; "
                        + $"this relay is for {DivisionNaming.Category(carnivalEvent.Category)}.",
                }, LegContext(legNo));
            if (siblingTeams.TryGetValue(sid, out var team))
                return RefuseSiblingDivision(team,
                    $"Leg {legNo} ({name}) already swims in this relay's {DivisionNaming.Category(team.Category)} "
                    + "team at this age; a swimmer may be in one category's team per age "
                    + "(ADR-0023 §5).", legNo);

            var captured = Snapshot.From(student, snapshot);
            legs.Add(new RelayLeg
            {
                Id = Guid.CreateVersion7(),
                LegOrder = legNo,
                Stroke = strokes is not null && i < strokes.Count ? strokes[i] : null,
                StudentId = sid,
                RosterSnapshotId = captured.RosterSnapshotId,
                SnapshotDisplayName = captured.DisplayName,
                SnapshotYearLevel = captured.YearLevel,
                SnapshotHouseCode = captured.HouseCode,
                SnapshotAgeGroup = captured.AgeGroup,
                SnapshotCategory = captured.Category,
            });
        }

        var sessionActor = SessionActor.From(ctx);
        var actor = CapturedActor.Resolve(ctx, sessionActor);
        var now = DateTimeOffset.UtcNow;
        var teamCategory = carnivalEvent.Category ?? legs[0].SnapshotCategory;
        var entry = new Entry
        {
            Id = clientEntryId ?? Guid.CreateVersion7(),
            CarnivalEventId = eventId,
            StudentId = null,
            HouseCode = houseCode,
            Heat = request.Heat,
            Lane = null,
            RosterSnapshotId = null,
            SnapshotDisplayName = $"{houseCode} team",
            SnapshotYearLevel = 0,
            SnapshotHouseCode = houseCode,
            SnapshotAgeGroup = carnivalEvent.AgeGroup,
            SnapshotCategory = teamCategory,
            CreatedAtUtc = now,
            CreatedBySubject = actor.Subject,
            RelayLegs = legs,
        };
        db.Entries.Add(entry);
        audit.Record(actor, AuditActions.TeamEntryCreate, nameof(Entry), entry.Id.ToString(),
            newValues: new
            {
                houseCode,
                ageGroup = entry.SnapshotAgeGroup,
                category = teamCategory.ToString(),
                legs = legs.Select(l => new { l.LegOrder, l.Stroke, l.StudentId, l.SnapshotDisplayName }),
                capturedActorDisplayName = CapturedActor.DisplayName(ctx),
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
                deviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
            });
        if (await SaveGuardingDuplicateAsync(db, ErrorCodes.TeamExists,
                $"House {houseCode} was entered in this event by another device while this create "
                + "was in flight. Re-read the entries; the team already exists.", ct) is { } lost)
            return lost;
        return Results.Created($"/api/v1/carnivals/{id}/events/{eventId}/entries/{entry.Id}",
            await PlacedAsync(id, eventId, request.RaceId, entry, carnivalEvent, ctx, db, audit, ct));
    }

    private static async Task<IResult> RemoveEntryAsync(
        Guid id, Guid eventId, Guid entryId, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var entry = await db.Entries
            .Include(e => e.CarnivalEvent).ThenInclude(ce => ce.Carnival)
            .Include(e => e.RelayLegs) // the removed swimmers must survive in the audit snapshot
            .FirstOrDefaultAsync(e => e.Id == entryId && e.CarnivalEventId == eventId && e.CarnivalEvent.CarnivalId == id, ct);
        if (entry is null) return EntryNotFound();
        if (CarnivalGuards.RequireEventEditable(entry.CarnivalEvent, "Entries cannot be removed") is { } refusal)
            return refusal;
        if (await db.Results.AnyAsync(r => r.EntryId == entryId, ct))
            return ApiError.Conflict(ErrorCodes.EntryHasResult,
                "The entry has a recorded result and cannot be removed.");

        // Projected before Remove: cascading the delete detaches the legs from
        // the navigation collection, which would empty the audit snapshot.
        var priorValues = Project(entry);
        db.Entries.Remove(entry);
        audit.Record(SessionActor.From(ctx), AuditActions.EntryRemove, nameof(Entry), entryId.ToString(),
            priorValues: priorValues);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetCategoryOverrideAsync(
        Guid id, Guid eventId, Guid entryId, CategoryOverrideRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var entry = await db.Entries
            .Include(e => e.CarnivalEvent).ThenInclude(ce => ce.Carnival)
            .Include(e => e.RelayLegs)
            .FirstOrDefaultAsync(e => e.Id == entryId && e.CarnivalEventId == eventId && e.CarnivalEvent.CarnivalId == id, ct);
        if (entry is null) return EntryNotFound();
        if (CarnivalGuards.RequireEventEditable(entry.CarnivalEvent, "Category overrides cannot be changed")
            is { } refusal)
            return refusal;
        // A team's SnapshotCategory is synthesised from the division; nothing to override.
        if (entry.StudentId is null)
            return ApiError.Unprocessable(ErrorCodes.CategoryOverrideNotApplicable,
                "A team entry has no student category to override; correct the event's division or the team instead.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired,
                "A reason is required for a category override change (ADR-0003).");

        if (EligibilityService.CheckCategory(entry.CarnivalEvent, entry.SnapshotCategory, request.Category)
            is { IsEligible: false } refused)
            return EligibilityService.Refuse(refused,
                $"That override would not match this event's division ({DivisionNaming.Category(entry.CarnivalEvent.Category)}).");

        var actor = SessionActor.From(ctx);
        var prior = entry.CategoryOverride;
        entry.CategoryOverride = request.Category;
        entry.CategoryOverrideActorSubject = actor.Subject;
        entry.CategoryOverrideAtUtc = DateTimeOffset.UtcNow;
        entry.CategoryOverrideReason = request.Reason;
        audit.Record(actor, AuditActions.EntryCategoryOverride, nameof(Entry), entryId.ToString(),
            priorValues: new { categoryOverride = prior?.ToString(), snapshotCategory = entry.SnapshotCategory.ToString() },
            newValues: new { categoryOverride = request.Category?.ToString() },
            reason: request.Reason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Project(entry));
    }
}
