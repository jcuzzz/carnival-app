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
/// Entry creation eligibility (current roster snapshot: age group + effective
/// category, ADR-0003), immutable snapshot fields on the entry (§9.1), the
/// audited per-entry category override, and eligible-student search.
/// </summary>
public sealed class EntryEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;

    public EntryEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-1", "CarnivalAdministrator");
    }

    private static string EntriesPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries";

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    /// <summary>
    /// A Recorder may create an entry (marshalling) but may NOT smuggle an
    /// ADR-0003 category override in with it.
    /// </summary>
    /// <remarks>
    /// The dedicated override endpoint is administrator-only and the
    /// authorisation matrix pins it — but entry CREATION also accepts an
    /// override, and creation is what ADR-0016 §1 hands to the Recorder. Splitting
    /// `carnivals:manage` without this check left a route around the split that
    /// the matrix stayed green over: the override decides whether the student
    /// clears the division at all, so it is the eligibility control, not a
    /// marshalling action.
    /// </remarks>
    [Fact]
    public async Task A_recorder_may_create_an_entry_but_not_apply_a_category_override()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var recorder = _factory.CreateClientAs("deck-recorder", "Recorder");

        // Marshalling: allowed.
        var plain = await recorder.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);

        // The same route, carrying an override: refused, terminally.
        var smuggled = await recorder.PostAsJsonAsync(EntriesPath(seeded),
            new
            {
                studentId = seeded.GirlId,
                categoryOverride = "M",
                categoryOverrideReason = "Should not be mine to decide",
            });
        Assert.Equal(HttpStatusCode.Forbidden, smuggled.StatusCode);
        await smuggled.AssertErrorAsync(ErrorCodes.CategoryOverrideForbidden);

        // Nothing was written — not the entry, and not the override.
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.CarnivalEventId == seeded.EventId)));
    }

    [Fact]
    public async Task An_administrator_may_still_apply_a_category_override_on_create()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded), new
        {
            studentId = seeded.GirlId,
            categoryOverride = "M",
            categoryOverrideReason = "Competing in the boys' division at the family's request",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("overrideApplied").GetBoolean());
    }

    [Fact]
    public async Task Eligible_student_entry_snapshots_display_fields_immutably()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId, heat = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Json(response);
        Assert.Equal("Aiden Brook", body.GetProperty("displayName").GetString());
        Assert.Equal("BANKSIA", body.GetProperty("houseCode").GetString());
        Assert.Equal(seeded.PrimaryAgeGroup, body.GetProperty("ageGroup").GetString());
        Assert.False(body.GetProperty("overrideApplied").GetBoolean());
        // A heat alone is a legitimate create-time statement (a field event's
        // flight); the lane it has no race for is not, and stays null.
        Assert.Equal(1, body.GetProperty("heat").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lane").ValueKind);

        var entry = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.SingleAsync(e => e.StudentId == seeded.EligibleBoyId));
        Assert.Equal(7, entry.SnapshotYearLevel);
        Assert.Equal("event-admin-1", entry.CreatedBySubject);

        // The entry references the snapshot it was created from — event-time
        // context survives later roster imports (§9.1).
        var snapshot = await CarnivalSeed.QueryAsync(_factory, db =>
            db.RosterSnapshots.SingleAsync(s => s.Id == entry.RosterSnapshotId));
        Assert.Equal(seeded.EligibleBoyId, snapshot.StudentId);
    }

    // --- Heat and lane at CREATE time (ADR-0015 §1/§3) ----------------------
    //
    // Before ADR-0015 Entry.Heat was inert — a deck-sort key, and a nonsense value
    // was only untidy. It is now the PLACING SCOPE: every distinct value forms its
    // own field with its own 1st place, so 24 entries carrying 24 different heats
    // would be 24 swimmers each paid the winner's 4 points. That makes an
    // unvalidated client-supplied heat a route to arbitrary house points, and
    // CLAUDE.md is explicit that all validation is server-side.
    //
    // The two fields answer differently here. A heat is a statement about the
    // EVENT, which the create route knows, so it is accepted on the same bounds
    // the placement endpoint's explicit heat carries. A lane is a position in a
    // RACE, which the create route cannot name — so it is refused outright,
    // whatever its value, and belongs to PUT .../placement.

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public async Task An_impossible_heat_is_refused_on_entry_creation(int heat)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId, heat });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EntryHeatInvalid);

        // Refused before anything is written: no half-entered swimmer.
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.AnyAsync(e =>
                e.CarnivalEventId == seeded.EventId && e.StudentId == seeded.EligibleBoyId)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(3)] // the dangerous one: perfectly plausible, and still refused
    public async Task Any_lane_is_refused_on_entry_creation(int lane)
    {
        // Lane 3 is the case this test exists for. It is not a nonsense value —
        // it is a value a client could send in good faith and the old bounds
        // accepted, and it would land in the column with NO RACE, which is the
        // exact state PUT .../placement refuses. Stored there it would silently
        // bind to whichever race the entry joined later, unchecked against the
        // venue's laneCount and against the lanes already claimed in that race.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId, lane });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EntryLaneInvalid);

        // Refused before anything is written: no half-entered swimmer.
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.AnyAsync(e =>
                e.CarnivalEventId == seeded.EventId && e.StudentId == seeded.EligibleBoyId)));
    }

    [Fact]
    public async Task An_entry_with_no_heat_or_lane_is_ordinary()
    {
        // Nobody types a heat or a lane (ADR-0015 §5), and the bounds must not
        // turn their absence into an error. ADR-0039 §3: the server places the
        // entry rather than leaving it in no race — a derived heat, and no lane,
        // because this carnival does not fill lanes.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Json(response);
        Assert.Equal(1, body.GetProperty("heat").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lane").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("raceId").ValueKind);
    }

    [Fact]
    public async Task Wrong_age_group_is_rejected_422()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.YoungerBoyId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AgeGroupIneligible);
    }

    [Fact]
    public async Task Wrong_category_without_override_is_rejected_422()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.GirlId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CategoryIneligible);
    }

    [Fact]
    public async Task A_category_refusal_names_the_events_division_and_not_the_students_category()
    {
        // ADR-0026 §2: a refusal sentence is still a response body. It says what
        // the event requires; the student's own category stays on the server.
        var seeded = await CarnivalSeed.SeedAsync(_factory); // the Boys division
        var refused = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.GirlId });
        await refused.AssertErrorAsync(ErrorCodes.CategoryIneligible);
        var detail = await refused.ErrorDetailAsync();
        Assert.Contains("Boys", detail);
        Assert.DoesNotContain("Girls", detail);

        // The override endpoint refuses through the same rule and must say no more.
        var entryId = (await Json(await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId }))).GetProperty("id").GetGuid();
        var badOverride = await _admin.PostAsJsonAsync(
            $"{EntriesPath(seeded)}/{entryId}/category-override",
            new { category = "F", reason = "Deliberately ineligible, to read the refusal" });
        await badOverride.AssertErrorAsync(ErrorCodes.CategoryIneligible);
        var overrideDetail = await badOverride.ErrorDetailAsync();
        Assert.Contains("Boys", overrideDetail);
        Assert.DoesNotContain("Girls", overrideDetail);
    }

    [Fact]
    public async Task Category_override_at_creation_enters_the_student_and_audits_reason()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        // S-category student competes in the M event under an audited override (ADR-0003).
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded), new
        {
            studentId = seeded.SCategoryStudentId,
            categoryOverride = "M",
            categoryOverrideReason = "Student elected to compete in the M division for this carnival",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // ADR-0003 wants the applied override visible; ADR-0026 §2 caps what that
        // may say to a boolean. The values themselves are checked in the store.
        var body = await Json(response);
        Assert.True(body.GetProperty("overrideApplied").GetBoolean());

        var entry = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.SingleAsync(e => e.StudentId == seeded.SCategoryStudentId));
        Assert.Equal(CompetitionCategory.S, entry.SnapshotCategory); // snapshot default untouched
        Assert.Equal(CompetitionCategory.M, entry.CategoryOverride);
        Assert.Equal("event-admin-1", entry.CategoryOverrideActorSubject);
        Assert.NotNull(entry.CategoryOverrideAtUtc);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == entry.Id.ToString() && a.Action == AuditActions.EntryCategoryOverride));
        Assert.Contains("elected to compete", audit.Reason);
        Assert.Contains("\"S\"", audit.PriorValuesJson);
        Assert.Contains("\"M\"", audit.NewValuesJson);
    }

    [Fact]
    public async Task Category_override_without_reason_is_400()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId, categoryOverride = "M" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Override_on_existing_entry_updates_actor_timestamp_reason_and_audits()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: null); // open-category event
        var create = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        var response = await _admin.PostAsJsonAsync(
            $"{EntriesPath(seeded)}/{entryId}/category-override",
            new { category = "S", reason = "Divisional placement agreed with student" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("overrideApplied").GetBoolean());
        Assert.Equal(CompetitionCategory.S, await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.Where(e => e.Id == entryId).Select(e => e.CategoryOverride).SingleAsync()));

        // Clearing the override is itself an audited change with a reason.
        var clear = await _admin.PostAsJsonAsync(
            $"{EntriesPath(seeded)}/{entryId}/category-override",
            new { category = (string?)null, reason = "Reverting to snapshot default" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.False((await Json(clear)).GetProperty("overrideApplied").GetBoolean());

        var audits = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.EntityId == entryId.ToString() && a.Action == AuditActions.EntryCategoryOverride)
            .ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.False(string.IsNullOrEmpty(a.Reason)));
    }

    [Fact]
    public async Task Override_breaking_event_division_eligibility_is_422()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory); // M-division event
        var create = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        var response = await _admin.PostAsJsonAsync(
            $"{EntriesPath(seeded)}/{entryId}/category-override",
            new { category = "F", reason = "Would break division match" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ADR-0010: offline-queued creation supplies its client-minted entry id in
    // the Idempotency-Key header. The id is adopted, retries replay the stored
    // response (never a duplicate), and the same key with a different payload is
    // refused as a client bug.
    [Fact]
    public async Task Client_supplied_entry_id_is_adopted_and_retries_replay()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create(Guid studentId) =>
            new(HttpMethod.Post, EntriesPath(seeded))
            {
                Content = JsonContent.Create(new { studentId }),
                Headers = { { "Idempotency-Key", entryId.ToString() } },
            };

        var first = await _admin.SendAsync(Create(seeded.EligibleBoyId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(entryId, (await Json(first)).GetProperty("id").GetGuid()); // client id adopted

        // Retry, same key + same payload: the stored 201 is replayed, no second row.
        var retry = await _admin.SendAsync(Create(seeded.EligibleBoyId));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(entryId, (await Json(retry)).GetProperty("id").GetGuid());
        var count = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.CountAsync(e => e.Id == entryId));
        Assert.Equal(1, count);

        // Same key, DIFFERENT payload: rejected as a payload mismatch (422), never
        // silently replayed against a new student.
        var mismatch = await _admin.SendAsync(Create(seeded.SCategoryStudentId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        await mismatch.AssertErrorAsync(ErrorCodes.IdempotencyPayloadMismatch);
    }

    /// <summary>
    /// A key that is PRESENT but unparseable is a client bug, not "no key".
    /// Degrading it to the unkeyed path minted a server-side id, so a retry the
    /// device believed was deduplicated made a second entry.
    /// </summary>
    [Fact]
    public async Task A_malformed_idempotency_key_is_refused_rather_than_run_unkeyed()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var request = new HttpRequestMessage(HttpMethod.Post, EntriesPath(seeded))
        {
            Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId }),
            Headers = { { "Idempotency-Key", "not-a-uuid" } },
        };

        var response = await _admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.IdempotencyKeyRequired);
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.CarnivalEventId == seeded.EventId)));
    }

    // ---- Captured-actor attribution (ADR-0002/0006) ---------------------
    // Same property as a result submission or correction: a deck add queued by
    // one marshal and drained after a handover is the FIRST marshal's, and the
    // entry it mints carries the lane and heat that decide house points
    // (ADR-0015). The audit trail names both identities and the device.

    [Fact]
    public async Task Queued_entry_create_drained_after_handover_records_the_captured_actor_and_audits_both()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = Guid.CreateVersion7();
        var request = new HttpRequestMessage(HttpMethod.Post, EntriesPath(seeded))
        {
            Content = JsonContent.Create(new { studentId = seeded.EligibleBoyId }),
            Headers = { { "Idempotency-Key", entryId.ToString() } },
        };
        request.Headers.Add("X-Device-Id", "ipad-9");
        request.Headers.Add(CapturedActor.SubjectHeader, "marshal-departed");
        request.Headers.Add(CapturedActor.NameHeader, Uri.EscapeDataString("Dana Departed"));

        // Drained under the administrator's session, not Dana's.
        var response = await _admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.SingleAsync(e => e.Id == entryId));
        Assert.Equal("marshal-departed", entry.CreatedBySubject); // never re-attributed

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == entryId.ToString() && a.Action == AuditActions.EntryCreate));
        Assert.Equal("marshal-departed", audit.ActorSubject);
        Assert.Null(audit.ActorRole); // the captured actor's role at capture is unknown at drain
        Assert.Contains("\"syncSessionSubject\":\"event-admin-1\"", audit.NewValuesJson);
        Assert.Contains("\"syncSessionRole\":\"CarnivalAdministrator\"", audit.NewValuesJson);
        Assert.Contains("\"deviceId\":\"ipad-9\"", audit.NewValuesJson);
        Assert.Contains("Dana Departed", audit.NewValuesJson);
    }

    [Fact]
    public async Task An_override_stays_attributed_to_the_session_that_authorised_it()
    {
        // The override is administrator-only and that check reads the session
        // user, so a captured actor must not take credit for an authority they
        // were not checked for. The entry itself is still the marshal's.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var request = new HttpRequestMessage(HttpMethod.Post, EntriesPath(seeded))
        {
            Content = JsonContent.Create(new
            {
                studentId = seeded.SCategoryStudentId,
                categoryOverride = "M",
                categoryOverrideReason = "Student elected to compete in this division",
            }),
        };
        request.Headers.Add(CapturedActor.SubjectHeader, "marshal-departed");

        var response = await _admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.SingleAsync(e => e.StudentId == seeded.SCategoryStudentId));
        Assert.Equal("marshal-departed", entry.CreatedBySubject);
        Assert.Equal("event-admin-1", entry.CategoryOverrideActorSubject);
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == entry.Id.ToString()
                              && a.Action == AuditActions.EntryCategoryOverride));
        Assert.Equal("event-admin-1", audit.ActorSubject);
    }

    // A queued create the server refuses on eligibility is a business 422; the
    // client surfaces "not added" and the operator dismisses it or fixes the
    // roster and retries. The refusal is NOT recorded (C1), so the same
    // client-minted id can be re-sent once the student becomes eligible. The
    // eligibility rule is unchanged by the id being client-supplied.
    [Fact]
    public async Task Client_supplied_id_on_an_ineligible_student_is_422()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var request = new HttpRequestMessage(HttpMethod.Post, EntriesPath(seeded))
        {
            Content = JsonContent.Create(new { studentId = seeded.YoungerBoyId }), // wrong age group
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        var response = await _admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AgeGroupIneligible);
    }

    [Fact]
    public async Task Duplicate_entry_for_the_same_student_is_409()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await _admin.PostAsJsonAsync(EntriesPath(seeded), new { studentId = seeded.EligibleBoyId });
        var duplicate = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Inactive_or_snapshotless_students_are_rejected_422()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var inactive = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.InactiveStudentId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inactive.StatusCode);

        var noSnapshot = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.NoSnapshotStudentId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noSnapshot.StatusCode);
    }

    [Fact]
    public async Task Entries_close_with_the_event_and_carnival()
    {
        var closedEvent = await CarnivalSeed.SeedAsync(_factory, eventStatus: CarnivalEventStatus.Closed);
        Assert.Equal(HttpStatusCode.Conflict,
            (await _admin.PostAsJsonAsync(EntriesPath(closedEvent),
                new { studentId = closedEvent.EligibleBoyId })).StatusCode);

        var closedCarnival = await CarnivalSeed.SeedAsync(_factory, carnivalStatus: CarnivalStatus.Closed);
        Assert.Equal(HttpStatusCode.Conflict,
            (await _admin.PostAsJsonAsync(EntriesPath(closedCarnival),
                new { studentId = closedCarnival.EligibleBoyId })).StatusCode);
    }

    [Fact]
    public async Task Eligible_student_search_matches_division_and_filters()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var basePath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/eligible-students";
        // The eligible search is marshalling surface since ADR-0032 §3.
        var searcher = _factory.CreateClientAs("search-recorder-1", "Recorder");

        // Division match: the two M students plus the S student, who is listed in
        // Boys, Girls and Open alike (ADR-0023 §1); the girl (F), the younger
        // boy, the inactive and the snapshotless students are all excluded.
        var all = await (await searcher.GetAsync(basePath)).Content.ReadFromJsonAsync<JsonElement>();
        var names = Students(all).Select(s => s.GetProperty("displayName").GetString()).ToList();
        Assert.Equal(["Aiden Brook", "Blake Chen", "Emery Frost"], names);
        // K11: a short result set is explicitly NOT truncated.
        Assert.False(all.GetProperty("truncated").GetBoolean());

        // Name filter (case-insensitive, preferred/first/surname).
        var byName = await (await searcher.GetAsync($"{basePath}?search=blake"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Blake Chen", Students(byName).Single().GetProperty("displayName").GetString());

        // House filter.
        var byHouse = await (await searcher.GetAsync($"{basePath}?house=banksia"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Aiden Brook", Students(byHouse).Single().GetProperty("displayName").GetString());

        // Already-entered students drop out of the search.
        await _admin.PostAsJsonAsync(EntriesPath(seeded), new { studentId = seeded.EligibleBoyId });
        var remaining = await (await searcher.GetAsync(basePath)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            ["Blake Chen", "Emery Frost"],
            Students(remaining).Select(s => s.GetProperty("displayName").GetString()));
    }

    /// <summary>The K11 search page: <c>{ students, truncated, limit }</c>.</summary>
    private static IEnumerable<JsonElement> Students(JsonElement page) =>
        page.GetProperty("students").EnumerateArray();

    [Fact]
    public async Task An_open_age_scope_admits_and_finds_students_of_every_age()
    {
        // ADR-0058 §1: the all-ages division is the one that does not filter on
        // the age axis. Both halves of the rule have to move together — a student
        // the entry path accepts but the search never lists is a carnival-day
        // dead end (K11), and the opposite is a 422 on a student the screen
        // offered.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await CarnivalSeed.QueryAsync(_factory, async db =>
        {
            var carnivalEvent = await db.CarnivalEvents.SingleAsync(e => e.Id == seeded.EventId);
            carnivalEvent.AgeGroup = AgeScope.OpenKey;
            return await db.SaveChangesAsync();
        });
        var basePath = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/eligible-students";

        // The junior (Dale Egan) is now offered, where the primary-age division
        // did not list him. Searched by name because an open scope also offers
        // every student the suite's other seeds have put on the roster.
        var page = await (await _admin.GetAsync($"{basePath}?search=Egan"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Dale Egan",
            Students(page).Select(s => s.GetProperty("displayName").GetString()));

        // And the entry path agrees: the junior was a 422 before the widening.
        var entry = await _admin.PostAsJsonAsync(EntriesPath(seeded), new { studentId = seeded.YoungerBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        // The entry snapshots the student's OWN age, not the event's scope —
        // the division is the event's, the age group is the student's (§9.1).
        Assert.Equal(seeded.JuniorAgeGroup, (await Json(entry)).GetProperty("ageGroup").GetString());
    }

    // --- ADR-0023: the S widening, and one entry per definition per tier ------

    private static string EventEntriesPath(SeededCarnival seeded, Guid eventId) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId}/entries";

    [Theory]
    [InlineData(CompetitionCategory.M)]
    [InlineData(CompetitionCategory.F)]
    [InlineData(CompetitionCategory.S)]
    public async Task An_S_student_may_enter_the_boys_girls_and_open_divisions(CompetitionCategory division)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: division);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // ADR-0023 §2: dual listing mints NO override. What she contested is the
        // division's category; the roster default is untouched (and, ADR-0026 §2,
        // never on the wire to say so).
        var body = await Json(response);
        Assert.False(body.GetProperty("overrideApplied").GetBoolean());

        Assert.Empty(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .Where(a => a.Action == AuditActions.EntryCategoryOverride
                && a.EntityId == body.GetProperty("id").GetGuid().ToString())
            .ToListAsync()));
    }

    [Fact]
    public async Task An_M_student_is_still_refused_by_the_girls_division()
    {
        // CATEGORY_INELIGIBLE is what stands between a mistap on a
        // several-hundred-name roster and the wrong division.
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: CompetitionCategory.F);
        var response = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CategoryIneligible);
    }

    // --- ADR-0026 §1: an Open division is open to everyone -------------------

    [Fact]
    public async Task An_M_student_may_enter_an_open_division_so_it_is_not_a_list_of_gender_diverse_students()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: CompetitionCategory.S);
        var entry = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        // No override was needed, so none was minted: eligibility widened, the
        // roster default did not move.
        Assert.False((await Json(entry)).GetProperty("overrideApplied").GetBoolean());

        // The search offers the same students the entry path accepts.
        var page = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/eligible-students"));
        Assert.Contains(Students(page), s => s.GetProperty("studentId").GetGuid() == seeded.GirlId);
    }

    [Fact]
    public async Task An_open_entry_still_blocks_the_same_student_from_a_boys_entry_at_the_same_tier()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, category: CompetitionCategory.S);
        var boys = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.M);

        var open = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);

        var second = await _admin.PostAsJsonAsync(EventEntriesPath(seeded, boys),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await second.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
        Assert.Equal(seeded.EventId, (await second.ErrorContextAsync("carnivalEventId")).GetGuid());
    }

    [Fact]
    public async Task A_second_entry_in_a_sibling_division_is_409_naming_where_the_first_one_is()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory); // the Boys division
        var girls = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);

        var first = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _admin.PostAsJsonAsync(EventEntriesPath(seeded, girls),
            new { studentId = seeded.SCategoryStudentId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await second.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);

        // The refusal has to be actionable: a marshal cannot act on "she is
        // entered somewhere else" (ADR-0023 §4).
        Assert.Equal((await Json(first)).GetProperty("id").GetGuid(),
            (await second.ErrorContextAsync("entryId")).GetGuid());
        Assert.Equal(seeded.EventId, (await second.ErrorContextAsync("carnivalEventId")).GetGuid());
        Assert.Equal(seeded.PrimaryAgeGroup, (await second.ErrorContextAsync("ageGroup")).GetString());
        Assert.Equal("M", (await second.ErrorContextAsync("category")).GetString());
        Assert.Equal("Competitive", (await second.ErrorContextAsync("tier")).GetString());

        // ENTRY_EXISTS keeps its shipped meaning: the SAME-division duplicate.
        var sameDivision = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId });
        await sameDivision.AssertErrorAsync(ErrorCodes.EntryExists);

        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.StudentId == seeded.SCategoryStudentId)));
    }

    [Theory]
    [InlineData(null, "All categories")]
    [InlineData(CompetitionCategory.S, "Open")]
    [InlineData(CompetitionCategory.F, "Girls")]
    public async Task The_sibling_refusal_names_the_division_an_operator_can_walk_to(
        CompetitionCategory? standing, string spelling)
    {
        // "She is in Girls 13, go and look" (ADR-0023 §4) — the marshal is
        // standing in the division being refused, so naming that one says
        // nothing. A division open on the category axis is still a division and
        // may not be spelled by dropping it (ADR-0022 §1).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var other = await CarnivalSeed.AddDivisionAsync(_factory, seeded, standing);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            EventEntriesPath(seeded, other),
            new { studentId = seeded.SCategoryStudentId })).StatusCode);

        var refused = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.SCategoryStudentId });
        await refused.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
        // Detail, exceptionally (ApiErrorAssertions): the client renders it
        // verbatim and the division name is the whole point of this refusal, so
        // the NAME is pinned and the sentence around it stays free copy.
        Assert.Contains($"in the {seeded.PrimaryAgeGroup} {spelling} division",
            (await Json(refused)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task The_refusal_names_the_same_sibling_entry_on_every_retry()
    {
        // The residual race ADR-0023 accepts leaves a student with two sibling
        // entries. An operator who removes the one named, retries and is refused
        // again naming the other has no signal that a third attempt differs.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var girls = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var open = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.S);
        var later = Guid.Parse("ffffffff-ffff-7fff-8fff-ffffffffffff");
        var earlier = Guid.Parse("11111111-1111-7111-8111-111111111111");

        var create = new HttpRequestMessage(HttpMethod.Post, EventEntriesPath(seeded, girls))
        {
            Content = JsonContent.Create(new { studentId = seeded.SCategoryStudentId }),
            Headers = { { "Idempotency-Key", later.ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await _admin.SendAsync(create)).StatusCode);
        // The other device's entry: written second, minted first.
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var standing = db.Entries.AsNoTracking().Single(e => e.Id == later);
            standing.Id = earlier;
            standing.CarnivalEventId = open;
            db.Entries.Add(standing);
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refused = await _admin.PostAsJsonAsync(EntriesPath(seeded),
                new { studentId = seeded.SCategoryStudentId });
            await refused.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
            Assert.Equal(earlier, (await refused.ErrorContextAsync("entryId")).GetGuid());
        }
    }

    [Fact]
    public async Task An_all_ages_division_is_a_sibling_division_of_the_age_one()
    {
        // §3.2: the AGE axis alone puts one student in two divisions of one
        // definition, because an OPEN division admits their age as well.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var allAges = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.M,
            ageGroup: AgeScope.OpenKey);
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            EventEntriesPath(seeded, allAges), new { studentId = seeded.EligibleBoyId })).StatusCode);

        var refused = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.EntryInOtherDivision);
        // The open division is spelled, never shown as the OPEN sentinel.
        Assert.Contains("All ages Boys", (await Json(refused)).GetProperty("detail").GetString());

        // §4: the eligible list suppresses across the age axis too.
        var remaining = Students(await Json(await _admin.GetAsync(
                $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/eligible-students")))
            .Select(s => s.GetProperty("displayName").GetString()).ToList();
        Assert.DoesNotContain("Aiden Brook", remaining);
        Assert.Contains("Blake Chen", remaining);
    }

    [Fact]
    public async Task A_social_swim_of_the_same_stroke_is_a_second_swim()
    {
        // ADR-0023 §3.1, ratified narrow by the owner 2026-08-18: the one-entry
        // rule is keyed PER TIER. Pinned so the ruling cannot be widened silently.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var social = await CarnivalSeed.AddDivisionAsync(
            _factory, seeded, CompetitionCategory.M, EventTier.Social);

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            EntriesPath(seeded), new { studentId = seeded.EligibleBoyId })).StatusCode);

        // The suppression does not cross the tier either — a swimmer the
        // Competitive list has dropped is still offered on the Social one.
        var page = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{social}/eligible-students"));
        Assert.Contains("Aiden Brook",
            Students(page).Select(s => s.GetProperty("displayName").GetString()));

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            EventEntriesPath(seeded, social), new { studentId = seeded.EligibleBoyId })).StatusCode);
    }

    [Fact]
    public async Task The_eligible_list_drops_a_student_already_entered_in_a_sibling_division()
    {
        // The common case never reaches a refusal (ADR-0023 §4); the server check
        // still stands, because the deck browses a cached per-division roster.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var girls = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var girlsList = $"/api/v1/carnivals/{seeded.CarnivalId}/events/{girls}/eligible-students";

        Assert.Contains("Emery Frost",
            Students(await Json(await _admin.GetAsync(girlsList)))
                .Select(s => s.GetProperty("displayName").GetString()));

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync(
            EntriesPath(seeded), new { studentId = seeded.SCategoryStudentId })).StatusCode);

        var after = Students(await Json(await _admin.GetAsync(girlsList)))
            .Select(s => s.GetProperty("displayName").GetString()).ToList();
        Assert.DoesNotContain("Emery Frost", after);
        Assert.Contains("Cora Diaz", after); // nobody else was suppressed
    }

    [Fact]
    public async Task A_replayed_offline_entry_is_not_refused_as_a_second_entry()
    {
        // The sibling check sits inside the handler, and a known key never
        // reaches it: IdempotencyService replays the stored response (the
        // Idempotency-Replayed header below is that proof). Getting this wrong
        // strands the capture.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create() => new(HttpMethod.Post, EntriesPath(seeded))
        {
            Content = JsonContent.Create(new { studentId = seeded.SCategoryStudentId }),
            Headers = { { "Idempotency-Key", entryId.ToString() } },
        };

        Assert.Equal(HttpStatusCode.Created, (await _admin.SendAsync(Create())).StatusCode);
        var replay = await _admin.SendAsync(Create());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(entryId, (await Json(replay)).GetProperty("id").GetGuid());
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.StudentId == seeded.SCategoryStudentId)));
    }

    [Fact]
    public async Task A_colliding_submission_id_is_reported_before_a_sibling_division_once_its_key_has_aged_out()
    {
        // With the key deleted by retention, the handler is what answers a reused id. Naming the
        // sibling entry would send the operator to remove an entry that does not clear the id.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var girls = await CarnivalSeed.AddDivisionAsync(_factory, seeded, CompetitionCategory.F);
        var entryId = Guid.CreateVersion7();

        HttpRequestMessage Create(Guid eventId) => new(HttpMethod.Post, EventEntriesPath(seeded, eventId))
        {
            Content = JsonContent.Create(new { studentId = seeded.SCategoryStudentId }),
            Headers = { { "Idempotency-Key", entryId.ToString() } },
        };

        Assert.Equal(HttpStatusCode.Created, (await _admin.SendAsync(Create(seeded.EventId))).StatusCode);
        await CarnivalSeed.MutateAsync(_factory,
            db => db.IdempotencyKeys.Remove(db.IdempotencyKeys.Single(k => k.Key == entryId)));

        var collided = await _admin.SendAsync(Create(girls));
        Assert.Equal(HttpStatusCode.Conflict, collided.StatusCode);
        await collided.AssertErrorAsync(ErrorCodes.SubmissionIdConflict);

        // The same create replayed after its key aged out is the entry that already exists.
        var replayed = await _admin.SendAsync(Create(seeded.EventId));
        Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
        await replayed.AssertErrorAsync(ErrorCodes.EntryExists);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Entries.CountAsync(e => e.StudentId == seeded.SCategoryStudentId)));
    }

    [Fact]
    public async Task Entry_removal_is_blocked_once_a_result_exists()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var create = await _admin.PostAsJsonAsync(EntriesPath(seeded),
            new { studentId = seeded.EligibleBoyId });
        var entryId = (await Json(create)).GetProperty("id").GetGuid();

        var official = _factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var submit = new HttpRequestMessage(HttpMethod.Post, $"{EntriesPath(seeded)}/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await official.SendAsync(submit)).StatusCode);

        var remove = await _admin.DeleteAsync($"{EntriesPath(seeded)}/{entryId}");
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
    }
}
