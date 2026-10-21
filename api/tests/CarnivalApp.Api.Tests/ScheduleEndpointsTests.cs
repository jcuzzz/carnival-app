using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The carnival program over the wire (ADR-0014 §1): streams of time slots, the
/// one-read schedule the PWA caches for offline use, and the
/// EventAdministrator-only, audited CRUD behind it.
///
/// Two rules carry the weight of this file. First, the schedule is ADVISORY AND
/// NEVER GATING — nothing about entries, results, eligibility or the event
/// lifecycle may consult it, because events run out of order and students
/// arrive out of order as a matter of course. That is not a nicety: a schedule
/// check in a capture path would refuse legitimate work at the busiest moment of
/// the day, and it would do so on the deck, offline, to a teacher with a
/// stopwatch. <see cref="Entry_result_and_finalisation_are_identical_whether_an_event_is_scheduled_or_not"/>
/// exists to fail loudly the moment anyone couples them.
///
/// Second, the program is destructible but the competition is not: deleting a
/// slot or a whole stream removes lines from a printed page and unschedules the
/// events, and must never remove an entry, a result or an event.
/// </summary>
public sealed class ScheduleEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public ScheduleEndpointsTests() =>
        _admin = _factory.CreateClientAs("event-admin-sched", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static string SchedulePath(Guid carnivalId) => $"/api/v1/carnivals/{carnivalId}/schedule";

    private async Task<Guid> StreamAsync(Guid carnivalId, string name, int? sortOrder = null)
    {
        var response = await _admin.PostAsJsonAsync(
            $"{SchedulePath(carnivalId)}/streams", new { name, sortOrder });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SlotAsync(
        Guid carnivalId, Guid streamId, string label,
        string? start = null, string? end = null, string? kind = null, string? adjustmentCategory = null)
    {
        var response = await _admin.PostAsJsonAsync(
            $"{SchedulePath(carnivalId)}/streams/{streamId}/slots",
            new { label, startTime = start, endTime = end, kind, adjustmentCategory });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PlaceAsync(
        Guid carnivalId, Guid slotId, Guid eventId, int? slotOrder = null) =>
        _admin.PostAsJsonAsync(
            $"{SchedulePath(carnivalId)}/slots/{slotId}/events", new { eventId, slotOrder });

    /// <summary>A second event in the seeded carnival, so placements have something to order.</summary>
    private async Task<Guid> ExtraEventAsync(SeededCarnival seeded, string ageGroup)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events",
            new { eventDefinitionId = seeded.DefinitionId, ageGroup = ageGroup, category = "F" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<CarnivalEvent> StoredEventAsync(Guid eventId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents.AsNoTracking().SingleAsync(e => e.Id == eventId));

    // ---- Read shape ------------------------------------------------------

    [Fact]
    public async Task The_schedule_reads_as_streams_of_slots_holding_event_ids_in_listed_order()
    {
        // ONE read, nested, ids only: it is what the client caches whole for
        // offline use (ADR-0012), so it must not need a second request to be
        // renderable and must not restate the event list it already holds.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var second = await ExtraEventAsync(seeded, seeded.PrimaryAgeGroup);

        // Two streams declared out of print order, to prove the read sorts them.
        var field = await StreamAsync(seeded.CarnivalId, "Long Jump", sortOrder: 1);
        var pool = await StreamAsync(seeded.CarnivalId, "Pool", sortOrder: 0);
        var freestyle = await SlotAsync(seeded.CarnivalId, pool, "FREESTYLE EVENTS", start: "09:45");
        await SlotAsync(seeded.CarnivalId, pool, "Novelty event #1", start: "10:30",
            kind: "Item", adjustmentCategory: "Novelty 1");
        await SlotAsync(seeded.CarnivalId, field, "LONG JUMP 13/14", start: "10:00", end: "12:00");

        // Placed in the opposite order to their creation, with explicit orders.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, freestyle, second, slotOrder: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, freestyle, seeded.EventId, slotOrder: 0)).StatusCode);

        var schedule = await Json(await _admin.GetAsync(SchedulePath(seeded.CarnivalId)));
        Assert.Equal(seeded.CarnivalId, schedule.GetProperty("carnivalId").GetGuid());

        var streams = schedule.GetProperty("streams").EnumerateArray().ToList();
        Assert.Equal(["Pool", "Long Jump"], streams.Select(s => s.GetProperty("name").GetString()));

        var poolSlots = streams[0].GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(2, poolSlots.Count);
        Assert.Equal("FREESTYLE EVENTS", poolSlots[0].GetProperty("label").GetString());
        Assert.Equal("Events", poolSlots[0].GetProperty("kind").GetString());
        // Local clock time, not an instant: the carnival owns the date.
        Assert.Equal("09:45:00", poolSlots[0].GetProperty("startTime").GetString());
        Assert.Equal(JsonValueKind.Null, poolSlots[0].GetProperty("endTime").ValueKind);
        Assert.Equal([seeded.EventId, second],
            poolSlots[0].GetProperty("eventIds").EnumerateArray().Select(e => e.GetGuid()));

        // A non-competition item names its ADR-0005 category and holds no events —
        // novelty points come from the adjustments panel, never from a slot.
        Assert.Equal("Item", poolSlots[1].GetProperty("kind").GetString());
        Assert.Equal("Novelty 1", poolSlots[1].GetProperty("adjustmentCategory").GetString());
        Assert.Empty(poolSlots[1].GetProperty("eventIds").EnumerateArray());

        // The field station's slot covers a RANGE — the athletics shape.
        var fieldSlot = streams[1].GetProperty("slots").EnumerateArray().Single();
        Assert.Equal("10:00:00", fieldSlot.GetProperty("startTime").GetString());
        Assert.Equal("12:00:00", fieldSlot.GetProperty("endTime").GetString());
    }

    [Fact]
    public async Task A_carnival_with_no_program_reads_as_an_empty_schedule_not_a_404()
    {
        // "Not scheduled yet" and "scheduled" are both ordinary states — the
        // client must render a carnival that has never been programmed.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var schedule = await Json(await _admin.GetAsync(SchedulePath(seeded.CarnivalId)));
        Assert.Empty(schedule.GetProperty("streams").EnumerateArray());

        var unknown = await _admin.GetAsync(SchedulePath(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await unknown.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }

    [Fact]
    public async Task Every_role_that_can_see_a_carnival_can_see_its_program()
    {
        // The program is the printed running sheet — read policy is the same as
        // the other carnival reads, or a ResultsOfficial could not navigate it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await StreamAsync(seeded.CarnivalId, "Pool");
        foreach (var role in new[] { "Recorder", "CarnivalAdministrator", "Viewer" })
        {
            var client = _factory.CreateClientAs($"reader-{role}", role);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SchedulePath(seeded.CarnivalId))).StatusCode);
        }
    }

    // ---- Authorisation ---------------------------------------------------

    // `CarnivalAdministrator` is REMOVED from this list, not renamed into it:
    // Verifier collapsed into that role under ADR-0016, so the row inverted.
    [Theory]
    [InlineData("Recorder")]
    [InlineData("Viewer")]
    public async Task Editing_the_program_is_administrator_only(string role)
    {
        // Server-side authorisation, not UI hiding: the program is handed to the
        // whole school, and an official reorganising the day mid-carnival is
        // exactly the change that must go through the organiser.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");
        var basePath = SchedulePath(seeded.CarnivalId);
        var client = _factory.CreateClientAs($"sched-{role}", role);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"{basePath}/streams", new { name = "Track" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{basePath}/streams/{streamId}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{basePath}/streams/{streamId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"{basePath}/streams/{streamId}/slots",
                new { label = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{basePath}/slots/{slotId}",
                new { label = "Renamed", startTime = "11:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"{basePath}/slots/{slotId}/events",
                new { eventId = seeded.EventId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{basePath}/slots/{slotId}/events/{seeded.EventId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{basePath}/slots/{slotId}")).StatusCode);

        // Refused before any assignment: every row is exactly as it was. Counting
        // as well as re-reading matters — checking only the KNOWN stream and slot
        // would still pass if the policy regressed to allow creates, leaving a
        // stray "Track" stream and a "Sneaky" slot on the printed program.
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.ScheduleStreams.CountAsync(s => s.CarnivalId == seeded.CarnivalId)));
        var stream = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleStreams
            .AsNoTracking().Include(s => s.Slots).SingleAsync(s => s.Id == streamId));
        Assert.Equal("Pool", stream.Name);
        var slot = Assert.Single(stream.Slots);
        Assert.Equal("FREESTYLE EVENTS", slot.Label);
        Assert.Equal(new TimeOnly(9, 45), slot.StartTime);
        Assert.Null((await StoredEventAsync(seeded.EventId)).ScheduleSlotId);
    }

    // ---- Audit -----------------------------------------------------------

    [Fact]
    public async Task Program_changes_are_audited_with_prior_and_new_values()
    {
        // §9.3: the program is the artefact printed and handed out, so who moved
        // the house relay to 13:30 — and what it said before — is evidence.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "HOUSE RELAY", start: "13:00");

        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"{SchedulePath(seeded.CarnivalId)}/slots/{slotId}",
            new { label = "HOUSE RELAY", startTime = "13:30" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);

        var created = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == slotId.ToString()
                              && a.Action == AuditActions.ScheduleSlotCreate));
        Assert.Equal("event-admin-sched", created.ActorSubject);
        Assert.Contains("\"HOUSE RELAY\"", created.NewValuesJson!);

        var updated = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == slotId.ToString()
                              && a.Action == AuditActions.ScheduleSlotUpdate));
        Assert.Contains("\"13:00\"", updated.PriorValuesJson!);
        Assert.Contains("\"13:30\"", updated.NewValuesJson!);

        var placed = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == seeded.EventId.ToString()
                              && a.Action == AuditActions.ScheduleEventPlace));
        Assert.Contains(slotId.ToString(), placed.NewValuesJson!);
        // Placement is audited apart from an event update: it changes the printed
        // program, never the event's division, status or scoring.
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .AnyAsync(a => a.EntityId == seeded.EventId.ToString()
                           && a.Action == AuditActions.CarnivalEventUpdate)));
    }

    // ---- Carnival state --------------------------------------------------

    [Fact]
    public async Task A_finalised_carnival_refuses_program_changes_but_still_serves_the_program()
    {
        // The program of a finalised carnival is archive: readable forever,
        // rewritable never. Closed is deliberately still editable — tidying the
        // running sheet for the archive is exactly a post-carnival task.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");

        // Finalise the carnival — one act now (ADR-0017 §4), with no per-event
        // gate to walk first — checking on the way that a CLOSED carnival still
        // accepts program edits.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"{SchedulePath(seeded.CarnivalId)}/slots/{slotId}",
            new { label = "FREESTYLE EVENTS", startTime = "09:50" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Finalised" })).StatusCode);

        var basePath = SchedulePath(seeded.CarnivalId);
        foreach (var refused in new[]
                 {
                     await _admin.PostAsJsonAsync($"{basePath}/streams", new { name = "Track" }),
                     await _admin.PutAsJsonAsync($"{basePath}/streams/{streamId}", new { name = "Renamed" }),
                     await _admin.PostAsJsonAsync($"{basePath}/streams/{streamId}/slots", new { label = "Late" }),
                     await _admin.PutAsJsonAsync($"{basePath}/slots/{slotId}", new { label = "Moved" }),
                     await _admin.PostAsJsonAsync($"{basePath}/slots/{slotId}/events", new { eventId = seeded.EventId }),
                     await _admin.DeleteAsync($"{basePath}/slots/{slotId}/events/{seeded.EventId}"),
                     await _admin.DeleteAsync($"{basePath}/slots/{slotId}"),
                     await _admin.DeleteAsync($"{basePath}/streams/{streamId}"),
                 })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            await refused.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);
        }

        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync(basePath)).StatusCode);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.ScheduleSlots.CountAsync(s => s.StreamId == streamId)));
    }

    // ---- Placement -------------------------------------------------------

    [Fact]
    public async Task Placing_an_event_that_is_already_in_a_slot_moves_it()
    {
        // One slot per event, so placement is a MOVE: "this actually ran in the
        // freestyle block" is an ordinary organiser correction, not a two-step
        // remove-then-add, and it must never leave the event listed twice.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var freestyle = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");
        var backstroke = await SlotAsync(seeded.CarnivalId, streamId, "BACKSTROKE EVENTS", start: "10:45");

        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, freestyle, seeded.EventId)).StatusCode);
        var moved = await PlaceAsync(seeded.CarnivalId, backstroke, seeded.EventId);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal([seeded.EventId],
            (await Json(moved)).GetProperty("eventIds").EnumerateArray().Select(e => e.GetGuid()));

        var streams = (await Json(await _admin.GetAsync(SchedulePath(seeded.CarnivalId))))
            .GetProperty("streams").EnumerateArray().Single()
            .GetProperty("slots").EnumerateArray().ToList();
        Assert.Empty(streams.Single(s => s.GetProperty("id").GetGuid() == freestyle)
            .GetProperty("eventIds").EnumerateArray());
        Assert.Single(streams.Single(s => s.GetProperty("id").GetGuid() == backstroke)
            .GetProperty("eventIds").EnumerateArray());

        Assert.Equal(backstroke, (await StoredEventAsync(seeded.EventId)).ScheduleSlotId);
    }

    [Fact]
    public async Task An_event_from_another_carnival_cannot_be_placed()
    {
        // Slots are carnival-scoped, so a foreign event is simply not findable
        // here — the same 404 the event endpoints give, never a cross-carnival leak.
        var host = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(host.CarnivalId, "Pool");
        var slotId = await SlotAsync(host.CarnivalId, streamId, "FREESTYLE EVENTS");

        var refused = await PlaceAsync(host.CarnivalId, slotId, other.EventId);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.EventNotFound);
        Assert.Null((await StoredEventAsync(other.EventId)).ScheduleSlotId);
    }

    [Fact]
    public async Task A_non_competition_item_never_holds_events()
    {
        // Novelty and logistics items produce no results, ever (ADR-0005): points
        // for a novelty come from the adjustments panel. Letting one hold events
        // would create exactly the "novelty result" the ADR rules out.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var item = await SlotAsync(seeded.CarnivalId, streamId, "Novelty event #1",
            start: "10:30", kind: "Item", adjustmentCategory: "Novelty 1");

        var refused = await PlaceAsync(seeded.CarnivalId, item, seeded.EventId);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.ScheduleSlotKindMismatch);
        Assert.Null((await StoredEventAsync(seeded.EventId)).ScheduleSlotId);

        // And a populated block cannot be turned into an item behind its events' backs.
        var block = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, block, seeded.EventId)).StatusCode);
        var converted = await _admin.PutAsJsonAsync(
            $"{SchedulePath(seeded.CarnivalId)}/slots/{block}",
            new { label = "Announcements", kind = "Item" });
        Assert.Equal(HttpStatusCode.Conflict, converted.StatusCode);
        await converted.AssertErrorAsync(ErrorCodes.ScheduleSlotKindMismatch);
    }

    [Fact]
    public async Task Removing_an_event_from_the_program_leaves_everything_but_its_place()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId })).StatusCode);

        var removed = await _admin.DeleteAsync(
            $"{SchedulePath(seeded.CarnivalId)}/slots/{slotId}/events/{seeded.EventId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty((await Json(removed)).GetProperty("eventIds").EnumerateArray());

        var stored = await StoredEventAsync(seeded.EventId);
        Assert.Null(stored.ScheduleSlotId);
        Assert.Null(stored.SlotOrder);
        Assert.Equal(CarnivalEventStatus.Open, stored.Status);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.CarnivalEventId == seeded.EventId)));

        // Removing it again is a no-op, not a conflict: the intent already holds.
        Assert.Equal(HttpStatusCode.OK, (await _admin.DeleteAsync(
            $"{SchedulePath(seeded.CarnivalId)}/slots/{slotId}/events/{seeded.EventId}")).StatusCode);
    }

    // ---- Deleting program structure --------------------------------------

    [Fact]
    public async Task Deleting_a_slot_leaves_its_events_intact_and_unscheduled()
    {
        // A slot is a LINE ON A PAGE. Deleting it must not delete the
        // competition — with cascade, a mistyped program edit would take a
        // morning's events, entries and results with it.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"{SchedulePath(seeded.CarnivalId)}/slots/{slotId}")).StatusCode);

        var stored = await StoredEventAsync(seeded.EventId);
        Assert.Null(stored.ScheduleSlotId);
        Assert.Null(stored.SlotOrder);
        Assert.Equal(CarnivalEventStatus.Open, stored.Status);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.CarnivalEventId == seeded.EventId)));
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory,
            db => db.ScheduleSlots.CountAsync(s => s.Id == slotId)));

        // The unscheduled event still takes entries — scheduling was additive.
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.SecondEligibleBoyId })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_stream_takes_its_slots_and_unschedules_its_events()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var second = await ExtraEventAsync(seeded, seeded.PrimaryAgeGroup);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS");
        var itemId = await SlotAsync(seeded.CarnivalId, streamId, "Buses depart", kind: "Item");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, second)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"{SchedulePath(seeded.CarnivalId)}/streams/{streamId}")).StatusCode);

        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory,
            db => db.ScheduleSlots.CountAsync(s => s.Id == slotId || s.Id == itemId)));
        foreach (var eventId in new[] { seeded.EventId, second })
        {
            var stored = await StoredEventAsync(eventId);
            Assert.Null(stored.ScheduleSlotId);
            Assert.Null(stored.SlotOrder);
        }
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == seeded.CarnivalId)));
    }

    // ---- Validation ------------------------------------------------------

    [Fact]
    public async Task Slot_times_that_could_not_be_printed_are_refused()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slots = $"{SchedulePath(seeded.CarnivalId)}/streams/{streamId}/slots";

        var backwards = await _admin.PostAsJsonAsync(slots,
            new { label = "SHOT PUT", startTime = "11:00", endTime = "10:00" });
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        await backwards.AssertErrorAsync(ErrorCodes.ScheduleTimesInvalid);

        var endless = await _admin.PostAsJsonAsync(slots, new { label = "SHOT PUT", endTime = "10:00" });
        Assert.Equal(HttpStatusCode.BadRequest, endless.StatusCode);
        await endless.AssertErrorAsync(ErrorCodes.ScheduleTimesInvalid);

        // A program item is a printed LINE, so it needs its text.
        var unlabelled = await _admin.PostAsJsonAsync(slots, new { kind = "Item", startTime = "10:00" });
        Assert.Equal(HttpStatusCode.BadRequest, unlabelled.StatusCode);
        await unlabelled.AssertErrorAsync(ErrorCodes.ValidationFailed);

        // A novelty item pointing at a category the carnival has not configured
        // would silently never line up with its adjustments.
        var unknown = await _admin.PostAsJsonAsync(slots,
            new { label = "Novelty", kind = "Item", adjustmentCategory = "Wheelbarrow Race" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await unknown.AssertErrorAsync(ErrorCodes.AdjustmentCategoryUnknown);
    }

    [Fact]
    public async Task Two_streams_in_one_carnival_cannot_share_a_name()
    {
        // The name is how staff (and a re-applied template) identify a station.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await StreamAsync(seeded.CarnivalId, "Pool");
        var duplicate = await _admin.PostAsJsonAsync(
            $"{SchedulePath(seeded.CarnivalId)}/streams", new { name = "Pool" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await duplicate.AssertErrorAsync(ErrorCodes.ScheduleStreamNameExists);
    }

    [Fact]
    public async Task Unknown_streams_and_slots_answer_with_their_own_codes()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var basePath = SchedulePath(seeded.CarnivalId);

        var stream = await _admin.PostAsJsonAsync($"{basePath}/streams/{Guid.NewGuid()}/slots",
            new { label = "Nowhere" });
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        await stream.AssertErrorAsync(ErrorCodes.ScheduleStreamNotFound);

        var slot = await _admin.PostAsJsonAsync($"{basePath}/slots/{Guid.NewGuid()}/events",
            new { eventId = seeded.EventId });
        Assert.Equal(HttpStatusCode.NotFound, slot.StatusCode);
        await slot.AssertErrorAsync(ErrorCodes.ScheduleSlotNotFound);
    }

    // ---- The advisory-never-gating rule ----------------------------------

    [Fact]
    public async Task Entry_result_and_finalisation_are_identical_whether_an_event_is_scheduled_or_not()
    {
        // THE ADR-0014 §1 RULE, pinned. Events run out of order and students
        // arrive out of order (clashes) as a matter of course, so the schedule is
        // advisory and NOTHING may gate on it — not entry, not result capture,
        // not verification, not the finalisation gate. An event in no slot at all
        // must be as usable as one in the 9:45 block.
        //
        // If this test ever fails, someone has coupled a capture path to the
        // program. That coupling is what the ADR forbids: it would refuse
        // legitimate work at the busiest moment of the day, on a deck, offline.
        var official = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");

        async Task<List<HttpStatusCode>> RunDayAsync(bool scheduled)
        {
            var seeded = await CarnivalSeed.SeedAsync(_factory);
            if (scheduled)
            {
                var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
                var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");
                Assert.Equal(HttpStatusCode.OK,
                    (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);
            }

            var eventPath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";
            var codes = new List<HttpStatusCode>();

            var entry = await _admin.PostAsJsonAsync($"{eventPath}/entries",
                new { studentId = seeded.EligibleBoyId });
            codes.Add(entry.StatusCode);
            var entryId = (await Json(entry)).GetProperty("id").GetGuid();

            var key = Guid.CreateVersion7();
            var submit = new HttpRequestMessage(HttpMethod.Post, $"{eventPath}/entries/{entryId}/results")
            {
                Content = JsonContent.Create(new { value = "34.56" }),
                Headers = { { "Idempotency-Key", key.ToString() } },
            };
            codes.Add((await official.SendAsync(submit)).StatusCode);
            // No verify step and no per-event finalisation after ADR-0017: the
            // capture is validated on arrival and the day is published once, at
            // the carnival. The walk is shorter; the rule under test is the same.
            codes.Add((await _admin.PostAsJsonAsync($"{eventPath}/status", new { status = "Closed" })).StatusCode);
            var carnivalStatus = $"/api/v1/carnivals/{seeded.CarnivalId}/status";
            codes.Add((await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Closed" })).StatusCode);
            codes.Add((await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Finalised" })).StatusCode);

            // The end state is what actually matters: both days publish one Final
            // result, whether or not the event was ever on the program.
            var stored = await StoredEventAsync(seeded.EventId);
            Assert.Equal(scheduled, stored.ScheduleSlotId is not null);
            Assert.Equal(ResultLifecycleState.Final, await CarnivalSeed.QueryAsync(_factory,
                db => db.Results.Where(r => r.Id == key).Select(r => r.LifecycleState).SingleAsync()));
            return codes;
        }

        var onProgram = await RunDayAsync(scheduled: true);
        var offProgram = await RunDayAsync(scheduled: false);
        Assert.Equal(onProgram, offProgram);
        Assert.All(onProgram, code => Assert.True(
            code is HttpStatusCode.OK or HttpStatusCode.Created, $"unexpected {code}"));
    }

    [Fact]
    public async Task Deleting_the_whole_program_mid_carnival_changes_nothing_about_running_it()
    {
        // The nightmare case: an organiser wipes the program at 11am. Every
        // event must keep running — the schedule was never the source of truth
        // for what is left to do (ADR-0014 §1: progress indicators are).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var official = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var slotId = await SlotAsync(seeded.CarnivalId, streamId, "FREESTYLE EVENTS", start: "09:45");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, slotId, seeded.EventId)).StatusCode);

        var eventPath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";
        var entryId = (await Json(await _admin.PostAsJsonAsync($"{eventPath}/entries",
            new { studentId = seeded.EligibleBoyId }))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"{SchedulePath(seeded.CarnivalId)}/streams/{streamId}")).StatusCode);

        var submit = new HttpRequestMessage(HttpMethod.Post, $"{eventPath}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await official.SendAsync(submit)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync($"{eventPath}/entries",
            new { studentId = seeded.SecondEligibleBoyId })).StatusCode);
    }

    [Fact]
    public async Task The_event_list_reads_in_program_order_with_unscheduled_events_last()
    {
        // The list orders by stream → slot → listed position, then by the
        // definition/division order it has always had. Unscheduled events sort
        // LAST rather than being hidden: they are ordinary events that simply are
        // not on the printed program.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var second = await ExtraEventAsync(seeded, seeded.PrimaryAgeGroup);
        var streamId = await StreamAsync(seeded.CarnivalId, "Pool");
        var late = await SlotAsync(seeded.CarnivalId, streamId, "BUTTERFLY EVENTS", start: "12:45");
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded.CarnivalId, late, second)).StatusCode);

        var listed = (await Json(await _admin.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events")))
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([second, seeded.EventId], listed);

        // The DTO carries the placement so the client can offer "next event →"
        // without a second read — advisory context, never a gate.
        var dto = (await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{second}")));
        Assert.Equal(late, dto.GetProperty("scheduleSlotId").GetGuid());
        Assert.Equal(0, dto.GetProperty("slotOrder").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}")))
            .GetProperty("scheduleSlotId").ValueKind);
    }
}
