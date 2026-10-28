using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Records;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Carnivals;

public static class CarnivalEndpoints
{
    public static void MapCarnivalEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var carnivals = apiV1.MapGroup("/carnivals");
        carnivals.MapGet("/", ListCarnivalsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<CarnivalDto>>();
        carnivals.MapGet("/{id:guid}", GetCarnivalAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<CarnivalDto>();
        carnivals.MapPost("/", CreateCarnivalAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalDto>(StatusCodes.Status201Created);
        carnivals.MapPut("/{id:guid}", UpdateCarnivalAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalDto>();
        carnivals.MapPost("/{id:guid}/status", ChangeCarnivalStatusAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalDto>();
        carnivals.MapPost("/{id:guid}/apply-template", ApplyTemplateAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<EventTemplateService.ApplicationResult>();
    }

    /// <summary>
    /// <see cref="LaneCount"/>/<see cref="AutoFillLanes"/> are honoured on create
    /// only; after that the settings PUT owns them.
    /// </summary>
    public sealed record CarnivalRequest(
        string? Name, CarnivalType Type, DateOnly Date, string? Venue, string? RuleVersion,
        int? LaneCount, bool? AutoFillLanes);

    public sealed record StatusRequest(string? Status, string? Reason);

    /// <summary>Clients echo no <see cref="Version"/>; it moving flags a stale screen.</summary>
    public sealed record CarnivalDto(
        Guid Id, string Name, CarnivalType Type, DateOnly Date, string? Venue,
        CarnivalStatus Status, string RuleVersion, string OwnerSubject, int Version);

    private static CarnivalDto Project(Carnival c) => new(
        c.Id, c.Name, c.Type, c.Date, c.Venue, c.Status, c.RuleVersion, c.OwnerSubject, c.Version);

    private static async Task<IResult> ListCarnivalsAsync([FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var carnivals = await db.Carnivals.AsNoTracking().OrderByDescending(c => c.Date).ToListAsync(ct);
        return Results.Ok(carnivals.Select(Project));
    }

    private static async Task<IResult> GetCarnivalAsync(Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return carnival is null ? CarnivalGuards.CarnivalNotFound() : Results.Ok(Project(carnival));
    }

    private static async Task<IResult> CreateCarnivalAsync(
        CarnivalRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "name is required.");
        if (request.Date == default)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "date is required.");
        if (CarnivalSettings.ValidateLaneCount(request.LaneCount) is { } badLaneCount)
            return ApiError.BadRequest(ErrorCodes.LaneCountInvalid, badLaneCount);
        if (CarnivalSettings.ValidateAutoFill(request.AutoFillLanes == true, request.LaneCount) is { } badAutoFill)
            return ApiError.BadRequest(ErrorCodes.AutoFillLanesRequiresLaneCount, badAutoFill);

        var actor = SessionActor.From(ctx);
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name.Trim(),
            Type = request.Type,
            Date = request.Date,
            Venue = request.Venue?.Trim(),
            Status = CarnivalStatus.Draft,
            RuleVersion = string.IsNullOrWhiteSpace(request.RuleVersion) ? "v1" : request.RuleVersion.Trim(),
            OwnerSubject = actor.Subject,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        if (request.LaneCount is not null || request.AutoFillLanes is not null)
            carnival.SettingsJson = new CarnivalSettings
            {
                LaneCount = request.LaneCount,
                AutoFillLanes = request.AutoFillLanes == true,
            }.Serialise();
        db.Carnivals.Add(carnival);
        audit.Record(actor, AuditActions.CarnivalCreate, nameof(Carnival), carnival.Id.ToString(),
            newValues: Project(carnival), ruleVersion: carnival.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{carnival.Id}", Project(carnival));
    }

    private static async Task<IResult> UpdateCarnivalAsync(
        Guid id, CarnivalRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireEditable(carnival, "Carnival details cannot be edited") is { } refusal)
            return refusal;
        if (string.IsNullOrWhiteSpace(request.Name))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "name is required.");

        var prior = Project(carnival);
        carnival.Name = request.Name.Trim();
        carnival.Type = request.Type;
        carnival.Date = request.Date == default ? carnival.Date : request.Date;
        carnival.Venue = request.Venue?.Trim();
        if (!string.IsNullOrWhiteSpace(request.RuleVersion)) carnival.RuleVersion = request.RuleVersion.Trim();
        carnival.Version++;
        carnival.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalUpdate, nameof(Carnival), id.ToString(),
            priorValues: prior, newValues: Project(carnival), ruleVersion: carnival.RuleVersion);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "carnival", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnival));
    }

