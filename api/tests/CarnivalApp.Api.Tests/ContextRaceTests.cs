using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0039 §2: the create names its heat. A <c>raceId</c> on the entry create
/// is the heat the marshal is standing at, and the server places the new entry
/// there in the create's transaction.
///
/// The rules this file holds:
/// <list type="bullet">
/// <item>The named race is filled, not the latest one — an explicit context
/// beats ADR-0038's pick.</item>
/// <item>No fullness refusal and no swum-race gate: the marshal chose the heat,
/// so a full or already-swum one yields a MEMBER with no lane rather than a
/// refusal or an overflow race.</item>
/// <item>Heat derivation and the cohort rule are untouched — joining an empty
/// heat mints its number, joining a running one takes it.</item>
/// <item><c>RACE_NOT_IN_EVENT</c> for a race of another division, and for an id
/// no race of this carnival carries.</item>
/// <item>The failure posture is ADR-0038's: the entry is the record. A
/// placement that never wins leaves the entry created and OUTSIDE the heat that
/// was asked for, and the 201 says so.</item>
/// </list>
/// </summary>
public sealed class ContextRaceTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _recorder;

    public ContextRaceTests()
    {
        _admin = _factory.CreateClientAs("context-admin", "CarnivalAdministrator");
        _recorder = _factory.CreateClientAs("context-marshal", "Recorder");
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task ConfigureAsync(Guid carnivalId, int laneCount, bool autoFill = true)
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { laneCount, autoFillLanes = autoFill });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private async Task<JsonElement> EnterAsync(
        SeededCarnival seeded, Guid studentId, Guid? raceId = null, Guid? eventId = null)
    {
        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId ?? seeded.EventId}/entries",
            new { studentId, raceId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    /// <summary>The "+" chip's mint: a race of this division, stamped and empty.</summary>
    private async Task<Guid> AddHeatAsync(Guid carnivalId, Guid eventId, string label)
    {
        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/races/materialise",
            new { eventId, scheduleSlotId = (Guid?)null, label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("race").GetProperty("id").GetGuid();
    }

    private static (Guid? RaceId, int? Lane, int? Heat) Placement(JsonElement entry) => (
        entry.GetProperty("raceId").ValueKind == JsonValueKind.Null
            ? null : entry.GetProperty("raceId").GetGuid(),
        entry.GetProperty("lane").ValueKind == JsonValueKind.Null
            ? null : entry.GetProperty("lane").GetInt32(),
        entry.GetProperty("heat").ValueKind == JsonValueKind.Null
            ? null : entry.GetProperty("heat").GetInt32());

    private Task<Entry> StoredEntryAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Entries.AsNoTracking().SingleAsync(e => e.Id == entryId));

    private Task<int> RaceCountAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Races.CountAsync(r => r.CarnivalId == carnivalId));

    private Task<int> EntryCountAsync(Guid eventId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Entries.CountAsync(e => e.CarnivalEventId == eventId));

    private Task<string> MarshalAuditAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, async db => (await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.EntryMarshal && a.EntityId == entryId.ToString()))
            .NewValuesJson!.Replace(" ", ""));

    /// <summary>A recorded swim, seeded directly — the capture path is not what is under test.</summary>
    private Task RecordAsync(Guid entryId, ResultStatus status, decimal? value) =>
        CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entryId,
            Status = status,
            MeasuredValue = value,
            LifecycleState = ResultLifecycleState.Provisional,
            CurrentRevision = 1,
            RecordedBySubject = "seed",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));

    // ---- The named heat is the one that is filled ----------------------------

    [Fact]
    public async Task The_named_heat_takes_the_lane_even_when_a_later_heat_exists()
    {
        // ADR-0038's unrequested auto-fill would have taken the LATEST race. An
        // explicit context beats it: the marshal is standing at heat 1.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        var heatTwo = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 2");

        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, first.RaceId));
        Assert.Equal(first.RaceId, second.RaceId);
        Assert.Equal((2, 1), (second.Lane, second.Heat));
        Assert.NotEqual(heatTwo, second.RaceId);
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task An_empty_named_heat_mints_its_heat_number()
    {
        // The empty stamped race the "+" chip mints is a first-class heat: the
        // first entry into it opens the field and derivation numbers it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 6);

        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var entry = Placement(await EnterAsync(seeded, seeded.EligibleBoyId, raceId));

        Assert.Equal((raceId, 1, 1), entry);
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId)); // nothing was minted beside it
    }

    [Fact]
    public async Task A_full_named_heat_takes_a_member_with_no_lane()
    {
        // ADR-0039 §2: no fullness refusal for an explicit context. The marshal
        // asked for this heat, so the swimmer joins it — unlaned, which the row
        // shows — instead of being pushed into an overflow race they did not ask
        // for.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 1);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, first.RaceId));

        Assert.Equal((first.RaceId, null, 1), second);
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_named_heat_that_has_swum_still_takes_the_swimmer()
    {
        // No swum-race guard either: that rule closes a heat auto-fill PICKED,
        // and here the marshal picked it. A late swimmer joining a contested
        // field is their call, and the deck shows it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        await RecordAsync(first.GetProperty("id").GetGuid(), ResultStatus.Ok, 31.5m);

        var raceId = Placement(first).RaceId;
        var late = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, raceId));

        Assert.Equal((raceId, 2, 1), late);
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task With_auto_fill_off_the_named_heat_gives_membership_and_a_derived_heat()
    {
        // The lane is auto-fill's business; the HEAT is the create's. With the
        // box unticked the entry is a member of the heat with no lane, and the
        // marshal lanes it from the row control.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8, autoFill: false);

        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var entry = Placement(await EnterAsync(seeded, seeded.EligibleBoyId, raceId));

        Assert.Equal((raceId, null, 1), entry);
        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, raceId));
        Assert.Equal((raceId, null, 1), second); // one race, one field
    }

    [Fact]
    public async Task A_field_event_joins_the_heat_without_a_lane()
    {
        // Auto-fill is scoped to Time definitions; the context heat is not. A
        // long jump has heats and no lanes.
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8);

        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        Assert.Equal((raceId, null, 1),
            Placement(await EnterAsync(seeded, seeded.EligibleBoyId, raceId)));
    }

    // ---- Team entries --------------------------------------------------------

    [Fact]
    public async Task A_team_entry_joins_the_named_heat_too()
    {
        var (carnivalId, eventId, houses) = await RelaySceneAsync();
        await ConfigureAsync(carnivalId, laneCount: 4);

        async Task<JsonElement> EnterTeamAsync(int index, Guid? raceId)
        {
            var response = await _recorder.PostAsJsonAsync(
                $"/api/v1/carnivals/{carnivalId}/events/{eventId}/team-entries",
                new { houseCode = houses[index].House, studentIds = houses[index].Swimmers, raceId });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return await Json(response);
        }

        var first = Placement(await EnterTeamAsync(0, null));
        var later = await AddHeatAsync(carnivalId, eventId, "Heat 2");
        var second = Placement(await EnterTeamAsync(1, first.RaceId));

        Assert.Equal((first.RaceId, 2, 1), second);
        Assert.NotEqual(later, second.RaceId);
    }

    [Fact]
    public async Task A_team_entry_naming_another_divisions_race_is_refused()
    {
        var (carnivalId, eventId, houses) = await RelaySceneAsync();
        await ConfigureAsync(carnivalId, laneCount: 4);

        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/{eventId}/team-entries",
            new { houseCode = houses[0].House, studentIds = houses[0].Swimmers, raceId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RaceNotInEvent);
        Assert.Equal(0, await EntryCountAsync(eventId));
    }

    // ---- RACE_NOT_IN_EVENT ---------------------------------------------------

    [Fact]
    public async Task A_race_of_another_division_is_refused_and_creates_nothing()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var otherEventId = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var otherHeat = await AddHeatAsync(seeded.CarnivalId, otherEventId, "Girls heat 1");

        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId, raceId = otherHeat });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RaceNotInEvent);

        // Refused BEFORE the entry exists: a create that cannot name its heat
        // parks in the outbox rather than landing somewhere else.
        Assert.Equal(0, await EntryCountAsync(seeded.EventId));
    }

    [Fact]
    public async Task An_unknown_race_id_is_the_same_refusal()
    {
        // The client mints heats with its own ids, so a create that overtook its
        // mint names a race the server has never seen. Terminal, not a 404: the
        // create is answerable, the heat it names is not this event's.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId, raceId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RaceNotInEvent);
        Assert.Equal(0, await EntryCountAsync(seeded.EventId));
    }

    [Fact]
    public async Task A_race_of_another_carnival_is_not_reachable_either()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var elsewhere = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var strangerHeat = await AddHeatAsync(elsewhere.CarnivalId, elsewhere.EventId, "Heat 1");

        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId, raceId = strangerHeat });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RaceNotInEvent);
    }

    // ---- The outbox ----------------------------------------------------------

    [Fact]
    public async Task A_replayed_create_returns_the_placement_in_the_named_heat()
    {
        // ADR-0010: the stored response IS the reply to every redrain, so the
        // heat the create named and the lane the server chose are both inside it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create() =>
            new(HttpMethod.Post, $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
            {
                Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId, raceId }),
                Headers = { { IdempotencyService.HeaderName, entryId.ToString() } },
            };

        var first = await _recorder.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var placed = Placement(await Json(first));
        Assert.Equal((raceId, 1, 1), placed);

        var replay = await _recorder.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(placed, Placement(await Json(replay)));

        var stored = await StoredEntryAsync(entryId);
        Assert.Equal((raceId, 1, 1), ((Guid?)stored.RaceId, stored.Lane, stored.Heat));
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- The trail -----------------------------------------------------------

    [Fact]
    public async Task The_trail_says_the_marshal_chose_the_heat_and_the_system_the_lane()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");

        var laned = (await EnterAsync(seeded, seeded.EligibleBoyId, raceId)).GetProperty("id").GetGuid();
        var trail = await MarshalAuditAsync(laned);
        Assert.Contains("\"contextRace\":true", trail);
        Assert.Contains("\"autoFilled\":true", trail);

        // Auto-fill off: the marshal chose the heat and nobody chose a lane, so
        // only the one flag stands.
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4, autoFill: false);
        var unlaned = (await EnterAsync(seeded, seeded.SecondEligibleBoyId, raceId)).GetProperty("id").GetGuid();
        var second = await MarshalAuditAsync(unlaned);
        Assert.Contains("\"contextRace\":true", second);
        Assert.DoesNotContain("\"autoFilled\":true", second);
    }

    // ---- The failure posture -------------------------------------------------

    [Fact]
    public async Task A_placement_that_never_wins_leaves_the_entry_outside_the_named_heat()
    {
        // ADR-0038's posture, restated for an explicit context: the entry is the
        // record. A competitor minting heats in this event until the retry budget
        // is spent leaves the swimmer created and in NO race — the 201 says
        // raceId null rather than claiming a heat it never joined.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var raceId = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");

        var collisions = 0;
        void ArmAgain() => _factory.Race.ArmOnce(RaceEndpoints.CohortMintTag, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).Version++;
            db.SaveChanges();
            if (++collisions < 20) ArmAgain();
        });
        ArmAgain();

        var created = await EnterAsync(seeded, seeded.EligibleBoyId, raceId);
        _factory.Race.Disarm();

        Assert.Equal((null, null, null), Placement(created));
        var stored = await StoredEntryAsync(created.GetProperty("id").GetGuid());
        Assert.Equal((null, null, null), ((Guid?)stored.RaceId, stored.Lane, stored.Heat));
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.EntryMarshal)));
    }

    [Fact]
    public async Task An_ambiguous_named_heat_falls_back_to_the_unrequested_placement()
    {
        // The derivation refuses to guess which field a new member of this race
        // belongs to, and that refusal is permanent — but ADR-0039 §3 does not let
        // it leave the entry raceless. The create spends one more attempt on the
        // unrequested path, which picks the division's latest heat instead.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        var ambiguousId = Placement(first).RaceId!.Value;
        var oddId = (await EnterAsync(seeded, seeded.SecondEligibleBoyId, ambiguousId))
            .GetProperty("id").GetGuid();
        // Two heats of one event in one race: only an explicit override makes it.
        await CarnivalSeed.MutateAsync(_factory, db => db.Entries.Single(e => e.Id == oddId).Heat = 5);
        var laterHeat = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 2");

        var late = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId, ambiguousId));
        // Placed, not raceless, and not in the ambiguous race. Heat 2 is that
        // race's ORDINAL in the division (ADR-0039 amendment) — the override's 5
        // no longer pushes the next field to 6, because nothing holds heat 2.
        Assert.Equal((laterHeat, 1, 2), late);
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- The heat number is the race's ordinal (ADR-0039 amendment) ----------

    [Fact]
    public async Task Two_empty_heats_filled_in_reverse_take_their_ordinals()
    {
        // The defect this rule exists for: numbering by fill order gave the
        // SECOND chip heat 1, and the deck then re-sorted the chips under the
        // marshal's finger. The heat is the race's position in the division.
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 6);

        var one = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var two = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 2");

        var later = Placement(await EnterAsync(seeded, seeded.EligibleBoyId, two));
        var earlier = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, one));

        Assert.Equal((two, 1, 2), later);
        Assert.Equal((one, 1, 1), earlier);
    }

    [Fact]
    public async Task A_third_heat_added_and_filled_first_takes_heat_three()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 6);

        var one = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var two = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 2");
        var three = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 3");

        Assert.Equal(3, Placement(await EnterAsync(seeded, seeded.EligibleBoyId, three)).Heat);
        Assert.Equal(1, Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, one)).Heat);
        Assert.Equal(2, Placement(await EnterAsync(seeded, seeded.GirlId, two)).Heat);
    }

    [Fact]
    public async Task An_ordinal_another_race_already_holds_falls_back_to_fill_order()
    {
        // Legacy and overridden data: heat 2 is already heat 1's, so the second
        // chip cannot take its ordinal without merging two placing pools. Fill
        // order answers instead, and the chip shows the heat it really holds.
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 6);

        var one = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 1");
        var two = await AddHeatAsync(seeded.CarnivalId, seeded.EventId, "Heat 2");
        var oddId = (await EnterAsync(seeded, seeded.EligibleBoyId, one)).GetProperty("id").GetGuid();
        await CarnivalSeed.MutateAsync(_factory, db => db.Entries.Single(e => e.Id == oddId).Heat = 2);

        Assert.Equal(3, Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId, two)).Heat);
    }

    // ---- Seeding -------------------------------------------------------------

    /// <summary>
    /// A two-leg house relay with two full houses — the smallest scene that can
    /// put two team entries in one race.
    /// </summary>
    private async Task<(Guid CarnivalId, Guid EventId, List<(string House, Guid[] Swimmers)> Houses)>
        RelaySceneAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();

        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            SourceFileName = "relay-context.csv",
            SourceFileHash = "relay-context-hash",
            MappingVersion = "test",
            ImportedBySubject = "seeder",
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = ImportBatchStatus.Promoted,
        };
        db.ImportBatches.Add(batch);

        Guid AddSwimmer(string first, string house)
        {
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = $"RC-{first}",
                Surname = "Kerr",
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
                CompetitionAgeGroup = "RC14",
                CompetitionCategory = CompetitionCategory.M,
                ImportBatchId = batch.Id,
                EffectiveDate = new DateOnly(2027, 1, 30),
                IsCurrent = true,
            });
            return student.Id;
        }

        var houses = new List<(string House, Guid[] Swimmers)>
        {
            ("BANKSIA", [AddSwimmer("Eli", "BANKSIA"), AddSwimmer("Fin", "BANKSIA")]),
            ("WATTLE", [AddSwimmer("Gus", "WATTLE"), AddSwimmer("Hal", "WATTLE")]),
        };

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Relay Context",
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
            Code = "SW-2X50-FREE-RELAY-CONTEXT",
            Name = "Freestyle Relay 2x50m",
            Sport = "Swimming",
            MeasureType = MeasureType.Time,
            Units = "s",
            Format = EventFormat.Team,
            TeamSize = 2,
            CreatedAtUtc = DateTimeOffset.UtcNow,
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
        carnivalEvent.AgeGroup = "RC14";
        db.CarnivalEvents.Add(carnivalEvent);

        await db.SaveChangesAsync();
        return (carnival.Id, carnivalEvent.Id, houses);
    }
}
