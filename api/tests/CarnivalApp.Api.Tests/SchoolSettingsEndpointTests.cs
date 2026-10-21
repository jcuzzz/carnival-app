using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// GET/PUT /api/v1/school/settings (ADR-0045): who may read and write school
/// identity, what the write refuses, and the consumers repainting from it
/// without a restart.
/// </summary>
public sealed class SchoolSettingsEndpointTests : IDisposable
{
    private const string Path = "/api/v1/school/settings";

    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public SchoolSettingsEndpointTests() => _admin = _factory.CreateClientAs("sys-1", "SystemAdministrator");

    public void Dispose() => _factory.Dispose();

    private static object House(string code, string? name = null, string? colour = null) =>
        new { code, name, colour };

    private static object Body(
        string? name = "Test Synthetic College",
        string? brandColour = null,
        object[]? houses = null,
        DateTimeOffset? updatedAtUtc = null) => new
        {
            name,
            brandColour,
            houses = houses ?? new[] { House("BANKSIA"), House("WATTLE"), House("MOUNTAIN ASH"), House("GREVILLEA") },
            updatedAtUtc,
        };

    private async Task<JsonElement> GetAsync()
    {
        var response = await _admin.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<DateTimeOffset?> StampAsync()
    {
        var settings = await GetAsync();
        return settings.GetProperty("updatedAtUtc").ValueKind == JsonValueKind.Null
            ? null
            : settings.GetProperty("updatedAtUtc").GetDateTimeOffset();
    }

    /// <summary>Rewrite the stored row directly, then drop the provider's cache.</summary>
    private async Task MutateRowAsync(Action<SchoolConfiguration> mutate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var row = await db.SchoolConfigurations.SingleAsync();
        mutate(row);
        await db.SaveChangesAsync();
        _factory.Services.GetRequiredService<SchoolSettingsProvider>().Invalidate();
    }

    /// <summary>The refusal code from a PUT built on the CURRENT stamp.</summary>
    private async Task<string> RefusalAsync(
        HttpStatusCode expected,
        string? name = "Test Synthetic College",
        string? brandColour = null,
        object[]? houses = null)
    {
        var response = await _admin.PutAsJsonAsync(
            Path, Body(name, brandColour, houses, await StampAsync()));
        Assert.Equal(expected, response.StatusCode);
        return await response.ErrorCodeAsync();
    }

    // ---- Read ------------------------------------------------------------

    [Fact]
    public async Task Get_serves_the_seeded_row()
    {
        var settings = await GetAsync();
        Assert.Equal("Test Synthetic College", settings.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("brandColour").ValueKind);
        Assert.Equal(
            ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"],
            settings.GetProperty("houses").EnumerateArray().Select(h => h.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("updatedAtUtc").ValueKind);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(Path)).StatusCode);
        var write = await _factory.CreateClient().PutAsJsonAsync(Path, Body());
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    /// <summary>ADR-0045 §2: SystemAdministrator alone, on the imports:view precedent.</summary>
    [Theory]
    [InlineData("CarnivalAdministrator")]
    [InlineData("Recorder")]
    [InlineData("Viewer")]
    public async Task Every_other_role_gets_403(string role)
    {
        var client = _factory.CreateClientAs($"other-{role}", role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(Path, Body())).StatusCode);
    }

    // ---- Write -----------------------------------------------------------