    /// <summary>
    /// Closed → Open exists because capture needs Open and finalisation needs every result:
    /// without it a missed DNS is a dead end. Finalised is terminal.
    /// </summary>
    private static readonly IReadOnlySet<(CarnivalStatus From, CarnivalStatus To)> CarnivalTransitions =
        new HashSet<(CarnivalStatus, CarnivalStatus)>
        {
            (CarnivalStatus.Draft, CarnivalStatus.Open),
            (CarnivalStatus.Open, CarnivalStatus.Closed),
            (CarnivalStatus.Closed, CarnivalStatus.Open),
            (CarnivalStatus.Closed, CarnivalStatus.Finalised),
        };

    private static async Task<IResult> ChangeCarnivalStatusAsync(
        Guid id, StatusRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        if (!Enum.TryParse<CarnivalStatus>(request.Status, ignoreCase: true, out var target))
            return ApiError.BadRequest(ErrorCodes.UnknownStatus, $"Unknown status '{request.Status}'.");
        if (FieldLimits.Refuse(request.Reason, FieldLimits.Reason, "reason") is { } tooLong) return tooLong;
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();

        if (!CarnivalTransitions.Contains((carnival.Status, target)))
            return ApiError.Conflict(ErrorCodes.IllegalCarnivalTransition,
                $"Illegal carnival status transition {carnival.Status} → {target}. " +
                "The lifecycle is Draft → Open → Closed → Finalised, and a Closed carnival " +
                "may be reopened. Finalising publishes the results and cannot be undone.");

        return target == CarnivalStatus.Finalised
            ? await FinaliseCarnivalAsync(carnival, request, ctx, db, audit, ct)
            : await ApplyCarnivalStatusAsync(carnival, target, request, ctx, db, audit, ct);
    }

