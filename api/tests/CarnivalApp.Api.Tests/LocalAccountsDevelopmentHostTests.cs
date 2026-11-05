using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// In Development the header sign-in stays, and a local account's cookie works beside it, so the
/// end-to-end suite can exercise the production path.
/// </summary>
public sealed class LocalAccountsDevelopmentHostTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private LocalSession Session() => new(factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }));

    [Fact]
    public async Task The_dev_header_and_a_local_cookie_both_sign_in()
    {
        var admin = factory.CreateClientAs("dev-admin", "SystemAdministrator");
        var me = await admin.GetFromJsonAsync<MeResponse>("/api/v1/me");
        Assert.Equal("dev-admin", me!.Subject);
        Assert.False(me.LocalAccount);

        var created = await admin.PostAsJsonAsync("/api/v1/admin/local-accounts",
            new { username = "dev-local", displayName = "Dev Local" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await created.JsonAsync()).GetProperty("setupToken").GetString();

        var session = Session();
        var completed = await session.PostAsync("/api/v1/auth/setup/complete",
            new { token, password = LocalAccountsHostFactory.Password });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var local = await (await session.GetAsync("/api/v1/me")).JsonAsync();
        Assert.True(local.GetProperty("localAccount").GetBoolean());
        Assert.Equal("Dev Local", local.GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/account")).StatusCode);
    }

    [Fact]
    public async Task My_account_refuses_a_session_that_is_not_a_local_accounts()
    {
        var response = await factory.CreateClientAs("dev-official", "Recorder").GetAsync("/api/v1/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.NotALocalAccount);
    }

    [Theory]
    [InlineData("CarnivalAdministrator")]
    [InlineData("Recorder")]
    public async Task People_is_refused_to_anyone_but_a_system_administrator(string role)
    {
        var client = factory.CreateClientAs($"dev-{role}", role);

        var list = await client.GetAsync("/api/v1/admin/local-accounts");
        var create = await client.PostAsJsonAsync("/api/v1/admin/local-accounts",
            new { username = "sneaky", displayName = "Sneaky" });

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        await list.AssertErrorAsync(ErrorCodes.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task Without_a_session_a_fetch_gets_401_not_a_redirect()
    {
        var response = await Session().GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        await response.AssertErrorAsync(ErrorCodes.AuthenticationRequired);
    }
}
