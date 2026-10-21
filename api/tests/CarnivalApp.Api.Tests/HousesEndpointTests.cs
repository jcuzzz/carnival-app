using System.Net;
using System.Text.Json;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// GET /api/v1/school/houses: the ADR-0008 code array, and the ADR-0044 §3
/// houseMarks that grew beside it without disturbing it.
/// </summary>
public sealed class HousesEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static async Task<JsonElement> HousesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/school/houses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<string?> MarkColours(JsonElement houses) =>
        houses.GetProperty("houseMarks").EnumerateArray()
            .Select(mark => mark.GetProperty("colour").GetString()).ToList();

    [Fact]
    public async Task Unconfigured_colours_leave_every_mark_text_only()
    {
        var houses = await HousesAsync(factory.CreateClientAs("houses-reader", "Viewer"));
        Assert.Equal(
            ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"],
            houses.GetProperty("houses").EnumerateArray().Select(h => h.GetString()));
        Assert.All(MarkColours(houses), Assert.Null);
        Assert.Equal(
            houses.GetProperty("houses").EnumerateArray().Select(h => h.GetString()),
            houses.GetProperty("houseMarks").EnumerateArray()
                .Select(mark => mark.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Configured_colours_are_served_beside_the_unchanged_code_array()
    {
        using var coloured = new ApiTestFactory()
            .With("School:HouseColours:BANKSIA", "#3346C0")
            .With("School:HouseColours:WATTLE", "#2e9e52")
            .With("School:HouseColours:GREVILLEA", "#c962d3");
        var houses = await HousesAsync(coloured.CreateClientAs("houses-reader", "Viewer"));

        Assert.Equal(
            ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"],
            houses.GetProperty("houses").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(new string?[] { "#3346c0", "#2e9e52", null, "#c962d3" }, MarkColours(houses));
    }

    /// <summary>A refused colour degrades that house, and nothing else (ADR-0044 §2).
    /// A pale one is not refused — there is no contrast floor.</summary>
    [Fact]
    public async Task A_refused_colour_serves_the_house_without_stopping_the_api()
    {
        using var coloured = new ApiTestFactory()
            .With("School:HouseColours:BANKSIA", "not-a-colour")
            .With("School:HouseColours:WATTLE", "#ffff00")
            .With("School:HouseColours:MOUNTAIN ASH", "#2e9e52")
            .With("School:HouseColours:GREVILLEA", "#2E9E52");
        var houses = await HousesAsync(coloured.CreateClientAs("houses-reader", "Viewer"));

        Assert.Equal(new string?[] { null, "#ffff00", "#2e9e52", null }, MarkColours(houses));
    }
}
