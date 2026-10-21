using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// One seeded world for the whole scoring authorisation matrix: a real carnival
/// (so a permitted role gets a genuine 2xx rather than a 404) and one existing
/// house-point adjustment for the void row.
/// </summary>
public sealed class ScoringAuthorizationFixture : IAsyncLifetime
{
    public ApiTestFactory Factory { get; } = new();
    public SeededCarnival Seed { get; private set; } = null!;
    public Guid AdjustmentId { get; private set; }

    public async Task InitializeAsync()
    {
        Seed = await CarnivalSeed.SeedAsync(Factory);
        var admin = Factory.CreateClientAs("scoring-authz-seed", "CarnivalAdministrator");
        var created = await admin.PostAsJsonAsync($"/api/v1/carnivals/{Seed.CarnivalId}/adjustments",
            new { houseCode = "WATTLE", points = 5, category = "Best Dressed House", reason = "matrix seed" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        AdjustmentId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Server-side authorisation for the scoring slice (proposal §8, ADR-0005/0007):
/// standings, per-carnival settings, house-point adjustments and void, plus the
/// two small authenticated platform reads (<c>/school/houses</c>, <c>/version</c>)
/// that no other matrix reaches. Three rows per endpoint: 401 unauthenticated,
/// 403 for a role outside the policy, 2xx for a permitted role.
///
/// Unlike <see cref="CarnivalAuthorizationHttpTests"/> (which probes made-up ids
/// and can only assert "not 401"), these run against a seeded carnival with
/// valid bodies, so the permitted row asserts the request actually succeeded.
/// </summary>
public sealed class ScoringAuthorizationHttpTests(ScoringAuthorizationFixture fixture)
    : IClassFixture<ScoringAuthorizationFixture>
{
    // method, path template, role that passes the policy, role that must be
    // refused ("" = no role is refused — read surface open to every platform role)
    public static TheoryData<string, string, string, string> Endpoints => new()
    {
        // Reads carry ViewCarnivalsPolicy: any platform role may watch the scoreboard.
        { "GET", "/api/v1/carnivals/{carnival}/standings", "Viewer", "" },
        { "GET", "/api/v1/carnivals/{carnival}/progress", "Viewer", "" },
        { "GET", "/api/v1/carnivals/{carnival}/settings", "Viewer", "" },
        { "GET", "/api/v1/carnivals/{carnival}/adjustments", "Viewer", "" },
        // Scoring configuration and house points are administrator-only
        // (ADR-0005/0007): a Recorder may not award their own house points.
        // The rows that refused `Verifier` INVERTED under ADR-0016 — Verifier
        // collapsed INTO CarnivalAdministrator, so refusing it would now be
        // refusing the permitted role. They are re-pointed at `Recorder`, which
        // is what the rule was always about.
        { "PUT", "/api/v1/carnivals/{carnival}/settings", "CarnivalAdministrator", "Recorder" },
        { "POST", "/api/v1/carnivals/{carnival}/adjustments", "CarnivalAdministrator", "Recorder" },
        { "POST", "/api/v1/carnivals/{carnival}/adjustments", "CarnivalAdministrator", "Viewer" },
        { "POST", "/api/v1/carnivals/{carnival}/adjustments/{adjustment}/void", "CarnivalAdministrator", "Recorder" },
        { "POST", "/api/v1/carnivals/{carnival}/adjustments/{adjustment}/void", "CarnivalAdministrator", "Viewer" },
        // Authenticated platform reads — no role gate, but never anonymous (H1).
        { "GET", "/api/v1/school/houses", "Viewer", "" },
        { "GET", "/api/v1/version", "Viewer", "" },
    };

    /// <summary>
    /// Reads guarded by ViewCarnivalsPolicy. Every <c>PlatformRole</c> is in that
    /// policy, so no *role* row can be refused — the refusal case is a principal
    /// holding no platform role at all (below). <c>/school/houses</c> and
    /// <c>/version</c> are deliberately absent: they carry a bare
    /// <c>RequireAuthorization()</c> and no policy.
    /// </summary>
    public static TheoryData<string> RoleGatedReads => new()
    {
        "/api/v1/carnivals",
        "/api/v1/carnivals/{carnival}",
        "/api/v1/carnivals/{carnival}/events",
        "/api/v1/carnivals/{carnival}/standings",
        "/api/v1/carnivals/{carnival}/progress",
        "/api/v1/carnivals/{carnival}/settings",
        "/api/v1/carnivals/{carnival}/adjustments",
        "/api/v1/event-definitions",
    };

    /// <summary>
    /// The carnival read surface × every role. All four hold ViewCarnivalsPolicy
    /// and must see every read; a role missing from that policy is invisible until
    /// somebody signs in and finds an empty app.
    /// </summary>
    public static TheoryData<string, string> ReadSurfaceRoles()
    {
        string[] paths =
        [
            "/api/v1/carnivals",
            "/api/v1/carnivals/{carnival}",
            "/api/v1/carnivals/{carnival}/events",
            "/api/v1/carnivals/{carnival}/events/{event}",
            "/api/v1/carnivals/{carnival}/events/{event}/entries",
            "/api/v1/carnivals/{carnival}/events/{event}/results",
            "/api/v1/carnivals/{carnival}/standings",
            "/api/v1/carnivals/{carnival}/progress",
            "/api/v1/carnivals/{carnival}/settings",
            "/api/v1/carnivals/{carnival}/adjustments",
            "/api/v1/event-definitions",
            "/api/v1/school/houses",
            "/api/v1/version",
        ];
        var data = new TheoryData<string, string>();
        // Every one of the four roles must see every read. `Viewer` is the tight
        // end (read-only by definition) and `SystemAdministrator` the other, since
        // it was historically the role most likely to be left out of a read policy.
        foreach (var role in new[] { "Viewer", "Recorder", "CarnivalAdministrator", "SystemAdministrator" })
            foreach (var path in paths)
                data.Add(role, path);
        return data;
    }

    private string Resolve(string template) => template
        .Replace("{carnival}", fixture.Seed.CarnivalId.ToString())
        .Replace("{event}", fixture.Seed.EventId.ToString())
        .Replace("{adjustment}", fixture.AdjustmentId.ToString());

    /// <summary>
    /// Valid bodies throughout, so the permitted-role row proves the request
    /// succeeded rather than merely getting past authorisation.
    /// </summary>
    private HttpRequestMessage Request(string method, string template)
    {
        JsonContent? body = null;
        if (method is "POST" or "PUT")
            body = template switch
            {
                var t when t.EndsWith("/settings", StringComparison.Ordinal) =>
                    JsonContent.Create(new { scoring = new { placePoints = new[] { 4, 3, 2 } } }),
                var t when t.EndsWith("/adjustments", StringComparison.Ordinal) =>
                    JsonContent.Create(new
                    {
                        houseCode = "BANKSIA",
                        points = 3,
                        category = "Novelty 1",
                        reason = "authorisation matrix",
                    }),
                _ => JsonContent.Create(new { reason = "authorisation matrix" }),
            };
        return new HttpRequestMessage(new HttpMethod(method), Resolve(template)) { Content = body };
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Unauthenticated_requests_get_401(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(passingRole);
        Assert.NotNull(refusedRole);
        var response = await fixture.Factory.CreateClient().SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // ADR-0011: the outbox pauses the queue on this code, so it is contract.
        await response.AssertErrorAsync(ErrorCodes.AuthenticationRequired);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Role_outside_the_policy_gets_403(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(passingRole);
        if (refusedRole.Length == 0) return; // read surface is open to every platform role
        var client = fixture.Factory.CreateClientAs($"wrong-role-{refusedRole}", refusedRole);
        var response = await client.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // ADR-0011: a policy refusal carries the same machine-code body as the
        // endpoints — the outbox discards on this code rather than retrying.
        await response.AssertErrorAsync(ErrorCodes.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Permitted_role_succeeds(string method, string path, string passingRole, string refusedRole)
    {
        Assert.NotNull(refusedRole);
        var client = fixture.Factory.CreateClientAs($"permitted-{passingRole}", passingRole);
        var response = await client.SendAsync(Request(method, path));
        Assert.True(response.IsSuccessStatusCode,
            $"{method} {path} as {passingRole} returned {(int)response.StatusCode} "
            + $"{await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [MemberData(nameof(ReadSurfaceRoles))]
    public async Task Records_manager_and_system_administrator_can_read_the_carnival_surface(string role, string path)
    {
        var client = fixture.Factory.CreateClientAs($"read-{role}", role);
        var response = await client.GetAsync(Resolve(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The read surface's 403 row. A staff member who signs in successfully but
    /// whose IdP groups map to no platform role
    /// (<see cref="Identity.GroupToRoleClaimsTransformation"/> yields nothing) is
    /// authenticated, not authorised — the standings must refuse rather than leak
    /// the scoreboard to anyone in the directory.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoleGatedReads))]
    public async Task Authenticated_principal_with_no_platform_role_is_refused_the_read_surface(string path)
    {
        var client = fixture.Factory.CreateClientAs("no-mapped-group-staff"); // no roles header
        var response = await client.GetAsync(Resolve(path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.Forbidden);
    }

    /// <summary>
    /// …and the deliberate exception to it: the two platform reads require a
    /// session but no role (H1 closed the anonymous hole; they expose only the
    /// configured school name and house codes, which every signed-in user needs
    /// before any role is assigned). Pinned so widening the gate is a choice.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/school/houses")]
    [InlineData("/api/v1/version")]
    public async Task Platform_reads_need_a_session_but_no_role(string path)
    {
        var client = fixture.Factory.CreateClientAs("no-mapped-group-staff");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_write_to_the_scoring_slice()
    {
        // Read access to standings is not permission to change how they are
        // computed. This pinned `RecordsManager` before ADR-0016; that role
        // collapsed INTO CarnivalAdministrator, which legitimately writes here, so
        // the row INVERTED rather than renaming. `Viewer` is what now carries the
        // property — deleting the test instead would have quietly removed the only
        // check that a read-only role cannot move house points.
        var client = fixture.Factory.CreateClientAs("viewer-write", "Viewer");
        foreach (var request in new[]
        {
            Request("PUT", "/api/v1/carnivals/{carnival}/settings"),
            Request("POST", "/api/v1/carnivals/{carnival}/adjustments"),
            Request("POST", "/api/v1/carnivals/{carnival}/adjustments/{adjustment}/void"),
        })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
}
