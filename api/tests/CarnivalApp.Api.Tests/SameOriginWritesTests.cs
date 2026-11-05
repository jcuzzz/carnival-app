using System.Net;
using Microsoft.AspNetCore.Http;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>ADR-0065 §9: unsafe requests under /api come from the app's own origin.</summary>
public sealed class SameOriginWritesTests : IDisposable
{
    private readonly LocalAccountsHostFactory _host = new();

    public void Dispose() => _host.Dispose();

    private static HttpRequest Request(string method, string path, string? fetchSite, string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("carnival.example.org");
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context.Request;
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/local", "same-origin", null, false)]
    [InlineData("POST", "/api/v1/auth/local", "none", null, false)]
    [InlineData("POST", "/api/v1/auth/local", "same-site", null, true)]
    [InlineData("POST", "/api/v1/auth/local", "cross-site", null, true)]
    // Sec-Fetch-Site decides when present, whatever Origin says.
    [InlineData("POST", "/api/v1/auth/local", "cross-site", "https://carnival.example.org", true)]
    [InlineData("POST", "/api/v1/auth/local", "same-origin", "https://evil.example", false)]
    [InlineData("POST", "/api/v1/auth/local", null, "https://carnival.example.org", false)]
    [InlineData("POST", "/api/v1/auth/local", null, "HTTPS://Carnival.Example.org", false)]
    [InlineData("POST", "/api/v1/auth/local", null, "https://evil.example", true)]
    [InlineData("POST", "/api/v1/auth/local", null, "http://carnival.example.org", true)]
    [InlineData("POST", "/api/v1/auth/local", null, "null", true)]
    // Not a browser: curl, the deploy smoke checks, test hosts.
    [InlineData("POST", "/api/v1/auth/local", null, null, false)]
    [InlineData("PUT", "/api/v1/school/settings", "cross-site", null, true)]
    [InlineData("DELETE", "/api/v1/admin/role-assignments/1", "cross-site", null, true)]
    [InlineData("PATCH", "/api/v1/anything", null, "https://evil.example", true)]
    [InlineData("GET", "/api/v1/me", "cross-site", "https://evil.example", false)]
    [InlineData("HEAD", "/api/v1/me", "cross-site", null, false)]
    [InlineData("OPTIONS", "/api/v1/me", "cross-site", null, false)]
    // The OIDC callback is a cross-site form post by design.
    [InlineData("POST", "/signin-oidc", "cross-site", "https://login.microsoftonline.com", false)]
    [InlineData("POST", "/apiary", "cross-site", null, false)]
    public void The_rule(string method, string path, string? fetchSite, string? origin, bool refused) =>
        Assert.Equal(refused, SameOriginWrites.IsRefused(Request(method, path, fetchSite, origin)));

    [Fact]
    public async Task A_cross_site_sign_in_is_refused_and_a_same_origin_one_is_answered()
    {
        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/local")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = "nobody", password = "x" }),
        };
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        var refused = await _host.Session().SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await refused.AssertErrorAsync(ErrorCodes.CrossSiteRequestRefused);

        var plain = await _host.Session().Client.PostAsync("/api/v1/auth/local",
            System.Net.Http.Json.JsonContent.Create(new { username = "nobody", password = "x" }));
        await plain.AssertErrorAsync(ErrorCodes.SignInFailed);
    }

    [Theory]
    // Behind the trusted proxy, the scheme is the client's.
    [InlineData("10.0.0.5", "https://localhost", false)]
    [InlineData("10.0.0.5", "http://localhost", true)]
    // From anywhere else the forwarded scheme is ignored, so the request is plain http.
    [InlineData("203.0.113.5", "https://localhost", true)]
    [InlineData("203.0.113.5", "http://localhost", false)]
    public async Task The_origin_is_compared_with_the_forwarded_scheme_only_from_a_trusted_proxy(
        string peer, string origin, bool refused)
    {
        _host.RemoteAddress = IPAddress.Parse(peer);
        var client = _host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/local")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = "nobody", password = "x" }),
        };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Origin", origin);

        var response = await client.SendAsync(request);

        await response.AssertErrorAsync(refused ? ErrorCodes.CrossSiteRequestRefused : ErrorCodes.SignInFailed);
    }

    [Fact]
    public async Task The_refusal_comes_before_authentication()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/local-accounts")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username = "x", displayName = "x" }),
        };
        request.Headers.Add("Origin", "https://evil.example");

        var response = await _host.Session().Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CrossSiteRequestRefused);
    }
}
