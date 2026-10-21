using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// HTTP-level authorisation assertions per import endpoint (proposal §8):
/// 401 unauthenticated, 403 wrong role, authorised with the right role.
/// Uses the config-gated dev header scheme — no live tenant involved.
/// </summary>
public sealed class EndpointAuthorizationHttpTests : IClassFixture<ApiTestFactory>
{
    private static readonly Guid SomeId = Guid.NewGuid();
    private readonly ApiTestFactory _factory;

    public EndpointAuthorizationHttpTests(ApiTestFactory factory) => _factory = factory;

    public static TheoryData<string, string> Endpoints => new()
    {
        { "POST", "/api/v1/imports" },
        { "GET", "/api/v1/imports" },
        { "GET", "/api/v1/imports/defaults" },
        { "GET", $"/api/v1/imports/{SomeId}/validation" },
        { "GET", $"/api/v1/imports/{SomeId}/reconciliation" },
        { "POST", $"/api/v1/imports/{SomeId}/approve" },
        { "POST", $"/api/v1/imports/{SomeId}/promote" },
        // ADR-0048 §7: roles:administer, SystemAdministrator only.
        { "GET", "/api/v1/admin/role-assignments" },
        { "POST", "/api/v1/admin/role-assignments" },
        { "DELETE", $"/api/v1/admin/role-assignments/{SomeId}" },
        // ADR-0050 §6: school:administer, SystemAdministrator only. The LIST is
        // no longer one of them (ADR-0054 §2) — see the split assertion below.
        { "GET", "/api/v1/admin/event-templates/swimming/reconcile" },
        { "POST", "/api/v1/admin/event-templates/swimming/reconcile" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var roleAssignment = path.StartsWith("/api/v1/admin/role-assignments", StringComparison.Ordinal);
        if (method == "DELETE" && roleAssignment)
            request.Content = JsonContent.Create(new { reason = "No longer required." });
        else if (method == "POST")
            request.Content = roleAssignment
                ? JsonContent.Create(new { upn = "coach@school.test", role = "Recorder", reason = "Setup." })
                : path.EndsWith("/reconcile", StringComparison.Ordinal)
                    ? JsonContent.Create(new { diffHash = "stale", reason = "Authorisation probe." })
                    : path.EndsWith("/approve", StringComparison.Ordinal)
                        ? JsonContent.Create(new { approve = true })
                        : new MultipartFormDataContent();
        return request;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Unauthenticated_requests_get_401(string method, string path)
    {
        var response = await _factory.CreateClient().SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Wrong_role_gets_403(string method, string path)
    {
        // ResultsOfficial has no import permissions at all (§8).
        var client = _factory.CreateClientAs("official-1", "Recorder");
        var response = await client.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task System_administrator_passes_authorisation_everywhere(string method, string path)
    {
        var client = _factory.CreateClientAs("sysadmin-1", "SystemAdministrator");
        var response = await client.SendAsync(Request(method, path));
        // Authorisation passed — outcome is now the handler's business
        // (404 unknown batch, 400 missing file part, etc.), never 401/403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A carnival administrator cannot READ an import, let alone manage one.
    /// </summary>
    /// <remarks>
    /// This is the cell that makes ADR-0016's four-role split real rather than
    /// cosmetic. It previously asserted that an `EventAdministrator` could view an
    /// import; the owner tightened `imports:view` to SystemAdministrator alone
    /// (2026-08-11), because the ADR rejects a three-role model on exactly one
    /// ground — imports and roster snapshots are where real student data lands —
    /// and `GET /imports/{id}/reconciliation` relays staged roster rows and
    /// unmatched students verbatim.
    /// </remarks>
    [Fact]
    public async Task A_carnival_administrator_cannot_view_or_manage_imports()
    {
        var client = _factory.CreateClientAs("carnival-admin-1", "CarnivalAdministrator");

        var view = await client.GetAsync($"/api/v1/imports/{SomeId}/validation");
        Assert.Equal(HttpStatusCode.Forbidden, view.StatusCode);

        var manage = await client.PostAsync($"/api/v1/imports/{SomeId}/promote", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.Forbidden, manage.StatusCode);
    }

    /// <summary>
    /// ADR-0054 §2's split: a carnival administrator may LIST the templates (the
    /// picker in front of apply-template) but not preview or apply a reconcile,
    /// which rewrites the catalogue every carnival shares.
    /// </summary>
    [Fact]
    public async Task A_carnival_administrator_lists_templates_but_cannot_reconcile_them()
    {
        var client = _factory.CreateClientAs("carnival-admin-templates", "CarnivalAdministrator");

        var listed = await client.GetAsync("/api/v1/admin/event-templates");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var preview = await client.GetAsync("/api/v1/admin/event-templates/swimming/reconcile");
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);

        var apply = await client.PostAsJsonAsync("/api/v1/admin/event-templates/swimming/reconcile",
            new { diffHash = "stale", reason = "Authorisation probe." });
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);
    }

    /// <summary>
    /// The alias table is reached from the DEV-AUTH entry point too, not only the
    /// group→role claims transformation.
    /// </summary>
    /// <remarks>
    /// Unit coverage of <c>PlatformRoles.TryParse</c> proves the table; it does
    /// not prove the handler is still wired to it. If this ever fails, the local
    /// dev loop, the runbook's local checklist and every persona start 403-ing
    /// while the unit tests stay green — which reads as an authorisation bug
    /// rather than a missing call.
    /// </remarks>
    [Theory]
    [InlineData("EventAdministrator")]
    [InlineData("Verifier")]
    [InlineData("RecordsManager")]
    public async Task A_retired_role_name_on_the_dev_auth_header_resolves_over_http(string retired)
    {
        // Carnival creation is CarnivalAdministrator-only, so a 201 proves the
        // retired name resolved all the way through to the policy.
        var client = _factory.CreateClientAs($"alias-{retired}", retired);
        var response = await client.PostAsJsonAsync("/api/v1/carnivals", new
        {
            name = $"Alias {retired}", type = "Swimming", date = "2027-02-25",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_numeric_role_value_never_resolves()
    {
        // Enum.TryParse accepts the numeric form, and the numbers MOVED in the
        // four-role rename: "3" meant RecordsManager (→ CarnivalAdministrator) and
        // now means SystemAdministrator. A config typo must not silently escalate.
        var client = _factory.CreateClientAs("numeric-role", "3");
        var response = await client.PostAsJsonAsync("/api/v1/carnivals", new
        {
            name = "Numeric", type = "Swimming", date = "2027-02-25",
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Full_import_flow_succeeds_over_http_as_system_administrator()
    {
        var client = _factory.CreateClientAs("sysadmin-http", "SystemAdministrator");
        using var form = new MultipartFormDataContent();
        var csv = "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                  + "H1,Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n";
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "roster.csv");

        var staged = await client.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, staged.StatusCode);
        var id = (await staged.Content.ReadFromJsonAsync<StageResponse>())!.Id;

        var approved = await client.PostAsJsonAsync($"/api/v1/imports/{id}/approve", new { approve = true });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var promoted = await client.PostAsync($"/api/v1/imports/{id}/promote", null);
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
    }

    private sealed record StageResponse(Guid Id, string Status);

    [Fact]
    public async Task Batch_list_returns_metadata_most_recent_first_for_viewers()
    {
        // Fresh factory: the shared one accumulates batches from other tests.
        using var factory = new ApiTestFactory();
        var admin = factory.CreateClientAs("sysadmin-list", "SystemAdministrator");

        async Task<Guid> StageAsync(string fileName, string stkey)
        {
            using var form = new MultipartFormDataContent();
            var csv = "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n"
                      + $"{stkey},Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n";
            form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", fileName);
            var staged = await admin.PostAsync("/api/v1/imports", form);
            Assert.Equal(HttpStatusCode.Created, staged.StatusCode);
            return (await staged.Content.ReadFromJsonAsync<StageResponse>())!.Id;
        }

        var first = await StageAsync("first.csv", "L1");
        var second = await StageAsync("second.csv", "L2");

        // Same read policy as the batch-detail endpoints — which is now
        // SystemAdministrator-only (ADR-0016, owner 2026-08-11).
        var viewer = factory.CreateClientAs("sysadmin-list-viewer", "SystemAdministrator");
        var response = await viewer.GetAsync("/api/v1/imports");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var batches = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .EnumerateArray().ToList();
        Assert.Equal(2, batches.Count);
        // Most recent first.
        Assert.Equal(second, batches[0].GetProperty("id").GetGuid());
        Assert.Equal(first, batches[1].GetProperty("id").GetGuid());
        Assert.Equal("second.csv", batches[0].GetProperty("sourceFileName").GetString());
        Assert.Equal("Validated", batches[0].GetProperty("status").GetString());
        Assert.Equal(1, batches[0].GetProperty("newCount").GetInt32());
        Assert.Equal("sysadmin-list", batches[0].GetProperty("importedBySubject").GetString());
        Assert.True(batches[0].GetProperty("importedAtUtc").GetDateTimeOffset()
                    >= batches[1].GetProperty("importedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Version_requires_authentication()
    {
        // It echoes the configured school name — not anonymous surface.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync("/api/v1/version")).StatusCode);

        var viewer = _factory.CreateClientAs("viewer-version", "Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/version")).StatusCode);
    }

    [Fact]
    public async Task Dev_header_scheme_is_never_registered_outside_development()
    {
        // Structural guard: "no tenant configured" is not enough to reach the
        // header handler — a non-Development host refuses it even with
        // Identity:DevAuth:Enabled=true, so a half-configured school deployment
        // is unusable rather than open.
        using var factory = new ApiTestFactory();
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "sysadmin-1");
        client.DefaultRequestHeaders.Add(DevAuthentication.RolesHeader, "SystemAdministrator");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/imports/{SomeId}/validation")).StatusCode);
    }

    [Fact]
    public async Task Explicitly_pinning_DevAuth_in_Production_still_grants_nothing()
    {
        // The pilot's demo-mode gate (BACKLOG P0). The test above enters the
        // dev-auth branch by INFERENCE (no provider section populated); a
        // deployment can also reach it by pinning `Identity:Provider=DevAuth`
        // outright, which is the shape a copied demo appsettings would take. The
        // environment check sits below both routes, so both answer 401.
        using var factory = new ApiTestFactory()
            .With("Identity:Provider", "DevAuth")
            .With(DevAuthentication.EnabledKey, "true");
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var client = production.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "sysadmin-1");
        client.DefaultRequestHeaders.Add(DevAuthentication.RolesHeader, "SystemAdministrator");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/imports/{SomeId}/validation")).StatusCode);
    }

    [Fact]
    public async Task Dev_headers_are_ignored_when_the_flag_is_off()
    {
        using var lockedDown = new ApiTestFactory().With(DevAuthentication.EnabledKey, "false");
        var client = lockedDown.CreateClientAs("sysadmin-1", "SystemAdministrator");
        var response = await client.GetAsync($"/api/v1/imports/{SomeId}/validation");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Entra_group_claims_map_to_roles_from_configuration()
    {
        // Group object id → role comes solely from configuration (§7.3).
        using var factory = new ApiTestFactory().With(
            "Identity:GroupToRoleMappings:11111111-aaaa-4bbb-8ccc-000000000001", "SystemAdministrator");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "group-member-1");
        client.DefaultRequestHeaders.Add(DevAuthentication.GroupsHeader, "11111111-aaaa-4bbb-8ccc-000000000001");

        var response = await client.GetAsync($"/api/v1/imports/{SomeId}/validation");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // authorised via group mapping

        // An unmapped group grants nothing.
        var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, "group-member-2");
        stranger.DefaultRequestHeaders.Add(DevAuthentication.GroupsHeader, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/v1/imports/{SomeId}/validation")).StatusCode);
    }
}
