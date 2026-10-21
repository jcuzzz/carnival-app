using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0015 §3: a lane is claimed by at most one entry IN A RACE — the constraint
/// that is unpoliceable if the lane belongs to the heat, and the reason the race
/// entity exists at all.
///
/// It is enforced by a unique index on (RaceId, Lane) with no explicit filter,
/// because each provider ADR-0001 review note 5 keeps open already does the right
/// thing with one: PostgreSQL and SQLite treat NULLs in a unique index as
/// DISTINCT, so the unmarshalled (null, null) rows never collide; and the SQL
/// Server provider gives any unique index over nullable columns its own
/// automatic, correctly-bracketed WHERE ... IS NOT NULL.
///
/// These tests run on SQLite (the test/dev provider). BE HONEST ABOUT WHAT THEY
/// PROVE: only <see cref="Two_entries_cannot_claim_the_same_lane_in_one_race"/>
/// and <see cref="The_model_carries_a_unique_index_over_race_and_lane"/> fail if
/// the index is deleted. The other three pass either way — they document which
/// states are deliberately OUTSIDE the constraint (a repeated lane in a different
/// race, unmarshalled entries, a race full of lane-less entries), so that a later
/// tightening has to argue with a named test instead of quietly breaking the deck.
/// </summary>
public sealed class RaceLaneUniquenessTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record Seeded(Guid CarnivalId, Guid EventId, Guid RaceA, Guid RaceB);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var now = DateTimeOffset.UtcNow;

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Lanes",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);
        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = $"EV-{Guid.NewGuid():N}",
            Name = "Freestyle 50m",
            Sport = "Swimming",
            MeasureType = MeasureType.Time,
            Units = "s",
            ScoringWeight = 1,
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
        carnivalEvent.AgeGroup = "13";
        db.CarnivalEvents.Add(carnivalEvent);

        // Two races with NO schedule slot: the schedule is advisory and never
        // gating (ADR-0014 §1, ADR-0015 §3), so a race must be fully usable with
        // no program built at all.
        var raceA = new Race { Id = Guid.CreateVersion7(), CarnivalId = carnival.Id, SortOrder = 1 };
        var raceB = new Race { Id = Guid.CreateVersion7(), CarnivalId = carnival.Id, SortOrder = 2 };
        db.Races.AddRange(raceA, raceB);

        await db.SaveChangesAsync();
        return new Seeded(carnival.Id, carnivalEvent.Id, raceA.Id, raceB.Id);
    }

    /// <summary>Add an entry to the event at the given race/lane and save.</summary>
    private async Task AddEntryAsync(Guid eventId, Guid? raceId, int? lane)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var student = new Student
        {
            Id = Guid.CreateVersion7(),
            ExternalStudentId = $"S-{Guid.NewGuid():N}",
            Surname = "X", FirstName = "Y", IsActive = true, CreatedAtUtc = now,
        };
        db.Students.Add(student);
        db.Entries.Add(new Entry
        {
            Id = Guid.CreateVersion7(),
            CarnivalEventId = eventId,
            StudentId = student.Id,
            RaceId = raceId,
            Lane = lane,
            SnapshotDisplayName = "Y X",
            SnapshotYearLevel = 9,
            SnapshotHouseCode = "BANKSIA",
            SnapshotAgeGroup = "13",
            SnapshotCategory = CompetitionCategory.M,
            CreatedAtUtc = now,
            CreatedBySubject = "seed",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Two_entries_cannot_claim_the_same_lane_in_one_race()
    {
        var seeded = await SeedAsync();
        await AddEntryAsync(seeded.EventId, seeded.RaceA, lane: 4);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => AddEntryAsync(seeded.EventId, seeded.RaceA, lane: 4));
    }

    [Fact]
    public async Task The_same_lane_number_in_a_different_race_is_ordinary()
    {
        // Lane 4 exists in every race the venue runs all day. The number means
        // nothing without its race (ADR-0015 §3).
        var seeded = await SeedAsync();
        await AddEntryAsync(seeded.EventId, seeded.RaceA, lane: 4);
        await AddEntryAsync(seeded.EventId, seeded.RaceB, lane: 4);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Equal(2, await db.Entries.CountAsync(e => e.Lane == 4));
    }

    [Fact]
    public async Task Unmarshalled_entries_collide_with_nothing()
    {
        // Every entry starts life with no race and no lane, and stays that way
        // until the morning. This is the case that would break on SQL Server if
        // the index ever reached it unfiltered — NULLs compare EQUAL there, so the
        // second such row would be refused outright. It does not: the provider
        // filters it. Passes on SQLite either way; kept as the statement of what
        // must remain true, and checked for real by the model test below.
        var seeded = await SeedAsync();
        for (var i = 0; i < 5; i++)
            await AddEntryAsync(seeded.EventId, raceId: null, lane: null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Equal(5, await db.Entries.CountAsync(e => e.RaceId == null && e.Lane == null));
    }

    [Fact]
    public async Task A_race_may_hold_several_entries_that_have_no_lane_yet()
    {
        // Marshalled into the race but not yet placed in a lane — an ordinary
        // intermediate state on the deck, and outside the index.
        var seeded = await SeedAsync();
        await AddEntryAsync(seeded.EventId, seeded.RaceA, lane: null);
        await AddEntryAsync(seeded.EventId, seeded.RaceA, lane: null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Equal(2, await db.Entries.CountAsync(e => e.RaceId == seeded.RaceA && e.Lane == null));
    }

    [Fact]
    public async Task A_lane_with_no_race_constrains_nothing()
    {
        // A lane number carried without a race is not yet a claim on anything —
        // there is no race for it to be unique within. (The Phase 2 assignment
        // endpoint is what will refuse to write one; the database's job is only
        // to make the race's lanes exclusive.)
        var seeded = await SeedAsync();
        await AddEntryAsync(seeded.EventId, raceId: null, lane: 4);
        await AddEntryAsync(seeded.EventId, raceId: null, lane: 4);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        Assert.Equal(2, await db.Entries.CountAsync(e => e.RaceId == null && e.Lane == 4));
    }

    [Fact]
    public void The_model_carries_a_unique_index_over_race_and_lane()
    {
        // THE LOAD-BEARING ASSERTION. The behavioural tests above all pass with
        // the index deleted, because SQLite treats NULLs as distinct and the only
        // one of them that can fail — the duplicate — is the one this pins. What
        // must not drift is the MODEL: a unique index whose key is exactly
        // (RaceId, Lane), which is what every provider turns into the ADR's rule.
        //
        // It asserts no filter STRING. The filter is the provider's business, not
        // the model's: PostgreSQL and SQLite need none (NULLs distinct), and the
        // SQL Server provider adds its own correctly-bracketed one to any unique
        // index over nullable columns. A filter written here would be handed
        // verbatim to all three, in one dialect's quoting.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var entry = db.Model.FindEntityType(typeof(Entry))!;
        var index = Assert.Single(entry.GetIndexes(),
            i => i.IsUnique && i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(Entry.RaceId), nameof(Entry.Lane) }));

        // Order matters as much as membership: (Lane, RaceId) would constrain the
        // same rows, but "lane 4 is claimed once in this race" is the rule, and
        // the race must lead for the index to serve a race-scoped read.
        Assert.Equal(nameof(Entry.RaceId), index.Properties[0].Name);
        Assert.Equal(nameof(Entry.Lane), index.Properties[1].Name);
    }

    [Fact]
    public async Task A_race_needs_no_schedule_slot()
    {
        // ADR-0014 §1 restated by ADR-0015 §3: the schedule is advisory and never
        // gating, so nothing about racing may require a program to exist.
        var seeded = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var race = await db.Races.SingleAsync(r => r.Id == seeded.RaceA);
        Assert.Null(race.ScheduleSlotId);
        Assert.Equal(seeded.CarnivalId, race.CarnivalId);
    }
}
