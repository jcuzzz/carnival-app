using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Demo;

public enum ProvisionOutcome { Existing, Created, AtCapacity, Busy }

public sealed record ProvisionResult(ProvisionOutcome Outcome, DemoSessionRow? Session = null, string? Token = null);

/// <summary>
/// Visitor sandboxes: one session row and one database each (ADR-0061 §3). Single instance, so one
/// gate serialises provisioning, eviction and the sweep — the sweep must never see a database
/// whose session row is not yet written.
/// </summary>
public sealed class DemoSessions(
    IServiceScopeFactory scopes,
    IVisitorDatabases databases,
    SchoolSettingsProvider settings,
    RoleAssignmentProvider assignments,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DemoSessions> logger) : IDisposable
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly FixedWindowRateLimiter _global = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, configuration.GetValue(DemoMode.ProvisionsPerMinuteKey, 30)),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });

    public static DateTimeOffset ExpiresAt(DemoSessionRow row)
    {
        var absolute = row.CreatedAtUtc + DemoMode.AbsoluteLifetime;
        var idle = row.LastSeenAtUtc + DemoMode.IdleLifetime;
        return absolute < idle ? absolute : idle;
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private bool IsLive(DemoSessionRow row, DateTimeOffset now) => now < ExpiresAt(row);

    public async Task<DemoSessionRow?> FindLiveAsync(string token, CancellationToken ct)
    {
        var hash = Hash(token);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DemoControlDbContext>();
        var row = await db.Sessions.FirstOrDefaultAsync(session => session.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (row is null || !IsLive(row, now)) return null;

        if (now - row.LastSeenAtUtc >= TouchInterval)
        {
            row.LastSeenAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        return row;
    }

    public async Task<ProvisionResult> ProvisionAsync(string? presentedToken, CancellationToken ct)
    {
        if (presentedToken is { Length: > 0 } && await FindLiveAsync(presentedToken, ct) is { } existing)
            return new ProvisionResult(ProvisionOutcome.Existing, existing);

        using var lease = _global.AttemptAcquire();
        if (!lease.IsAcquired) return new ProvisionResult(ProvisionOutcome.Busy);

        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DemoControlDbContext>();
            var now = time.GetUtcNow();
            // Few hundred rows at most; filtered here because SQLite cannot compare DateTimeOffset.
            var live = (await db.Sessions.ToListAsync(ct)).Where(row => IsLive(row, now)).ToList();

            var max = Math.Max(1, configuration.GetValue(DemoMode.MaxSessionsKey, DemoMode.DefaultMaxSessions));
            if (live.Count >= max)
            {
                var victim = live
                    .Where(row => now - row.LastSeenAtUtc >= DemoMode.EvictableAfterIdle)
                    .MinBy(row => row.LastSeenAtUtc);
                if (victim is null) return new ProvisionResult(ProvisionOutcome.AtCapacity);
                logger.LogInformation("Demo at capacity ({Max}); evicting idle session {Database}.",
                    max, victim.DatabaseName);
                await EndAsync(db, victim);
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var session = new DemoSessionRow
            {
                Id = Guid.CreateVersion7(),
                TokenHash = Hash(token),
                DatabaseName = VisitorDatabase.NewName(),
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
            };

            // Not the request's token: a visitor who navigates away must not orphan a database.
            await databases.CreateAsync(session.DatabaseName, CancellationToken.None);
            try
            {
                db.Sessions.Add(session);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch
            {
                await databases.DropAsync(session.DatabaseName, CancellationToken.None);
                throw;
            }

            logger.LogInformation("Demo session provisioned: {Database} ({Live} live).",
                session.DatabaseName, live.Count + 1);
            return new ProvisionResult(ProvisionOutcome.Created, session, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends expired sessions and drops any visitor database without a session.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DemoControlDbContext>();
            var now = time.GetUtcNow();
            var rows = await db.Sessions.ToListAsync(ct);

            var ended = 0;
            foreach (var row in rows.Where(row => !IsLive(row, now)))
            {
                await EndAsync(db, row);
                ended++;
            }

            var kept = rows.Where(row => IsLive(row, now)).Select(row => row.DatabaseName).ToHashSet();
            foreach (var orphan in (await databases.ListAsync(ct)).Where(name => !kept.Contains(name)))
            {
                await databases.DropAsync(orphan, ct);
                Forget(orphan);
                ended++;
            }
            return ended;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndAsync(DemoControlDbContext db, DemoSessionRow row)
    {
        await databases.DropAsync(row.DatabaseName, CancellationToken.None);
        db.Sessions.Remove(row);
        await db.SaveChangesAsync(CancellationToken.None);
        Forget(row.DatabaseName);
    }

    private void Forget(string database)
    {
        settings.Forget(database);
        assignments.Forget(database);
    }

    public void Dispose()
    {
        _gate.Dispose();
        _global.Dispose();
    }
}
