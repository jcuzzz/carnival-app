using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;

namespace CarnivalApp.Api.Identity;

public sealed record ActiveAssignment(Guid Id, string? SubjectId, string SubjectUpn, PlatformRole Role);

/// <summary>
/// Separated from the provider so the transformation is testable without a database.
/// </summary>
public interface IRoleAssignmentSource
{
    Task<IReadOnlyList<ActiveAssignment>> GetActiveAsync(CancellationToken ct = default);

    Task BindAsync(
        IReadOnlyList<Guid> assignmentIds,
        string subjectId,
        string upn,
        string? displayName,
        CancellationToken ct = default);
}

/// <summary>
/// Single-instance deployment, so a synchronous <see cref="Invalidate"/> on every write is enough
/// for changes to take effect on the next request. One slot per database, the last-good list
/// included: a demo host serves one database per visitor (ADR-0061 §1).
/// </summary>
public sealed class RoleAssignmentProvider(
    IServiceScopeFactory scopes, IDatabaseKey database, ILogger<RoleAssignmentProvider> logger)
    : IRoleAssignmentSource
{
    private sealed class Slot
    {
        public IReadOnlyList<ActiveAssignment>? Cached;
        public IReadOnlyList<ActiveAssignment> LastGood = [];
        public long Generation;
        public int Healthy = 1;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();

    /// <summary>
    /// A failed read serves the last-good list: inside the claims
    /// transformation a throw would 500 every authenticated request.
    /// </summary>
    public async Task<IReadOnlyList<ActiveAssignment>> GetActiveAsync(CancellationToken ct = default)
    {
        var slot = _slots.GetOrAdd(database.Current, _ => new Slot());
        if (Volatile.Read(ref slot.Cached) is { } cached) return cached;

        var generation = Volatile.Read(ref slot.Generation);
        List<ActiveAssignment> rows;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            rows = await db.RoleAssignments.AsNoTracking()
                .Where(row => row.RevokedAtUtc == null)
                .Select(row => new ActiveAssignment(row.Id, row.SubjectId, row.SubjectUpn, row.Role))
                .ToListAsync(ct);
        }
        catch (Exception error)
        {
            // Once per outage, not once per request.
            if (Interlocked.Exchange(ref slot.Healthy, 0) == 1)
                logger.LogError(error,
                    "Reading the active role assignments failed; serving the last-good list "
                    + "of {Count}. In-app role changes will not take effect until this recovers.",
                    slot.LastGood.Count);
            return Volatile.Read(ref slot.LastGood);
        }

        Volatile.Write(ref slot.LastGood, rows);
        Volatile.Write(ref slot.Healthy, 1);
        // Never cache a read an Invalidate overtook.
        if (Volatile.Read(ref slot.Generation) == generation) Volatile.Write(ref slot.Cached, rows);
        return rows;
    }

    public async Task BindAsync(
        IReadOnlyList<Guid> assignmentIds,
        string subjectId,
        string upn,
        string? displayName,
        CancellationToken ct = default)
    {
        if (assignmentIds.Count == 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();

        try
        {
            var rows = await db.RoleAssignments
                .Where(row => assignmentIds.Contains(row.Id) && row.SubjectId == null)
                .ToListAsync(ct);
            if (rows.Count == 0) return;

            foreach (var row in rows)
            {
                row.SubjectId = subjectId;
                row.SubjectDisplayName ??= displayName;
                audit.Record(
                    RoleAssignmentAudit.SystemActor,
                    AuditActions.RoleAssignmentBind,
                    nameof(RoleAssignment),
                    row.Id.ToString(),
                    priorValues: new { subjectId = (string?)null, upn = row.SubjectUpn, role = row.Role },
                    newValues: new { subjectId, upn = row.SubjectUpn, role = row.Role, displayName });
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception error)
        {
            // Must not fail the request being authenticated; the next request tries again.
            logger.LogWarning(error, "Binding {Count} role assignment(s) for {Upn} failed.",
                assignmentIds.Count, upn);
            return;
        }

        Invalidate();
    }

    public void Invalidate()
    {
        if (!_slots.TryGetValue(database.Current, out var slot)) return;
        Interlocked.Increment(ref slot.Generation);
        Volatile.Write(ref slot.Cached, null);
    }

    public void Forget(string databaseName) => _slots.TryRemove(databaseName, out _);
}

public static class RoleAssignmentAudit
{
    /// <summary>The bind is nobody's act — it happens on the target's own first request.</summary>
    public static readonly AuditActor SystemActor = new("system", null);
}
