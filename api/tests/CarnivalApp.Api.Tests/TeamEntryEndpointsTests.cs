using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Team/relay entry (ADR-0008): POST /team-entries builds one house team of
/// TeamSize swimmers, snapshots each leg, enforces per-leg house/division
/// eligibility, and yields a single team entry that carries a normal result.
/// Fresh factory per test (IDisposable) so fixed age-group labels stay isolated.
/// </summary>
public sealed class TeamEntryEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public TeamEntryEndpointsTests() => _admin = _factory.CreateClientAs("event-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private sealed record TeamScene(
        Guid CarnivalId, Guid AgeEventId, Guid OpenEventId, Guid GirlsAgeEventId,
        Guid GirlsOpenEventId, Guid SocialAgeEventId, Guid NoCategoryAgeEventId,
        Guid[] BanksiaBoys14, Guid WaaBoy14, Guid BanksiaGirl14, Guid BanksiaBoy15,
        Guid[] BanksiaOpenCategory14);

    private static string TeamPath(Guid carnivalId, Guid eventId) =>
        $"/api/v1/carnivals/{carnivalId}/events/{eventId}/team-entries";

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>())!;

    /// <summary>Seed a carnival with a Team (medley) definition scheduled both
    /// as a 14/M age-group relay and an OPEN/M relay, plus swimmers.</summary>
    private async Task<TeamScene> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();

        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            SourceFileName = "team-seed.csv",
            SourceFileHash = "team-hash",
            MappingVersion = "test",
            ImportedBySubject = "seeder",
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = ImportBatchStatus.Promoted,
        };
        db.ImportBatches.Add(batch);

        Guid AddSwimmer(string first, string house, string ageGroup, CompetitionCategory category)
        {
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = $"T-{first}",
                Surname = "Swift",
                FirstName = first,
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Students.Add(student);
            db.RosterSnapshots.Add(new RosterSnapshot
            {
                Id = Guid.CreateVersion7(),
                StudentId = student.Id,
                SchoolYear = 2027,
                YearLevel = 9,
                HouseCode = house,
                CompetitionAgeGroup = ageGroup,
                CompetitionCategory = category,
                ImportBatchId = batch.Id,
                EffectiveDate = new DateOnly(2027, 1, 30),
                IsCurrent = true,
            });
            return student.Id;
        }

        var banksiaBoys14 = new[]
        {
            AddSwimmer("Ari", "BANKSIA", "14", CompetitionCategory.M),
            AddSwimmer("Ben", "BANKSIA", "14", CompetitionCategory.M),
            AddSwimmer("Cai", "BANKSIA", "14", CompetitionCategory.M),
            AddSwimmer("Dev", "BANKSIA", "14", CompetitionCategory.M),
        };
        var waaBoy14 = AddSwimmer("Eli", "WATTLE", "14", CompetitionCategory.M);
        var banksiaGirl14 = AddSwimmer("Fay", "BANKSIA", "14", CompetitionCategory.F);
        var banksiaBoy15 = AddSwimmer("Gus", "BANKSIA", "15", CompetitionCategory.M);
        // ADR-0023: Open-category swimmers, who are listed in Boys and Girls alike
        // and can therefore be legged into two teams of one age scope.
        var banksiaOpen14 = new[]
        {
            AddSwimmer("Hal", "BANKSIA", "14", CompetitionCategory.S),
            AddSwimmer("Ivy", "BANKSIA", "14", CompetitionCategory.S),
            AddSwimmer("Jai", "BANKSIA", "14", CompetitionCategory.S),
            AddSwimmer("Kit", "BANKSIA", "14", CompetitionCategory.S),
        };

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Team Carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Open,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);

        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = "SW-4X50-MEDLEY-RELAY",
            Name = "Medley Relay 4x50m",
            Sport = "Swimming",
            MeasureType = MeasureType.Time,
            Units = "s",
            Format = EventFormat.Team,
            TeamSize = 4,
            LegsJson = JsonSerializer.Serialize(new[] { "Backstroke", "Breaststroke", "Butterfly", "Freestyle" }),
            ScoringWeight = 2,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.EventDefinitions.Add(definition);

        var ageEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = CompetitionCategory.M,
            Status = CarnivalEventStatus.Open,
        };
        ageEvent.AgeGroup = "14";
        var openEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = CompetitionCategory.M,
            Status = CarnivalEventStatus.Open,
        };
        openEvent.AgeGroup = AgeScope.OpenKey; // OPEN (all-ages) house relay — empty scope
        // The 14/F relay: the same definition, tier and age scope as ageEvent,
        // differing only by category — ADR-0023 §5's whole subject.
        var girlsAgeEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = CompetitionCategory.F,
            Status = CarnivalEventStatus.Open,
        };
        girlsAgeEvent.AgeGroup = "14";
        // The same pair one age scope up: OPEN is a sentinel age key, so these two
        // are siblings on the category axis exactly as the 14 pair above are.
        var girlsOpenEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = CompetitionCategory.F,
            Status = CarnivalEventStatus.Open,
        };
        girlsOpenEvent.AgeGroup = AgeScope.OpenKey;
        // The Social 14/M twin (§3.1) and a 14 relay open on the category axis —
        // the two divisions §5 has to answer for and the matrix can hold.
        var socialAgeEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = CompetitionCategory.M,
            Tier = EventTier.Social,
            Status = CarnivalEventStatus.Open,
        };
        socialAgeEvent.AgeGroup = "14";
        var noCategoryAgeEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definition.Id,
            Category = null,
            Status = CarnivalEventStatus.Open,
        };
        noCategoryAgeEvent.AgeGroup = "14";
        db.CarnivalEvents.AddRange(
            ageEvent, openEvent, girlsAgeEvent, girlsOpenEvent, socialAgeEvent, noCategoryAgeEvent);
        await db.SaveChangesAsync();

        return new TeamScene(carnival.Id, ageEvent.Id, openEvent.Id, girlsAgeEvent.Id,
            girlsOpenEvent.Id, socialAgeEvent.Id, noCategoryAgeEvent.Id,
            banksiaBoys14, waaBoy14, banksiaGirl14, banksiaBoy15, banksiaOpen14);
    }

    [Fact]
    public async Task Create_team_entry_snapshots_each_leg_in_order_with_medley_strokes()
    {
        var s = await SeedAsync();
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Json(response);
        Assert.True(body.GetProperty("isTeam").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("studentId").ValueKind); // no single student
        Assert.Equal("BANKSIA", body.GetProperty("teamHouseCode").GetString());
        var legs = body.GetProperty("legs").EnumerateArray().ToList();
        Assert.Equal(4, legs.Count);
        Assert.Equal([1, 2, 3, 4], legs.Select(l => l.GetProperty("legOrder").GetInt32()));
        Assert.Equal(
            ["Backstroke", "Breaststroke", "Butterfly", "Freestyle"],
            legs.Select(l => l.GetProperty("stroke").GetString()));
        Assert.Equal(s.BanksiaBoys14, legs.Select(l => l.GetProperty("studentId").GetGuid()).ToArray());

        // One Entry with four RelayLegs, no StudentId, house set.
        var entry = await CarnivalSeed.QueryAsync(_factory, db => db.Entries
            .Include(e => e.RelayLegs).SingleAsync(e => e.CarnivalEventId == s.AgeEventId));
        Assert.Null(entry.StudentId);
        Assert.Equal("BANKSIA", entry.HouseCode);
        Assert.Equal(4, entry.RelayLegs.Count);
    }

    [Fact]
    public async Task Wrong_number_of_swimmers_is_400()
    {
        var s = await SeedAsync();
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14.Take(3).ToArray() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TeamSizeMismatch);
    }

    [Fact]
    public async Task The_heat_is_bounded_and_the_lane_is_refused_on_the_team_path_too()
    {
        // The team path takes create-time placement exactly as the individual
        // path does, and a relay is scored on the same ladder inside the same
        // heat (ADR-0015 §1) — so it takes the same posture. Two create paths,
        // one rule; validating only one of them validates neither.
        var s = await SeedAsync();

        var badHeat = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14, heat = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badHeat.StatusCode);
        await badHeat.AssertErrorAsync(ErrorCodes.EntryHeatInvalid);

        // A lane is refused whatever it says — nonsense or plausible alike —
        // because creating an entry names no race for it to be a position in
        // (ADR-0015 §3); lanes are assigned at marshalling.
        foreach (var lane in new[] { int.MaxValue, 5 })
        {
            var badLane = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
                new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14, lane });
            Assert.Equal(HttpStatusCode.BadRequest, badLane.StatusCode);
            await badLane.AssertErrorAsync(ErrorCodes.EntryLaneInvalid);
        }

        // No refusal consumed the house's one team slot for the event, and a
        // heat alone still creates the team.
        var ok = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14, heat = 2 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("heat").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lane").ValueKind);
    }

    [Fact]
    public async Task Duplicate_swimmer_across_legs_is_400()
    {
        var s = await SeedAsync();
        var dupes = new[] { s.BanksiaBoys14[0], s.BanksiaBoys14[0], s.BanksiaBoys14[1], s.BanksiaBoys14[2] };
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = dupes });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.DuplicateTeamMember);
    }

    [Fact]
    public async Task Swimmer_from_another_house_is_422()
    {
        var s = await SeedAsync();
        var mixed = new[] { s.BanksiaBoys14[0], s.BanksiaBoys14[1], s.BanksiaBoys14[2], s.WaaBoy14 };
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = mixed });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("house", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_category_and_wrong_age_swimmers_are_422()
    {
        var s = await SeedAsync();
        var withGirl = new[] { s.BanksiaBoys14[0], s.BanksiaBoys14[1], s.BanksiaBoys14[2], s.BanksiaGirl14 };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId), new { houseCode = "BANKSIA", studentIds = withGirl })).StatusCode);

        var withOlder = new[] { s.BanksiaBoys14[0], s.BanksiaBoys14[1], s.BanksiaBoys14[2], s.BanksiaBoy15 };
        var ageResp = await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId), new { houseCode = "BANKSIA", studentIds = withOlder });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ageResp.StatusCode);
        Assert.Contains("age group", await ageResp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Open_relay_accepts_a_mixed_age_team()
    {
        var s = await SeedAsync();
        // The 15-year-old is ineligible for the 14 relay but fine for the OPEN one.
        var mixedAges = new[] { s.BanksiaBoys14[0], s.BanksiaBoys14[1], s.BanksiaBoys14[2], s.BanksiaBoy15 };
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.OpenEventId),
            new { houseCode = "BANKSIA", studentIds = mixedAges });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("OPEN", (await Json(response)).GetProperty("ageGroup").GetString());
    }

    [Fact]
    public async Task One_team_per_house_second_is_409()
    {
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
        var second = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already has a team", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_client_key_becomes_the_team_entry_id_and_a_replay_lands_one_team()
    {
        // The server half of the `team.create` outbox kind (ADR-0039 note A4, on
        // the ADR-0010 shape): the marshal's capture-time UUIDv7 is both the
        // Idempotency-Key and the entry id, so a redrained relay is one team and
        // one set of legs — never a second team refused as TEAM_EXISTS.
        var s = await SeedAsync();
        var key = Guid.CreateVersion7();
        HttpRequestMessage Keyed() => new(HttpMethod.Post, TeamPath(s.CarnivalId, s.AgeEventId))
        {
            Content = JsonContent.Create(new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 }),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };

        var first = await _admin.SendAsync(Keyed());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(key, (await Json(first)).GetProperty("id").GetGuid());

        var replay = await _admin.SendAsync(Keyed());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(key, (await Json(replay)).GetProperty("id").GetGuid());

        var entries = await CarnivalSeed.QueryAsync(_factory, db => db.Entries
            .Include(e => e.RelayLegs)
            .Where(e => e.CarnivalEventId == s.AgeEventId).ToListAsync());
        Assert.Single(entries);
        Assert.Equal(4, entries[0].RelayLegs.Count);
    }

    [Fact]
    public async Task A_keyed_team_create_names_the_heat_it_was_captured_against()
    {
        // ADR-0039 §2: a relay occupies a lane like anyone, so the queued create
        // carries the heat the marshal was standing at and the server places it
        // there inside the create — which is what lets the item ride behind this
        // device's own queued mint instead of waiting for it (note A4).
        var s = await SeedAsync();
        var race = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{s.CarnivalId}/races/materialise",
            new { eventId = s.AgeEventId, label = "Relay heat 1" });
        Assert.Equal(HttpStatusCode.Created, race.StatusCode);
        var raceId = (await Json(race)).GetProperty("race").GetProperty("id").GetGuid();

        var create = new HttpRequestMessage(HttpMethod.Post, TeamPath(s.CarnivalId, s.AgeEventId))
        {
            Content = JsonContent.Create(
                new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14, raceId }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        var response = await _admin.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(raceId, (await Json(response)).GetProperty("raceId").GetGuid());
    }

    [Fact]
    public async Task Category_override_on_a_team_entry_is_refused()
    {
        var s = await SeedAsync();
        var create = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        // ADR-0008: the team's SnapshotCategory is synthesised from the event's
        // division, so there is no per-student category an override could correct.
        var refused = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/entries/{entryId}/category-override",
            new { category = "F", reason = "Requested by parents" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.CategoryOverrideNotApplicable);
    }

    [Fact]
    public async Task A_team_entry_carries_a_normal_result()
    {
        var s = await SeedAsync();
        var create = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        // The team's single time flows through the ordinary result path (an
        // EventAdministrator may submit — ADR-0009).
        var submit = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "2:11.90" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        var result = await _admin.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        Assert.Equal(entryId, (await Json(result)).GetProperty("entryId").GetGuid());
    }

    [Fact]
    public async Task Event_projection_exposes_team_format_and_legs()
    {
        var s = await SeedAsync();
        var response = await _admin.GetAsync($"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Team", body.GetProperty("format").GetString());
        Assert.Equal(4, body.GetProperty("teamSize").GetInt32());
        Assert.Equal(
            ["Backstroke", "Breaststroke", "Butterfly", "Freestyle"],
            body.GetProperty("legs").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task School_houses_endpoint_lists_houses()
    {
        var response = await _admin.GetAsync("/api/v1/school/houses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var houses = (await Json(response)).GetProperty("houses")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.NotEmpty(houses); // configured, or the default four
    }

    // §9.3: removing a relay must leave the swimmers recoverable from the audit
    // trail — the prior-values snapshot has to carry the legs, not an empty list.
    [Fact]
    public async Task Removing_a_team_entry_audits_the_legs_in_prior_values()
    {
        var s = await SeedAsync();
        var create = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        var remove = await _admin.DeleteAsync(
            $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/entries/{entryId}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == entryId.ToString() && a.Action == AuditActions.EntryRemove));
        Assert.NotNull(audit.PriorValuesJson);
        var prior = JsonDocument.Parse(audit.PriorValuesJson!).RootElement;
        var legs = prior.GetProperty("Legs").EnumerateArray().ToList();
        Assert.Equal(4, legs.Count);
        // Audit JSON keeps the projection's own casing (no camelCase policy) —
        // uniformly PascalCase since K6 made the projection a record (EntryDto).
        Assert.Equal(s.BanksiaBoys14, legs.Select(l => l.GetProperty("StudentId").GetGuid()).ToArray());
        Assert.Equal(
            ["Ari Swift", "Ben Swift", "Cai Swift", "Dev Swift"],
            legs.Select(l => l.GetProperty("DisplayName").GetString()));
    }

    // ADR-0008: a team entry's StudentId is null. The already-entered subquery
    // excludes those nulls so the search can never be emptied by a NOT IN over
    // a null-bearing set.
    [Fact]
    public async Task Eligible_student_search_still_returns_students_after_a_team_entry()
    {
        var s = await SeedAsync();
        var path = $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/eligible-students";
        var before = await Json(await _admin.GetAsync(path));
        // four BANKSIA + one WATTLE 14/M swimmer, plus the four Open-category
        // swimmers the ADR-0023 widening lists in the Boys division too
        // (K11 page shape: { students, truncated, limit })
        Assert.Equal(9, before.GetProperty("students").EnumerateArray().Count());

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);

        // The relay's swimmers hold no individual entry, so they all remain
        // searchable; the team entry itself must not filter anyone out.
        var after = await Json(await _admin.GetAsync(path));
        Assert.Equal(9, after.GetProperty("students").EnumerateArray().Count());
    }

    // --- ADR-0023: the widening on legs, and the §5 rule one axis narrower -----

    [Fact]
    public async Task An_open_category_swimmer_may_leg_a_house_team_of_another_category()
    {
        // §5: CheckLeg calls CheckDivision, so the widening reaches relay legs
        // with no extra code — which is why it belongs in EligibilityService.
        var s = await SeedAsync();

        // Nothing else widened: a category-F swimmer is still refused by the
        // Boys relay, on the leg that names her.
        var mixed = new[]
        {
            s.BanksiaOpenCategory14[0], s.BanksiaOpenCategory14[1], s.BanksiaOpenCategory14[2], s.BanksiaGirl14,
        };
        var refused = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = mixed });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.CategoryIneligible);
        Assert.Equal(4, (await refused.ErrorContextAsync("leg")).GetInt32());

        // ADR-0026 §2: the leg refusal names the relay's division, not Fay's category.
        var legDetail = await refused.ErrorDetailAsync();
        Assert.Contains("Boys", legDetail);
        Assert.DoesNotContain("Girls", legDetail);

        // The Open-category swimmers make BANKSIA's Girls 14 team — the widening,
        // with the ADR-0008 house rule and the snapshots untouched.
        var girls = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.GirlsAgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 });
        Assert.Equal(HttpStatusCode.Created, girls.StatusCode);
        var legs = await CarnivalSeed.QueryAsync(_factory, db => db.RelayLegs
            .Where(l => l.Entry.CarnivalEventId == s.GirlsAgeEventId).ToListAsync());
        Assert.All(legs, l => Assert.Equal(CompetitionCategory.S, l.SnapshotCategory));
    }

    [Fact]
    public async Task A_swimmer_may_not_be_legged_into_two_teams_that_differ_only_by_category()
    {
        // The hole §1 opens: one swimmer contributing twice to their house from
        // the Boys 14 and the Girls 14 relay.
        var s = await SeedAsync();
        var girls = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.GirlsAgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 });
        Assert.Equal(HttpStatusCode.Created, girls.StatusCode);

        var boys = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 });
        Assert.Equal(HttpStatusCode.Conflict, boys.StatusCode);
        await boys.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
        Assert.Equal(1, (await boys.ErrorContextAsync("leg")).GetInt32());
        Assert.Equal(s.GirlsAgeEventId, (await boys.ErrorContextAsync("carnivalEventId")).GetGuid());
        Assert.Equal("F", (await boys.ErrorContextAsync("category")).GetString());

        // Nothing was written: the refusal happens before the team is added.
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.CarnivalEventId == s.AgeEventId)));
    }

    [Fact]
    public async Task A_swimmer_may_still_swim_the_age_band_relay_and_the_all_ages_one()
    {
        // ADR-0008 point 2 is ratified and §5 is scoped to protect it: the
        // all-ages relay exists precisely so a 14-year-old swims both. The age
        // scope is held fixed, so these two are not siblings.
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.OpenEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
    }

    [Fact]
    public async Task A_swimmer_may_leg_both_the_competitive_and_the_social_relay()
    {
        // §3.1, ratified narrow 2026-08-18: the tier is part of the key, so the
        // Social version of one relay is a second swim. Pinned on legs too.
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.SocialAgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
    }

    [Fact]
    public async Task The_leg_refusal_names_the_category_of_the_team_the_swimmer_is_in()
    {
        // A division open on the category axis is still a division, and the
        // refusal may not drop what it cannot spell (ADR-0022 §1). The age half
        // is never in doubt on this path — §5 holds the age scope fixed.
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.NoCategoryAgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 })).StatusCode);

        var refused = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 });
        await refused.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
        Assert.Equal(s.NoCategoryAgeEventId, (await refused.ErrorContextAsync("carnivalEventId")).GetGuid());
        // Detail, exceptionally (ApiErrorAssertions): naming the division IS the
        // actionable half of this refusal (ADR-0023 §4), so only the name is
        // pinned — the sentence around it stays free copy.
        Assert.Contains("this relay's All categories team at this age",
            (await Json(refused)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Eligible_search_drops_a_swimmer_already_legged_into_a_sibling_relay_team()
    {
        // ADR-0023 §4's promise — "the common case never reaches a refusal" —
        // reaches relays only if the search looks at RelayLeg as well as
        // Entry.StudentId: otherwise the marshal picks all four and the 409
        // arrives all-or-nothing.
        var s = await SeedAsync();
        var path = $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/eligible-students";
        Assert.Equal(9, (await Json(await _admin.GetAsync(path)))
            .GetProperty("students").EnumerateArray().Count());

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.GirlsAgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 })).StatusCode);

        var after = (await Json(await _admin.GetAsync(path)))
            .GetProperty("students").EnumerateArray()
            .Select(e => e.GetProperty("studentId").GetGuid()).ToList();
        Assert.Equal(5, after.Count);
        Assert.All(s.BanksiaOpenCategory14, id => Assert.DoesNotContain(id, after));
    }

    [Fact]
    public async Task Eligible_search_still_offers_a_swimmer_legged_at_another_age_scope()
    {
        // The control for the test above: ADR-0008 point 2 gives a 14-year-old
        // the 14 relay AND the all-ages one, so suppressing across the age axis
        // would empty the all-ages team's own search.
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);

        var offered = (await Json(await _admin.GetAsync(
                $"/api/v1/carnivals/{s.CarnivalId}/events/{s.OpenEventId}/eligible-students")))
            .GetProperty("students").EnumerateArray()
            .Select(e => e.GetProperty("studentId").GetGuid()).ToList();
        Assert.All(s.BanksiaBoys14, id => Assert.Contains(id, offered));
    }

    [Fact]
    public async Task Eligible_search_drops_a_swimmer_already_legged_into_the_sibling_OPEN_relay()
    {
        // The OPEN sentinel is an age key like any other, so the two all-ages
        // relays suppress each other across the category axis — otherwise the
        // Boys OPEN marshal is offered a swimmer the Girls OPEN team already holds.
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.GirlsOpenEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaOpenCategory14 })).StatusCode);

        var offered = (await Json(await _admin.GetAsync(
                $"/api/v1/carnivals/{s.CarnivalId}/events/{s.OpenEventId}/eligible-students")))
            .GetProperty("students").EnumerateArray()
            .Select(e => e.GetProperty("studentId").GetGuid()).ToList();
        Assert.All(s.BanksiaOpenCategory14, id => Assert.DoesNotContain(id, offered));
        // The age-scope control still holds: the 14 relay is a different scope.
        Assert.All(s.BanksiaBoys14, id => Assert.Contains(id, offered));
    }

    [Fact]
    public async Task Team_entry_on_an_individual_event_is_400()
    {
        // A plain individual event from the shared seed.
        var indiv = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(TeamPath(indiv.CarnivalId, indiv.EventId),
            new { houseCode = "BANKSIA", studentIds = new[] { indiv.EligibleBoyId } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not a team event", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_individual_entry_into_a_relay_division_is_400_online_and_keyed()
    {
        var s = await SeedAsync();
        var path = $"/api/v1/carnivals/{s.CarnivalId}/events/{s.AgeEventId}/entries";

        var online = await _admin.PostAsJsonAsync(path, new { studentId = s.BanksiaBoys14[0] });
        Assert.Equal(HttpStatusCode.BadRequest, online.StatusCode);
        await online.AssertErrorAsync(ErrorCodes.NotAnIndividualEvent);

        var keyed = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { studentId = s.BanksiaBoys14[1] }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, keyed.StatusCode);
        await keyed.AssertErrorAsync(ErrorCodes.NotAnIndividualEvent);

        Assert.False(await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.AnyAsync(e => e.CarnivalEventId == s.AgeEventId)));
    }

    [Fact]
    public async Task A_house_code_in_another_case_is_the_same_team()
    {
        var s = await SeedAsync();
        var first = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "banksia", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("BANKSIA", (await Json(first)).GetProperty("teamHouseCode").GetString());

        var second = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "Banksia", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await second.AssertErrorAsync(ErrorCodes.TeamExists);
    }

    [Fact]
    public async Task A_team_create_refuses_a_legacy_team_that_differs_only_by_case()
    {
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 })).StatusCode);
        // A row written before house codes were canonicalised.
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var team = db.Entries.Single(e => e.CarnivalEventId == s.AgeEventId);
            team.HouseCode = "Banksia";
        });

        var again = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "BANKSIA", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.TeamExists);
    }

    [Fact]
    public async Task A_team_for_a_house_the_school_does_not_have_is_400()
    {
        var s = await SeedAsync();
        var response = await _admin.PostAsJsonAsync(TeamPath(s.CarnivalId, s.AgeEventId),
            new { houseCode = "PHANTOM", studentIds = s.BanksiaBoys14 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HouseUnknown);
        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"],
            (await response.ErrorContextAsync("configuredHouses"))
                .EnumerateArray().Select(h => h.GetString()!));
        Assert.False(await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.AnyAsync(e => e.CarnivalEventId == s.AgeEventId)));
    }
}
