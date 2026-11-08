using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CarnivalApp.Api.Data;

public static class StartupMigrations
{
    /// <summary>
    /// Creates the history table first: on a fresh database EF reads it before creating it, and
    /// logs that failed read as an error.
    /// </summary>
    public static async Task<string[]> ApplyAsync(
        CarnivalAppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(ct);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
        logger.LogInformation("Startup migrations pending: {Count} [{Migrations}].",
            pending.Length, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Startup migrations applied: {Count}.", pending.Length);
        return pending;
    }
}
