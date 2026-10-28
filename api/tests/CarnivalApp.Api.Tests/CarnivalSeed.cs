using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Seeds a synthetic carnival world directly through the DbContext for
/// HTTP-level carnival/entry/result tests: promoted roster snapshots (current),
/// one carnival, one Time event definition and one scheduled event, with an
/// assigned official. Every call creates fresh rows (unique ids/codes), so
/// tests sharing an ApiTestFactory never interfere.
/// </summary>
public sealed record SeededCarnival(
    Guid CarnivalId,
    Guid DefinitionId,
    Guid EventId,
    string PrimaryAgeGroup,   // the event's division age group (unique per seed for isolation)
    string JuniorAgeGroup,    // one age group younger (unique per seed)
    Guid EligibleBoyId,       // primary age group, category M, BANKSIA, year 7
    Guid SecondEligibleBoyId, // primary age group, category M, WATTLE, year 7
    Guid GirlId,              // primary age group, category F, WATTLE, year 7
    Guid YoungerBoyId,        // junior age group, category M, MOUNTAIN ASH, year 7
    Guid SCategoryStudentId,  // primary age group, category S, GREVILLEA, year 7
    Guid InactiveStudentId,   // inactive, no current snapshot
    Guid NoSnapshotStudentId);

public static class CarnivalSeed
{
    public const string AssignedOfficial = "official-assigned";

    private static int _sequence;

    public static async Task<SeededCarnival> SeedAsync(
        ApiTestFactory factory,
        CarnivalStatus carnivalStatus = CarnivalStatus.Open,
        CarnivalEventStatus eventStatus = CarnivalEventStatus.Open,
        CompetitionCategory? category = CompetitionCategory.M,
        MeasureType measureType = MeasureType.Time,
        // ADR-0053 §1: a value makes the event an attempts event, captured one
        // ordinal at a time with the best mark derived.
        int? attemptCount = null)
    {
        var n = Interlocked.Increment(ref _sequence);
        // Unique per seed so eligibility/search (which scan the whole current
        // roster) stay isolated between tests sharing one factory database.
        var primaryAgeGroup = $"A{n}";
        var juniorAgeGroup = $"B{n}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();

        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            SourceFileName = $"seed-{n}.csv",
            SourceFileHash = $"hash-{n}",
            MappingVersion = "test",
            ImportedBySubject = "seeder",
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = ImportBatchStatus.Promoted,
        };
        db.ImportBatches.Add(batch);

        Guid AddStudent(string first, string surname, int yearLevel, string house,
            string studentAgeGroup, CompetitionCategory studentCategory,
            bool active = true, bool withSnapshot = true)
        {
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = $"SEED{n}-{first}",
                Surname = surname,
                FirstName = first,
                IsActive = active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Students.Add(student);
            if (withSnapshot)
                db.RosterSnapshots.Add(new RosterSnapshot
                {
                    Id = Guid.CreateVersion7(),
                    StudentId = student.Id,
                    SchoolYear = 2027,
                    YearLevel = yearLevel,
                    HouseCode = house,
                    CompetitionAgeGroup = studentAgeGroup,
                    CompetitionCategory = studentCategory,
                    ImportBatchId = batch.Id,
                    EffectiveDate = new DateOnly(2027, 1, 30),
                    IsCurrent = true,
                });
            return student.Id;
        }

        var eligibleBoy = AddStudent("Aiden", "Brook", 7, "BANKSIA", primaryAgeGroup, CompetitionCategory.M);
        var secondBoy = AddStudent("Blake", "Chen", 7, "WATTLE", primaryAgeGroup, CompetitionCategory.M);
        var girl = AddStudent("Cora", "Diaz", 7, "WATTLE", primaryAgeGroup, CompetitionCategory.F);
        var youngerBoy = AddStudent("Dale", "Egan", 7, "MOUNTAIN ASH", juniorAgeGroup, CompetitionCategory.M);
        var sStudent = AddStudent("Emery", "Frost", 7, "GREVILLEA", primaryAgeGroup, CompetitionCategory.S);
        var inactive = AddStudent("Finn", "Gone", 7, "BANKSIA", primaryAgeGroup, CompetitionCategory.M, active: false);
        var noSnapshot = AddStudent("Gil", "Hollow", 7, "BANKSIA", primaryAgeGroup, CompetitionCategory.M, withSnapshot: false);

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = $"Swimming Carnival {n}",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Venue = "Outdoor Pool",
            Status = carnivalStatus,
            RuleVersion = "v1",
            OwnerSubject = "event-admin-seed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);

        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = $"SW-50-FREE-{n}",
            Name = "Freestyle 50m",
            Sport = "Swimming",
            MeasureType = measureType,
            AttemptCount = attemptCount,
            Units = measureType == MeasureType.Time ? "s" : "m",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.EventDefinitions.Add(definition);

