using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The bounds the server puts on a request before it believes any of it: the
/// device-asserted captured actor, what an upload may be, and how often the
/// bulk-data surfaces may be asked. All three answer in the ADR-0011 body.
/// </summary>
public sealed class RequestHardeningTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    private static MultipartFormDataContent Roster(
        string fileName, string? contentType = null, int rows = 1)
    {
        var csv = new StringBuilder(
            "STKEY,SURNAME,FIRST_NAME,SECOND_NAME,PREF_NAME,TITLE,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n");
        for (var i = 0; i < rows; i++)
            csv.Append($"U{Guid.NewGuid():N},Smith,Ann,,Ann,Miss,F,WATTLE,ACTV,2013-01-20,07A,07\n");
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv.ToString()));
        if (contentType is not null) part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    // ---- The captured actor (ADR-0002/0006) --------------------------------

    // A tab is the only control character HttpClient will put on the wire, so it
    // is the one that can be proved end to end; the rest are unit-covered below.
    [Theory]
    [InlineData("with\ttab")]
    public async Task A_captured_actor_carrying_control_characters_is_refused(string subject)
    {
        // The header names the RECORDER on a result, so an arbitrary string must
        // not reach the record book — a newline in an IdP subject is a
        // log-forging tool, never a subject.
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("capture-recorder", "Recorder");
        client.DefaultRequestHeaders.Add(CapturedActor.SubjectHeader, subject);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CapturedActorInvalid);
    }

    [Fact]
    public async Task An_over_long_captured_actor_is_refused_rather_than_truncated()
    {
        var seeded = await CarnivalSeed.SeedAsync(_factory);
        var client = _factory.CreateClientAs("capture-recorder-long", "Recorder");
        client.DefaultRequestHeaders.Add(
            CapturedActor.SubjectHeader, new string('a', CapturedActor.MaxSubjectLength + 1));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}/entries",
            new { studentId = seeded.EligibleBoyId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CapturedActorInvalid);

        // Nothing was written under the malformed identity.
        Assert.Empty(await CarnivalSeed.QueryAsync(_factory, db =>
            db.Entries.Where(e => e.CarnivalEventId == seeded.EventId).ToListAsync()));
    }

    [Fact]
    public void The_subject_bound_is_the_audit_columns_width()
    {
        Assert.True(CapturedActor.IsWellFormedSubject(
            new string('a', CapturedActor.MaxSubjectLength)));
        Assert.True(CapturedActor.IsWellFormedSubject(null));
        Assert.True(CapturedActor.IsWellFormedSubject("s-1234@school.vic.edu.au"));
        Assert.False(CapturedActor.IsWellFormedSubject(
            new string('a', CapturedActor.MaxSubjectLength + 1)));
    }

    [Theory]
    [InlineData("with\nnewline")]
    [InlineData("with\rreturn")]
    [InlineData("with\ttab")]
    [InlineData("with\0null")]
    [InlineData("with\u007fdelete")]
    public void No_control_character_survives_the_subject_check(string subject) =>
        Assert.False(CapturedActor.IsWellFormedSubject(subject));

    // ---- What an upload may be ---------------------------------------------

    [Theory]
    [InlineData("roster.txt", null)]
    [InlineData("roster.csv.exe", null)]
    [InlineData("roster.csv", "application/zip")]
    public async Task An_upload_outside_the_csv_allowlist_is_refused(string fileName, string? contentType)
    {
        var client = _factory.CreateClientAs("upload-admin", "SystemAdministrator");
        using var form = Roster(fileName, contentType);

        var response = await client.PostAsync("/api/v1/imports", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ImportFileTypeUnsupported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/csv")]
    [InlineData("text/plain")]
    [InlineData("application/vnd.ms-excel")]
    public async Task A_csv_upload_is_accepted_with_or_without_a_content_type(string? contentType)
    {
        // Browsers label a .csv part inconsistently (Windows says Excel), and a
        // multipart part often carries no type at all; the extension is the gate.
        var client = _factory.CreateClientAs("upload-admin-ok", "SystemAdministrator");
        using var form = Roster("roster.csv", contentType);

        var response = await client.PostAsync("/api/v1/imports", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_basename_of_the_uploaded_file_is_stored()
    {
        // The stored name is rendered client-side, so a path (or a Windows path
        // on the Linux deployment) must not survive intake.
        var client = _factory.CreateClientAs("upload-admin-path", "SystemAdministrator");
        using var form = Roster(@"C:\Users\staff\Desktop\..\secret\roster.csv");

        var response = await client.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Staged>())!.Id;

        var stored = await CarnivalSeed.QueryAsync(_factory, db =>
            db.ImportBatches.SingleAsync(b => b.Id == id));
        Assert.Equal("roster.csv", stored.SourceFileName);
    }

    [Theory]
    [InlineData("roster.csv", "roster.csv")]
    [InlineData(@"C:\a\b\roster.csv", "roster.csv")]
    [InlineData("/var/tmp/roster.csv", "roster.csv")]
    [InlineData("../../roster.csv", "roster.csv")]
    public void The_basename_is_cut_on_both_separators(string sent, string expected) =>
        Assert.Equal(expected, RosterUpload.BaseName(sent));

    private sealed record Staged(Guid Id);

    // ---- Rate limiting ------------------------------------------------------

    [Fact]
    public async Task The_bulk_data_ceiling_answers_429_in_the_error_contract()
    {
        // Its own host: the shared factory sets the ceiling out of the way so one
        // test class cannot exhaust another's budget.
        using var limited = new ApiTestFactory().With(RateLimiting.PermitsPerMinuteKey, "2");
        var seeded = await CarnivalSeed.SeedAsync(limited);
        var client = limited.CreateClientAs("limited-admin", "CarnivalAdministrator");
        var path = $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results.csv";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);

        var refused = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.TooManyRequests);
        // A fixed window without Retry-After tells the caller nothing about
        // whether to wait two seconds or the full minute.
        Assert.True(refused.Headers.RetryAfter is not null);

        // Partitioned by SUBJECT: a second official is unaffected by the first's
        // ceiling, which is what makes this a per-person limit and not an outage.
        Assert.Equal(HttpStatusCode.OK,
            (await limited.CreateClientAs("limited-admin-2", "CarnivalAdministrator")
                .GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task An_unrated_read_is_never_limited()
    {
        // Only the CSV exports and the roster upload carry the policy; the
        // on-screen book (and every capture path) must stay unthrottled.
        using var limited = new ApiTestFactory().With(RateLimiting.PermitsPerMinuteKey, "1");
        var seeded = await CarnivalSeed.SeedAsync(limited);
        var client = limited.CreateClientAs("unlimited-admin", "CarnivalAdministrator");
        var path = $"/api/v1/carnivals/{seeded.CarnivalId}/reports/results";

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    // ---- The fallback policy's blast radius ---------------------------------

    [Fact]
    public async Task An_unmatched_path_answers_404_not_401()
    {
        // The fallback authorization policy also covers requests that match NO
        // endpoint; without the unmatched-route middleware they would 401 — and
        // a 401 pauses every device's entire outbox drain (ADR-0002). This pin
        // is what makes moving that middleware a test failure, not an incident.
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/api/v1/no-such-route", "/definitely-not-a-route" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await response.AssertErrorAsync(ErrorCodes.RouteNotFound);
        }

        var signedIn = await _factory.CreateClientAs("route-admin", "CarnivalAdministrator")
            .GetAsync("/api/v1/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
        await signedIn.AssertErrorAsync(ErrorCodes.RouteNotFound);
    }

    [Fact]
    public async Task A_wrong_method_answers_405_in_the_error_contract()
    {
        var response = await _factory.CreateClientAs("method-admin", "CarnivalAdministrator")
            .DeleteAsync("/api/v1/version");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.MethodNotAllowed);
    }

    [Fact]
    public async Task A_content_type_no_endpoint_accepts_answers_415_in_the_error_contract()
    {
        var response = await _factory.CreateClientAs("media-admin", "CarnivalAdministrator")
            .PostAsync("/api/v1/carnivals", new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    public async Task A_query_flag_that_is_not_true_or_false_is_a_400(string value)
    {
        var response = await _factory.CreateClientAs("flag-admin", "CarnivalAdministrator")
            .GetAsync($"/api/v1/event-definitions?includeInactive={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?includeInactive=true")]
    [InlineData("?includeInactive=False")]
    public async Task A_query_flag_absent_or_boolean_is_read(string query)
    {
        var response = await _factory.CreateClientAs("flag-admin", "CarnivalAdministrator")
            .GetAsync($"/api/v1/event-definitions{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- The session cookie (ADR-0006, B3) ----------------------------------

    [Fact]
    public void The_session_cookie_is_configured_rather_than_left_at_framework_defaults()
    {
        // The framework default is a 14-day SLIDING cookie, which on a shared
        // iPad is a session that renews itself all term.
        var options = new CookieAuthenticationOptions();
        IdentitySetup.ConfigureSessionCookie(options);

        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
        Assert.False(options.SlidingExpiration);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    // Both provider paths (Entra and ADFS) call ConfigureSessionCookie. The
    // values above are the pin for this host; the forbid/challenge behaviour it
    // also configures is host-tested on the Entra branch in
    // CookieForbidContractTests.

    // ---- Enum binding (ADR-0011) --------------------------------------------

    [Fact]
    public async Task An_integer_enum_value_is_refused_at_binding_not_stored()
    {
        // With allowIntegerValues an out-of-range number bound silently and
        // reached a varchar(1) column as "99": a 500 on PostgreSQL, a phantom
        // unenterable division on SQLite. Nothing validates a bound enum, so the
        // converter is the gate.
        var carnival = await _factory.CreateClientAs("enum-admin", "CarnivalAdministrator")
            .PostAsJsonAsync("/api/v1/carnivals",
                new { name = "Enum", type = "Swimming", date = "2027-02-25", venue = "Pool" });
        Assert.Equal(HttpStatusCode.Created, carnival.StatusCode);
        var carnivalId = (await carnival.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("id").GetGuid();

        var response = await _factory.CreateClientAs("enum-admin", "CarnivalAdministrator")
            .PostAsync($"/api/v1/carnivals/{carnivalId}/events",
                new StringContent(
                    $$"""{"eventDefinitionId":"{{Guid.CreateVersion7()}}","ageGroup":"13","category":99}""",
                    Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RequestInvalid);
    }

    [Fact]
    public async Task An_integer_measure_type_is_refused_at_binding()
    {
        // Left to bind, an unknown measure type ranks as a field event in
        // Placement.HigherIsBetter — a scoring defect from a stray number.
        var response = await _factory.CreateClientAs("enum-admin", "CarnivalAdministrator")
            .PostAsync("/api/v1/event-definitions",
                new StringContent(
                    $$"""
                    {"code":"ENUM-{{Guid.NewGuid():N}}","name":"Freestyle 50m","sport":"Swimming",
                     "measureType":99,"units":"s"}
                    """,
                    Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.RequestInvalid);
    }
}
