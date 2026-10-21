using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// GET /api/v1/branding (ADR-0040 §3) and the startup gate behind it. The read
/// is the one anonymous route under /api/v1 outside /auth/: the sign-in screen
/// paints the school's chrome before a session exists.
/// </summary>
public sealed class BrandingEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static async Task<JsonElement> BrandingAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/branding");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Branding_answers_an_unauthenticated_caller()
    {
        var branding = await BrandingAsync(factory.CreateClient());
        Assert.Equal("Test Synthetic College", branding.GetProperty("schoolName").GetString());
    }

    [Fact]
    public async Task An_unconfigured_colour_is_null_rather_than_a_default()
    {
        // The default palette is complete without one (ADR-0040 §2), so the
        // client must be able to tell "no brand colour" from "this colour".
        var branding = await BrandingAsync(factory.CreateClient());
        Assert.Equal(JsonValueKind.Null, branding.GetProperty("brandColour").ValueKind);
    }

    [Fact]
    public async Task A_configured_colour_is_served_normalised()
    {
        using var branded = new ApiTestFactory().With(BrandColour.ConfigKey, "#1F3A5F");
        var branding = await BrandingAsync(branded.CreateClient());
        Assert.Equal("#1f3a5f", branding.GetProperty("brandColour").GetString());
    }

    [Fact]
    public async Task Branding_carries_the_typed_response_shape()
    {
        var branding = await factory.CreateClient()
            .GetFromJsonAsync<BrandingProjection>("/api/v1/branding");
        Assert.Equal("Test Synthetic College", branding!.SchoolName);
        Assert.Null(branding.BrandColour);
    }

    /// <summary>
    /// The fail-fast half: a configured colour the client could not write text
    /// on stops the host, the way a missing connection string does. Malformed is
    /// the case that can actually occur — see BrandColourTests for why no
    /// well-formed colour can fail the contrast rule in both directions.
    /// </summary>
    [Fact]
    public void A_malformed_brand_colour_refuses_to_start()
    {
        using var misconfigured = new ApiTestFactory().With(BrandColour.ConfigKey, "navy-ish");
        var error = Record.Exception(() => misconfigured.CreateClient());
        Assert.NotNull(error);
        Assert.Contains(BrandColour.ConfigKey, error.ToString());
        Assert.Contains("navy-ish", error.ToString());
    }

    private sealed record BrandingProjection(string SchoolName, string? BrandColour);
}
