using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0036: the deck's results read carries each entry's place in its own
/// (event, heat) field. It is the results book's Place column, computed by the
/// shared ranking (<c>HeatPlacings</c>), so a ribbon called at the deck and a
/// ribbon printed in the book cannot disagree — which is what the agreement
/// test at the bottom pins.
/// </summary>
public sealed class ResultPlacingTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private HttpClient Recorder => _factory.CreateClientAs("place-recorder", "Recorder");
    private HttpClient Admin => _factory.CreateClientAs("place-admin", "CarnivalAdministrator");

    // ---- Seed -------------------------------------------------------------

    /// <summary>One swim: heat, who, what happened.</summary>
    private sealed record Swim(int? Heat, string Name, ResultStatus Status, decimal? Value);

    private sealed record EventSpec(
        EventTier Tier, bool ScoresPoints, EventScoringMode? ScoringMode, Swim[] Swims);

    private sealed record SeededEvent(Guid EventId, IReadOnlyDictionary<Guid, string> EntryNames);

    private sealed record Seeded(Guid CarnivalId, IReadOnlyList<SeededEvent> Events);

    private static EventSpec Competitive(params Swim[] swims) =>
        new(EventTier.Competitive, ScoresPoints: true, ScoringMode: null, swims);

    private async Task<Seeded> SeedAsync(params EventSpec[] specs)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Placings",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 3, 6),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);

        var seeded = new List<SeededEvent>();
        foreach (var spec in specs)
        {
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"SW-{Guid.NewGuid():N}"[..12],
                Name = "Freestyle 50m",
                Sport = "Swimming",
                MeasureType = MeasureType.Time,
                Units = "s",
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnival.Id,
                EventDefinitionId = definition.Id,
                Category = CompetitionCategory.M,
                Tier = spec.Tier,
                ScoringMode = spec.ScoringMode,
                ScoresPoints = spec.ScoresPoints,
                Status = CarnivalEventStatus.Open,
            };
            carnivalEvent.AgeGroup = "13";
            db.CarnivalEvents.Add(carnivalEvent);

            var names = new Dictionary<Guid, string>();
            foreach (var swim in spec.Swims)
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"S-{Guid.NewGuid():N}",
                    Surname = "X", FirstName = "Y", IsActive = true, CreatedAtUtc = now,
                };
                db.Students.Add(student);
                var entry = new Entry
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalEventId = carnivalEvent.Id,
                    StudentId = student.Id,
                    Heat = swim.Heat,
                    SnapshotDisplayName = swim.Name,
                    SnapshotYearLevel = 7,
                    SnapshotHouseCode = "BANKSIA",
                    SnapshotAgeGroup = "13",
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = swim.Status,
                    MeasuredValue = swim.Value,
                    LifecycleState = ResultLifecycleState.Verified,
                    CurrentRevision = 1,
                    RecordedBySubject = "seed",
                    RecordedAtUtc = now,
                    CreatedAtUtc = now,
                });
                names[entry.Id] = swim.Name;
            }
            seeded.Add(new SeededEvent(carnivalEvent.Id, names));
        }

        await db.SaveChangesAsync();
        return new Seeded(carnival.Id, seeded);
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>The deck's read, as name → place (absent place is null).</summary>
    private async Task<Dictionary<string, int?>> DeckPlacesAsync(Seeded seeded, int index = 0)
    {
        var ev = seeded.Events[index];
        var response = await Recorder.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{ev.EventId}/results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var places = new Dictionary<string, int?>();
        foreach (var row in (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray())
        {
            var place = row.GetProperty("place");
            places[ev.EntryNames[row.GetProperty("entryId").GetGuid()]] =
                place.ValueKind == JsonValueKind.Null ? null : place.GetInt32();
        }
        return places;
    }

    // ---- The place per heat -----------------------------------------------

    [Fact]
    public async Task Each_heat_is_placed_in_its_own_field()
    {
        // Heat 2's winner is slower than heat 1's runner-up and still reads 1st:
        // the heat is the placing unit (ADR-0015 §1).
        var seeded = await SeedAsync(Competitive(
            new Swim(1, "Ada", ResultStatus.Ok, 31.2m),
            new Swim(1, "Blake", ResultStatus.Ok, 31.8m),
            new Swim(2, "Cora", ResultStatus.Ok, 35.0m),
            new Swim(2, "Dale", ResultStatus.Ok, 36.0m)));

        var places = await DeckPlacesAsync(seeded);
        Assert.Equal(1, places["Ada"]);
        Assert.Equal(2, places["Blake"]);
        Assert.Equal(1, places["Cora"]);
        Assert.Equal(2, places["Dale"]);
    }

    [Fact]
    public async Task An_unheated_remainder_is_placed_among_itself()
    {
        // Heat null is its OWN field, not part of heat 1 — the ordinary state of
        // a carnival morning, where heats are assigned as swimmers present.
        var seeded = await SeedAsync(Competitive(
            new Swim(1, "Ada", ResultStatus.Ok, 30.0m),
            new Swim(null, "Blake", ResultStatus.Ok, 31.0m),
            new Swim(null, "Cora", ResultStatus.Ok, 32.0m),
            new Swim(1, "Dale", ResultStatus.Ok, 33.0m)));

        var places = await DeckPlacesAsync(seeded);
        Assert.Equal(1, places["Ada"]);
        Assert.Equal(1, places["Blake"]);  // 2nd if ranked against heat 1
        Assert.Equal(2, places["Cora"]);
        Assert.Equal(2, places["Dale"]);
    }

    [Fact]
    public async Task A_non_finish_carries_no_place_and_never_displaces_a_finisher()
    {
        var seeded = await SeedAsync(Competitive(
            new Swim(1, "Ada", ResultStatus.Dns, null),
            new Swim(1, "Blake", ResultStatus.Ok, 31.8m),
            new Swim(1, "Cora", ResultStatus.Dnf, null),
            new Swim(1, "Dale", ResultStatus.Ok, 32.0m),
            new Swim(1, "Emery", ResultStatus.Dq, null)));

        var places = await DeckPlacesAsync(seeded);
        Assert.Null(places["Ada"]);
        Assert.Null(places["Cora"]);
        Assert.Null(places["Emery"]);
        Assert.Equal(1, places["Blake"]);
        Assert.Equal(2, places["Dale"]);
    }

    [Fact]
    public async Task A_tie_shares_the_higher_place_and_skips_the_next()
    {
        // Standard competition ranking, per heat (Placement's own semantics).
        var seeded = await SeedAsync(Competitive(
            new Swim(1, "Ada", ResultStatus.Ok, 30.0m),
            new Swim(1, "Blake", ResultStatus.Ok, 30.0m),
            new Swim(1, "Cora", ResultStatus.Ok, 31.0m)));

        var places = await DeckPlacesAsync(seeded);
        Assert.Equal(1, places["Ada"]);
        Assert.Equal(1, places["Blake"]);
        Assert.Equal(3, places["Cora"]);
    }

    [Fact]
    public async Task A_social_event_places_nothing()
    {
        // The gate is server-side in the shared ranking, so the deck cannot badge
        // a Social row however ranked its times look (ADR-0014 §4).
        var seeded = await SeedAsync(new EventSpec(EventTier.Social, true, null,
        [
            new Swim(1, "Ada", ResultStatus.Ok, 30.0m),
            new Swim(1, "Blake", ResultStatus.Ok, 31.0m),
        ]));

        var places = await DeckPlacesAsync(seeded);
        Assert.Null(places["Ada"]);
        Assert.Null(places["Blake"]);
    }

    [Fact]
    public async Task A_competitive_event_scored_socially_places_nothing()
    {
        var seeded = await SeedAsync(new EventSpec(
            EventTier.Competitive, true, EventScoringMode.Social,
        [
            new Swim(1, "Ada", ResultStatus.Ok, 30.0m),
            new Swim(1, "Blake", ResultStatus.Ok, 31.0m),
        ]));

        var places = await DeckPlacesAsync(seeded);
        Assert.Null(places["Ada"]);
        Assert.Null(places["Blake"]);
    }

    [Fact]
    public async Task An_event_that_pays_no_house_points_is_still_placed()
    {
        // ScoresPoints keeps an event OUT OF THE HOUSE TOTALS; it does not stop
        // the field being ranked, and the results book prints its places today.
        // The deck must read what the book prints (ADR-0036 §1) — see the
        // agreement test below.
        var seeded = await SeedAsync(new EventSpec(EventTier.Competitive, false, null,
        [
            new Swim(1, "Ada", ResultStatus.Ok, 30.0m),
            new Swim(1, "Blake", ResultStatus.Ok, 31.0m),
        ]));

        var places = await DeckPlacesAsync(seeded);
        Assert.Equal(1, places["Ada"]);
        Assert.Equal(2, places["Blake"]);
    }

    // ---- The book and the deck are the same ranking ------------------------

    [Fact]
    public async Task The_deck_read_and_the_results_book_place_every_row_the_same()
    {
        var seeded = await SeedAsync(
            Competitive(
                new Swim(1, "Ada", ResultStatus.Ok, 31.2m),
                new Swim(1, "Blake", ResultStatus.Ok, 31.2m),
                new Swim(1, "Cora", ResultStatus.Dq, null),
                new Swim(2, "Dale", ResultStatus.Ok, 35.0m),
                new Swim(null, "Emery", ResultStatus.Ok, 40.0m)),
            new EventSpec(EventTier.Social, true, null,
            [
                new Swim(1, "Frankie", ResultStatus.Ok, 30.0m),
            ]),
            new EventSpec(EventTier.Competitive, false, null,
            [
                new Swim(1, "Gale", ResultStatus.Ok, 30.0m),
                new Swim(1, "Harper", ResultStatus.Ok, 31.0m),
            ]));

        var book = await (await Admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var printed = new Dictionary<string, int?>();
        foreach (var row in book.GetProperty("rows").EnumerateArray())
        {
            var place = row.GetProperty("place");
            printed[row.GetProperty("displayName").GetString()!] =
                place.ValueKind == JsonValueKind.Null ? null : place.GetInt32();
        }

        var deck = new Dictionary<string, int?>();
        for (var i = 0; i < seeded.Events.Count; i++)
            foreach (var (name, place) in await DeckPlacesAsync(seeded, i))
                deck[name] = place;

        Assert.Equal(printed.Count, deck.Count);
        Assert.Equal(printed, deck);
    }
}
