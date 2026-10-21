using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The standing record book read (ADR-0028 §5): school-wide, one row per record
/// that STANDS, and gated on viewing rather than administering — a record book
/// is a thing schools put on a wall (ADR-0017 §5).
/// </summary>
public sealed class RecordEndpointsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private static string BoysAndGirls(params string[] bands) =>
        RecordsSeed.Categories(bands, CompetitionCategory.M, CompetitionCategory.F);

    private async Task<List<JsonElement>> BookAsync(HttpClient client, string? sport = null)
    {
        var response = await client.GetAsync(sport is null ? "/api/v1/records" : $"/api/v1/records?sport={sport}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private HttpClient Viewer => _factory.CreateClientAs("records-viewer", "Viewer");

    [Fact]
    public async Task An_empty_book_is_an_empty_list_not_a_refusal()
    {
        // Which is the whole of the first pilot carnival (ruling 1).
        using var clean = new ApiTestFactory();
        Assert.Empty(await BookAsync(clean.CreateClientAs("fresh-viewer", "Viewer")));
    }

    [Fact]
    public async Task A_standing_record_carries_everything_the_book_and_the_badge_need()
    {
        var seeded = await new RecordsSeed()
            .Definition("SW-BOOK", BoysAndGirls("13"))
            .Division("SW-BOOK", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var code = seeded.DefinitionCodes["SW-BOOK"];
        var record = Assert.Single(await BookAsync(Viewer), r => r.GetProperty("eventCode").GetString() == code);

        Assert.Equal(seeded.DefinitionIds["SW-BOOK"].ToString(), record.GetProperty("eventDefinitionId").GetString());
        Assert.Equal("SW-BOOK", record.GetProperty("eventName").GetString());
        Assert.Equal("13", record.GetProperty("ageGroup").GetString());
        Assert.Equal("M", record.GetProperty("category").GetString());
        // The one server-side spelling, and the ONLY category-shaped value here.
        Assert.Equal("13 Boys", record.GetProperty("division").GetString());
        Assert.Equal(31.2m, record.GetProperty("value").GetDecimal());
        Assert.Equal("31.20", record.GetProperty("formattedValue").GetString());
        Assert.False(record.GetProperty("higherBetter").GetBoolean());
        Assert.Equal("Aiden Brook", record.GetProperty("holderDisplayName").GetString());
        Assert.Equal("2027-03-06", record.GetProperty("setOn").GetString());
        Assert.StartsWith("Records Carnival", record.GetProperty("carnivalName").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_record_that_stands_is_in_the_book()
    {
        var first = await new RecordsSeed()
            .Definition("SW-CHAIN", BoysAndGirls("13"))
            .Division("SW-CHAIN", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        var second = await new RecordsSeed()
            .Definition("SW-CHAIN", BoysAndGirls("13"))
            .Division("SW-CHAIN", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 30.9m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, second.CarnivalId)).StatusCode);

        var code = first.DefinitionCodes["SW-CHAIN"];
        var record = Assert.Single(await BookAsync(Viewer), r => r.GetProperty("eventCode").GetString() == code);
        Assert.Equal("Blake Chen", record.GetProperty("holderDisplayName").GetString());
        Assert.Equal("2028-03-04", record.GetProperty("setOn").GetString());
    }

    [Fact]
    public async Task A_non_time_measure_says_higher_is_better()
    {
        var seeded = await new RecordsSeed()
            .Definition("AT-SHOT", BoysAndGirls("13"), measureType: MeasureType.Distance, sport: "Athletics")
            .Division("AT-SHOT", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 8.25m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var code = seeded.DefinitionCodes["AT-SHOT"];
        var record = Assert.Single(await BookAsync(Viewer), r => r.GetProperty("eventCode").GetString() == code);
        // Server-computed from the measure, so the client never re-derives it.
        Assert.True(record.GetProperty("higherBetter").GetBoolean());
        Assert.Equal("8.25 m", record.GetProperty("formattedValue").GetString());
    }

    [Fact]
    public async Task The_sport_filter_narrows_the_school_wide_book()
    {
        var swimming = await new RecordsSeed()
            .Definition("SW-SPORT", BoysAndGirls("13"))
            .Division("SW-SPORT", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, swimming.CarnivalId)).StatusCode);

        var athletics = await new RecordsSeed()
            .Definition("AT-SPORT", BoysAndGirls("13"), measureType: MeasureType.Distance, sport: "Athletics")
            .Division("AT-SPORT", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 9.10m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, athletics.CarnivalId)).StatusCode);

        var codes = (await BookAsync(Viewer, "athletics")).Select(r => r.GetProperty("eventCode").GetString()).ToList();
        Assert.Contains(athletics.DefinitionCodes["AT-SPORT"], codes);
        Assert.DoesNotContain(swimming.DefinitionCodes["SW-SPORT"], codes);
    }

    [Fact]
    public async Task Unauthenticated_reads_are_refused()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/records");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
