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
/// Result submission (idempotent per ADR-0002: the client UUIDv7 is both the
/// idempotency key and the result id) and the §9.2 lifecycle: correct with
/// optimistic concurrency, verify, return-to-provisional and the Verifier
/// finalisation gate.
/// </summary>
public sealed class ResultEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _official;
    private readonly HttpClient _verifier;

    public ResultEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _admin = factory.CreateClientAs("event-admin-1", "CarnivalAdministrator");
        _official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        _verifier = factory.CreateClientAs("verifier-1", "CarnivalAdministrator");
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    private async Task<Guid> EnterAsync(SeededCarnival seeded, Guid studentId)
    {
        var response = await _admin.PostAsJsonAsync($"{EventPath(seeded)}/entries", new { studentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage SubmitRequest(
        SeededCarnival seeded, Guid entryId, Guid key, object body, string? deviceId = null,
        string? capturedActor = null, string? capturedActorName = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };
        if (deviceId is not null) request.Headers.Add("X-Device-Id", deviceId);
        if (capturedActor is not null)
            request.Headers.Add(CapturedActor.SubjectHeader, capturedActor);
        if (capturedActorName is not null)
            request.Headers.Add(CapturedActor.NameHeader, capturedActorName);
        return request;
    }

    // ---- Submission -----------------------------------------------------

    [Fact]
    public async Task Submission_stores_canonical_time_provisional_with_revision_1_and_audit()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "1:02.34" }, deviceId: "ipad-3"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Json(response);
        Assert.Equal(key, body.GetProperty("id").GetGuid()); // client UUIDv7 IS the result id
        Assert.Equal(62.34m, body.GetProperty("measuredValue").GetDecimal());
        // ADR-0017: a capture that passes the coherence rules IS validated, at
        // capture, with no human act. The state survives; the gate is gone.
        Assert.Equal("Verified", body.GetProperty("lifecycleState").GetString());
        Assert.Equal(1, body.GetProperty("currentRevision").GetInt32());

        var result = await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Include(r => r.Revisions).SingleAsync(r => r.Id == key));
        Assert.Equal(CarnivalSeed.AssignedOfficial, result.RecordedBySubject);
        Assert.Equal("ipad-3", result.DeviceId);
        var revision = Assert.Single(result.Revisions);
        Assert.Equal(1, revision.RevisionNumber);

        Assert.NotNull(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultSubmit)));
    }

    [Fact]
    public async Task Replay_with_same_key_returns_stored_response_and_never_duplicates()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();
        var body = new { value = "34.56" };

        var first = await _official.SendAsync(SubmitRequest(seeded, entryId, key, body));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());

        var retry = await _official.SendAsync(SubmitRequest(seeded, entryId, key, body));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());

        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.CountAsync(r => r.EntryId == entryId)));
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.ResultRevisions.CountAsync(r => r.ResultId == key)));
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_422_not_a_replay()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        await _official.SendAsync(SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        var clash = await _official.SendAsync(SubmitRequest(seeded, entryId, key, new { value = "35.00" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, clash.StatusCode);
        await clash.AssertErrorAsync(ErrorCodes.IdempotencyPayloadMismatch);
    }

    [Theory]
    [InlineData("DNS")]
    [InlineData("dnf")]
    [InlineData("Dq")]
    public async Task Non_result_statuses_are_accepted_without_a_value(string status)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(status.ToUpperInvariant() switch
        {
            "DNS" => "Dns", "DNF" => "Dnf", _ => "Dq",
        }, body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
    }

    [Theory]
    [InlineData("{}")]                                    // neither value nor status
    [InlineData("""{"status":"DNS","value":"34.56"}""")]  // status + value
    [InlineData("""{"value":"not-a-time"}""")]            // unparseable time
    [InlineData("""{"status":"Retired"}""")]              // unknown status
    public async Task Invalid_submissions_are_400(string rawBody)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await _official.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Second_result_for_the_same_entry_is_a_409_conflict()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _official.SendAsync(SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));

        // A second official's capture (different key) surfaces as a conflict,
        // never a silent duplicate.
        var second = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.60" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Results_are_only_accepted_while_the_event_is_open()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, eventStatus: CarnivalEventStatus.Closed);
        var entryId = await CarnivalSeed.QueryAsync(_factory, async db =>
        {
            // Seed the entry directly — entry via API is also closed.
            var snapshot = await db.RosterSnapshots.SingleAsync(
                s => s.StudentId == seeded.EligibleBoyId && s.IsCurrent);
            var entry = new Entry
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = seeded.EventId,
                StudentId = seeded.EligibleBoyId,
                RosterSnapshotId = snapshot.Id,
                SnapshotDisplayName = "Aiden Brook",
                SnapshotYearLevel = 7,
                SnapshotHouseCode = "BANKSIA",
                SnapshotAgeGroup = seeded.PrimaryAgeGroup,
                SnapshotCategory = CompetitionCategory.M,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "seeder",
            };
            db.Entries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// An in-handler refusal on the REAL results route does not burn the client's
    /// idempotency key: the same queued item succeeds once the state moves on.
    /// </summary>
    /// <remarks>
    /// This is the only HTTP-level pin of the round-1 C1 contract on the results
    /// route itself — the filter-level twin (<c>IdempotencyTests</c>) uses a
    /// throwaway demo endpoint. It was written against the assignment refusal,
    /// which ADR-0016 §3 retired, and is RE-POINTED at another state-dependent
    /// in-handler refusal rather than deleted: without it, a regression that
    /// started recording non-2xx outcomes would permanently destroy an offline
    /// capture and nothing on this route would notice.
    /// </remarks>
    [Fact]
    public async Task An_in_handler_refusal_does_not_burn_the_idempotency_key()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        // The event is not accepting results yet — transient, and exactly the
        // kind of refusal an offline queue retries through.
        await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Closed" });
        var refused = await _official.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.EventNotOpen);
        Assert.Equal("false", refused.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Empty(await CarnivalSeed.QueryAsync(_factory,
            db => db.IdempotencyKeys.Where(k => k.Key == key).ToListAsync()));

        // Nothing was recorded against the key, so the SAME queued item lands
        // once the event reopens.
        await _admin.PostAsJsonAsync($"{EventPath(seeded)}/status", new { status = "Open" });
        var retry = await _official.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("false", retry.Headers.GetValues("Idempotency-Replayed").Single());
    }

    /// <summary>
    /// The navigation progress readout (ADR-0014 §3): a tile and a division chip
    /// both say "results entered / entries", so the event projection has to carry
    /// both numbers. `resultCount` counts ENTRIES that have a result, so it can
    /// never overtake `entryCount` and "2/3" always means one swim still to
    /// record.
    /// </summary>
    [Fact]
    public async Task Event_projection_carries_entry_and_result_counts_for_the_progress_readout()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var listPath = $"/api/v1/carnivals/{seeded.CarnivalId}/events";

        async Task<(int Entries, int Results)> CountsAsync()
        {
            var list = await Json(await _admin.GetAsync(listPath));
            var item = list.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == seeded.EventId);
            var single = await Json(await _admin.GetAsync($"{listPath}/{seeded.EventId}"));
            // The single GET is the same projection, so the chip on the event
            // page and the tile on the landing screen cannot disagree.
            Assert.Equal(item.GetRawText(), single.GetRawText());
            return (item.GetProperty("entryCount").GetInt32(), item.GetProperty("resultCount").GetInt32());
        }

        Assert.Equal((0, 0), await CountsAsync());

        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.Equal((2, 0), await CountsAsync());

        await _official.SendAsync(SubmitRequest(seeded, first, Guid.CreateVersion7(), new { value = "34.56" }));
        Assert.Equal((2, 1), await CountsAsync());
    }

    // ---- Captured-actor attribution (ADR-0002/0006) ---------------------
    // The outbox is device-bound: a queued item drained after a device
    // handover must be recorded against the actor captured at ENTRY time,
    // never the sync-session user — while the audit trail carries both.

    [Fact]
    public async Task Queued_submission_drained_after_handover_records_the_captured_actor_and_audits_both()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        // The departing official captured the item; the incoming (assigned)
        // official's session drains it after handover (ADR-0006).
        var response = await _official.SendAsync(SubmitRequest(
            seeded, entryId, key, new { value = "1:02.34" }, deviceId: "ipad-7",
            capturedActor: "official-departed",
            capturedActorName: Uri.EscapeDataString("Dana Departed")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("official-departed", body.GetProperty("recordedBySubject").GetString());

        var result = await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Include(r => r.Revisions).SingleAsync(r => r.Id == key));
        Assert.Equal("official-departed", result.RecordedBySubject); // never re-attributed
        Assert.Equal("ipad-7", result.DeviceId);
        var revision = Assert.Single(result.Revisions);
        Assert.Equal("official-departed", revision.ActorSubject);
        Assert.Null(revision.ActorRole); // captured actor's role at entry is unknown at drain

        // The audit event names the captured actor AND the authenticated
        // sync-session subject + device — nothing hidden.
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultSubmit));
        Assert.Equal("official-departed", audit.ActorSubject);
        Assert.Contains("\"recordedBySubject\":\"official-departed\"", audit.NewValuesJson);
        Assert.Contains($"\"syncSessionSubject\":\"{CarnivalSeed.AssignedOfficial}\"", audit.NewValuesJson);
        Assert.Contains("\"syncSessionRole\":\"Recorder\"", audit.NewValuesJson);
        Assert.Contains("\"deviceId\":\"ipad-7\"", audit.NewValuesJson);
        Assert.Contains("Dana Departed", audit.NewValuesJson);
    }

    [Fact]
    public async Task Queued_correction_drained_after_handover_records_the_captured_actor_too()
    {
        // ADR-0002/0006 say capture-time identity is never re-attributed. That
        // held for submissions and NOT for corrections, which were attributed to
        // whoever was signed in at drain — so on a shared iPad a correction typed
        // by Dana and drained after Sam signs in was recorded against Sam. That
        // also made ADR-0016 §2's "every amendment stores its actor" false
        // offline, which is the claim the ADR rests its no-new-mechanism
        // argument on.
        var (seeded, _, resultId) = await SubmittedResultAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/results/{resultId}/correct")
        {
            Content = JsonContent.Create(new
            {
                expectedRevision = 1,
                value = "33.90",
                reason = "Dana re-read the board after the heat.",
            }),
        };
        request.Headers.Add(CapturedActor.SubjectHeader, "official-departed");
        request.Headers.Add(CapturedActor.NameHeader, Uri.EscapeDataString("Dana Departed"));

        // Drained by the ASSIGNED official's session, not Dana's.
        var response = await _official.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Include(r => r.Revisions).SingleAsync(r => r.Id == resultId));
        var correction = Assert.Single(result.Revisions, r => r.RevisionNumber == 2);
        Assert.Equal("official-departed", correction.ActorSubject);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == resultId.ToString() && a.Action == AuditActions.ResultCorrect));
        Assert.Equal("official-departed", audit.ActorSubject);
        // Both identities, exactly as on a submission — the sync session stays visible.
        Assert.Contains($"\"syncSessionSubject\":\"{CarnivalSeed.AssignedOfficial}\"", audit.NewValuesJson);
        Assert.Contains("Dana Departed", audit.NewValuesJson);
    }

    [Fact]
    public async Task Submission_without_captured_actor_header_records_the_session_subject()
    {
        // Online direct submit: no X-Captured-Actor → fall back to the
        // authenticated session subject (also covered by the canonical
        // submission test; asserted explicitly here as the fallback rule).
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultSubmit));
        Assert.Equal(CarnivalSeed.AssignedOfficial, audit.ActorSubject);
        Assert.Equal("Recorder", audit.ActorRole);
        Assert.Contains($"\"syncSessionSubject\":\"{CarnivalSeed.AssignedOfficial}\"", audit.NewValuesJson);
    }

    // ADR-0009: submission is ResultsOfficial (assigned events only, §8) OR
    // EventAdministrator (runs the carnival — no per-event assignment).
    // Verifiers review/resolve/finalise and get NO submission path or
    // assignment-check bypass. The policy and the assignment filter agree.

    [Fact]
    public async Task Unassigned_event_administrator_can_submit_and_audit_records_actor_and_role()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        // "event-admin-1" is NOT in CarnivalEventOfficials — ADR-0009 lets an
        // administrator record for any event in the carnival they run.
        var response = await _admin.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Verified", (await Json(response)).GetProperty("lifecycleState").GetString());

        var result = await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.SingleAsync(r => r.Id == key));
        Assert.Equal("event-admin-1", result.RecordedBySubject);

        // §9.3 / ADR-0009: who recorded — official or administrator — is
        // always on the record, with actor identity AND role.
        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultSubmit));
        Assert.Equal("event-admin-1", audit.ActorSubject);
        Assert.Equal("CarnivalAdministrator", audit.ActorRole);
    }

    [Fact]
    public async Task Unassigned_event_administrator_can_correct_results()
    {
        var (_, _, resultId) = await SubmittedResultAsync();

        var response = await _admin.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            value = "33.90",
            reason = "Administrator correction (ADR-0009)",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await Json(response)).GetProperty("currentRevision").GetInt32());
    }

    // `Verifier_role_alone_cannot_submit_results` is GONE, and its deletion is the
    // point rather than a casualty: it pinned §8's "verifiers review, they do not
    // capture" separation, which ADR-0017 emptied (there is nothing to verify) and
    // ADR-0016 then collapsed. Re-pointing it at CarnivalAdministrator would have
    // asserted the opposite of the ADR. `Capture_still_requires_a_role_that_may_record`
    // above keeps the property that survives: a Viewer is refused.

    /// <summary>
    /// ADR-0016 §3: an official captures for any event in the carnival, assigned
    /// or not — and the assignment record survives as advisory data.
    /// </summary>
    /// <remarks>
    /// This replaces two tests that pinned the opposite. The gate could not
    /// survive ADR-0015: a race spanning two events is ordinary, so the volunteer
    /// recorded exactly the lane the screen showed them and was refused with a
    /// RETRYABLE code, which halts that device's whole drain head-of-line.
    /// </remarks>
    [Fact]
    public async Task An_unassigned_official_can_capture_and_amend_any_event_in_the_carnival()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var unassigned = _factory.CreateClientAs("official-unassigned", "Recorder");
        var key = Guid.CreateVersion7();

        var captured = await unassigned.SendAsync(
            SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Created, captured.StatusCode);

        // …and may amend it. Ownership was never the rule either: an assigned
        // official could always correct any result on their event, so restricting
        // amendment to the capturer would leave a typo unfixable once they left.
        var amended = await unassigned.PostAsJsonAsync($"/api/v1/results/{key}/correct",
            new { expectedRevision = 1, value = "33.90", reason = "Timekeeper re-read the board" });
        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);

        // The assignment record is untouched — it still answers "who is expected
        // at this event" and still drives ADR-0014's "My events" block.
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEventOfficials
            .CountAsync(o => o.CarnivalEventId == seeded.EventId)));
    }

    [Fact]
    public async Task Capture_still_requires_a_role_that_may_record()
    {
        // Retiring the assignment gate is not the same as opening the route: a
        // named, authenticated recorder is still required, and every action is
        // still audited. That is what makes the gate's removal a coordination
        // change rather than a security one.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var viewer = _factory.CreateClientAs("viewer-1", "Viewer");

        var refused = await viewer.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ---- Lifecycle ------------------------------------------------------

    private async Task<(SeededCarnival Seeded, Guid EntryId, Guid ResultId)> SubmittedResultAsync()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();
        var response = await _official.SendAsync(SubmitRequest(seeded, entryId, key, new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (seeded, entryId, key);
    }

    [Fact]
    public async Task Correction_appends_a_revision_and_audits_prior_new_and_reason()
    {
        var (_, _, resultId) = await SubmittedResultAsync();

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            value = "33.90",
            reason = "Timekeeper transposition error",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(33.90m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal(2, body.GetProperty("currentRevision").GetInt32());
        // A correction does not move the lifecycle: the result was validated at
        // capture and the corrected value is validated by the same rules.
        Assert.Equal("Verified", body.GetProperty("lifecycleState").GetString());

        var revisions = await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == resultId).OrderBy(r => r.RevisionNumber).ToListAsync());
        Assert.Equal(2, revisions.Count);
        Assert.Contains("34.56", revisions[1].PriorValuesJson);
        Assert.Contains("33.9", revisions[1].NewValuesJson);
        Assert.Equal("Timekeeper transposition error", revisions[1].Reason);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == resultId.ToString() && a.Action == AuditActions.ResultCorrect));
        Assert.Contains("34.56", audit.PriorValuesJson);
        Assert.Contains("33.9", audit.NewValuesJson);
    }

    [Fact]
    public async Task Stale_expected_revision_is_a_409_optimistic_concurrency_conflict()
    {
        var (_, _, resultId) = await SubmittedResultAsync();
        await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "33.90", reason = "First correction" });

        // A second corrector read revision 1 before the first correction landed.
        var stale = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "34.10", reason = "Concurrent correction" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await stale.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);
        Assert.Equal(2, (await stale.ErrorContextAsync("currentRevision")).GetInt32());

        // Never last-write-wins: the first correction stands.
        var result = await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.SingleAsync(r => r.Id == resultId));
        Assert.Equal(33.90m, result.MeasuredValue);
        Assert.Equal(2, result.CurrentRevision);
    }

    [Fact]
    public async Task Correction_without_reason_is_400()
    {
        var (_, _, resultId) = await SubmittedResultAsync();
        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "33.90" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_correct_a_result()
    {
        // The correction path keeps its POLICY gate after ADR-0016 §3 retires the
        // assignment gate. (This test previously asserted that an unassigned
        // official was refused; that refusal is what §3 removes.)
        var (_, _, resultId) = await SubmittedResultAsync();
        var viewer = _factory.CreateClientAs("viewer-correct", "Viewer");

        var refused = await viewer.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "33.90", reason = "Not my job" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ADR-0017 deleted the human verify and return-to-provisional acts, so the two
    // tests that walked them (Verify_then_return_to_provisional_walks_9_2_transitions
    // and Illegal_lifecycle_transitions_are_409) are gone with the endpoints. What
    // they were really protecting — that a result cannot be amended once it is
    // published, and that the amendment path is state-gated at all — survives here.

    [Fact]
    public async Task The_verify_and_return_to_provisional_endpoints_no_longer_exist()
    {
        var (_, _, resultId) = await SubmittedResultAsync();

        // Not 403: the routes are gone, because the human act is gone. If either
        // ever answers again, something has re-introduced manual verification.
        Assert.Equal(HttpStatusCode.NotFound, (await _verifier.PostAsJsonAsync(
            $"/api/v1/results/{resultId}/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _verifier.PostAsJsonAsync(
            $"/api/v1/results/{resultId}/return-to-provisional", new { reason = "r" })).StatusCode);
    }

    [Fact]
    public async Task A_validated_result_stays_correctable_until_it_is_finalised()
    {
        var (seeded, _, resultId) = await SubmittedResultAsync();

        // Validated at capture, and STILL amendable — this is the change. Under
        // §9.2 as built, a Verified result refused correction until a human
        // returned it to provisional; that human no longer exists.
        var amended = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "33.90", reason = "Timekeeper re-read the board" });
        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);
        Assert.Equal(2, (await Json(amended)).GetProperty("currentRevision").GetInt32());

        // Carnival finalisation is the one thing that closes it (ADR-0017 §4).
        await _admin.PostAsJsonAsync($"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/status", new { status = "Finalised" })).StatusCode);

        var refused = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 2, value = "33.80", reason = "Too late" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.ResultFinalised);
    }

    // ---- Carnival finalisation (ADR-0017 §4) ------------------------------
    // The per-event gate is gone. These cover what replaced it: one act, per
    // carnival, that publishes the day.

    private string CarnivalStatusPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/status";

    [Fact]
    public async Task Carnival_finalisation_blocks_on_entries_that_owe_a_result()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entry1 = await EnterAsync(seeded, seeded.EligibleBoyId);
        var entry2 = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        var key = Guid.CreateVersion7();
        await _official.SendAsync(SubmitRequest(seeded, entry1, key, new { value = "34.56" }));

        // Blocked: entry2 has no result at all. The refusal NAMES who is missing —
        // an organiser at 4pm needs to know which swimmer, not just a count. The
        // carnival is still Open here, which is the only state the missing result
        // can be added in (see Closing_the_carnival_before_every_result_is_in…).
        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        var missing = await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" });
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        await missing.AssertErrorAsync(ErrorCodes.CarnivalFinalisationBlocked);
        Assert.Equal(1, (await missing.ErrorContextAsync("entriesWithoutResult")).GetInt32());
        Assert.Contains("Blake Chen", (await missing.ErrorContextAsync("sample")).ToString());
        Assert.False((await missing.ErrorContextAsync("sampleTruncated")).GetBoolean());
        Assert.Contains("(DNS, DNF, DQ, NM)", await missing.ErrorDetailAsync());

        // The same day, done in the right order: DNS satisfies "a result or a
        // non-result status" (ADR-0017 §4). Note the EVENT is never closed or
        // finalised anywhere here — completion is derived, so nothing has to be
        // walked through a per-event gate.
        var complete = await CarnivalSeed.SeedAsync(_factory);
        var e1 = await EnterAsync(complete, complete.EligibleBoyId);
        var e2 = await EnterAsync(complete, complete.SecondEligibleBoyId);
        var okKey = Guid.CreateVersion7();
        var dnsKey = Guid.CreateVersion7();
        await _official.SendAsync(SubmitRequest(complete, e1, okKey, new { value = "34.56" }));
        await _official.SendAsync(SubmitRequest(complete, e2, dnsKey, new { status = "DNS" }));
        await _admin.PostAsJsonAsync(CarnivalStatusPath(complete), new { status = "Closed" });

        var finalise = await _admin.PostAsJsonAsync(CarnivalStatusPath(complete), new { status = "Finalised" });
        Assert.Equal(HttpStatusCode.OK, finalise.StatusCode);
        seeded = complete;
        key = okKey;

        var states = await CarnivalSeed.QueryAsync(_factory, db => db.Results
            .Where(r => r.Entry.CarnivalEventId == seeded.EventId)
            .Select(r => r.LifecycleState).ToListAsync());
        Assert.All(states, s => Assert.Equal(ResultLifecycleState.Final, s));

        // Every published result carries the transition in the audit trail.
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.ResultFinalise
                             && (a.EntityId == key.ToString() || a.EntityId == dnsKey.ToString()))));
    }

    /// <summary>
    /// Closing the carnival before the last result is in is RECOVERABLE — the
    /// escape is Closed → Open (owner decision, 2026-08-11).
    /// </summary>
    /// <remarks>
    /// Without the reopen this is a dead end: capture needs an Open carnival and
    /// finalisation needs every entry resulted, so a carnival closed one DNS early
    /// could never be published and the refusal named a swimmer nobody could do
    /// anything about. The trap predates ADR-0017 — but the ADR moved the
    /// completeness check from a per-event gate walked while the carnival was
    /// still Open to carnival finalisation, which happens after Closed.
    /// </remarks>
    [Fact]
    public async Task A_carnival_closed_before_the_last_result_can_be_reopened_to_finish()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });

        // Closed early: the capture is refused and finalisation is blocked.
        var late = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        await late.AssertErrorAsync(ErrorCodes.CarnivalNotOpen);
        var blocked = await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await blocked.AssertErrorAsync(ErrorCodes.CarnivalFinalisationBlocked);

        // The escape: reopen, record the missing swim, close, publish.
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Open" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }))).StatusCode);
        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);
    }

    [Fact]
    public async Task Publication_is_the_one_way_door_a_finalised_carnival_never_reopens()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _official.SendAsync(SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));
        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);

        // The record book and the district list are derived from this moment, so
        // it is the one transition with no way back — unlike Closed.
        var reopen = await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Open" });
        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
        await reopen.AssertErrorAsync(ErrorCodes.IllegalCarnivalTransition);
    }

    /// <summary>
    /// Risk R7: the finalisation sweep selects <c>!= Draft</c>, not <c>Verified</c>.
    /// A row captured before ADR-0017 is <c>Provisional</c> and must still be
    /// published — narrowing that filter would silently strand every legacy result
    /// on the VM at the first finalisation, and nothing else in the suite would
    /// notice, because every result captured by the new code arrives Verified.
    /// </summary>
    [Fact]
    public async Task A_legacy_provisional_result_is_published_by_the_sweep()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var resultId = Guid.CreateVersion7();
        await _official.SendAsync(SubmitRequest(seeded, entryId, resultId, new { value = "34.56" }));

        // Rewind it to the pre-ADR-0017 state, as the VM's rows are today.
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            var result = db.Results.Single(r => r.Id == resultId);
            result.LifecycleState = ResultLifecycleState.Provisional;
        });

        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);

        var state = await CarnivalSeed.QueryAsync(_factory, db => db.Results
            .Where(r => r.Id == resultId).Select(r => r.LifecycleState).SingleAsync());
        Assert.Equal(ResultLifecycleState.Final, state);
    }

    [Fact]
    public async Task An_event_nobody_entered_does_not_block_finalisation()
    {
        // The sibling division has no entries. Under the old per-event gate every
        // event had to be walked to Finalised by hand, so an empty division was an
        // administrative chore; now it simply owes nothing.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _official.SendAsync(SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));

        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);
    }

    [Fact]
    public async Task A_finalised_carnival_refuses_a_second_finalisation()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _official.SendAsync(SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "34.56" }));
        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);

        // Forward-only: Finalised is terminal for the CARNIVAL (it is only the
        // event that became reversible).
        var again = await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await again.AssertErrorAsync(ErrorCodes.IllegalCarnivalTransition);
    }

    // ---- NM, the field-only status (ADR-0052 §4) ------------------------

    [Fact]
    public async Task Nm_is_accepted_on_a_field_event_and_carries_no_value()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "NM" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Nm", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
    }

    [Fact]
    public async Task Nm_is_refused_on_a_timed_event_because_that_entrant_is_a_dnf()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "NM" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.NmRequiresFieldMeasure);
    }

    [Fact]
    public async Task A_correction_to_nm_follows_the_same_measure_rule()
    {
        var timed = await CarnivalSeed.SeedAsync(_factory);
        var timedEntry = await EnterAsync(timed, timed.EligibleBoyId);
        var timedKey = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(timed, timedEntry, timedKey, new { value = "34.56" }))).StatusCode);
        var refused = await _official.PostAsJsonAsync($"/api/v1/results/{timedKey}/correct",
            new { expectedRevision = 1, status = "NM", reason = "Fouled every attempt" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.NmRequiresFieldMeasure);

        var field = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        var fieldEntry = await EnterAsync(field, field.EligibleBoyId);
        var fieldKey = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(field, fieldEntry, fieldKey, new { value = "4.32" }))).StatusCode);
        var amended = await _official.PostAsJsonAsync($"/api/v1/results/{fieldKey}/correct",
            new { expectedRevision = 1, status = "NM", reason = "Fouled every attempt" });
        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);
        var body = await Json(amended);
        Assert.Equal("Nm", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
    }

    [Fact]
    public async Task Nm_with_a_mark_on_a_timed_event_names_the_measure_not_the_value()
    {
        // Refusal order: the recorder is wrong about the EVENT, and being told
        // to delete the mark would send them to fix what is not broken.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(SubmitRequest(
            seeded, entryId, Guid.CreateVersion7(), new { status = "NM", value = "4.32" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.NmRequiresFieldMeasure);
    }

    [Theory]
    // A number is not a status: Enum.TryParse reads "9" as an undefined member
    // and "3" as Dq.
    [InlineData("9")]
    [InlineData("3")]
    // A comma list COMBINES on this non-flags enum: Dns|Dnf is 3, which is Dq.
    [InlineData("Dns,Dnf")]
    [InlineData("Swum")]
    public async Task Only_a_spelled_status_name_is_a_status(string raw)
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = raw }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.UnknownResultStatus);
    }

    [Fact]
    public async Task An_nm_entry_counts_as_resulted_for_finalisation()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "NM" }))).StatusCode);

        await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync(CarnivalStatusPath(seeded), new { status = "Finalised" })).StatusCode);
    }

    // ---- Field attempts (ADR-0053 §3/§4) ---------------------------------

    private static HttpRequestMessage AttemptRequest(
        SeededCarnival seeded, Guid entryId, int ordinal, Guid key, object body) =>
        new(HttpMethod.Put, $"{EventPath(seeded)}/entries/{entryId}/attempts/{ordinal}")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };

    private Task<HttpResponseMessage> RecordAttemptAsync(
        SeededCarnival seeded, Guid entryId, int ordinal, object body) =>
        _official.SendAsync(AttemptRequest(seeded, entryId, ordinal, Guid.CreateVersion7(), body));

    private Task<SeededCarnival> AttemptsEventAsync(int attempts = 3) =>
        CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance, attemptCount: attempts);

    /// <summary>The series as the wire carries it: a mark, "X" for a foul, "-" for untaken.</summary>
    private static IReadOnlyList<string> Series(JsonElement body) =>
        [.. body.GetProperty("attempts").EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Null
            ? "-"
            : a.TryGetProperty("foul", out _) ? "X" : a.GetProperty("value").GetDecimal().ToString())];

    private static HttpRequestMessage RawJson(HttpMethod method, string path, string body, Guid key) =>
        new(method, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };

    [Fact]
    public async Task The_first_attempt_creates_the_result_keyed_by_its_idempotency_key()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        var response = await _official.SendAsync(
            AttemptRequest(seeded, entryId, 1, key, new { value = "4.32" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(key, body.GetProperty("id").GetGuid());
        Assert.Equal(4.32m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal("Ok", body.GetProperty("status").GetString());
        Assert.Equal("Verified", body.GetProperty("lifecycleState").GetString());
        Assert.Equal(1, body.GetProperty("currentRevision").GetInt32());
        Assert.Equal(["4.32"], Series(body));

        var revision = Assert.Single(await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == key).ToListAsync()));
        Assert.Equal("Attempt 1", revision.Reason);
        Assert.NotNull(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultAttempt)));
    }

    [Fact]
    public async Task A_later_ordinal_appends_to_the_series_and_the_best_mark_is_derived()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        var second = await RecordAttemptAsync(seeded, entryId, 2, new { foul = true });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var third = await RecordAttemptAsync(seeded, entryId, 3, new { value = "4.55" });
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var body = await Json(third);
        Assert.Equal(4.55m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal(3, body.GetProperty("currentRevision").GetInt32());
        Assert.Equal(["4.32", "X", "4.55"], Series(body));

        var resultId = body.GetProperty("id").GetGuid();
        var reasons = await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == resultId)
            .OrderBy(r => r.RevisionNumber).Select(r => r.Reason).ToListAsync());
        Assert.Equal(["Attempt 1", "Attempt 2", "Attempt 3"], reasons);
    }

    [Fact]
    public async Task An_out_of_order_ordinal_leaves_the_untaken_one_as_a_gap()
    {
        // Two officials at one station: round 2 lands before round 1 (ADR-0053 §3).
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var created = await RecordAttemptAsync(seeded, entryId, 2, new { value = "4.10" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await Json(created);
        Assert.Equal(["-", "4.10"], Series(createdBody));
        var resultId = createdBody.GetProperty("id").GetGuid();
        // The creating revision names the ordinal it recorded, not "attempt 1".
        Assert.Equal("Attempt 2", (await CarnivalSeed.QueryAsync(_factory,
            db => db.ResultRevisions.SingleAsync(r => r.ResultId == resultId))).Reason);

        var filled = await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.40" });
        Assert.Equal(["4.40", "4.10"], Series(await Json(filled)));
    }

    [Fact]
    public async Task An_attempt_replay_with_the_same_key_never_writes_twice()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        var key = Guid.CreateVersion7();
        var body = new { value = "4.55" };

        var first = await _official.SendAsync(AttemptRequest(seeded, entryId, 2, key, body));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());

        var retry = await _official.SendAsync(AttemptRequest(seeded, entryId, 2, key, body));
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());

        var resultId = (await Json(first)).GetProperty("id").GetGuid();
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory,
            db => db.ResultRevisions.CountAsync(r => r.ResultId == resultId)));
    }

    [Fact]
    public async Task Every_taken_attempt_fouled_derives_nm_with_no_value()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        await RecordAttemptAsync(seeded, entryId, 1, new { foul = true });
        var body = await Json(await RecordAttemptAsync(seeded, entryId, 2, new { foul = true }));
        Assert.Equal("Nm", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
        Assert.Equal(["X", "X"], Series(body));

        // One valid mark later and the same result is Ok again — nothing is typed.
        var recovered = await Json(await RecordAttemptAsync(seeded, entryId, 3, new { value = "3.90" }));
        Assert.Equal("Ok", recovered.GetProperty("status").GetString());
        Assert.Equal(3.90m, recovered.GetProperty("measuredValue").GetDecimal());
    }

    [Fact]
    public async Task An_attempt_on_a_single_measure_event_is_refused()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptsNotConfigured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task An_ordinal_the_definition_does_not_contest_is_refused(int ordinal)
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await RecordAttemptAsync(seeded, entryId, ordinal, new { value = "4.32" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptOrdinalOutOfRange);
    }

    [Fact]
    public async Task Re_recording_an_ordinal_is_a_correction_not_a_second_attempt()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });

        var response = await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.35" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptAlreadyRecorded);
    }

    [Fact]
    public async Task An_attempt_against_a_typed_non_result_status_is_refused()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "DNS" }))).StatusCode);

        var response = await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ResultStatusBlocksAttempts);
    }

    [Theory]
    // Exactly one of a mark and a foul: an attempt is one or the other.
    [InlineData("{\"value\":\"4.32\",\"foul\":true}")]
    [InlineData("{\"foul\":false}")]
    public async Task An_attempt_is_either_a_mark_or_a_foul(string body)
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(RawJson(HttpMethod.Put,
            $"{EventPath(seeded)}/entries/{entryId}/attempts/1", body, Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task An_attempts_mark_is_parsed_in_the_events_measure()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await RecordAttemptAsync(seeded, entryId, 1, new { value = "1:02.34" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ResultValueInvalid);
    }

    [Theory]
    [InlineData("{\"value\":\"4.32\"}")]
    [InlineData("{\"status\":\"NM\"}")]
    public async Task A_submit_on_an_attempts_event_is_refused(string body)
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(RawJson(HttpMethod.Post,
            $"{EventPath(seeded)}/entries/{entryId}/results", body, Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptsRequired);
    }

    [Fact]
    public async Task A_typed_non_result_status_still_submits_on_an_attempts_event()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "DNF" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Dnf", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("attempts").ValueKind);
    }

    [Fact]
    public async Task A_correction_carries_the_whole_series_and_a_reason()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var resultId = (await Json(
            await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" }))).GetProperty("id").GetGuid();

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Tape misread at the pit",
            attempts = new object?[] { new { value = "4.42" }, new { foul = true }, null },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(4.42m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal(["4.42", "X"], Series(body));

        var revisions = await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == resultId).OrderBy(r => r.RevisionNumber).ToListAsync());
        Assert.Equal("Tape misread at the pit", revisions[1].Reason);
        Assert.Contains("4.32", revisions[1].PriorValuesJson);
        Assert.Contains("4.42", revisions[1].NewValuesJson);
    }

    [Fact]
    public async Task A_correction_typing_a_bare_mark_on_an_attempts_event_is_refused()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var resultId = (await Json(
            await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" }))).GetProperty("id").GetGuid();

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, value = "4.42", reason = "Tape misread" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptsRequired);
    }

    [Fact]
    public async Task Correcting_to_dns_clears_the_series()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var resultId = (await Json(
            await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" }))).GetProperty("id").GetGuid();

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct",
            new { expectedRevision = 1, status = "DNS", reason = "Withdrew before the first round" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Dns", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("attempts").ValueKind);
        Assert.Null(await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Where(r => r.Id == resultId).Select(r => r.AttemptsJson).SingleAsync()));
    }

    [Fact]
    public async Task A_series_on_a_single_measure_event_is_refused()
    {
        var (_, _, resultId) = await SubmittedResultAsync();

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Not this kind of event",
            attempts = new object?[] { new { value = "4.32" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_tie_on_the_best_mark_breaks_on_the_next_best_attempt_at_the_deck()
    {
        var seeded = await AttemptsEventAsync();
        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        var second = await EnterAsync(seeded, seeded.SecondEligibleBoyId);

        await RecordAttemptAsync(seeded, first, 1, new { value = "4.32" });
        await RecordAttemptAsync(seeded, first, 2, new { value = "4.10" });
        await RecordAttemptAsync(seeded, second, 1, new { value = "4.32" });
        await RecordAttemptAsync(seeded, second, 2, new { value = "4.20" });

        var places = await PlacesByEntryAsync(seeded);
        Assert.Equal(1, places[second]);
        Assert.Equal(2, places[first]);

        // Equal at every position and they share the place, as two equal times do.
        var corrected = await _official.PostAsJsonAsync(
            $"/api/v1/results/{await ResultIdAsync(first)}/correct", new
            {
                expectedRevision = 2,
                reason = "Second round re-measured",
                attempts = new object?[] { new { value = "4.32" }, new { value = "4.20" } },
            });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var tied = await PlacesByEntryAsync(seeded);
        Assert.Equal(1, tied[first]);
        Assert.Equal(1, tied[second]);
    }

    [Fact]
    public async Task A_correction_shorter_than_the_stored_series_is_refused()
    {
        var seeded = await AttemptsEventAsync();
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await RecordAttemptAsync(seeded, entryId, 1, new { value = "4.32" });
        await RecordAttemptAsync(seeded, entryId, 3, new { value = "4.55" });
        var resultId = await ResultIdAsync(entryId);

        // Attempt 3 is recorded, so a two-element correction would delete it
        // without saying so — the series is restated whole or cleared outright.
        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 2,
            reason = "Re-measured the first round",
            attempts = new object?[] { new { value = "4.40" }, null },
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptsSeriesTruncated);

        // The whole series, the edited cell replaced, is accepted.
        var restated = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 2,
            reason = "Re-measured the first round",
            attempts = new object?[] { new { value = "4.40" }, null, new { value = "4.55" } },
        });
        Assert.Equal(HttpStatusCode.OK, restated.StatusCode);
        Assert.Equal(["4.40", "-", "4.55"], Series(await Json(restated)));
    }

    [Fact]
    public async Task A_field_division_split_across_flights_places_as_one_field()
    {
        var seeded = await AttemptsEventAsync();
        var first = await EnterAsync(seeded, seeded.EligibleBoyId);
        var second = await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        // Split for venue reasons, as the server still does for a field entry
        // (ADR-0039): the heat is data, and the deck says nothing about it.
        await CarnivalSeed.MutateAsync(_factory, db =>
        {
            db.Entries.Single(e => e.Id == first).Heat = 1;
            db.Entries.Single(e => e.Id == second).Heat = 2;
        });

        await RecordAttemptAsync(seeded, first, 1, new { value = "4.55" });
        await RecordAttemptAsync(seeded, second, 1, new { value = "4.10" });

        // ADR-0053 §6 amendment: one competition, one 1st — not a 1st per flight.
        var places = await PlacesByEntryAsync(seeded);
        Assert.Equal(1, places[first]);
        Assert.Equal(2, places[second]);
    }

    // ---- High jump: the heights ladder (ADR-0056 §2/§4) -------------------

    private static string JumpPath(SeededCarnival seeded, Guid entryId, decimal height) =>
        $"{EventPath(seeded)}/entries/{entryId}/jumps/" +
        height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private static HttpRequestMessage JumpRequest(
        SeededCarnival seeded, Guid entryId, decimal height, Guid key, object body) =>
        new(HttpMethod.Put, JumpPath(seeded, entryId, height))
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", key.ToString() } },
        };

    private Task<HttpResponseMessage> JumpAsync(
        SeededCarnival seeded, Guid entryId, decimal height, string? mark) =>
        _official.SendAsync(JumpRequest(seeded, entryId, height, Guid.CreateVersion7(), new { mark }));

    private Task<HttpResponseMessage> RaiseBarAsync(SeededCarnival seeded, decimal height) =>
        _official.PostAsJsonAsync($"{EventPath(seeded)}/heights", new { height });

    /// <summary>A Height event with its ladder suggestions set, and the bar already raised.</summary>
    private Task<SeededCarnival> LadderEventAsync(params decimal[] heights) =>
        LadderEventAsync(CompetitionCategory.M, heights);

    /// <summary>The same, in a division open to every category — a field of four.</summary>
    private async Task<SeededCarnival> LadderEventAsync(CompetitionCategory? category, decimal[] heights)
    {
        var seeded = await CarnivalSeed.SeedAsync(
            _factory, category: category, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == seeded.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        foreach (var height in heights)
            Assert.Equal(HttpStatusCode.OK, (await RaiseBarAsync(seeded, height)).StatusCode);
        return seeded;
    }

    /// <summary>The card as the wire carries it: "1.25 XO · 1.30 X".</summary>
    private static string CardOf(JsonElement body) => string.Join(" · ",
        body.GetProperty("jumps").EnumerateArray().Select(
            j => $"{j.GetProperty("height").GetDecimal():0.00} {j.GetProperty("marks").GetString()}"));

    [Fact]
    public async Task The_first_mark_creates_the_card_keyed_by_its_idempotency_key()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        var key = Guid.CreateVersion7();

        var response = await _official.SendAsync(
            JumpRequest(seeded, entryId, 1.25m, key, new { mark = "X" }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(key, body.GetProperty("id").GetGuid());
        // No clearance yet, so the card is provisionally NM with no value.
        Assert.Equal("Nm", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
        Assert.Equal("1.25 X", CardOf(body));
        // The two shapes never both carry a value (ADR-0056 §1).
        Assert.Equal(JsonValueKind.Null, body.GetProperty("attempts").ValueKind);

        var revision = Assert.Single(await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == key).ToListAsync()));
        Assert.Equal("Jump at 1.25 m", revision.Reason);
        Assert.NotNull(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.EntityId == key.ToString() && a.Action == AuditActions.ResultJump)));
    }

    [Fact]
    public async Task Later_marks_append_and_the_best_height_is_derived()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m, 1.30m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        await JumpAsync(seeded, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.OK, (await JumpAsync(seeded, entryId, 1.25m, "X")).StatusCode);
        var body = await Json(await JumpAsync(seeded, entryId, 1.25m, "O"));
        Assert.Equal(HttpStatusCode.OK, (await JumpAsync(seeded, entryId, 1.30m, "X")).StatusCode);

        Assert.Equal(1.25m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal("Ok", body.GetProperty("status").GetString());
        Assert.Equal("1.20 O · 1.25 XO", CardOf(body));

        var resultId = body.GetProperty("id").GetGuid();
        var reasons = await CarnivalSeed.QueryAsync(_factory, db => db.ResultRevisions
            .Where(r => r.ResultId == resultId)
            .OrderBy(r => r.RevisionNumber).Select(r => r.Reason).ToListAsync());
        Assert.Equal(["Jump at 1.20 m", "Jump at 1.25 m", "Jump at 1.25 m", "Jump at 1.30 m"], reasons);
    }

    [Fact]
    public async Task A_jump_replay_with_the_same_key_never_writes_twice()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        var key = Guid.CreateVersion7();

        var first = await _official.SendAsync(
            JumpRequest(seeded, entryId, 1.25m, key, new { mark = "X" }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("false", first.Headers.GetValues("Idempotency-Replayed").Single());

        var retry = await _official.SendAsync(
            JumpRequest(seeded, entryId, 1.25m, key, new { mark = "X" }));
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());

        var resultId = (await Json(first)).GetProperty("id").GetGuid();
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory,
            db => db.ResultRevisions.CountAsync(r => r.ResultId == resultId)));
    }

    [Fact]
    public async Task A_raise_the_heights_column_cannot_hold_is_refused_before_saving()
    {
        var seeded = await LadderEventAsync();
        // 0.01 to 3.99 in centimetre steps serialises to 1996 characters; one more height overflows.
        var full = CarnivalApp.Api.Carnivals.JumpCard.SerialiseHeights([.. Enumerable.Range(1, 399).Select(cm => cm / 100m)]);
        Assert.True(full.Length <= CarnivalApp.Api.Carnivals.JumpCard.MaxHeightsJsonLength);
        await CarnivalSeed.MutateAsync(_factory,
            db => db.CarnivalEvents.Single(e => e.Id == seeded.EventId).HeightsJson = full);

        var refused = await RaiseBarAsync(seeded, 4.00m);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.ValidationFailed);
        Assert.Equal(full, await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents.AsNoTracking()
            .Where(e => e.Id == seeded.EventId).Select(e => e.HeightsJson).SingleAsync()));
    }

    [Fact]
    public async Task A_mark_at_a_lower_height_with_attempts_outstanding_is_accepted()
    {
        // The bar is at 1.30 for the division; this athlete has one failure at
        // 1.25 and nothing above, so their outstanding attempts there are theirs.
        var seeded = await LadderEventAsync(1.20m, 1.25m, 1.30m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.25m, "X");

        var accepted = await JumpAsync(seeded, entryId, 1.25m, "O");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("1.25 XO", CardOf(await Json(accepted)));

        // …but once they have jumped at 1.30 the bar never comes back down.
        await JumpAsync(seeded, entryId, 1.30m, "X");
        var refused = await JumpAsync(seeded, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.BarNeverLowered);
    }

    [Fact]
    public async Task A_height_the_bar_has_not_reached_is_refused()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await JumpAsync(seeded, entryId, 1.25m, "O");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.HeightNotOnLadder);
    }

    [Fact]
    public async Task An_eliminated_athlete_takes_no_further_mark()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "X");
        await JumpAsync(seeded, entryId, 1.20m, "X");
        var third = await JumpAsync(seeded, entryId, 1.25m, "X");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var response = await JumpAsync(seeded, entryId, 1.25m, "O");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AthleteEliminated);
    }

    [Theory]
    [InlineData("O", ErrorCodes.HeightAlreadyCleared)]
    [InlineData("-", ErrorCodes.HeightPassed)]
    public async Task A_height_that_is_finished_takes_no_more_marks(string first, string expected)
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, first);

        var response = await JumpAsync(seeded, entryId, 1.20m, "X");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(expected);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_mark_outside_the_notation_is_a_400(string? mark)
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await JumpAsync(seeded, entryId, 1.20m, mark);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_height_that_is_not_a_whole_centimetre_is_a_400()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        var response = await _official.SendAsync(new HttpRequestMessage(
            HttpMethod.Put, $"{EventPath(seeded)}/entries/{entryId}/jumps/1.234")
        {
            Content = JsonContent.Create(new { mark = "O" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_typed_status_blocks_a_mark()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "DNS" }))).StatusCode);

        var blocked = await JumpAsync(seeded, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        await blocked.AssertErrorAsync(ErrorCodes.ResultStatusBlocksAttempts);
    }

    [Fact]
    public async Task A_height_division_with_no_ladder_suggestions_is_still_contested_on_the_bar()
    {
        // ADR-0056, amended 2026-09-17: the MEASURE is the mode. A Height event
        // created by hand, with "Bar starts at" left blank, takes the bar and the
        // card exactly as the templated one does.
        var bare = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        var entryId = await EnterAsync(bare, bare.EligibleBoyId);

        var raised = await Json(await RaiseBarAsync(bare, 1.20m));
        Assert.Equal([1.20m], raised.GetProperty("heights").EnumerateArray().Select(h => h.GetDecimal()));
        Assert.Equal(JsonValueKind.Null, raised.GetProperty("ladder").ValueKind);

        var marked = await JumpAsync(bare, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.Created, marked.StatusCode);
        var body = await Json(marked);
        Assert.Equal("1.20 O", CardOf(body));
        Assert.Equal(1.20m, body.GetProperty("measuredValue").GetDecimal());
    }

    [Fact]
    public async Task A_series_on_a_height_division_with_no_ladder_suggestions_is_refused()
    {
        var bare = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Height);
        var entryId = await EnterAsync(bare, bare.EligibleBoyId);

        var typed = await _official.SendAsync(
            SubmitRequest(bare, entryId, Guid.CreateVersion7(), new { value = "1.25" }));
        Assert.Equal(HttpStatusCode.Conflict, typed.StatusCode);
        await typed.AssertErrorAsync(ErrorCodes.LadderRequired);

        var attempt = await RecordAttemptAsync(bare, entryId, 1, new { value = "1.25" });
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        await attempt.AssertErrorAsync(ErrorCodes.LadderRequired);
    }

    [Fact]
    public async Task A_mark_and_a_raise_on_an_event_that_is_not_a_height_are_refused()
    {
        var distance = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var entryId = await EnterAsync(distance, distance.EligibleBoyId);

        var marked = await JumpAsync(distance, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.Conflict, marked.StatusCode);
        await marked.AssertErrorAsync(ErrorCodes.LadderRequired);

        var raised = await RaiseBarAsync(distance, 1.20m);
        Assert.Equal(HttpStatusCode.Conflict, raised.StatusCode);
        await raised.AssertErrorAsync(ErrorCodes.LadderRequired);

        // …and the series it IS contested on still lands.
        Assert.Equal(HttpStatusCode.Created,
            (await RecordAttemptAsync(distance, entryId, 1, new { value = "4.20" })).StatusCode);
    }

    [Fact]
    public async Task A_submit_or_an_attempt_on_a_height_event_draws_ladder_required()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        foreach (var body in new object[] { new { value = "1.25" }, new { status = "NM" } })
        {
            var refused = await _official.SendAsync(
                SubmitRequest(seeded, entryId, Guid.CreateVersion7(), body));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            await refused.AssertErrorAsync(ErrorCodes.LadderRequired);
        }

        var attempt = await RecordAttemptAsync(seeded, entryId, 1, new { value = "1.25" });
        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        await attempt.AssertErrorAsync(ErrorCodes.LadderRequired);

        // A DNS is not a jump and still submits, clearing nothing that exists.
        Assert.Equal(HttpStatusCode.Created, (await _official.SendAsync(
            SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { status = "DNS" }))).StatusCode);
    }

    // ---- Raising the bar (ADR-0056 §2) ------------------------------------

    [Fact]
    public async Task Raising_the_bar_appends_a_height_and_is_audited()
    {
        var seeded = await LadderEventAsync();
        var body = await Json(await RaiseBarAsync(seeded, 1.20m));
        Assert.Equal([1.20m], body.GetProperty("heights").EnumerateArray().Select(h => h.GetDecimal()));
        Assert.Equal(1.20m, body.GetProperty("ladder").GetProperty("start").GetDecimal());
        Assert.Equal(0.05m, body.GetProperty("ladder").GetProperty("step").GetDecimal());

        var raised = await Json(await RaiseBarAsync(seeded, 1.25m));
        Assert.Equal([1.20m, 1.25m],
            raised.GetProperty("heights").EnumerateArray().Select(h => h.GetDecimal()));
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.CountAsync(
            a => a.EntityId == seeded.EventId.ToString()
                 && a.Action == AuditActions.CarnivalEventRaiseBar)));
    }

    [Fact]
    public async Task The_bar_is_never_lowered()
    {
        var seeded = await LadderEventAsync(1.30m);
        foreach (var height in new[] { 1.30m, 1.25m })
        {
            var refused = await RaiseBarAsync(seeded, height);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            await refused.AssertErrorAsync(ErrorCodes.BarNeverLowered);
        }
    }

    [Fact]
    public async Task The_opening_height_is_set_before_the_division_opens_but_not_off_an_open_carnival()
    {
        // The EVENT's status is deliberately not checked: the bar is set at the
        // mat before the division opens.
        var scheduled = await CarnivalSeed.SeedAsync(
            _factory, eventStatus: CarnivalEventStatus.Scheduled, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == scheduled.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        Assert.Equal(HttpStatusCode.OK, (await RaiseBarAsync(scheduled, 1.20m)).StatusCode);

        var closed = await CarnivalSeed.SeedAsync(
            _factory, carnivalStatus: CarnivalStatus.Closed, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == closed.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");
        var refused = await RaiseBarAsync(closed, 1.20m);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.CarnivalNotOpen);
    }

    [Fact]
    public async Task The_bar_is_not_raised_on_a_division_that_has_closed()
    {
        var closed = await CarnivalSeed.SeedAsync(
            _factory, eventStatus: CarnivalEventStatus.Closed, measureType: MeasureType.Height);
        await CarnivalSeed.MutateAsync(_factory, db => db.EventDefinitions
            .Single(d => d.Id == closed.DefinitionId).LadderJson = """{"start":1.20,"step":0.05}""");

        var refused = await RaiseBarAsync(closed, 1.20m);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.EventNotOpen);
    }

    // ---- Attempts per height (ADR-0056 amendment 2026-09-25) ---------------

    private Task<HttpResponseMessage> SetAttemptsPerHeightAsync(SeededCarnival seeded, int value) =>
        _official.PutAsJsonAsync($"{EventPath(seeded)}/attempts-per-height",
            new { attemptsPerHeight = value });

    [Fact]
    public async Task A_division_switches_to_two_before_its_first_mark_audited_and_versioned()
    {
        var seeded = await LadderEventAsync(1.20m);
        var before = await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.AsNoTracking().SingleAsync(e => e.Id == seeded.EventId));

        var response = await SetAttemptsPerHeightAsync(seeded, 2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("attemptsPerHeight").GetInt32());
        Assert.Equal(before.Version + 1, body.GetProperty("version").GetInt32());
        Assert.NotNull(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.SingleAsync(
            a => a.EntityId == seeded.EventId.ToString()
                 && a.Action == AuditActions.CarnivalEventAttemptsPerHeight)));

        // The same value again is nothing to do, and nothing to audit.
        var again = await SetAttemptsPerHeightAsync(seeded, 2);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(before.Version + 1, (await Json(again)).GetProperty("version").GetInt32());
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.CountAsync(
            a => a.EntityId == seeded.EventId.ToString()
                 && a.Action == AuditActions.CarnivalEventAttemptsPerHeight)));
    }

    [Fact]
    public async Task Attempts_per_height_is_fixed_once_a_mark_lands()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "X");

        var response = await SetAttemptsPerHeightAsync(seeded, 2);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AttemptsPerHeightFrozen);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public async Task Attempts_per_height_is_2_or_3(int value)
    {
        var seeded = await LadderEventAsync(1.20m);
        var response = await SetAttemptsPerHeightAsync(seeded, value);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Attempts_per_height_on_an_event_that_is_not_a_height_is_refused()
    {
        var distance = await CarnivalSeed.SeedAsync(
            _factory, measureType: MeasureType.Distance, attemptCount: 3);
        var response = await SetAttemptsPerHeightAsync(distance, 2);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.LadderRequired);
    }

    [Fact]
    public async Task At_two_attempts_both_failed_at_one_height_is_out_and_a_third_is_refused()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        Assert.Equal(HttpStatusCode.OK, (await SetAttemptsPerHeightAsync(seeded, 2)).StatusCode);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);

        // A fail, a pass, a fail at the next bar: nothing carries at two.
        await JumpAsync(seeded, entryId, 1.20m, "X");
        await JumpAsync(seeded, entryId, 1.20m, "-");
        Assert.Equal(HttpStatusCode.OK, (await JumpAsync(seeded, entryId, 1.25m, "X")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await JumpAsync(seeded, entryId, 1.25m, "X")).StatusCode);

        var refused = await JumpAsync(seeded, entryId, 1.25m, "O");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.AthleteEliminated);
    }

    [Fact]
    public async Task At_two_attempts_a_correction_is_judged_against_two()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        Assert.Equal(HttpStatusCode.OK, (await SetAttemptsPerHeightAsync(seeded, 2)).StatusCode);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "X");
        var resultId = await ResultIdAsync(entryId);

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Re-read the card",
            jumps = new[] { new { height = 1.20m, marks = "XXO" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    // ---- Correcting a card (ADR-0056 §4) ----------------------------------

    [Fact]
    public async Task A_correction_replays_the_whole_card_and_re_derives_the_best_height()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m, 1.30m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        await JumpAsync(seeded, entryId, 1.25m, "X");
        var resultId = await ResultIdAsync(entryId);

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 2,
            reason = "The bar was read wrong at 1.25",
            jumps = new[]
            {
                new { height = 1.20m, marks = "O" },
                new { height = 1.25m, marks = "XO" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(1.25m, body.GetProperty("measuredValue").GetDecimal());
        Assert.Equal("1.20 O · 1.25 XO", CardOf(body));
        Assert.Equal(3, body.GetProperty("currentRevision").GetInt32());
    }

    [Theory]
    // Grammar: a clearance ends the height, so nothing may follow it.
    [InlineData(1.25, "OX", (int)HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed)]
    // Replayed through the capture rules: the bar never came down.
    [InlineData(1.20, "O", (int)HttpStatusCode.Conflict, ErrorCodes.BarNeverLowered)]
    // …and a height the division never contested is not on the ladder.
    [InlineData(1.40, "O", (int)HttpStatusCode.Conflict, ErrorCodes.HeightNotOnLadder)]
    public async Task A_card_that_could_not_have_been_captured_cannot_be_corrected_into_existence(
        double second, string marks, int status, string expected)
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m, 1.30m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.25m, "X");
        var resultId = await ResultIdAsync(entryId);

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Re-read the card",
            jumps = new[]
            {
                new { height = 1.25m, marks = "XXO" },
                new { height = (decimal)second, marks },
            },
        });
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await response.AssertErrorAsync(expected);
    }

    [Fact]
    public async Task A_correction_replay_refuses_a_card_that_kept_jumping_after_elimination()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "X");
        var resultId = await ResultIdAsync(entryId);

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Re-read the card",
            jumps = new[]
            {
                new { height = 1.20m, marks = "XX-" },
                new { height = 1.25m, marks = "XO" },
            },
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AthleteEliminated);
    }

    [Fact]
    public async Task A_typed_status_clears_the_card_but_cannot_carry_one_even_empty()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        var resultId = await ResultIdAsync(entryId);

        var empty = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1, reason = "Nothing to record", status = "DNS",
            jumps = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        await empty.AssertErrorAsync(ErrorCodes.ValidationFailed);

        var disqualified = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1, reason = "Disqualified at the mat", status = "DQ",
        });
        Assert.Equal(HttpStatusCode.OK, disqualified.StatusCode);
        var body = await Json(disqualified);
        Assert.Equal("Dq", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("jumps").ValueKind);
        Assert.Null(await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Where(r => r.Id == resultId).Select(r => r.AttemptsJson).SingleAsync()));
    }

    // ---- The last mark comes off (ADR-0056 amendment 2026-09-25 (c)) --------

    private async Task<HttpResponseMessage> ClearCardAsync(Guid resultId, int expectedRevision) =>
        await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision, reason = "Attempt 1 at 1.20 m removed", jumps = Array.Empty<object>(),
        });

    [Fact]
    public async Task An_empty_card_is_a_correction_that_keeps_the_result_and_its_history()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        var resultId = await ResultIdAsync(entryId);

        var cleared = await ClearCardAsync(resultId, 1);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var body = await Json(cleared);
        Assert.Equal(resultId, body.GetProperty("id").GetGuid());
        Assert.Equal("Nm", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredValue").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("jumps").ValueKind);
        Assert.Equal(2, body.GetProperty("currentRevision").GetInt32());

        Assert.Null(await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Where(r => r.Id == resultId).Select(r => r.AttemptsJson).SingleAsync()));
        Assert.Equal(2, await CarnivalSeed.QueryAsync(_factory,
            db => db.ResultRevisions.CountAsync(r => r.ResultId == resultId)));
        Assert.NotNull(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.SingleAsync(
            a => a.EntityId == resultId.ToString() && a.Action == AuditActions.ResultCorrect)));
    }

    [Fact]
    public async Task The_next_mark_after_a_cleared_card_starts_a_fresh_one()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.25m, "O");
        var resultId = await ResultIdAsync(entryId);
        Assert.Equal(HttpStatusCode.OK, (await ClearCardAsync(resultId, 1)).StatusCode);

        // Below the height the old card reached: nothing on the card holds the bar up.
        var marked = await JumpAsync(seeded, entryId, 1.20m, "O");
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var body = await Json(marked);
        Assert.Equal(resultId, body.GetProperty("id").GetGuid());
        Assert.Equal("1.20 O", CardOf(body));
        Assert.Equal("Ok", body.GetProperty("status").GetString());
        Assert.Equal(1.20m, body.GetProperty("measuredValue").GetDecimal());
    }

    [Fact]
    public async Task A_cleared_card_neither_places_nor_counts_as_recorded()
    {
        var seeded = await LadderEventAsync(null, [1.20m]);
        var cleared = await EnterAsync(seeded, seeded.EligibleBoyId);
        var jumped = await EnterAsync(seeded, seeded.GirlId);
        await JumpAsync(seeded, cleared, 1.20m, "O");
        await JumpAsync(seeded, jumped, 1.20m, "X");
        await JumpAsync(seeded, jumped, 1.20m, "O");
        Assert.Equal(HttpStatusCode.OK,
            (await ClearCardAsync(await ResultIdAsync(cleared), 1)).StatusCode);

        var places = await PlacesByEntryAsync(seeded);
        Assert.Null(places[cleared]);
        Assert.Equal(1, places[jumped]);

        var division = await Json(await _official.GetAsync(EventPath(seeded)));
        Assert.Equal(1, division.GetProperty("resultCount").GetInt32());
        Assert.False(division.GetProperty("complete").GetBoolean());
    }

    private async Task<int> HousePointsAsync(SeededCarnival seeded) =>
        (await Json(await _admin.GetAsync($"/api/v1/carnivals/{seeded.CarnivalId}/standings")))
        .GetProperty("houses").EnumerateArray().Sum(h => h.GetProperty("total").GetInt32());

    [Fact]
    public async Task A_cleared_card_scores_as_an_entry_with_no_result()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        Assert.Equal(0, await HousePointsAsync(seeded));

        // Still jumping with no clearance is NM, which earns DNF participation.
        await JumpAsync(seeded, entryId, 1.20m, "X");
        Assert.True(await HousePointsAsync(seeded) > 0);

        Assert.Equal(HttpStatusCode.OK,
            (await ClearCardAsync(await ResultIdAsync(entryId), 1)).StatusCode);
        Assert.Equal(0, await HousePointsAsync(seeded));
    }

    [Fact]
    public async Task A_cleared_card_is_left_out_of_the_book_and_its_csv_as_no_result_is()
    {
        var seeded = await LadderEventAsync(null, [1.20m]);
        var cleared = await EnterAsync(seeded, seeded.EligibleBoyId);
        var jumped = await EnterAsync(seeded, seeded.GirlId);
        await JumpAsync(seeded, cleared, 1.20m, "X");
        await JumpAsync(seeded, jumped, 1.20m, "O");
        await EnterAsync(seeded, seeded.SecondEligibleBoyId);
        Assert.Equal(HttpStatusCode.OK,
            (await ClearCardAsync(await ResultIdAsync(cleared), 1)).StatusCode);

        var book = await Json(await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results"));
        var row = Assert.Single(book.GetProperty("rows").EnumerateArray());
        Assert.Equal(1, row.GetProperty("place").GetInt32());

        var lines = await (await _admin.GetAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results.csv")).CsvLinesAsync();
        Assert.Equal(2, lines.Length); // the header and the one recorded jump
    }

    [Fact]
    public async Task A_corrected_card_that_names_a_height_twice_is_refused_before_the_replay()
    {
        var seeded = await LadderEventAsync(1.20m, 1.25m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.25m, "X");
        var resultId = await ResultIdAsync(entryId);

        // The replay is cumulative: 1.25 twice would land as "XXO", a card
        // nobody sent and one the second element contradicts.
        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1,
            reason = "Re-read the card",
            jumps = new[]
            {
                new { height = 1.25m, marks = "X" },
                new { height = 1.25m, marks = "XO" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
        Assert.Equal("""[{"height":1.25,"marks":"X"}]""", await CarnivalSeed.QueryAsync(_factory,
            db => db.Results.Where(r => r.Id == resultId).Select(r => r.AttemptsJson).SingleAsync()));
    }

    [Fact]
    public async Task A_status_correction_on_a_height_result_carries_neither_a_card_nor_a_series()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        var resultId = await ResultIdAsync(entryId);

        object[] bodies =
        [
            new
            {
                expectedRevision = 1, reason = "Did not start", status = "DNS",
                jumps = new[] { new { height = 1.20m, marks = "O" } },
            },
            new
            {
                expectedRevision = 1, reason = "Did not start", status = "DNS",
                attempts = new object?[] { new { value = "1.20" } },
            },
        ];
        foreach (var body in bodies)
        {
            var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task A_correction_typing_a_height_or_an_ordinal_series_draws_ladder_required()
    {
        var seeded = await LadderEventAsync(1.20m);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await JumpAsync(seeded, entryId, 1.20m, "O");
        var resultId = await ResultIdAsync(entryId);

        object[] bodies =
        [
            new { expectedRevision = 1, reason = "Typed the height", value = "1.25" },
            new
            {
                expectedRevision = 1, reason = "Typed a series",
                attempts = new object?[] { new { value = "1.25" } },
            },
            new { expectedRevision = 1, reason = "Nothing said at all" },
        ];
        foreach (var body in bodies)
        {
            var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", body);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await response.AssertErrorAsync(ErrorCodes.LadderRequired);
        }
    }

    [Fact]
    public async Task A_card_on_an_event_that_is_not_a_height_is_refused()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory, measureType: MeasureType.Distance);
        var entryId = await EnterAsync(seeded, seeded.EligibleBoyId);
        await _official.SendAsync(SubmitRequest(seeded, entryId, Guid.CreateVersion7(), new { value = "4.25" }));
        var resultId = await ResultIdAsync(entryId);

        var response = await _official.PostAsJsonAsync($"/api/v1/results/{resultId}/correct", new
        {
            expectedRevision = 1, reason = "Sent a card",
            jumps = new[] { new { height = 1.25m, marks = "O" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    // ---- Countback in the field (ADR-0056 §3) -----------------------------

    [Fact]
    public async Task One_best_height_four_athletes_and_the_countback_separates_them()
    {
        // The normal case in high jump: everybody clears the same bar, and the
        // rules — not the platform — decide who won.
        var seeded = await LadderEventAsync(null, [1.20m, 1.25m]);
        var clean = await EnterAsync(seeded, seeded.EligibleBoyId);
        var oneFail = await EnterAsync(seeded, seeded.GirlId);
        var alsoOneFail = await EnterAsync(seeded, seeded.SCategoryStudentId);
        var twoFails = await EnterAsync(seeded, seeded.SecondEligibleBoyId);

        await JumpAsync(seeded, clean, 1.25m, "O");
        foreach (var entry in new[] { oneFail, alsoOneFail })
        {
            await JumpAsync(seeded, entry, 1.25m, "X");
            await JumpAsync(seeded, entry, 1.25m, "O");
        }
        // The same failure at the best height, but one more on the way there.
        await JumpAsync(seeded, twoFails, 1.20m, "X");
        await JumpAsync(seeded, twoFails, 1.25m, "X");
        await JumpAsync(seeded, twoFails, 1.25m, "O");

        var places = await PlacesByEntryAsync(seeded);
        Assert.Equal(1, places[clean]);
        // Equal on all three numbers ties and shares the place, as always.
        Assert.Equal(2, places[oneFail]);
        Assert.Equal(2, places[alsoOneFail]);
        Assert.Equal(4, places[twoFails]);
        // Every one of them cleared 1.25: on the mark alone this was a four-way tie.
        Assert.All(await ResultsAsync(seeded),
            r => Assert.Equal(1.25m, r.GetProperty("measuredValue").GetDecimal()));
    }

    private Task<Guid> ResultIdAsync(Guid entryId) => CarnivalSeed.QueryAsync(_factory,

        db => db.Results.Where(r => r.EntryId == entryId).Select(r => r.Id).SingleAsync());

    private async Task<List<JsonElement>> ResultsAsync(SeededCarnival seeded)
    {
        var response = await _official.GetAsync($"{EventPath(seeded)}/results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await Json(response)).EnumerateArray()];
    }

    private async Task<Dictionary<Guid, int?>> PlacesByEntryAsync(SeededCarnival seeded)
    {
        var response = await _official.GetAsync($"{EventPath(seeded)}/results");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).EnumerateArray().ToDictionary(
            r => r.GetProperty("entryId").GetGuid(),
            r => r.GetProperty("place").ValueKind == JsonValueKind.Null
                ? (int?)null
                : r.GetProperty("place").GetInt32());
    }
}
