using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// <see cref="AccountsCommand"/> as Program runs it: the command never migrates, so on a database
/// the API has not migrated yet it refuses rather than failing half way.
/// </summary>
public static class AccountsCommandHost
{
    public const string NotMigrated = "database not migrated yet: deploy the API first";

    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            // The Development SQLite loop is created, not migrated.
            if (db.Database.IsNpgsql() && (await db.Database.GetPendingMigrationsAsync()).Any())
            {
                await error.WriteLineAsync(NotMigrated);
                return AccountsCommand.Failure;
            }
        }
        catch (Exception failure)
        {
            await error.WriteLineAsync($"accounts: {failure.Message}");
            return AccountsCommand.Failure;
        }
        return await AccountsCommand.RunAsync(services, args, output, error);
    }
}
