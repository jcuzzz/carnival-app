using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Carnivals;

/// <summary>
/// The Idempotency-Key is the result id. Every change appends a revision and bumps the
/// <see cref="Result.CurrentRevision"/> token: conflicts are 409, never last-write-wins.
/// </summary>
public static class ResultEndpoints
{
    public static void MapResultEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var events = apiV1.MapGroup("/carnivals/{id:guid}/events/{eventId:guid}");
        events.MapGet("/results", ListResultsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<ResultDto>>();
        events.MapPost("/entries/{entryId:guid}/results", SubmitResultAsync)
            .RequireAuthorization(CarnivalAuthorization.SubmitResultsPolicy)
            .AddEndpointFilter<IdempotencyEndpointFilter>()
            .Produces<ResultDto>(StatusCodes.Status201Created);
        // The first attempt creates the result, so its key is the result id as a submit's is.
        events.MapPut("/entries/{entryId:guid}/attempts/{ordinal:int}", RecordAttemptAsync)
            .RequireAuthorization(CarnivalAuthorization.SubmitResultsPolicy)
            .AddEndpointFilter<IdempotencyEndpointFilter>()
            .Produces<ResultDto>(StatusCodes.Status201Created)
            .Produces<ResultDto>(StatusCodes.Status200OK);
        // Addressed by bar height: an ordinal is meaningless across heights.
        events.MapPut("/entries/{entryId:guid}/jumps/{height:decimal}", RecordJumpAsync)
            .RequireAuthorization(CarnivalAuthorization.SubmitResultsPolicy)
            .AddEndpointFilter<IdempotencyEndpointFilter>()
            .Produces<ResultDto>(StatusCodes.Status201Created)
            .Produces<ResultDto>(StatusCodes.Status200OK);

        var results = apiV1.MapGroup("/results/{resultId:guid}");
        results.MapPost("/correct", CorrectResultAsync)
            .RequireAuthorization(CarnivalAuthorization.CorrectResultsPolicy)
            .Produces<ResultDto>();
    }

    /// <summary>Test interleave point: after "no result yet", before the write.</summary>
    public const string AttemptCreateTag = "attempt create id check";

    public sealed record SubmitResultRequest(string? Value, string? Status, DateTimeOffset? RecordedAtUtc);

    /// <summary>
    /// <see cref="Attempts"/> is the whole series on an attempts event (a null element is an
    /// untaken ordinal) and absent everywhere else.
    /// </summary>
    public sealed record CorrectResultRequest(
        int ExpectedRevision, string? Value, string? Status, string? Reason,
        IReadOnlyList<AttemptCorrection?>? Attempts = null,
        /// <summary>The whole card on a Height event; absent elsewhere.</summary>
        IReadOnlyList<JumpCorrection>? Jumps = null);

    public sealed record AttemptCorrection(string? Value, bool Foul);

    public sealed record JumpCorrection(decimal Height, string? Marks);

    public sealed record AttemptRequest(string? Value, bool Foul, DateTimeOffset? RecordedAtUtc);

    /// <summary>O cleared, X failed, - passed.</summary>
    public sealed record JumpRequest(string? Mark, DateTimeOffset? RecordedAtUtc);

    public sealed record TransitionRequest(string? Reason);

    /// <summary>A correction echoes <see cref="CurrentRevision"/> as expectedRevision.</summary>
    public sealed record ResultDto(
        Guid Id,
        Guid EntryId,
        ResultStatus Status,
        /// <summary>Canonical measure (seconds for Time); null for DNS/DNF/DQ/NM.</summary>
        decimal? MeasuredValue,
        ResultLifecycleState LifecycleState,
        int CurrentRevision,
        DateTimeOffset RecordedAtUtc,
        string RecordedBySubject,
        /// <summary>The results book's rank; null where it prints "—" and on writes.</summary>
        int? Place,
        /// <summary>Null on a single-measure event; a null element is an untaken ordinal.</summary>
        IReadOnlyList<AttemptDto?>? Attempts = null,
        /// <summary>Height events only; never set alongside <see cref="Attempts"/>.</summary>
        IReadOnlyList<JumpDto>? Jumps = null);

    private static ResultDto Project(Result r, int? place = null)
    {
        // One blob, two shapes: the card and the ordinal series never fill each other's field.
        var jumps = JumpCard.TryParse(r.AttemptsJson, out var card) ? card : null;
        return new(
            r.Id,
            r.EntryId,
            r.Status,
            r.MeasuredValue,
            r.LifecycleState,
            r.CurrentRevision,
            r.RecordedAtUtc,
            r.RecordedBySubject,
            place,
            jumps is null && r.AttemptsJson is not null ? AttemptSeries.Parse(r.AttemptsJson) : null,
            jumps);
    }

