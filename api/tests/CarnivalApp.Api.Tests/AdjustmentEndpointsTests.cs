using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Novelty/bonus house-point adjustments (ADR-0005) and per-carnival scoring
/// settings (ADR-0007): EventAdministrator-only, audited, reason-required,
/// category-validated, idempotent (offline outbox), and voidable. Settings
/// overrides drive the scoring engine.
/// </summary>
public sealed class AdjustmentEndpointsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public AdjustmentEndpointsTests() => _admin = _factory.CreateClientAs("event-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private static readonly IEnumerable<(string, int)> AllHousesAtZero =
        Fixtures.Houses.Select(h => (h, 0)).Order().ToList();

    private static IEnumerable<(string, int)> Totals(IEnumerable<JsonElement> houses) =>
        houses.Select(h => (h.GetProperty("houseCode").GetString()!, h.GetProperty("total").GetInt32())).Order();

    private async Task<Guid> SeedCarnivalAsync(CarnivalStatus status = CarnivalStatus.Open)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Adjust",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = status,
            RuleVersion = "v1",
            OwnerSubject = "seed",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        await db.SaveChangesAsync();
        return carnival.Id;
    }

    private static object Award(string house, int points, string category, string reason) =>
        new { houseCode = house, points, category, reason };

    [Fact]
    public async Task Admin_awards_bonus_points_and_they_appear_in_standings()
    {
        var id = await SeedCarnivalAsync();
        var create = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 10, "Best Dressed House", "Great costumes"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var standings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/standings");
        var wattle = standings.GetProperty("houses").EnumerateArray().First();
        Assert.Equal("WATTLE", wattle.GetProperty("houseCode").GetString());
        Assert.Equal(10, wattle.GetProperty("total").GetInt32());
        Assert.Equal(10, wattle.GetProperty("bonusPoints").GetInt32());
    }

    [Fact]
    public async Task Reason_is_required()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Best Dressed House", ""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Zero_points_are_rejected()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 0, "Best Dressed House", "nope"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_category_is_rejected()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Totally Made Up", "x"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Negative_deduction_is_permitted()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", -8, "Loudest House", "penalty"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task A_house_the_school_does_not_have_is_400_and_mints_no_board_row()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("PHANTOM", 10, "Best Dressed House", "no such house"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await r.AssertErrorAsync(ErrorCodes.HouseUnknown);

        var standings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/standings");
        var houses = standings.GetProperty("houses").EnumerateArray().ToList();
        Assert.DoesNotContain(houses, h => h.GetProperty("houseCode").GetString() == "PHANTOM");
        Assert.Equal(AllHousesAtZero, Totals(houses));
    }

    [Fact]
    public async Task A_house_typed_in_another_case_is_stored_as_the_configured_code()
    {
        var id = await SeedCarnivalAsync();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("mountain ash", 4, "Best Dressed House", "costumes"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("MOUNTAIN ASH", (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("houseCode").GetString());
    }

    [Fact]
    public async Task Finalised_carnival_blocks_adjustments()
    {
        var id = await SeedCarnivalAsync(CarnivalStatus.Finalised);
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Best Dressed House", "late"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_award_points()
    {
        var id = await SeedCarnivalAsync();
        var viewer = _factory.CreateClientAs("viewer", "Viewer");
        var r = await viewer.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Best Dressed House", "x"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Same_idempotency_key_does_not_double_award()
    {
        var id = await SeedCarnivalAsync();
        var key = Guid.CreateVersion7().ToString();
        var body = Award("GREVILLEA", 7, "Novelty 1", "spirit");

        var first = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/carnivals/{id}/adjustments")
        { Content = JsonContent.Create(body) };
        first.Headers.Add("Idempotency-Key", key);
        var r1 = await _admin.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        var second = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/carnivals/{id}/adjustments")
        { Content = JsonContent.Create(body) };
        second.Headers.Add("Idempotency-Key", key);
        var r2 = await _admin.SendAsync(second);
        Assert.True((int)r2.StatusCode < 300); // replayed, not an error

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        Assert.Equal(1, await db.HousePointsAdjustments.CountAsync(a => a.CarnivalId == id));
    }

    [Fact]
    public async Task Voiding_removes_the_points_from_the_total()
    {
        var id = await SeedCarnivalAsync();
        var create = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 10, "Best Dressed House", "costumes"));
        var adjId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var voided = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments/{adjId}/void",
            new { reason = "awarded in error" });
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var standings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/standings");
        Assert.Equal(AllHousesAtZero, Totals(standings.GetProperty("houses").EnumerateArray()));
    }

    [Fact]
    public async Task Void_requires_a_reason()
    {
        var id = await SeedCarnivalAsync();
        var create = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 10, "Best Dressed House", "costumes"));
        var adjId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var r = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments/{adjId}/void",
            new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // --- Captured-actor attribution (ADR-0002/0006) -----------------------
    // Adjustments queue offline like results, so a drained award must be
    // attributed to whoever captured it — and must NOT borrow the sync
    // session's role, which says nothing about the captured actor.

    [Fact]
    public async Task Queued_award_drained_after_handover_records_the_captured_actor_with_no_role()
    {
        var id = await SeedCarnivalAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/carnivals/{id}/adjustments")
        { Content = JsonContent.Create(Award("WATTLE", 10, "Best Dressed House", "novelty race")) };
        request.Headers.Add(CapturedActor.SubjectHeader, "admin-departed");
        var create = await _admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var adjId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var adjustment = await db.HousePointsAdjustments.SingleAsync(a => a.Id == adjId);
        Assert.Equal("admin-departed", adjustment.RecordedBySubject); // never re-attributed
        Assert.Null(adjustment.RecordedByRole);

        var audit = await db.AuditEvents.SingleAsync(
            a => a.EntityId == adjId.ToString() && a.Action == AuditActions.HouseAdjustmentCreate);
        Assert.Equal("admin-departed", audit.ActorSubject);
        Assert.Null(audit.ActorRole); // the captured actor's role at capture time is unknown
    }

    [Fact]
    public async Task Award_without_captured_actor_header_records_the_session_subject_and_role()
    {
        var id = await SeedCarnivalAsync();
        var create = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 10, "Best Dressed House", "online direct award"));
        var adjId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var audit = await db.AuditEvents.SingleAsync(
            a => a.EntityId == adjId.ToString() && a.Action == AuditActions.HouseAdjustmentCreate);
        Assert.Equal("event-admin", audit.ActorSubject);
        Assert.Equal("CarnivalAdministrator", audit.ActorRole);
    }

    // --- Settings ---------------------------------------------------------

    [Fact]
    public async Task Default_settings_are_the_ratified_v1_rules()
    {
        var id = await SeedCarnivalAsync();
        var s = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        var place = s.GetProperty("scoring").GetProperty("placePoints").EnumerateArray()
            .Select(e => e.GetInt32()).ToArray();
        Assert.Equal([4, 3, 2], place);
        Assert.Equal(5, s.GetProperty("adjustmentCategories").GetArrayLength());
    }

    [Fact]
    public async Task Updating_scoring_settings_changes_the_configured_categories_and_version()
    {
        var id = await SeedCarnivalAsync();
        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings", new
        {
            scoring = new { placePoints = new[] { 8, 5, 3 }, participationFloor = 2 },
            adjustmentCategories = new[] { "Spirit", "Costume" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var s = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal([8, 5, 3], s.GetProperty("scoring").GetProperty("placePoints")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.StartsWith("custom-", s.GetProperty("scoring").GetProperty("version").GetString());

        // The new category list is now the one enforced on awards.
        var goodCat = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Spirit", "loud"));
        Assert.Equal(HttpStatusCode.Created, goodCat.StatusCode);
        var oldCat = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{id}/adjustments",
            Award("WATTLE", 5, "Best Dressed House", "old category gone"));
        Assert.Equal(HttpStatusCode.BadRequest, oldCat.StatusCode);
    }

    [Theory]
    [InlineData("placePoints")]
    [InlineData("participationFloor")]
    [InlineData("dnfPoints")]
    [InlineData("socialPerEntryPoints")]
    public async Task Negative_scoring_values_are_rejected_on_write(string field)
    {
        // Write and read enforce the same bounds, so nothing accepted here can be
        // silently discarded by CarnivalSettings.Parse later.
        var id = await SeedCarnivalAsync();
        object scoring = field switch
        {
            "placePoints" => new { placePoints = new[] { 4, -1 } },
            "participationFloor" => new { participationFloor = -1 },
            "socialPerEntryPoints" => new { socialPerEntryPoints = -1 },
            _ => new { dnfPoints = -1 },
        };
        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings", new { scoring });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await put.AssertErrorAsync(ErrorCodes.ScoringRulesInvalid);
    }

    [Fact]
    public async Task Social_per_entry_points_are_readable_and_settable_per_carnival()
    {
        // ADR-0014 §4: the flat social award is configuration, not code. It
        // reads back on the settings document so the client can show what a
        // social entry is worth before anybody swims.
        var id = await SeedCarnivalAsync();
        var defaults = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(1, defaults.GetProperty("scoring").GetProperty("socialPerEntryPoints").GetInt32());

        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { scoring = new { socialPerEntryPoints = 3 } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var stored = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(3, stored.GetProperty("scoring").GetProperty("socialPerEntryPoints").GetInt32());
        // Overriding one rule must not reset the others to nothing.
        Assert.Equal([4, 3, 2], stored.GetProperty("scoring").GetProperty("placePoints")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public async Task Lane_count_is_venue_configuration_on_the_carnival_settings()
    {
        // ADR-0015 §6: one lane count per VENUE, and a carnival happens at one
        // venue, so per-carnival is per-venue. It rides the settings endpoint
        // that already has validation, audit and the Finalised guard — a field,
        // not an endpoint.
        var id = await SeedCarnivalAsync();
        var defaults = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        // Unconfigured, and deliberately NOT defaulted to a plausible pool: a
        // guessed 8 that later refuses a real lane 9 is worse than no limit.
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("laneCount").ValueKind);

        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings", new { laneCount = 6 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var stored = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(6, stored.GetProperty("laneCount").GetInt32());
        // Setting the lane count must not reset the scoring rules it sits beside.
        Assert.Equal([4, 3, 2], stored.GetProperty("scoring").GetProperty("placePoints")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray());

        // …and an unrelated settings write leaves it alone.
        await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { adjustmentCategories = new[] { "Spirit" } });
        var after = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(6, after.GetProperty("laneCount").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(CarnivalSettings.MaxLaneCount + 1)]
    [InlineData(int.MaxValue)]
    public async Task An_impossible_lane_count_is_rejected_on_write(int laneCount)
    {
        // Validated on write exactly as the scoring values are, and by the same
        // rule Parse applies on read: no venue has zero lanes, and a stored 0
        // read as a limit would refuse every lane assignment.
        //
        // BOUNDED ABOVE AS WELL, unlike the place-points ladder beside it. Place
        // points are a school's free choice about how generously to reward a
        // field; a lane count is a claim about a physical place, and its only
        // consumer is the phase-2 check that an assigned lane exists at the venue
        // (ADR-0015 §6). An accepted int.MaxValue would silently disable exactly
        // the check the value exists to drive — a limit that limits nothing is
        // worse than no limit, because it looks configured.
        var id = await SeedCarnivalAsync();
        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings", new { laneCount });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await put.AssertErrorAsync(ErrorCodes.LaneCountInvalid);

        var after = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("laneCount").ValueKind);
    }

    // ---- Auto-fill lanes (ADR-0038 §1) -------------------------------------

    [Fact]
    public async Task Auto_fill_lanes_round_trips_beside_the_lane_count()
    {
        var id = await SeedCarnivalAsync();
        var defaults = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.False(defaults.GetProperty("autoFillLanes").GetBoolean()); // off until the organiser ticks it

        // The editor sends the pair, which is the only way to turn it on from
        // an unconfigured venue.
        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { laneCount = 6, autoFillLanes = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoFillLanes").GetBoolean());

        var stored = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.True(stored.GetProperty("autoFillLanes").GetBoolean());
        Assert.Equal(6, stored.GetProperty("laneCount").GetInt32());

        // An unrelated settings write leaves both alone — each field is an override.
        await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { adjustmentCategories = new[] { "Spirit" } });
        var after = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.True(after.GetProperty("autoFillLanes").GetBoolean());
        Assert.Equal(6, after.GetProperty("laneCount").GetInt32());

        // …and it turns off on its own, the lane count surviving.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{id}/settings", new { autoFillLanes = false })).StatusCode);
        var off = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.False(off.GetProperty("autoFillLanes").GetBoolean());
        Assert.Equal(6, off.GetProperty("laneCount").GetInt32());
    }

    [Fact]
    public async Task Auto_fill_without_a_lane_count_is_refused()
    {
        // ADR-0038 §1: "full" is the lane count, and no pool is assumed (the
        // same reasoning that left laneCount undefaulted). Ticking the box on an
        // unconfigured venue would fill lanes up to a ceiling nobody stated.
        var id = await SeedCarnivalAsync();
        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { autoFillLanes = true });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        await put.AssertErrorAsync(ErrorCodes.AutoFillLanesRequiresLaneCount);

        var after = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.False(after.GetProperty("autoFillLanes").GetBoolean());

        // The lane count already stored satisfies it just as well as one sent
        // in the same request: the check is on the RESULTING pair.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{id}/settings", new { laneCount = 8 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{id}/settings", new { autoFillLanes = true })).StatusCode);
    }

    [Fact]
    public async Task A_stored_auto_fill_without_a_lane_count_reads_as_off()
    {
        // Tolerant read (ADR-0011/0012): a hand-edited blob the write path would
        // have refused must not become a rule that lanes entries to a ceiling
        // nobody configured — it reads as OFF, exactly as a stored laneCount of
        // 0 reads as unconfigured.
        var id = await SeedCarnivalAsync();
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.Carnivals.Single(c => c.Id == id).SettingsJson = """{"autoFillLanes":true}""");

        var settings = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/carnivals/{id}/settings");
        Assert.False(settings.GetProperty("autoFillLanes").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("laneCount").ValueKind);
    }

    [Fact]
    public async Task The_lane_count_cannot_be_lowered_under_a_lane_in_use()
    {
        // ADR-0038 review amendment: laneCount is the ceiling every placement is
        // validated against, so shrinking it under an assigned lane would strand
        // entries no marshal could re-save. Refused, naming the lane in the way.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var put = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings", new { laneCount = 8 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var race = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/races", new { label = (string?)null });
        Assert.Equal(HttpStatusCode.Created, race.StatusCode);
        var raceId = (await race.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var entry = await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var entryId = (await entry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var placed = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries/{entryId}/placement",
            new { raceId, lane = 6, heat = (int?)null });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);

        var refused = await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings", new { laneCount = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.LaneCountInvalid);
        Assert.Equal(8, (await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings")).GetProperty("laneCount").GetInt32());

        // The lane in use is the floor, not the whole venue: 6 is accepted.
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/settings", new { laneCount = 6 })).StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_change_settings()
    {
        var id = await SeedCarnivalAsync();
        var viewer = _factory.CreateClientAs("viewer", "Viewer");
        var r = await viewer.PutAsJsonAsync($"/api/v1/carnivals/{id}/settings",
            new { scoring = new { placePoints = new[] { 1 } } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
