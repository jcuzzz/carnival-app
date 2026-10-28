using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Scoring;

public static class AdjustmentEndpoints
{
    public static void MapAdjustmentEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var carnival = apiV1.MapGroup("/carnivals/{id:guid}");
        carnival.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<CarnivalSettingsDto>();
        carnival.MapPut("/settings", UpdateSettingsAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<CarnivalSettingsDto>();

        carnival.MapGet("/adjustments", ListAdjustmentsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<IReadOnlyList<AdjustmentDto>>();
        carnival.MapPost("/adjustments", CreateAdjustmentAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<AdjustmentDto>(StatusCodes.Status201Created);
        carnival.MapPost("/adjustments/{adjustmentId:guid}/void", VoidAdjustmentAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<AdjustmentDto>();
    }

    public sealed record ScoringRulesDto(
        string Version, IReadOnlyList<int> PlacePoints, int ParticipationFloor,
        int DnfPoints, int DnsPoints, int DqPoints,
        int SocialPerEntryPoints);

    // LaneCount null means no limit, never zero. Divisions null means no profile yet, never a
    // carnival that contests nothing. RosterAgeGroups, RosterYear and DivisionsDefault are never
    // stored; RosterYear is null when no roster is promoted.
    public sealed record CarnivalSettingsDto(
        ScoringRulesDto Scoring,
        IReadOnlyList<string> AdjustmentCategories,
        int? LaneCount,
        bool AutoFillLanes,
        DivisionProfileDto? Divisions,
        IReadOnlyList<string> RosterAgeGroups,
        DivisionProfileDto? DivisionsDefault,
        int? RosterYear);

    private sealed record RosterFacts(IReadOnlyList<string> AgeGroups, int? Year);

    // Leaves out the roster facts: an auditor would read their defaults beside a settings change as
    // "the roster was empty".
    private sealed record AuditedSettingsDto(
        ScoringRulesDto Scoring,
        IReadOnlyList<string> AdjustmentCategories,
        int? LaneCount,
        bool AutoFillLanes,
        DivisionProfileDto? Divisions);

    private static AuditedSettingsDto ProjectForAudit(
        CarnivalSettings s, DivisionProfile? divisions) => new(
        new ScoringRulesDto(
            s.Scoring.Version,
            s.Scoring.PlacePoints,
            s.Scoring.ParticipationFloor,
            s.Scoring.DnfPoints,
            s.Scoring.DnsPoints,
            s.Scoring.DqPoints,
            s.Scoring.SocialPerEntryPoints),
        s.AdjustmentCategories,
        s.LaneCount,
        s.AutoFillLanes,
        divisions?.ToDto());

    private static CarnivalSettingsDto ProjectSettings(
        CarnivalSettings s, DivisionProfile? divisions, RosterFacts roster)
    {
        var audited = ProjectForAudit(s, divisions);
        return new(
            audited.Scoring,
            audited.AdjustmentCategories,
            audited.LaneCount,
            audited.AutoFillLanes,
            audited.Divisions,
            roster.AgeGroups,
            audited.Divisions is null && roster.AgeGroups.Count > 0
                ? DivisionProfile.FromRoster(roster.AgeGroups)?.ToDto()
                : null,
            roster.Year);
    }

    // Drops codes no division may carry (a birthdate past the cut-off derives "-12"), so the editor
    // cannot offer an age the PUT would refuse.
    private static async Task<RosterFacts> ReadRosterAsync(CarnivalAppDbContext db, CancellationToken ct)
    {
        var groups = await db.RosterSnapshots.AsNoTracking()
            .Where(s => s.IsCurrent && s.Student.IsActive)
            .GroupBy(s => new { s.CompetitionAgeGroup, s.SchoolYear })
            .Select(g => new { g.Key.CompetitionAgeGroup, g.Key.SchoolYear, Count = g.Count() })
            .ToListAsync(ct);
        // A student missing from the latest file keeps an older snapshot, so the roster's year is
        // the one most of it carries.
        var year = groups
            .GroupBy(g => g.SchoolYear)
            .OrderByDescending(y => y.Sum(g => g.Count))
            .ThenByDescending(y => y.Key)
            .Select(y => (int?)y.Key)
            .FirstOrDefault();
        return new RosterFacts(
        [
            .. groups
                .Select(g => g.CompetitionAgeGroup)
                .Distinct()
                .Where(c => !string.IsNullOrWhiteSpace(c) && AgeScope.Validate(c) is null)
                .OrderBy(AgeScope.Order)
                .ThenBy(c => c, StringComparer.Ordinal),
        ], year);
    }

    private static async Task<DivisionProfile?> ReadProfileAsync(
        CarnivalAppDbContext db, Guid carnivalId, CancellationToken ct) =>
        DivisionProfile.FromRows(await db.CarnivalDivisions.AsNoTracking()
            .Where(d => d.CarnivalId == carnivalId)
            .ToListAsync(ct));

    private static async Task<IResult> GetSettingsAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        return Results.Ok(ProjectSettings(
            CarnivalSettings.Parse(carnival.SettingsJson), await ReadProfileAsync(db, id, ct),
            await ReadRosterAsync(db, ct)));
    }

    public sealed record ScoringSettingsRequest(
        List<int>? PlacePoints, int? ParticipationFloor, int? DnfPoints, int? DnsPoints, int? DqPoints,
        int? SocialPerEntryPoints);

    // Each field is an override: absent leaves the stored value alone.
    public sealed record SettingsRequest(
        ScoringSettingsRequest? Scoring, List<string>? AdjustmentCategories, int? LaneCount,
        bool? AutoFillLanes, DivisionProfileDto? Divisions);

    private static async Task<IResult> UpdateSettingsAsync(
        Guid id, SettingsRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireNotFinalised(carnival, "Scoring settings cannot be changed") is { } refusal)
            return refusal;

        var current = CarnivalSettings.Parse(carnival.SettingsJson);
        var s = request.Scoring;
        // The same bounds Parse enforces on read, so nothing accepted here is discarded later.
        if (s is not null && ScoringRules.Validate(
                s.PlacePoints, s.ParticipationFloor, s.DnfPoints, s.DnsPoints, s.DqPoints,
                s.SocialPerEntryPoints) is { } invalid)
            return ApiError.BadRequest(ErrorCodes.ScoringRulesInvalid, invalid);
        if (CarnivalSettings.ValidateLaneCount(request.LaneCount) is { } badLaneCount)
            return ApiError.BadRequest(ErrorCodes.LaneCountInvalid, badLaneCount);
        // Shrinking under an assigned lane would leave entries no marshal could re-save; moving a
        // swimmer is a marshalling act with its own audit row, so refuse rather than truncate.
        if (request.LaneCount is int submitted)
        {
            var highest = await db.Entries
                .Where(e => e.Race != null && e.Race.CarnivalId == id && e.Lane != null)
                .MaxAsync(e => (int?)e.Lane, ct);
            if (highest is int inUse && submitted < inUse)
                return ApiError.BadRequest(ErrorCodes.LaneCountInvalid,
                    $"laneCount cannot be {submitted}: lane {inUse} is already assigned in this carnival. "
                    + "Move that entry to a lower lane first.");
        }
        // Validated on the resulting pair, so the checkbox and the lane count may arrive together.
        var laneCount = request.LaneCount ?? current.LaneCount;
        var autoFillLanes = request.AutoFillLanes ?? current.AutoFillLanes;
        if (CarnivalSettings.ValidateAutoFill(autoFillLanes, laneCount) is { } badAutoFill)
            return ApiError.BadRequest(ErrorCodes.AutoFillLanesRequiresLaneCount, badAutoFill);
        if (DivisionProfile.Validate(request.Divisions) is { } badProfile)
            return ApiError.BadRequest(badProfile.Code, badProfile.Detail);

        var scoring = s is null
            ? current.Scoring
            : new ScoringRules
            {
                Version = $"custom-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
                PlacePoints = s.PlacePoints is { Count: > 0 } ? s.PlacePoints : current.Scoring.PlacePoints,
                ParticipationFloor = s.ParticipationFloor ?? current.Scoring.ParticipationFloor,
                DnfPoints = s.DnfPoints ?? current.Scoring.DnfPoints,
                DnsPoints = s.DnsPoints ?? current.Scoring.DnsPoints,
                DqPoints = s.DqPoints ?? current.Scoring.DqPoints,
                SocialPerEntryPoints = s.SocialPerEntryPoints ?? current.Scoring.SocialPerEntryPoints,
            };
        var categories = NormaliseCategories(request.AdjustmentCategories) ?? current.AdjustmentCategories;
        CarnivalSettings Build(JsonElement? legacyDivisions) => new()
        {
            Scoring = scoring,
            AdjustmentCategories = categories,
            LaneCount = laneCount,
            AutoFillLanes = autoFillLanes,
            LegacyDivisions = legacyDivisions,
        };

        var updated = Build(current.LegacyDivisions);
        var serialised = updated.Serialise();
        // The legacy divisions key is the one occupant no write can shrink: drop it, don't refuse
        // every write. The rows are the truth; MoveDivisionProfileToItsOwnTable.Down rebuilds it.
        if (serialised.Length > CarnivalSettings.MaxSettingsJsonLength && updated.LegacyDivisions is not null)
        {
            updated = Build(null);
            serialised = updated.Serialise();
        }
        if (serialised.Length > CarnivalSettings.MaxSettingsJsonLength)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"The carnival's settings would exceed {CarnivalSettings.MaxSettingsJsonLength} characters; " +
                "shorten the adjustment category list.");

        // Tracked, not ExecuteDelete: a concurrency conflict must leave the old profile in place.
        var priorRows = await db.CarnivalDivisions.Where(d => d.CarnivalId == id).ToListAsync(ct);
        var priorProfile = DivisionProfile.FromRows(priorRows);
        var updatedProfile = priorProfile;
        if (request.Divisions is not null)
        {
            updatedProfile = DivisionProfile.From(request.Divisions);
            db.CarnivalDivisions.RemoveRange(priorRows);
            db.CarnivalDivisions.AddRange(updatedProfile.ToRows(id));
        }

        var prior = ProjectForAudit(current, priorProfile);
        carnival.SettingsJson = serialised;
        carnival.RuleVersion = updated.Scoring.Version;
        // Settings are read-modify-written, so concurrent editors would otherwise lose one's rules.
        carnival.Version++;
        carnival.UpdatedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(SessionActor.From(ctx), AuditActions.CarnivalSettings, nameof(Carnival), id.ToString(),
            priorValues: prior, newValues: ProjectForAudit(updated, updatedProfile),
            ruleVersion: updated.Scoring.Version);
        if (await CarnivalGuards.SaveGuardingConcurrencyAsync(db, "carnival", ct) is { } conflict)
            return conflict;
        return Results.Ok(ProjectSettings(
            updated, updatedProfile, await ReadRosterAsync(db, ct)));
    }

