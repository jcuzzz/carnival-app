using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The PWA session surface (ADR-0007 cookie/BFF flow): <c>/me</c> reports the
/// signed-in official (401 when there is none), the interactive <c>/auth/login</c>
/// redirect exists only when a real OIDC provider is configured, and <c>/auth/logout</c>
/// always does. Exercised through the dev-header scheme — no live
/// tenant. The XHR-vs-navigation challenge rule is unit-tested separately
/// (<see cref="ApiChallengeSuppression"/>) because it only fires on the OIDC
/// handler, which these tests do not run.
/// </summary>
public sealed class AuthEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public AuthEndpointsTests(ApiTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Me_is_401_when_not_signed_in()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_subject_display_name_and_mapped_roles()
    {
        var client = _factory.CreateClientAs("official-42", "Recorder", "CarnivalAdministrator");
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me");

        Assert.NotNull(me);
        Assert.Equal("official-42", me!.Subject);
        Assert.Equal("official-42", me.DisplayName); // dev scheme sets name = subject
        Assert.Equal(2, me.Roles.Count);
        Assert.Contains("Recorder", me.Roles);
        Assert.Contains("CarnivalAdministrator", me.Roles);
    }

    [Fact]
    public async Task Login_is_absent_without_a_configured_oidc_provider_and_logout_is_always_there()
    {
        // Dev-auth host: no AzureAd/Adfs section → no provider redirect to start. A local
        // account's session still has to end (ADR-0065), so logout is mapped regardless.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/auth/login")).StatusCode);
        var logout = await client.GetAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Me_says_a_header_session_is_not_a_local_account()
    {
        var me = await _factory.CreateClientAs("official-43", "Viewer").GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.False(me!.LocalAccount);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/carnivals", "/carnivals")]
    [InlineData("/carnivals/123/standings", "/carnivals/123/standings")]
    [InlineData("https://evil.example/steal", "/")]     // absolute → rejected
    [InlineData("//evil.example/steal", "/")]           // protocol-relative → rejected
    [InlineData("/\\evil.example", "/")]                // backslash trick → rejected
    [InlineData("javascript:alert(1)", "/")]            // not a local path → rejected
    [InlineData("/\t/evil.example", "/")]               // tab stripped by browsers → rejected
    [InlineData("/\n/evil.example", "/")]               // newline, likewise
    [InlineData("/carnivals\u007F", "/")]               // DEL anywhere → rejected
    public void LocalReturnUrl_only_accepts_app_local_paths(string? input, string expected) =>
        Assert.Equal(expected, AuthEndpoints.LocalReturnUrl(input));
}

/// <summary>
/// The data-plane-vs-navigation rule that turns an OIDC challenge into a 401 for
/// SPA fetches while leaving the full-page login redirect intact.
/// </summary>
public sealed class ApiChallengeSuppressionTests
{
    private static HttpRequest Request(string path, string accept)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (accept.Length > 0) context.Request.Headers.Accept = accept;
        return context.Request;
    }

    [Fact]
    public void Api_xhr_prefers_401() =>
        Assert.True(ApiChallengeSuppression.PrefersStatusOverRedirect(Request("/api/v1/me", "*/*")));

    [Fact]
    public void Api_document_navigation_still_redirects() =>
        Assert.False(ApiChallengeSuppression.PrefersStatusOverRedirect(
            Request("/api/v1/auth/login", "text/html,application/xhtml+xml")));

    [Fact]
    public void Non_api_path_is_left_to_the_handler() =>
        Assert.False(ApiChallengeSuppression.PrefersStatusOverRedirect(Request("/health", "*/*")));

    [Fact]
    public void Api_call_with_no_accept_header_prefers_401() =>
        Assert.True(ApiChallengeSuppression.PrefersStatusOverRedirect(Request("/api/v1/carnivals", "")));
}
