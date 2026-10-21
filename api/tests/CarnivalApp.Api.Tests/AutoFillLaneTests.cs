using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Idempotency;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0038 §2: with "auto-fill lanes" ticked, the SERVER gives a new entry in
/// a race-style event the next free lane, in the transaction that creates it.
///
/// The rules this file holds:
/// <list type="bullet">
/// <item>Lanes fill lowest-first in the event's LATEST race; a full race starts
/// the next one at lane 1, and the heat follows from the unchanged derivation —
/// so heats form in entry order without a tap.</item>
/// <item>An earlier race is never refilled. A gap there is a swap, a removal or
/// a DNS in a heat that may already have swum.</item>
/// <item>Race-style means <c>MeasureType.Time</c>. A long jump is never laned,
/// nor is anything in a carnival with the box unticked — but ADR-0039 §3 places
/// those entries in a heat all the same, as lane-less members.</item>
/// <item>Auto-fill is a CONVENIENCE; the entry is the record. A placement that
/// cannot be made leaves the entry created and unlaned rather than losing it.</item>
/// </list>
/// </summary>
public sealed class AutoFillLaneTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _recorder;

    public AutoFillLaneTests()
    {
        _admin = _factory.CreateClientAs("fill-admin", "CarnivalAdministrator");
        _recorder = _factory.CreateClientAs("fill-marshal", "Recorder");
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

    /// <summary>The create response, which ADR-0038 §2 requires to carry the placement.</summary>
    private async Task<JsonElement> EnterAsync(SeededCarnival seeded, Guid studentId, Guid? eventId = null)
    {
        var response = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId ?? seeded.EventId}/entries",
            new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
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

    private Task<int> MarshalAuditCountAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.EntryMarshal && a.EntityId == entryId.ToString()));

    /// <summary>The entry exists and is in no race: created, never placed.</summary>
    private async Task AssertUnplacedAsync(Guid entryId)
    {
        var stored = await StoredEntryAsync(entryId);
        Assert.Equal((null, null, null), ((Guid?)stored.RaceId, stored.Lane, stored.Heat));
    }

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

    /// <summary>
    /// Fills the division's only race, then arms a competing marshal to take lane
    /// 1 of the race the NEXT create mints — between the pick and the write.
    /// <see cref="RaceEndpoints.CohortMintTag"/> is that interleave point: the
    /// placement into a freshly minted race always opens a field, so the cohort
    /// query runs after the lane is chosen and before it is saved. Returns the
    /// competitor's entry — where it ends up IS the race the create was heading for.
    /// </summary>
    private async Task<Guid> StealSceneAsync(SeededCarnival seeded)
    {
        await ConfigureAsync(seeded.CarnivalId, laneCount: 2, autoFill: false);
        var thiefEntryId = (await EnterAsync(seeded, seeded.GirlId)).GetProperty("id").GetGuid();
        await ConfigureAsync(seeded.CarnivalId, laneCount: 2);
        await EnterAsync(seeded, seeded.EligibleBoyId);
        await EnterAsync(seeded, seeded.SecondEligibleBoyId);

        _factory.Race.ArmOnce(RaceEndpoints.CohortMintTag, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            // The race the create just minted and is about to place into.
            var minted = db.Races.Where(r => r.CarnivalId == seeded.CarnivalId)
                .OrderByDescending(r => r.SortOrder).First().Id;
            var entry = db.Entries.Single(e => e.Id == thiefEntryId);
            entry.RaceId = minted;
            entry.Lane = 1;
            entry.Heat = 2;
            db.SaveChanges();
        });
        return thiefEntryId;
    }

    // ---- Filling ------------------------------------------------------------

    [Fact]
    public async Task Each_new_entry_takes_the_next_free_lane_in_creation_order()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 3);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));

        // One race, minted by the first create; lanes 1 then 2; one heat, because
        // entries of one event in one race ARE a heat (ADR-0015).
        Assert.NotNull(first.RaceId);
        Assert.Equal(first.RaceId, second.RaceId);
        Assert.Equal((1, 1), (first.Lane, first.Heat));
        Assert.Equal((2, 1), (second.Lane, second.Heat));
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_full_race_starts_the_next_one_at_lane_one_with_the_next_heat()
    {
        // The cohort rule, unchanged: the overflow race mints heat 2, so the two
        // fields place separately instead of both minting a 1st.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 2);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        var third = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId));

        Assert.Equal(first.RaceId, second.RaceId);
        Assert.NotEqual(first.RaceId, third.RaceId);
        Assert.Equal((1, 2), (third.Lane, third.Heat));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_gap_in_an_earlier_race_is_never_refilled()
    {
        // ADR-0038: only the LATEST race is filled. Heat 1 may already have swum,
        // so a late entry must not be dropped into a lane a swap or a removal
        // vacated there — the marshal fills it by hand if they mean to.
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 2);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        var second = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        var third = Placement(await EnterAsync(seeded, seeded.GirlId));

        // Clear the second swimmer's lane, leaving lane 2 of race 1 empty.
        var cleared = await _recorder.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}"
            + $"/entries/{second.GetProperty("id").GetGuid()}/placement",
            new { raceId = first.RaceId, lane = (int?)null, heat = (int?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var fourth = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId));
        Assert.Equal(third.RaceId, fourth.RaceId); // race 2, not the gap in race 1
        Assert.Equal((2, 2), (fourth.Lane, fourth.Heat));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task An_added_race_is_filled_rather_than_bypassed()
    {
        // ADR-0038 amendment: a mint STAMPS the division on the race
        // (Race.CarnivalEventId), so a race a marshal added for a deliberate
        // split is findable while it is still empty. Without the stamp, race
        // membership is known only through entries and auto-fill would mint its
        // own race beside the marshal's — ignoring the split they just made.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));

        var added = await _recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/races/materialise",
            new { eventId = seeded.EventId, scheduleSlotId = (Guid?)null, label = "Heat 2" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var addedRace = (await Json(added)).GetProperty("race");
        // Empty: every entry of the division is already laned, so the sweep moved
        // nobody. This is exactly the race that used to be invisible.
        Assert.Equal(0, addedRace.GetProperty("entries").GetArrayLength());
        var addedId = addedRace.GetProperty("id").GetGuid();
        Assert.Equal(seeded.EventId, addedRace.GetProperty("eventId").GetGuid());

        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        Assert.Equal(addedId, second.RaceId);       // the marshal's race, not a third one
        Assert.Equal((1, 2), (second.Lane, second.Heat));
        Assert.NotEqual(first.RaceId, second.RaceId);
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_race_with_no_stamp_still_counts_through_its_entries()
    {
        // No backfill: races that pre-date the column carry null, and the admin
        // race-create path (which names no event) always will. Entry membership
        // is still an attribution, so auto-fill fills such a race rather than
        // starting a second one beside it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        Assert.NotNull(first.RaceId);
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Races.Single(r => r.Id == first.RaceId!.Value).CarnivalEventId = null);

        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        Assert.Equal(first.RaceId, second.RaceId);
        Assert.Equal((2, 1), (second.Lane, second.Heat));
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- Where the LANE does not apply, but the heat still does --------------
    //
    // ADR-0039 §3 widened this path: a create with no raceId is placed whether or
    // not it can be laned, because an entry in no heat is a state the venue never
    // has. Auto-fill's scope (ADR-0038 §1) is now exactly the lane's.

    [Fact]
    public async Task A_field_event_is_never_laned_but_still_joins_a_heat()
    {
        // ADR-0038 §1: lanes are for race-style events only, and the measure type
        // answers "does this event have lanes" without an exception setting. A
        // long jump still has heats, so the entry is a MEMBER of one.
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8);

        var (raceId, lane, heat) = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        Assert.NotNull(raceId);
        Assert.Equal((null, 1), (lane, heat));
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task With_the_box_unticked_the_entry_joins_the_latest_heat_unlaned()
    {
        // The first create mints the division's heat and is its first member; the
        // second joins the same one, because the latest race is the one being
        // filled and fullness means nothing where there are no lanes to fill.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8, autoFill: false);

        var first = Placement(await EnterAsync(seeded, seeded.EligibleBoyId));
        Assert.NotNull(first.RaceId);
        Assert.Equal((null, 1), (first.Lane, first.Heat));

        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        Assert.Equal((first.RaceId, null, 1), second);
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_swum_race_is_closed_to_an_unlaned_entry_too()
    {
        // The swum-race rule is about re-ranking a contested field, not about
        // lanes: it closes the latest race for the unrequested path whether or
        // not the entry joining would carry a lane.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 8, autoFill: false);

        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        await RecordAsync(first.GetProperty("id").GetGuid(), ResultStatus.Ok, 33.1m);

        var late = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        Assert.NotEqual(Placement(first).RaceId, late.RaceId);
        Assert.Equal((null, 2), (late.Lane, late.Heat));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- Team entries --------------------------------------------------------

    [Fact]
    public async Task Team_entries_are_auto_filled_too()
    {
        var (carnivalId, eventId, houses) = await RelaySceneAsync();
        await ConfigureAsync(carnivalId, laneCount: 2);

        var lanes = new List<(Guid? RaceId, int? Lane, int? Heat)>();
        foreach (var (house, swimmers) in houses)
        {
            var response = await _recorder.PostAsJsonAsync(
                $"/api/v1/carnivals/{carnivalId}/events/{eventId}/team-entries",
                new { houseCode = house, studentIds = swimmers });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            lanes.Add(Placement(await Json(response)));
        }

        Assert.Equal(lanes[0].RaceId, lanes[1].RaceId);
        Assert.Equal([1, 2], lanes.Select(l => l.Lane));
        Assert.Equal([1, 1], lanes.Select(l => l.Heat));
    }

    // ---- The outbox ----------------------------------------------------------

    [Fact]
    public async Task A_replayed_outbox_create_returns_the_same_placement()
    {
        // ADR-0010: the stored response IS the reply to every redrain, so the
        // lane the server chose has to be inside it — a replay that answered
        // "no lane" would leave the device showing an unlaned swimmer forever.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create() =>
            new(HttpMethod.Post, $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
            {
                Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId }),
                Headers = { { IdempotencyService.HeaderName, entryId.ToString() } },
            };

        var first = await _recorder.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var placed = Placement(await Json(first));
        Assert.Equal((1, 1), (placed.Lane, placed.Heat));

        var replay = await _recorder.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(placed, Placement(await Json(replay)));

        // One entry, one race: the replay re-ran nothing.
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
        Assert.Equal(placed.RaceId, (await StoredEntryAsync(entryId)).RaceId);
    }

    // ---- The trail -----------------------------------------------------------

    [Fact]
    public async Task The_trail_says_a_person_created_the_entry_and_the_system_chose_the_lane()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var entryId = Guid.CreateVersion7();

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
        {
            Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId }),
            Headers = { { IdempotencyService.HeaderName, entryId.ToString() } },
        };
        request.Headers.Add(CapturedActor.SubjectHeader, "marshal-on-deck");
        Assert.Equal(HttpStatusCode.Created, (await _recorder.SendAsync(request)).StatusCode);

        var marshalled = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.EntryMarshal && a.EntityId == entryId.ToString()));
        // The creating actor, not a system principal: a person put this swimmer
        // in the event and the flag says the lane was not their choice.
        Assert.Equal("marshal-on-deck", marshalled.ActorSubject);
        Assert.Contains("\"autoFilled\":true", marshalled.NewValuesJson!.Replace(" ", ""));
        Assert.Contains("\"lane\":1", marshalled.NewValuesJson.Replace(" ", ""));

        // …and the race the create had to mint says the same, so a race row that
        // no organiser tapped for is explained where it appears.
        var minted = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.RaceMaterialise));
        Assert.Contains("\"autoFilled\":true", minted.NewValuesJson!.Replace(" ", ""));
    }

    // ---- The failure posture (ADR-0038 implementation note) --------------------

    [Fact]
    public async Task A_placement_that_never_wins_still_creates_the_entry()
    {
        // The entry is the record; the lane is a convenience. A competitor that
        // keeps minting heats in this event exhausts the placement's retries —
        // and the create must survive it, unlaned, rather than being lost.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

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

        var created = await EnterAsync(seeded, seeded.EligibleBoyId);
        _factory.Race.Disarm();

        var placement = Placement(created);
        Assert.Null(placement.Lane);
        Assert.Null(placement.Heat);

        // The entry exists and is the one the response described.
        var stored = await StoredEntryAsync(created.GetProperty("id").GetGuid());
        Assert.Null(stored.Lane);
        Assert.Equal(placement.RaceId, stored.RaceId);
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.EntryMarshal)));

        // The race minted for the placement goes with it, and the trail says why.
        Assert.Equal(0, await RaceCountAsync(seeded.CarnivalId));
        var discarded = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.RaceDelete));
        Assert.Contains("\"autoFillAbandoned\":true", discarded.NewValuesJson!.Replace(" ", ""));
    }

    [Fact]
    public async Task A_keyed_create_whose_placement_never_wins_leaves_no_empty_race()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
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

        var entryId = Guid.CreateVersion7();
        var created = await _recorder.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
        {
            Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId }),
            Headers = { { IdempotencyService.HeaderName, entryId.ToString() } },
        });
        _factory.Race.Disarm();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertUnplacedAsync(entryId);
        Assert.Equal(0, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_retry_that_lands_in_another_race_discards_the_race_it_minted()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);
        var competitorRaceId = Guid.CreateVersion7();

        // Attempt 1 mints a race and loses its save; meanwhile a competitor adds a later race,
        // which attempt 2 picks instead.
        _factory.Race.ArmOnce(RaceEndpoints.CohortMintTag, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            var after = db.Races.Where(r => r.CarnivalId == seeded.CarnivalId).Max(r => r.SortOrder);
            db.Races.Add(new Race
            {
                Id = competitorRaceId,
                CarnivalId = seeded.CarnivalId,
                CarnivalEventId = seeded.EventId,
                SortOrder = after + 10,
            });
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).Version++;
            db.SaveChanges();
        });

        var created = await EnterAsync(seeded, seeded.EligibleBoyId);
        _factory.Race.Disarm();

        var placed = Placement(created);
        Assert.Equal((competitorRaceId, 1), (placed.RaceId, placed.Lane));
        var remaining = await CarnivalSeed.QueryAsync(_factory, db => db.Races
            .Where(r => r.CarnivalId == seeded.CarnivalId).Select(r => r.Id).ToListAsync());
        Assert.Equal([competitorRaceId], remaining);
        var discarded = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.RaceDelete));
        Assert.Contains("\"autoFillAbandoned\":true", discarded.NewValuesJson!.Replace(" ", ""));
    }

    // ---- What the mint must NOT move (review amendment) ----------------------

    [Fact]
    public async Task An_auto_filled_mint_leaves_the_division_s_unraced_entries_alone()
    {
        // ADR-0038 §2 promises auto-fill never moves an existing swimmer. The
        // materialisation core sweeps a division's unraced entries into the race
        // it mints — the right answer for "Add race", and the wrong one here: the
        // sweep would race an entry nobody asked to place, and the cohort rule
        // would then stamp it with a heat.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 1);
        // Seeded, not created: since ADR-0039 §3 a create is never raceless, so
        // the row the sweep must leave alone is now the legacy one.
        var strangerId = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);

        // The FIRST auto-filled create mints a race — with nobody in it but its
        // own entry.
        var first = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));
        Assert.Equal((1, 1), (first.Lane, first.Heat));
        await AssertUnplacedAsync(strangerId);

        // …and so does the overflow mint, one full race later.
        var second = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId));
        Assert.NotEqual(first.RaceId, second.RaceId);
        Assert.Equal((1, 2), (second.Lane, second.Heat));
        await AssertUnplacedAsync(strangerId);
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task A_scratched_swimmer_is_not_dragged_into_the_next_race()
    {
        // The same rule from the deck's point of view: "Remove from race" is a
        // marshal's decision, and the next create must not undo it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 1);

        var scratchedId = (await EnterAsync(seeded, seeded.EligibleBoyId)).GetProperty("id").GetGuid();
        var second = Placement(await EnterAsync(seeded, seeded.SecondEligibleBoyId));

        var cleared = await _recorder.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{scratchedId}/placement",
            new { raceId = (Guid?)null, lane = (int?)null, heat = (int?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        // The latest race is full, so this create mints — and takes nobody with it.
        var third = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId));
        Assert.NotEqual(second.RaceId, third.RaceId);
        Assert.Equal((1, 3), (third.Lane, third.Heat));
        await AssertUnplacedAsync(scratchedId);
        Assert.Equal(3, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- A race that has swum (review amendment) -----------------------------

    [Fact]
    public async Task A_race_that_has_swum_is_closed_and_the_late_entry_starts_the_next()
    {
        // A late entry dropped into a heat that has already been swum re-ranks a
        // scored field. Any result on any of the race's entries closes it — a DNS
        // included, because that is a heat that ran.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        // Lanes free and nothing recorded: the second entry joins the same race,
        // which is what makes the result — not the free lane — the thing that
        // closes it.
        var second = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.Equal(Placement(first).RaceId, Placement(second).RaceId);
        Assert.Equal((2, 1), (Placement(second).Lane, Placement(second).Heat));

        await RecordAsync(first.GetProperty("id").GetGuid(), ResultStatus.Ok, 31.5m);
        await RecordAsync(second.GetProperty("id").GetGuid(), ResultStatus.Dns, null);

        var third = Placement(await EnterAsync(seeded, seeded.SCategoryStudentId));
        Assert.NotEqual(Placement(first).RaceId, third.RaceId);
        Assert.Equal((1, 2), (third.Lane, third.Heat));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- The retry budget (review amendment) ---------------------------------

    [Fact]
    public async Task A_permanent_refusal_costs_one_attempt_not_the_whole_budget()
    {
        // A lost race is worth re-picking for; an ambiguous heat is not — it
        // answers the same way however many times it is asked. Counting the
        // latest-race pick counts the attempts.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await ConfigureAsync(seeded.CarnivalId, laneCount: 4);

        await EnterAsync(seeded, seeded.EligibleBoyId);
        var oddId = (await EnterAsync(seeded, seeded.SecondEligibleBoyId)).GetProperty("id").GetGuid();
        // Two heats of one event in one race: only an explicit override produces
        // it, and derivation refuses to guess which field a new lane belongs to.
        await CarnivalSeed.MutateAsync(_factory, db => db.Entries.Single(e => e.Id == oddId).Heat = 5);

        _factory.Race.CountFrom(RaceEndpoints.AutoFillPickTag);
        var late = await EnterAsync(seeded, seeded.SCategoryStudentId);
        Assert.Equal(1, _factory.Race.Counted);

        // The entry is still the record: 201, unlaned, for a marshal to place.
        Assert.Equal((null, null, null), Placement(late));
        await AssertUnplacedAsync(late.GetProperty("id").GetGuid());
        Assert.Equal(1, await RaceCountAsync(seeded.CarnivalId));
    }

    // ---- Losing the picked lane (review §8) ----------------------------------
    //
    // NOT covered, and accepted as a residual: two devices whose creates BOTH
    // find the latest race full mint two races simultaneously. Both mints read
    // the same SortOrder, both save, and each create places into its own — so one
    // of them is a stranded race holding a single swimmer with a stamped heat.
    // It cannot be reproduced here: the HTTP tests share one in-memory SQLite
    // connection, which serialises the two mints instead of interleaving them,
    // and RaceInterceptor's competing writer runs INSIDE the handler it
    // interrupts rather than beside it. Recorded in ADR-0038 rather than pinned
    // by a test that would only assert the serialised outcome.

    [Fact]
    public async Task A_lane_taken_between_the_pick_and_the_write_falls_to_the_next_lane()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        var thiefId = await StealSceneAsync(seeded);

        var created = await EnterAsync(seeded, seeded.SCategoryStudentId);
        _factory.Race.Disarm();

        // Lane 1 of the minted race went to the competitor; ours took lane 2 of
        // the same race and joined the heat they opened.
        var placed = Placement(created);
        var thief = await StoredEntryAsync(thiefId);
        Assert.Equal((thief.RaceId, 2, 2), (placed.RaceId, placed.Lane, placed.Heat));
        Assert.Equal(1, thief.Lane);

        // One marshalling row: the losing attempt's audit went with its rollback,
        // and the row agrees with what the create answered.
        var stored = await StoredEntryAsync(created.GetProperty("id").GetGuid());
        Assert.Equal((placed.RaceId, placed.Lane, placed.Heat), (stored.RaceId, stored.Lane, stored.Heat));
        Assert.Equal(1, await MarshalAuditCountAsync(stored.Id));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
    }

    [Fact]
    public async Task The_keyed_create_loses_the_lane_the_same_way_and_stores_what_it_answered()
    {
        // The ADR-0010 path: the whole create, mint and placement run inside one
        // idempotency transaction, so the losing attempt unwinds to its savepoint
        // and the stored response is the lane that actually landed.
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null);
        var thiefId = await StealSceneAsync(seeded);
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create() =>
            new(HttpMethod.Post, $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries")
            {
                Content = JsonContent.Create(new { studentId = seeded.SCategoryStudentId }),
                Headers = { { IdempotencyService.HeaderName, entryId.ToString() } },
            };

        var created = await _recorder.SendAsync(Create());
        _factory.Race.Disarm();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var placed = Placement(await Json(created));
        var thief = await StoredEntryAsync(thiefId);
        Assert.Equal((thief.RaceId, 2, 2), (placed.RaceId, placed.Lane, placed.Heat));

        // The stored response replays the same placement, and the row agrees with it.
        var replay = await _recorder.SendAsync(Create());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(placed, Placement(await Json(replay)));
        var stored = await StoredEntryAsync(entryId);
        Assert.Equal((placed.RaceId, placed.Lane, placed.Heat), (stored.RaceId, stored.Lane, stored.Heat));
        Assert.Equal(1, await MarshalAuditCountAsync(entryId));
        Assert.Equal(2, await RaceCountAsync(seeded.CarnivalId));
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
            SourceFileName = "relay-fill.csv",
            SourceFileHash = "relay-fill-hash",
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
                ExternalStudentId = $"RF-{first}",
                Surname = "Reed",
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
                CompetitionAgeGroup = "RF14",
                CompetitionCategory = CompetitionCategory.M,
                ImportBatchId = batch.Id,
                EffectiveDate = new DateOnly(2027, 1, 30),
                IsCurrent = true,
            });
            return student.Id;
        }

        var houses = new List<(string House, Guid[] Swimmers)>
        {
            ("BANKSIA", [AddSwimmer("Ari", "BANKSIA"), AddSwimmer("Ben", "BANKSIA")]),
            ("WATTLE", [AddSwimmer("Cai", "WATTLE"), AddSwimmer("Dev", "WATTLE")]),
        };

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Relay Fill",
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
            Code = "SW-2X50-FREE-RELAY-FILL",
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
        carnivalEvent.AgeGroup = "RF14";
        db.CarnivalEvents.Add(carnivalEvent);

        await db.SaveChangesAsync();
        return (carnival.Id, carnivalEvent.Id, houses);
    }
}
