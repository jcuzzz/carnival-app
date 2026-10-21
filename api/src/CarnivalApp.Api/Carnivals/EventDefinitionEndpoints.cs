using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Records;

namespace CarnivalApp.Api.Carnivals;

/// <summary>Definitions are referenced forever, so they are deactivated, never deleted.</summary>
public static class EventDefinitionEndpoints
{
    public static void MapEventDefinitionEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var definitions = apiV1.MapGroup("/event-definitions");
        definitions.MapGet("/", ListDefinitionsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<EventDefinitionDto>>()
            .DocumentsBooleanQuery("includeInactive");
        definitions.MapPost("/", CreateDefinitionAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<EventDefinitionDto>(StatusCodes.Status201Created);
        definitions.MapPut("/{id:guid}", UpdateDefinitionAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<EventDefinitionDto>();
        definitions.MapDelete("/{id:guid}", DeactivateDefinitionAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces(StatusCodes.Status204NoContent);
    }

    public sealed record DefinitionRequest(
        string? Code, string? Name, string? Sport, MeasureType MeasureType, string? Units,
        string? EligibilityJson, string? RecordCategoriesJson,
        /// <summary>A JSON string array.</summary>
        string? TagsJson,
        /// <summary>
        /// Unlike the blobs above, absent means NULL on update: the editors always send this
        /// scalar, so "leave unchanged" would make clearing it impossible.
        /// </summary>
        int? AttemptCount = null,
        /// <summary>Absent means NULL on update, as for <see cref="AttemptCount"/>.</summary>
        LadderRequest? Ladder = null,
        /// <summary>Absent reads as 3, and means NULL on update.</summary>
        int? AttemptsPerHeight = null);

    /// <summary>In metres.</summary>
    public sealed record LadderRequest(decimal Start, decimal Step);

    private static IResult DefinitionNotFound() =>
        ApiError.NotFound(ErrorCodes.EventDefinitionNotFound, "No event definition exists with that id.");

    private static IResult? LadderRule(DefinitionRequest request, EventFormat format)
    {
        if (request.Ladder is not { } ladder) return null;
        if (request.MeasureType != MeasureType.Height)
            return ApiError.BadRequest(ErrorCodes.LadderRequiresHeightMeasure,
                "A heights ladder belongs to a Height event: the bar rises in metres.");
        if (format != EventFormat.Individual)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A team event is one performance by a house, so it cannot set a heights ladder.");
        if (!JumpCard.IsHeight(ladder.Start))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"ladder.start must be a whole centimetre between {JumpCard.Format(JumpCard.MinHeight)} " +
                $"and {JumpCard.Format(JumpCard.MaxHeight)} metres.");
        return JumpCard.IsStep(ladder.Step)
            ? null
            : ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"ladder.step must be a whole centimetre between {JumpCard.Format(JumpCard.MinHeight)} " +
                $"and {JumpCard.Format(JumpCard.MaxStep)} metres.");
    }

    private static IResult? AttemptsPerHeightRule(DefinitionRequest request)
    {
        if (request.AttemptsPerHeight is not { } perHeight) return null;
        if (request.MeasureType != MeasureType.Height)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "Attempts per height belong to a Height event: the bar rises between them.");
        return JumpCard.IsAttemptsPerHeight(perHeight)
            ? null
            : ApiError.BadRequest(ErrorCodes.ValidationFailed, "attemptsPerHeight must be 2 or 3.");
    }

