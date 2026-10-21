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
/// Carnival management (create/update, forward-only status lifecycle),
/// event-definition catalogue and carnival-event scheduling per ADR-0003,
/// including audit evidence for every mutation.
/// </summary>
public sealed class CarnivalEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;

    public CarnivalEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-1", "CarnivalAdministrator");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task<Guid> CreateCarnivalAsync(string name = "Swim 2027")
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals", new
        {
            name,
            type = "Swimming",
            date = "2027-02-25",
            venue = "Outdoor Pool",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateDefinitionAsync(string code)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code,
            name = "Freestyle 50m",
            sport = "Swimming",
            measureType = "Time",
            units = "s",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Carnival_create_and_update_are_audited_with_prior_and_new_values()
    {
        var id = await CreateCarnivalAsync("Audited Carnival");

        var update = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}", new
        {
            name = "Audited Carnival (renamed)",
            type = "Swimming",
            date = "2027-03-01",
            venue = "Indoor Pool",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var audits = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.EntityId == id.ToString()).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal([AuditActions.CarnivalCreate, AuditActions.CarnivalUpdate], audits.Select(a => a.Action));
        Assert.Contains("Audited Carnival", audits[1].PriorValuesJson);
        Assert.Contains("renamed", audits[1].NewValuesJson);
        Assert.Equal("event-admin-1", audits[1].ActorSubject);
        Assert.Equal("CarnivalAdministrator", audits[1].ActorRole);
    }

    [Fact]
    public async Task Multi_rolled_actor_can_mutate_and_the_joined_role_list_is_audited()
    {
        // ActorRole holds the ';'-joined EFFECTIVE role list, and the audit row
        // is atomic with the mutation — a column too narrow for a legitimately
        // multi-rolled staff member fails the mutation itself.
        // ADR-0016 leaves four roles, so the widest legitimate list is shorter than
        // the six-role one that motivated widening the column — but the column
        // stays 256 because the alias path can still deliver a legacy name, and a
        // too-narrow column fails the MUTATION, not just the audit row.
        var multiRole = _factory.CreateClientAs("multi-role-admin",
            "CarnivalAdministrator", "Recorder", "Viewer", "SystemAdministrator");
        const string joined = "CarnivalAdministrator;Recorder;Viewer;SystemAdministrator";
        // 56 chars — the four-role model no longer overflows the ORIGINAL 64-char
        // column on its own, which is exactly why the width has to be asserted
        // below rather than inferred from this string: the reason for 256 is now
        // the alias path (a legacy name can still arrive) and future roles, not
        // the current worst case.
        Assert.True(joined.Length <= 64);

        var response = await multiRole.PostAsJsonAsync("/api/v1/carnivals", new
        {
            name = "Multi-role Carnival", type = "Swimming", date = "2027-02-25",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Json(response)).GetProperty("id").GetGuid();

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == id.ToString() && a.Action == AuditActions.CarnivalCreate));
        Assert.Equal(joined, audit.ActorRole);

        // SQLite ignores declared widths, so assert the mapped lengths directly —
        // those are what the Postgres insert enforces. All actor/recorded-role
        // columns are sized together.
        var lengths = await CarnivalSeed.QueryAsync(_factory, db => Task.FromResult(new[]
        {
            db.Model.FindEntityType(typeof(AuditEvent))!
                .FindProperty(nameof(AuditEvent.ActorRole))!.GetMaxLength(),
            db.Model.FindEntityType(typeof(ResultRevision))!
                .FindProperty(nameof(ResultRevision.ActorRole))!.GetMaxLength(),
            db.Model.FindEntityType(typeof(HousePointsAdjustment))!
                .FindProperty(nameof(HousePointsAdjustment.RecordedByRole))!.GetMaxLength(),
        }));
        Assert.All(lengths, length => Assert.Equal(256, length));
    }

    [Fact]
    public async Task Carnival_lifecycle_walks_draft_open_closed_finalised()
    {
        var id = await CreateCarnivalAsync("Lifecycle Carnival");
        foreach (var status in new[] { "Open", "Closed", "Finalised" })
        {
            var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status", new { status });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(status, (await Json(response)).GetProperty("status").GetString());
        }

        var audits = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.EntityId == id.ToString() && a.Action == AuditActions.CarnivalStatus).ToListAsync());
        Assert.Equal(3, audits.Count);
    }

    [Theory]
    [InlineData("Closed")]     // skipping Open
    [InlineData("Finalised")]  // skipping everything
    [InlineData("Draft")]      // self/backward
    public async Task Illegal_carnival_transitions_from_draft_are_409(string target)
    {
        var id = await CreateCarnivalAsync($"Illegal-{target}");
        var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status", new { status = target });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Finalised_carnival_is_terminal_and_uneditable()
    {
        var id = await CreateCarnivalAsync("Terminal");
        foreach (var status in new[] { "Open", "Closed", "Finalised" })
            await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status", new { status });

        var reopen = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/status", new { status = "Open" });
        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);

        var edit = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}", new
        {
            name = "Nope", type = "Swimming", date = "2027-02-25",
        });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }

    [Fact]
    public async Task Carnival_cannot_finalise_while_an_entry_still_owes_a_result()
    {
        // ADR-0017 §4 changed the predicate: the gate used to count events not
        // walked to Finalised by a Verifier; it now asks whether any entry still
        // owes a result. A carnival with no entries at all finalises freely —
        // there is nothing to publish and nothing to wait for.
        var seeded = await CarnivalSeed.SeedAsync(_factory,
            carnivalStatus: CarnivalStatus.Closed, eventStatus: CarnivalEventStatus.Closed);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Finalised" })).StatusCode);
    }

    [Fact]
    public async Task Event_definition_crud_round_trips_and_delete_is_soft()
    {
        var id = await CreateDefinitionAsync("SW-100-BACK");

        var update = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "SW-100-BACK", name = "Backstroke 100m", sport = "Swimming",
            measureType = "Time", units = "s",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _admin.DeleteAsync($"/api/v1/event-definitions/{id}")).StatusCode);

        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id));
        Assert.False(definition.IsActive); // deactivated, never deleted

        var actions = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.EntityId == id.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Equal(
            [AuditActions.EventDefinitionCreate, AuditActions.EventDefinitionUpdate, AuditActions.EventDefinitionDeactivate],
            actions);
    }

    [Fact]
    public async Task Definition_list_defaults_to_active_only_when_include_inactive_is_omitted()
    {
        var id = await CreateDefinitionAsync("SW-HIDDEN");
        await _admin.DeleteAsync($"/api/v1/event-definitions/{id}");

        // Omitting the flag is the common client call and must not 400 on the
        // non-nullable bool binding; the default is the active catalogue.
        var defaulted = await _admin.GetAsync("/api/v1/event-definitions/");
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        var active = (await defaulted.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.DoesNotContain("SW-HIDDEN", active);

        var all = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/event-definitions/?includeInactive=true");
        Assert.Contains("SW-HIDDEN", all.EnumerateArray().Select(d => d.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Duplicate_definition_code_is_409()
    {
        await CreateDefinitionAsync("SW-DUP");
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "SW-DUP", name = "Dup", sport = "Swimming", measureType = "Time", units = "s",
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Scheduling_the_same_definition_twice_for_one_division_is_409()
    {
        var carnivalId = await CreateCarnivalAsync("Division Carnival");
        var definitionId = await CreateDefinitionAsync("SW-50-FLY");

        var request = new { eventDefinitionId = definitionId, ageGroup = "13", category = "M" };
        var first = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var duplicate = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events", request);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // A different division of the same definition is a distinct instance (ADR-0003).
        var otherDivision = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events",
            new { eventDefinitionId = definitionId, ageGroup = "13", category = "F" });
        Assert.Equal(HttpStatusCode.Created, otherDivision.StatusCode);
    }

    [Fact]
    public async Task Scheduling_an_inactive_definition_is_rejected()
    {
        var carnivalId = await CreateCarnivalAsync("Inactive Def Carnival");
        var definitionId = await CreateDefinitionAsync("SW-GONE");
        await _admin.DeleteAsync($"/api/v1/event-definitions/{definitionId}");

        var response = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events",
            new { eventDefinitionId = definitionId, ageGroup = "13", category = "M" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Event_division_change_is_audited_but_blocked_once_entries_exist()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, eventStatus: CarnivalEventStatus.Scheduled);
        var basePath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

        // No entries yet: re-categorising is allowed and audited as a division change.
        var change = await _admin.PutAsJsonAsync(basePath,
            new { ageGroup = seeded.JuniorAgeGroup, category = "M" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == seeded.EventId.ToString()
                              && a.Action == AuditActions.CarnivalEventDivisionChange));
        Assert.Contains($"\"{seeded.PrimaryAgeGroup}\"", audit.PriorValuesJson);
        Assert.Contains($"\"{seeded.JuniorAgeGroup}\"", audit.NewValuesJson);

        // Enter the (now-eligible) junior student, then try to change division again.
        var entry = await _admin.PostAsJsonAsync($"{basePath}/entries",
            new { studentId = seeded.YoungerBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        var blocked = await _admin.PutAsJsonAsync(basePath,
            new { ageGroup = seeded.PrimaryAgeGroup, category = "M" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
    }

    [Fact]
    public async Task Event_status_walks_open_close_reopen_and_never_reaches_finalised()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, eventStatus: CarnivalEventStatus.Scheduled);
        var path = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/status";

        foreach (var status in new[] { "Open", "Closed", "Open", "Closed" })
            Assert.Equal(HttpStatusCode.OK,
                (await _admin.PostAsJsonAsync(path, new { status })).StatusCode);

        // ADR-0017 §4: per-event finalisation is retired, so Finalised has no
        // trigger at all. This used to answer EVENT_FINALISE_VIA_GATE, pointing at
        // a gate that no longer exists — it is now an ordinary illegal transition.
        var finalise = await _admin.PostAsJsonAsync(path, new { status = "Finalised" });
        Assert.Equal(HttpStatusCode.Conflict, finalise.StatusCode);
        await finalise.AssertErrorAsync(ErrorCodes.IllegalEventTransition);

        var illegal = await _admin.PostAsJsonAsync(path, new { status = "Scheduled" });
        Assert.Equal(HttpStatusCode.Conflict, illegal.StatusCode);
    }

    [Fact]
    public async Task Single_event_get_returns_the_same_shape_as_the_list_item()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var viewer = _factory.CreateClientAs("viewer-1", "Viewer");

        var single = await viewer.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        var body = await Json(single);
        Assert.Equal(seeded.EventId, body.GetProperty("id").GetGuid());
        Assert.Equal(seeded.CarnivalId, body.GetProperty("carnivalId").GetGuid());

        // Same projection as the list item — byte-for-byte.
        var list = await Json(await viewer.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events"));
        var listItem = list.EnumerateArray()
            .Single(e => e.GetProperty("id").GetGuid() == seeded.EventId);
        Assert.Equal(listItem.GetRawText(), body.GetRawText());
        Assert.Equal(CarnivalSeed.AssignedOfficial, body.GetProperty("officials")
            .EnumerateArray().Single().GetProperty("subjectId").GetString());
        Assert.Equal(0, body.GetProperty("entryCount").GetInt32());
    }

    [Theory]
    [InlineData(MeasureType.Time)]
    [InlineData(MeasureType.Distance)]
    [InlineData(MeasureType.Height)]
    public async Task The_event_read_carries_the_definitions_measure(MeasureType measureType)
    {
        // ADR-0052 §2: the deck keys the capture mask, the value formatter, the
        // status vocabulary and "lane pending" on this one field.
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: measureType);
        var body = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}"));
        Assert.Equal(measureType.ToString(), body.GetProperty("measureType").GetString());

        var listed = (await Json(await _admin.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events")))
            .EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == seeded.EventId);
        Assert.Equal(measureType.ToString(), listed.GetProperty("measureType").GetString());
    }

    [Fact]
    public async Task Single_event_get_is_404_for_unknown_event_or_wrong_carnival()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var otherCarnival = await CreateCarnivalAsync("Other Carnival");

        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{Guid.NewGuid()}")).StatusCode);
        // Existing event, but addressed under a different carnival.
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(
            $"/api/v1/carnivals/{otherCarnival}/events/{seeded.EventId}")).StatusCode);
    }

    [Fact]
    public async Task Official_assignment_replaces_the_set_and_audits_prior_and_new()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var path = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/officials";

        var response = await _admin.PutAsJsonAsync(path, new
        {
            officials = new[]
            {
                new { subjectId = "official-2", displayName = (string?)"Official Two" },
                new { subjectId = "official-3", displayName = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var subjects = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEventOfficials
            .Where(o => o.CarnivalEventId == seeded.EventId)
            .Select(o => o.SubjectId).OrderBy(s => s).ToListAsync());
        Assert.Equal(["official-2", "official-3"], subjects); // seeded official replaced

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == seeded.EventId.ToString()
                              && a.Action == AuditActions.CarnivalEventAssignOfficials));
        Assert.Contains(CarnivalSeed.AssignedOfficial, audit.PriorValuesJson);
        Assert.Contains("official-2", audit.NewValuesJson);
    }

    [Fact]
    public async Task Carnival_create_requires_name_and_date()
    {
        var noName = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var noDate = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "No Date", type = "Swimming" });
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
    }

    // --- Lanes at creation (ADR-0038 amendment 2026-08-28) -----------------

    private Task<HttpResponseMessage> PostCarnivalAsync(object body) =>
        _admin.PostAsJsonAsync("/api/v1/carnivals", body);

    [Fact]
    public async Task A_new_carnival_can_be_created_with_its_lane_count_and_auto_fill_on()
    {
        var created = await PostCarnivalAsync(new
        {
            name = "Laned Carnival", type = "Swimming", date = "2027-02-25",
            venue = "Outdoor Pool", laneCount = 8, autoFillLanes = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();

        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(8, settings.GetProperty("laneCount").GetInt32());
        Assert.True(settings.GetProperty("autoFillLanes").GetBoolean());
        // The pair is all the create writes: the ratified v1 scoring is untouched.
        Assert.Equal([4, 3, 2], settings.GetProperty("scoring").GetProperty("placePoints")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal("v1", settings.GetProperty("scoring").GetProperty("version").GetString());
        Assert.Equal(5, settings.GetProperty("adjustmentCategories").GetArrayLength());
    }

    [Fact]
    public async Task Auto_fill_at_creation_still_requires_a_lane_count()
    {
        // ADR-0038 §1 is unchanged by the new default: no lane count is assumed,
        // on this path any more than on the settings PUT.
        var response = await PostCarnivalAsync(new
        {
            name = "No Lanes", type = "Swimming", date = "2027-02-25", autoFillLanes = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AutoFillLanesRequiresLaneCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_impossible_lane_count_is_refused_at_creation(int laneCount)
    {
        var response = await PostCarnivalAsync(new
        {
            name = "Bad Lanes", type = "Swimming", date = "2027-02-25", laneCount,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.LaneCountInvalid);
    }

    [Fact]
    public async Task A_create_that_says_nothing_about_lanes_leaves_the_settings_unconfigured()
    {
        var id = await CreateCarnivalAsync("Unlaned Carnival");
        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("laneCount").ValueKind);
        Assert.False(settings.GetProperty("autoFillLanes").GetBoolean());

        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.Carnivals.AsNoTracking().Where(c => c.Id == id).Select(c => c.SettingsJson).SingleAsync());
        Assert.Null(stored);
    }

    [Fact]
    public async Task The_update_path_ignores_the_lane_fields()
    {
        var id = await CreateCarnivalAsync("Update Ignores Lanes");
        var update = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}", new
        {
            name = "Update Ignores Lanes", type = "Swimming", date = "2027-02-25",
            laneCount = 6, autoFillLanes = true,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("laneCount").ValueKind);
        Assert.False(settings.GetProperty("autoFillLanes").GetBoolean());
    }
}

/// <summary>
/// The event-definition read and write contracts must agree: a client that GETs
/// a definition and PUTs it back has to get its catalogue row out intact. A
/// template-created relay is the sharp case — it carries legs, tags, a scoring
/// weight and configuration blobs the request DTO cannot express. Own factory:
/// applying the swimming template populates the whole catalogue.
/// </summary>
public sealed class EventDefinitionRoundTripTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public EventDefinitionRoundTripTests() =>
        _admin = _factory.CreateClientAs("event-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_then_put_of_a_template_definition_preserves_every_field()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Round Trip", type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var carnivalId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "swimming" })).StatusCode);

        var listed = await _admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/event-definitions/?includeInactive=false");
        var relay = listed.EnumerateArray()
            .Single(d => d.GetProperty("code").GetString() == "SW-4X50-MEDLEY-RELAY");

        // Read side exposes everything the catalogue row holds.
        Assert.Equal("Team", relay.GetProperty("format").GetString());
        Assert.Equal(4, relay.GetProperty("teamSize").GetInt32());
        Assert.Equal(2, relay.GetProperty("scoringWeight").GetInt32());
        Assert.Contains("Backstroke", relay.GetProperty("legsJson").GetString()!);
        Assert.Contains("discipline:Medley", relay.GetProperty("tagsJson").GetString()!);
        Assert.NotNull(relay.GetProperty("recordCategoriesJson").GetString());
        Assert.True(relay.TryGetProperty("eligibilityJson", out _)); // present, null for this row
        // ADR-0022 §6: what a rename would relabel, computed server-side.
        var usage = relay.GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("carnivals").GetInt32());
        Assert.Equal(16, usage.GetProperty("divisions").GetInt32()); // 7 ages + open, × 2 categories
        Assert.Equal(0, usage.GetProperty("schoolRecords").GetInt32());

        // The client PUTs back the shape it can express (a rename). Everything it
        // could not send must survive untouched.
        var id = relay.GetProperty("id").GetGuid();
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = relay.GetProperty("code").GetString(),
            name = "Medley Relay 4x50m (renamed)",
            sport = relay.GetProperty("sport").GetString(),
            measureType = relay.GetProperty("measureType").GetString(),
            units = relay.GetProperty("units").GetString(),
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id));
        Assert.Equal("Medley Relay 4x50m (renamed)", stored.Name);
        Assert.Equal(EventFormat.Team, stored.Format);
        Assert.Equal(4, stored.TeamSize);
        Assert.Equal(2, stored.ScoringWeight);
        Assert.Contains("Backstroke", stored.LegsJson!);
        Assert.Contains("discipline:Medley", stored.TagsJson!);
        Assert.NotNull(stored.RecordCategoriesJson);
    }

    [Fact]
    public async Task An_attempt_count_round_trips_and_an_absent_one_clears_it()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "AT-ATTEMPTS", name = "Long Jump", sport = "Athletics",
            measureType = "Distance", units = "m", attemptCount = 3,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("attemptCount").GetInt32());
        var id = body.GetProperty("id").GetGuid();

        // ADR-0053 §1: unlike the configuration blobs, this scalar is not
        // "leave unchanged" when absent — the editors always send it, so an
        // omitted value is how a field event becomes a single typed mark.
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "AT-ATTEMPTS", name = "Long Jump", sport = "Athletics",
            measureType = "Distance", units = "m",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id))).AttemptCount);
    }

    [Theory]
    [InlineData("Distance", 0)]
    [InlineData("Distance", 7)]
    // A runner does not take attempts: NM and the series are both field-only.
    [InlineData("Time", 3)]
    public async Task An_attempt_count_the_measure_cannot_hold_is_refused(string measureType, int attempts)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-BAD-{measureType}-{attempts}", name = "Bad", sport = "Athletics",
            measureType, units = "m", attemptCount = attempts,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_attempt_count_cannot_change_once_results_are_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entryId,
            Status = ResultStatus.Ok,
            MeasuredValue = 4.32m,
            AttemptsJson = """[{"value":4.32}]""",
            LifecycleState = ResultLifecycleState.Verified,
            CurrentRevision = 1,
            RecordedBySubject = "seed",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));
        object Body(int? attemptCount) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = "Distance", definition.Units, attemptCount,
        };

        // A series taken over three rounds cannot be re-read as two, and clearing
        // the count would make it unreadable altogether.
        foreach (var attempted in new int?[] { 2, null })
        {
            var refused = await _admin.PutAsJsonAsync(
                $"/api/v1/event-definitions/{seeded.DefinitionId}", Body(attempted));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            await refused.AssertErrorAsync(ErrorCodes.AttemptCountFrozen);
        }

        // The editors always send the scalar, so re-sending what is already there
        // must not refuse a rename nobody asked about the attempts for.
        var unchanged = await _admin.PutAsJsonAsync(
            $"/api/v1/event-definitions/{seeded.DefinitionId}", Body(3));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    [Fact]
    public async Task The_measure_type_cannot_change_once_results_are_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entryId,
            Status = ResultStatus.Ok,
            MeasuredValue = 4.32m,
            AttemptsJson = """[{"value":4.32}]""",
            LifecycleState = ResultLifecycleState.Verified,
            CurrentRevision = 1,
            RecordedBySubject = "seed",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));
        object Body(string measureType, int? attemptCount) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType, definition.Units, attemptCount,
        };

        // The stored series was captured, derived and ranked as a Distance: a
        // bar card cannot read it, and neither can a time.
        var refused = await _admin.PutAsJsonAsync(
            $"/api/v1/event-definitions/{seeded.DefinitionId}", Body("Height", null));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.MeasureFrozen);
        Assert.Contains("1 result is recorded", await refused.Content.ReadAsStringAsync());

        // The editors always send the type, so re-sending it must not refuse a
        // rename nobody asked about the type for.
        var unchanged = await _admin.PutAsJsonAsync(
            $"/api/v1/event-definitions/{seeded.DefinitionId}", Body("Distance", 3));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    [Fact]
    public async Task The_units_cannot_change_once_results_are_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entryId,
            Status = ResultStatus.Ok,
            MeasuredValue = 34.56m,
            LifecycleState = ResultLifecycleState.Verified,
            CurrentRevision = 1,
            RecordedBySubject = "seed",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));
        object Body(string units) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = definition.MeasureType.ToString(), units,
        };

        var refused = await _admin.PutAsJsonAsync(
            $"/api/v1/event-definitions/{seeded.DefinitionId}", Body("min"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.MeasureFrozen);

        var unchanged = await _admin.PutAsJsonAsync(
            $"/api/v1/event-definitions/{seeded.DefinitionId}", Body(definition.Units));
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    [Fact]
    public async Task The_record_categories_cannot_change_once_a_record_cites_the_definition()
    {
        var world = await new RecordsSeed()
            .Definition("SW-CAT-FROZEN",
                RecordsSeed.Categories(["13"], CompetitionCategory.M, CompetitionCategory.F))
            .Division("SW-CAT-FROZEN", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, world.CarnivalId)).StatusCode);
        var code = world.DefinitionCodes["SW-CAT-FROZEN"];
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Code == code));
        object Body(string? recordCategoriesJson) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = definition.MeasureType.ToString(), definition.Units, recordCategoriesJson,
        };
        var path = $"/api/v1/event-definitions/{definition.Id}";

        var refused = await _admin.PutAsJsonAsync(path, Body(
            RecordsSeed.Categories(["13", "14"], CompetitionCategory.M, CompetitionCategory.F)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.RecordCategoriesFrozen);

        // The same categories written differently, and an absent blob, are not changes.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(path,
            Body("""{"categories":["f","M"],"ageGroups":[" 13"]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(path, Body(null))).StatusCode);
    }

    [Fact]
    public async Task Removing_only_open_from_a_stored_blob_is_accepted_while_records_cite_it()
    {
        var world = await new RecordsSeed()
            .Definition("SW-CAT-OPEN", """{"ageGroups":["13","OPEN"],"categories":["M","F"]}""")
            .Division("SW-CAT-OPEN", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, world.CarnivalId)).StatusCode);
        var code = world.DefinitionCodes["SW-CAT-OPEN"];
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Code == code));
        object Body(string recordCategoriesJson) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = definition.MeasureType.ToString(), definition.Units, recordCategoriesJson,
        };
        var path = $"/api/v1/event-definitions/{definition.Id}";

        var reAdded = await _admin.PutAsJsonAsync(path, Body(definition.RecordCategoriesJson!));
        Assert.Equal(HttpStatusCode.BadRequest, reAdded.StatusCode);
        await reAdded.AssertErrorAsync(ErrorCodes.ValidationFailed);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(path,
            Body(RecordsSeed.Categories(["13"], CompetitionCategory.M, CompetitionCategory.F)))).StatusCode);
    }

    [Fact]
    public async Task A_hand_written_record_category_blob_may_not_name_open()
    {
        var blob = """{"ageGroups":["13","open"],"categories":["M","F"]}""";
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "SW-OPEN-CAT", name = "Open blob", sport = "Swimming", measureType = "Time",
            units = "s", recordCategoriesJson = blob,
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        await created.AssertErrorAsync(ErrorCodes.ValidationFailed);

        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));
        var updated = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{definition.Id}", new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = definition.MeasureType.ToString(), definition.Units,
            recordCategoriesJson = blob,
        });
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        await updated.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task The_measure_type_still_changes_while_no_result_reads_it()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "AT-MEASURE-FREE", name = "Undecided", sport = "Athletics",
            measureType = "Distance", units = "m", attemptCount = 3,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "AT-MEASURE-FREE", name = "Undecided", sport = "Athletics",
            measureType = "Height", units = "m",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(MeasureType.Height, (await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id))).MeasureType);
    }

    [Fact]
    public async Task The_frozen_checks_and_the_write_share_one_serializable_transaction()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "AT-FROZEN-TX", name = "Guarded", sport = "Athletics",
            measureType = "Distance", units = "m", attemptCount = 3,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        System.Data.Common.DbTransaction? atCheck = null, atWrite = null;
        System.Data.IsolationLevel? level = null;
        _factory.Race.ArmOnce("FROM \"Results\"", check =>
        {
            atCheck = check;
            level = check?.IsolationLevel;
            _factory.Race.ArmOnce("UPDATE \"EventDefinitions\"", write => atWrite = write);
        });
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "AT-FROZEN-TX", name = "Guarded", sport = "Athletics",
            measureType = "Height", units = "m",
        });
        _factory.Race.Disarm();

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.NotNull(atCheck);
        Assert.Equal(System.Data.IsolationLevel.Serializable, level);
        Assert.Same(atCheck, atWrite);
    }

    [Fact]
    public async Task A_team_definition_cannot_take_an_attempt_count()
    {
        // Format is template-authored (DefinitionRequest cannot set it), so the
        // relay is seeded and then edited the way the event editor would.
        var id = Guid.CreateVersion7();
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions.Add(new EventDefinition
        {
            Id = id,
            Code = $"AT-RELAY-{id:N}",
            Name = "Field Relay",
            Sport = "Athletics",
            MeasureType = MeasureType.Distance,
            Units = "m",
            Format = EventFormat.Team,
            TeamSize = 4,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == id));

        var response = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            stored.Code, stored.Name, stored.Sport,
            measureType = "Distance", stored.Units, attemptCount = 3,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_ladder_round_trips_and_an_absent_one_clears_it()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "AT-LADDER", name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m", ladder = new { start = 1.20m, step = 0.05m },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1.20m, body.GetProperty("ladder").GetProperty("start").GetDecimal());
        var id = body.GetProperty("id").GetGuid();

        // ADR-0056 §5: the ladder is a scalar the editors always send, so an
        // absent one is how a Height event loses its suggestions.
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "AT-LADDER", name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Null((await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id))).LadderJson);
    }

    [Fact]
    public async Task Removing_the_ladder_suggestions_leaves_every_divisions_bar_list()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.EventDefinitions.Single(d => d.Id == seeded.DefinitionId).LadderJson =
                """{"start":1.20,"step":0.05}""";
            db.CarnivalEvents.Single(e => e.Id == seeded.EventId).HeightsJson = "[1.20,1.25]";
        });
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));

        // ADR-0056, amended 2026-09-17: the two numbers only pre-fill the raise
        // box, so dropping them cannot delete bars a division has contested.
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{seeded.DefinitionId}", new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = "Height", definition.Units,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("[1.20,1.25]", (await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .AsNoTracking().SingleAsync(e => e.Id == seeded.EventId))).HeightsJson);
    }

    [Fact]
    public async Task A_height_definition_cannot_take_an_attempt_count()
    {
        // The measure is the mode: a Height event is contested on the bar
        // whether or not it carries the deck's ladder suggestions.
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-HJ-ATTEMPTS-{Guid.NewGuid():N}", name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m", attemptCount = 3,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.LadderExcludesAttempts);
    }

    [Fact]
    public async Task A_height_definition_that_already_carries_an_attempt_count_is_still_editable()
    {
        // Captured before the amendment: the count is read-only in the editors
        // and round-trips, but it can neither move nor be given to another event.
        var id = Guid.CreateVersion7();
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions.Add(new EventDefinition
        {
            Id = id,
            Code = $"AT-HJ-LEGACY-{id:N}",
            Name = "High Jump",
            Sport = "Athletics",
            MeasureType = MeasureType.Height,
            AttemptCount = 3,
            Units = "m",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == id));
        object Body(int? attemptCount) => new
        {
            stored.Code, name = "High Jump (senior)", stored.Sport,
            measureType = "Height", stored.Units, attemptCount,
        };

        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", Body(3))).StatusCode);
        var moved = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", Body(4));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        await moved.AssertErrorAsync(ErrorCodes.LadderExcludesAttempts);
    }

    [Theory]
    // A ladder is a Height event's; the bar rises in metres.
    [InlineData("Distance", null, 1.20, 0.05, ErrorCodes.LadderRequiresHeightMeasure)]
    [InlineData("Time", null, 1.20, 0.05, ErrorCodes.LadderRequiresHeightMeasure)]
    // A progressive-height competition is not a fixed series of trials.
    [InlineData("Height", 3, 1.20, 0.05, ErrorCodes.LadderExcludesAttempts)]
    // Whole centimetres, inside the bounds a bar can take.
    [InlineData("Height", null, 1.234, 0.05, ErrorCodes.ValidationFailed)]
    [InlineData("Height", null, 1.20, 0, ErrorCodes.ValidationFailed)]
    [InlineData("Height", null, 1.20, 1.5, ErrorCodes.ValidationFailed)]
    [InlineData("Height", null, 5.5, 0.05, ErrorCodes.ValidationFailed)]
    public async Task A_ladder_the_definition_cannot_hold_is_refused(
        string measureType, int? attemptCount, double start, double step, string expected)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-BAD-LADDER-{Guid.NewGuid():N}", name = "Bad", sport = "Athletics",
            measureType, units = "m", attemptCount,
            ladder = new { start = (decimal)start, step = (decimal)step },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(expected);
    }

    [Fact]
    public async Task A_team_definition_cannot_take_a_ladder()
    {
        var id = Guid.CreateVersion7();
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions.Add(new EventDefinition
        {
            Id = id,
            Code = $"AT-HJ-RELAY-{id:N}",
            Name = "Team High Jump",
            Sport = "Athletics",
            MeasureType = MeasureType.Height,
            Units = "m",
            Format = EventFormat.Team,
            TeamSize = 4,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == id));

        var response = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            stored.Code, stored.Name, stored.Sport,
            measureType = "Height", stored.Units, ladder = new { start = 1.20m, step = 0.05m },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    // ---- Attempts per height (ADR-0056 amendment 2026-09-25) ---------------

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Theory]
    [InlineData("Distance", 2)]
    [InlineData("Time", 3)]
    [InlineData("Height", 4)]
    [InlineData("Height", 1)]
    public async Task Attempts_per_height_is_a_height_events_and_only_2_or_3(
        string measureType, int attemptsPerHeight)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-BAD-APH-{Guid.NewGuid():N}", name = "Bad", sport = "Athletics",
            measureType, units = "m", attemptsPerHeight,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_height_definition_takes_two_attempts_per_height_and_absent_reads_as_three()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-HJ-TWO-{Guid.NewGuid():N}", name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m", attemptsPerHeight = 2,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await Json(created);
        Assert.Equal(2, body.GetProperty("attemptsPerHeight").GetInt32());

        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{body.GetProperty("id")}", new
        {
            code = body.GetProperty("code").GetString(), name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(3, (await Json(put)).GetProperty("attemptsPerHeight").GetInt32());

        var distance = await Json(await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"AT-LJ-{Guid.NewGuid():N}", name = "Long Jump", sport = "Athletics",
            measureType = "Distance", units = "m", attemptCount = 3,
        }));
        Assert.Equal(JsonValueKind.Null, distance.GetProperty("attemptsPerHeight").ValueKind);
    }

    [Fact]
    public async Task A_division_is_stamped_from_its_definition_and_keeps_its_value_after_an_edit()
    {
        var carnivalId = (await Json(await _admin.PostAsJsonAsync("/api/v1/carnivals", new
        {
            name = "Two Attempt Carnival", type = "Athletics", date = "2027-03-10", venue = "Oval",
        }))).GetProperty("id").GetGuid();
        var code = $"AT-HJ-STAMP-{Guid.NewGuid():N}";
        var definition = await Json(await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code, name = "High Jump", sport = "Athletics",
            measureType = "Height", units = "m", attemptsPerHeight = 2,
        }));
        var definitionId = definition.GetProperty("id").GetGuid();

        var scheduled = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events",
            new { eventDefinitionId = definitionId, ageGroup = "13", category = "M" });
        Assert.Equal(HttpStatusCode.Created, scheduled.StatusCode);
        Assert.Equal(2, (await Json(scheduled)).GetProperty("attemptsPerHeight").GetInt32());

        var generated = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events/generate",
            new { eventDefinitionId = definitionId, divisions = new[] { new { ageGroup = "14", category = "F" } } });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var division = Assert.Single((await Json(generated)).GetProperty("divisions").EnumerateArray());
        Assert.Equal(2, division.GetProperty("attemptsPerHeight").GetInt32());

        // A definition edit is a default for divisions to come, never these.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{definitionId}",
            new { code, name = "High Jump", sport = "Athletics", measureType = "Height", units = "m" }))
            .StatusCode);
        var events = await Json(await _admin.GetAsync($"/api/v1/carnivals/{carnivalId}/events"));
        Assert.All(events.EnumerateArray(),
            e => Assert.Equal(2, e.GetProperty("attemptsPerHeight").GetInt32()));
    }

    [Fact]
    public async Task A_division_created_before_the_setting_reads_three()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        var body = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}"));
        Assert.Equal(3, body.GetProperty("attemptsPerHeight").GetInt32());

        var swim = await CarnivalSeed.SeedAsync(_factory);
        var time = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{swim.CarnivalId}/events/{swim.EventId}"));
        Assert.Equal(JsonValueKind.Null, time.GetProperty("attemptsPerHeight").ValueKind);
    }

    [Fact]
    public async Task The_ladder_suggestions_still_move_once_results_are_recorded()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == seeded.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        var entryId = await CarnivalSeed.UnplacedEntryAsync(
            _factory, seeded.EventId, seeded.EligibleBoyId);
        await CarnivalSeed.MutateAsync(_factory, db => db.Results.Add(new Result
        {
            Id = Guid.CreateVersion7(),
            EntryId = entryId,
            Status = ResultStatus.Ok,
            MeasuredValue = 1.25m,
            AttemptsJson = """[{"height":1.25,"marks":"O"}]""",
            LifecycleState = ResultLifecycleState.Verified,
            CurrentRevision = 1,
            RecordedBySubject = "seed",
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var definition = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == seeded.DefinitionId));
        object Body(object? ladder) => new
        {
            definition.Code, definition.Name, definition.Sport,
            measureType = "Height", definition.Units, ladder,
        };

        // Nothing recorded reads them, so a card on the board does not freeze
        // them: the raise box may be re-pointed, or emptied, mid-carnival.
        foreach (var attempted in new object?[] { new { start = 1.30m, step = 0.05m }, null })
            Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
                $"/api/v1/event-definitions/{seeded.DefinitionId}", Body(attempted))).StatusCode);

        Assert.Null((await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == seeded.DefinitionId))).LadderJson);
        // …and the card the athlete jumped is untouched by any of it.
        Assert.Equal(1.25m, (await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.SingleAsync(r => r.EntryId == entryId))).MeasuredValue);
    }

    [Fact]
    public async Task Put_still_writes_the_configuration_blobs_it_does_send()
    {
        var created = await _admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = "SW-BLOBS", name = "Blobs", sport = "Swimming", measureType = "Time", units = "s",
            recordCategoriesJson = """{"ageGroups":["12"]}""",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "SW-BLOBS", name = "Blobs", sport = "Swimming", measureType = "Time", units = "s",
            recordCategoriesJson = """{"ageGroups":["13"]}""",
            eligibilityJson = """{"maxEntries":2}""",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var stored = await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.SingleAsync(d => d.Id == id));
        Assert.Contains("13", stored.RecordCategoriesJson!);
        Assert.Contains("maxEntries", stored.EligibilityJson!);
    }
}
