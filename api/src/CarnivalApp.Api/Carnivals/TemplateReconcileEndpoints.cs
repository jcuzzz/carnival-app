using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Carnivals;

public sealed record TemplateReconcileRequest(
    string? DiffHash, string? Reason, List<string>? ExcludeCodes);

/// <summary>
/// Reconcile is school:administer because catalogue definitions are shared by every carnival past
/// and present.
/// </summary>
public static class TemplateReconcileEndpoints
{
    public const int MaxReasonLength = 500;

    public static void MapTemplateReconcileEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var group = apiV1.MapGroup("/admin/event-templates");

        // The role that may apply a template must be able to list them.
        group.MapGet("", List)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<IReadOnlyList<EventTemplateService.TemplateSummary>>();
        group.MapGet("/{templateId}/reconcile", PreviewAsync)
            .RequireAuthorization(SchoolAuthorization.AdministerSchoolPolicy)
            .Produces<TemplateDiff>();
        group.MapPost("/{templateId}/reconcile", ApplyAsync)
            .RequireAuthorization(SchoolAuthorization.AdministerSchoolPolicy)
            .Produces<TemplateReconcileOutcome>();
    }

    private static IResult List([FromServices] EventTemplateService templates) =>
        Results.Ok(templates.List());

    private static async Task<IResult> PreviewAsync(
        string templateId,
        [FromServices] EventTemplateService templates,
        [FromServices] TemplateReconciler reconciler,
        CancellationToken ct)
    {
        var lookup = templates.Lookup(templateId);
        if (lookup.Template is null)
            return ApiError.Status(lookup.Status, lookup.ErrorCode!, lookup.Detail!);
        var plan = await reconciler.PlanAsync(lookup.Template, ct);
        return Results.Ok(plan.Diff);
    }

    /// <summary>Staleness first: a guard off an unseen diff names a field nobody saw.</summary>
    private static async Task<IResult> ApplyAsync(
        string templateId,
        TemplateReconcileRequest request,
        HttpContext ctx,
        [FromServices] EventTemplateService templates,
        [FromServices] TemplateReconciler reconciler,
        [FromServices] CarnivalAppDbContext db,
        CancellationToken ct)
    {
        var lookup = templates.Lookup(templateId);
        if (lookup.Template is null)
            return ApiError.Status(lookup.Status, lookup.ErrorCode!, lookup.Detail!);

        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0 || reason.Length > MaxReasonLength)
            return ApiError.Unprocessable(ErrorCodes.ReasonRequired,
                $"Give a reason for this reconcile (at most {MaxReasonLength} characters). "
                + "It is recorded on the audit trail beside every row it changes.");

        // The catalogue must not move between plan, hash check and writes. SQLite ignores the level
        // and takes the write lock.
        await using var transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var plan = await reconciler.PlanAsync(lookup.Template, ct);
        if (!string.Equals(request.DiffHash?.Trim(), plan.Diff.DiffHash, StringComparison.Ordinal))
            return Stale();

        var requested = (request.ExcludeCodes ?? [])
            .Select(code => code?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Exclusions are recorded in the catalogue's own spelling, and a code
        // this plan does not name excludes nothing — it never reaches the audit.
        var excluded = plan.Rows
            .Select(row => row.Row.Code)
            .Where(requested.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] ignored = [.. requested
            .Where(code => !plan.Rows.Any(row =>
                string.Equals(row.Row.Code, code, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)];

        if (TemplateReconciler.FirstUnsafe(plan, excluded) is { } unsafeChange)
            return ApiError.Unprocessable(ErrorCodes.TemplateReconcileUnsafe,
                $"'{unsafeChange.Code}': {unsafeChange.Guard} Leave this event out to apply the rest.",
                new Dictionary<string, object?>
                {
                    ["code"] = unsafeChange.Code,
                    ["field"] = unsafeChange.Field,
                });

        TemplateReconcileOutcome outcome;
        try
        {
            outcome = await reconciler.ApplyAsync(
                plan, excluded, ignored, reason, SessionActor.From(ctx), ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
                                           && ex.Entries.Any(e => e.Entity is EventDefinition))
        {
            // Something created a code this plan saw as missing.
            return Stale();
        }
        return Results.Ok(outcome);
    }

    private static IResult Stale() =>
        ApiError.Conflict(ErrorCodes.TemplateReconcileStale,
            "The catalogue or the template has changed since this preview was taken. "
            + "Preview it again and check what it now proposes.");
}
