using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0067 §2: an "N+" division takes every student whose age is at least N, in the search and
/// at entry alike. Own factory: the roster is school-wide and these ages are plain numbers.
/// </summary>
public sealed class AndOverEligibilityTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public AndOverEligibilityTests() =>
        _admin = _factory.CreateClientAs("and-over-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid CarnivalId, Guid EventId, Dictionary<string, Guid> Students)> SeedAsync()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var eventId = Guid.CreateVersion7();
        var students = new Dictionary<string, Guid>();
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.CarnivalEvents.Add(new CarnivalEvent
            {
                Id = eventId,
                CarnivalId = seeded.CarnivalId,
                EventDefinitionId = seeded.DefinitionId,
                AgeGroup = "18+",
                Category = CompetitionCategory.M,
                Status = CarnivalEventStatus.Open,
            });
            var batch = db.ImportBatches.Local.FirstOrDefault()?.Id
                ?? db.ImportBatches.Select(b => b.Id).First();
            foreach (var age in new[] { "9", "17", "18", "19" })
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"AND-OVER-{age}",
                    Surname = "Senior",
                    FirstName = $"Age{age}",
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                db.Students.Add(student);
                db.RosterSnapshots.Add(new RosterSnapshot
                {
                    Id = Guid.CreateVersion7(),
                    StudentId = student.Id,
                    SchoolYear = 2027,
                    YearLevel = 12,
                    HouseCode = "BANKSIA",
                    CompetitionAgeGroup = age,
                    CompetitionCategory = CompetitionCategory.M,
                    ImportBatchId = batch,
                    EffectiveDate = new DateOnly(2027, 1, 30),
                    IsCurrent = true,
                });
                students[age] = student.Id;
            }
        });
        return (seeded.CarnivalId, eventId, students);
    }

    [Fact]
    public async Task The_search_lists_18_and_19_but_not_17_or_9()
    {
        var (carnivalId, eventId, _) = await SeedAsync();

        var page = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/carnivals/{carnivalId}/events/{eventId}/eligible-students");
        Assert.Equal(["18", "19"], page.GetProperty("students").EnumerateArray()
            .Select(s => s.GetProperty("ageGroup").GetString()!).Order());
    }

    [Fact]
    public async Task An_entry_takes_18_and_19_and_refuses_17()
    {
        var (carnivalId, eventId, students) = await SeedAsync();
        var path = $"/api/v1/carnivals/{carnivalId}/events/{eventId}/entries";

        var refused = await _admin.PostAsJsonAsync(path, new { studentId = students["17"] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AgeGroupIneligible);

        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync(path, new { studentId = students["18"] })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync(path, new { studentId = students["19"] })).StatusCode);
    }
}

