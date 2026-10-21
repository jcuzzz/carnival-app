using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The ADR-0014 §4 event classification over the wire: the age axis as a SET
/// with a canonical key and a staff label, the Competitive/Social tier, the
/// scoring-mode override and the points exclusion.
///
/// Two rules carry most of the weight here. First, TIER and AGE SCOPE are part
/// of the division — they change who may enter and what an entry means — so they
/// join the uniqueness key and carry the existing entry lock; scoring mode and
/// the points exclusion do neither, because they only change how an event
/// scores. Second, the age key is canonical: an official who types the same
/// merged division in a different order must collide with the event that already
/// exists rather than schedule a twin nobody notices until the results diverge.
/// </summary>
public sealed class EventClassificationEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public EventClassificationEndpointsTests() =>
        _admin = _factory.CreateClientAs("event-admin-cls", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task<Guid> CarnivalAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Classification Carnival", type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> DefinitionAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"SW-CLS-{Guid.CreateVersion7():N}",
            name = "Freestyle 50m", sport = "Swimming", measureType = "Time", units = "s",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> ScheduleAsync(Guid carnivalId, object request) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events", request);

    // ---- Round-trip ------------------------------------------------------

    [Fact]
    public async Task Every_new_field_round_trips_through_schedule_get_and_list()
    {
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();

        var created = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId,
            ageGroup = "snr", // normalised on the way in
            category = "M",
            tier = "Social",
            scoringMode = "Places", // an explicit override AGAINST the tier default
            scoresPoints = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        var eventId = body.GetProperty("id").GetGuid();

        // The same projection must come back from the write, the single GET and
        // the list item — the PWA generates one type from it (ADR-0012).
        var single = await Json(await _admin.GetAsync($"/api/v1/carnivals/{carnivalId}/events/{eventId}"));
        var listed = (await Json(await _admin.GetAsync($"/api/v1/carnivals/{carnivalId}/events")))
            .EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == eventId);

        foreach (var dto in new[] { body, single, listed })
        {
            Assert.Equal("SNR", dto.GetProperty("ageGroup").GetString());
            Assert.Equal("Social", dto.GetProperty("tier").GetString());
            Assert.Equal("Places", dto.GetProperty("scoringMode").GetString());
            Assert.Equal("Places", dto.GetProperty("effectiveScoringMode").GetString());
            Assert.True(dto.GetProperty("scoresPoints").GetBoolean());
        }
    }

    [Fact]
    public async Task An_unclassified_request_schedules_exactly_what_it_used_to()
    {
        // Every client written before ADR-0014 omits all four fields; it must
        // still get an ordinary scoring Competitive event on the places table.
        var carnivalId = await CarnivalAsync();
        var created = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = await DefinitionAsync(),
            ageGroup = "13",
            category = "M",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var dto = await Json(created);
        Assert.Equal("13", dto.GetProperty("ageGroup").GetString());
        Assert.Equal("Competitive", dto.GetProperty("tier").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("scoringMode").ValueKind); // null = follow the tier
        Assert.Equal("Places", dto.GetProperty("effectiveScoringMode").GetString());
        Assert.True(dto.GetProperty("scoresPoints").GetBoolean());
    }

    [Fact]
    public async Task An_absent_age_schedules_an_open_division()
    {
        // ADR-0008's all-ages house relay, as a client that models "open" as the
        // absence of an age sends it, and as one that names the sentinel. Both
        // read back as OPEN, which the unique index needs (two OPEN twins of one
        // definition collide in the database).
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();
        var absent = await Json(await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId,
            category = "M",
        }));
        Assert.Equal("OPEN", absent.GetProperty("ageGroup").GetString());

        var named = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId, ageGroup = "OPEN", category = "F",
        });
        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
        Assert.Equal("OPEN", (await Json(named)).GetProperty("ageGroup").GetString());
    }

    [Fact]
    public async Task A_social_tier_event_scores_socially_without_being_told_to()
    {
        var carnivalId = await CarnivalAsync();
        var dto = await Json(await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = await DefinitionAsync(),
            ageGroup = "13",
            tier = "Social",
        }));
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("scoringMode").ValueKind);
        Assert.Equal("Social", dto.GetProperty("effectiveScoringMode").GetString());
    }

    // ---- Uniqueness ------------------------------------------------------

    [Fact]
    public async Task The_same_division_at_a_different_tier_is_a_different_event()
    {
        // The school's program runs a Competitive and a Social 13 Freestyle side
        // by side (ADR-0014 §4): tier joins the uniqueness key so both exist,
        // and only a genuine repeat of one of them is a 409.
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();
        object Division(string? tier) => new
        {
            eventDefinitionId = definitionId, ageGroup = "13", category = "M", tier,
        };

        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(carnivalId, Division("Competitive"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(carnivalId, Division("Social"))).StatusCode);

        var duplicate = await ScheduleAsync(carnivalId, Division("Social"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await duplicate.AssertErrorAsync(ErrorCodes.EventAlreadyScheduled);

        // An omitted tier means Competitive, so it collides with the first one.
        Assert.Equal(HttpStatusCode.Conflict, (await ScheduleAsync(carnivalId, Division(null))).StatusCode);
    }

    [Fact]
    public async Task Two_spellings_of_one_age_are_the_same_division()
    {
        // The stored age is normalised, so an official re-typing a division in
        // another casing would otherwise schedule a silent twin and half the
        // entries would land on the wrong one.
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();

        Assert.Equal(HttpStatusCode.Created, (await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId, ageGroup = "SNR", category = "M",
        })).StatusCode);

        var again = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId, ageGroup = " snr ", category = "M",
        });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.EventAlreadyScheduled);
    }

    // ---- Reclassifying an event that already has entries -----------------

    /// <summary>A seeded carnival whose event has one entry on it.</summary>
    private async Task<SeededCarnival> SeedWithEntryAsync()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        return seeded;
    }

    private Task<HttpResponseMessage> UpdateAsync(SeededCarnival seeded, object request) =>
        _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}", request);

    [Fact]
    public async Task Changing_the_tier_of_an_event_with_entries_is_refused_as_a_division_change()
    {
        // An entry carries immutable division eligibility, and tier is part of
        // the division: the entrants signed up for the Competitive event, so
        // moving it to Social is the same refusal as re-aging it.
        var seeded = await SeedWithEntryAsync();
        var blocked = await UpdateAsync(seeded, new
        {
            ageGroup = seeded.PrimaryAgeGroup,
            category = "M",
            tier = "Social",
        });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await blocked.AssertErrorAsync(ErrorCodes.EventDivisionLocked);
    }

    [Fact]
    public async Task Changing_the_age_of_an_event_with_entries_is_refused_too()
    {
        // Merging a second age group in is a division change even though every
        // existing entrant stays eligible — the entries were taken against a
        // narrower division and the records/standings context moved.
        var seeded = await SeedWithEntryAsync();
        var blocked = await UpdateAsync(seeded, new
        {
            ageGroup = seeded.JuniorAgeGroup,
            category = "M",
        });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await blocked.AssertErrorAsync(ErrorCodes.EventDivisionLocked);
    }

    [Theory]
    [InlineData("13-14")]   // a merged scope, which no division can represent
    [InlineData("15/16")]
    [InlineData("13+")]     // a LABEL shape, not an age code
    public async Task An_age_that_is_not_a_plain_code_is_refused_on_the_write_path(string group)
    {
        // Left unguarded this schedules an event that looks like a merged
        // division on every screen and matches no student at all — discovered
        // when the first official cannot find anyone to enter, mid-carnival.
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();
        var refused = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId,
            ageGroup = group,
            category = "M",
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AgeScopeInvalid);
    }

    [Fact]
    public async Task An_age_too_long_for_the_column_is_refused_as_a_bad_request_not_a_500()
    {
        // SQLite ignores column lengths, so without the server-side bound this
        // would pass here and fail only on PostgreSQL, as an opaque 500.
        var carnivalId = await CarnivalAsync();
        var definitionId = await DefinitionAsync();
        var refused = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = definitionId,
            ageGroup = new string('G', AgeScope.MaxCodeLength + 1),
            category = "M",
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AgeScopeInvalid);
    }

    [Fact]
    public async Task Scoring_mode_and_the_points_exclusion_stay_editable_with_entries_present()
    {
        // These change how the event SCORES, never who may enter it, so they
        // must remain available on the day — an event discovered mid-carnival to
        // be un-scoreable (a washed-out heat) has to be excludable without
        // deleting everybody's entries.
        var seeded = await SeedWithEntryAsync();
        var updated = await UpdateAsync(seeded, new
        {
            ageGroup = seeded.PrimaryAgeGroup,
            category = "M",
            scoringMode = "Social",
            scoresPoints = false,
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var dto = await Json(updated);
        Assert.Equal("Social", dto.GetProperty("scoringMode").GetString());
        Assert.Equal("Social", dto.GetProperty("effectiveScoringMode").GetString());
        Assert.False(dto.GetProperty("scoresPoints").GetBoolean());
        Assert.Equal("Competitive", dto.GetProperty("tier").GetString()); // tier untouched

        // Audited as a plain update, NOT as a division change — the two actions
        // are queried separately by compliance reporting (§9.3).
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == seeded.EventId.ToString()
                              && a.Action == AuditActions.CarnivalEventUpdate));
        Assert.Equal("event-admin-cls", audit.ActorSubject);
        Assert.Contains("\"ScoresPoints\":true", audit.PriorValuesJson!);
        Assert.Contains("\"ScoresPoints\":false", audit.NewValuesJson!);
        Assert.Contains("\"ScoringMode\":\"Social\"", audit.NewValuesJson!);
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .AnyAsync(a => a.EntityId == seeded.EventId.ToString()
                           && a.Action == AuditActions.CarnivalEventDivisionChange)));
    }

    [Fact]
    public async Task Scheduling_records_the_classification_in_the_audit_trail()
    {
        // §9.3: the audit row is the evidence that an event was deliberately
        // classified, so the classification has to be IN it — not just the ids.
        var carnivalId = await CarnivalAsync();
        var created = await ScheduleAsync(carnivalId, new
        {
            eventDefinitionId = await DefinitionAsync(),
            ageGroup = "15",
            tier = "Social",
            scoresPoints = false,
        });
        var eventId = (await Json(created)).GetProperty("id").GetGuid();

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == eventId.ToString()
                              && a.Action == AuditActions.CarnivalEventSchedule));
        Assert.Contains("\"AgeGroup\":\"15\"", audit.NewValuesJson!);
        Assert.Contains("\"Tier\":\"Social\"", audit.NewValuesJson!);
        Assert.Contains("\"ScoresPoints\":false", audit.NewValuesJson!);
    }

    // ---- Authorisation ---------------------------------------------------

    // `CarnivalAdministrator` is REMOVED from this list, not renamed into it:
    // Verifier collapsed into that role under ADR-0016, so the row inverted.
    [Theory]
    [InlineData("Recorder")]
    [InlineData("Viewer")]
    public async Task Classifying_an_event_is_administrator_only(string role)
    {
        // Server-side authorisation, not UI hiding: tier and the points
        // exclusion move house points, so no other role may set them even
        // though every role can READ them.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs($"cls-{role}", role);
        var basePath = $"/api/v1/carnivals/{seeded.CarnivalId}/events";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(basePath, new
        {
            eventDefinitionId = seeded.DefinitionId, ageGroup = "13", tier = "Social",
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{basePath}/{seeded.EventId}", new
        {
            ageGroup = seeded.PrimaryAgeGroup, category = "M", scoresPoints = false,
        })).StatusCode);

        // Refused before any assignment: the stored event is untouched.
        var stored = await CarnivalSeed.QueryAsync(_factory, db =>
            db.CarnivalEvents.SingleAsync(e => e.Id == seeded.EventId));
        Assert.Equal(EventTier.Competitive, stored.Tier);
        Assert.True(stored.ScoresPoints);
        Assert.Equal(seeded.PrimaryAgeGroup, stored.AgeGroup);
    }
}