    private static object MaterialValues(Result r) => new
    {
        measuredValue = r.MeasuredValue,
        status = r.Status.ToString(),
        lifecycleState = r.LifecycleState.ToString(),
        attemptsJson = r.AttemptsJson,
    };

    private static void AppendRevision(
        CarnivalAppDbContext db, Result result, object prior, AuditActor actor, string reason)
    {
        result.CurrentRevision++;
        db.ResultRevisions.Add(new ResultRevision
        {
            Id = Guid.CreateVersion7(),
            ResultId = result.Id,
            RevisionNumber = result.CurrentRevision,
            PriorValuesJson = System.Text.Json.JsonSerializer.Serialize(prior),
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(MaterialValues(result)),
            ActorSubject = actor.Subject,
            ActorRole = actor.Role,
            AtUtc = DateTimeOffset.UtcNow,
            Reason = reason,
        });
    }

    private static IResult? ValidateStatus(
        MeasureType measureType, string? raw, string? value, out ResultStatus status)
    {
        status = ResultStatus.Ok;
        if (string.IsNullOrWhiteSpace(raw))
            return string.IsNullOrWhiteSpace(value)
                ? ApiError.BadRequest(ErrorCodes.ResultValueRequired,
                    "Provide a measured value, or a non-result status (DNS, DNF, DQ, NM).")
                : null;
        // Enum.TryParse also reads "3" (Dq) and "Dns,Dnf" (a defined member on this non-flags
        // enum), so only a spelled name is a status.
        var typed = raw.Trim();
        if (typed.Contains(',') || !char.IsLetter(typed[0])
            || !Enum.TryParse(typed, ignoreCase: true, out status) || !Enum.IsDefined(status))
            return ApiError.BadRequest(ErrorCodes.UnknownResultStatus,
                $"Unknown result status '{raw}'. Valid: Ok, DNS, DNF, DQ, NM.");
        // Before the value checks: "cannot carry a value" would send the recorder after the wrong
        // problem.
        if (status == ResultStatus.Nm && measureType == MeasureType.Time)
            return ApiError.BadRequest(ErrorCodes.NmRequiresFieldMeasure,
                "NM is recorded on a field event only (ADR-0052 §4): a runner who starts and " +
                "does not finish is a DNF.");
        if (status == ResultStatus.Ok && string.IsNullOrWhiteSpace(value))
            return ApiError.BadRequest(ErrorCodes.ResultValueRequired,
                "A measured value is required for an Ok result.");
        if (status != ResultStatus.Ok && !string.IsNullOrWhiteSpace(value))
            return ApiError.BadRequest(ErrorCodes.ResultValueNotAllowed,
                $"A {status.ToString().ToUpperInvariant()} result cannot carry a measured value.");
        return null;
    }

    /// <summary>A conflict, not a 400: the mark is real work, so the item parks.</summary>
    private static IResult AttemptsRequired(ResultStatus status) => ApiError.Conflict(
        ErrorCodes.AttemptsRequired,
        status == ResultStatus.Nm
            ? "This event is recorded attempt by attempt, and NM is derived from a series of fouls."
            : "This event is recorded attempt by attempt, so the best mark is derived, never typed.");

    private static IResult LadderRequired() => ApiError.Conflict(
        ErrorCodes.LadderRequired,
        "This event takes jumps, not a series: each mark is recorded against a bar height and " +
        "the best height is derived, never typed.");

    private static IResult NotContestedOnABar() => ApiError.Conflict(
        ErrorCodes.LadderRequired,
        "This event is not measured as a height, so it has no bar heights to jump at.");

    /// <summary>A typed DNS/DNF/DQ clears the series; anything else restates it whole.</summary>
    private static IResult? CorrectedSeries(
        CorrectResultRequest request, MeasureType measureType, int attemptCount,
        IReadOnlyList<AttemptDto?> stored,
        out ResultStatus status, out decimal? measured, out string? attemptsJson)
    {
        status = ResultStatus.Ok;
        measured = null;
        attemptsJson = null;

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (ValidateStatus(measureType, request.Status, request.Value, out status) is { } statusError)
                return statusError;
            if (status is ResultStatus.Ok or ResultStatus.Nm) return AttemptsRequired(status);
            return request.Attempts is null
                ? null
                : ApiError.BadRequest(ErrorCodes.ValidationFailed,
                    $"A {status.ToString().ToUpperInvariant()} result cannot carry attempts.");
        }

