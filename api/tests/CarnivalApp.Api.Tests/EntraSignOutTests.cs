using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Single sign-out against Entra (ADR-0007): <c>/auth/logout</c> must reach the
/// end-session endpoint carrying <c>id_token_hint</c>, otherwise Entra cannot
/// tell which session is ending and shows its account picker. Runs the real
/// host on the Entra branch of <see cref="CarnivalApp.Api.Identity.IdentitySetup"/>
/// with a static OIDC discovery document, so nothing is fetched from the network.
/// </summary>
public sealed class EntraSignOutTests : IDisposable
{
    internal const string AuthorizationEndpoint = "https://login.test/authorize";
    internal const string EndSessionEndpoint = "https://login.test/logout";
    private const string IdToken = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJvZmZpY2lhbC00MiJ9.not-a-real-signature";

    private readonly ApiTestFactory _factory;
    private readonly WebApplicationFactory<Program> _host;

    public EntraSignOutTests()
    {
        _factory = new ApiTestFactory();
        _host = CreateEntraHost(_factory);
    }

    /// <summary>The real host on the Entra branch, discovery served statically.</summary>
    internal static WebApplicationFactory<Program> CreateEntraHost(ApiTestFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            // AddPlatformIdentity reads configuration BEFORE the host is built, and
            // ConfigureAppConfiguration is only merged in at Build — only UseSetting
            // is visible early enough to select the Entra branch.
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "8f2b1c4e-6d3a-4f19-9c07-5a1e2b3d4c5f");
            builder.UseSetting("AzureAd:ClientId", "2c9e7a1b-4f60-4d3e-8a52-7b6c1d0e9f34");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("AzureAd:CallbackPath", "/signin-oidc");
            builder.ConfigureServices(services =>
                // The metadata address is already resolved into a live
                // ConfigurationManager by the time this runs, so replace the manager
                // itself — configuring Authority/MetadataAddress would still fetch.
                services.PostConfigure<OpenIdConnectOptions>(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                        {
                            Issuer = "https://login.test/",
                            AuthorizationEndpoint = AuthorizationEndpoint,
                            TokenEndpoint = "https://login.test/token",
                            EndSessionEndpoint = EndSessionEndpoint,
                        })));
        });

    private HttpClient CreateClient() =>
        _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private string SignedInCookieHeader() => SignedInCookieHeader(_host);

    /// <summary>A session cookie carrying a stored id token, exactly as the OIDC callback writes it.</summary>
    internal static string SignedInCookieHeader(WebApplicationFactory<Program> host)
    {
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Alex Official"),
                new Claim("preferred_username", "alex.official@synthetic.test"),
                new Claim("oid", "3d1f6a02-9b47-4c8e-a5d2-0e7f8b9c1a23"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        var properties = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        };
        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = IdToken }]);
        // The OIDC handler authenticates only a ticket it issued itself.
        properties.Items[".AuthScheme"] = OpenIdConnectDefaults.AuthenticationScheme;

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    [Fact]
    public void Entra_saves_tokens_so_the_session_cookie_carries_the_id_token()
    {
        var options = _host.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        // SaveTokens is what puts the id token on the cookie ticket; the handler
        // reads it back from SignOutScheme to build id_token_hint.
        Assert.True(options.SaveTokens);
        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.SignOutScheme);
    }

    [Fact]
    public async Task Logout_sends_the_id_token_hint_to_the_end_session_endpoint()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", SignedInCookieHeader());

        var response = await client.GetAsync("/api/v1/auth/logout");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(EndSessionEndpoint, location);
        Assert.Contains($"id_token_hint={IdToken}", location);
        Assert.Contains("post_logout_redirect_uri=", location);
    }

    [Fact]
    public async Task Logout_without_a_session_still_redirects_to_the_end_session_endpoint()
    {
        var response = await CreateClient().GetAsync("/api/v1/auth/logout");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith(EndSessionEndpoint, response.Headers.Location!.ToString());
    }

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }
}
