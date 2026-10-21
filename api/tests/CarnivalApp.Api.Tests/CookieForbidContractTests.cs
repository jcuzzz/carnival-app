using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0011 on the OIDC hosts, which the rest of the suite never sees:
/// <see cref="ApiTestFactory"/> pins Development + the dev-header scheme, whose
/// base handler already writes a plain 403. Under Entra/ADFS the OIDC handler
/// forwards forbid to its cookie sign-in scheme, and the cookie defaults
/// answered a 302 to <c>/Account/AccessDenied</c> with no body — a redirect
/// <c>fetch()</c> follows into a 404 the offline outbox reads as a TERMINAL
/// rejection, discarding a capture the recorder simply lacked the role for.
///
/// Built on the Entra branch with a static discovery document, as
/// <see cref="EntraSignOutTests"/> does, so nothing is fetched from the network
/// (an unreachable authority is itself a bodyless 500 on the challenge path).
/// </summary>
public sealed class CookieForbidContractTests : IDisposable
{
    private readonly ApiTestFactory _factory;
    private readonly WebApplicationFactory<Program> _host;

    public CookieForbidContractTests()
    {
        _factory = new ApiTestFactory();
        _host = _factory.WithWebHostBuilder(builder =>
        {
            // Only UseSetting is visible before the host is built, which is when
            // AddPlatformIdentity picks its branch.
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "8f2b1c4e-6d3a-4f19-9c07-5a1e2b3d4c5f");
            builder.UseSetting("AzureAd:ClientId", "2c9e7a1b-4f60-4d3e-8a52-7b6c1d0e9f34");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("AzureAd:CallbackPath", "/signin-oidc");
            builder.ConfigureServices(services =>
                services.PostConfigure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                        {
                            Issuer = "https://login.test/",
                            AuthorizationEndpoint = "https://login.test/authorize",
                            TokenEndpoint = "https://login.test/token",
                            EndSessionEndpoint = "https://login.test/logout",
                        })));
        });
    }

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    private HttpClient CreateClient() =>
        _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A session cookie for an authenticated user holding no platform role.</summary>
    private string SignedInCookieHeader()
    {
        var options = _host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Sam Spectator"),
                new Claim("oid", "6c0a4f13-2d58-4e7b-9f31-8a2b5c6d7e90"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        };
        // RemoteAuthenticationHandler forwards authenticate to its sign-in cookie
        // and accepts the ticket only if this item names the scheme that issued
        // it, exactly as the OIDC callback stamps it.
        properties.Items[".AuthScheme"] = OpenIdConnectDefaults.AuthenticationScheme;
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    [Fact]
    public async Task An_authenticated_call_without_the_role_is_403_with_the_contract_body()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", SignedInCookieHeader());

        var response = await client.GetAsync("/api/v1/imports");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        await response.AssertErrorAsync(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task An_anonymous_api_call_is_401_with_the_contract_body()
    {
        var response = await CreateClient().GetAsync("/api/v1/imports");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        await response.AssertErrorAsync(ErrorCodes.AuthenticationRequired);
    }

    /// <summary>
    /// The half that must NOT change: a browser navigation still gets the
    /// redirect, which is how a person signs in at all.
    /// </summary>
    [Fact]
    public async Task A_document_navigation_still_redirects_to_the_identity_provider()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");

        var response = await client.GetAsync("/api/v1/imports");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("https://login.test/authorize", response.Headers.Location!.ToString());
    }

    /// <summary>
    /// M17: <see cref="EndpointAuthorizationCoverageTests"/> sweeps the dev-auth
    /// host only, where /auth/login and /auth/logout are not even mapped. The
    /// same invariant, on the host that maps them.
    /// </summary>
    [Fact]
    public void Every_endpoint_on_the_oidc_host_declares_authorisation_or_anonymity()
    {
        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToList();

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();
        Assert.Empty(undeclared);

        var anonymousApi = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Where(path => path.StartsWith("/api/v1", StringComparison.Ordinal))
            .ToList();
        Assert.All(anonymousApi, path => Assert.True(
            path.StartsWith("/api/v1/auth/", StringComparison.Ordinal) || path == "/api/v1/branding",
            $"{path} is anonymous under /api/v1 and is neither an auth route nor /branding."));
        Assert.Contains("/api/v1/auth/login", anonymousApi);
        Assert.Contains("/api/v1/auth/logout", anonymousApi);
    }
}
