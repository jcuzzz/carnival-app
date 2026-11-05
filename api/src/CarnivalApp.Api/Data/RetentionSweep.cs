using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CarnivalApp.Api.Imports;

namespace CarnivalApp.Api.Data;

public sealed record RetentionOutcome(
    int IdempotencyKeysDeleted,
    int ImportBatchesPurged,
    int SignInAttemptsDeleted = 0,
    int SetupLinksDeleted = 0);

/// <summary>
/// Ages out operational rows only. Record classes are never touched, and the removed rows are not
/// audited entities, so nothing is audited per row.
/// </summary>
public sealed class RetentionSweep(CarnivalAppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan IdempotencyKeyRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan SignInAttemptRetention = TimeSpan.FromDays(90);

    /// <summary>Counted from expiry, so a used link's address stays visible for the window.</summary>
    public static readonly TimeSpan SetupLinkRetention = TimeSpan.FromDays(90);

    private const int DeleteChunk = 500;

    // Ages are compared in memory: SQLite cannot translate a DateTimeOffset comparison, and both
    // tables stay small once swept.
    public async Task<RetentionOutcome> RunAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();

        var keyCutOff = now - IdempotencyKeyRetention;
        var expiredKeys = (await db.IdempotencyKeys.AsNoTracking()
                .Select(k => new { k.Key, k.CreatedAtUtc })
                .ToListAsync(ct))
            .Where(k => k.CreatedAtUtc < keyCutOff)
            .Select(k => k.Key)
            .ToList();
        var keys = 0;
        foreach (var chunk in expiredKeys.Chunk(DeleteChunk))
            keys += await db.IdempotencyKeys.Where(k => chunk.Contains(k.Key)).ExecuteDeleteAsync(ct);

        var batchCutOff = now - RosterImportService.UnpromotedRetention;
        var expiredBatches = (await db.ImportBatches.AsNoTracking()
                .Where(b => b.StagedRowsJson != null || b.ValidationResultsJson != null)
                .Select(b => new { b.Id, b.ImportedAtUtc })
                .ToListAsync(ct))
            .Where(b => b.ImportedAtUtc < batchCutOff)
            .Select(b => b.Id)
            .ToList();
        var batches = 0;
        foreach (var chunk in expiredBatches.Chunk(DeleteChunk))
            batches += await db.ImportBatches
                .Where(b => chunk.Contains(b.Id))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(b => b.StagedRowsJson, (string?)null)
                    .SetProperty(b => b.ValidationResultsJson, (string?)null), ct);

        var attempts = await DeleteSignInAttemptsAsync(now - SignInAttemptRetention, ct);

        var linkCutOff = now - SetupLinkRetention;
        var expiredLinks = (await db.LocalAccountSetupLinks.AsNoTracking()
                .Select(l => new { l.Id, l.ExpiresAtUtc })
                .ToListAsync(ct))
            .Where(l => l.ExpiresAtUtc < linkCutOff)
            .Select(l => l.Id)
            .ToList();
        var links = 0;
        foreach (var chunk in expiredLinks.Chunk(DeleteChunk))
            links += await db.LocalAccountSetupLinks.Where(l => chunk.Contains(l.Id)).ExecuteDeleteAsync(ct);

        return new(keys, batches, attempts, links);
    }

    // The table can grow under a guessing attack, so it is walked oldest first in Id order, which is
    // time order, and stops at the first chunk holding a row still inside the window.
    private async Task<int> DeleteSignInAttemptsAsync(DateTimeOffset cutOff, CancellationToken ct)
    {
        var deleted = 0;
        while (true)
        {
            var oldest = await db.SignInAttempts.AsNoTracking()
                .OrderBy(a => a.Id)
                .Take(DeleteChunk)
                .Select(a => new { a.Id, a.AtUtc })
                .ToListAsync(ct);
            var expired = oldest.TakeWhile(a => a.AtUtc < cutOff).ToList();
            if (expired.Count == 0) return deleted;
            var last = expired[^1].Id;
            deleted += await db.SignInAttempts.Where(a => a.Id <= last).ExecuteDeleteAsync(ct);
            if (expired.Count < oldest.Count) return deleted;
        }
    }
}

/// <summary>Runs the sweep at startup and then daily.</summary>
public sealed class RetentionJob(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Retention sweep failed; retrying next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>False when the context is not PostgreSQL (a test host swaps the provider).</summary>
    public async Task<bool> SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        if (!db.Database.IsNpgsql()) return false;

        var outcome = await scope.ServiceProvider.GetRequiredService<RetentionSweep>().RunAsync(ct);
        logger.LogInformation(
            "Retention sweep: {Keys} idempotency key(s) deleted, {Batches} import batch(es) purged, "
            + "{Attempts} sign-in attempt(s) and {Links} setup link(s) deleted.",
            outcome.IdempotencyKeysDeleted, outcome.ImportBatchesPurged,
            outcome.SignInAttemptsDeleted, outcome.SetupLinksDeleted);
        return true;
    }
}

public static class RetentionRegistration
{
    /// <summary>School deployments only: a demo visitor database is short-lived.</summary>
    public static IServiceCollection AddRetention(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<RetentionSweep>();
        services.AddHostedService<RetentionJob>();
        return services;
    }
}
