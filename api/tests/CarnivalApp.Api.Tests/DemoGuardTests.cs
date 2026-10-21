using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0061 §4: a public host has no path by which a student export can be uploaded, every
/// response is noindex, and every client address is rate limited.
/// </summary>
public sealed class DemoGuardTests
{
    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

    public static TheoryData<string, string> ImportWrites()
    {
        using var host = new DemoHost();
        host.Client();
        var data = new TheoryData<string, string>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var route = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            if (!route.StartsWith(DemoGuard.ImportsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                if (method is not ("GET" or "HEAD")) data.Add(method, route);
        }
        return data;
    }

    [Fact]
    public void The_import_routes_exist_to_be_refused()
    {
        Assert.True(ImportWrites().Count >= 3);
    }

    [Theory]
    [MemberData(nameof(ImportWrites))]
    public async Task Every_import_write_is_refused_even_for_a_system_administrator(string method, string route)
    {
        using var host = new DemoHost();
        var client = host.Client();
        var cookie = await DemoHttp.StartAsync(client);
        var path = Regex.Replace(route, @"\{[^}]+\}", Guid.NewGuid().ToString());

        var response = await DemoHttp.SendAsync(client, new HttpMethod(method), path, cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.DemoImportsDisabled, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_anonymous_import_is_refused_rather_than_challenged()
    {
        using var host = new DemoHost();

        var response = await DemoHttp.SendAsync(host.Client(), HttpMethod.Post, "/API/V1/Imports");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.DemoImportsDisabled, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Every_response_is_marked_noindex()
    {
        using var host = new DemoHost();
        var client = host.Client();

        foreach (var response in new[]
        {
            await client.GetAsync("/api/v1/branding"),
            await client.GetAsync("/api/v1/me"),
            await client.GetAsync("/api/v1/no-such-route"),
        })
            Assert.Equal("noindex, nofollow", string.Join(",", response.Headers.GetValues("X-Robots-Tag")));
    }

    [Fact]
    public async Task A_school_deployment_is_not_marked_noindex()
    {
        using var factory = new ApiTestFactory();

        var response = await factory.CreateClient().GetAsync("/api/v1/branding");

        Assert.False(response.Headers.Contains("X-Robots-Tag"));
    }

    [Fact]
    public async Task Every_endpoint_is_limited_per_client_address()
    {
        using var host = new DemoHost((DemoMode.RequestsPerIpPerMinuteKey, "2"));
        var client = host.Client();
        await client.GetAsync("/api/v1/branding");
        await client.GetAsync("/api/v1/branding");

        var limited = await client.GetAsync("/api/v1/branding");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(ErrorCodes.TooManyRequests, await ErrorCodeAsync(limited));
    }
}