        var carnivalEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            AgeGroup = primaryAgeGroup,
            Category = category,
            Status = eventStatus,
        };
        db.CarnivalEvents.Add(carnivalEvent);
        db.CarnivalEventOfficials.Add(new CarnivalEventOfficial
        {
            Id = Guid.CreateVersion7(),
            CarnivalEventId = carnivalEvent.Id,
            SubjectId = AssignedOfficial,
            AssignedAtUtc = DateTimeOffset.UtcNow,
            AssignedBySubject = "seeder",
        });

        await db.SaveChangesAsync();
        return new SeededCarnival(
            carnival.Id, definition.Id, carnivalEvent.Id, primaryAgeGroup, juniorAgeGroup,
            eligibleBoy, secondBoy, girl, youngerBoy, sStudent, inactive, noSnapshot);
    }

    /// <summary>
    /// Schedule another division of the seeded event's DEFINITION — a sibling by
    /// category and/or tier (ADR-0023 §3), or by age. Returns its event id.
    /// </summary>
    public static async Task<Guid> AddDivisionAsync(
        ApiTestFactory factory, SeededCarnival seeded, CompetitionCategory? category,
        EventTier tier = EventTier.Competitive, string? ageGroup = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var carnivalEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = seeded.CarnivalId,
            EventDefinitionId = seeded.DefinitionId,
            AgeGroup = ageGroup ?? seeded.PrimaryAgeGroup,
            Category = category,
            Tier = tier,
            Status = CarnivalEventStatus.Open,
        };
        db.CarnivalEvents.Add(carnivalEvent);
        await db.SaveChangesAsync();
        return carnivalEvent.Id;
    }

    /// <summary>
    /// An entry in NO race, inserted directly. Since ADR-0039 §2 no create is
    /// raceless — the server places every one — so a marshalling scene that needs
    /// an unplaced row (§3's legacy case, and everything the materialisation
    /// sweep exists for) has to seed it.
    /// </summary>
    public static async Task<Guid> UnplacedEntryAsync(
        ApiTestFactory factory, Guid eventId, Guid studentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var student = await db.Students.SingleAsync(s => s.Id == studentId);
        var snapshot = await db.RosterSnapshots
            .SingleAsync(r => r.StudentId == studentId && r.IsCurrent);
        var entry = new Entry
        {
            Id = Guid.CreateVersion7(),
            CarnivalEventId = eventId,
            StudentId = studentId,
            RosterSnapshotId = snapshot.Id,
            SnapshotDisplayName = $"{student.PreferredName ?? student.FirstName} {student.Surname}",
            SnapshotYearLevel = snapshot.YearLevel,
            SnapshotHouseCode = snapshot.HouseCode,
            SnapshotAgeGroup = snapshot.CompetitionAgeGroup,
            SnapshotCategory = snapshot.CompetitionCategory,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBySubject = "seeder",
        };
        db.Entries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    /// <summary>Run an action against the factory's DbContext (fresh scope).</summary>
    public static async Task<T> QueryAsync<T>(ApiTestFactory factory, Func<CarnivalAppDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
    }

    /// <summary>
    /// Mutate seeded rows directly, for states the API can no longer produce —
    /// principally a pre-ADR-0017 <c>Provisional</c> result, which every capture
    /// path now creates as <c>Verified</c>.
    /// </summary>
    public static async Task MutateAsync(ApiTestFactory factory, Action<CarnivalAppDbContext> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        mutate(db);
        await db.SaveChangesAsync();
    }
}
