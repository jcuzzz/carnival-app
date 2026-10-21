using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0017 §4: an event completes automatically when every entry carries a
/// result or a non-result status — and that completion is DERIVED, NON-GATING
/// and REVERSIBLE.
/// </summary>
/// <remarks>
/// The reversibility is the point, not a detail. Under ADR-0010/0015 swimmers
/// are marshalled onto the deck all day, so "every entry has a result" goes
/// briefly true between heats. If completion were a status, that moment would
/// take the event out of <c>Open</c>, the next capture would draw a RETRYABLE
/// <c>EVENT_NOT_OPEN</c>, and the drain halts head-of-line to preserve capture
/// order — silently stalling that device's whole queue at 10:15 and being
/// discovered at 4pm. That is the exact failure ADR-0016 §3 retires the
/// assignment gate to remove, and it must not be rebuilt at another door.
/// </remarks>
public sealed class EventCompletionTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _official;

    public EventCompletionTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-complete", "CarnivalAdministrator");
        _official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
    }

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    private async Task<bool> CompleteAsync(SeededCarnival seeded)
    {
        var response = await _admin.GetAsync(EventPath(seeded));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("complete").GetBoolean();
    }

    private async Task<Guid> EnterAsync(SeededCarnival seeded, Guid studentId)
    {
        var response = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries", new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> CaptureAsync(SeededCarnival seeded, Guid entryId, object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        return _official.SendAsync(request);
    }

    [Fact]
    public async Task An_event_completes_when_the_last_entry_gets_a_result()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        var second = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.False(await CompleteAsync(seeded));

        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(seeded, first, new { value = "34.56" })).StatusCode);
        Assert.False(await CompleteAsync(seeded)); // one still swimming

        // A non-result status counts — that is the completion rule's wording.
        Assert.Equal(HttpStatusCode.Created, (await CaptureAsync(seeded, second, new { status = "DNS" })).StatusCode);
        Assert.True(await CompleteAsync(seeded));
    }

    [Fact]
    public async Task Completion_does_not_change_what_the_event_accepts_and_a_late_entry_undoes_it()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        await CaptureAsync(seeded, first, new { value = "34.56" });
        Assert.True(await CompleteAsync(seeded));

        // THE LOAD-BEARING ASSERTION. The event is complete and still Open, so a
        // swimmer marshalled late is entered and captured normally. If completion
        // ever becomes a status, this is the call that starts returning
        // EVENT_NOT_OPEN and stalls a device's queue for the rest of the day.
        var late = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.False(await CompleteAsync(seeded)); // reversible: it owes a result again

        Assert.Equal(HttpStatusCode.Created,
            (await CaptureAsync(seeded, late, new { value = "35.10" })).StatusCode);
        Assert.True(await CompleteAsync(seeded));

        var status = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Where(e => e.Id == seeded.EventId).Select(e => e.Status).SingleAsync());
        Assert.Equal(CarnivalEventStatus.Open, status); // never moved by completion
    }

    [Fact]
    public async Task An_event_with_no_entries_is_not_complete()
    {
        // Nothing was contested. Reading "done" over an empty division would hide
        // the division nobody entered, which is the case an organiser most needs
        // to see before the day ends.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        Assert.False(await CompleteAsync(seeded));
    }

    [Fact]
    public async Task Nothing_finalises_an_event_any_more()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await CaptureAsync(seeded, entryId, new { value = "34.56" });
        await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Closed" });

        // The route is gone…
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PostAsJsonAsync($"{EventPath(seeded)}/finalise", new { })).StatusCode);

        // …and the carnival can be published without any event being Finalised.
        var carnivalStatus = $"/api/v1/carnivals/{seeded.CarnivalId}/status";
        await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Finalised" })).StatusCode);

        var status = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Where(e => e.Id == seeded.EventId).Select(e => e.Status).SingleAsync());
        Assert.NotEqual(CarnivalEventStatus.Finalised, status);
    }

    [Fact]
    public async Task A_finalised_carnival_refuses_the_administrative_writes_the_event_status_used_to_guard()
    {
        // These four endpoints were gated ONLY on the event being Finalised, a
        // status nothing reaches any more — so without re-pointing them at the
        // carnival they would have become ungated by omission. Two of the four
        // load the event WITHOUT its carnival by default, where a missing Include
        // is a null-navigation 500 at runtime rather than a compile error — which
        // is the whole reason all four are exercised here.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await CaptureAsync(seeded, entryId, new { value = "34.56" });
        var carnivalStatus = $"/api/v1/carnivals/{seeded.CarnivalId}/status";
        await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(carnivalStatus, new { status = "Finalised" })).StatusCode);

        // 1. Event details.
        var update = await _admin.PutAsJsonAsync(EventPath(seeded), new { scoresPoints = false });
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
        await update.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);

        // 2. Officials.
        var officials = await _admin.PutAsJsonAsync($"{EventPath(seeded)}/officials",
            new { officials = new[] { new { subjectId = "someone", displayName = "Someone" } } });
        Assert.Equal(HttpStatusCode.Conflict, officials.StatusCode);
        await officials.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);

        // 3. Entry removal.
        var remove = await _admin.DeleteAsync($"{EventPath(seeded)}/entries/{entryId}");
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        await remove.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);

        // 4. Category override.
        var over = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries/{entryId}/category-override",
            new { category = "F", reason = "Too late to matter" });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        await over.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);
    }
}
