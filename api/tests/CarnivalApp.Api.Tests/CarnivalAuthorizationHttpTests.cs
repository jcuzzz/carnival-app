using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Server-side authorisation for the carnival/entry/result surface (ADR-0016,
/// amending proposal §8): 401 unauthenticated, 403 for a role outside the
/// endpoint's policy, and pass-through for the intended role.
/// </summary>
/// <remarks>
/// Role membership is now the ONLY gate here — ADR-0016 §3 retired the
/// data-dependent event-assignment check, so nothing outside this matrix can
/// refuse a correctly-roled caller.
/// </remarks>
public sealed class CarnivalAuthorizationHttpTests : IClassFixture<ApiTestFactory>
{
    private static readonly Guid SomeId = Guid.NewGuid();
    private readonly ApiTestFactory _factory;

    public CarnivalAuthorizationHttpTests(ApiTestFactory factory) => _factory = factory;

    // method, path, role that passes the policy, role that must be refused
    //
    // ADR-0016: four roles. The REFUSED column is the load-bearing one — it is
    // where a collapse can silently widen access — so every row's refused role is
    // now `Recorder` or `Viewer`, the only two that can be refused anything on
    // this surface. Rows that previously refused `Verifier` INVERTED rather than
    // renamed (Verifier collapsed INTO the permitted role) and are re-pointed at
    // `Recorder`; rows that refused `ResultsOfficial` on the marshalling
    // endpoints inverted the other way and are now `passing` rows.
    public static TheoryData<string, string, string, string> Endpoints => new()
    {
        // Carnival administration — a Recorder is refused.
        { "POST", "/api/v1/carnivals", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/carnivals/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "POST", $"/api/v1/carnivals/{SomeId}/status", "CarnivalAdministrator", "Recorder" },
        { "POST", $"/api/v1/carnivals/{SomeId}/apply-template", "CarnivalAdministrator", "Recorder" },
        // ADR-0054 §2 widened the template LIST to this policy so the picker in
        // front of apply-template can be filled by the role that applies one.
        // The reconcile pair behind it stays SystemAdministrator-only
        // (EndpointAuthorizationHttpTests holds that half of the split).
        { "GET", "/api/v1/admin/event-templates", "CarnivalAdministrator", "Recorder" },
        { "GET", "/api/v1/admin/event-templates", "SystemAdministrator", "Viewer" },
        { "POST", "/api/v1/event-definitions", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/event-definitions/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "DELETE", $"/api/v1/event-definitions/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}", "CarnivalAdministrator", "Recorder" },
        // ADR-0022: generating and unscheduling divisions are setup actions, on
        // the administer side of the ADR-0016 §1 line that the marshalling rows
        // below sit on the other side of.
        { "POST", $"/api/v1/carnivals/{SomeId}/events/generate", "CarnivalAdministrator", "Recorder" },
        { "DELETE", $"/api/v1/carnivals/{SomeId}/events/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/status", "CarnivalAdministrator", "Recorder" },
        // ADR-0054 §4: opening every Scheduled division at once is the same
        // right as opening one, so it sits beside the row above.
        { "POST", $"/api/v1/carnivals/{SomeId}/events/open-scheduled", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/officials", "CarnivalAdministrator", "Recorder" },
        // MARSHALLING (ADR-0016 §1) — these two INVERTED: a Recorder creates and
        // removes entries on the deck now, so the old "ResultsOfficial refused"
        // rows became passing rows. A Viewer is the refusal that remains.
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries", "Recorder", "Viewer" },
        { "DELETE", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}", "Recorder", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/team-entries", "Recorder", "Viewer" },
        // …but NOT the category override: it is the ADR-0003 eligibility control,
        // not a marshalling action, so it stays administrator-only and a Recorder
        // is refused. This is the one cell where the marshalling split is visible.
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/category-override", "CarnivalAdministrator", "Recorder" },
        // RACES (ADR-0015 phase 2): the marshalling split, pinned on BOTH
        // halves. Race CRUD is carnivals:administer — a Recorder is refused —
        // while placing an entry into a race/lane/heat is carnivals:marshal, so
        // a Recorder passes and a Viewer is the refusal that remains. Creating
        // the grouping and placing a student into it are different rights
        // (ADR-0016 §1: the Recorder gets the assignment, not the race).
        { "POST", $"/api/v1/carnivals/{SomeId}/races", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/races/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "DELETE", $"/api/v1/carnivals/{SomeId}/races/{SomeId}", "CarnivalAdministrator", "Recorder" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/placement", "Recorder", "Viewer" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/placement", "CarnivalAdministrator", "Viewer" },
        // MATERIALISE (ADR-0018 §8, ADR-0019) falls on the MARSHAL side of that
        // same line even though it creates a Race row: the operation is
        // placement-shaped and the row is a byproduct. The three race-CRUD rows
        // above are the control — they still refuse a Recorder, so the split has
        // not collapsed.
        { "POST", $"/api/v1/carnivals/{SomeId}/races/materialise", "Recorder", "Viewer" },
        // REPORTS AND EXPORTS (ADR-0004 tier 1) sit on the administer side: the
        // live scoreboard is a read every role may watch, but taking a file of
        // student names off the platform is a staff desk act, so a Recorder is
        // refused all three.
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/results", "CarnivalAdministrator", "Recorder" },
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/standings.csv", "CarnivalAdministrator", "Recorder" },
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/results.csv", "CarnivalAdministrator", "Recorder" },
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/results.csv", "SystemAdministrator", "Viewer" },
        // ADR-0028 §4: the district list rides the same group and the same gate.
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/district.csv", "CarnivalAdministrator", "Recorder" },
        { "GET", $"/api/v1/carnivals/{SomeId}/reports/district.csv", "SystemAdministrator", "Viewer" },
        // DEVICES (ADR-0034), and the split is the interesting cell: REPORTING a
        // queue depth is carnivals:view — any role that can hold a device can
        // hold a queue, and the report carries no student data — while READING
        // the fleet names every official holding one, so it is the organiser's
        // administer gate and a Recorder is refused.
        { "PUT", $"/api/v1/carnivals/{SomeId}/devices/device-a/sync-status", "Viewer", "" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/devices/device-a/sync-status", "Recorder", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/devices", "CarnivalAdministrator", "Recorder" },
        { "GET", $"/api/v1/carnivals/{SomeId}/devices", "SystemAdministrator", "Viewer" },
        // …but the RECORD BOOK is a read every role may take (ADR-0028 §5): a
        // record book is a thing schools put on a wall, so it is carnivals:view
        // and not the administer gate its two artefact siblings sit behind.
        { "GET", "/api/v1/records", "Viewer", "" },
        // …but record CUSTODY is not (ADR-0035). Reading the book is a wall
        // poster; voiding a record takes something OUT of a published book, and
        // the custody read carries who voided what, so both sit on administer.
        { "GET", "/api/v1/records/administration", "CarnivalAdministrator", "Recorder" },
        { "GET", "/api/v1/records/administration", "SystemAdministrator", "Viewer" },
        { "POST", $"/api/v1/records/{SomeId}/void", "CarnivalAdministrator", "Recorder" },
        { "POST", $"/api/v1/records/{SomeId}/void", "SystemAdministrator", "Viewer" },
        // Read surface — every role including Viewer; unauthenticated still 401.
        { "GET", "/api/v1/carnivals", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/races", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}", "Viewer", "" },
        { "GET", "/api/v1/event-definitions", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/events", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/events/{SomeId}", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries", "Viewer", "" },
        { "GET", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/results", "Viewer", "" },
        // …but NOT the whole-roster eligible search. ADR-0032 §3 moved it to the
        // marshalling policy: it lists every student in the school who could be
        // entered, and a Viewer watching the scoreboard has no entry to create.
        { "GET", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/eligible-students", "Recorder", "Viewer" },
        { "GET", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/eligible-students", "CarnivalAdministrator", "Viewer" },
        // Result capture — Recorder or CarnivalAdministrator (ADR-0009 retained);
        // a Viewer is refused. The old "Verifier alone is refused" row inverted.
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/results", "Recorder", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/results", "CarnivalAdministrator", "Viewer" },
        // The Verifier surface is GONE. ADR-0017 deleted /verify and
        // /return-to-provisional (validation is automatic) and retired per-event
        // /finalise (completion is derived). All three rows go with their routes,
        // not re-pointed at another role — there is no action left to guard.
        // Carnival finalisation, the one human act that survives, is an ordinary
        // carnival status change and is covered by the /status row above.
        // ADR-0056 §2: a mark on the card and raising the bar are the same right
        // as recording a result — the people at the mat do all three.
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/jumps/1.20", "Recorder", "Viewer" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/jumps/1.20", "CarnivalAdministrator", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/heights", "Recorder", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/heights", "SystemAdministrator", "Viewer" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/attempts-per-height", "Recorder", "Viewer" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/attempts-per-height", "SystemAdministrator", "Viewer" },
        // Corrections: Recorder or CarnivalAdministrator; a Viewer is refused.
        { "POST", $"/api/v1/results/{SomeId}/correct", "Recorder", "Viewer" },
        { "POST", $"/api/v1/results/{SomeId}/correct", "CarnivalAdministrator", "Viewer" },
        // SystemAdministrator holds the FULL carnival surface (owner, 2026-08-11)
        // so a school with one technical person is not locked out of its own
        // carnival. One passing row per policy: without these, dropping the role
        // from a PolicyRoles list fails nothing.
        { "POST", "/api/v1/carnivals", "SystemAdministrator", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/races", "SystemAdministrator", "Viewer" },
        { "PUT", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/placement", "SystemAdministrator", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries", "SystemAdministrator", "Viewer" },
        { "POST", $"/api/v1/carnivals/{SomeId}/events/{SomeId}/entries/{SomeId}/results", "SystemAdministrator", "Viewer" },
        { "POST", $"/api/v1/results/{SomeId}/correct", "SystemAdministrator", "Viewer" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null,
        };
        // The sync-status route refuses a device that is not the caller (ADR-0034
        // §2) BEFORE the handler does anything else, and that 403 is not a policy
        // one — so the matrix row carries the header a real client sends.
        if (SyncStatusDeviceId(path) is { } deviceId) request.Headers.Add("X-Device-Id", deviceId);
        return request;
    }

    private static string? SyncStatusDeviceId(string path)
    {
        var match = Regex.Match(path, "^/api/v1/carnivals/[^/]+/devices/([^/]+)/sync-status$");
        return match.Success ? match.Groups[1].Value : null;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Unauthenticated_requests_get_401(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(passingRole);
        Assert.NotNull(refusedRole);
        var response = await _factory.CreateClient().SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Role_outside_the_policy_gets_403(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(passingRole);
        if (refusedRole.Length == 0) return; // read surface is open to all roles
        var client = _factory.CreateClientAs("wrong-role-user", refusedRole);
        var response = await client.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Intended_role_passes_the_policy(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(refusedRole);
        var client = _factory.CreateClientAs("right-role-user", passingRole);
        var response = await client.SendAsync(Request(method, path));
        // Past authorisation the handler decides (404 unknown ids, 400 bad body)
        // — but never 401, and after ADR-0016 §3 retired the per-event assignment
        // gate, never 403 either: the policy is now the ONLY thing that can
        // refuse the role, so a 403 here means a policy is wrong.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
