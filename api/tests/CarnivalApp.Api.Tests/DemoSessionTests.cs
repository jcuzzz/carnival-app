using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0061 §2–3 over HTTP: the demo session cookie is the only identity, sandboxes expire and are
/// dropped, capacity evicts the idle. Databases are faked; real cloning is in the Postgres tests.
/// </summary>
public sealed class DemoSessionTests
{
    private const string Sessions = "/api/v1/demo/sessions";

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task Starting_a_demo_clones_a_database_and_sets_a_strict_session_cookie()
    {
        using var host = new DemoHost();
        var response = await DemoHttp.SendAsync(host.Client(), HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var name = Assert.Single(host.Databases.Existing);
        Assert.True(VisitorDatabase.IsValidName(name));
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("httponly", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(host.Time.Now + DemoMode.IdleLifetime, body.GetProperty("expiresAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_session_cookie_signs_the_visitor_in_as_system_administrator()
    {
        using var host = new DemoHost();
        var client = host.Client();
        var cookie = await DemoHttp.StartAsync(client);

        var me = await DemoHttp.SendAsync(client, HttpMethod.Get, "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var roles = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("roles");
        Assert.Contains(roles.EnumerateArray(), role => role.GetString() == "SystemAdministrator");
    }

    [Fact]
    public async Task A_returning_visitor_with_a_live_cookie_keeps_their_sandbox()
    {
        using var host = new DemoHost();
        var client = host.Client();
        var cookie = await DemoHttp.StartAsync(client);

        var again = await DemoHttp.SendAsync(client, HttpMethod.Post, Sessions, cookie);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(DemoHttp.CookieFrom(again));
        Assert.Single(host.Databases.Existing);
    }

    [Fact]
    public async Task Dev_identity_headers_are_ignored_on_a_demo_host()
    {
        using var host = new DemoHost();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Add("X-Dev-Subject", "intruder");
        request.Headers.Add("X-Dev-Roles", "SystemAdministrator");

        var response = await host.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sports_demo=not-a-token")]
    public async Task Without_a_live_session_the_session_check_is_401(string? cookie)
    {
        using var host = new DemoHost();
        var response = await DemoHttp.SendAsync(host.Client(), HttpMethod.Get, Sessions + "/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.DemoSessionRequired, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_session_idle_for_two_hours_has_expired()
    {
        using var host = new DemoHost();
        var client = host.Client();
        var cookie = await DemoHttp.StartAsync(client);

        host.Time.Now += TimeSpan.FromMinutes(119);
        Assert.Equal(HttpStatusCode.OK, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", cookie)).StatusCode);
        host.Time.Now += TimeSpan.FromMinutes(121);

        Assert.Equal(HttpStatusCode.Unauthorized, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DemoHttp.SendAsync(client, HttpMethod.Get, "/api/v1/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task An_active_session_still_ends_24_hours_after_it_started()
    {
        using var host = new DemoHost();
        var client = host.Client();
        var cookie = await DemoHttp.StartAsync(client);

        for (var hour = 1; hour < 24; hour++)
        {
            host.Time.Now += TimeSpan.FromHours(1);
            Assert.Equal(HttpStatusCode.OK, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", cookie)).StatusCode);
        }
        host.Time.Now += TimeSpan.FromHours(1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", cookie)).StatusCode);
    }

    [Fact]
    public async Task At_capacity_an_idle_sandbox_is_evicted_and_a_busy_one_is_not()
    {
        using var host = new DemoHost((DemoMode.MaxSessionsKey, "1"));
        var client = host.Client();
        var first = await DemoHttp.StartAsync(client);

        var refused = await DemoHttp.SendAsync(client, HttpMethod.Post, Sessions);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(ErrorCodes.DemoAtCapacity, await ErrorCodeAsync(refused));
        Assert.True(refused.Headers.Contains("Retry-After"));

        host.Time.Now += DemoMode.EvictableAfterIdle;
        await DemoHttp.StartAsync(client);

        Assert.Single(host.Databases.Existing);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", first)).StatusCode);
    }

    [Fact]
    public async Task Provisioning_is_limited_per_client_address()
    {
        using var host = new DemoHost((DemoMode.ProvisionsPerIpPerHourKey, "1"));
        var client = host.Client();
        await DemoHttp.StartAsync(client);

        var limited = await DemoHttp.SendAsync(client, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(ErrorCodes.TooManyRequests, await ErrorCodeAsync(limited));
        Assert.Single(host.Databases.Existing);
    }

    [Fact]
    public async Task Provisioning_is_limited_across_all_clients()
    {
        using var host = new DemoHost((DemoMode.ProvisionsPerMinuteKey, "1"));
        var client = host.Client();
        await DemoHttp.StartAsync(client);

        var limited = await DemoHttp.SendAsync(client, HttpMethod.Post, Sessions);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Single(host.Databases.Existing);
    }

    [Fact]
    public async Task The_sweep_drops_expired_and_orphaned_sandboxes_and_keeps_live_ones()
    {
        using var host = new DemoHost();
        var client = host.Client();
        await DemoHttp.StartAsync(client);
        host.Time.Now += TimeSpan.FromHours(3);
        var live = await DemoHttp.StartAsync(client);
        var orphan = VisitorDatabase.NewName();
        host.Databases.Existing.Add(orphan);

        var dropped = await host.Services.GetRequiredService<DemoSessions>().SweepAsync(CancellationToken.None);

        Assert.Equal(2, dropped);
        Assert.Single(host.Databases.Existing);
        Assert.DoesNotContain(orphan, host.Databases.Existing);
        Assert.Equal(HttpStatusCode.OK, (await DemoHttp.SendAsync(client, HttpMethod.Get, Sessions + "/me", live)).StatusCode);
    }

    [Fact]
    public async Task Branding_without_a_session_needs_no_visitor_database()
    {
        using var host = new DemoHost(("Demo:SchoolName", "Example Synthetic College"));

        var response = await host.Client().GetAsync("/api/v1/branding");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Example Synthetic College",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("schoolName").GetString());
    }

    [Theory]
    [InlineData("Identity:DevAuth:Enabled", "true")]
    [InlineData("AzureAd:TenantId", "8f2b1c4e-6d3a-4f19-9c07-5a1e2b3d4c5f")]
    [InlineData("Adfs:Authority", "https://adfs.school/adfs")]
    public void The_demo_identity_refuses_any_other_identity_source(string key, string value)
    {
        using var host = new DemoHost((key, value));

        var error = Assert.ThrowsAny<Exception>(() => host.Client());
        Assert.Contains("DEMO_COEXISTENCE_FORBIDDEN", error.ToString());
    }

    [Fact]
    public void The_demo_identity_requires_demo_mode()
    {
        using var host = new DemoHost((DemoMode.EnabledKey, "false"));

        var error = Assert.ThrowsAny<Exception>(() => host.Client());
        Assert.Contains("requires Demo:Enabled=true", error.ToString());
    }

    [Fact]
    public void Demo_mode_requires_the_demo_identity()
    {
        using var host = new DemoHost((CarnivalApp.Api.Identity.IdentitySetup.ProviderConfig, "DevAuth"));

        var error = Assert.ThrowsAny<Exception>(() => host.Client());
        Assert.Contains("requires Identity:Provider=Demo", error.ToString());
    }
}
