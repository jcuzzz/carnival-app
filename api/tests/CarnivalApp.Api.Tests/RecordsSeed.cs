using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// A carnival built division by division for the ADR-0028 record and district
/// tests: each definition declares its own record categories (or none), each
/// division names its age scope, category and tier, and each swim is one result.
/// Definition CODES are suffixed per build, so worlds sharing one factory
/// database get their own definitions and cannot see each other's records.
/// </summary>
public sealed class RecordsSeed
{
    private static int _sequence;

    /// <summary>One recorded swim: an individual entrant unless <paramref name="Team"/>.</summary>
    public sealed record Swim(
        string Name,
        string House,
        decimal? Value,
        ResultStatus Status = ResultStatus.Ok,
        int? Heat = null,
        int YearLevel = 7,
        bool Team = false,
        /// <summary>False leaves the entry owing a result, which refuses finalisation.</summary>
        bool Resulted = true,
        /// <summary>The stored attempt series (ADR-0053 §2); the countback key comes off it.</summary>
        string? AttemptsJson = null);

    private sealed record PlannedDefinition(
        string Key, string? RecordCategoriesJson, MeasureType MeasureType, EventFormat Format, string Sport,
        int? AttemptCount);

    private sealed record PlannedDivision(
        string DefinitionKey, string? AgeGroup, CompetitionCategory? Category, EventTier Tier, Swim[] Swims);

    private readonly List<PlannedDefinition> _definitions = [];
    private readonly List<PlannedDivision> _divisions = [];

    /// <summary>The blob <c>EventTemplateService</c> writes: ages × categories.</summary>
    public static string Categories(string[] ageGroups, params CompetitionCategory[] categories) =>
        JsonSerializer.Serialize(new
        {
            ageGroups,
            categories = categories.Select(c => c.ToString()),
        });

    public RecordsSeed Definition(
        string key,
        string? recordCategories,
        MeasureType measureType = MeasureType.Time,
        EventFormat format = EventFormat.Individual,
        string sport = "Swimming",
        int? attemptCount = null)
    {
        _definitions.Add(
            new PlannedDefinition(key, recordCategories, measureType, format, sport, attemptCount));
        return this;
    }

    /// <summary>A division of one age; null contests the OPEN (all-ages) axis.</summary>
    public RecordsSeed Division(
        string definitionKey,
        string? ageGroup,
        CompetitionCategory? category,
        EventTier tier,
        params Swim[] swims)
    {
        _divisions.Add(new PlannedDivision(definitionKey, ageGroup, category, tier, swims));
        return this;
    }

    /// <summary>Competitive is the norm; the tier only matters where a test says so.</summary>
    public RecordsSeed Division(
        string definitionKey, string? ageGroup, CompetitionCategory? category, params Swim[] swims) =>
        Division(definitionKey, ageGroup, category, EventTier.Competitive, swims);

    /// <summary>
    /// Materialise the world. <paramref name="reusing"/> shares another world's
    /// DEFINITIONS by key, which is how a second carnival contests the same
    /// record categories as the first.
    /// </summary>
    public async Task<SeededRecords> BuildAsync(
        ApiTestFactory factory,
        CarnivalStatus status = CarnivalStatus.Closed,
        SeededRecords? reusing = null,
        DateOnly? date = null)
    {
        var n = Interlocked.Increment(ref _sequence);
        var now = DateTimeOffset.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = $"Records Carnival {n}",
            Type = CarnivalType.Swimming,
            Date = date ?? new DateOnly(2027, 3, 6),
            Status = status,
            RuleVersion = "v1",
            OwnerSubject = "records-seed",
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);

        var definitions = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
        foreach (var planned in _definitions)
        {
            if (reusing is not null && reusing.DefinitionIds.TryGetValue(planned.Key, out var existingId))
            {
                definitions[planned.Key] = await db.EventDefinitions.FirstAsync(d => d.Id == existingId);
                continue;
            }
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = $"{planned.Key}-{n}",
                Name = planned.Key,
                Sport = planned.Sport,
                MeasureType = planned.MeasureType,
                AttemptCount = planned.AttemptCount,
                Units = planned.MeasureType == MeasureType.Time ? "s" : "m",
                Format = planned.Format,
                TeamSize = planned.Format == EventFormat.Team ? 4 : null,
                RecordCategoriesJson = planned.RecordCategoriesJson,
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            definitions[planned.Key] = definition;
        }

        var eventIds = new List<Guid>();
        foreach (var planned in _divisions)
        {
            var definition = definitions[planned.DefinitionKey];
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnival.Id,
                EventDefinitionId = definition.Id,
                AgeGroup = AgeScope.Normalise(planned.AgeGroup),
                Category = planned.Category,
                Tier = planned.Tier,
                Status = CarnivalEventStatus.Open,
            };
            db.CarnivalEvents.Add(carnivalEvent);
            eventIds.Add(carnivalEvent.Id);

            foreach (var swim in planned.Swims)
            {
                Guid? studentId = null;
                if (!swim.Team)
                {
                    var student = new Student
                    {
                        Id = Guid.CreateVersion7(),
                        ExternalStudentId = $"R{n}-{Guid.NewGuid():N}",
                        Surname = "X",
                        FirstName = "Y",
                        IsActive = true,
                        CreatedAtUtc = now,
                    };
                    db.Students.Add(student);
                    studentId = student.Id;
                }
                var entry = new Entry
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalEventId = carnivalEvent.Id,
                    StudentId = studentId,
                    // The check constraint: exactly one shape (ADR-0008).
                    HouseCode = swim.Team ? swim.House : null,
                    Heat = swim.Heat,
                    SnapshotDisplayName = swim.Name,
                    SnapshotYearLevel = swim.YearLevel,
                    SnapshotHouseCode = swim.House,
                    SnapshotAgeGroup = AgeScope.Normalise(planned.AgeGroup),
                    SnapshotCategory = planned.Category ?? CompetitionCategory.M,
                    CreatedAtUtc = now,
                    CreatedBySubject = "records-seed",
                };
                db.Entries.Add(entry);
                if (!swim.Resulted) continue;
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    Status = swim.Status,
                    MeasuredValue = swim.Value,
                    AttemptsJson = swim.AttemptsJson,
                    LifecycleState = ResultLifecycleState.Verified,
                    CurrentRevision = 1,
                    RecordedBySubject = "records-seed",
                    RecordedAtUtc = now,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync();
        return new SeededRecords(
            carnival.Id,
            carnival.Date,
            definitions.ToDictionary(d => d.Key, d => d.Value.Id, StringComparer.Ordinal),
            definitions.ToDictionary(d => d.Key, d => d.Value.Code, StringComparer.Ordinal),
            eventIds);
    }

    /// <summary>Finalise through the real endpoint — the only thing that mints a record.</summary>
    public static Task<HttpResponseMessage> FinaliseAsync(
        ApiTestFactory factory, Guid carnivalId, string subject = "records-finaliser", string? reason = null) =>
        factory.CreateClientAs(subject, "CarnivalAdministrator")
            .PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/status", new { status = "Finalised", reason });
}

public sealed record SeededRecords(
    Guid CarnivalId,
    DateOnly Date,
    IReadOnlyDictionary<string, Guid> DefinitionIds,
    IReadOnlyDictionary<string, string> DefinitionCodes,
    IReadOnlyList<Guid> EventIds);
