using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.Idempotency;

namespace CarnivalApp.Api.Carnivals;

/// <summary>
/// Telemetry: last-write-wins with no concurrency token, and not audited (a report a minute per
/// device would bury the audit trail). Carries no student data.
/// </summary>
public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var devices = apiV1.MapGroup("/carnivals/{id:guid}/devices");

        // Any carnival role may report: every role that holds a device can hold a queue.
        devices.MapPut("/{deviceId}/sync-status", ReportSyncStatusAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<DeviceSyncStatusDto>();

        // Reading names every official and their backlog: staff information, not scoreboard.
        devices.MapGet("/", ListDevicesAsync)
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy)
            .Produces<DeviceFleetDto>();
    }

    /// <summary>Counts only: the queue items would put student names in a telemetry path.</summary>
    public sealed record DeviceSyncStatusRequest(
        int Pending,
        int Parked,
        DateTimeOffset? OldestPendingCapturedAt,
        string? AppVersion);

    public sealed record DeviceSyncStatusDto(
        string DeviceId,
        DateTimeOffset LastSeenUtc,
        DateTimeOffset? LastDrainUtc,
        int PendingCount,
        int ParkedCount,
        DateTimeOffset? OldestPendingCapturedAtUtc,
        string LastSubject,
        string? LastDisplayName,
        string? AppVersion);

    /// <summary>
    /// Every panel state is an age, so the server's clock travels with the fleet for the client to
    /// correct its own skew.
    /// </summary>
    public sealed record DeviceFleetDto(
        DateTimeOffset ServerNowUtc,
        IReadOnlyList<DeviceSyncStatusDto> Devices);

    private static DeviceSyncStatusDto Project(DeviceSyncStatus d) => new(
        d.DeviceId, d.LastSeenUtc, d.LastDrainUtc, d.PendingCount, d.ParkedCount,
        d.OldestPendingCapturedAtUtc, d.LastSubject, d.LastDisplayName, d.AppVersion);

    public const int MaxDeviceIdLength = 64;

    /// <summary>
    /// Device-asserted, becomes a key and is shown to an organiser: the charset stops log or row
    /// forgery, and is wider than a UUID so asset tags work.
    /// </summary>
    public static bool IsWellFormedDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && deviceId.Length <= MaxDeviceIdLength
        && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    public const int MaxReportedCount = 100_000;

    /// <summary>Matches the columns these two land in.</summary>
    public const int MaxDisplayNameLength = 128;
    public const int MaxAppVersionLength = 32;

    private static async Task<IResult> ReportSyncStatusAsync(
        Guid id, string deviceId, DeviceSyncStatusRequest request, HttpContext ctx,
        [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        if (!IsWellFormedDeviceId(deviceId))
            return ApiError.BadRequest(ErrorCodes.DeviceIdInvalid,
                $"deviceId must be 1–{MaxDeviceIdLength} characters of letters, digits, "
                + "'-', '_', '.' or ':'.");
        // Otherwise any role could zero another device's row and erase a stranded warning.
        var asserted = ctx.Request.Headers[IdempotencyEndpointFilter.DeviceIdHeader].ToString();
        if (!string.Equals(asserted, deviceId, StringComparison.Ordinal))
            return ApiError.Forbidden(ErrorCodes.DeviceIdMismatch,
                $"A device may only report its own queue: {IdempotencyEndpointFilter.DeviceIdHeader} "
                + "must be present and match the device id in the route.");
        if (request.Pending < 0 || request.Parked < 0)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                "pending and parked cannot be negative.");
        if (request.Pending > MaxReportedCount || request.Parked > MaxReportedCount)
            return ApiError.BadRequest(ErrorCodes.ValidationFailed,
                $"pending and parked cannot exceed {MaxReportedCount}.");
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct))
            return CarnivalGuards.CarnivalNotFound();

        var now = DateTimeOffset.UtcNow;
        var actor = SessionActor.From(ctx);
        var row = await db.DeviceSyncStatuses
            .FirstOrDefaultAsync(d => d.CarnivalId == id && d.DeviceId == deviceId, ct);
        int? priorPending = row?.PendingCount;
        if (row is null)
        {
            row = new DeviceSyncStatus
            {
                CarnivalId = id, DeviceId = deviceId, LastSubject = actor.Subject,
            };
            db.DeviceSyncStatuses.Add(row);
        }

        row.LastSeenUtc = now;
        // Derived: only a falling count means something drained; checking in is not draining.
        if (priorPending is { } prior && request.Pending < prior) row.LastDrainUtc = now;
        row.PendingCount = request.Pending;
        row.ParkedCount = request.Parked;
        // Clamped: a fast device clock would report a future capture and hide the age.
        row.OldestPendingCapturedAtUtc = request.OldestPendingCapturedAt is { } oldest && oldest > now
            ? now
            : request.OldestPendingCapturedAt;
        row.LastSubject = actor.Subject;
        row.LastDisplayName = Bounded(ctx.User.GetDisplayName(), MaxDisplayNameLength);
        row.AppVersion = Bounded(request.AppVersion, MaxAppVersionLength);

        await db.SaveChangesAsync(ct);
        return Results.Ok(Project(row));
    }

    /// <summary>
    /// Truncated rather than refused: a report failing over a cosmetic field is a device the
    /// organiser cannot see at all.
    /// </summary>
    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var stripped = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (stripped.Length == 0) return null;
        return stripped.Length <= maxLength ? stripped : stripped[..maxLength];
    }

    private static async Task<IResult> ListDevicesAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        if (!await CarnivalGuards.CarnivalExistsAsync(db, id, ct))
            return CarnivalGuards.CarnivalNotFound();

        var devices = await db.DeviceSyncStatuses.AsNoTracking()
            .Where(d => d.CarnivalId == id)
            .ToListAsync(ct);
        return Results.Ok(new DeviceFleetDto(DateTimeOffset.UtcNow, devices
            .OrderByDescending(d => d.PendingCount + d.ParkedCount > 0)
            .ThenBy(d => d.LastSeenUtc)
            .Select(Project)
            .ToList()));
    }
}
