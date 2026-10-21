using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.School;

/// <summary>
/// Seeds the row on first run only; after that the configured name, brand and houses are ignored.
/// </summary>
public static class SchoolSettingsBootstrap
{
    public const string IdentifierKey = "School:Identifier";
    public const string DefaultIdentifier = "SCHOOL";

    public static async Task EnsureAsync(
        IServiceProvider services, string? identifierOverride = null, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SchoolSettingsBootstrap).FullName!);

        // The process's first database access; the raw
        // provider exception names neither cause nor fix.
        SchoolConfiguration? existing;
        try
        {
            existing = await db.SchoolConfigurations.AsNoTracking().TheSchool().FirstOrDefaultAsync(ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "School settings: the database has no schema or is unreachable — apply the EF migrations "
                + "(or set Database:ApplyMigrationsOnStartup=true) before starting the API. Cause: "
                + error.Message, error);
        }

        if (existing is not null)
        {
            logger.LogInformation(
                "School settings: source = database (row {SchoolConfigurationId}); configuration values "
                + "for name/brand/houses are ignored", existing.Id);
            return;
        }

        // The last moment a person can be told the
        // configured colour is unusable before it is stored.
        if (BrandColour.DescribeRefusal(config[BrandColour.ConfigKey]) is { } refusal)
            throw new InvalidOperationException(refusal);

        var row = new SchoolConfiguration
        {
            Id = Guid.CreateVersion7(),
            SchoolIdentifier = identifierOverride ?? config[IdentifierKey] ?? DefaultIdentifier,
            Name = NonBlank(config["School:Name"]) ?? "(unconfigured)",
            Timezone = SchoolTimezone.From(config),
            BrandingJson = SchoolSettings.SerialiseBranding(
                BrandColour.Normalise(config[BrandColour.ConfigKey])),
            HousesJson = SchoolSettings.SerialiseHouses(HouseColour.Resolve(config, logger)),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.SchoolConfigurations.Add(row);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "School settings: source = configuration (first run, seeded row {SchoolConfigurationId})", row.Id);
    }

    // Compose passes an unset variable as "".
    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
