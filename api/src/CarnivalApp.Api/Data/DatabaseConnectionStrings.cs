using Microsoft.EntityFrameworkCore;

namespace CarnivalApp.Api.Data;

/// <summary>
/// The owner role builds the schema, a least-privilege role serves requests; with no migrations
/// string both are the application string.
/// </summary>
public static class DatabaseConnectionStrings
{
    public const string DevelopmentFallback =
        "Host=localhost;Port=5432;Database=carnival;Username=carnival;Password=dev-only";

    public static string Application(IConfiguration configuration) =>
        Configured(configuration, "CarnivalAppDb") ?? DevelopmentFallback;

    public static string? MigrationsOverride(IConfiguration configuration) =>
        Configured(configuration, "CarnivalAppDbMigrations");

    public static string Migrations(IConfiguration configuration) =>
        MigrationsOverride(configuration) ?? Application(configuration);

    private static string? Configured(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>
/// Built outside the host container, so the logger factory is
/// passed in or EF's migration lines never reach the api log.
/// </summary>
public static class MigrationContextOptions
{
    public static DbContextOptions<CarnivalAppDbContext> Build(
        string connectionString, ILoggerFactory loggerFactory) =>
        new DbContextOptionsBuilder<CarnivalAppDbContext>()
            .UseNpgsql(connectionString)
            .UseLoggerFactory(loggerFactory)
            .Options;
}
