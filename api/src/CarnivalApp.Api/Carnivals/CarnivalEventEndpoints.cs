using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

public static class CarnivalEventEndpoints
{
    public static void MapCarnivalEventEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var carnivals = apiV1.MapGroup("/carnivals");
        carnivals.MapGet("/{id:guid}/events", ListEventsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<CarnivalEventDto>>();
        carnivals.MapGet("/{id:guid}/events/{eventId:guid}", GetEventAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<CarnivalEventDto>();
        carnivals.MapPost("/{id:guid}/events", ScheduleEventAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalEventDto>(StatusCodes.Status201Created);
        carnivals.MapPost("/{id:guid}/events/generate", GenerateDivisionsAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<GenerateDivisionsResponse>();
        carnivals.MapDelete("/{id:guid}/events/{eventId:guid}", UnscheduleEventAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);
        carnivals.MapPut("/{id:guid}/events/{eventId:guid}", UpdateEventAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalEventDto>();
        carnivals.MapPost("/{id:guid}/events/{eventId:guid}/status", ChangeEventStatusAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalEventDto>();
        carnivals.MapPost("/{id:guid}/events/open-scheduled", OpenScheduledEventsAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<OpenScheduledResponse>();
        carnivals.MapPut("/{id:guid}/events/{eventId:guid}/officials", AssignOfficialsAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<OfficialsResponse>();
        // Capture policy, not administer: the people at the mat raise the bar.
        carnivals.MapPost("/{id:guid}/events/{eventId:guid}/heights", RaiseBarAsync)
            .RequireAuthorization(CarnivalAuthorization.SubmitResultsPolicy)
            .Produces<CarnivalEventDto>();
        carnivals.MapPut("/{id:guid}/events/{eventId:guid}/attempts-per-height", SetAttemptsPerHeightAsync)
            .RequireAuthorization(CarnivalAuthorization.SubmitResultsPolicy)
            .Produces<CarnivalEventDto>();
    }

    /// <summary>
    /// A null or "OPEN" age is open across the age axis. Slot placement is
    /// <see cref="ScheduleEndpoints"/>' alone.
    /// </summary>
    public sealed record ScheduleEventRequest(
        Guid EventDefinitionId, string? AgeGroup,
        CompetitionCategory? Category, EventTier? Tier, EventScoringMode? ScoringMode,
        bool? ScoresPoints);

    public sealed record UpdateEventRequest(
        string? AgeGroup,
        CompetitionCategory? Category, EventTier? Tier, EventScoringMode? ScoringMode,
        bool? ScoresPoints);

    public sealed record StatusRequest(string? Status, string? Reason);

    public sealed record OpenScheduledResponse(int Opened, IReadOnlyList<Guid> EventIds);

    public sealed record GenerateDivisionsRequest(
        Guid EventDefinitionId, List<GenerateDivisionRequest>? Divisions,
        EventScoringMode? ScoringMode, bool? ScoresPoints);

    public sealed record GenerateDivisionRequest(
        string? AgeGroup, CompetitionCategory? Category, EventTier? Tier);

    public sealed record GenerateDivisionsResponse(
        Guid EventDefinitionId, IReadOnlyList<CarnivalEventDto> Divisions, int Created, int Skipped);

    /// <summary>Above any real matrix, so a request past it is a client defect.</summary>
    private const int MaxGeneratedDivisions = 200;

    public sealed record OfficialsRequest(List<OfficialAssignment>? Officials);

    /// <summary>In metres.</summary>
    public sealed record RaiseBarRequest(decimal Height);

    public sealed record AttemptsPerHeightRequest(int AttemptsPerHeight);
    public sealed record OfficialAssignment(string SubjectId, string? DisplayName);

    /// <summary>Write paths answer with the event alone; GETs fill the context.</summary>
    public sealed record CarnivalEventDto(
        Guid Id,
        Guid CarnivalId,
        Guid EventDefinitionId,
        MeasureType MeasureType,
        /// <summary>Null means one typed mark.</summary>
        int? AttemptCount,
        /// <summary>Suggestions only; never a mode test.</summary>
        LadderDto? Ladder,
        /// <summary>Ascending. Null on a non-Height event; empty when no bar is set yet.</summary>
        IReadOnlyList<decimal>? Heights,
        /// <summary>Null on a non-Height event.</summary>
        int? AttemptsPerHeight,
        /// <summary>The age contested ("13", "SNR"), or "OPEN" across the axis.</summary>
        string AgeGroup,
        CompetitionCategory? Category,
        EventTier Tier,
        /// <summary>Explicit override only; null means the tier decides.</summary>
        EventScoringMode? ScoringMode,
        EventScoringMode EffectiveScoringMode,
        bool ScoresPoints,
        /// <summary>Advisory: nothing may gate on null.</summary>
        Guid? ScheduleSlotId,
        int? SlotOrder,
        CarnivalEventStatus Status,
        int Version,
        string? DefinitionCode = null,
        string? DefinitionName = null,
        int? EntryCount = null,
        /// <summary>Entries with a result, not results: never exceeds EntryCount.</summary>
        int? ResultCount = null,
        /// <summary>
        /// A derived readout, never a status: a late entry un-completes the event. False also means
        /// "not known" on a write response.
        /// </summary>
        bool Complete = false,
        IReadOnlyList<OfficialDto>? Officials = null,
        EventFormat? Format = null,
        int? TeamSize = null,
        IReadOnlyList<string>? Legs = null,
        IReadOnlyList<string>? Tags = null);

    public sealed record OfficialDto(string SubjectId, string? SubjectDisplayName);

    public sealed record OfficialsResponse(IReadOnlyList<OfficialDto> Officials);

    /// <summary>Not every write path loads the definition, so the measure is passed in.</summary>
    private static CarnivalEventDto Project(
        CarnivalEvent e, MeasureType measureType, int? attemptCount, string? ladderJson) => new(
        e.Id, e.CarnivalId, e.EventDefinitionId, measureType, attemptCount,
        JumpCard.ParseLadder(ladderJson), HeightsOrNull(measureType, e.HeightsJson),
        AttemptsPerHeightOrNull(measureType, e),
        e.AgeGroup, e.Category,
        e.Tier, e.ScoringMode, e.EffectiveScoringMode, e.ScoresPoints,
        e.ScheduleSlotId, e.SlotOrder, e.Status, e.Version);

    private static CarnivalEventDto Project(CarnivalEvent e) => Project(
        e, e.EventDefinition.MeasureType, e.EventDefinition.AttemptCount, e.EventDefinition.LadderJson);

    private static IReadOnlyList<decimal>? HeightsOrNull(MeasureType measureType, string? heightsJson) =>
        measureType == MeasureType.Height ? JumpCard.ParseHeights(heightsJson) : null;

    private static int? AttemptsPerHeightOrNull(MeasureType measureType, CarnivalEvent e) =>
        measureType == MeasureType.Height ? JumpCard.AttemptsPerHeightOf(e) : null;

    private sealed record CarnivalEventRow(
        CarnivalEvent Event, string Code, string Name, MeasureType MeasureType, int? AttemptCount,
        string? LadderJson,
        EventFormat Format, int? TeamSize, string? LegsJson, string? TagsJson,
        int EntryCount, int ResultCount, List<OfficialDto> Officials,
        int? StreamOrder, int? SlotOrder);

    private static CarnivalEventDto Project(CarnivalEventRow row) => new(
        row.Event.Id,
        row.Event.CarnivalId,
        row.Event.EventDefinitionId,
        row.MeasureType,
        row.AttemptCount,
        JumpCard.ParseLadder(row.LadderJson),
        HeightsOrNull(row.MeasureType, row.Event.HeightsJson),
        AttemptsPerHeightOrNull(row.MeasureType, row.Event),
        row.Event.AgeGroup,
        row.Event.Category,
        row.Event.Tier,
        row.Event.ScoringMode,
        row.Event.EffectiveScoringMode,
        row.Event.ScoresPoints,
        row.Event.ScheduleSlotId,
        row.Event.SlotOrder,
        row.Event.Status,
        row.Event.Version,
        row.Code,
        row.Name,
        row.EntryCount,
        row.ResultCount,
        IsComplete(row),
        row.Officials,
        row.Format,
        row.TeamSize,
        // Tolerant: one malformed blob degrades that field, not the whole list.
        JsonBlob.StringList(row.LegsJson),
        JsonBlob.StringList(row.TagsJson));

    /// <summary>
    /// No entries is not complete: "done" would hide a division nobody entered. A Social event
    /// owes no results.
    /// </summary>
    private static bool IsComplete(CarnivalEventRow row) =>
        row.EntryCount > 0 &&
        (row.Event.EffectiveScoringMode == EventScoringMode.Social || row.ResultCount >= row.EntryCount);

    private static IQueryable<CarnivalEventRow> EventRows(IQueryable<CarnivalEvent> events) =>
        events.AsNoTracking()
            .Select(e => new CarnivalEventRow(
                e,
                e.EventDefinition.Code,
                e.EventDefinition.Name,
                e.EventDefinition.MeasureType,
                e.EventDefinition.AttemptCount,
                e.EventDefinition.LadderJson,
                e.EventDefinition.Format,
                e.EventDefinition.TeamSize,
                e.EventDefinition.LegsJson,
                e.EventDefinition.TagsJson,
                e.Entries.Count,
                e.Entries.Count(en => en.Results.AsQueryable().Any(JumpCard.Recorded)),
                e.Officials.Select(o => new OfficialDto(o.SubjectId, o.SubjectDisplayName)).ToList(),
                e.ScheduleSlot == null ? null : (int?)e.ScheduleSlot.Stream.SortOrder,
                e.ScheduleSlot == null ? null : (int?)e.ScheduleSlot.SortOrder));

    private static async Task<IResult> ListEventsAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct)) return CarnivalGuards.CarnivalNotFound();
        // Ordered in memory to keep the query provider-clean; the list is small.
        var events = await EventRows(db.CarnivalEvents.Where(e => e.CarnivalId == id))
            .ToListAsync(ct);
        // Display order only, never "runnable yet": events run out of order as a matter of course.
        return Results.Ok(events
            .OrderBy(e => e.StreamOrder ?? int.MaxValue)
            .ThenBy(e => e.SlotOrder ?? int.MaxValue)
            .ThenBy(e => e.Event.SlotOrder ?? int.MaxValue)
            .ThenBy(e => e.Code).ThenBy(e => e.Event.AgeGroup).ThenBy(e => e.Event.Category)
            .ThenBy(e => e.Event.Tier)
            .Select(Project));
    }

    private static async Task<IResult> GetEventAsync(
        Guid id, Guid eventId, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var row = await EventRows(db.CarnivalEvents.Where(e => e.CarnivalId == id && e.Id == eventId))
            .FirstOrDefaultAsync(ct);
        return row is null ? CarnivalGuards.EventNotFound() : Results.Ok(Project(row));
    }

    private static async Task<IResult> ScheduleEventAsync(
        Guid id, ScheduleEventRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireEditable(carnival, "Events cannot be scheduled") is { } refusal)
            return refusal;
        if (AgeScope.Validate(request.AgeGroup) is { } badScope)
            return ApiError.BadRequest(ErrorCodes.AgeScopeInvalid, badScope);

        var definition = await db.EventDefinitions.FirstOrDefaultAsync(d => d.Id == request.EventDefinitionId, ct);
        if (definition is null)
            return ApiError.BadRequest(ErrorCodes.EventDefinitionNotFound,
                "eventDefinitionId does not reference a known event definition.");
        if (!definition.IsActive)
            return ApiError.BadRequest(ErrorCodes.EventDefinitionInactive,
                $"Event definition '{definition.Code}' is inactive.");

        var age = AgeScope.Normalise(request.AgeGroup);
        var tier = request.Tier ?? EventTier.Competitive;
        // The unique index treats NULL categories as distinct, so this is the only guard for an
        // uncategorised division.
        var duplicate = await db.CarnivalEvents.AnyAsync(e =>
            e.CarnivalId == id
            && e.EventDefinitionId == request.EventDefinitionId
            && e.AgeGroup == age
            && e.Category == request.Category
            && e.Tier == tier, ct);
        if (duplicate)
            return ApiError.Conflict(ErrorCodes.EventAlreadyScheduled,
                "This event definition is already scheduled for this division " +
                "(one instance per definition + age + category + tier, ADR-0003/0014).");

        var carnivalEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = id,
            EventDefinitionId = definition.Id,
            AgeGroup = age,
            Category = request.Category,
            Tier = tier,
            ScoringMode = request.ScoringMode,
            ScoresPoints = request.ScoresPoints ?? true,
            AttemptsPerHeight = JumpCard.AttemptsPerHeightFor(definition),
            Status = CarnivalEventStatus.Scheduled,
        };
        db.CarnivalEvents.Add(carnivalEvent);
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventSchedule, nameof(CarnivalEvent),
            carnivalEvent.Id.ToString(),
            newValues: Project(carnivalEvent, definition.MeasureType, definition.AttemptCount, definition.LadderJson), ruleVersion: carnival.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/events/{carnivalEvent.Id}",
            Project(carnivalEvent, definition.MeasureType, definition.AttemptCount, definition.LadderJson));
    }

    /// <summary>Idempotent, and one SaveChanges: a partial matrix is not a valid state.</summary>
    private static async Task<IResult> GenerateDivisionsAsync(
        Guid id, GenerateDivisionsRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireEditable(carnival, "Divisions cannot be generated") is { } refusal)
            return refusal;

        var requested = request.Divisions ?? [];
        if (requested.Count == 0)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "divisions must name at least one division to generate.");
        if (requested.Count > MaxGeneratedDivisions)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"A generate request may name at most {MaxGeneratedDivisions} divisions.");
        foreach (var division in requested)
            if (AgeScope.Validate(division.AgeGroup) is { } badScope)
                return ApiError.BadRequest(ErrorCodes.AgeScopeInvalid, badScope);

        var definition = await db.EventDefinitions.FirstOrDefaultAsync(d => d.Id == request.EventDefinitionId, ct);
        if (definition is null)
            return ApiError.BadRequest(ErrorCodes.EventDefinitionNotFound,
                "eventDefinitionId does not reference a known event definition.");
        if (!definition.IsActive)
            return ApiError.BadRequest(ErrorCodes.EventDefinitionInactive,
                $"Event definition '{definition.Code}' is inactive.");

        var alreadyScheduled = (await db.CarnivalEvents
                .Where(e => e.CarnivalId == id && e.EventDefinitionId == definition.Id)
                .Select(e => new { e.AgeGroup, e.Category, e.Tier })
                .ToListAsync(ct))
            .Select(e => new DivisionGenerator.DivisionKey(definition.Id, e.AgeGroup, e.Category, e.Tier))
            .ToHashSet();

        var outcome = DivisionGenerator.Generate(
            id, definition,
            requested.Select(d => new DivisionSpec(
                d.AgeGroup, d.Category, d.Tier ?? EventTier.Competitive)),
            alreadyScheduled, request.ScoringMode, request.ScoresPoints ?? true);

        db.CarnivalEvents.AddRange(outcome.Created);
        var actor = SessionActor.From(ctx);
        foreach (var created in outcome.Created)
            audit.Record(actor, AuditActions.CarnivalEventSchedule, nameof(CarnivalEvent), created.Id.ToString(),
                newValues: Project(created, definition.MeasureType, definition.AttemptCount, definition.LadderJson), ruleVersion: carnival.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new GenerateDivisionsResponse(
            definition.Id,
            outcome.Created.Select(e => Project(e, definition.MeasureType, definition.AttemptCount, definition.LadderJson)).ToList(),
            outcome.Created.Count, outcome.Skipped));
    }

    /// <summary>A contested division is closed, never deleted.</summary>
    private static async Task<IResult> UnscheduleEventAsync(
        Guid id, Guid eventId, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireEditable(carnivalEvent.Carnival, "Divisions cannot be removed") is { } refusal)
            return refusal;

        var entries = await db.Entries.CountAsync(e => e.CarnivalEventId == eventId, ct);
        var results = await db.Results.CountAsync(r => r.Entry.CarnivalEventId == eventId, ct);
        if (entries > 0 || results > 0)
            return ApiError.Conflict(ErrorCodes.EventHasEntries,
                $"This division has {entries} entries and {results} recorded results. " +
                "Close it instead — a division that has been contested keeps its history.",
                new Dictionary<string, object?> { ["entryCount"] = entries, ["resultCount"] = results });

        // The stamp is "minted for", not ownership: the race outlives the division.
        var stamped = await db.Races.Where(r => r.CarnivalEventId == eventId).ToListAsync(ct);
        foreach (var race in stamped) race.CarnivalEventId = null;

        var prior = Project(carnivalEvent);
        db.CarnivalEvents.Remove(carnivalEvent);
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventUnschedule, nameof(CarnivalEvent),
            eventId.ToString(),
            priorValues: prior, newValues: new { racesDetached = stamped.Count },
            ruleVersion: carnivalEvent.Carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateEventAsync(
        Guid id, Guid eventId, UpdateEventRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireEventEditable(carnivalEvent, "Event details cannot be changed") is { } refusal)
            return refusal;
        if (AgeScope.Validate(request.AgeGroup) is { } badScope)
            return ApiError.BadRequest(ErrorCodes.AgeScopeInvalid, badScope);

        var age = AgeScope.Normalise(request.AgeGroup);
        var tier = request.Tier ?? EventTier.Competitive;
        // Scoring fields change how the event scores, never who may enter, so they are not locked.
        var divisionChanged = age != carnivalEvent.AgeGroup
            || request.Category != carnivalEvent.Category
            || tier != carnivalEvent.Tier;
        if (divisionChanged)
        {
            // Entries carry immutable division eligibility.
            if (await db.Entries.AnyAsync(e => e.CarnivalEventId == eventId, ct))
                return ApiError.Conflict(ErrorCodes.EventDivisionLocked,
                    "The event division cannot be changed while entries exist; remove the entries first.");

            var duplicate = await db.CarnivalEvents.AnyAsync(e =>
                e.CarnivalId == id
                && e.EventDefinitionId == carnivalEvent.EventDefinitionId
                && e.AgeGroup == age
                && e.Category == request.Category
                && e.Tier == tier
                && e.Id != eventId, ct);
            if (duplicate)
                return ApiError.Conflict(ErrorCodes.EventAlreadyScheduled,
                    "Another instance of this event is already scheduled for that division.");
        }

        var prior = Project(carnivalEvent);
        carnivalEvent.AgeGroup = age;
        carnivalEvent.Category = request.Category;
        carnivalEvent.Tier = tier;
        carnivalEvent.ScoringMode = request.ScoringMode;
        carnivalEvent.ScoresPoints = request.ScoresPoints ?? true;
        carnivalEvent.Version++;
        audit.Record(SessionActor.From(ctx),
            divisionChanged ? AuditActions.CarnivalEventDivisionChange : AuditActions.CarnivalEventUpdate,
            nameof(CarnivalEvent), eventId.ToString(),
            priorValues: prior, newValues: Project(carnivalEvent));
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnivalEvent));
    }

    /// <summary>Finalised → Closed reopens legacy rows; nothing enters Finalised.</summary>
    private static readonly IReadOnlySet<(CarnivalEventStatus From, CarnivalEventStatus To)> EventTransitions =
        new HashSet<(CarnivalEventStatus, CarnivalEventStatus)>
        {
            (CarnivalEventStatus.Scheduled, CarnivalEventStatus.Open),
            (CarnivalEventStatus.Open, CarnivalEventStatus.Closed),
            (CarnivalEventStatus.Closed, CarnivalEventStatus.Open),
            (CarnivalEventStatus.Finalised, CarnivalEventStatus.Closed),
        };

    private static async Task<IResult> ChangeEventStatusAsync(
        Guid id, Guid eventId, StatusRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        if (!Enum.TryParse<CarnivalEventStatus>(request.Status, ignoreCase: true, out var target))
            return ApiError.BadRequest(ErrorCodes.UnknownStatus, $"Unknown status '{request.Status}'.");
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireEventEditable(carnivalEvent, "Event status cannot be changed")
            is { } refusal)
            return refusal;

        if (!EventTransitions.Contains((carnivalEvent.Status, target)))
            return ApiError.Conflict(ErrorCodes.IllegalEventTransition,
                $"Illegal event status transition {carnivalEvent.Status} → {target}.");

        var prior = carnivalEvent.Status;
        carnivalEvent.Status = target;
        // Makes the status read-then-write a checked update: concurrent transitions give one 409.
        carnivalEvent.Version++;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventStatus, nameof(CarnivalEvent), eventId.ToString(),
            priorValues: new { status = prior.ToString() },
            newValues: new { status = target.ToString() },
            reason: request.Reason);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnivalEvent));
    }

    /// <summary>A Scheduled event takes a height: the opening one is set before it opens.</summary>
    private static async Task<IResult> RaiseBarAsync(
        Guid id, Guid eventId, RaiseBarRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireOpen(carnivalEvent.Carnival, "The bar cannot be raised") is { } refusal)
            return refusal;
        if (CarnivalGuards.RequireEventCapturable(carnivalEvent, "The bar cannot be raised") is { } shut)
            return shut;
        if (carnivalEvent.EventDefinition.MeasureType != MeasureType.Height)
            return ApiError.Conflict(ErrorCodes.LadderRequired,
                "This event is not measured as a height, so it has no bar to raise.");
        if (!JumpCard.IsHeight(request.Height))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"{request.Height.ToString(CultureInfo.InvariantCulture)} is not a bar height: heights " +
                $"are whole centimetres between {JumpCard.Format(JumpCard.MinHeight)} and " +
                $"{JumpCard.Format(JumpCard.MaxHeight)} metres.");

        var heights = JumpCard.ParseHeights(carnivalEvent.HeightsJson);
        if (heights.Count > 0 && request.Height <= heights[^1])
            return ApiError.Conflict(ErrorCodes.BarNeverLowered,
                $"The bar is at {JumpCard.Format(heights[^1])} m and never comes down, so it cannot go " +
                $"to {JumpCard.Format(request.Height)} m.");

        heights.Add(request.Height);
        var raised = JumpCard.SerialiseHeights(heights);
        if (raised.Length > JumpCard.MaxHeightsJsonLength)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"This division already has {heights.Count - 1} bar heights, the most it can record; "
                + $"the bar cannot go to {JumpCard.Format(request.Height)} m.");

        var prior = Project(carnivalEvent);
        carnivalEvent.HeightsJson = raised;
        carnivalEvent.Version++;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventRaiseBar, nameof(CarnivalEvent),
            eventId.ToString(),
            priorValues: new { heights = prior.Heights },
            newValues: new { height = request.Height, heights },
            ruleVersion: carnivalEvent.Carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnivalEvent));
    }

    /// <summary>Fixed once any card carries a mark judged against the old value.</summary>
    private static async Task<IResult> SetAttemptsPerHeightAsync(
        Guid id, Guid eventId, AttemptsPerHeightRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await CarnivalGuards.FindEventWithCarnivalAsync(db, id, eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        const string refused = "Attempts per height cannot change";
        if (CarnivalGuards.RequireOpen(carnivalEvent.Carnival, refused) is { } refusal)
            return refusal;
        if (CarnivalGuards.RequireEventCapturable(carnivalEvent, refused) is { } shut)
            return shut;
        if (carnivalEvent.EventDefinition.MeasureType != MeasureType.Height)
            return ApiError.Conflict(ErrorCodes.LadderRequired,
                "This event is not measured as a height, so it has no attempts per height.");
        if (!JumpCard.IsAttemptsPerHeight(request.AttemptsPerHeight))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "attemptsPerHeight must be 2 or 3.");

        var prior = JumpCard.AttemptsPerHeightOf(carnivalEvent);
        if (prior == request.AttemptsPerHeight) return Results.Ok(Project(carnivalEvent));
        // An empty card stores as null (JumpCard.Serialise), so any stored card has a mark.
        var cards = await db.Results.AsNoTracking()
            .Where(r => r.Entry.CarnivalEventId == eventId && r.AttemptsJson != null)
            .Select(r => r.AttemptsJson)
            .ToListAsync(ct);
        if (cards.Any(json => JumpCard.Parse(json).Count > 0))
            return ApiError.Conflict(ErrorCodes.AttemptsPerHeightFrozen,
                $"A mark has landed in this division, so it stays at {prior} attempts per height.");

        carnivalEvent.AttemptsPerHeight = request.AttemptsPerHeight;
        carnivalEvent.Version++;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventAttemptsPerHeight,
            nameof(CarnivalEvent), eventId.ToString(),
            priorValues: new { attemptsPerHeight = prior },
            newValues: new { attemptsPerHeight = request.AttemptsPerHeight },
            ruleVersion: carnivalEvent.Carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnivalEvent));
    }

    /// <summary>Idempotent: a second press opens and audits nothing.</summary>
    private static async Task<IResult> OpenScheduledEventsAsync(
        Guid id, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        // Must match the single-event gate: the bulk act may do nothing one-at-a-time may not.
        if (CarnivalGuards.RequireNotFinalised(carnival, "Divisions cannot be opened")
            is { } refusal)
            return refusal;

        var scheduled = await db.CarnivalEvents
            .Where(e => e.CarnivalId == id && e.Status == CarnivalEventStatus.Scheduled)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        foreach (var carnivalEvent in scheduled)
        {
            carnivalEvent.Status = CarnivalEventStatus.Open;
            carnivalEvent.Version++;
        }

        var eventIds = scheduled.Select(e => e.Id).ToList();
        if (eventIds.Count > 0)
            audit.Record(SessionActor.From(ctx), AuditActions.CarnivalEventOpenAll, nameof(Carnival),
                id.ToString(),
                newValues: new { opened = eventIds.Count, eventIds },
                ruleVersion: carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "event", ct) is { } conflict)
            return conflict;
        return Results.Ok(new OpenScheduledResponse(eventIds.Count, eventIds));
    }

    private static async Task<IResult> AssignOfficialsAsync(
        Guid id, Guid eventId, OfficialsRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnivalEvent = await db.CarnivalEvents
            .Include(e => e.Officials)
            .Include(e => e.Carnival)
            .FirstOrDefaultAsync(e => e.CarnivalId == id && e.Id == eventId, ct);
        if (carnivalEvent is null) return CarnivalGuards.EventNotFound();
        if (CarnivalGuards.RequireEventEditable(carnivalEvent, "Officials cannot be reassigned") is { } refusal)
            return refusal;

        var requested = (request.Officials ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.SubjectId))
            .DistinctBy(o => o.SubjectId)
            .ToList();
        foreach (var official in requested)
        {
            var tooLong = FieldLimits.Refuse(official.SubjectId, FieldLimits.Subject, "officials[].subjectId")
                ?? FieldLimits.Refuse(official.DisplayName, FieldLimits.DisplayName, "officials[].displayName");
            if (tooLong is not null) return tooLong;
        }

        var actor = SessionActor.From(ctx);
        var prior = carnivalEvent.Officials.Select(o => o.SubjectId).Order().ToList();

        db.CarnivalEventOfficials.RemoveRange(
            carnivalEvent.Officials.Where(o => requested.All(r => r.SubjectId != o.SubjectId)));
        foreach (var assignment in requested.Where(r => carnivalEvent.Officials.All(o => o.SubjectId != r.SubjectId)))
            db.CarnivalEventOfficials.Add(new CarnivalEventOfficial
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = eventId,
                SubjectId = assignment.SubjectId,
                SubjectDisplayName = assignment.DisplayName,
                AssignedAtUtc = DateTimeOffset.UtcNow,
                AssignedBySubject = actor.Subject,
            });

        audit.Record(actor, AuditActions.CarnivalEventAssignOfficials, nameof(CarnivalEvent), eventId.ToString(),
            priorValues: new { officials = prior },
            newValues: new { officials = requested.Select(o => o.SubjectId).Order().ToList() });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new OfficialsResponse(
            requested.Select(o => new OfficialDto(o.SubjectId, o.DisplayName)).ToList()));
    }

}