        if (request.Attempts is not { } corrections) return AttemptsRequired(ResultStatus.Ok);
        if (!string.IsNullOrWhiteSpace(request.Value))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A corrected series carries no best mark: the server derives it.");
        if (corrections.Count > attemptCount)
            return ApiError.Conflict(ErrorCodes.AttemptOrdinalOutOfRange,
                $"This event allows {attemptCount} attempts, and the correction carries {corrections.Count}.");
        // A short series would silently delete a recorded ordinal.
        var lastTaken = stored.Count;
        while (lastTaken > 0 && stored[lastTaken - 1] is not { Taken: true }) lastTaken--;
        if (corrections.Count < lastTaken)
            return ApiError.Conflict(ErrorCodes.AttemptsSeriesTruncated,
                $"Attempt {lastTaken} is recorded, so a correction cannot carry only " +
                $"{corrections.Count}. Restate the whole series, or clear it with DNS, DNF or DQ.");

        var series = new List<AttemptDto?>();
        foreach (var correction in corrections)
        {
            if (correction is null) { series.Add(null); continue; }
            if (AttemptMark(correction.Value, correction.Foul, measureType, out var mark) is { } markError)
                return markError;
            series.Add(new AttemptDto(mark, correction.Foul));
        }
        if (series.All(a => a is not { Taken: true }))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A corrected series needs at least one attempt; DNS, DNF or DQ clears it entirely.");

