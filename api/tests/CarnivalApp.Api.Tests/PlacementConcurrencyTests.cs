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
/// Minting a heat is serialised on <c>CarnivalEvent.Version</c> (ADR-0015
/// implementation note, 2026-08-22): without it, two marshals placing entries of
/// ONE event into DIFFERENT races derived the same heat number, splitting the
/// ADR-0015 §1 placing pool so both halves minted a 1st.
///
/// <see cref="RaceInterceptor"/> puts the competing writer at
/// <see cref="RaceEndpoints.CohortMintTag"/> — after the heat is derived, before
/// it is written — so the competitor's write survives to be re-read.
/// </summary>
public sealed class PlacementConcurrencyTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _recorder;

    public PlacementConcurrencyTests()
    {
        _admin = _factory.CreateClientAs("placement-admin", "CarnivalAdministrator");
        _recorder = _factory.CreateClientAs("placement-marshal", "Recorder");
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task<Guid> RaceAsync(Guid carnivalId)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/races", new { label = (string?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An entry for a marshal to place: seeded unplaced, because since ADR-0039
    /// §2 the CREATE places every entry it makes and the contention under test
    /// is the marshalling write's, not the create's.
    /// </summary>
    private Task<Guid> EntryAsync(SeededCarnival seeded, Guid studentId) =>
        CarnivalSeed.UnplacedEntryAsync(_factory, seeded.EventId, studentId);

    /// <summary>A key routes the same write through the ADR-0010 transactional path.</summary>
    private Task<HttpResponseMessage> PlaceAsync(
        SeededCarnival seeded, Guid entryId, Guid? raceId, int? lane = null, Guid? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entryId}/placement")
        {
            Content = JsonContent.Create(new { raceId, lane, heat = (int?)null }),
        };
        if (key is Guid supplied)
            request.Headers.Add(IdempotencyService.HeaderName, supplied.ToString());
        return _recorder.SendAsync(request);
    }

    private Task<Entry> StoredEntryAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.Entries.AsNoTracking().SingleAsync(e => e.Id == entryId));

    private Task<int> EventVersionAsync(Guid eventId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents.AsNoTracking()
            .Where(e => e.Id == eventId).Select(e => e.Version).SingleAsync());

    private Task<int> MarshalAuditCountAsync(Guid entryId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.EntryMarshal && a.EntityId == entryId.ToString()));

    /// <summary>
    /// Run <paramref name="write"/> as a competing request immediately before the
    /// first command containing <paramref name="marker"/>, on whatever transaction
    /// the handler is already inside (the keyed path opens one).
    /// </summary>
    private void ArmCompetingWrite(string marker, Action<CarnivalAppDbContext> write) =>
        _factory.Race.ArmOnce(marker, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            write(db);
            db.SaveChanges();
        });

    [Fact]
    public async Task A_mint_that_loses_to_another_race_re_derives_instead_of_duplicating_the_heat()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var ours = await EntryAsync(seeded, seeded.EligibleBoyId);
        var theirs = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var ourRace = await RaceAsync(seeded.CarnivalId);
        var theirRace = await RaceAsync(seeded.CarnivalId);

        // The other marshal's placement, in the write-set the endpoint itself
        // uses: their entry takes the heat they derived (1 — the same number ours
        // just derived, which is the whole bug) and their mint bumps the event
        // token. It lands while ours is between derivation and save.
        ArmCompetingWrite(RaceEndpoints.CohortMintTag, db =>
        {
            var entry = db.Entries.Single(e => e.Id == theirs);
            entry.RaceId = theirRace;
            entry.Lane = 1;
            entry.Heat = 1;
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).Version++;
        });

        var placed = await PlaceAsync(seeded, ours, ourRace, lane: 1);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        // Not a 409: the marshal tapped a legal lane and must not be sent to the
        // outbox conflict pile for someone else's timing.
        Assert.Equal(2, (await Json(placed)).GetProperty("heat").GetInt32());

        // Two races of one event, two heats — the placing pools stay separate and
        // one of them does not swallow the other's 1st place.
        Assert.Equal(2, (await StoredEntryAsync(ours)).Heat);
        Assert.Equal(1, (await StoredEntryAsync(theirs)).Heat);
        Assert.Equal(2, await EventVersionAsync(seeded.EventId));

        // The losing attempt left nothing behind: its audit row went with the
        // rolled-back batch, so the trail records one marshalling act, not two.
        Assert.Equal(1, await MarshalAuditCountAsync(ours));
    }

    [Fact]
    public async Task A_mint_that_never_wins_answers_retryably_and_writes_nothing()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EntryAsync(seeded, seeded.EligibleBoyId);
        var raceId = await RaceAsync(seeded.CarnivalId);

        // A competitor that re-arms itself, so every attempt loses. Bounded well
        // above the retry budget: if the handler ever stopped giving up, this is
        // what would spin instead of hanging the suite.
        var collisions = 0;
        void ArmAgain() => ArmCompetingWrite(RaceEndpoints.CohortMintTag, db =>
        {
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).Version++;
            if (++collisions < 10) ArmAgain();
        });
        ArmAgain();

        var placed = await PlaceAsync(seeded, entryId, raceId, lane: 1);
        Assert.Equal(HttpStatusCode.InternalServerError, placed.StatusCode);
        // INTERNAL_ERROR is RETRYABLE client-side (app/src/api/errorCodes.ts): the
        // capture is good and the same payload lands once the deck quietens, so
        // the item must stay queued rather than park.
        await placed.AssertErrorAsync(ErrorCodes.InternalError);
        _factory.Race.Disarm();

        var stored = await StoredEntryAsync(entryId);
        Assert.Null(stored.RaceId);
        Assert.Null(stored.Heat);
        Assert.Null(stored.Lane);
        Assert.Equal(0, await MarshalAuditCountAsync(entryId));
    }

    [Fact]
    public async Task A_keyed_mint_that_never_wins_leaves_its_key_unburned_for_the_retry()
    {
        // The outbox path: the retry loop now runs inside the idempotency
        // transaction, so each losing attempt unwinds to its savepoint rather
        // than to nothing.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EntryAsync(seeded, seeded.EligibleBoyId);
        var raceId = await RaceAsync(seeded.CarnivalId);
        var key = Guid.CreateVersion7();

        var collisions = 0;
        void ArmAgain() => ArmCompetingWrite(RaceEndpoints.CohortMintTag, db =>
        {
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).Version++;
            if (++collisions < 10) ArmAgain();
        });
        ArmAgain();

        var exhausted = await PlaceAsync(seeded, entryId, raceId, lane: 1, key: key);
        Assert.Equal(HttpStatusCode.InternalServerError, exhausted.StatusCode);
        await exhausted.AssertErrorAsync(ErrorCodes.InternalError);
        Assert.Equal("false", exhausted.Headers.GetValues("Idempotency-Replayed").Single());
        _factory.Race.Disarm();

        // Only a 2xx burns a key (ADR-0002): the item stays queued and the SAME
        // key lands once the deck quietens.
        var retried = await PlaceAsync(seeded, entryId, raceId, lane: 1, key: key);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("false", retried.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, (await Json(retried)).GetProperty("heat").GetInt32());

        // …and lands ONCE: a further redrain replays the stored response.
        var replay = await PlaceAsync(seeded, entryId, raceId, lane: 1, key: key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, (await StoredEntryAsync(entryId)).Heat);
        Assert.Equal(1, await MarshalAuditCountAsync(entryId));
    }

    [Fact]
    public async Task Only_a_mint_touches_the_event_token_so_ordinary_marshalling_takes_no_contention()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var first = await EntryAsync(seeded, seeded.EligibleBoyId);
        var second = await EntryAsync(seeded, seeded.SecondEligibleBoyId);
        var race1 = await RaceAsync(seeded.CarnivalId);
        var race2 = await RaceAsync(seeded.CarnivalId);

        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, first, race1, lane: 1)).StatusCode);
        Assert.Equal(1, await EventVersionAsync(seeded.EventId)); // opened heat 1

        // Joining the field this race already holds mints nothing.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, second, race1, lane: 2)).StatusCode);
        Assert.Equal(1, await EventVersionAsync(seeded.EventId));

        // A second race of the same event opens heat 2, and does bump.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, second, race2, lane: 1)).StatusCode);
        Assert.Equal(2, (await StoredEntryAsync(second)).Heat);
        Assert.Equal(2, await EventVersionAsync(seeded.EventId));

        // Clearing a placement mints nothing either.
        Assert.Equal(HttpStatusCode.OK, (await PlaceAsync(seeded, second, raceId: null)).StatusCode);
        Assert.Equal(2, await EventVersionAsync(seeded.EventId));
    }
}
