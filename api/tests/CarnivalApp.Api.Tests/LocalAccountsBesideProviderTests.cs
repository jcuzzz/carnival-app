using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Local accounts beside Entra (ADR-0065 §1–3): the one box routes by name, a local session never
/// reaches Microsoft, and by default only System administrators may hold a local account.
/// </summary>
public sealed class LocalAccountsBesideProviderTests : IDisposable
{
    private readonly LocalAccountsHostFactory _host = new LocalAccountsHostFactory().WithEntra();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_local_sign_in_and_sign_out_never_touch_the_provider()
    {
        var admin = await _host.FirstAdministratorAsync();
        await _host.SignInAsync("backup-admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);

        var logout = await admin.GetAsync("/api/v1/auth/logout");

        Assert.Equal(HttpStatusCode.Found, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location!.OriginalString);
        Assert.Null(admin.Cookie);
        Assert.Equal(0, _host.Discovery.Fetches);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_a_refused_or_expired_local_cookie_still_skips_the_provider()
    {
        var admin = await _host.FirstAdministratorAsync();
        var copied = admin.Cookie;
        await admin.GetAsync("/api/v1/auth/logout");

        var refused = _host.Session();
        refused.Cookie = copied;
        var afterRefusal = await refused.GetAsync("/api/v1/auth/logout");
        Assert.Equal("/", afterRefusal.Headers.Location!.OriginalString);

        var expired = await _host.SignInAsync("backup-admin");
        _host.Time.Now += TimeSpan.FromHours(13);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync("/api/v1/me")).StatusCode);
        var afterExpiry = await expired.GetAsync("/api/v1/auth/logout");
        Assert.Equal("/", afterExpiry.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Methods_names_the_provider_and_identify_routes_by_name()
    {
        var admin = await _host.FirstAdministratorAsync();
        var methods = await (await admin.GetAsync("/api/v1/auth/methods")).JsonAsync();
        Assert.Equal("Entra", methods.GetProperty("provider").GetString());

        var local = await (await admin.PostAsync("/api/v1/auth/identify", new { username = "Backup-Admin" })).JsonAsync();
        Assert.Equal("password", local.GetProperty("method").GetString());

        foreach (var name in new[] { "alex@school.test", "jsmith" })
        {
            var routed = await (await admin.PostAsync("/api/v1/auth/identify",
                new { username = name, returnUrl = "https://evil.example/" })).JsonAsync();
            Assert.Equal("provider", routed.GetProperty("method").GetString());
            Assert.Equal($"/api/v1/auth/login?returnUrl=%2F&loginHint={Uri.EscapeDataString(name)}",
                routed.GetProperty("loginUrl").GetString());
        }
    }

    [Fact]
    public async Task The_login_hint_reaches_the_provider_encoded()
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/auth/login?returnUrl=%2Fdeck&loginHint=alex%26x%40school.test");
        request.Headers.Add("Accept", "text/html");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(LocalAccountsHostFactory.AuthorizationEndpoint, location);
        Assert.Contains("login_hint=alex%26x%40school.test", location);
    }

    [Fact]
    public async Task Only_system_administrators_sign_in_locally_until_the_school_allows_everyone()
    {
        var admin = await _host.FirstAdministratorAsync();
        var (_, token) = await LocalAccountsHostFactory.CreateAccountAsync(admin, "helper1");

        var completed = await _host.Session().PostAsync("/api/v1/auth/setup/complete",
            new { token, password = LocalAccountsHostFactory.Password });
        Assert.Equal(HttpStatusCode.Forbidden, completed.StatusCode);
        await completed.AssertErrorAsync(ErrorCodes.LocalSignInNotAllowed);
        Assert.False(completed.Headers.Contains("Set-Cookie"));

        var refused = await _host.Session().PostAsync("/api/v1/auth/local",
            new { username = "helper1", password = LocalAccountsHostFactory.Password });
        await refused.AssertErrorAsync(ErrorCodes.LocalSignInNotAllowed);

        await LocalAccountsHostFactory.SetSignInSettingsAsync(admin, localAccounts: "Everyone");
        var helper = await _host.SignInAsync("helper1");
        Assert.Equal(HttpStatusCode.OK, (await helper.GetAsync("/api/v1/me")).StatusCode);

        await LocalAccountsHostFactory.SetSignInSettingsAsync(admin, localAccounts: "SystemAdministrators");
        Assert.Equal(HttpStatusCode.Unauthorized, (await helper.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_local_accounts_administrator_role_ends_its_session_beside_a_provider()
    {
        var admin = await _host.FirstAdministratorAsync();
        await LocalAccountsHostFactory.SetSignInSettingsAsync(admin, localAccounts: "Everyone");
        var (_, second) = await _host.ActiveAccountAsync(admin, "second-admin");
        var grant = await (await admin.PostAsync("/api/v1/admin/role-assignments",
            new { upn = "second-admin", role = "SystemAdministrator", reason = "Backup" })).JsonAsync();
        await LocalAccountsHostFactory.SetSignInSettingsAsync(admin, localAccounts: "SystemAdministrators");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/me")).StatusCode);

        var revoke = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/admin/role-assignments/{grant.GetProperty("id").GetGuid()}")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { reason = "No longer needed" }),
        };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(revoke)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/v1/me")).StatusCode);
    }
}

/// <summary>A provider session never pays for the local-account check (ADR-0065).</summary>
public sealed class ProviderSessionTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public ProviderSessionTests() => _host = EntraSignOutTests.CreateEntraHost(_factory);

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task An_entra_cookie_never_queries_the_local_accounts()
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", EntraSignOutTests.SignedInCookieHeader(_host));
        _factory.Race.CountFrom("LocalAccounts");

        var me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False((await me.JsonAsync()).GetProperty("localAccount").GetBoolean());
        Assert.Equal(0, _factory.Race.Counted);
    }

    [Fact]
    public async Task An_entra_session_has_no_local_account_to_manage()
    {
        var client = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", EntraSignOutTests.SignedInCookieHeader(_host));

        var response = await client.GetAsync("/api/v1/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.NotALocalAccount);
    }

    [Fact]
    public async Task A_local_cookie_on_the_same_host_is_checked_against_its_account()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalAccount>>();
            var account = new LocalAccount
            {
                Id = Guid.NewGuid(),
                UserName = "backup-admin",
                DisplayName = "Backup administrator",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "test",
            };
            Assert.True((await users.CreateAsync(account, LocalAccountsHostFactory.Password)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            db.RoleAssignments.Add(new RoleAssignment
            {
                Id = Guid.NewGuid(),
                SubjectId = LocalAccount.SubjectFor(account.Id),
                SubjectUpn = "backup-admin",
                Role = PlatformRole.SystemAdministrator,
                Reason = "Backup",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "test",
            });
            await db.SaveChangesAsync();
        }
        var session = new LocalSession(
            _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }));
        Assert.Equal(HttpStatusCode.OK, (await session.PostAsync("/api/v1/auth/local",
            new { username = "backup-admin", password = LocalAccountsHostFactory.Password })).StatusCode);
        _factory.Race.CountFrom("LocalAccounts");

        var me = await session.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True((await me.JsonAsync()).GetProperty("localAccount").GetBoolean());
        Assert.True(_factory.Race.Counted > 0);
    }
}

/// <summary>ADFS has no hint of its own in the OIDC handler, so the API adds it.</summary>
public sealed class AdfsLoginHintTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly WebApplicationFactory<Program> _host;

    public AdfsLoginHintTests() => _host = _factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Adfs:Authority", "https://adfs.test/adfs");
        builder.UseSetting("Adfs:ClientId", "carnival-app");
        builder.UseSetting("Adfs:ClientSecret", "test-secret");
        builder.ConfigureServices(services => services.PostConfigure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme,
            options => options.ConfigurationManager = new CountingConfigurationManager()));
    });

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task The_login_hint_reaches_the_adfs_authorize_url()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/login?loginHint=alex%40school.test");
        request.Headers.Add("Accept", "text/html");

        var response = await _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(LocalAccountsHostFactory.AuthorizationEndpoint, location);
        Assert.Contains("client_id=carnival-app", location);
        Assert.Contains("login_hint=alex%40school.test", location);
    }
}
