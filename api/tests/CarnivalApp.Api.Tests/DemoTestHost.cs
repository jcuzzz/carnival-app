using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CarnivalApp.Api.Demo;
using Xunit;

namespace CarnivalApp.Api.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeDatabases : IVisitorDatabases
{
    public HashSet<string> Existing { get; } = [];

    public Task CreateAsync(string name, CancellationToken ct)
    {
        Existing.Add(name);
        return Task.CompletedTask;
    }

    public Task DropAsync(string name, CancellationToken ct)
    {
        Existing.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Existing.ToList());
}

internal sealed class DemoHost : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly SqliteConnection _control = new("DataSource=:memory:");
    private readonly WebApplicationFactory<Program> _host;

    public ManualTime Time { get; } = new();
    public FakeDatabases Databases { get; } = new();

    public DemoHost(params (string Key, string Value)[] settings)
    {
        _control.Open();
        _host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            // Only UseSetting is visible before the host is built.
            builder.UseSetting(DemoMode.EnabledKey, "true");
            builder.UseSetting(CarnivalApp.Api.Identity.IdentitySetup.ProviderConfig, "Demo");
            builder.UseSetting($"ConnectionStrings:{DemoMode.ControlConnectionName}", "Host=unused;Database=control");
            builder.UseSetting(DemoMode.ProvisionsPerIpPerHourKey, "1000");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<DemoControlDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<DemoControlDbContext>>();
                services.AddDbContext<DemoControlDbContext>(o => o.UseSqlite(_control));
                services.RemoveAll<IVisitorDatabases>();
                services.AddSingleton<IVisitorDatabases>(Databases);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                var cleanup = services.Single(d => d.ImplementationType == typeof(DemoCleanupService));
                services.Remove(cleanup);
            });
        });
    }

    public IServiceProvider Services => _host.Services;

    public HttpClient Client() => _host.CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = false,
    });

    public void Dispose()
    {
        _host.Dispose();
        _factory.Dispose();
        _control.Dispose();
    }
}

/// <summary>The demo cookie is Secure, so the test client never replays it; these do.</summary>
internal static class DemoHttp
{
    public static string? CookieFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';')[0])
                .FirstOrDefault(pair => pair.StartsWith(DemoAuthentication.CookieName + "="))
            : null;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    public static async Task<string> StartAsync(HttpClient client)
    {
        var response = await SendAsync(client, HttpMethod.Post, "/api/v1/demo/sessions");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return CookieFrom(response)!;
    }
}
