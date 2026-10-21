using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// In-process host for HTTP-level tests: SQLite in-memory database (schema is
/// provider-clean, ADR-0001) and the config-gated dev-header authentication
/// fallback instead of a live Entra tenant.
/// </summary>
public class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Dictionary<string, string?> _settings;

    /// <summary>
    /// Disarmed unless a test arms it — the hook the concurrency tests (C4) use
    /// to interleave a competing writer at an exact point inside a handler.
    /// </summary>
    public RaceInterceptor Race { get; } = new();

    /// <summary>Adjust before the first CreateClient call (the host builds lazily).</summary>
    public ApiTestFactory With(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    public ApiTestFactory()
    {
        _settings = new Dictionary<string, string?>
        {
            [DevAuthentication.EnabledKey] = "true",
            ["School:Name"] = "Test Synthetic College",
            ["School:Timezone"] = "Australia/Melbourne",
            ["School:Houses:0"] = "BANKSIA",
            ["School:Houses:1"] = "WATTLE",
            ["School:Houses:2"] = "MOUNTAIN ASH",
            ["School:Houses:3"] = "GREVILLEA",
            // appsettings.Development.json colours the four houses for the dev
            // loop; the suite starts unconfigured and a test opts in with With().
            ["School:HouseColours:BANKSIA"] = null,
            ["School:HouseColours:WATTLE"] = null,
            ["School:HouseColours:MOUNTAIN ASH"] = null,
            ["School:HouseColours:GREVILLEA"] = null,
            ["Imports:MappingPath"] = Fixtures.Path("import-mapping.example.json"),
            ["EventTemplates:Path"] = Fixtures.Path("event-templates"),
            // Never connected to (ConfigureServices swaps in SQLite), but present
            // so a host built as Production passes the startup configuration gate.
            ["ConnectionStrings:SportsDb"] =
                "Host=localhost;Port=5432;Database=sports-tests;Username=tests;Password=tests",
            // Effectively off: the suite shares subjects across many exports, and
            // RateLimitingTests arms a real ceiling on its own host.
            [CarnivalApp.Api.Http.RateLimiting.PermitsPerMinuteKey] = "100000",
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<SportsDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SportsDbContext>>();
            _connection.Open();
            services.AddDbContext<SportsDbContext>(o => o.UseSqlite(_connection).AddInterceptors(Race));

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<SportsDbContext>().Database.EnsureCreated();
        });
    }

    /// <summary>Client authenticated via the dev header scheme.</summary>
    public HttpClient CreateClientAs(string subject, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthentication.SubjectHeader, subject);
        if (roles.Length > 0)
            client.DefaultRequestHeaders.Add(DevAuthentication.RolesHeader, string.Join(',', roles));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
