using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// K3: the string-typed configuration blobs are tolerant on READ (a bad blob
/// degrades one field, it never fails the request that merely lists the row) and
/// checked on WRITE (a client learns immediately that its blob is unusable,
/// rather than having it silently swallowed later).
/// </summary>
public sealed class JsonBlobTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;

    public JsonBlobTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-blobs", "CarnivalAdministrator");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("""["Backstroke","Freestyle"]""", "Backstroke,Freestyle")]
    [InlineData("""["Backstroke",""", "")]        // truncated blob
    [InlineData("""{"legs":["Backstroke"]}""", "")] // right JSON, wrong shape
    [InlineData("""[1,2]""", "")]                  // array of the wrong element type
    public void StringList_never_throws(string? json, string? expected)
    {
        var parsed = JsonBlob.StringList(json);
        Assert.Equal(expected, parsed is null ? null : string.Join(',', parsed));
    }

    [Fact]
    public async Task A_malformed_tags_blob_degrades_to_empty_and_still_lists_the_event()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        // Written straight to the database: this is history/DBA-shaped damage,
        // exactly what the write-side validation cannot prevent.
        await CarnivalSeed.QueryAsync(_factory, async db =>
        {
            var definition = await db.EventDefinitions.SingleAsync(d => d.Id == seeded.DefinitionId);
            definition.TagsJson = "{not json";
            definition.LegsJson = """["Backstroke",""";
            return await db.SaveChangesAsync();
        });

        var response = await _admin.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = (await Json(response)).EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == seeded.EventId);
        Assert.Empty(row.GetProperty("tags").EnumerateArray());
        Assert.Empty(row.GetProperty("legs").EnumerateArray());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""["12","13"]""")] // an array where the shape is an object
    [InlineData("   ")]
    public async Task A_malformed_definition_blob_is_refused_on_create(string blob)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"SW-BAD-{Guid.CreateVersion7():N}", name = "Bad", sport = "Swimming",
            measureType = "Time", units = "s",
            eligibilityJson = blob,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_malformed_definition_blob_is_refused_on_update_and_changes_nothing()
    {
        var code = $"SW-BLOB-{Guid.CreateVersion7():N}";
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code, name = "Blobs", sport = "Swimming", measureType = "Time", units = "s",
            recordCategoriesJson = """{"ageGroups":["12"]}""",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code, name = "Renamed", sport = "Swimming", measureType = "Time", units = "s",
            eligibilityJson = "{\"maxEntries\":",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await put.AssertErrorAsync(ErrorCodes.ValidationFailed);

        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id));
        Assert.Equal("Blobs", stored.Name); // refused before any assignment
        Assert.Null(stored.EligibilityJson);
    }

    [Fact]
    public async Task The_dropped_heat_lane_blob_is_no_longer_part_of_the_event_shape()
    {
        // ADR-0015 §6 dropped CarnivalEvent.HeatLaneSettingsJson: the lane count
        // is venue configuration on the carnival's settings and there is no
        // seeding to configure. This replaces the blob-validation test that used
        // to live here. A client still sending the field must not be refused —
        // an unknown property is ignored, exactly as it is everywhere else — and
        // the field must not come back on the event.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var definition = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"SW-HEAT-{Guid.CreateVersion7():N}", name = "Heats", sport = "Swimming",
            measureType = "Time", units = "s",
        });
        var definitionId = (await Json(definition)).GetProperty("id").GetGuid();

        var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events", new
        {
            eventDefinitionId = definitionId,
            ageGroup = "OPEN", // the all-ages scope (ADR-0058 §1)
            heatLaneSettingsJson = "{lanes: 8}", // once a 400; now simply not a field
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False((await Json(response)).TryGetProperty("heatLaneSettingsJson", out _));
    }

    [Fact]
    public async Task A_team_entry_survives_an_unreadable_legs_blob()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await CarnivalSeed.QueryAsync(_factory, async db =>
        {
            var definition = await db.EventDefinitions.SingleAsync(d => d.Id == seeded.DefinitionId);
            definition.Format = EventFormat.Team;
            definition.TeamSize = 2;
            definition.LegsJson = "{oops";
            return await db.SaveChangesAsync();
        });

        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/team-entries",
            new { houseCode = "WATTLE", studentIds = new[] { seeded.SecondEligibleBoyId, seeded.GirlId } });

        // The refusal is the house/division rule, not a JSON crash: the girl is
        // category F in an M event, so the SECOND leg is what stops this.
        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CategoryIneligible);
    }
}