    private static async Task<IResult> ApplyCarnivalStatusAsync(
        Carnival carnival, CarnivalStatus target, StatusRequest request, HttpContext ctx,
        CarnivalAppDbContext db, AuditService audit, CancellationToken ct)
    {
        var prior = carnival.Status;
        carnival.Status = target;
        // Makes the read-then-write a checked update: two concurrent transitions give one 409.
        carnival.Version++;
        carnival.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalStatus, nameof(Carnival), carnival.Id.ToString(),
            priorValues: new { status = prior.ToString() },
            newValues: new { status = target.ToString() },
            reason: request.Reason);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "carnival", ct) is { } conflict)
            return conflict;
        return Results.Ok(Project(carnival));
    }

    /// <summary>Test interleave point for the finalisation gate's re-check.</summary>
    public const string CarnivalGateRecheckTag = "carnival-finalisation-gate re-check";

    /// <summary>
    /// Results load tracked so their revision tokens ride into the save. No pending-device block:
    /// an offline device and a lost one look the same, so a block would give a lost device a veto.
    /// </summary>
    private static async Task<IResult> FinaliseCarnivalAsync(
        Carnival carnival, StatusRequest request, HttpContext ctx,
        CarnivalAppDbContext db, AuditService audit, CancellationToken ct)
    {
        var id = carnival.Id;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Only the mode decides: a non-scoring event still owes its times. EffectiveScoringMode
        // does not translate to SQL, so it is resolved in memory.
        var scoringModes = await db.CarnivalEvents
            .Where(e => e.CarnivalId == id)
            .Select(e => new { e.Id, e.Tier, e.ScoringMode })
            .ToListAsync(ct);
        var owingEventIds = scoringModes
            .Where(e => (e.ScoringMode ?? (e.Tier == EventTier.Social
                ? EventScoringMode.Social
                : EventScoringMode.Places)) != EventScoringMode.Social)
            .Select(e => e.Id)
            .ToList();

        if (await OwingEntriesAsync(db, owingEventIds, ct) is { } blocked) return blocked;

        // Tracked on purpose: their revision tokens make a concurrent correction collide.
        var results = await db.Results
            .Where(r => r.Entry.CarnivalEvent.CarnivalId == id
                        && r.LifecycleState != ResultLifecycleState.Draft)
            .ToListAsync(ct);
        var actor = SessionActor.From(ctx);
        foreach (var result in results)
            ResultEndpoints.ApplyTransition(db, audit, result, ResultLifecycleState.Final, actor,
                request.Reason, defaultReason: "Carnival finalised");

        var prior = carnival.Status;
        var finalisedAtUtc = DateTimeOffset.UtcNow;
        // In this transaction: if record minting throws, nothing is finalised.
        var recordsSet = await RecordDetection.MintAsync(
            db, audit, carnival, actor, request.Reason, finalisedAtUtc, ct);

        carnival.Status = CarnivalStatus.Finalised;
        carnival.Version++;
        carnival.UpdatedAtUtc = finalisedAtUtc;
        audit.Record(actor, AuditActions.CarnivalStatus, nameof(Carnival), id.ToString(),
            priorValues: new { status = prior.ToString() },
            newValues: new
            {
                status = CarnivalStatus.Finalised.ToString(),
                finalisedResults = results.Count,
                recordsSet,
            },
            reason: request.Reason);

        // Nothing above is written yet, so this sees pre-flip rows and catches a late entry.
        var owingNow = await db.Entries.TagWith(CarnivalGateRecheckTag)
            .CountAsync(e => owingEventIds.Contains(e.CarnivalEventId)
                             && !e.Results.AsQueryable().Any(JumpCard.Recorded), ct);
        if (owingNow > 0)
            return CarnivalGuards.ConcurrencyConflict(
                "Finalisation refused: an entry without a result appeared while the finalisation gate " +
                "was running, so nothing was finalised. Re-read the carnival and finalise again.");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CarnivalGuards.ConcurrencyConflict(
                "Finalisation refused: a result changed while the finalisation gate was running, " +
                "so nothing was finalised. Re-read the carnival and finalise again.");
        }
        await tx.CommitAsync(ct);
        return Results.Ok(Project(carnival));
    }

    /// <summary>Bounded: a large carnival can owe a thousand entries.</summary>
    private const int BlockedSampleLimit = 20;

    private static async Task<IResult?> OwingEntriesAsync(
        CarnivalAppDbContext db, IReadOnlyCollection<Guid> owingEventIds, CancellationToken ct)
    {
        var owing = db.Entries.Where(e => owingEventIds.Contains(e.CarnivalEventId)
                                          && !e.Results.AsQueryable().Any(JumpCard.Recorded));
        var total = await owing.CountAsync(ct);
        if (total == 0) return null;

        var sample = await owing
            .OrderBy(e => e.SnapshotDisplayName)
            .Take(BlockedSampleLimit)
            .Select(e => new { entryId = e.Id, displayName = e.SnapshotDisplayName, eventId = e.CarnivalEventId })
            .ToListAsync(ct);
        return ApiError.Conflict(ErrorCodes.CarnivalFinalisationBlocked,
            $"Carnival cannot be finalised: {total} entr{(total == 1 ? "y" : "ies")} still need a result " +
            "or a non-result status (DNS, DNF, DQ, NM).",
            new()
            {
                ["entriesWithoutResult"] = total,
                ["sample"] = sample,
                ["sampleTruncated"] = total > BlockedSampleLimit,
            });
    }

    public sealed record ApplyTemplateRequest(string? Template);

    /// <summary>Idempotent: re-applying skips whatever already exists.</summary>
    private static async Task<IResult> ApplyTemplateAsync(
        Guid id, ApplyTemplateRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] EventTemplateService templates,
        CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireEditable(carnival, "Events cannot be scheduled") is { } refusal)
            return refusal;

        var resolution = templates.Resolve(request.Template);
        if (resolution.Template is null)
            return ApiError.Status(
                resolution.ErrorStatus,
                resolution.ErrorStatus == StatusCodes.Status400BadRequest
                    ? ErrorCodes.TemplateInvalid
                    : ErrorCodes.TemplateUnavailable,
                resolution.Error!);

        var result = await templates.ApplyAsync(carnival, resolution.Template, SessionActor.From(ctx), ct);
        return Results.Ok(result);
    }
}