    [Fact]
    public async Task A_save_persists_audits_and_repaints_the_consumers_without_a_restart()
    {
        var response = await _admin.PutAsJsonAsync(Path, Body(
            "Renamed College",
            "#1F3A5F",
            [House("BANKSIA", "Banksia", "#3346C0"), House("WATTLE", "Wattle"), House("GREVILLEA", colour: "#2e9e52")]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed College", saved.GetProperty("name").GetString());
        Assert.Equal("#1f3a5f", saved.GetProperty("brandColour").GetString());
        var stamp = saved.GetProperty("updatedAtUtc").GetDateTimeOffset();
        Assert.Equal(stamp, await StampAsync());

        // The houses as stored: an omitted display name falls back to the code,
        // and the colours normalise.
        var houses = (await GetAsync()).GetProperty("houses").EnumerateArray().ToList();
        Assert.Equal(["BANKSIA", "WATTLE", "GREVILLEA"], houses.Select(h => h.GetProperty("code").GetString()));
        Assert.Equal(["Banksia", "Wattle", "GREVILLEA"], houses.Select(h => h.GetProperty("name").GetString()));
        Assert.Equal(
            new string?[] { "#3346c0", null, "#2e9e52" },
            houses.Select(h => h.GetProperty("colour").GetString()));

        // ADR-0045 §5: the cached provider is invalidated, so the anonymous
        // sign-in read and the house marks answer the new values immediately.
        var branding = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/branding");
        Assert.Equal("Renamed College", branding.GetProperty("schoolName").GetString());
        Assert.Equal("#1f3a5f", branding.GetProperty("brandColour").GetString());

        var marks = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/school/houses");
        Assert.Equal(
            ["BANKSIA", "WATTLE", "GREVILLEA"],
            marks.GetProperty("houses").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(
            ["Banksia", "Wattle", "GREVILLEA"],
            marks.GetProperty("houseMarks").EnumerateArray().Select(h => h.GetProperty("name").GetString()));

        var version = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/version");
        Assert.Equal("Renamed College", version.GetProperty("school").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var audit = await db.AuditEvents.SingleAsync(a => a.Action == AuditActions.SchoolSettings);
        Assert.Equal("sys-1", audit.ActorSubject);
        Assert.Equal(nameof(SchoolConfiguration), audit.EntityType);
        Assert.Contains("Test Synthetic College", audit.PriorValuesJson);
        Assert.Contains("Renamed College", audit.NewValuesJson);
    }

    [Fact]
    public async Task A_second_save_advances_the_stamp()
    {
        var first = await _admin.PutAsJsonAsync(Path, Body("One", updatedAtUtc: null));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstStamp = await StampAsync();

        // The stamp truncates to the millisecond, so the second save has to land
        // in a later one for ">" to mean anything.
        await Task.Delay(20);
        var second = await _admin.PutAsJsonAsync(Path, Body("Two", updatedAtUtc: firstStamp));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(await StampAsync() > firstStamp);
        Assert.Equal("Two", (await GetAsync()).GetProperty("name").GetString());
    }

    // ---- Refusals --------------------------------------------------------

    [Fact]
    public async Task A_stamp_the_row_never_carried_is_refused()
    {
        var response = await _admin.PutAsJsonAsync(
            Path, Body("Stale", updatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.SettingsStale);
    }

    /// <summary>Two administrators on the same render: the second save loses.</summary>
    [Fact]
    public async Task A_concurrent_second_save_with_the_old_stamp_is_refused()
    {
        var rendered = await StampAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PutAsJsonAsync(Path, Body("First in", updatedAtUtc: rendered))).StatusCode);

        var second = await _admin.PutAsJsonAsync(Path, Body("Second in", updatedAtUtc: rendered));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await second.AssertErrorAsync(ErrorCodes.SettingsStale);
        Assert.Equal("First in", (await GetAsync()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(null, ErrorCodes.NameRequired)]
    [InlineData("   ", ErrorCodes.NameRequired)]
    public async Task A_missing_name_is_refused(string? name, string expected) =>
        Assert.Equal(expected, await RefusalAsync(HttpStatusCode.UnprocessableEntity, name));

    [Fact]
    public async Task A_name_over_two_hundred_characters_is_refused() =>
        Assert.Equal(ErrorCodes.NameTooLong,
            await RefusalAsync(HttpStatusCode.UnprocessableEntity, new string('x', 201)));

    [Fact]
    public async Task An_unparseable_brand_colour_is_refused() =>
        Assert.Equal(ErrorCodes.BrandColourInvalid,
            await RefusalAsync(HttpStatusCode.UnprocessableEntity, brandColour: "navy-ish"));

    /// <summary>
    /// The ADR-0040 §3 contrast branch (BRAND_COLOUR_UNREADABLE) is defensive and
    /// nothing well-formed reaches it: every sRGB colour clears 4.5:1 against
    /// black or white, worst case the mid-grey below (BrandColourTests). So the
    /// assertion is that the worst case is ACCEPTED — a refusal here would mean
    /// the threshold moved without ADR-0040 moving with it.
    /// </summary>
    [Fact]
    public async Task The_worst_case_brand_colour_is_still_accepted()
    {
        var response = await _admin.PutAsJsonAsync(
            Path, Body(brandColour: "#767676", updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("#767676", (await GetAsync()).GetProperty("brandColour").GetString());
    }

    [Fact]
    public async Task No_houses_at_all_is_refused() =>
        Assert.Equal(ErrorCodes.HousesRequired,
            await RefusalAsync(HttpStatusCode.UnprocessableEntity, houses: []));

    [Theory]
    [InlineData("", ErrorCodes.HouseCodeRequired)]
    [InlineData("  ", ErrorCodes.HouseCodeRequired)]
    public async Task A_house_without_a_code_is_refused(string code, string expected) =>
        Assert.Equal(expected, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity, houses: [House("BANKSIA"), House(code)]));

    [Fact]
    public async Task A_house_code_over_thirty_two_characters_is_refused() =>
        Assert.Equal(ErrorCodes.HouseCodeTooLong, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity, houses: [House(new string('x', 33))]));

    [Fact]
    public async Task A_repeated_house_code_is_refused_case_insensitively() =>
        Assert.Equal(ErrorCodes.HouseCodeDuplicate, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity, houses: [House("BANKSIA"), House("banksia")]));

    [Fact]
    public async Task A_house_name_over_sixty_characters_is_refused() =>
        Assert.Equal(ErrorCodes.HouseNameTooLong, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity, houses: [House("BANKSIA", new string('x', 61))]));

    [Fact]
    public async Task An_unparseable_house_colour_is_refused() =>
        Assert.Equal(ErrorCodes.HouseColourInvalid, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity, houses: [House("BANKSIA", colour: "greenish")]));

    /// <summary>Owner ruling 2026-09-03: no contrast floor — the yellow a school
    /// actually uses saves, and the swatch's border keeps it visible.</summary>
    [Theory]
    [InlineData("#ffff00")]
    [InlineData("#F5F5F5")]
    public async Task A_house_colour_with_low_contrast_on_white_saves(string colour)
    {
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses:
            [
                House("BANKSIA", colour: colour), House("WATTLE"), House("MOUNTAIN ASH"), House("GREVILLEA"),
            ],
            updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var houses = (await GetAsync()).GetProperty("houses").EnumerateArray().ToList();
        Assert.Equal(colour.ToLowerInvariant(), houses[0].GetProperty("colour").GetString());
    }


    [Fact]
    public async Task Two_houses_sharing_a_colour_are_refused() =>
        Assert.Equal(ErrorCodes.HouseColourDuplicate, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity,
            houses: [House("BANKSIA", colour: "#3346c0"), House("WATTLE", colour: "#3346C0")]));

    /// <summary>Shorthand and long form are one colour.</summary>
    [Fact]
    public async Task Two_houses_sharing_a_colour_in_different_notations_are_refused() =>
        Assert.Equal(ErrorCodes.HouseColourDuplicate, await RefusalAsync(
            HttpStatusCode.UnprocessableEntity,
            houses: [House("BANKSIA", colour: "#0f0"), House("WATTLE", colour: "#00FF00")]));

    // ---- Houses that data points at ---------------------------------------

    private async Task SeedRosterHouseAsync(string houseCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            SourceFileName = "school-settings.csv",
            SourceFileHash = "hash",
            MappingVersion = "test",
            ImportedBySubject = "seeder",
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = ImportBatchStatus.Promoted,
        };
        db.ImportBatches.Add(batch);
        var student = new Student
        {
            Id = Guid.CreateVersion7(),
            ExternalStudentId = $"S{Guid.NewGuid():N}"[..8],
            Surname = "Synthetic",
            FirstName = "Row",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Students.Add(student);
        db.RosterSnapshots.Add(new RosterSnapshot
        {
            Id = Guid.CreateVersion7(),
            StudentId = student.Id,
            SchoolYear = 2027,
            YearLevel = 7,
            HouseCode = houseCode,
            CompetitionAgeGroup = "13",
            CompetitionCategory = CompetitionCategory.M,
            ImportBatchId = batch.Id,
            EffectiveDate = new DateOnly(2027, 1, 30),
            IsCurrent = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>ADR-0045 §6: the screen disables Remove from this flag alone.</summary>
    [Fact]
    public async Task Get_marks_a_house_stored_data_points_at_as_in_use()
    {
        await SeedRosterHouseAsync("WATTLE");

        var houses = (await GetAsync()).GetProperty("houses").EnumerateArray()
            .ToDictionary(h => h.GetProperty("code").GetString()!, h => h.GetProperty("inUse").GetBoolean());
        Assert.True(houses["WATTLE"]);
        Assert.False(houses["BANKSIA"]);
    }

    [Fact]
    public async Task A_house_a_roster_row_points_at_cannot_be_dropped()
    {
        await SeedRosterHouseAsync("WATTLE");
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("BANKSIA"), House("MOUNTAIN ASH"), House("GREVILLEA")],
            updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseInUse);
        Assert.Contains("WATTLE", await response.ErrorDetailAsync());
    }

    /// <summary>Renaming the CODE is the same removal — the rows would orphan.</summary>
    [Fact]
    public async Task A_house_a_roster_row_points_at_cannot_be_recoded()
    {
        await SeedRosterHouseAsync("WATTLE");
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("BANKSIA"), House("WAAH"), House("MOUNTAIN ASH"), House("GREVILLEA")],
            updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseInUse);
    }

