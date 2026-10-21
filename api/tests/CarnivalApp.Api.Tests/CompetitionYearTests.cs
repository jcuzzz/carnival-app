using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>ADR-0060 §2: the year an import derives ages for, when the upload names none.</summary>
public sealed class CompetitionYearDefaultTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SportsDbContext _db;

    public CompetitionYearDefaultTests()
    {
        _connection.Open();
        _db = new SportsDbContext(
            new DbContextOptionsBuilder<SportsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task CarnivalOnAsync(int year, int month, int day)
    {
        _db.Carnivals.Add(new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = $"Carnival {year}-{month}-{day}",
            Date = new DateOnly(year, month, day),
            RuleVersion = "test",
            OwnerSubject = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private Task<int> DefaultOnAsync(int year, int month, int day) =>
        CompetitionYears.DefaultAsync(_db, new DateOnly(year, month, day), CancellationToken.None);

    [Fact]
    public async Task With_no_carnival_it_is_the_current_year()
        => Assert.Equal(2026, await DefaultOnAsync(2026, 11, 15));

    [Fact]
    public async Task A_past_carnival_does_not_hold_the_year_back()
    {
        await CarnivalOnAsync(2026, 2, 26);
        Assert.Equal(2027, await DefaultOnAsync(2027, 1, 20));
    }

    [Fact]
    public async Task A_november_import_ahead_of_a_february_carnival_takes_the_carnivals_year()
    {
        await CarnivalOnAsync(2026, 2, 26);
        await CarnivalOnAsync(2027, 2, 25);
        Assert.Equal(2027, await DefaultOnAsync(2026, 11, 15));
    }

    [Fact]
    public async Task The_earliest_carnival_from_today_on_decides()
    {
        await CarnivalOnAsync(2027, 2, 25);
        await CarnivalOnAsync(2026, 11, 15); // today counts
        Assert.Equal(2026, await DefaultOnAsync(2026, 11, 15));
    }

    [Fact]
    public async Task A_carnival_beyond_the_accepted_range_is_not_a_default()
    {
        await CarnivalOnAsync(2028, 3, 1);
        Assert.Equal(2026, await DefaultOnAsync(2026, 11, 15));
    }

    [Theory]
    [InlineData(2025, true)]
    [InlineData(2026, true)]
    [InlineData(2027, true)]
    [InlineData(2024, false)]
    [InlineData(2028, false)]
    public void Only_a_year_either_side_of_the_current_one_is_accepted(int year, bool accepted)
        => Assert.Equal(accepted, CompetitionYears.InRange(year, new DateOnly(2026, 11, 15)));

    [Fact]
    public void Today_is_the_schools_date_not_utcs()
    {
        // 14:00 UTC on 31 December is already 1 January in Melbourne.
        var instant = new DateTimeOffset(2026, 12, 31, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2027, 1, 1), CompetitionYears.SchoolToday(Fixtures.Config(), instant));
        Assert.Equal(new DateOnly(2026, 12, 31), CompetitionYears.SchoolToday(
            Fixtures.Config(("School:Timezone", "Not/AZone")), instant));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_timezone_falls_back_to_the_default_zone_not_utc(string configured)
    {
        var instant = new DateTimeOffset(2026, 12, 31, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2027, 1, 1), CompetitionYears.SchoolToday(
            Fixtures.Config(("School:Timezone", configured)), instant));
    }
}

/// <summary>
/// ADR-0060 §2-§3 over HTTP. Each test owns its factory: carnivals anywhere in the database move
/// the default.
/// </summary>
public sealed class CompetitionYearUploadTests
{
    private static readonly DateOnly Today =
        CompetitionYears.SchoolToday(Fixtures.Config(), DateTimeOffset.UtcNow);

    private static HttpClient Admin(ApiTestFactory factory) =>
        factory.CreateClientAs("year-admin", "SystemAdministrator");

    private static MultipartFormDataContent Upload(string? competitionYear)
    {
        const string csv =
            "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n" +
            "Y1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n";
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "roster.csv" },
        };
        if (competitionYear is not null)
            form.Add(new StringContent(competitionYear), "competitionYear");
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> StageAndReconcileAsync(HttpClient admin, string? year)
    {
        using var form = Upload(year);
        var staged = await admin.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, staged.StatusCode);
        var stage = await Json(staged);
        var report = await Json(
            await admin.GetAsync($"/api/v1/imports/{stage.GetProperty("id").GetGuid()}/reconciliation"));
        Assert.Equal(stage.GetProperty("competitionYear").GetInt32(),
            report.GetProperty("competitionYear").GetInt32());
        return report;
    }

    private static string AgeOf(JsonElement report) =>
        report.GetProperty("rows")[0].GetProperty("competitionAgeGroup").GetString()!;

    [Fact]
    public async Task A_named_year_is_the_one_ages_are_derived_for()
    {
        using var factory = new ApiTestFactory();
        var year = Today.Year + 1;

        var report = await StageAndReconcileAsync(Admin(factory), year.ToString());

        Assert.Equal(year, report.GetProperty("competitionYear").GetInt32());
        Assert.Equal("12-31", report.GetProperty("cutOff").GetString());
        Assert.Equal((year - 2013).ToString(), AgeOf(report));
    }

    [Fact]
    public async Task With_no_year_and_no_carnival_it_is_the_current_year()
    {
        using var factory = new ApiTestFactory();
        var report = await StageAndReconcileAsync(Admin(factory), null);
        Assert.Equal(Today.Year, report.GetProperty("competitionYear").GetInt32());
        Assert.Equal((Today.Year - 2013).ToString(), AgeOf(report));
    }

    [Fact]
    public async Task With_no_year_the_next_carnival_decides_and_the_defaults_read_agrees()
    {
        using var factory = new ApiTestFactory();
        var next = Today.Year + 1;
        await CarnivalSeed.MutateAsync(factory, db => db.Carnivals.Add(new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Next year's carnival",
            Date = new DateOnly(next, 2, 25),
            RuleVersion = "test",
            OwnerSubject = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var admin = Admin(factory);

        var defaults = await Json(await admin.GetAsync("/api/v1/imports/defaults"));
        Assert.Equal(next, defaults.GetProperty("competitionYear").GetInt32());
        Assert.Equal(Today.Year - 1, defaults.GetProperty("earliestYear").GetInt32());
        Assert.Equal(Today.Year + 1, defaults.GetProperty("latestYear").GetInt32());
        Assert.Equal("12-31", defaults.GetProperty("cutOff").GetString());

        Assert.Equal(next, (await StageAndReconcileAsync(admin, null))
            .GetProperty("competitionYear").GetInt32());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task A_year_more_than_one_from_now_is_refused_and_nothing_is_staged(int offset)
    {
        using var factory = new ApiTestFactory();
        using var form = Upload((Today.Year + offset).ToString());

        var response = await Admin(factory).PostAsync("/api/v1/imports", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ImportCompetitionYearInvalid);
        Assert.Equal(Today.Year - 1, (await response.ErrorContextAsync("earliestYear")).GetInt32());
        Assert.Equal(Today.Year + 1, (await response.ErrorContextAsync("latestYear")).GetInt32());
        Assert.Equal(0, await CarnivalSeed.QueryAsync(factory, db => db.ImportBatches.CountAsync()));
    }

    [Theory]
    [InlineData("next year")]
    [InlineData("-2026")]
    [InlineData("2026.0")]
    public async Task A_year_that_is_not_a_year_is_refused(string year)
    {
        using var factory = new ApiTestFactory();
        using var form = Upload(year);
        var response = await Admin(factory).PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ImportCompetitionYearInvalid);
    }

    [Fact]
    public async Task A_carnival_administrator_cannot_read_the_defaults()
    {
        using var factory = new ApiTestFactory();
        var response = await factory.CreateClientAs("year-carnival-admin", "CarnivalAdministrator")
            .GetAsync("/api/v1/imports/defaults");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
