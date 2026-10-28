using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CarnivalApp.Api.Data;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0047 §3: the owner role runs migrations, the least-privilege role serves
/// requests — and with no migrations string configured nothing changes.
/// </summary>
public sealed class DatabaseConnectionStringTests
{
    private const string App = "Host=db;Database=carnival;Username=carnival_app;Password=a";
    private const string Owner = "Host=db;Database=carnival;Username=carnival;Password=o";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Migrations_fall_back_to_the_application_string_when_none_is_configured()
    {
        var config = Config(("ConnectionStrings:CarnivalAppDb", App));
        Assert.Null(DatabaseConnectionStrings.MigrationsOverride(config));
        Assert.Equal(App, DatabaseConnectionStrings.Migrations(config));
        Assert.Equal(App, DatabaseConnectionStrings.Application(config));
    }

    [Fact]
    public void Migrations_string_is_preferred_and_leaves_the_application_string_alone()
    {
        var config = Config(
            ("ConnectionStrings:CarnivalAppDb", App),
            ("ConnectionStrings:CarnivalAppDbMigrations", Owner));
        Assert.Equal(Owner, DatabaseConnectionStrings.MigrationsOverride(config));
        Assert.Equal(Owner, DatabaseConnectionStrings.Migrations(config));
        Assert.Equal(App, DatabaseConnectionStrings.Application(config));
    }

    [Fact]
    public void A_blank_migrations_string_is_treated_as_absent()
    {
        var config = Config(("ConnectionStrings:CarnivalAppDb", App), ("ConnectionStrings:CarnivalAppDbMigrations", "  "));
        Assert.Null(DatabaseConnectionStrings.MigrationsOverride(config));
        Assert.Equal(App, DatabaseConnectionStrings.Migrations(config));
    }

    [Fact]
    public void An_unconfigured_application_string_keeps_the_development_fallback()
    {
        Assert.Equal(DatabaseConnectionStrings.DevelopmentFallback,
            DatabaseConnectionStrings.Application(Config()));
        Assert.Equal(DatabaseConnectionStrings.DevelopmentFallback,
            DatabaseConnectionStrings.Migrations(Config()));
    }
}

/// <summary>
/// The startup migrations context is built outside the host container
/// (ADR-0047 §3), so its options carry the host logger factory — without it EF's
/// "Applying migration" lines never reach the api log.
/// </summary>
public sealed class MigrationContextOptionsTests
{
    private const string Owner = "Host=db;Database=carnival;Username=carnival;Password=o";

    [Fact]
    public void The_migrations_context_logs_through_the_host_logger_factory()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });

        var options = MigrationContextOptions.Build(Owner, loggerFactory);

        Assert.Same(loggerFactory, options.FindExtension<CoreOptionsExtension>()?.LoggerFactory);
    }

    [Fact]
    public void The_migrations_context_uses_the_given_connection_string()
    {
        using var loggerFactory = LoggerFactory.Create(builder => { });

        var options = MigrationContextOptions.Build(Owner, loggerFactory);

        Assert.Equal(Owner, RelationalOptionsExtension.Extract(options).ConnectionString);
    }
}

/// <summary>
/// ADR-0001: PostgreSQL is the only supported deployment target, and the SQLite
/// loop is a Development throwaway built with EnsureCreated — it never runs the
/// migrations, so the append-only audit trigger (ADR-0027) would simply not
/// exist on a deployment that reached it. The auth side already states and
/// ENFORCES the same principle; this is the database side's missing half.
/// </summary>
public sealed class DatabaseProviderGateTests
{
    [Fact]
    public void Sqlite_outside_development_refuses_to_start()
    {
        using var factory = new ApiTestFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            // The provider is read before the host is built, so only UseSetting
            // is visible in time.
            builder.UseEnvironment("Production");
            builder.UseSetting("Database:Provider", "Sqlite");
        });

        var error = Assert.Throws<InvalidOperationException>(() => host.Services);

        Assert.Contains("Development-only", error.Message);
    }

    [Fact]
    public void The_development_loop_still_runs_on_sqlite()
    {
        using var factory = new ApiTestFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Database:Provider", "Sqlite"));

        Assert.NotNull(host.Services);
    }
}
