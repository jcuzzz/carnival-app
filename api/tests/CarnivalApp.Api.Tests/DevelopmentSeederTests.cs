using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>The synthetic dev loop's database counts as a set-up school (ADR-0066 §2).</summary>
public sealed class DevelopmentSeederTests : IDisposable
{
    private readonly ApiTestFactory _factory = new ApiTestFactory()
        .With("Database:Seed:RosterCsvPath", Fixtures.Path("ST_SYNTH.csv"));

    public void Dispose() => _factory.Dispose();

    private async Task SeedAsync()
    {
        _factory.CreateClient().Dispose(); // builds the host, which bootstraps the school row
        using var scope = _factory.Services.CreateScope();
        await DevelopmentSeeder.SeedAsync(scope.ServiceProvider);
    }

    private async Task<bool> SetupConfirmedAsync() =>
        (await _factory.Services.GetRequiredService<SchoolSettingsProvider>().GetAsync()).SetupConfirmed;

    [Fact]
    public async Task Seeding_an_empty_database_marks_the_school_set_up()
    {
        await SeedAsync();

        Assert.True(await SetupConfirmedAsync());
        Assert.True(await CarnivalSeed.QueryAsync(_factory, db => db.Students.AnyAsync()));
    }

    [Fact]
    public async Task A_database_seeded_before_the_marker_existed_gets_it_and_nothing_else()
    {
        _factory.CreateClient().Dispose();
        await CarnivalSeed.MutateAsync(_factory, db => db.Carnivals.Add(new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "An older dev carnival",
            Date = new DateOnly(2026, 2, 26),
            RuleVersion = "test",
            OwnerSubject = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        Assert.False(await SetupConfirmedAsync());

        await SeedAsync();

        Assert.True(await SetupConfirmedAsync());
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals.CountAsync()));
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db => db.Students.AnyAsync()));
    }

    [Fact]
    public async Task A_second_start_writes_nothing()
    {
        await SeedAsync();
        var before = await SnapshotAsync();

        await SeedAsync();

        Assert.Equal(before, await SnapshotAsync());
    }

    private Task<(string? Settings, int Students, int Carnivals, int Events, int Batches, int Audit)>
        SnapshotAsync() => CarnivalSeed.QueryAsync(_factory, async db => (
            (await db.SchoolConfigurations.TheSchool().FirstAsync()).SettingsJson,
            await db.Students.CountAsync(),
            await db.Carnivals.CountAsync(),
            await db.CarnivalEvents.CountAsync(),
            await db.ImportBatches.CountAsync(),
            await db.AuditEvents.CountAsync()));
}