    /// <param name="storedOnAHeight">A legacy count on a Height definition, which may be re-sent
    /// unchanged.</param>
    private static IResult? AttemptCountRule(
        DefinitionRequest request, EventFormat format, int? storedOnAHeight = null)
    {
        if (request.AttemptCount is not { } attempts) return null;
        if (attempts is < 1 or > 6)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "attemptCount must be between 1 and 6, or absent for a single measure.");
        if (request.MeasureType == MeasureType.Time)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A Time event is captured as one measure, so it cannot set an attempt count.");
        if (request.MeasureType == MeasureType.Height && attempts != storedOnAHeight)
            return ApiError.BadRequest(ErrorCodes.LadderExcludesAttempts,
                "A Height event is contested on the bar, so it cannot set an attempt count.");
        return format == EventFormat.Individual
            ? null
            : ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "A team event is one performance by a house, so it cannot set an attempt count.");
    }

    /// <summary>
    /// Checked on write because the read path is tolerant and would silently swallow a blob the
    /// client believes it saved.
    /// </summary>
    private static IResult? ConfigurationBlobs(DefinitionRequest request) => JsonBlob.Refuse(
            (request.EligibilityJson, "eligibilityJson", JsonBlobShape.Object),
            (request.RecordCategoriesJson, "recordCategoriesJson", JsonBlobShape.Object),
            (request.TagsJson, "tagsJson", JsonBlobShape.StringArray))
        ?? (RecordCategories.OpenAgeRefusal(request.RecordCategoriesJson) is { } open
            ? ApiError.BadRequest(ErrorCodes.ValidationFailed, open)
            : null);

    private static IResult? TooLong(DefinitionRequest request) =>
        FieldLimits.Refuse(request.Code, FieldLimits.Code, "code")
        ?? FieldLimits.Refuse(request.Name, FieldLimits.Name, "name")
        ?? FieldLimits.Refuse(request.Sport, FieldLimits.Sport, "sport")
        ?? FieldLimits.Refuse(request.Units, FieldLimits.Units, "units");

    /// <summary>
    /// Exposes every stored field, including ones the request cannot set, so a client that GETs and
    /// PUTs back sees everything it is about to overwrite.
    /// </summary>
    public sealed record EventDefinitionDto(
        Guid Id, string Code, string Name, string Sport,
        MeasureType MeasureType,
        int? AttemptCount,
        LadderDto? Ladder,
        /// <summary>Null on every non-Height measure; stored null reads as 3.</summary>
        int? AttemptsPerHeight,
        string Units, bool IsActive,
        string? EligibilityJson, string? RecordCategoriesJson,
        EventFormat Format, int? TeamSize, string? LegsJson, string? TagsJson, decimal ScoringWeight,
        EventDefinitionUsageDto Usage);

    /// <summary>
    /// What a rename would relabel; <paramref name="Results"/> lets the editor grey frozen fields.
    /// </summary>
    public sealed record EventDefinitionUsageDto(
        int Carnivals, int Divisions, int SchoolRecords, int Results)
    {
        public static readonly EventDefinitionUsageDto None = new(0, 0, 0, 0);
    }

    internal static EventDefinitionDto Project(EventDefinition d, EventDefinitionUsageDto usage) => new(
        d.Id, d.Code, d.Name, d.Sport,
        d.MeasureType, d.AttemptCount, JumpCard.ParseLadder(d.LadderJson),
        d.MeasureType == MeasureType.Height
            ? d.AttemptsPerHeight ?? JumpCard.DefaultAttemptsPerHeight
            : null,
        d.Units, d.IsActive,
        d.EligibilityJson, d.RecordCategoriesJson,
        d.Format, d.TeamSize, d.LegsJson, d.TagsJson, d.ScoringWeight, usage);

    /// <summary>
    /// Folded in memory rather than COUNT(DISTINCT): plain GROUP BY translates identically on
    /// SQLite and PostgreSQL.
    /// </summary>
    internal static async Task<Dictionary<Guid, EventDefinitionUsageDto>> UsageAsync(
        SportsDbContext db, Guid? definitionId, CancellationToken ct)
    {
        var divisions = await db.CarnivalEvents.AsNoTracking()
            .Where(e => definitionId == null || e.EventDefinitionId == definitionId)
            .GroupBy(e => new { e.EventDefinitionId, e.CarnivalId })
            .Select(g => new { g.Key.EventDefinitionId, Divisions = g.Count() })
            .ToListAsync(ct);
        var records = await db.SchoolRecords.AsNoTracking()
            .Where(r => definitionId == null || r.EventDefinitionId == definitionId)
            .GroupBy(r => r.EventDefinitionId)
            .Select(g => new { EventDefinitionId = g.Key, Records = g.Count() })
            .ToListAsync(ct);
        var results = await db.Results.AsNoTracking()
            .Where(r => definitionId == null
                || r.Entry.CarnivalEvent.EventDefinitionId == definitionId)
            .GroupBy(r => r.Entry.CarnivalEvent.EventDefinitionId)
            .Select(g => new { EventDefinitionId = g.Key, Results = g.Count() })
            .ToListAsync(ct);

        var usage = divisions
            .GroupBy(d => d.EventDefinitionId)
            .ToDictionary(g => g.Key,
                g => new EventDefinitionUsageDto(g.Count(), g.Sum(d => d.Divisions), 0, 0));
        foreach (var row in records)
            usage[row.EventDefinitionId] = (usage.TryGetValue(row.EventDefinitionId, out var counted)
                ? counted
                : EventDefinitionUsageDto.None) with { SchoolRecords = row.Records };
        foreach (var row in results)
            usage[row.EventDefinitionId] = (usage.TryGetValue(row.EventDefinitionId, out var counted)
                ? counted
                : EventDefinitionUsageDto.None) with { Results = row.Results };
        return usage;
    }

    /// <summary>
    /// A string, not a bool: a bool that fails to bind (<c>?includeInactive=</c>) can only reach
    /// the exception handler, which answers a retryable 500.
    /// </summary>
    private static async Task<IResult> ListDefinitionsAsync(
        [FromServices] SportsDbContext db, CancellationToken ct, [FromQuery] string? includeInactive = null)
    {
        var withInactive = false;
        if (includeInactive is not null && !bool.TryParse(includeInactive, out withInactive))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "includeInactive must be true or false.");
        var definitions = await db.EventDefinitions.AsNoTracking()
            .Where(d => withInactive || d.IsActive)
            .OrderBy(d => d.Sport).ThenBy(d => d.Code)
            .ToListAsync(ct);
        var usage = await UsageAsync(db, null, ct);
        return Results.Ok(definitions.Select(d =>
            Project(d, usage.GetValueOrDefault(d.Id, EventDefinitionUsageDto.None))));
    }

    private static async Task<IResult> CreateDefinitionAsync(
        DefinitionRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Sport)
            || string.IsNullOrWhiteSpace(request.Units))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "code, name, sport and units are required.");
        if (TooLong(request) is { } overlong) return overlong;
        if (ConfigurationBlobs(request) is { } malformed) return malformed;
        // The request cannot set Format, so a hand-made definition is Individual.
        if (AttemptCountRule(request, EventFormat.Individual) is { } badAttempts) return badAttempts;
        if (LadderRule(request, EventFormat.Individual) is { } badLadder) return badLadder;
        if (AttemptsPerHeightRule(request) is { } badPerHeight) return badPerHeight;
        if (await db.EventDefinitions.AnyAsync(d => d.Code == request.Code, ct))
            return ApiError.Conflict(ErrorCodes.EventDefinitionCodeExists,
                $"An event definition with code '{request.Code}' already exists.");

        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Sport = request.Sport.Trim(),
            MeasureType = request.MeasureType,
            AttemptCount = request.AttemptCount,
            LadderJson = LadderJsonOf(request),
            AttemptsPerHeight = request.AttemptsPerHeight,
            Units = request.Units.Trim(),
            EligibilityJson = request.EligibilityJson,
            RecordCategoriesJson = request.RecordCategoriesJson,
            TagsJson = request.TagsJson,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.EventDefinitions.Add(definition);
        var projected = Project(definition, EventDefinitionUsageDto.None);
        audit.Record(SessionActor.From(ctx), AuditActions.EventDefinitionCreate, nameof(EventDefinition),
            definition.Id.ToString(), newValues: projected);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/event-definitions/{definition.Id}", projected);
    }

    private static async Task<IResult> UpdateDefinitionAsync(
        Guid id, DefinitionRequest request, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        // Guards other catalogue writers; a Read Committed result capture can still slip in.
        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var definition = await db.EventDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (definition is null) return DefinitionNotFound();
        if (string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Sport)
            || string.IsNullOrWhiteSpace(request.Units))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "code, name, sport and units are required.");
        if (TooLong(request) is { } overlong) return overlong;
        if (ConfigurationBlobs(request) is { } malformed) return malformed;
        var storedOnAHeight = definition.MeasureType == MeasureType.Height
            ? definition.AttemptCount
            : null;
        if (AttemptCountRule(request, definition.Format, storedOnAHeight) is { } badAttempts)
            return badAttempts;
        if (LadderRule(request, definition.Format) is { } badLadder) return badLadder;
        if (AttemptsPerHeightRule(request) is { } badPerHeight) return badPerHeight;
        var usage = (await UsageAsync(db, id, ct)).GetValueOrDefault(id, EventDefinitionUsageDto.None);
        // Before the count: a type change clears the count, so the type is the actionable refusal.
        if (MeasureFrozen(usage.Results, definition, request) is { } frozenType) return frozenType;
        if (AttemptCountFrozen(usage.Results > 0, definition, request) is { } frozen) return frozen;
        if (UnitsFrozen(usage.Results, definition, request) is { } frozenUnits) return frozenUnits;
        if (RecordCategoriesFrozen(usage.SchoolRecords, definition, request) is { } frozenCategories)
            return frozenCategories;
        if (await db.EventDefinitions.AnyAsync(d => d.Code == request.Code && d.Id != id, ct))
            return ApiError.Conflict(ErrorCodes.EventDefinitionCodeExists,
                $"An event definition with code '{request.Code}' already exists.");

        var prior = Project(definition, usage);
        definition.Code = request.Code.Trim();
        definition.Name = request.Name.Trim();
        definition.Sport = request.Sport.Trim();
        definition.MeasureType = request.MeasureType;
        definition.AttemptCount = request.AttemptCount;
        // Pre-fill only: a division keeps every height it has contested.
        definition.LadderJson = LadderJsonOf(request);
        // A default for divisions created from now on; none already scheduled moves.
        definition.AttemptsPerHeight = request.AttemptsPerHeight;
        definition.Units = request.Units.Trim();
        // Absent means unchanged, so a client that cannot express a template-authored blob never
        // wipes it.
        if (request.EligibilityJson is not null) definition.EligibilityJson = request.EligibilityJson;
        if (request.RecordCategoriesJson is not null) definition.RecordCategoriesJson = request.RecordCategoriesJson;
        if (request.TagsJson is not null) definition.TagsJson = request.TagsJson;
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.EventDefinitionUpdate, nameof(EventDefinition), id.ToString(),
            priorValues: prior, newValues: Project(definition, usage));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(Project(definition, usage));
    }

    /// <summary>
    /// Re-sending the same value passes: the editors always send it, so a rename would otherwise be
    /// refused.
    /// </summary>
    private static IResult? AttemptCountFrozen(
        bool hasResults, EventDefinition definition, DefinitionRequest request) =>
        hasResults && definition.AttemptCount != request.AttemptCount
            ? ApiError.Conflict(ErrorCodes.AttemptCountFrozen,
                "Results are recorded against this event, so the attempt count cannot change. " +
                "A series taken over " +
                $"{definition.AttemptCount?.ToString() ?? "a single measure"} cannot be re-read as " +
                $"{request.AttemptCount?.ToString() ?? "a single measure"}.")
            : null;

    private static IResult? MeasureFrozen(
        int resultCount, EventDefinition definition, DefinitionRequest request) =>
        resultCount > 0 && definition.MeasureType != request.MeasureType
            ? ApiError.Conflict(ErrorCodes.MeasureFrozen,
                "The event type is frozen once results exist; " +
                $"{resultCount} {(resultCount == 1 ? "result is" : "results are")} recorded.")
            : null;

    private static IResult? UnitsFrozen(
        int resultCount, EventDefinition definition, DefinitionRequest request) =>
        resultCount > 0 && !string.Equals(definition.Units, request.Units!.Trim(), StringComparison.Ordinal)
            ? ApiError.Conflict(ErrorCodes.MeasureFrozen,
                "The units are frozen once results exist; " +
                $"{resultCount} {(resultCount == 1 ? "result is" : "results are")} recorded in " +
                $"'{definition.Units}'.")
            : null;

    // Compared as the categories they mean, so a re-serialised blob is not a change.
    private static IResult? RecordCategoriesFrozen(
        int recordCount, EventDefinition definition, DefinitionRequest request)
    {
        if (recordCount == 0 || request.RecordCategoriesJson is null) return null;
        var stored = RecordCategories.Parse(definition.RecordCategoriesJson);
        var requested = RecordCategories.Parse(request.RecordCategoriesJson);
        var same = stored is null ? requested is null : requested is not null && stored.SetEquals(requested);
        return same
            ? null
            : ApiError.Conflict(ErrorCodes.RecordCategoriesFrozen,
                "The record book cites this event, so its record categories are frozen; " +
                $"{recordCount} school {(recordCount == 1 ? "record cites" : "records cite")} it.");
    }

    private static string? LadderJsonOf(DefinitionRequest request) =>
        request.Ladder is { } ladder
            ? JumpCard.SerialiseLadder(new LadderDto(ladder.Start, ladder.Step))
            : null;

    private static async Task<IResult> DeactivateDefinitionAsync(
        Guid id, HttpContext ctx,
        [FromServices] SportsDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var definition = await db.EventDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (definition is null) return DefinitionNotFound();
        if (!definition.IsActive) return Results.NoContent();

        definition.IsActive = false;
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.EventDefinitionDeactivate, nameof(EventDefinition),
            id.ToString(),
            priorValues: new { isActive = true }, newValues: new { isActive = false });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
