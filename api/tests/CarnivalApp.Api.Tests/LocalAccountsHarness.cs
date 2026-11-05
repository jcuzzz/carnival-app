using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.Identity.LocalAccounts;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The local-account services as <c>AddLocalAccounts()</c> registers them, over a file-backed SQLite
/// database (each scope its own connection, so concurrent work really is concurrent) or any other
/// provider. The clock is manual; the work factor is lowered unless a test asks for the real one.
/// </summary>
internal sealed class LocalAccountsHarness : IAsyncDisposable
{
    public const string StrongPassword = "copper kettle on a quiet river";
    public static readonly IPAddress Office = IPAddress.Parse("10.0.0.10");
    public static readonly IPAddress Elsewhere = IPAddress.Parse("10.0.0.20");

    private readonly string? _directory;

    public ManualTime Time { get; } = new() { Now = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero) };
    public ServiceProvider Services { get; }
    public AuditActor Admin { get; } = new("admin-1", "SystemAdministrator");

    private LocalAccountsHarness(
        Action<DbContextOptionsBuilder> database, string? directory, IDictionary<string, string?>? settings,
        int iterations)
    {
        _directory = directory;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["School:Name"] = "Test Synthetic College",
                ["School:Timezone"] = "Australia/Melbourne",
                ["School:Houses:0"] = "BANKSIA",
                ["School:Houses:1"] = "WATTLE",
                [SetupLinks.SiteAddressConfig] = "carnival.example.org",
            })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<CarnivalAppDbContext>(database);
        services.AddScoped<AuditService>();
        services.AddSingleton<IDatabaseKey, SingleDatabase>();
        services.AddSingleton<SchoolSettingsProvider>();
        services.AddSingleton<RoleAssignmentProvider>();
        services.AddSingleton<TimeProvider>(Time);
        services.AddLocalAccounts();
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = iterations);
        Services = services.BuildServiceProvider();
    }

    public static async Task<LocalAccountsHarness> CreateAsync(
        IDictionary<string, string?>? settings = null, int iterations = 1_000)
    {
        var directory = Path.Combine(Path.GetTempPath(), "carnival-local-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "accounts.db")}";
        var harness = new LocalAccountsHarness(o => o.UseSqlite(connectionString), directory, settings, iterations);
        await using (var scope = harness.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>().Database.EnsureCreatedAsync();
        await SchoolSettingsBootstrap.EnsureAsync(harness.Services);
        return harness;
    }

    /// <summary>The schema must already exist; the school row is created here.</summary>
    public static async Task<LocalAccountsHarness> CreateAsync(
        Action<DbContextOptionsBuilder> database, int iterations = 1_000)
    {
        var harness = new LocalAccountsHarness(database, null, null, iterations);
        await SchoolSettingsBootstrap.EnsureAsync(harness.Services);
        return harness;
    }

    public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task WithAsync(Func<IServiceProvider, Task> work) =>
        WithAsync<bool>(async services =>
        {
            await work(services);
            return true;
        });

    public Task<T> DbAsync<T>(Func<CarnivalAppDbContext, Task<T>> query) =>
        WithAsync(services => query(services.GetRequiredService<CarnivalAppDbContext>()));

    public Task<IssuedAccountLink> CreateAccountAsync(string username, string? displayName = null) =>
        WithAsync(async services =>
        {
            var result = await services.GetRequiredService<LocalAccountAdministration>()
                .CreateAsync(Admin, username, displayName ?? username);
            Assert.True(result.Succeeded, result.ErrorCode);
            return result.Value!;
        });

    public Task<LocalSignInOutcome> CompleteAsync(
        string token, string password = StrongPassword, string? username = null, IPAddress? from = null) =>
        WithAsync(services => services.GetRequiredService<SetupLinks>()
            .CompleteAsync(new SetupCompletion(token, username, null, password), from ?? Office));

    /// <summary>Created by an administrator and set up through its link.</summary>
    public async Task<Guid> ActiveAccountAsync(string username, string password = StrongPassword)
    {
        var issued = await CreateAccountAsync(username);
        Assert.IsType<LocalSignInOutcome.SignedIn>(await CompleteAsync(issued.SetupToken, password));
        return issued.Account.Id;
    }

    public Task<LocalSignInOutcome> SignInAsync(
        string username, string password = StrongPassword, IPAddress? from = null,
        string? code = null, string? recoveryCode = null, string? enrolmentCode = null) =>
        WithAsync(services => services.GetRequiredService<LocalSignInService>().SignInAsync(
            new LocalSignInInput(username, password, code, recoveryCode, enrolmentCode), from ?? Office));

    public Task<(int Exit, string Output, string Error)> RunCommandAsync(params string[] args) =>
        WithAsync(async _ =>
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await AccountsCommand.RunAsync(Services, args, output, error);
            return (exit, output.ToString(), error.ToString());
        });

    public static string CodeFor(string secret, DateTimeOffset at) =>
        Totp.Code(Totp.DecodeBase32(secret)!, Totp.StepAt(at));

    public static void AssertFailed(string expectedCode, LocalSignInOutcome outcome) =>
        Assert.Equal(expectedCode, Assert.IsType<LocalSignInOutcome.Failed>(outcome).ErrorCode);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        if (_directory is null) return;
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
