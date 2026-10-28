using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CarnivalApp.Api.Imports;

namespace CarnivalApp.Api.Data;

public sealed record RetentionOutcome(int IdempotencyKeysDeleted, int ImportBatchesPurged);

/// <summary>
/// Ages out operational rows only. Record classes are never touched, and the removed rows are not
/// audited entities, so nothing is audited per row.
/// </summary>
public sealed class RetentionSweep(CarnivalAppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan IdempotencyKeyRetention = TimeSpan.FromDays(30);

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

        return new(keys, batches);
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
            "Retention sweep: {Keys} idempotency key(s) deleted, {Batches} import batch(es) purged.",
            outcome.IdempotencyKeysDeleted, outcome.ImportBatchesPurged);
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