        var higherBetter = Placement.HigherIsBetter(measureType);
        status = AttemptSeries.DeriveStatus(series);
        measured = AttemptSeries.Best(series, higherBetter);
        attemptsJson = AttemptSeries.Serialise(series);
        return null;
    }

    private static IResult JumpsNotLaddered() => ApiError.BadRequest(
        ErrorCodes.ValidationFailed,
        "This event is not measured as a height, so a correction cannot carry a card.");

    /// <summary>
    /// Replayed mark by mark through <see cref="JumpCard.TryAppend"/>, so a card that could not
    /// have been captured cannot be corrected into existence.
    /// </summary>
    private static IResult? CorrectedCard(
        CorrectResultRequest request, MeasureType measureType, IReadOnlyList<decimal> ladderHeights,
        int attemptsPerHeight,
        out ResultStatus status, out decimal? measured, out string? attemptsJson)
    {
        status = ResultStatus.Ok;
        measured = null;
        attemptsJson = null;

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (ValidateStatus(measureType, request.Status, request.Value, out status) is { } statusError)
                return statusError;
            if (status is ResultStatus.Ok or ResultStatus.Nm) return LadderRequired();
            var typed = status.ToString().ToUpperInvariant();
            if (request.Jumps is not null)
                return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                    $"A {typed} result cannot carry a card.");
            return request.Attempts is null
                ? null
                : ApiError.BadRequest(ErrorCodes.ValidationFailed,
                    $"A {typed} result cannot carry attempts.");
        }

        if (!string.IsNullOrWhiteSpace(request.Value) || request.Attempts is not null)
            return LadderRequired();
        if (request.Jumps is not { } corrections) return LadderRequired();
        // An empty card keeps the result row and its history, holding no card: NM.
        if (corrections.Count == 0)
        {
            status = ResultStatus.Nm;
            return null;
        }
        // The replay is cumulative, so a height named twice would append to itself.
        if (corrections.Select(c => c.Height).Distinct().Count() != corrections.Count)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A corrected card names each bar height once, carrying every mark taken at it.");

        List<JumpDto> card = [];
        foreach (var correction in corrections)
        {
            if (!JumpCard.IsValidMarks(correction.Marks, attemptsPerHeight))
                return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                    $"'{correction.Marks}' is not a legal set of marks at {JumpCard.Format(correction.Height)} m: " +
                    $"at most {attemptsPerHeight}, each an X until an O or - ends the height.");
            foreach (var mark in correction.Marks!)
                if (JumpCard.TryAppend(card, ladderHeights, attemptsPerHeight, correction.Height,
                        mark.ToString(), out card)
                    is { } refusal)
                    return refusal.Result();
        }

        status = JumpCard.DeriveStatus(card);
        measured = JumpCard.Best(card);
        attemptsJson = JumpCard.Serialise(card);
        return null;
    }

    /// <summary>Null <paramref name="mark"/> is a foul.</summary>
    private static IResult? AttemptMark(
        string? value, bool foul, MeasureType measureType, out decimal? mark)
    {
        mark = null;
        var typed = !string.IsNullOrWhiteSpace(value);
        if (foul && typed)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A fouled attempt carries no mark.");
        if (!foul && !typed)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "An attempt is either a mark or a foul.");
        if (foul) return null;
        if (!ResultValueParser.TryParse(measureType, value!, out var parsed, out var parseError))
            return ApiError.BadRequest(ErrorCodes.ResultValueInvalid, parseError!);
        mark = parsed;
        return null;
    }

    private static async Task<IResult> ListResultsAsync(
        Guid id, Guid eventId, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var scoring = await db.CarnivalEvents.AsNoTracking()
            .Where(e => e.CarnivalId == id && e.Id == eventId)
            .Select(e => new { e.Tier, e.ScoringMode, e.EventDefinition.MeasureType })
            .FirstOrDefaultAsync(ct);
        if (scoring is null) return CarnivalGuards.EventNotFound();
        var results = await db.Results.AsNoTracking()
            .Where(r => r.Entry.CarnivalEventId == eventId)
            .OrderBy(r => r.Entry.SnapshotDisplayName)
            .Select(r => new { Result = r, r.Entry.Heat })
            .ToListAsync(ct);
        // Must rank exactly the field the results book ranks: a Draft is in neither.
        var higherBetter = Placement.HigherIsBetter(scoring.MeasureType);
        var places = HeatPlacings.Rank(
            results
                .Where(r => r.Result.LifecycleState != ResultLifecycleState.Draft)
                .Select(r => new HeatPlacings.Swim(
                    r.Result.Id, r.Heat, r.Result.Status, r.Result.MeasuredValue,
                    AttemptSeries.KeyOf(r.Result.AttemptsJson, higherBetter))),
            scoring.MeasureType, scoring.ScoringMode, scoring.Tier);
        return Results.Ok(results.Select(r => Project(r.Result, HeatPlacings.Of(places, r.Result.Id))));
    }

    private static async Task<IResult> SubmitResultAsync(
        Guid id, Guid eventId, Guid entryId, SubmitResultRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        // The idempotency filter guarantees a parseable key.
        var resultId = Guid.Parse(ctx.Request.Headers[IdempotencyService.HeaderName].ToString());

        var entry = await db.Entries
            .Include(e => e.CarnivalEvent).ThenInclude(e => e.Carnival)
            .Include(e => e.CarnivalEvent).ThenInclude(e => e.EventDefinition)
            .FirstOrDefaultAsync(e => e.Id == entryId && e.CarnivalEventId == eventId
                                      && e.CarnivalEvent.CarnivalId == id, ct);
        if (entry is null)
            return ApiError.NotFound(ErrorCodes.EntryNotFound, "No entry exists with that id in this event.");

        var carnivalEvent = entry.CarnivalEvent;
        if (CarnivalGuards.RequireOpen(carnivalEvent.Carnival, "Results are not accepted") is { } carnivalClosed)
            return carnivalClosed;
        if (CarnivalGuards.RequireEventOpen(carnivalEvent, "Results are not accepted") is { } eventClosed)
            return eventClosed;

        if (ValidateStatus(carnivalEvent.EventDefinition.MeasureType, request.Status, request.Value,
                out var status) is { } statusError)
            return statusError;

        // On an attempts or Height event the best mark and NM are derived; DNS/DNF/DQ still submit.
        if (status is ResultStatus.Ok or ResultStatus.Nm)
        {
            if (carnivalEvent.EventDefinition.MeasureType == MeasureType.Height) return LadderRequired();
            if (carnivalEvent.EventDefinition.AttemptCount is not null) return AttemptsRequired(status);
        }

        decimal? measured = null;
        if (status == ResultStatus.Ok)
        {
            if (!ResultValueParser.TryParse(carnivalEvent.EventDefinition.MeasureType, request.Value!, out var value, out var parseError))
                return ApiError.BadRequest(ErrorCodes.ResultValueInvalid, parseError!);
            measured = value;
        }

        // One result per entry: a second official's capture is a conflict for a person to resolve.
        if (await db.Results.AnyAsync(r => r.EntryId == entryId, ct)) return ResultExists();

        if (await db.Results.AnyAsync(r => r.Id == resultId, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different result already exists with this submission id.");

        var sessionActor = SessionActor.From(ctx);
        var recorder = CapturedActor.Resolve(ctx, sessionActor);
        var recorderSubject = recorder.Subject;
        var capturedName = CapturedActor.DisplayName(ctx);

        var now = DateTimeOffset.UtcNow;
        var result = new Result
        {
            Id = resultId,
            EntryId = entryId,
            MeasuredValue = measured,
            Status = status,
            // No human verify step: a capture that passes the checks above is verified.
            LifecycleState = ResultLifecycleState.Verified,
            CurrentRevision = 1,
            RecordedBySubject = recorderSubject,
            RecordedAtUtc = request.RecordedAtUtc ?? now,
            CreatedAtUtc = now,
            DeviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
        };
        db.Results.Add(result);
        db.ResultRevisions.Add(new ResultRevision
        {
            Id = Guid.CreateVersion7(),
            ResultId = result.Id,
            RevisionNumber = 1,
            PriorValuesJson = "{}",
            NewValuesJson = System.Text.Json.JsonSerializer.Serialize(MaterialValues(result)),
            ActorSubject = recorder.Subject,
            ActorRole = recorder.Role,
            AtUtc = now,
            Reason = "Initial submission",
        });
        audit.Record(recorder, AuditActions.ResultSubmit, nameof(Result), result.Id.ToString(),
            newValues: new
            {
                measuredValue = result.MeasuredValue,
                status = result.Status.ToString(),
                lifecycleState = result.LifecycleState.ToString(),
                recordedBySubject = recorderSubject,
                capturedActorDisplayName = capturedName,
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
                deviceId = result.DeviceId,
            },
            // The validation rule set, not the carnival's scoring rule version.
            ruleVersion: ValidationRules.Version);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ConcurrencyConflict();
        }
        // The EntryId unique index: a concurrent capture won. Anything else is the exception handler's.
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex, "Results", nameof(Result.EntryId)))
        {
            db.ChangeTracker.Clear();
            return ResultExists();
        }
        return Results.Created($"/api/v1/results/{result.Id}", Project(result));
    }

    private static IResult ResultExists() => ApiError.Conflict(ErrorCodes.ResultExists,
        "This entry already has a result. Correct it rather than capturing it again.");

    private readonly record struct Captured(
        string? AttemptsJson, decimal? MeasuredValue, ResultStatus Status);

    private delegate IResult? CaptureAppend(string? storedJson, out Captured captured);

    private readonly record struct CaptureActors(
        AuditActor Recorder, AuditActor Session, string? CapturedName);

    /// <summary>Ordinals may arrive out of order across devices, so a gap is ordinary.</summary>
    private static Task<IResult> RecordAttemptAsync(
        Guid id, Guid eventId, Guid entryId, int ordinal, AttemptRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        decimal? mark = null;
        var higherBetter = false;
        return CaptureByEntryAsync(
            id, eventId, entryId, ctx, db, "Attempts are not accepted", "an attempt",
            $"Attempt {ordinal}", request.RecordedAtUtc,
            carnivalEvent =>
            {
                var definition = carnivalEvent.EventDefinition;
                // Before ATTEMPTS_NOT_CONFIGURED, which is true of Height but unhelpful.
                if (definition.MeasureType == MeasureType.Height) return LadderRequired();
                if (definition.AttemptCount is not { } attemptCount)
                    return ApiError.Conflict(ErrorCodes.AttemptsNotConfigured,
                        "This event records a single measure, not a series of attempts.");
                if (ordinal < 1 || ordinal > attemptCount)
                    return ApiError.Conflict(ErrorCodes.AttemptOrdinalOutOfRange,
                        $"This event allows {attemptCount} attempts, so attempt {ordinal} does not exist.");
                if (AttemptMark(request.Value, request.Foul, definition.MeasureType, out mark)
                    is { } markError)
                    return markError;
                higherBetter = Placement.HigherIsBetter(definition.MeasureType);
                return null;
            },
            (string? stored, out Captured captured) =>
            {
                captured = default;
                var series = AttemptSeries.Parse(stored);
                if (AttemptSeries.At(series, ordinal) is { Taken: true })
                    return ApiError.Conflict(ErrorCodes.AttemptAlreadyRecorded,
                        $"Attempt {ordinal} is already recorded. Changing it is a correction, which carries a reason.");
                var updated = AttemptSeries.Upsert(series, ordinal, mark, request.Foul);
                captured = new Captured(
                    AttemptSeries.Serialise(updated), AttemptSeries.Best(updated, higherBetter),
                    AttemptSeries.DeriveStatus(updated));
                return null;
            },
            (result, prior, actors) => AuditAttempt(audit, actors, result, ordinal, prior),
            ct);
    }

    private static Task<IResult> RecordJumpAsync(
        Guid id, Guid eventId, Guid entryId, decimal height, JumpRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        List<decimal> ladderHeights = [];
        var attemptsPerHeight = JumpCard.DefaultAttemptsPerHeight;
        List<JumpDto> card = [];
        return CaptureByEntryAsync(
            id, eventId, entryId, ctx, db, "Marks are not accepted", "a jump",
            $"Jump at {EventLadder.FormatValue(MeasureType.Height, height)}", request.RecordedAtUtc,
            carnivalEvent =>
            {
                if (carnivalEvent.EventDefinition.MeasureType != MeasureType.Height)
                    return NotContestedOnABar();
                ladderHeights = JumpCard.ParseHeights(carnivalEvent.HeightsJson);
                attemptsPerHeight = JumpCard.AttemptsPerHeightOf(carnivalEvent);
                return null;
            },
            (string? stored, out Captured captured) =>
            {
                captured = default;
                if (JumpCard.TryAppend(JumpCard.Parse(stored), ladderHeights, attemptsPerHeight, height,
                        request.Mark, out card)
                    is { } refusal)
                    return refusal.Result();
                captured = new Captured(
                    JumpCard.Serialise(card), JumpCard.Best(card), JumpCard.DeriveStatus(card));
                return null;
            },
            (result, prior, actors) =>
                AuditJump(audit, actors, result, height, request.Mark, card, attemptsPerHeight, prior),
            ct);
    }

    /// <summary>Upserts by entry: the first mark to land creates the result.</summary>
    private static async Task<IResult> CaptureByEntryAsync(
        Guid id, Guid eventId, Guid entryId, HttpContext ctx, CarnivalAppDbContext db,
        string notAccepted, string captureNoun, string reason, DateTimeOffset? recordedAtUtc,
        Func<CarnivalEvent, IResult?> configure, CaptureAppend append,
        Action<Result, object?, CaptureActors> auditCapture, CancellationToken ct)
    {
        var resultId = Guid.Parse(ctx.Request.Headers[IdempotencyService.HeaderName].ToString());

        var entry = await db.Entries
            .Include(e => e.CarnivalEvent).ThenInclude(e => e.Carnival)
            .Include(e => e.CarnivalEvent).ThenInclude(e => e.EventDefinition)
            .FirstOrDefaultAsync(e => e.Id == entryId && e.CarnivalEventId == eventId
                                      && e.CarnivalEvent.CarnivalId == id, ct);
        if (entry is null)
            return ApiError.NotFound(ErrorCodes.EntryNotFound, "No entry exists with that id in this event.");

        var carnivalEvent = entry.CarnivalEvent;
        if (CarnivalGuards.RequireOpen(carnivalEvent.Carnival, notAccepted) is { } carnivalClosed)
            return carnivalClosed;
        if (CarnivalGuards.RequireEventOpen(carnivalEvent, notAccepted) is { } eventClosed)
            return eventClosed;
        if (configure(carnivalEvent) is { } misconfigured) return misconfigured;

        var sessionActor = SessionActor.From(ctx);
        var recorder = CapturedActor.Resolve(ctx, sessionActor);
        var actors = new CaptureActors(recorder, sessionActor, CapturedActor.DisplayName(ctx));
        var now = DateTimeOffset.UtcNow;

        return await ApplyAsync(retrying: false);

        // A capture that loses a concurrent create or append is good, so it is re-applied once;
        // the series rules still refuse a mark the winner already recorded.
        async Task<IResult> ApplyAsync(bool retrying)
        {
            var result = await db.Results.FirstOrDefaultAsync(r => r.EntryId == entryId, ct);

            if (result is null)
            {
                if (await db.Results.TagWith(AttemptCreateTag).AnyAsync(r => r.Id == resultId, ct))
                    return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                        "A different result already exists with this submission id.");
                if (append(null, out var created) is { } refusedCreate) return refusedCreate;

                result = new Result
                {
                    Id = resultId,
                    EntryId = entryId,
                    AttemptsJson = created.AttemptsJson,
                    MeasuredValue = created.MeasuredValue,
                    Status = created.Status,
                    LifecycleState = ResultLifecycleState.Verified,
                    CurrentRevision = 1,
                    RecordedBySubject = recorder.Subject,
                    RecordedAtUtc = recordedAtUtc ?? now,
                    CreatedAtUtc = now,
                    DeviceId = NullIfEmpty(ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString()),
                };
                db.Results.Add(result);
                db.ResultRevisions.Add(new ResultRevision
                {
                    Id = Guid.CreateVersion7(),
                    ResultId = result.Id,
                    RevisionNumber = 1,
                    PriorValuesJson = "{}",
                    NewValuesJson = System.Text.Json.JsonSerializer.Serialize(MaterialValues(result)),
                    ActorSubject = recorder.Subject,
                    ActorRole = recorder.Role,
                    AtUtc = now,
                    Reason = reason,
                });
                auditCapture(result, null, actors);
                return await SaveAsync(retrying, Results.Created($"/api/v1/results/{result.Id}", Project(result)));
            }

            if (result.LifecycleState == ResultLifecycleState.Final)
                return ApiError.Conflict(ErrorCodes.ResultFinalised,
                    "This result is part of the finalised record and cannot be corrected. " +
                    "Finalising the carnival published it (ADR-0017).");
            if (result.Status is ResultStatus.Dns or ResultStatus.Dnf or ResultStatus.Dq)
                return ApiError.Conflict(ErrorCodes.ResultStatusBlocksAttempts,
                    $"This entry is recorded {result.Status.ToString().ToUpperInvariant()}, which is not {captureNoun}. " +
                    "Correct the result first if they did compete.");

            if (append(result.AttemptsJson, out var appended) is { } refusal) return refusal;
            var prior = MaterialValues(result);
            result.AttemptsJson = appended.AttemptsJson;
            result.MeasuredValue = appended.MeasuredValue;
            result.Status = appended.Status;
            AppendRevision(db, result, prior, recorder, reason);
            auditCapture(result, prior, actors);
            return await SaveAsync(retrying, Results.Ok(Project(result)));
        }

        async Task<IResult> SaveAsync(bool retrying, IResult success)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The tracker holds edits the database never took.
                db.ChangeTracker.Clear();
                return retrying ? ConcurrencyConflict() : await ApplyAsync(retrying: true);
            }
            return success;
        }
    }

    private static void AuditAttempt(
        AuditService audit, CaptureActors actors, Result result, int ordinal, object? prior) =>
        audit.Record(actors.Recorder, AuditActions.ResultAttempt, nameof(Result), result.Id.ToString(),
            priorValues: prior,
            newValues: new
            {
                ordinal,
                measuredValue = result.MeasuredValue,
                status = result.Status.ToString(),
                lifecycleState = result.LifecycleState.ToString(),
                attemptsJson = result.AttemptsJson,
                recordedBySubject = result.RecordedBySubject,
                capturedActorDisplayName = actors.CapturedName,
                syncSessionSubject = actors.Session.Subject,
                syncSessionRole = actors.Session.Role,
                deviceId = result.DeviceId,
            },
            ruleVersion: ValidationRules.Version);

    private static void AuditJump(
        AuditService audit, CaptureActors actors, Result result, decimal height, string? mark,
        IReadOnlyList<JumpDto> card, int attemptsPerHeight, object? prior) =>
        audit.Record(actors.Recorder, AuditActions.ResultJump, nameof(Result), result.Id.ToString(),
            priorValues: prior,
            newValues: new
            {
                height,
                mark,
                measuredValue = result.MeasuredValue,
                status = result.Status.ToString(),
                failsAtBest = JumpCard.FailsAtBest(card),
                totalFails = JumpCard.CountbackFails(card),
                eliminated = JumpCard.IsEliminated(card, attemptsPerHeight),
                lifecycleState = result.LifecycleState.ToString(),
                attemptsJson = result.AttemptsJson,
                recordedBySubject = result.RecordedBySubject,
                capturedActorDisplayName = actors.CapturedName,
                syncSessionSubject = actors.Session.Subject,
                syncSessionRole = actors.Session.Role,
                deviceId = result.DeviceId,
            },
            ruleVersion: ValidationRules.Version);

    private static async Task<IResult> CorrectResultAsync(
        Guid resultId, CorrectResultRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired, "A reason is required for a correction (§9.3).");
        if (FieldLimits.Refuse(request.Reason, FieldLimits.Reason, "reason") is { } tooLong) return tooLong;

        var result = await db.Results
            .Include(r => r.Entry).ThenInclude(e => e.CarnivalEvent).ThenInclude(e => e.EventDefinition)
            .FirstOrDefaultAsync(r => r.Id == resultId, ct);
        if (result is null) return ResultNotFound();

        if (result.LifecycleState == ResultLifecycleState.Final)
            return ApiError.Conflict(ErrorCodes.ResultFinalised,
                "This result is part of the finalised record and cannot be corrected. " +
                "Finalising the carnival published it (ADR-0017).");
        if (request.ExpectedRevision != result.CurrentRevision)
            return ApiError.Conflict(ErrorCodes.ConcurrencyConflict,
                "Concurrent correction conflict: the result changed since it was read. " +
                "Re-read the result; if two recorders disagree, an administrator decides which stands (ADR-0002).",
                new() { ["currentRevision"] = result.CurrentRevision });

        var carnivalEvent = result.Entry.CarnivalEvent;
        var definition = carnivalEvent.EventDefinition;
        var measureType = definition.MeasureType;
        ResultStatus status;
        decimal? measured = null;
        string? attemptsJson = null;
        if (measureType == MeasureType.Height)
        {
            if (CorrectedCard(request, measureType, JumpCard.ParseHeights(carnivalEvent.HeightsJson),
                    JumpCard.AttemptsPerHeightOf(carnivalEvent),
                    out status, out measured, out attemptsJson) is { } cardError)
                return cardError;
        }
        else if (definition.AttemptCount is { } attemptCount)
        {
            if (request.Jumps is not null) return JumpsNotLaddered();
            if (CorrectedSeries(request, measureType, attemptCount,
                    AttemptSeries.Parse(result.AttemptsJson),
                    out status, out measured, out attemptsJson) is { } seriesError)
                return seriesError;
        }
        else
        {
            if (request.Attempts is not null)
                return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                    "This event records one measure, so a correction cannot carry an attempt series.");
            if (request.Jumps is not null) return JumpsNotLaddered();
            if (ValidateStatus(measureType, request.Status, request.Value, out status) is { } statusError)
                return statusError;
            if (status == ResultStatus.Ok)
            {
                if (!ResultValueParser.TryParse(measureType, request.Value!, out var value, out var parseError))
                    return ApiError.BadRequest(ErrorCodes.ResultValueInvalid, parseError!);
                measured = value;
            }
        }

        var sessionActor = SessionActor.From(ctx);
        var actor = CapturedActor.Resolve(ctx, sessionActor);
        var capturedName = CapturedActor.DisplayName(ctx);
        var prior = MaterialValues(result);
        result.MeasuredValue = measured;
        result.Status = status;
        result.AttemptsJson = attemptsJson;
        AppendRevision(db, result, prior, actor, request.Reason);
        audit.Record(actor, AuditActions.ResultCorrect, nameof(Result), resultId.ToString(),
            priorValues: prior,
            newValues: new
            {
                measuredValue = result.MeasuredValue,
                status = result.Status.ToString(),
                lifecycleState = result.LifecycleState.ToString(),
                attemptsJson = result.AttemptsJson,
                capturedActorDisplayName = capturedName,
                syncSessionSubject = sessionActor.Subject,
                syncSessionRole = sessionActor.Role,
            },
            reason: request.Reason,
            ruleVersion: ValidationRules.Version);
        return await SaveGuardingConcurrencyAsync(db, result, ct);
    }

    /// <summary>
    /// The revision always carries a reason (else the default); the audit records the caller's
    /// reason as given, absent included.
    /// </summary>
    internal static void ApplyTransition(
        CarnivalAppDbContext db, AuditService audit, Result result, ResultLifecycleState target,
        AuditActor actor, string? reason, string defaultReason)
    {
        var prior = MaterialValues(result);
        result.LifecycleState = target;
        AppendRevision(db, result, prior, actor, reason ?? defaultReason);
        audit.Record(actor, TransitionAction(target), nameof(Result), result.Id.ToString(),
            priorValues: prior, newValues: MaterialValues(result), reason: reason);
    }

    /// <summary>Throws rather than defaults, so a new target is never mis-audited.</summary>
    private static string TransitionAction(ResultLifecycleState target) => target switch
    {
        ResultLifecycleState.Final => AuditActions.ResultFinalise,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target,
            "No audit action is defined for this lifecycle target. AuditActions values are a " +
            "database contract — add an explicit mapping rather than reusing an existing action."),
    };

    private static async Task<IResult> SaveGuardingConcurrencyAsync(
        CarnivalAppDbContext db, Result result, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict();
        }
        return Results.Ok(Project(result));
    }

    private static IResult ConcurrencyConflict() => CarnivalGuards.ConcurrencyConflict(
        "Concurrent change conflict: the result was modified by another request. " +
        "Re-read and retry; if two recorders disagree, an administrator decides (ADR-0002).");

    private static IResult ResultNotFound() =>
        ApiError.NotFound(ErrorCodes.ResultNotFound, "No result exists with that id.");

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
