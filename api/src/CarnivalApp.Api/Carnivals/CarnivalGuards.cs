using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

// The outbox branches retry-vs-reject on these codes: administration refuses as final
// (NOT_EDITABLE), capture as transient (NOT_OPEN) so a queued item waits for it to open.
internal static class CarnivalGuards
{
    // Tracked: the mutation paths edit what they read.
    public static Task<Carnival?> FindCarnivalAsync(CarnivalAppDbContext db, Guid id, CancellationToken ct) =>
        db.Carnivals.FirstOrDefaultAsync(c => c.Id == id, ct);

    public static Task<bool> CarnivalExistsAsync(CarnivalAppDbContext db, Guid id, CancellationToken ct) =>
        db.Carnivals.AnyAsync(c => c.Id == id, ct);

    // Scoped to its carnival, so a wrong-carnival id is a 404 rather than a leak.
    public static Task<CarnivalEvent?> FindEventAsync(
        CarnivalAppDbContext db, Guid carnivalId, Guid eventId, CancellationToken ct) =>
        db.CarnivalEvents.FirstOrDefaultAsync(e => e.CarnivalId == carnivalId && e.Id == eventId, ct);

    public static Task<CarnivalEvent?> FindEventWithCarnivalAsync(
        CarnivalAppDbContext db, Guid carnivalId, Guid eventId, CancellationToken ct) =>
        db.CarnivalEvents
            .Include(e => e.Carnival)
            // Every event projection reads the measure; a missing include is a runtime 500.
            .Include(e => e.EventDefinition)
            .FirstOrDefaultAsync(e => e.CarnivalId == carnivalId && e.Id == eventId, ct);

    public static Task<bool> EventExistsAsync(
        CarnivalAppDbContext db, Guid carnivalId, Guid eventId, CancellationToken ct) =>
        db.CarnivalEvents.AnyAsync(e => e.CarnivalId == carnivalId && e.Id == eventId, ct);

    public static IResult CarnivalNotFound() =>
        ApiError.NotFound(ErrorCodes.CarnivalNotFound, "No carnival exists with that id.");

    public static IResult EventNotFound() =>
        ApiError.NotFound(ErrorCodes.EventNotFound, "No event exists with that id in this carnival.");

    public static bool IsLocked(CarnivalStatus status) =>
        status is CarnivalStatus.Closed or CarnivalStatus.Finalised;

    public static IResult? RequireEditable(Carnival carnival, string attempt) =>
        IsLocked(carnival.Status) ? NotEditable(carnival, attempt) : null;

    // Survives Closed: scoring settings stay editable while standings are reconciled.
    public static IResult? RequireNotFinalised(Carnival carnival, string attempt) =>
        carnival.Status == CarnivalStatus.Finalised ? NotEditable(carnival, attempt) : null;

    public static IResult? RequireCapturable(Carnival carnival, string attempt) =>
        IsLocked(carnival.Status) ? NotOpen(carnival, attempt) : null;

    public static IResult? RequireNotFinalisedForCapture(Carnival carnival, string attempt) =>
        carnival.Status == CarnivalStatus.Finalised ? NotOpen(carnival, attempt) : null;

    public static IResult? RequireOpen(Carnival carnival, string attempt) =>
        carnival.Status != CarnivalStatus.Open ? NotOpen(carnival, attempt) : null;

    // Load the event with its carnival (FindEventWithCarnivalAsync), or this is a runtime 500.
    public static IResult? RequireEventEditable(CarnivalEvent carnivalEvent, string attempt) =>
        RequireNotFinalised(carnivalEvent.Carnival, attempt);

    public static IResult? RequireEventCapturable(CarnivalEvent carnivalEvent, string attempt) =>
        carnivalEvent.Status is CarnivalEventStatus.Closed or CarnivalEventStatus.Finalised
            ? EventNotOpen(carnivalEvent, attempt)
            : null;

    public static IResult? RequireEventOpen(CarnivalEvent carnivalEvent, string attempt) =>
        carnivalEvent.Status != CarnivalEventStatus.Open ? EventNotOpen(carnivalEvent, attempt) : null;

    // Same code as the result-correction conflict: to the outbox both mean re-read and retry.
    public static IResult ConcurrencyConflict(string detail) =>
        ApiError.Conflict(ErrorCodes.ConcurrencyConflict, detail);

    public static async Task<IResult?> SaveGuardingConcurrencyAsync(
        CarnivalAppDbContext db, string subject, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrencyConflict(
                $"Concurrent change conflict: the {subject} was modified by another request " +
                "since it was read. Re-read it and retry.");
        }
        catch (DbUpdateException)
        {
            // Any other database refusal is terminal for this payload: park it rather than
            // retry a 500 for ever.
            return ApiError.Conflict(ErrorCodes.WriteRefused,
                $"The database refused this change to the {subject}. It cannot succeed as sent — "
                + "check the values and re-enter it.");
        }
    }

    private static IResult NotEditable(Carnival carnival, string attempt) =>
        ApiError.Conflict(ErrorCodes.CarnivalNotEditable, CarnivalDetail(carnival, attempt));

    private static IResult NotOpen(Carnival carnival, string attempt) =>
        ApiError.Conflict(ErrorCodes.CarnivalNotOpen, CarnivalDetail(carnival, attempt));

    private static IResult EventNotOpen(CarnivalEvent carnivalEvent, string attempt) =>
        ApiError.Conflict(ErrorCodes.EventNotOpen, EventDetail(carnivalEvent, attempt));

    private static string CarnivalDetail(Carnival carnival, string attempt) =>
        $"{attempt} on a {carnival.Status} carnival.";

    private static string EventDetail(CarnivalEvent carnivalEvent, string attempt) =>
        $"{attempt} on a {carnivalEvent.Status} event.";
}
