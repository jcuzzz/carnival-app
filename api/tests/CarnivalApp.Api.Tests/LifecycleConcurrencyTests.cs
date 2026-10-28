using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Optimistic concurrency on the carnival/event lifecycle (assessment C4):
/// <c>Carnival.Version</c> and <c>CarnivalEvent.Version</c> turn every
/// read-then-write status transition, settings write and the Verifier
/// finalisation gate into a checked update, so a concurrent modification loses
/// with a retryable 409 CONCURRENCY_CONFLICT instead of silently overwriting —
/// or, at the gate, instead of finalising an event over a result nobody
/// verified.
///
/// The competing writer is placed deterministically by
/// <see cref="RaceInterceptor"/>; see its remarks for what that costs.
/// </summary>
public sealed class LifecycleConcurrencyTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _official;
    private readonly HttpClient _verifier;

    public LifecycleConcurrencyTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-c4", "CarnivalAdministrator");
        _official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        _verifier = factory.CreateClientAs("verifier-c4", "CarnivalAdministrator");
    }

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    /// <summary>
    /// Run <paramref name="write"/> as a competing request immediately before
    /// the first command containing <paramref name="marker"/>, on whatever
    /// transaction the handler is already inside.
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

    // ---- The token itself -------------------------------------------------

    [Fact]
    public async Task Two_writers_racing_one_carnival_leave_one_winner_and_one_concurrency_failure()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, CarnivalStatus.Draft);
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();

        // Both administrators read the Draft carnival before either commits.
        var a = await dbA.Carnivals.SingleAsync(c => c.Id == seeded.CarnivalId);
        var b = await dbB.Carnivals.SingleAsync(c => c.Id == seeded.CarnivalId);

        a.Status = CarnivalStatus.Open;
        a.Version++;
        await dbA.SaveChangesAsync();

        b.Status = CarnivalStatus.Open;
        b.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        // The winner stands, and exactly one transition was applied.
        var stored = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().SingleAsync(c => c.Id == seeded.CarnivalId));
        Assert.Equal(CarnivalStatus.Open, stored.Status);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Status_and_settings_writes_advance_the_lifecycle_token()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, CarnivalStatus.Draft);
        var carnivalPath = $"/api/v1/carnivals/{seeded.CarnivalId}";

        var open = await _admin.PostAsJsonAsync($"{carnivalPath}/status", new { status = "Open" });
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        var settings = await _admin.PutAsJsonAsync($"{carnivalPath}/settings",
            new { scoring = new { participationFloor = 2 } });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        var eventStatus = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, eventStatus.StatusCode);

        var carnival = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().SingleAsync(c => c.Id == seeded.CarnivalId));
        Assert.Equal(2, carnival.Version); // status transition + settings write
        var carnivalEvent = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .AsNoTracking().SingleAsync(e => e.Id == seeded.EventId));
        Assert.Equal(1, carnivalEvent.Version);
    }

    // ---- Status transitions through the API -------------------------------

    [Fact]
    public async Task Carnival_status_transition_that_loses_the_race_is_a_409_and_retries_cleanly()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, CarnivalStatus.Draft);
        var statusPath = $"/api/v1/carnivals/{seeded.CarnivalId}/status";

        ArmCompetingWrite("UPDATE \"Carnivals\"", db =>
        {
            var carnival = db.Carnivals.Single(c => c.Id == seeded.CarnivalId);
            carnival.Status = CarnivalStatus.Open;
            carnival.Version++;
        });

        var lost = await _admin.PostAsJsonAsync(statusPath, new { status = "Open" });
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        await lost.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);

        // Nothing of the losing transition survives (its audit row is in the
        // same failed save), and the retry — the whole point of the code being
        // retryable rather than terminal — goes through.
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.EntityId == seeded.CarnivalId.ToString()
                             && a.Action == AuditActions.CarnivalStatus)));

        var retry = await _admin.PostAsJsonAsync(statusPath, new { status = "Open" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var carnival = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().SingleAsync(c => c.Id == seeded.CarnivalId));
        Assert.Equal(CarnivalStatus.Open, carnival.Status);
        Assert.Equal(1, carnival.Version);
    }

    [Fact]
    public async Task Event_status_transition_that_loses_the_race_is_a_409_and_retries_cleanly()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, eventStatus: CarnivalEventStatus.Scheduled);

        ArmCompetingWrite("UPDATE \"CarnivalEvents\"", db =>
        {
            var carnivalEvent = db.CarnivalEvents.Single(e => e.Id == seeded.EventId);
            carnivalEvent.Status = CarnivalEventStatus.Open;
            carnivalEvent.Version++;
        });

        var lost = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Open" });
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        await lost.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);

        var retry = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Open" });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var carnivalEvent = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .AsNoTracking().SingleAsync(e => e.Id == seeded.EventId));
        Assert.Equal(CarnivalEventStatus.Open, carnivalEvent.Status);
        Assert.Equal(1, carnivalEvent.Version);
    }

    [Fact]
    public async Task Raising_the_bar_advances_the_event_token_so_a_stale_reader_loses()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == seeded.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");

        // The second official read the division before the first raise committed.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var stale = await db.CarnivalEvents.SingleAsync(e => e.Id == seeded.EventId);

        Assert.Equal(HttpStatusCode.OK, (await _official.PostAsJsonAsync(
            $"{EventPath(seeded)}/heights", new { height = 1.20m })).StatusCode);

        stale.HeightsJson = "[1.25]";
        stale.Version++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());

        var carnivalEvent = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .AsNoTracking().SingleAsync(e => e.Id == seeded.EventId));
        Assert.Equal(1, carnivalEvent.Version);
        Assert.Equal([1.20m], JumpCard.ParseHeights(carnivalEvent.HeightsJson));
    }

    // ---- The finalisation-gate TOCTOU -------------------------------------
    //
    // ADR-0017 §4 moved this gate from the event to the CARNIVAL, so all three
    // tests below moved with it. They are re-pointed rather than deleted on
    // purpose: the scenario changed, but the three properties are exactly the
    // ones assessment finding C4 was about, and deleting them would silently
    // reopen it. They also matter MORE now — the old event gate could argue that
    // "an event never leaves Finalised, so the count can only fall", and
    // reversible completion destroys that argument.

    /// <summary>A Closed carnival whose only entry carries a result — the gate's happy path.</summary>
    private async Task<(SeededCarnival Seeded, Guid ResultId)> CarnivalReadyToFinaliseAsync()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries",
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        var resultId = Guid.CreateVersion7();
        var entryId = (await entry.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("id").GetGuid();
        var submit = new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", resultId.ToString() } },
        };
        // ADR-0017: the capture arrives Verified. There is no verify step to walk.
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(submit)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Closed" })).StatusCode);
        return (seeded, resultId);
    }

    private Task<HttpResponseMessage> FinaliseCarnivalAsync(SeededCarnival seeded) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{seeded.CarnivalId}/status",
            new { status = "Finalised" });

    [Fact]
    public async Task Entry_added_behind_the_gate_refuses_finalisation_instead_of_publishing_without_it()
    {
        var (seeded, resultId) = await CarnivalReadyToFinaliseAsync();

        // The completeness check has already passed. A swimmer marshalled onto
        // the deck lands before the status flip is written — under ADR-0010/0015
        // this is ordinary, not exotic, which is exactly why the re-check has to
        // survive the move to the carnival level.
        ArmCompetingWrite(CarnivalEndpoints.CarnivalGateRecheckTag, db =>
        {
            db.Entries.Add(new Entry
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = seeded.EventId,
                StudentId = seeded.SecondEligibleBoyId,
                SnapshotDisplayName = "Blake Chen",
                SnapshotYearLevel = 7,
                SnapshotHouseCode = "WATTLE",
                SnapshotAgeGroup = seeded.PrimaryAgeGroup,
                SnapshotCategory = CompetitionCategory.M,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "late-official",
            });
        });

        var finalise = await FinaliseCarnivalAsync(seeded);
        Assert.Equal(HttpStatusCode.Conflict, finalise.StatusCode);
        await finalise.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);

        // Refused, not half-applied: the carnival is still Closed and the result
        // was not published.
        var carnivalStatus = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().Where(c => c.Id == seeded.CarnivalId).Select(c => c.Status).SingleAsync());
        Assert.Equal(CarnivalStatus.Closed, carnivalStatus);
        var state = await CarnivalSeed.QueryAsync(_factory, db => db.Results
            .AsNoTracking().Where(r => r.Id == resultId).Select(r => r.LifecycleState).SingleAsync());
        Assert.Equal(ResultLifecycleState.Verified, state);
    }

    [Fact]
    public async Task Result_changed_behind_the_gate_collides_on_its_own_revision_token()
    {
        var (seeded, resultId) = await CarnivalReadyToFinaliseAsync();

        // A correction lands after the gate judged the carnival complete. The
        // gate holds the rows it judged, so their own CurrentRevision tokens ride
        // into the save and it collides rather than publishing a stale value.
        // This is the case ADR-0017 leaves genuinely open: corrections are the
        // only write that survives a Closed carnival.
        ArmCompetingWrite("UPDATE \"Results\"", db =>
        {
            var result = db.Results.Single(r => r.Id == resultId);
            result.MeasuredValue = 33.90m;
            result.CurrentRevision++;
        });

        var finalise = await FinaliseCarnivalAsync(seeded);
        Assert.Equal(HttpStatusCode.Conflict, finalise.StatusCode);
        await finalise.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);

        var carnivalStatus = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().Where(c => c.Id == seeded.CarnivalId).Select(c => c.Status).SingleAsync());
        Assert.Equal(CarnivalStatus.Closed, carnivalStatus);
        var state = await CarnivalSeed.QueryAsync(_factory, db => db.Results
            .AsNoTracking().Where(r => r.Id == resultId).Select(r => r.LifecycleState).SingleAsync());
        Assert.NotEqual(ResultLifecycleState.Final, state);
    }

    [Fact]
    public async Task Uncontested_finalisation_commits_publishes_every_result_and_bumps_the_token()
    {
        var (seeded, resultId) = await CarnivalReadyToFinaliseAsync();

        var finalise = await FinaliseCarnivalAsync(seeded);
        Assert.Equal(HttpStatusCode.OK, finalise.StatusCode);

        var carnival = await CarnivalSeed.QueryAsync(_factory, db => db.Carnivals
            .AsNoTracking().SingleAsync(c => c.Id == seeded.CarnivalId));
        Assert.Equal(CarnivalStatus.Finalised, carnival.Status);
        Assert.Equal(2, carnival.Version); // Open → Closed, then the gate
        var state = await CarnivalSeed.QueryAsync(_factory, db => db.Results
            .AsNoTracking().Where(r => r.Id == resultId).Select(r => r.LifecycleState).SingleAsync());
        Assert.Equal(ResultLifecycleState.Final, state);
    }
}
