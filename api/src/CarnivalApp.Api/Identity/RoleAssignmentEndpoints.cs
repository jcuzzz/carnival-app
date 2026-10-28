using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Identity;

/// <summary>A bootstrap administrator row carries no Id and cannot be revoked in-app.</summary>
public sealed record RoleAssignmentDto(
    Guid? Id,
    string Upn,
    string? DisplayName,
    PlatformRole Role,
    bool Bootstrap,
    bool BoundToSubject,
    string? Reason,
    string? AssignedBySubject,
    DateTimeOffset? AssignedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedBySubject,
    string? RevocationReason);

public sealed record GrantRoleRequest(string? Upn, string? Role, string? Reason);

public sealed record RevokeRoleRequest(string? Reason);

/// <summary>
/// Every write invalidates the claims cache synchronously, effective on the target's next request.
/// </summary>
public static class RoleAssignmentEndpoints
{
    public static void MapRoleAssignmentEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var group = apiV1.MapGroup("/admin/role-assignments")
            .RequireAuthorization(RoleAuthorization.AdministerRolesPolicy);

        group.MapGet("", ListAsync).Produces<IReadOnlyList<RoleAssignmentDto>>();
        group.MapPost("", GrantAsync).Produces<RoleAssignmentDto>(StatusCodes.Status201Created);
        group.MapDelete("/{id:guid}", RevokeAsync).Produces<RoleAssignmentDto>();
    }

    private static RoleAssignmentDto Project(RoleAssignment row) => new(
        row.Id,
        row.SubjectUpn,
        row.SubjectDisplayName,
        row.Role,
        Bootstrap: false,
        BoundToSubject: row.SubjectId is not null,
        row.Reason,
        row.CreatedBySubject,
        row.CreatedAtUtc,
        row.RevokedAtUtc,
        row.RevokedBySubject,
        row.RevocationReason);

    private static IEnumerable<RoleAssignmentDto> BootstrapRows(IConfiguration configuration) =>
        GroupToRoleClaimsTransformation.BootstrapAdministrators(configuration)
        .Select(entry => new RoleAssignmentDto(
            Id: null,
            entry,
            DisplayName: null,
            PlatformRole.SystemAdministrator,
            Bootstrap: true,
            BoundToSubject: true,
            Reason: null,
            AssignedBySubject: null,
            AssignedAtUtc: null,
            RevokedAtUtc: null,
            RevokedBySubject: null,
            RevocationReason: null));

    private static async Task<IResult> ListAsync(
        [FromServices] CarnivalAppDbContext db,
        [FromServices] IConfiguration configuration,
        CancellationToken ct)
    {
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        var rows = await db.RoleAssignments.AsNoTracking().ToListAsync(ct);
        var ordered = rows
            .OrderBy(row => row.SubjectUpn, StringComparer.Ordinal)
            .ThenBy(row => row.CreatedAtUtc)
            .Select(Project);
        return Results.Ok(BootstrapRows(configuration).Concat(ordered).ToList());
    }

    private static async Task<IResult> GrantAsync(
        GrantRoleRequest request,
        HttpContext ctx,
        [FromServices] CarnivalAppDbContext db,
        [FromServices] AuditService audit,
        [FromServices] RoleAssignmentProvider provider,
        CancellationToken ct)
    {
        if (NormaliseUpn(request.Upn) is not { } upn)
            return ApiError.Unprocessable(ErrorCodes.RoleUpnInvalid,
                "Enter the person's sign-in address (their UPN or email), e.g. name@school.vic.edu.au.");
        if (!PlatformRoles.TryParse(request.Role, out var role, out _))
            return ApiError.Unprocessable(ErrorCodes.RoleUnknown,
                $"'{request.Role}' is not a platform role.");
        if (NormaliseReason(request.Reason) is not { } reason)
            return ApiError.Unprocessable(ErrorCodes.RoleReasonRequired,
                $"Give a reason for this assignment (at most {RoleAssignmentLimits.MaxReasonLength} characters).");

        if (await IsActiveAsync(db, upn, role, ct))
            return ApiError.Conflict(ErrorCodes.RoleAssignmentDuplicate,
                $"{upn} already holds {role} by an in-app assignment.");

        var actor = SessionActor.From(ctx);
        var assignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            SubjectUpn = upn,
            SubjectType = RoleSubjectType.User,
            Role = role,
            Reason = reason,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBySubject = actor.Subject,
        };
        db.RoleAssignments.Add(assignment);
        audit.Record(actor, AuditActions.RoleAssignmentGrant, nameof(RoleAssignment),
            assignment.Id.ToString(),
            newValues: new { upn, role, reason });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index is the real guard:
            // two concurrent grants both clear the check.
            db.ChangeTracker.Clear();
            if (!await IsActiveAsync(db, upn, role, ct)) throw;
            return ApiError.Conflict(ErrorCodes.RoleAssignmentDuplicate,
                $"{upn} already holds {role} by an in-app assignment.");
        }

        provider.Invalidate();
        return Results.Created($"/api/v1/admin/role-assignments/{assignment.Id}", Project(assignment));
    }

    private static Task<bool> IsActiveAsync(
        CarnivalAppDbContext db, string upn, PlatformRole role, CancellationToken ct) =>
        db.RoleAssignments.AsNoTracking()
            .AnyAsync(row => row.SubjectUpn == upn && row.Role == role && row.RevokedAtUtc == null, ct);

    private static async Task<IResult> RevokeAsync(
        Guid id,
        // Explicit: a minimal-API DELETE never infers a body.
        [FromBody] RevokeRoleRequest? request,
        HttpContext ctx,
        [FromServices] CarnivalAppDbContext db,
        [FromServices] AuditService audit,
        [FromServices] RoleAssignmentProvider provider,
        CancellationToken ct)
    {
        var row = await db.RoleAssignments.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (row is null)
            return ApiError.NotFound(ErrorCodes.RoleAssignmentNotFound, "That role assignment does not exist.");

        if (NormaliseReason(request?.Reason) is not { } reason)
            return ApiError.Unprocessable(ErrorCodes.RoleReasonRequired,
                $"Give a reason for this revocation (at most {RoleAssignmentLimits.MaxReasonLength} characters).");

        if (row.RevokedAtUtc is not null) return Results.Ok(Project(row));

        // A UPN rename between grants leaves one subject holding the role under two rows.
        var role = row.Role;
        var upn = row.SubjectUpn;
        var subjectId = row.SubjectId;
        var targets = await db.RoleAssignments
            .Where(a => a.RevokedAtUtc == null && a.Role == role
                && (a.SubjectUpn == upn || (subjectId != null && a.SubjectId == subjectId)))
            .ToListAsync(ct);

        var actor = SessionActor.From(ctx);
        // Not "the last administrator": uncomputable while roles also arrive from groups.
        if (role == PlatformRole.SystemAdministrator && targets.Any(a => IsCaller(a, ctx)))
            return ApiError.Conflict(ErrorCodes.RoleSelfRevoke,
                "You cannot revoke your own system administrator role. Ask another system administrator.");

        var revokedAtUtc = DateTimeOffset.UtcNow;
        foreach (var target in targets)
        {
            target.RevokedAtUtc = revokedAtUtc;
            target.RevokedBySubject = actor.Subject;
            target.RevocationReason = reason;
            audit.Record(actor, AuditActions.RoleAssignmentRevoke, nameof(RoleAssignment), target.Id.ToString(),
                priorValues: new { upn = target.SubjectUpn, role = target.Role, revoked = false },
                newValues: new { upn = target.SubjectUpn, role = target.Role, revoked = true, reason });
        }
        await db.SaveChangesAsync(ct);

        provider.Invalidate();
        return Results.Ok(Project(row));
    }

    private static bool IsCaller(RoleAssignment row, HttpContext ctx)
    {
        var subject = ctx.User.GetStableSubject();
        if (row.SubjectId is not null && subject is not null
            && row.SubjectId.Equals(subject, StringComparison.OrdinalIgnoreCase))
            return true;
        var upn = ctx.User.GetUpn();
        return upn is not null && row.SubjectUpn.Equals(upn.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // Stored lower-case: a UPN is case-insensitive and is matched verbatim afterwards.
    private static string? NormaliseUpn(string? value)
    {
        var upn = (value ?? "").Trim().ToLowerInvariant();
        if (upn.Length == 0 || upn.Length > RoleAssignmentLimits.MaxUpnLength) return null;
        if (upn.Any(char.IsWhiteSpace)) return null;
        var at = upn.IndexOf('@');
        return at > 0 && at < upn.Length - 1 && upn.IndexOf('@', at + 1) < 0 ? upn : null;
    }

    private static string? NormaliseReason(string? value)
    {
        var reason = (value ?? "").Trim();
        return reason.Length == 0 || reason.Length > RoleAssignmentLimits.MaxReasonLength ? null : reason;
    }
}
