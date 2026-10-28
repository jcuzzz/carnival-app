using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Races and marshalling over the wire (ADR-0015 phase 2).
///
/// The rules that carry this file's weight:
/// <list type="bullet">
/// <item>The HEAT IS DERIVED, never typed by the marshal (review decision 2):
/// entries of one event sharing one race ARE a heat, so the derivation — join
/// the co-members' heat, else open the event's next — is what makes a
/// phantom-heat scoring corruption structurally impossible.</item>
/// <item>The race owns the LANES (one entry per lane per race), the venue owns
/// the lane COUNT (ADR-0015 §6), and a lane without a race claims nothing.</item>
/// <item>Everything is correctable — a wrong heat is wrong house points, so an
/// entry with a recorded result stays reassignable — and races are logistics:
/// deleting one detaches entries but KEEPS their heats, because tidying a
/// logistics row must never move recorded results between placing pools.</item>
/// <item>The schedule stays advisory: deleting a slot or stream detaches the
/// races under it and deletes nothing downstream.</item>
/// </list>
/// </summary>
public sealed class RaceEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _recorder;

    public RaceEndpointsTests()
    {
        _admin = _factory.CreateClientAs("race-admin", "CarnivalAdministrator");
        _recorder = _factory.CreateClientAs("race-marshal", "Recorder");
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static string RacesPath(Guid carnivalId) => $"/api/v1/carnivals/{carnivalId}/races";

    private async Task<Guid> RaceAsync(Guid carnivalId, Guid? slotId = null, string? label = null)
    {
        var response = await _admin.PostAsJsonAsync(RacesPath(carnivalId),
            new { scheduleSlotId = slotId, label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An entry for a marshal to place: seeded unplaced, because since ADR-0039
    /// §2 the CREATE places every entry it makes and these scenes are about what
    /// marshalling and materialisation then do to a row in no race.
    /// </summary>
    private Task<Guid> EntryAsync(SeededCarnival seeded, Guid studentId, Guid? eventId = null) =>
        CarnivalSeed.UnplacedEntryAsync(_factory, eventId ?? seeded.EventId, studentId);

    private Task<HttpResponseMessage> PlaceAsync(
        SeededCarnival seeded, Guid entryId, Guid? raceId, int? lane = null, int? heat = null,
        Guid? eventId = null) =>
        _recorder.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId ?? seeded.EventId}/entries/{entryId}/placement",
            new { raceId, lane, heat });

    private Task<Entry> StoredEntryAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Entries.AsNoTracking().SingleAsync(e => e.Id == entryId));

    /// <summary>A second event instance in the seeded carnival (same definition, F category).</summary>
    private async Task<Guid> SecondEventAsync(SeededCarnival seeded)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events",
            new { eventDefinitionId = seeded.DefinitionId, ageGroup = seeded.PrimaryAgeGroup, category = "F" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid StreamId, Guid SlotId)> SlotAsync(Guid carnivalId, string kind = "Events")
    {
        var stream = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/schedule/streams", new { name = $"Pool-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, stream.StatusCode);
        var streamId = (await Json(stream)).GetProperty("id").GetGuid();
        var slot = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/schedule/streams/{streamId}/slots",
            new { label = "FREESTYLE EVENTS", kind, adjustmentCategory = kind == "Item" ? "Novelty 1" : null });
        Assert.Equal(HttpStatusCode.Created, slot.StatusCode);
        return (streamId, (await Json(slot)).GetProperty("id").GetGuid());
    }

    // ---- Race CRUD ---------------------------------------------------------

    [Fact]
    public async Task Race_crud_round_trips_with_and_without_a_slot()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (_, slotId) = await SlotAsync(seeded.CarnivalId);

        // A race needs no program (the schedule is advisory)…
        var bare = await RaceAsync(seeded.CarnivalId, label: "Race 1");
        // …and may run under a slot.
        var scheduled = await RaceAsync(seeded.CarnivalId, slotId, "Race 2");

        var list = (await Json(await _recorder.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal([bare, scheduled], list.Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("scheduleSlotId").ValueKind);
        Assert.Equal(slotId, list[1].GetProperty("scheduleSlotId").GetGuid());

        // PUT replaces: relabel and take off the program in one statement.
        var updated = await _admin.PutAsJsonAsync($"{RacesPath(seeded.CarnivalId)}/{scheduled}",
            new { label = "Race 2 (moved)" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await Json(updated);
        Assert.Equal("Race 2 (moved)", body.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("scheduleSlotId").ValueKind);

        var deleted = await _admin.DeleteAsync($"{RacesPath(seeded.CarnivalId)}/{bare}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Single((await Json(await _recorder.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray());
    }

    [Fact]
    public async Task A_race_cannot_claim_another_carnivals_slot_or_an_item_slot()
    {
        // Race.CarnivalId and Race.ScheduleSlotId are structurally independent
        // (the slot's carnival is two joins away) — this application check is
        // the only thing stopping a race pointing at another carnival's program.
        //
        // Every create here is UNKEYED, which is the whole point: interactively,
        // a slot the server cannot use is a typo and the 404 is the signal. The
        // keyed (outbox) path deliberately answers differently — see
        // A_keyed_create_whose_slot_vanished_creates_the_race_unscheduled below.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.SeedAsync(_factory);
        var (_, foreignSlot) = await SlotAsync(other.CarnivalId);

        var cross = await _admin.PostAsJsonAsync(RacesPath(seeded.CarnivalId),
            new { scheduleSlotId = foreignSlot });
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        await cross.AssertErrorAsync(ErrorCodes.ScheduleSlotNotFound);

        var (_, itemSlot) = await SlotAsync(seeded.CarnivalId, kind: "Item");
        var item = await _admin.PostAsJsonAsync(RacesPath(seeded.CarnivalId),
            new { scheduleSlotId = itemSlot });
        Assert.Equal(HttpStatusCode.Conflict, item.StatusCode);
        await item.AssertErrorAsync(ErrorCodes.ScheduleSlotKindMismatch);

        // Same guard on update.
        var race = await RaceAsync(seeded.CarnivalId);
        var moved = await _admin.PutAsJsonAsync($"{RacesPath(seeded.CarnivalId)}/{race}",
            new { scheduleSlotId = foreignSlot });
        Assert.Equal(HttpStatusCode.NotFound, moved.StatusCode);
    }

    [Fact]
    public async Task Race_creation_is_idempotent_under_a_client_key()
    {
        // ADR-0010 shape: the client-minted UUIDv7 becomes the race id, so a
        // drained retry never doubles the race.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var clientId = Guid.CreateVersion7();

        async Task<HttpResponseMessage> SendAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, RacesPath(seeded.CarnivalId))
            {
                Content = JsonContent.Create(new { label = "Race 1" }),
            };
            request.Headers.Add("Idempotency-Key", clientId.ToString());
            return await _admin.SendAsync(request);
        }

        var first = await SendAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(clientId, (await Json(first)).GetProperty("id").GetGuid());

        var replay = await SendAsync();
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(clientId, (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Single((await Json(await _admin.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray());
    }

    /// <summary>A keyed (outbox) race create — the client's UUIDv7 becomes the race id.</summary>
    private Task<HttpResponseMessage> KeyedRaceAsync(Guid carnivalId, Guid clientRaceId, Guid? slotId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RacesPath(carnivalId))
        {
            Content = JsonContent.Create(new { scheduleSlotId = slotId, label = "Race 1" }),
        };
        request.Headers.Add("Idempotency-Key", clientRaceId.ToString());
        return _admin.SendAsync(request);
    }

    [Fact]
    public async Task A_keyed_create_whose_slot_vanished_creates_the_race_unscheduled()
    {
        // The queued capture from the day: the item was minted at the pool and
        // the program was tidied while it sat in the outbox. The schedule is
        // advisory and never gating (ADR-0014 §1), so the race lands WITHOUT a
        // slot rather than being refused — and a refusal here would not park one
        // item, it would park the whole marshalled field, because every queued
        // assignment behind it names this race id.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.SeedAsync(_factory);
        // A slot that is not a slot of THIS carnival is indistinguishable from a
        // deleted one to the server, and is the same reading either way.
        var (_, vanished) = await SlotAsync(other.CarnivalId);
        var clientId = Guid.CreateVersion7();

        var created = await KeyedRaceAsync(seeded.CarnivalId, clientId, vanished);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        Assert.Equal(clientId, body.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("scheduleSlotId").ValueKind);

        // The trail has to explain a race that ASKED for a place on the program
        // and has none; the stored null alone would read as "never scheduled".
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == "race.create" && a.EntityId == clientId.ToString()));
        Assert.Contains("\"requestedScheduleSlotId\"", audit.NewValuesJson!);
        Assert.Contains(vanished.ToString(), audit.NewValuesJson);
        Assert.Contains("unscheduled rather than refused", audit.NewValuesJson);

        // Still idempotent: a redrain of the same item is the same race, once.
        var replay = await KeyedRaceAsync(seeded.CarnivalId, clientId, vanished);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(clientId, (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Single((await Json(await _admin.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray());

        // And it is a real race, not a placeholder: the assignments queued
        // behind it drain into it.
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(1, (await Placed(seeded, entry, clientId, lane: 1)).Heat);
    }

    [Fact]
    public async Task A_keyed_create_against_an_Item_slot_degrades_the_same_way()
    {
        // The other shape of "the program moved": the slot still exists but has
        // been retyped to a non-competition item, so no race runs under it. Same
        // reading for a queued capture, so the same outcome — and this half is
        // easy to miss, because it is a 409 rather than a 404.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (_, itemSlot) = await SlotAsync(seeded.CarnivalId, kind: "Item");
        var clientId = Guid.CreateVersion7();

        var created = await KeyedRaceAsync(seeded.CarnivalId, clientId, itemSlot);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(created)).GetProperty("scheduleSlotId").ValueKind);

        var stored = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Races.AsNoTracking().SingleAsync(r => r.Id == clientId));
        Assert.Null(stored.ScheduleSlotId);
    }

    [Fact]
    public async Task Deleting_a_race_detaches_entries_but_keeps_their_heats()
    {
        // Tidying a logistics row must never move recorded results between
        // placing pools: clearing the heat here would silently re-score the
        // event across one pool, which is exactly the corruption ADR-0015 §1
        // exists to prevent. Lane goes (it means nothing without the race);
        // heat stays.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, entry, race, lane: 3)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"{RacesPath(seeded.CarnivalId)}/{race}")).StatusCode);

        var stored = await StoredEntryAsync(entry);
        Assert.Null(stored.RaceId);
        Assert.Null(stored.Lane);
        Assert.Equal(1, stored.Heat);
    }

    [Fact]
    public async Task A_placement_landing_while_the_race_is_deleted_is_a_409_not_a_500()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        _factory.Race.ArmOnce("DELETE FROM \"Races\"", transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            db.Entries.Single(e => e.Id == entry).RaceId = race;
            db.SaveChanges();
        });

        var response = await _admin.DeleteAsync($"{RacesPath(seeded.CarnivalId)}/{race}");
        _factory.Race.Disarm();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);
        Assert.True(await CarnivalSeed.QueryAsync(_factory, db => db.Races.AnyAsync(r => r.Id == race)));
    }

    // ---- Heat derivation (review decision 2) --------------------------------

    [Fact]
    public async Task The_heat_is_derived_entries_of_one_event_sharing_one_race_are_one_heat()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race1 = await RaceAsync(seeded.CarnivalId);
        var race2 = await RaceAsync(seeded.CarnivalId);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        // First entry of the event in race 1 opens heat 1…
        Assert.Equal(1, (await Placed(seeded, first, race1, lane: 1)).Heat);
        // …a co-member joins THE SAME heat…
        Assert.Equal(1, (await Placed(seeded, second, race1, lane: 2)).Heat);
        // …and moving the second to a fresh race opens the event's next heat.
        Assert.Equal(2, (await Placed(seeded, second, race2, lane: 1)).Heat);
    }

    /// <summary>Placement response, asserted OK.</summary>
    private async Task<(int? Heat, int? Lane)> Placed(
        SeededCarnival seeded, Guid entryId, Guid? raceId, int? lane = null, int? heat = null, Guid? eventId = null)
    {
        var response = await PlaceAsync(seeded, entryId, raceId, lane, heat, eventId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        return (
            body.GetProperty("heat").ValueKind == JsonValueKind.Null ? null : body.GetProperty("heat").GetInt32(),
            body.GetProperty("lane").ValueKind == JsonValueKind.Null ? null : body.GetProperty("lane").GetInt32());
    }

    /// <summary>
    /// An extra entry in the seeded M event, inserted directly (the seed has only
    /// two M-eligible students; eligibility is an API-create concern, not a
    /// marshalling one).
    /// </summary>
    private async Task<Guid> DirectEntryAsync(
        SeededCarnival seeded, Guid studentId, string displayName, string house)
    {
        var id = Guid.CreateVersion7();
        await CarnivalSeed.MutateAsync(_factory, db => db.Entries.Add(new Entry
        {
            Id = id,
            CarnivalEventId = seeded.EventId,
            StudentId = studentId,
            SnapshotDisplayName = displayName,
            SnapshotYearLevel = 7,
            SnapshotHouseCode = house,
            SnapshotAgeGroup = seeded.PrimaryAgeGroup,
            SnapshotCategory = CompetitionCategory.M,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBySubject = "seeder",
        }));
        return id;
    }

    private Task<Guid> DirectThirdEntryAsync(SeededCarnival seeded) =>
        DirectEntryAsync(seeded, seeded.SCategoryStudentId, "Direct Third", "GREVILLEA");

    [Fact]
    public async Task The_second_heat_tapped_first_still_derives_heat_two()
    {
        // ADR-0039 amendment: the heat is the race's ORDINAL within the division,
        // not one past the event's highest. Fill order used to decide it, so a
        // marshal who lanes the second heat first gave it heat 1 — and the deck's
        // chips, which read the scoring heat, swapped places under their finger.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (heatOne, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        var (heatTwo, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        Assert.Equal(2, (await Placed(seeded, second, heatTwo, lane: 1)).Heat);
        Assert.Equal(1, (await Placed(seeded, first, heatOne, lane: 1)).Heat);
    }

    [Fact]
    public async Task Two_events_sharing_one_race_derive_their_heats_independently()
    {
        // 17 Boys lanes 1–4, 18 Boys lanes 5–8 (ADR-0015 §3): one pool pass,
        // two events, two heats — each event's field is its own placing scope.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var girlsEvent = await SecondEventAsync(seeded);
        var race = await RaceAsync(seeded.CarnivalId);
        var boy = await EntryAsync(seeded, seeded.EligibleBoyId);
        var girl = await EntryAsync(seeded, seeded.GirlId, girlsEvent);

        Assert.Equal(1, (await Placed(seeded, boy, race, lane: 1)).Heat);
        // The girls' entry derives ITS event's heat 1 — not the boys' heat, and
        // not heat 2.
        Assert.Equal(1, (await Placed(seeded, girl, race, lane: 5, eventId: girlsEvent)).Heat);
    }

    [Fact]
    public async Task An_explicit_heat_wins_and_mixed_heats_refuse_derivation_with_HEAT_AMBIGUOUS()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var third = await DirectThirdEntryAsync(seeded);

        // The explicit heat is the marshal's override for the genuinely odd
        // case — it wins over derivation…
        Assert.Equal(5, (await Placed(seeded, first, race, lane: 1, heat: 5)).Heat);
        Assert.Equal(7, (await Placed(seeded, second, race, lane: 2, heat: 7)).Heat);

        // …and the mess it can make is refused, not guessed: a third same-event
        // entry cannot derive from {5, 7}, because the guess would be a placing
        // scope.
        var derived = await PlaceAsync(seeded, third, race, lane: 3);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, derived.StatusCode);
        await derived.AssertErrorAsync(ErrorCodes.HeatAmbiguous);

        // An explicit heat resolves it.
        Assert.Equal(5, (await Placed(seeded, third, race, lane: 3, heat: 5)).Heat);
    }

    [Fact]
    public async Task Replaying_the_same_assignment_keeps_the_heat_stable()
    {
        // An unkeyed replay (or a marshal re-tapping the same lane) must be a
        // no-op — deriving max+1 again would drift the entry into a phantom
        // heat of its own.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race1 = await RaceAsync(seeded.CarnivalId);
        var race2 = await RaceAsync(seeded.CarnivalId);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        Assert.Equal(1, (await Placed(seeded, first, race1, lane: 1)).Heat);
        Assert.Equal(2, (await Placed(seeded, second, race2, lane: 1)).Heat);

        // first is alone in race1; the event's max heat is now 2. A replay must
        // answer 1 (its own current heat), never 3.
        Assert.Equal(1, (await Placed(seeded, first, race1, lane: 1)).Heat);
    }

    [Fact]
    public async Task Clearing_a_placement_clears_race_lane_and_derived_heat()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(1, (await Placed(seeded, entry, race, lane: 4)).Heat);

        var cleared = await Placed(seeded, entry, raceId: null);
        Assert.Null(cleared.Heat);
        Assert.Null(cleared.Lane);
        var stored = await StoredEntryAsync(entry);
        Assert.Null(stored.RaceId);

        // A field event's explicit heat with no race stays expressible.
        Assert.Equal(3, (await Placed(seeded, entry, raceId: null, heat: 3)).Heat);
    }

    // ---- Lanes ---------------------------------------------------------------

    [Fact]
    public async Task A_lane_needs_a_race_and_a_taken_lane_is_a_conflict()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        var laneless = await PlaceAsync(seeded, first, raceId: null, lane: 3);
        Assert.Equal(HttpStatusCode.BadRequest, laneless.StatusCode);
        await laneless.AssertErrorAsync(ErrorCodes.EntryLaneInvalid);

        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, first, race, lane: 3)).StatusCode);
        var taken = await PlaceAsync(seeded, second, race, lane: 3);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        await taken.AssertErrorAsync(ErrorCodes.LaneTaken);

        // The refused marshal picks the next lane; and a freed lane is reusable.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, second, race, lane: 4)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, first, raceId: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, second, race, lane: 3)).StatusCode);
    }

    [Fact]
    public async Task The_lane_is_bounded_by_the_venues_configured_lane_count()
    {
        // ADR-0015 §6: laneCount is venue configuration in carnival settings —
        // phase 1 added it, this endpoint is the first thing that reads it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var put = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings", new { laneCount = 4 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);

        var wide = await PlaceAsync(seeded, entry, race, lane: 5);
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        await wide.AssertErrorAsync(ErrorCodes.EntryLaneInvalid);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, entry, race, lane: 4)).StatusCode);
    }

    [Fact]
    public async Task Two_swimmers_exchange_lanes_through_the_three_write_sequence()
    {
        // ADR-0030 §3: the swap the deck offers is three ORDERED placements on
        // this endpoint and nothing else — vacate keeping the race, cross, back.
        // Pinned server-side because the ADR's whole case for "no API change" is
        // that this sequence already lands.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var a = await EntryAsync(seeded, seeded.EligibleBoyId);
        var b = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, a, race, lane: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, b, race, lane: 4)).StatusCode);
        var heat = (await StoredEntryAsync(a)).Heat;

        // Swapping naively is what the ADR exists to prevent: each lane is still
        // held by the other swimmer, so BOTH writes park as LANE_TAKEN.
        var refusedA = await PlaceAsync(seeded, a, race, lane: 4);
        var refusedB = await PlaceAsync(seeded, b, race, lane: 3);
        Assert.Equal(HttpStatusCode.Conflict, refusedA.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refusedB.StatusCode);
        await refusedA.AssertErrorAsync(ErrorCodes.LaneTaken);
        await refusedB.AssertErrorAsync(ErrorCodes.LaneTaken);

        // The sequence the deck queues instead. No write carries a heat.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, a, race, lane: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, b, race, lane: 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, a, race, lane: 4)).StatusCode);

        var swappedA = await StoredEntryAsync(a);
        var swappedB = await StoredEntryAsync(b);
        Assert.Equal(4, swappedA.Lane);
        Assert.Equal(3, swappedB.Lane);
        // §4: heats are structurally untouched — the vacating write keeps the
        // race, so derivation rule 1 rejoins the same-event co-member's heat.
        Assert.Equal(race, swappedA.RaceId);
        Assert.Equal(race, swappedB.RaceId);
        Assert.Equal(heat, swappedA.Heat);
        Assert.Equal(heat, swappedB.Heat);
    }

    [Fact]
    public async Task An_unconfigured_carnival_falls_back_to_the_absolute_lane_ceiling()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);

        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, entry, race, lane: 64)).StatusCode);
        var wide = await PlaceAsync(seeded, entry, race, lane: 65);
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        await wide.AssertErrorAsync(ErrorCodes.EntryLaneInvalid);
    }

    // ---- Cross-carnival and correction ---------------------------------------

    [Fact]
    public async Task An_entry_cannot_be_placed_into_another_carnivals_race()
    {
        // Race.CarnivalId and the entry's carnival are enforced against the SAME
        // route carnival, so a race in carnival A can never hold an entry whose
        // event belongs to carnival B.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.SeedAsync(_factory);
        var foreignRace = await RaceAsync(other.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);

        var response = await PlaceAsync(seeded, entry, foreignRace, lane: 1);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RaceNotFound);
    }

    [Fact]
    public async Task A_heat_stays_correctable_after_the_result_is_recorded()
    {
        // BACKLOG (phase 1 review): a wrong heat is wrong house points, so
        // correcting one after the swim is this endpoint's most important job —
        // there is deliberately NO entry-has-result refusal here.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(1, (await Placed(seeded, entry, race, lane: 1)).Heat);

        var submit = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entry}/results")
        {
            Content = JsonContent.Create(new { value = "32.10" }),
        };
        submit.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        Assert.Equal(HttpStatusCode.Created, (await _recorder.SendAsync(submit)).StatusCode);

        Assert.Equal(4, (await Placed(seeded, entry, race, lane: 1, heat: 4)).Heat);
    }

    [Fact]
    public async Task Marshalling_is_audited_with_prior_and_new_placement_and_the_captured_actor()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entry}/placement")
        {
            Content = JsonContent.Create(new { raceId = race, lane = 2 }),
        };
        // The queued item's capture-time actor (ADR-0002/0006): the marshal who
        // tapped the lane, not whoever's session drains the queue. The heat this
        // write assigns decides house points, so attribution matters.
        request.Headers.Add("X-Captured-Actor", "marshal-on-deck");
        Assert.Equal(HttpStatusCode.OK, (await _recorder.SendAsync(request)).StatusCode);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == "entry.marshal" && a.EntityId == entry.ToString()));
        Assert.Equal("marshal-on-deck", audit.ActorSubject);
        Assert.Contains("\"lane\":2", audit.NewValuesJson!.Replace(" ", ""));
        Assert.Contains("race-marshal", audit.NewValuesJson); // sync-session subject audited beside it
        Assert.NotNull(audit.PriorValuesJson);
    }

    // ---- The schedule stays advisory ------------------------------------------

    [Fact]
    public async Task Deleting_a_slot_or_stream_detaches_its_races_and_deletes_nothing_downstream()
    {
        // The phase-1 review's armed trap: Race→Slot is ClientSetNull, so
        // without the explicit detach, deleting a slot with races under it dies
        // at the database. Deleting a line of the PROGRAM must never delete or
        // block the racing.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (_, slotId) = await SlotAsync(seeded.CarnivalId);
        var race = await RaceAsync(seeded.CarnivalId, slotId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, entry, race, lane: 1)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/schedule/slots/{slotId}")).StatusCode);

        var races = (await Json(await _recorder.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray().ToList();
        Assert.Single(races);
        Assert.Equal(JsonValueKind.Null, races[0].GetProperty("scheduleSlotId").ValueKind);
        var stored = await StoredEntryAsync(entry);
        Assert.Equal(race, stored.RaceId);
        Assert.Equal(1, stored.Lane);

        // Same through a whole-stream delete.
        var (streamId2, slotId2) = await SlotAsync(seeded.CarnivalId);
        var race2 = await RaceAsync(seeded.CarnivalId, slotId2);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/schedule/streams/{streamId2}")).StatusCode);
        var after = (await Json(await _recorder.GetAsync(RacesPath(seeded.CarnivalId))))
            .EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == race2);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("scheduleSlotId").ValueKind);
    }

    [Fact]
    public async Task A_slot_holding_races_cannot_become_a_non_competition_item()
    {
        // Races attach to slots independently of events (ADR-0015 §3), so a slot
        // can hold races while listing none. A race cannot be CREATED under an
        // Item slot, so a slot must not BECOME one over a race's head either —
        // the race would be stranded, refused by every later re-statement of its
        // own slot.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (_, slotId) = await SlotAsync(seeded.CarnivalId);
        await RaceAsync(seeded.CarnivalId, slotId);

        // PUT replaces, so the label rides along even though the kind is what
        // this states.
        var demoted = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/schedule/slots/{slotId}",
            new { label = "Lunch", kind = "Item" });
        Assert.Equal(HttpStatusCode.Conflict, demoted.StatusCode);
        await demoted.AssertErrorAsync(ErrorCodes.ScheduleSlotKindMismatch);
    }

    // ---- Lifecycle gates -------------------------------------------------------

    [Fact]
    public async Task A_finalised_carnival_refuses_races_and_marshalling_a_closed_one_refuses_marshalling()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);

        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Closed);

        // Marshalling is a CAPTURE (retryable refusal — the queued item survives
        // an early close and lands after a reopen)…
        var closed = await PlaceAsync(seeded, entry, race, lane: 1);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        await closed.AssertErrorAsync(ErrorCodes.CarnivalNotOpen);
        // …while race CRUD is administration and stays available while Closed
        // (the ScheduleEndpoints posture: tidying is a post-carnival task).
        Assert.Equal(HttpStatusCode.Created,
            (await _admin.PostAsJsonAsync(RacesPath(seeded.CarnivalId), new { })).StatusCode);

        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Finalised);

        Assert.Equal(HttpStatusCode.Conflict,
            (await _admin.PostAsJsonAsync(RacesPath(seeded.CarnivalId), new { })).StatusCode);
        var finalised = await _admin.DeleteAsync($"{RacesPath(seeded.CarnivalId)}/{race}");
        Assert.Equal(HttpStatusCode.Conflict, finalised.StatusCode);
        await finalised.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);
    }

    // ---- Materialisation (ADR-0019) -------------------------------------------
    //
    // One server operation, one audit record, and a race row that is a byproduct
    // of the act rather than the point of it — which is why a Recorder holds it
    // (ADR-0018 §8). The rules this block pins: only UNRACED entries move (so a
    // replay is safe and a division's second race behaves), entries join UNLANED,
    // an oversized field is WARNED never refused, and — the load-bearing one
    // (owner, 2026-08-17) — materialising NEVER writes a heat. Entry.Heat is the
    // placing scope, so the only way this act can be guaranteed not to move house
    // points is for it never to touch it.

    private Task<HttpResponseMessage> MaterialiseAsync(
        Guid carnivalId, Guid eventId, Guid? slotId = null, string? label = null,
        Guid? clientRaceId = null, string? capturedActor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{RacesPath(carnivalId)}/materialise")
        {
            Content = JsonContent.Create(new { eventId, scheduleSlotId = slotId, label }),
        };
        if (clientRaceId is Guid key) request.Headers.Add("Idempotency-Key", key.ToString());
        if (capturedActor is not null) request.Headers.Add("X-Captured-Actor", capturedActor);
        return _recorder.SendAsync(request);
    }

    /// <summary>Materialise, asserted 201, returning the race body and its warnings.</summary>
    private async Task<(Guid RaceId, JsonElement Race, List<string> Warnings)> MaterialisedAsync(
        Guid carnivalId, Guid eventId, Guid? slotId = null, Guid? clientRaceId = null,
        string? capturedActor = null)
    {
        var response = await MaterialiseAsync(carnivalId, eventId, slotId, null, clientRaceId, capturedActor);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        var race = body.GetProperty("race");
        return (
            race.GetProperty("id").GetGuid(),
            race,
            body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList());
    }

    [Fact]
    public async Task Materialising_stamps_the_division_on_the_race_and_creating_one_does_not()
    {
        // ADR-0038 amendment. A single-division mint knows its event, so the
        // race carries it and stays attributable while it holds nobody. The
        // multi-event admin create names no event and leaves it null — a race
        // may hold heats of several events, and guessing one would be a claim
        // nothing asked for.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await EntryAsync(seeded, seeded.EligibleBoyId);

        var (raceId, race, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(seeded.EventId, race.GetProperty("eventId").GetGuid());
        Assert.Equal(seeded.EventId, await CarnivalSeed.QueryAsync(_factory, db => db.Races
            .AsNoTracking().Where(r => r.Id == raceId).Select(r => r.CarnivalEventId).SingleAsync()));

        var created = await RaceAsync(seeded.CarnivalId, label: "Mixed pass");
        var list = (await Json(await _recorder.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == created);
        Assert.Equal(JsonValueKind.Null, list.GetProperty("eventId").ValueKind);
    }

    [Fact]
    public async Task Removing_a_division_detaches_the_race_it_was_minted_for()
    {
        // The stamp is a "minted for", never an ownership: the division goes and
        // the race stays, exactly as a deleted slot leaves its races unscheduled.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);

        var removed = await _admin.DeleteAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var stamp = await CarnivalSeed.QueryAsync(_factory, db => db.Races
            .AsNoTracking().Where(r => r.Id == raceId).Select(r => r.CarnivalEventId).SingleAsync());
        Assert.Null(stamp);
    }

    [Fact]
    public async Task Materialising_moves_race_membership_only_and_leaves_every_heat_untouched()
    {
        // The first marshalling act in a division creates its race (ADR-0018 §4)
        // and nothing else: these entries were placing across their whole event
        // before it and still are after it, because the field that scopes a
        // placing was never written.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var boy = await EntryAsync(seeded, seeded.EligibleBoyId);
        var secondBoy = await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        var (raceId, race, warnings) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Empty(warnings);
        Assert.Equal(2, race.GetProperty("entries").GetArrayLength());

        foreach (var entryId in new[] { boy, secondBoy })
        {
            var stored = await StoredEntryAsync(entryId);
            Assert.Equal(raceId, stored.RaceId);
            Assert.Null(stored.Heat);
            // Unlaned, always: lanes fill as students present themselves.
            Assert.Null(stored.Lane);
        }
    }

    [Fact]
    public async Task Materialising_leaves_a_heat_a_marshal_already_set_exactly_where_it_was()
    {
        // The other half of "touches no heat": an entry that carries one keeps
        // the number it carries. Minting a race is not a correction of marshalling
        // and must not renumber a field somebody deliberately set.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var flighted = await EntryAsync(seeded, seeded.EligibleBoyId);
        var fresh = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        // A field event's explicit flight, stated with no race (ADR-0015 §5).
        Assert.Equal(3, (await Placed(seeded, flighted, raceId: null, heat: 3)).Heat);

        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);

        var kept = await StoredEntryAsync(flighted);
        Assert.Equal(raceId, kept.RaceId);
        Assert.Equal(3, kept.Heat);
        var untouched = await StoredEntryAsync(fresh);
        Assert.Equal(raceId, untouched.RaceId);
        Assert.Null(untouched.Heat);
    }

    [Fact]
    public async Task Materialising_is_idempotent_under_a_client_key()
    {
        // ADR-0010, as race.create: the client-minted UUIDv7 becomes the race id,
        // so a redrain returns the stored 201 rather than a second race.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        var clientId = Guid.CreateVersion7();

        var (first, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId, clientRaceId: clientId);
        Assert.Equal(clientId, first);

        var (replayed, replayBody, _) = await MaterialisedAsync(
            seeded.CarnivalId, seeded.EventId, clientRaceId: clientId);
        Assert.Equal(clientId, replayed);
        Assert.Single(replayBody.GetProperty("entries").EnumerateArray());
        Assert.Single((await Json(await _admin.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray());
        Assert.Equal(clientId, (await StoredEntryAsync(entry)).RaceId);
    }

    [Fact]
    public async Task Materialising_takes_only_the_unraced_entries_and_leaves_a_placed_one_alone()
    {
        // The overflow case (ADR-0018 §4): the boy already in a race stays in it —
        // race, lane and heat all untouched — and the division's second race takes
        // only the swimmer still waiting. This is what makes the operation
        // replay-safe.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var firstRace = await RaceAsync(seeded.CarnivalId);
        var placedBoy = await EntryAsync(seeded, seeded.EligibleBoyId);
        var leftoverBoy = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.Equal(1, (await Placed(seeded, placedBoy, firstRace, lane: 1)).Heat);

        var (raceId, race, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(leftoverBoy,
            Assert.Single(race.GetProperty("entries").EnumerateArray()).GetProperty("entryId").GetGuid());

        var untouched = await StoredEntryAsync(placedBoy);
        Assert.Equal(firstRace, untouched.RaceId);
        Assert.Equal(1, untouched.Lane);
        Assert.Equal(1, untouched.Heat);

        var joined = await StoredEntryAsync(leftoverBoy);
        Assert.Equal(raceId, joined.RaceId);
        Assert.Null(joined.Heat);
    }

    [Fact]
    public async Task A_division_larger_than_the_venues_lane_count_is_warned_not_refused()
    {
        // ADR-0017's grammar: implausible is warned, incoherent is refused. A mass
        // start has no lanes and a distance race legitimately holds twenty, so
        // refusing here would block a legitimate 1500m.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings", new { laneCount = 1 })).StatusCode);
        await EntryAsync(seeded, seeded.EligibleBoyId);
        await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        var (_, race, warnings) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(2, race.GetProperty("entries").GetArrayLength());
        Assert.Equal(
            "This race holds 2 entries but this venue has 1 lane (carnival settings).",
            Assert.Single(warnings));
    }

    [Fact]
    public async Task Materialising_claims_a_slot_and_a_keyed_one_whose_slot_vanished_lands_unscheduled()
    {
        // Same posture as the keyed race.create: the program is advisory
        // (ADR-0014 §1), so a queued materialisation whose slot was tidied away
        // while it sat in the outbox creates the race UNSCHEDULED rather than
        // refusing — and refusing would strand the whole field behind it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var (_, slotId) = await SlotAsync(seeded.CarnivalId);
        await EntryAsync(seeded, seeded.EligibleBoyId);

        var (_, scheduled, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId, slotId);
        Assert.Equal(slotId, scheduled.GetProperty("scheduleSlotId").GetGuid());

        // Unkeyed and interactive, a slot the server cannot use is a typo.
        var other = await CarnivalSeed.SeedAsync(_factory);
        var (_, foreignSlot) = await SlotAsync(other.CarnivalId);
        var typo = await MaterialiseAsync(seeded.CarnivalId, seeded.EventId, foreignSlot);
        Assert.Equal(HttpStatusCode.NotFound, typo.StatusCode);
        await typo.AssertErrorAsync(ErrorCodes.ScheduleSlotNotFound);

        var clientId = Guid.CreateVersion7();
        var (degraded, degradedRace, _) = await MaterialisedAsync(
            seeded.CarnivalId, seeded.EventId, foreignSlot, clientRaceId: clientId);
        Assert.Equal(clientId, degraded);
        Assert.Equal(JsonValueKind.Null, degradedRace.GetProperty("scheduleSlotId").ValueKind);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == "race.materialise" && a.EntityId == clientId.ToString()));
        Assert.Contains("\"requestedScheduleSlotId\"", audit.NewValuesJson!);
        Assert.Contains("unscheduled rather than refused", audit.NewValuesJson);
    }

    [Fact]
    public async Task An_event_of_another_carnival_or_no_event_at_all_cannot_be_materialised()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.SeedAsync(_factory);

        var cross = await MaterialiseAsync(seeded.CarnivalId, other.EventId);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        await cross.AssertErrorAsync(ErrorCodes.EventNotFound);
        Assert.Empty((await Json(await _admin.GetAsync(RacesPath(seeded.CarnivalId)))).EnumerateArray());

        var missing = await MaterialiseAsync(seeded.CarnivalId, Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await missing.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Materialising_a_division_whose_entries_are_all_raced_still_mints_the_race()
    {
        // Not a refusal: the race is what the marshal asked for, and an empty one
        // is the honest answer to "everyone in this division is already placed".
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var race = await RaceAsync(seeded.CarnivalId);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(1, (await Placed(seeded, entry, race, lane: 1)).Heat);

        var (minted, body, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.NotEqual(race, minted);
        Assert.Empty(body.GetProperty("entries").EnumerateArray());
        Assert.Equal(race, (await StoredEntryAsync(entry)).RaceId);
    }

    [Fact]
    public async Task A_refused_materialisation_does_not_burn_its_idempotency_key()
    {
        // ADR-0002 pause-never-drop: only a 2xx is recorded, so a refusal must not
        // be stored as this key's answer for ever. The same queued item, redrained
        // after the carnival is reopened, has to land.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Closed);
        var clientId = Guid.CreateVersion7();

        var refused = await MaterialiseAsync(seeded.CarnivalId, seeded.EventId, clientRaceId: clientId);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.CarnivalNotOpen);

        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Open);
        var (raceId, _, _) = await MaterialisedAsync(
            seeded.CarnivalId, seeded.EventId, clientRaceId: clientId);
        Assert.Equal(clientId, raceId);
        Assert.Equal(clientId, (await StoredEntryAsync(entry)).RaceId);
    }

    [Fact]
    public async Task A_division_that_already_has_results_may_be_materialised_and_its_placing_pool_does_not_move()
    {
        // There is no ENTRY_HAS_RESULT refusal — and none is needed, because the
        // entry's placing pool is decided by its heat and this writes none.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        var submit = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entry}/results")
        {
            Content = JsonContent.Create(new { value = "32.10" }),
        };
        submit.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        Assert.Equal(HttpStatusCode.Created, (await _recorder.SendAsync(submit)).StatusCode);

        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        var stored = await StoredEntryAsync(entry);
        Assert.Equal(raceId, stored.RaceId);
        // Still placing across the whole event, exactly as before.
        Assert.Null(stored.Heat);
    }

    [Fact]
    public async Task Materialising_is_one_audit_record_carrying_the_event_and_the_captured_actor()
    {
        // ONE record for one human act — the alternative is N entry.marshal rows
        // and nothing anywhere saying what was intended.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await EntryAsync(seeded, seeded.EligibleBoyId);
        await EntryAsync(seeded, seeded.SecondEligibleBoyId);

        var (raceId, _, _) = await MaterialisedAsync(
            seeded.CarnivalId, seeded.EventId, capturedActor: "marshal-on-deck");

        var records = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == raceId.ToString()).ToListAsync());
        var audit = Assert.Single(records);
        Assert.Equal("race.materialise", audit.Action);
        Assert.Equal("marshal-on-deck", audit.ActorSubject);
        var values = audit.NewValuesJson!.Replace(" ", "");
        Assert.Contains(seeded.EventId.ToString(), values);
        Assert.Contains("\"entriesJoined\":2", values);
        // No heat anywhere in the record: the operation writes none, and an
        // audited value would read as one it had set.
        Assert.DoesNotContain("heat", values, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("race-marshal", values); // sync-session subject audited beside it
    }

    [Fact]
    public async Task A_finalised_carnival_refuses_materialising_and_a_closed_one_refuses_it_retryably()
    {
        // Materialising is placement-shaped (ADR-0018 §8), so it takes the
        // placement path's capture-family gates: CARNIVAL_NOT_OPEN is retryable,
        // and a queued one survives an early close to land after a reopen.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await EntryAsync(seeded, seeded.EligibleBoyId);
        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(raceId, (await StoredEntryAsync(entry)).RaceId);

        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Closed);
        var closed = await MaterialiseAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        await closed.AssertErrorAsync(ErrorCodes.CarnivalNotOpen);

        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == seeded.CarnivalId).Status = CarnivalStatus.Finalised);
        Assert.Equal(HttpStatusCode.Conflict,
            (await MaterialiseAsync(seeded.CarnivalId, seeded.EventId)).StatusCode);
    }

    // ---- Cohort-wide heat minting (Phase 3 review, 2026-08-17) ----------------
    //
    // The bug this block exists to prevent, and it is a scoring bug:
    // materialisation joins a division UNHEATED, so the first lane tap used to
    // heat that ONE entry. ScoringService places Heat=null as its own group, so one field
    // became two pools and BOTH minted a 1st — the slower swimmer taking a win
    // because a marshal gave the faster one a lane. Minting cohort-wide keeps the
    // placing pool exactly the membership it already had.

    private async Task SubmitResultAsync(SeededCarnival seeded, Guid entryId, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value }),
        };
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        Assert.Equal(HttpStatusCode.Created, (await _recorder.SendAsync(request)).StatusCode);
    }

    private Task<Dictionary<string, ScoringService.HouseStanding>> StandingsAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, async db =>
            (await new ScoringService(db).ComputeAsync(carnivalId, default))!
            .Houses.ToDictionary(h => h.HouseCode));

    [Fact]
    public async Task One_lane_tap_in_a_materialised_race_cannot_mint_a_second_winner()
    {
        // The probe, end to end. Two swimmers, one race, 30.00 beats 40.00 — so
        // the board must read 4 and 3. Before cohort minting it read 4 and 4: the
        // unlaned swimmer stayed heatless, became a pool of one, and won it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var fast = await EntryAsync(seeded, seeded.EligibleBoyId);        // BANKSIA
        var slow = await EntryAsync(seeded, seeded.SecondEligibleBoyId);  // WATTLE
        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);

        // ONE swimmer is given a lane; the other is still walking to the blocks.
        Assert.Equal(1, (await Placed(seeded, fast, raceId, lane: 1)).Heat);
        Assert.Equal(1, (await StoredEntryAsync(slow)).Heat);

        await SubmitResultAsync(seeded, fast, "30.00");
        await SubmitResultAsync(seeded, slow, "40.00");

        var houses = await StandingsAsync(seeded.CarnivalId);
        Assert.Equal(4, houses["BANKSIA"].Total);
        Assert.Equal(3, houses["WATTLE"].Total); // 4 before the fix — its own pool, its own 1st
    }

    [Fact]
    public async Task A_mint_covers_only_the_tapped_entrys_own_event()
    {
        // The race spans two divisions and the heat does not: heat is EVENT-scoped
        // (ADR-0015 §1), so 12 Girls must not be dragged into a heat because a
        // 12 Boys swimmer was handed a lane, nor the reverse. Both divisions get
        // there the way the day does — materialisation for one, a placement into
        // the same race for the other (FindRaceAsync scopes to the carnival).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var girlsEvent = await SecondEventAsync(seeded);
        var tapped = await EntryAsync(seeded, seeded.EligibleBoyId);
        var cohortMate = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var girl = await EntryAsync(seeded, seeded.GirlId, girlsEvent);
        // An overridden heat 7 sits in a race of its own in the boys' event — the
        // scene the two divisions' numbers are read against.
        var elsewhere = await DirectThirdEntryAsync(seeded);
        Assert.Equal(7, (await Placed(
            seeded, elsewhere, await RaceAsync(seeded.CarnivalId), lane: 1, heat: 7)).Heat);

        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        // The girl joins that race and opens HER event's first field.
        Assert.Equal(1, (await Placed(seeded, girl, raceId, lane: 5, eventId: girlsEvent)).Heat);
        // Which stamps nobody else: the boys are in the race, still unheated.
        Assert.Null((await StoredEntryAsync(tapped)).Heat);
        Assert.Null((await StoredEntryAsync(cohortMate)).Heat);

        // The boys' SECOND race, so heat 2 (ADR-0039 amendment: the number is the
        // race's ordinal in the division, not one past the override's 7).
        Assert.Equal(2, (await Placed(seeded, tapped, raceId, lane: 1)).Heat);

        Assert.Equal(2, (await StoredEntryAsync(cohortMate)).Heat); // swept, same event
        Assert.Equal(1, (await StoredEntryAsync(girl)).Heat);       // a different field entirely
    }

    [Fact]
    public async Task A_mint_never_reaches_an_entry_in_another_race_or_one_that_carries_a_heat()
    {
        // The cohort predicate is narrow on purpose: THIS race, THIS event, and
        // heatless. An entry already marshalled elsewhere is in somebody else's
        // pool and renumbering it would move results between pools — the thing
        // race deletion is careful never to do either.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var elsewhere = await EntryAsync(seeded, seeded.EligibleBoyId);
        var tapped = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var cohortMate = await DirectThirdEntryAsync(seeded);
        var otherRace = await RaceAsync(seeded.CarnivalId);
        // An explicit heat, in a race of its own (the genuinely odd case).
        Assert.Equal(7, (await Placed(seeded, elsewhere, otherRace, lane: 1, heat: 7)).Heat);

        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        // The division's second race, so it opens heat 2 — for both of its
        // members, not just the tapped one.
        Assert.Equal(2, (await Placed(seeded, tapped, raceId, lane: 1)).Heat);
        Assert.Equal(2, (await StoredEntryAsync(cohortMate)).Heat);

        var untouched = await StoredEntryAsync(elsewhere);
        Assert.Equal(otherRace, untouched.RaceId);
        Assert.Equal(7, untouched.Heat);
    }

    [Fact]
    public async Task A_division_split_across_two_races_mints_one_heat_per_race()
    {
        // ADR-0015's ratified model, reached by the cohort rule rather than in
        // spite of it: a field bigger than the venue is several races, and each
        // race is one heat — its own field, its own 1st place.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var firstRace = await RaceAsync(seeded.CarnivalId);
        var a = await EntryAsync(seeded, seeded.EligibleBoyId);
        var b = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var c = await DirectThirdEntryAsync(seeded);
        var d = await DirectEntryAsync(seeded, seeded.YoungerBoyId, "Direct Fourth", "MOUNTAIN ASH");

        // Heat 1 is marshalled the ordinary way, one lane at a time.
        Assert.Equal(1, (await Placed(seeded, a, firstRace, lane: 1)).Heat);
        Assert.Equal(1, (await Placed(seeded, b, firstRace, lane: 2)).Heat);

        // The remainder materialises a second race and opens heat 2 together.
        var (secondRace, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);
        Assert.Equal(2, (await Placed(seeded, c, secondRace, lane: 1)).Heat);
        Assert.Equal(2, (await StoredEntryAsync(d)).Heat);

        // …and heat 1's swimmers are untouched by it.
        Assert.Equal(1, (await StoredEntryAsync(a)).Heat);
        Assert.Equal(1, (await StoredEntryAsync(b)).Heat);
    }

    [Fact]
    public async Task Re_draining_a_placement_neither_re_mints_nor_bumps_the_cohort()
    {
        // A device replaying its outbox must not walk the division up the heat
        // ladder. The second pass finds a co-member carrying the heat and joins
        // it (rule 1), so nothing is minted and the audit says so exactly once.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var tapped = await EntryAsync(seeded, seeded.EligibleBoyId);
        var cohortMate = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);

        Assert.Equal(1, (await Placed(seeded, tapped, raceId, lane: 1)).Heat);
        Assert.Equal(1, (await Placed(seeded, tapped, raceId, lane: 1)).Heat);
        Assert.Equal(1, (await StoredEntryAsync(cohortMate)).Heat);

        var records = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "entry.marshal" && a.EntityId == tapped.ToString())
            .ToListAsync());
        Assert.Equal(2, records.Count);
        Assert.Single(records, r => r.NewValuesJson!.Replace(" ", "").Contains("\"cohortHeatAssigned\":{"));
    }

    [Fact]
    public async Task A_cohort_mint_is_audited_with_the_heat_and_the_number_of_entries_it_moved()
    {
        // A marshalling act that moved somebody else's scoring scope has to say
        // so — this record is the evidence if a placing is ever disputed.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var tapped = await EntryAsync(seeded, seeded.EligibleBoyId);
        await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        await DirectThirdEntryAsync(seeded);
        var (raceId, _, _) = await MaterialisedAsync(seeded.CarnivalId, seeded.EventId);

        Assert.Equal(1, (await Placed(seeded, tapped, raceId, lane: 1)).Heat);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == "entry.marshal" && a.EntityId == tapped.ToString()));
        var values = audit.NewValuesJson!.Replace(" ", "");
        Assert.Contains("\"cohortHeatAssigned\":{\"heat\":1,\"entries\":3}", values);
    }
}
