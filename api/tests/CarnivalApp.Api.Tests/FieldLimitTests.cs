using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The free-text and code fields that reach a sized column refuse an over-long
/// value at the door. SQLite ignores <c>HasMaxLength</c>, so nothing in this
/// suite would ever see the PostgreSQL 22001 these guards exist to prevent —
/// which is exactly why the guard, not the column, is what is tested. Left
/// unguarded the overflow is an unhandled <c>DbUpdateException</c>, answered
/// 500 <c>INTERNAL_ERROR</c>, which the offline outbox retries for ever on a
/// payload that can never shorten (ADR-0002/ADR-0011).
/// </summary>
public sealed class FieldLimitTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public FieldLimitTests() => _admin = _factory.CreateClientAs("limits-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private static string Over(int limit) => new('x', limit + 1);

    private async Task<Guid> SeedCarnivalAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Limits",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        await db.SaveChangesAsync();
        return carnival.Id;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    // The reason guard runs before the result lookup, so no result is needed to
    // prove the refusal — and a queued correction never reaches the 500.
    [Fact]
    public async Task A_correction_reason_over_the_limit_is_refused()
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/results/{Guid.CreateVersion7()}/correct",
            new { value = "31.20", reason = Over(FieldLimits.Reason), expectedRevision = 1 });

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task A_carnival_status_reason_over_the_limit_is_refused()
    {
        var id = await SeedCarnivalAsync();
        var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status",
            new { status = "Closed", reason = Over(FieldLimits.Reason) });

        await AssertRefusedAsync(response);
    }

    [Theory]
    [InlineData("houseCode")]
    [InlineData("category")]
    [InlineData("reason")]
    public async Task An_adjustment_field_over_the_limit_is_refused(string field)
    {
        var id = await SeedCarnivalAsync();
        var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments", new
        {
            houseCode = field == "houseCode" ? Over(FieldLimits.HouseCode) : "WATTLE",
            points = 5,
            category = field == "category" ? Over(FieldLimits.Category) : "Best Dressed House",
            reason = field == "reason" ? Over(FieldLimits.Reason) : "Great costumes",
        });

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task An_adjustment_void_reason_over_the_limit_is_refused()
    {
        var id = await SeedCarnivalAsync();
        var created = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            new { houseCode = "WATTLE", points = 5, category = "Best Dressed House", reason = "Costumes" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var adjustmentId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("id").GetGuid();

        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{id}/adjustments/{adjustmentId}/void",
            new { reason = Over(FieldLimits.Reason) });

        await AssertRefusedAsync(response);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("sport")]
    [InlineData("units")]
    public async Task A_definition_field_over_the_limit_is_refused(string field)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = field == "code" ? Over(FieldLimits.Code) : $"LIM-{Guid.NewGuid():N}",
            name = field == "name" ? Over(FieldLimits.Name) : "Freestyle 50m",
            sport = field == "sport" ? Over(FieldLimits.Sport) : "Swimming",
            measureType = "Time",
            units = field == "units" ? Over(FieldLimits.Units) : "s",
        });

        await AssertRefusedAsync(response);
    }

    [Theory]
    [InlineData("subjectId")]
    [InlineData("displayName")]
    public async Task An_official_field_over_the_limit_is_refused(string field)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/officials",
            new
            {
                officials = new[]
                {
                    new
                    {
                        subjectId = field == "subjectId" ? Over(FieldLimits.Subject) : "official-7",
                        displayName = field == "displayName" ? Over(FieldLimits.DisplayName) : "Pat Official",
                    },
                },
            });

        await AssertRefusedAsync(response);
    }

    [Fact]
    public void The_guard_measures_the_stored_form_and_passes_a_value_at_the_limit()
    {
        Assert.Null(FieldLimits.Refuse(null, 10, "reason"));
        Assert.Null(FieldLimits.Refuse(new string('x', 10), 10, "reason"));
        // Trimmed, because trimming is what the endpoints store.
        Assert.Null(FieldLimits.Refuse($"  {new string('x', 10)}  ", 10, "reason"));
        Assert.NotNull(FieldLimits.Refuse(new string('x', 11), 10, "reason"));
    }
}
