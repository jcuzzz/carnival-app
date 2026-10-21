using System.Net;
using System.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The authorisation sweep on the Entra host, where the interactive sign-in surface exists:
/// <c>/auth/login</c> and <c>/auth/logout</c> are endpoints the DevAuth sweep never sees, and the
/// OIDC callbacks are handler paths no endpoint sweep can see, so their outcomes are pinned here
/// by request.
/// </summary>
public sealed class EntraEndpointAuthorizationCoverageTests : IDisposable
{
    private const string SignInCallback = "/signin-oidc";
    private const string SignOutCallback = "/signout-callback-oidc";
    private const string SessionCookie = ".AspNetCore.Cookies=";

    private readonly ApiTestFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public EntraEndpointAuthorizationCoverageTests() =>
        _host = EntraSignOutTests.CreateEntraHost(_factory);

    private IEnumerable<RouteEndpoint> Endpoints => _host.Services
        .GetRequiredService<EndpointDataSource>()
        .Endpoints
        .OfType<RouteEndpoint>();

    private HttpClient CreateClient() =>
        _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Navigation(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Accept", "text/html");
        return request;
    }

    private static bool IssuesSession(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => c.StartsWith(SessionCookie, StringComparison.Ordinal)
                            && !c.StartsWith(SessionCookie + ";", StringComparison.Ordinal));

    [Fact]
    public void Every_endpoint_either_carries_authorisation_or_says_it_is_anonymous()
    {
        var undeclared = Endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void The_anonymous_surface_is_exactly_the_named_routes()
    {
        var anonymous = Endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            EndpointAuthorizationCoverageTests.Anonymous.Order(StringComparer.Ordinal), anonymous);
    }

    [Fact]
    public void The_oidc_callbacks_are_handler_paths_not_endpoints()
    {
        var patterns = Endpoints.Select(e => e.RoutePattern.RawText).ToList();

        Assert.DoesNotContain(SignInCallback, patterns);
        Assert.DoesNotContain(SignOutCallback, patterns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?returnUrl=%2Fcarnivals")]
    [InlineData("?returnUrl=https%3A%2F%2Fevil.example")]
    [InlineData("?returnUrl=%2F%09%2Fevil.example")]
    public async Task Login_navigation_is_anonymous_and_redirects_to_the_authority(string query)
    {
        var response = await CreateClient().SendAsync(Navigation($"/api/v1/auth/login{query}"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(EntraSignOutTests.AuthorizationEndpoint, location);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fsignin-oidc", location);
    }

    [Fact]
    public async Task Login_from_fetch_answers_401_rather_than_an_opaque_redirect()
    {
        var response = await CreateClient().GetAsync("/api/v1/auth/login");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Logout_is_anonymous_and_clears_the_session_cookie()
    {
        var response = await CreateClient().GetAsync("/api/v1/auth/logout");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(EntraSignOutTests.EndSessionEndpoint, location);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(SessionCookie + ";", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task A_forged_sign_in_callback_is_refused_and_issues_no_session(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), SignInCallback);
        if (method == "POST")
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id_token"] = "forged",
                ["state"] = "forged",
            });

        var response = await CreateClient().SendAsync(request);

        // The fallback policy would have challenged: a 401 or a 302 to the IdP.
        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.True(
            location is null
            || (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)),
            $"A refused callback may only land on an app-local path, not {location}.");
        Assert.False(IssuesSession(response));
    }

    [Fact]
    public async Task The_sign_out_callback_is_anonymous_and_returns_to_the_app_root()
    {
        var client = CreateClient();
        var logout = await client.GetAsync("/api/v1/auth/logout");
        var state = HttpUtility.ParseQueryString(logout.Headers.Location!.Query)["state"];
        Assert.False(string.IsNullOrEmpty(state));

        var callback = $"{SignOutCallback}?state={Uri.EscapeDataString(state)}";
        var response = await client.GetAsync(callback);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.False(IssuesSession(response));
    }

    [Fact]
    public async Task A_sign_out_callback_without_state_goes_nowhere_and_opens_no_session()
    {
        var response = await CreateClient().GetAsync(SignOutCallback);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(IssuesSession(response));
    }

    [Fact]
    public async Task An_authorised_route_refuses_fetch_without_a_session_and_serves_one()
    {
        var anonymous = await CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", EntraSignOutTests.SignedInCookieHeader(_host));
        var signedIn = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }
}