    private static List<string>? NormaliseCategories(List<string>? categories)
    {
        if (categories is null) return null;
        var cleaned = categories
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return cleaned.Count > 0 ? cleaned : null;
    }

    public sealed record AdjustmentDto(
        Guid Id, Guid CarnivalId, string HouseCode, int Points, string Category, string Reason,
        string RecordedBySubject, DateTimeOffset RecordedAtUtc, bool Voided, string? VoidReason);

    private static AdjustmentDto Project(HousePointsAdjustment a) => new(
        a.Id, a.CarnivalId, a.HouseCode, a.Points, a.Category, a.Reason,
        a.RecordedBySubject, a.RecordedAtUtc, a.Voided, a.VoidReason);

    private static async Task<IResult> ListAdjustmentsAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct)) return CarnivalGuards.CarnivalNotFound();
        var adjustments = await db.HousePointsAdjustments.AsNoTracking()
            .Where(a => a.CarnivalId == id)
            .ToListAsync(ct);
        // In-memory order: SQLite (dev loop) can't ORDER BY DateTimeOffset.
        return Results.Ok(adjustments.OrderByDescending(a => a.RecordedAtUtc).Select(Project));
    }

    public sealed record AdjustmentRequest(string? HouseCode, int Points, string? Category, string? Reason);

    private static Task<IResult> CreateAdjustmentAsync(
        Guid id, AdjustmentRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit,
        [FromServices] IdempotencyService idempotency, [FromServices] SchoolSettingsProvider school,
        CancellationToken ct) =>
        OptionalIdempotency.ExecuteAsync(ctx, idempotency,
            (clientId, innerCt) =>
                CreateAdjustmentCoreAsync(id, clientId, request, ctx, db, audit, school, innerCt),
            ct);

    private static async Task<IResult> CreateAdjustmentCoreAsync(
        Guid id, Guid? clientId, AdjustmentRequest request, HttpContext ctx,
        CarnivalAppDbContext db, AuditService audit, SchoolSettingsProvider school, CancellationToken ct)
    {
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (CarnivalGuards.RequireNotFinalisedForCapture(carnival, "House points cannot be adjusted") is { } refusal)
            return refusal;

        var typedHouse = request.HouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(typedHouse))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "houseCode is required.");
        if (FieldLimits.Refuse(typedHouse, FieldLimits.HouseCode, "houseCode") is { } longHouse)
            return longHouse;
        var (houseCode, unknownHouse) = await ConfiguredHouse.ResolveAsync(school, typedHouse, ct);
        if (houseCode is null) return unknownHouse!;
        var category = request.Category?.Trim();
        if (string.IsNullOrWhiteSpace(category))
            return ApiError.BadRequest(ErrorCodes.ValidationFailed, "category is required.");
        if (FieldLimits.Refuse(category, FieldLimits.Category, "category") is { } longCategory) return longCategory;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired,
                "A reason is required for a house-point adjustment (ADR-0005).");
        if (FieldLimits.Refuse(request.Reason, FieldLimits.Reason, "reason") is { } longReason) return longReason;
        if (request.Points == 0)
            return ApiError.BadRequest(ErrorCodes.AdjustmentPointsInvalid,
                "points must be non-zero (a positive award or a negative deduction).");

        var settings = CarnivalSettings.Parse(carnival.SettingsJson);
        if (!settings.AdjustmentCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            return ApiError.BadRequest(ErrorCodes.AdjustmentCategoryUnknown,
                $"'{category}' is not a configured adjustment category. " +
                $"Configured: {string.Join(", ", settings.AdjustmentCategories)}.",
                new() { ["configuredCategories"] = settings.AdjustmentCategories });

        if (clientId is Guid provided && await db.HousePointsAdjustments.AnyAsync(a => a.Id == provided, ct))
            return ApiError.Conflict(ErrorCodes.SubmissionIdConflict,
                "A different adjustment already exists with this submission id.");

        // Capture-time actor: a queued award drained after a device handover is attributed to
        // whoever entered it.
        var actor = CapturedActor.Resolve(ctx, SessionActor.From(ctx));
        var adjustment = new HousePointsAdjustment
        {
            Id = clientId ?? Guid.CreateVersion7(),
            CarnivalId = id,
            HouseCode = houseCode,
            Points = request.Points,
            Category = category,
            Reason = request.Reason.Trim(),
            RecordedBySubject = actor.Subject,
            RecordedByRole = actor.Role,
            RecordedAtUtc = DateTimeOffset.UtcNow,
        };
        db.HousePointsAdjustments.Add(adjustment);
        audit.Record(actor, AuditActions.HouseAdjustmentCreate, nameof(HousePointsAdjustment), adjustment.Id.ToString(),
            newValues: Project(adjustment), reason: adjustment.Reason, ruleVersion: carnival.RuleVersion);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/carnivals/{id}/adjustments/{adjustment.Id}", Project(adjustment));
    }

    public sealed record VoidRequest(string? Reason);

    private static async Task<IResult> VoidAdjustmentAsync(
        Guid id, Guid adjustmentId, VoidRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, [FromServices] AuditService audit, CancellationToken ct)
    {
        var adjustment = await db.HousePointsAdjustments
            .FirstOrDefaultAsync(a => a.Id == adjustmentId && a.CarnivalId == id, ct);
        if (adjustment is null)
            return ApiError.NotFound(ErrorCodes.AdjustmentNotFound,
                "No house-point adjustment exists with that id in this carnival.");
        var carnival = await CarnivalGuards.FindCarnivalAsync(db, id, ct);
        if (carnival is not null
            && CarnivalGuards.RequireNotFinalisedForCapture(carnival, "Adjustments cannot be voided") is { } refusal)
            return refusal;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiError.BadRequest(ErrorCodes.ReasonRequired,
                "A reason is required to void an adjustment (ADR-0005).");
        if (FieldLimits.Refuse(request.Reason, FieldLimits.Reason, "reason") is { } longReason) return longReason;
        if (adjustment.Voided) return Results.Ok(Project(adjustment));

        var actor = SessionActor.From(ctx);
        adjustment.Voided = true;
        adjustment.VoidedBySubject = actor.Subject;
        adjustment.VoidReason = request.Reason.Trim();
        adjustment.VoidedAtUtc = DateTimeOffset.UtcNow;
        audit.Record(actor, AuditActions.HouseAdjustmentVoid, nameof(HousePointsAdjustment), adjustmentId.ToString(),
            priorValues: new { voided = false },
            newValues: new { voided = true }, reason: adjustment.VoidReason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Project(adjustment));
    }
}
