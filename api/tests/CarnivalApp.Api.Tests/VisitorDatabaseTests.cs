using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0061 §1: a demo host serves one database per visitor, so nothing cached for one database
/// may be served for another. SQLite stands in for the per-visitor Postgres databases.
/// </summary>
public sealed class VisitorDatabaseTests : IDisposable
{
    private readonly string _a = VisitorDatabase.NewName();
    private readonly string _b = VisitorDatabase.NewName();
    private readonly Dictionary<string, SqliteConnection> _databases = [];
    private readonly HttpContextAccessor _accessor = new();
    private readonly ServiceProvider _services;

    public VisitorDatabaseTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(_accessor);
        services.AddSingleton<VisitorDatabase>();
        services.AddSingleton<IDatabaseKey>(sp => sp.GetRequiredService<VisitorDatabase>());
        services.AddDbContext<CarnivalAppDbContext>((sp, options) =>
            options.UseSqlite(_databases[sp.GetRequiredService<VisitorDatabase>().Selected!]));
        services.AddSingleton<SchoolSettingsProvider>();
        services.AddSingleton<RoleAssignmentProvider>();
        _services = services.BuildServiceProvider();

        foreach (var name in new[] { _a, _b })
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            _databases[name] = connection;
            Use(name, db =>
            {
                db.Database.EnsureCreated();
                db.SchoolConfigurations.Add(new SchoolConfiguration
                {
                    SchoolIdentifier = "demo", Name = $"School {name}", Timezone = "Australia/Melbourne",
                });
                db.RoleAssignments.Add(new RoleAssignment
                {
                    SubjectUpn = $"{name}@example.test", Role = PlatformRole.SystemAdministrator,
                    Reason = "visitor", CreatedBySubject = "test",
                });
                db.SaveChanges();
            });
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        foreach (var connection in _databases.Values) connection.Dispose();
    }

    private void Select(string name)
    {
        var context = new DefaultHttpContext();
        VisitorDatabase.Select(context, name);
        _accessor.HttpContext = context;
    }

    private void Use(string name, Action<CarnivalAppDbContext> act)
    {
        Select(name);
        using var scope = _services.CreateScope();
        act(scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
    }

    private void Rename(string name, string schoolName) => Use(name, db =>
    {
        db.SchoolConfigurations.Single().Name = schoolName;
        db.SaveChanges();
    });

    private async Task<string> SchoolNameAsync(string name)
    {
        Select(name);
        return (await _services.GetRequiredService<SchoolSettingsProvider>().GetAsync()).Name;
    }

    private async Task<string> AssignedUpnAsync(string name)
    {
        Select(name);
        return Assert.Single(await _services.GetRequiredService<RoleAssignmentProvider>().GetActiveAsync())
            .SubjectUpn;
    }

    [Fact]
    public async Task School_settings_are_cached_per_database()
    {
        Assert.Equal($"School {_a}", await SchoolNameAsync(_a));
        Assert.Equal($"School {_b}", await SchoolNameAsync(_b));
        Assert.Equal($"School {_a}", await SchoolNameAsync(_a));
    }

    [Fact]
    public async Task Invalidating_school_settings_reaches_only_the_selected_database()
    {
        await SchoolNameAsync(_a);
        await SchoolNameAsync(_b);
        Rename(_a, "A renamed");
        Rename(_b, "B renamed");

        Select(_a);
        _services.GetRequiredService<SchoolSettingsProvider>().Invalidate();

        Assert.Equal("A renamed", await SchoolNameAsync(_a));
        Assert.Equal($"School {_b}", await SchoolNameAsync(_b));
    }

    [Fact]
    public async Task Forgetting_a_database_drops_its_school_settings()
    {
        await SchoolNameAsync(_a);
        Rename(_a, "A renamed");

        _services.GetRequiredService<SchoolSettingsProvider>().Forget(_a);

        Assert.Equal("A renamed", await SchoolNameAsync(_a));
    }

    [Fact]
    public async Task Role_assignments_are_cached_per_database()
    {
        Assert.Equal($"{_a}@example.test", await AssignedUpnAsync(_a));
        Assert.Equal($"{_b}@example.test", await AssignedUpnAsync(_b));
        Assert.Equal($"{_a}@example.test", await AssignedUpnAsync(_a));
    }

    [Fact]
    public async Task A_failed_read_never_serves_another_databases_last_good_assignments()
    {
        await AssignedUpnAsync(_a);

        Select(VisitorDatabase.NewName());
        var unreadable = await _services.GetRequiredService<RoleAssignmentProvider>().GetActiveAsync();

        Assert.Empty(unreadable);
    }

    [Theory]
    [InlineData("demo_v_0123456789abcdef0123456789abcdef", true)]
    [InlineData("demo_v_0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("demo_v_0123456789abcdef", false)]
    [InlineData("carnival", false)]
    [InlineData("carnival_demo_template", false)]
    [InlineData("demo_v_0123456789abcdef0123456789abcdef;Database=carnival", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_generated_visitor_names_are_accepted(string? name, bool valid)
    {
        Assert.Equal(valid, VisitorDatabase.IsValidName(name));
        if (!valid)
            Assert.Throws<ArgumentException>(() => VisitorDatabase.Select(new DefaultHttpContext(), name!));
    }

    [Fact]
    public void A_generated_name_is_valid()
    {
        Assert.True(VisitorDatabase.IsValidName(VisitorDatabase.NewName()));
    }

    [Fact]
    public void The_connection_string_names_the_visitor_database_with_a_small_draining_pool()
    {
        var built = new NpgsqlConnectionStringBuilder(VisitorDatabase.ForDatabase(
            "Host=db;Database=carnival_demo_control;Username=app;Password=x", _a, poolSize: 3));

        Assert.Equal(_a, built.Database);
        Assert.Equal("db", built.Host);
        Assert.Equal(0, built.MinPoolSize);
        Assert.Equal(3, built.MaxPoolSize);
    }

    [Fact]
    public void Without_a_selected_visitor_no_connection_string_is_produced()
    {
        _accessor.HttpContext = new DefaultHttpContext();
        var visitor = _services.GetRequiredService<VisitorDatabase>();

        Assert.Throws<InvalidOperationException>(() => visitor.ConnectionString("Host=db;Database=carnival", 3));
        Assert.Equal("", visitor.Current);
    }

    [Fact]
    public void Demo_mode_refuses_the_SQLite_loop()
    {
        using var factory = new ApiTestFactory();
        // Only UseSetting is visible before the host is built.
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(DemoMode.EnabledKey, "true");
            builder.UseSetting("Database:Provider", "Sqlite");
        });

        var error = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("Demo:Enabled needs PostgreSQL", error.ToString());
    }
}
