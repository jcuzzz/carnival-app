using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0045 §1: configuration is the FIRST-RUN seed of the SchoolConfiguration
/// row and nothing more. The second boot reads the row, whatever the environment
/// now says — that is the behaviour change the runbook records.
/// </summary>
public sealed class SchoolSettingsBootstrapTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SchoolSettingsBootstrapTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = new CarnivalAppDbContext(
            new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private ServiceProvider Host(params (string Key, string? Value)[] config) =>
        Host(_connection, config);

    private static ServiceProvider Host(
        SqliteConnection connection,
        (string Key, string? Value)[] config,
        Action<DbContextOptionsBuilder>? tune = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(config.ToDictionary(entry => entry.Key, entry => entry.Value))
            .Build());
        services.AddDbContext<CarnivalAppDbContext>(options =>
        {
            options.UseSqlite(connection);
            tune?.Invoke(options);
        });
        services.AddSingleton<IDatabaseKey, SingleDatabase>();
        services.AddSingleton<SchoolSettingsProvider>();
        return services.BuildServiceProvider();
    }

    private static (string Key, string? Value)[] FourHouses(params (string Key, string? Value)[] extra) =>
    [
        ("School:Name", "Bootstrap College"),
        ("School:Timezone", "Australia/Melbourne"),
        ("School:Houses:0", "BANKSIA"),
        ("School:Houses:1", "WATTLE"),
        ("School:Houses:2", "MOUNTAIN ASH"),
        .. extra,
    ];

    private async Task<SchoolSettings> ReadAsync()
    {
        await using var db = new CarnivalAppDbContext(
            new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options);
        return SchoolSettings.From(
            await db.SchoolConfigurations.AsNoTracking().SingleAsync(), NullLogger.Instance);
    }

    [Fact]
    public async Task First_run_seeds_the_row_from_configuration()
    {
        using var host = Host(FourHouses(
            ("School:BrandColour", "#1F3A5F"),
            ("School:HouseColours:BANKSIA", "#3346c0"),
            // A duplicate degrades to text-only with a warning, exactly as the
            // startup path did before the row existed (ADR-0044 §2).
            ("School:HouseColours:WATTLE", "#3346C0")));
        await SchoolSettingsBootstrap.EnsureAsync(host);

        var settings = await ReadAsync();
        Assert.Equal("Bootstrap College", settings.Name);
        Assert.Equal("#1f3a5f", settings.BrandColour);
        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH"], settings.Houses.Select(house => house.Code));
        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH"], settings.Houses.Select(house => house.Name));
        Assert.Equal(new string?[] { "#3346c0", null, null }, settings.Houses.Select(house => house.Colour));
        Assert.Null(settings.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_or_timezone_seeds_the_same_default_as_an_absent_one(string? configured)
    {
        // Compose passes an unset SCHOOL_NAME / SCHOOL_TIMEZONE as an empty string.
        using var host = Host(("School:Name", configured), ("School:Timezone", configured));
        await SchoolSettingsBootstrap.EnsureAsync(host);

        await using var db = new CarnivalAppDbContext(
            new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options);
        var row = await db.SchoolConfigurations.SingleAsync();
        Assert.Equal("(unconfigured)", row.Name);
        Assert.Equal("Australia/Melbourne", row.Timezone);
    }

    [Theory]
    [InlineData(null, SchoolSettingsBootstrap.DefaultIdentifier)]
    [InlineData("8123", "8123")]
    public async Task The_identifier_comes_from_configuration_or_the_default(
        string? configured, string expected)
    {
        using var host = Host(configured is null
            ? FourHouses()
            : FourHouses((SchoolSettingsBootstrap.IdentifierKey, configured)));
        await SchoolSettingsBootstrap.EnsureAsync(host);

        await using var db = new CarnivalAppDbContext(
            new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options);
        Assert.Equal(expected, (await db.SchoolConfigurations.SingleAsync()).SchoolIdentifier);
    }

    [Fact]
    public async Task An_identifier_override_wins_for_the_synthetic_dev_loop()
    {
        using var host = Host(FourHouses(("School:Identifier", "0000")));
        await SchoolSettingsBootstrap.EnsureAsync(host, "DEV-SYNTH");

        await using var db = new CarnivalAppDbContext(
            new DbContextOptionsBuilder<CarnivalAppDbContext>().UseSqlite(_connection).Options);
        Assert.Equal("DEV-SYNTH", (await db.SchoolConfigurations.SingleAsync()).SchoolIdentifier);
    }

    [Fact]
    public async Task A_second_run_leaves_the_row_alone_even_when_configuration_changed()
    {
        using var first = Host(FourHouses(("School:BrandColour", "#1F3A5F")));
        await SchoolSettingsBootstrap.EnsureAsync(first);

        using var second = Host(
            ("School:Name", "Somewhere Else"),
            ("School:Timezone", "Australia/Perth"),
            ("School:BrandColour", "#2e9e52"),
            ("School:Houses:0", "RED"),
            ("School:Houses:1", "BLUE"));
        await SchoolSettingsBootstrap.EnsureAsync(second);

        var settings = await ReadAsync();
        Assert.Equal("Bootstrap College", settings.Name);
        Assert.Equal("#1f3a5f", settings.BrandColour);
        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH"], settings.Houses.Select(house => house.Code));
    }

    [Fact]
    public async Task A_malformed_brand_colour_refuses_the_first_run()
    {
        using var host = Host(FourHouses(("School:BrandColour", "navy-ish")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SchoolSettingsBootstrap.EnsureAsync(host));
        Assert.Contains("navy-ish", error.Message);
    }

    /// <summary>
    /// The other half of the same rule: once the row exists a bad configured
    /// colour is ignored, not a boot failure (ADR-0045 consequences).
    /// </summary>
    [Fact]
    public async Task A_malformed_brand_colour_no_longer_stops_a_later_boot()
    {
        using var first = Host(FourHouses());
        await SchoolSettingsBootstrap.EnsureAsync(first);

        using var second = Host(FourHouses(("School:BrandColour", "navy-ish")));
        await SchoolSettingsBootstrap.EnsureAsync(second);

        Assert.Null((await ReadAsync()).BrandColour);
    }

    /// <summary>
    /// Both boot-time reads of the one school row order before FirstOrDefault.
    /// Unordered, EF only logs a warning, so the warning is promoted to a throw
    /// here — that is the whole assertion.
    /// </summary>
    [Fact]
    public async Task The_boot_reads_of_the_school_row_are_ordered()
    {
        using var host = Host(_connection, FourHouses(), options => options.ConfigureWarnings(
            warnings => warnings.Throw(CoreEventId.FirstWithoutOrderByAndFilterWarning)));

        await SchoolSettingsBootstrap.EnsureAsync(host);   // seeds, then reads on the next line
        await SchoolSettingsBootstrap.EnsureAsync(host);
        var settings = await host.GetRequiredService<SchoolSettingsProvider>().GetAsync();

        Assert.Equal("Bootstrap College", settings.Name);
    }

    /// <summary>
    /// The first database touch of the process: without this the operator sees a
    /// raw provider error naming a missing table, not the step that fixes it.
    /// </summary>
    [Fact]
    public async Task A_database_with_no_schema_fails_with_a_named_startup_error()
    {
        using var bare = new SqliteConnection("DataSource=:memory:");
        bare.Open();
        using var host = Host(bare, FourHouses());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SchoolSettingsBootstrap.EnsureAsync(host));
        Assert.Contains("no schema or is unreachable", error.Message);
        Assert.Contains("Database:ApplyMigrationsOnStartup", error.Message);
        Assert.NotNull(error.InnerException);
    }
}
