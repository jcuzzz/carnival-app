using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The ADR-0014 behaviour pin: an ordinary carnival — every event Competitive,
/// no scoring-mode overrides, no points exclusions, default rules — must score
/// EXACTLY as it did before the classification model landed. The expected
/// document below was captured from the engine as it stood on 2026-08-07, before
/// tier/mode/ScoresPoints/age-set existed; any change to it is a change to what
/// a school's scoreboard says, and needs a decision, not a test edit.
///
/// The scenario deliberately exercises every scoring path in one board: placings
/// with a tie, the participation floor, DNF/DNS/DQ, a doubled relay event
/// (ADR-0008), an event with no results at all, a house-point adjustment
/// (ADR-0005) including a voided one, and a higher-is-better measure.
/// </summary>
public sealed class StandingsRegressionTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const string Expected = """
        {
          "ruleVersion": "v1",
          "provisional": true,
          "eventsCounted": 3,
          "resultsCounted": 12,
          "houses": [
            { "houseCode": "BANKSIA", "total": 22, "placementPoints": 12, "participationPoints": 0, "bonusPoints": 10 },
            { "houseCode": "WATTLE", "total": 13, "placementPoints": 12, "participationPoints": 1, "bonusPoints": 0 },
            { "houseCode": "MOUNTAIN ASH", "total": 8, "placementPoints": 8, "participationPoints": 0, "bonusPoints": 0 },
            { "houseCode": "GREVILLEA", "total": 4, "placementPoints": 4, "participationPoints": 0, "bonusPoints": 0 }
          ]
        }
        """;

    [Fact]
    public async Task An_all_competitive_default_rules_carnival_scores_identically()
    {
        var carnivalId = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var scoring = new ScoringService(scope.ServiceProvider.GetRequiredService<SportsDbContext>());
        var standings = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(standings);
        Assert.Equal(Canonical(Expected), Canonical(Serialise(standings!)));
    }

    /// <summary>
    /// ADR-0017 §4: carnival finalisation sweeps every result to <c>Final</c>.
    /// <c>Final</c> is <c>!= Draft</c>, so it must move ZERO house points — the
    /// act publishes the numbers, it does not change them.
    /// </summary>
    /// <remarks>
    /// The comparison is of the HOUSE ROWS, not the whole document, and that is
    /// deliberate rather than a weakening. The pinned document carries
    /// <c>"provisional": true</c>, which <c>ScoringService</c> derives from the
    /// carnival's status — so finalising flips it by definition and a
    /// byte-identical assertion would be unachievable. An implementer told to
    /// assert byte-identity here would "fix" it by editing the pinned
    /// expectations, which is the one thing this file forbids.
    /// </remarks>
    [Fact]
    public async Task Finalising_the_carnival_publishes_the_same_numbers_it_showed_all_day()
    {
        var carnivalId = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var scoring = new ScoringService(db);

        var before = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(before);
        Assert.True(before!.Provisional);

        // The sweep, as finalisation performs it.
        foreach (var result in db.Results.Where(r => r.LifecycleState != ResultLifecycleState.Draft))
            result.LifecycleState = ResultLifecycleState.Final;
        db.Carnivals.Single(c => c.Id == carnivalId).Status = CarnivalStatus.Finalised;
        await db.SaveChangesAsync();

        var after = await scoring.ComputeAsync(carnivalId, default);
        Assert.NotNull(after);
        Assert.False(after!.Provisional); // the ONE thing that legitimately changes
        Assert.Equal(before.EventsCounted, after.EventsCounted);
        Assert.Equal(before.ResultsCounted, after.ResultsCounted);
        Assert.Equal(Houses(before), Houses(after));
        // …and they are still the pinned numbers, not merely self-consistent.
        Assert.Equal(Canonical(HousesOf(Expected)), Canonical(Houses(after)));
    }

    private static string Houses(ScoringService.Standings s) => JsonSerializer.Serialize(
        s.Houses.Select(h => new
        {
            houseCode = h.HouseCode,
            total = h.Total,
            placementPoints = h.PlacementPoints,
            participationPoints = h.ParticipationPoints,
            bonusPoints = h.BonusPoints,
        }));

    private static string HousesOf(string document) =>
        JsonSerializer.Deserialize<JsonElement>(document).GetProperty("houses").GetRawText();

    /// <summary>Everything the pin covers, minus the carnival id (a fresh GUID per run).</summary>
    private static string Serialise(ScoringService.Standings s) => JsonSerializer.Serialize(new
    {
        ruleVersion = s.RuleVersion,
        provisional = s.Provisional,
        eventsCounted = s.EventsCounted,
        resultsCounted = s.ResultsCounted,
        houses = s.Houses.Select(h => new
        {
            houseCode = h.HouseCode,
            total = h.Total,
            placementPoints = h.PlacementPoints,
            participationPoints = h.ParticipationPoints,
            bonusPoints = h.BonusPoints,
        }),
    });

    /// <summary>Re-serialise so the comparison is of the document, not its whitespace.</summary>
    private static string Canonical(string json) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));

    private sealed record Row(string House, ResultStatus Status, decimal? Value);

    private async Task<Guid> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var now = DateTimeOffset.UtcNow;

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Regression Carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);

        void AddEvent(string code, MeasureType measure, int weight, string ageGroup, Row[] rows)
        {
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"{code}-{Guid.NewGuid():N}",
                Name = code,
                Sport = "Swimming",
                MeasureType = measure,
                Units = measure == MeasureType.Time ? "s" : "m",
                ScoringWeight = weight,
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnival.Id,
                EventDefinitionId = definition.Id,
                Category = CompetitionCategory.M,
                Status = CarnivalEventStatus.Open,
            };
            carnivalEvent.AgeGroup = ageGroup;
            db.CarnivalEvents.Add(carnivalEvent);

            foreach (var row in rows)
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
                    SnapshotDisplayName = "Y X",
                    SnapshotYearLevel = 9,
                    SnapshotHouseCode = row.House,
                    SnapshotAgeGroup = ageGroup,
                    SnapshotCategory = CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "seed",
                };
                db.Entries.Add(entry);
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = row.Status,
                    MeasuredValue = row.Value,
                    LifecycleState = ResultLifecycleState.Provisional,
                    CurrentRevision = 1,
                    RecordedBySubject = "seed",
                    RecordedAtUtc = now,
                    CreatedAtUtc = now,
                });
            }
        }

        // Freestyle 50m 13 M — placings with a tie for 1st, then the floor.
        AddEvent("SW-50-FREE", MeasureType.Time, 1, "13",
        [
            new Row("BANKSIA", ResultStatus.Ok, 30.00m),   // rank 1 → 4
            new Row("WATTLE", ResultStatus.Ok, 30.00m),      // rank 1 → 4
            new Row("MOUNTAIN ASH", ResultStatus.Ok, 31.00m),  // rank 3 → 2
            new Row("GREVILLEA", ResultStatus.Ok, 32.00m),   // rank 4 → floor 1
            new Row("WATTLE", ResultStatus.Dnf, null),       // participation 1
            new Row("MOUNTAIN ASH", ResultStatus.Dns, null),   // 0
            new Row("GREVILLEA", ResultStatus.Dq, null),     // 0
        ]);

        // Freestyle Relay 4x50m 14 M — every placing doubled (ADR-0008).
        AddEvent("SW-4X50-FREE-RELAY", MeasureType.Time, 2, "14",
        [
            new Row("BANKSIA", ResultStatus.Ok, 120.00m),  // 1st ×2 → 8
            new Row("MOUNTAIN ASH", ResultStatus.Ok, 121.00m), // 2nd ×2 → 6
            new Row("WATTLE", ResultStatus.Ok, 122.00m),     // 3rd ×2 → 4
        ]);

        // Long jump 14 M — higher is better, so the comparison inverts.
        AddEvent("AT-LONG-JUMP", MeasureType.Distance, 1, "14",
        [
            new Row("WATTLE", ResultStatus.Ok, 4.85m),       // 1st → 4
            new Row("GREVILLEA", ResultStatus.Ok, 4.20m),    // 2nd → 3
        ]);

        // A scheduled event nobody has swum yet — must not appear in the counts.
        AddEvent("SW-50-FLY", MeasureType.Time, 1, "15", []);

        db.HousePointsAdjustments.Add(new HousePointsAdjustment
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            HouseCode = "BANKSIA",
            Points = 10,
            Category = "Best Dressed House",
            Reason = "regression pin",
            RecordedBySubject = "seed",
            RecordedAtUtc = now,
        });
        db.HousePointsAdjustments.Add(new HousePointsAdjustment
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            HouseCode = "WATTLE",
            Points = 99,
            Category = "Best Dressed House",
            Reason = "voided, must not count",
            RecordedBySubject = "seed",
            RecordedAtUtc = now,
            Voided = true,
        });

        await db.SaveChangesAsync();
        return carnival.Id;
    }
}