    [Fact]
    public async Task A_used_house_may_still_be_renamed_recoloured_and_joined_by_a_new_one()
    {
        await SeedRosterHouseAsync("WATTLE");
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses:
            [
                House("BANKSIA"), House("WATTLE", "Wattle the Crow", "#2e9e52"), House("MOUNTAIN ASH"),
                House("GREVILLEA"), House("NEW"),
            ],
            updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var houses = (await GetAsync()).GetProperty("houses").EnumerateArray().ToList();
        Assert.Equal(5, houses.Count);
        Assert.Equal("Wattle the Crow", houses[1].GetProperty("name").GetString());
    }

    /// <summary>The refusal names the field, so the form can mark it (ADR-0045 §3).</summary>
    [Fact]
    public async Task A_refusal_names_the_field_it_is_about()
    {
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("BANKSIA"), House("WATTLE", colour: "greenish")],
            updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("houses[1].colour", (await response.ErrorContextAsync("field")).GetString());
    }

    /// <summary>
    /// An unreadable HousesJson reads as NO houses; the in-use guard must not
    /// disappear with it, or the blob becomes a way to orphan every row.
    /// </summary>
    [Fact]
    public async Task An_unreadable_house_list_still_refuses_a_save_that_would_orphan_rows()
    {
        await SeedRosterHouseAsync("WATTLE");
        await MutateRowAsync(row => row.HousesJson = "not json at all");

        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("BANKSIA")], updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseInUse);
        Assert.Contains("WATTLE", await response.ErrorDetailAsync());
    }

    /// <summary>Codes are stored upper-case, so re-typing one in lower case is not a rename.</summary>
    [Fact]
    public async Task A_case_only_edit_of_a_used_house_is_the_same_house()
    {
        await SeedRosterHouseAsync("WATTLE");
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("banksia"), House("wattle", "Wattle"), House("mountain ash"), House("grevillea")],
            updatedAtUtc: await StampAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var houses = (await GetAsync()).GetProperty("houses").EnumerateArray().ToList();
        Assert.Equal(
            ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"], houses.Select(h => h.GetProperty("code").GetString()));
        Assert.Equal("Wattle", houses[1].GetProperty("name").GetString());
    }

    /// <summary>The blob belongs to more than the brand colour (a future logo).</summary>
    [Fact]
    public async Task A_save_keeps_branding_keys_it_does_not_own()
    {
        await MutateRowAsync(row => row.BrandingJson = """{"logo":"x"}""");

        var response = await _admin.PutAsJsonAsync(
            Path, Body(brandColour: "#1F3A5F", updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await CarnivalSeed.QueryAsync(_factory, db => db.SchoolConfigurations
            .AsNoTracking().Select(row => row.BrandingJson).SingleAsync());
        Assert.Contains("\"logo\":\"x\"", stored!);
        Assert.Equal("#1f3a5f", (await GetAsync()).GetProperty("brandColour").GetString());
    }

    /// <summary>
    /// The stale check reads the row before the save; a competitor that commits
    /// in between passes it. UpdatedAtUtc is the EF concurrency token, so the
    /// UPDATE itself refuses — one winner, one 409, one audit row.
    /// </summary>
    [Fact]
    public async Task Two_saves_from_one_render_leave_one_winner_and_one_stale_refusal()
    {
        var rendered = await StampAsync();
        _factory.Race.ArmOnce("UPDATE \"SchoolConfigurations\"", transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            var row = db.SchoolConfigurations.Single();
            row.Name = "Competitor";
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.SaveChanges();
        });

        var lost = await _admin.PutAsJsonAsync(Path, Body("Mine", updatedAtUtc: rendered));
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        await lost.AssertErrorAsync(ErrorCodes.SettingsStale);

        // The competing write shared the failed save's transaction and went down
        // with it (see RaceInterceptor), so the same render retries and wins.
        var won = await _admin.PutAsJsonAsync(Path, Body("Mine", updatedAtUtc: rendered));
        Assert.Equal(HttpStatusCode.OK, won.StatusCode);
        Assert.Equal("Mine", (await GetAsync()).GetProperty("name").GetString());

        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.SchoolSettings)));
    }

    /// <summary>
    /// A relay team's own house (Entry.HouseCode) — pinned with a snapshot house
    /// the save KEEPS, so nothing but that column can produce the refusal.
    /// </summary>
    [Fact]
    public async Task A_house_a_team_entry_points_at_cannot_be_dropped()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await AddHouseAsync("TEAMS");
        await CarnivalSeed.MutateAsync(_factory, db => db.Entries.Add(new Entry
        {
            Id = Guid.CreateVersion7(),
            CarnivalEventId = seeded.EventId,
            HouseCode = "TEAMS",
            SnapshotDisplayName = "TEAMS relay team",
            SnapshotYearLevel = 7,
            SnapshotHouseCode = "BANKSIA",
            SnapshotAgeGroup = seeded.PrimaryAgeGroup,
            SnapshotCategory = CompetitionCategory.M,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBySubject = "seeder",
        }));

        var response = await _admin.PutAsJsonAsync(Path, Body(updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseInUse);
        Assert.Contains("TEAMS", await response.ErrorDetailAsync());
    }

    /// <summary>House-point adjustments reference a house and no student at all (ADR-0005).</summary>
    [Fact]
    public async Task A_house_an_adjustment_points_at_cannot_be_dropped()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await AddHouseAsync("SPIRIT");
        await CarnivalSeed.MutateAsync(_factory, db => db.HousePointsAdjustments.Add(new HousePointsAdjustment
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = seeded.CarnivalId,
            HouseCode = "SPIRIT",
            Points = 5,
            Category = "Best Dressed House",
            Reason = "Synthetic award",
            RecordedBySubject = "seeder",
            RecordedAtUtc = DateTimeOffset.UtcNow,
        }));

        var response = await _admin.PutAsJsonAsync(Path, Body(updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseInUse);
        Assert.Contains("SPIRIT", await response.ErrorDetailAsync());
    }

    /// <summary>Adds a fifth house nothing references yet (houses may be added freely).</summary>
    private async Task AddHouseAsync(string code)
    {
        var response = await _admin.PutAsJsonAsync(Path, Body(
            houses: [House("BANKSIA"), House("WATTLE"), House("MOUNTAIN ASH"), House("GREVILLEA"), House(code)],
            updatedAtUtc: await StampAsync()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