/// <summary>
/// ADR-0067 §5: Add age N lowers the carnival's youngest age and adds the new ages' divisions to
/// every event that has a division at the carnival's youngest age, in one audited write.
/// </summary>
public sealed class ExtendAgesTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public ExtendAgesTests() =>
        _admin = _factory.CreateClientAs("extend-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> CarnivalAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Extend Carnival", type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> ExtendAsync(Guid carnivalId, int youngest) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/divisions/extend-ages", new { youngest });

    private Task<List<CarnivalEvent>> EventsAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId)
            .ToListAsync());

    private async Task<Guid> GenerateAsync(Guid carnivalId, string code, params string[] ages)
    {
        var definitionId = await DivisionFixture.CreateDefinitionAsync(_admin, code);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/generate", new
            {
                eventDefinitionId = definitionId,
                divisions = ages.Select(age => new { ageGroup = age, category = "M", tier = "Competitive" }),
            })).StatusCode);
        return definitionId;
    }

    [Fact]
    public async Task New_ages_go_to_events_at_the_youngest_age_and_not_to_ones_that_start_older()
    {
        await CarnivalSeed.SetSchoolAgesAsync(_factory, 12, 18);
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "swimming" })).StatusCode);
        // Has the youngest age, though not every age: extended.
        var gappedId = await GenerateAsync(carnivalId, "GAPPED-50", "12", "14");
        // Starts at 15: left alone.
        var seniorId = await GenerateAsync(carnivalId, "SENIOR-50", "15", "16", "17", "18+");
        var before = (await EventsAsync(carnivalId)).Count;

        var extended = await ExtendAsync(carnivalId, 10);
        Assert.Equal(HttpStatusCode.OK, extended.StatusCode);
        var body = await extended.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["10", "11"], body.GetProperty("addedAges").EnumerateArray().Select(a => a.GetString()!));

        var events = await EventsAsync(carnivalId);
        var added = events.Where(e => e.AgeGroup is "10" or "11").ToList();
        Assert.Equal(body.GetProperty("divisionsAdded").GetInt32(), added.Count);
        Assert.Equal(before + added.Count, events.Count);
        Assert.DoesNotContain(added, e => e.EventDefinitionId == seniorId);
        Assert.Equal(2, added.Count(e => e.EventDefinitionId == gappedId));
        // Every (category, tier) of the freestyle gains both ages.
        var freestyle = events.Where(e => e.EventDefinition.Code == "SW-50-FREE").ToList();
        Assert.Equal(
            freestyle.Where(e => e.AgeGroup == "12").Select(e => (e.Category, e.Tier, "10")).ToHashSet(),
            freestyle.Where(e => e.AgeGroup == "10").Select(e => (e.Category, e.Tier, e.AgeGroup)).ToHashSet());

        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{carnivalId}/settings");
        Assert.Equal(["10", "11", "12", "13", "14", "15", "16", "17", "18+"],
            settings.GetProperty("divisions").GetProperty("ageGroups").EnumerateArray().Select(a => a.GetString()!));

        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.CarnivalExtendAges && a.EntityId == carnivalId.ToString())));
    }

    [Fact]
    public async Task A_new_age_takes_the_scoring_and_program_place_of_the_division_it_extends()
    {
        await CarnivalSeed.SetSchoolAgesAsync(_factory, 12, 18);
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "swimming" })).StatusCode);
        var sibling = (await EventsAsync(carnivalId))
            .First(e => e.AgeGroup == "12" && e.ScheduleSlotId is not null);
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var row = db.CarnivalEvents.Single(e => e.Id == sibling.Id);
            row.ScoringMode = EventScoringMode.Social;
            row.ScoresPoints = false;
        });

        Assert.Equal(HttpStatusCode.OK, (await ExtendAsync(carnivalId, 11)).StatusCode);

        var added = (await EventsAsync(carnivalId)).Single(e =>
            e.AgeGroup == "11" && e.EventDefinitionId == sibling.EventDefinitionId
            && e.Category == sibling.Category && e.Tier == sibling.Tier);
        Assert.Equal(EventScoringMode.Social, added.ScoringMode);
        Assert.False(added.ScoresPoints);
        Assert.Equal(sibling.ScheduleSlotId, added.ScheduleSlotId);
        Assert.Equal(sibling.SlotOrder, added.SlotOrder);
    }

    [Fact]
    public async Task An_event_still_on_a_plain_top_age_is_extended_after_the_top_age_became_and_over()
    {
        var carnivalId = await CarnivalAsync();
        object Profile(string top) => new
        {
            divisions = new
            {
                ageGroups = new[] { "12", "13", top },
                categories = new[] { "M" },
                tiers = new[] { "Competitive" },
            },
        };
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings", Profile("14"))).StatusCode);
        var definitionId = await GenerateAsync(carnivalId, "LEGACY-50", "12", "13", "14");
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings", Profile("14+"))).StatusCode);

        var body = await (await ExtendAsync(carnivalId, 11)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("divisionsAdded").GetInt32());
        Assert.Contains(await EventsAsync(carnivalId),
            e => e.EventDefinitionId == definitionId && e.AgeGroup == "11");
    }

    [Fact]
    public async Task Without_a_saved_profile_the_schools_range_is_extended_and_saved()
    {
        await CarnivalSeed.SetSchoolAgesAsync(_factory, 12, 18);
        var carnivalId = await CarnivalAsync();

        var body = await (await ExtendAsync(carnivalId, 11)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("divisionsAdded").GetInt32());

        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{carnivalId}/settings");
        Assert.Equal("11", settings.GetProperty("divisions").GetProperty("ageGroups")[0].GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisionsDefault").ValueKind);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(15)]
    public async Task An_age_the_carnival_already_covers_is_refused(int youngest)
    {
        await CarnivalSeed.SetSchoolAgesAsync(_factory, 12, 18);
        var carnivalId = await CarnivalAsync();

        var refused = await ExtendAsync(carnivalId, youngest);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AgeAlreadyCovered);
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalDivisions.CountAsync()));
    }

    [Fact]
    public async Task With_no_ages_anywhere_it_is_refused()
    {
        var carnivalId = await CarnivalAsync();

        var refused = await ExtendAsync(carnivalId, 11);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AgesRequired);
    }

    [Fact]
    public async Task A_recorder_may_not_extend_ages()
    {
        await CarnivalSeed.SetSchoolAgesAsync(_factory, 12, 18);
        var carnivalId = await CarnivalAsync();

        var recorder = _factory.CreateClientAs("extend-recorder", "Recorder");
        Assert.Equal(HttpStatusCode.Forbidden, (await recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/divisions/extend-ages", new { youngest = 11 })).StatusCode);
    }
}
