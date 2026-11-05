using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Identity.LocalAccounts;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The school deployment as it ships with no identity provider: Production, the cookie scheme, a
/// file-backed SQLite database (a connection per context, so concurrent requests really are
/// concurrent). <see cref="WithEntra"/> puts Entra beside it, with discovery served statically and
/// every fetch of it counted.
/// </summary>
internal sealed class LocalAccountsHostFactory : WebApplicationFactory<Program>
{
    public const string Password = LocalAccountsHarness.StrongPassword;
    public const string AuthorizationEndpoint = "https://login.test/authorize";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carnival-local-hosts-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _settings = new()
    {
        ["School:Name"] = "Test Synthetic College",
        ["School:Timezone"] = "Australia/Melbourne",
        ["School:Houses:0"] = "BANKSIA",
        ["School:Houses:1"] = "WATTLE",
        ["Imports:MappingPath"] = Fixtures.Path("import-mapping.example.json"),
        ["EventTemplates:Path"] = Fixtures.Path("event-templates"),
        ["ConnectionStrings:CarnivalAppDb"] =
            "Host=localhost;Port=5432;Database=carnival-tests;Username=tests;Password=tests",
        [SetupLinks.SiteAddressConfig] = "carnival.example.org",
    };
    private bool _entra;

    public CountingConfigurationManager Discovery { get; } = new();

    /// <summary>Closed, it holds every password check, so a test can fill the sign-in limiter.</summary>
    public PasswordGate Gate { get; } = new();

    /// <summary>The connection's address, as the proxy in front of the API would present it.</summary>
    public IPAddress? RemoteAddress { get; set; }

    /// <summary>Real time to start with; a test steps it past an authenticator code's 30 seconds.</summary>
    public ManualTime Time { get; } = new() { Now = DateTimeOffset.UtcNow };

    /// <summary>Adjust before the host is first used.</summary>
    public LocalAccountsHostFactory With(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    public LocalAccountsHostFactory WithEntra()
    {
        _entra = true;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment("Production");
        // UseSetting, not ConfigureAppConfiguration: provider selection reads configuration before Build.
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        if (_entra)
        {
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", "8f2b1c4e-6d3a-4f19-9c07-5a1e2b3d4c5f");
            builder.UseSetting("AzureAd:ClientId", "2c9e7a1b-4f60-4d3e-8a52-7b6c1d0e9f34");
            builder.UseSetting("AzureAd:ClientSecret", "test-secret");
            builder.UseSetting("AzureAd:CallbackPath", "/signin-oidc");
        }

        var connectionString = $"Data Source={Path.Combine(_directory, "api.db")}";
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CarnivalAppDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<CarnivalAppDbContext>>();
            services.AddDbContext<CarnivalAppDbContext>(o => o.UseSqlite(connectionString));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            // The unknown-name dummy hash stays at the full work factor whatever this says.
            services.Configure<PasswordHasherOptions>(o => o.IterationCount = 1_000);
            services.RemoveAll<IPasswordHasher<LocalAccount>>();
            services.AddScoped<IPasswordHasher<LocalAccount>>(sp => new GatedPasswordHasher(
                Gate, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PasswordHasherOptions>>()));
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(() => RemoteAddress));
            if (_entra)
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = Discovery);

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>().Database.EnsureCreated();
        });
    }

    public LocalSession Session() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
        BaseAddress = new Uri("https://localhost"),
    }));

    public async Task<T> DbAsync<T>(Func<CarnivalAppDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>());
    }

    /// <summary>Runs the host command in-process; returns its exit code and stdout.</summary>
    public async Task<(int Exit, string Output)> RunAccountsCommandAsync(params string[] args)
    {
        var output = new StringWriter();
        var exit = await AccountsCommand.RunAsync(Services, args, output, new StringWriter());
        return (exit, output.ToString());
    }

    public static string TokenFrom(string commandOutput)
    {
        var start = commandOutput.IndexOf("/#/setup/", StringComparison.Ordinal) + "/#/setup/".Length;
        return commandOutput[start..].Split(' ', 2)[0].Trim();
    }

    /// <summary>The first System administrator, set up through the host command's link.</summary>
    public async Task<LocalSession> FirstAdministratorAsync(string username = "backup-admin")
    {
        var (exit, output) = await RunAccountsCommandAsync("first-admin");
        Assert.Equal(AccountsCommand.Done, exit);
        var session = Session();
        var response = await session.PostAsync("/api/v1/auth/setup/complete",
            new { token = TokenFrom(output), username, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return session;
    }

    /// <summary>Created by <paramref name="admin"/>; returns its id and the setup token.</summary>
    public static async Task<(Guid Id, string Token)> CreateAccountAsync(LocalSession admin, string username)
    {
        var response = await admin.PostAsync("/api/v1/admin/local-accounts",
            new { username, displayName = $"Person {username}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        return (body.GetProperty("account").GetProperty("id").GetGuid(), body.GetProperty("setupToken").GetString()!);
    }

    /// <summary>Created by <paramref name="admin"/> and set up through its link, in a session of its own.</summary>
    public async Task<(Guid Id, LocalSession Session)> ActiveAccountAsync(LocalSession admin, string username)
    {
        var (id, token) = await CreateAccountAsync(admin, username);
        var session = Session();
        var response = await session.PostAsync("/api/v1/auth/setup/complete", new { token, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (id, session);
    }

    public async Task<LocalSession> SignInAsync(string username, string password = Password)
    {
        var session = Session();
        var response = await session.PostAsync("/api/v1/auth/local", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("signedIn", (await response.JsonAsync()).GetProperty("status").GetString());
        return session;
    }

    /// <summary>Through the settings endpoint, so the settings cache is refreshed as in use.</summary>
    public static async Task SetSignInSettingsAsync(
        LocalSession admin, string? localAccounts = null, bool? requireAuthenticator = null)
    {
        var current = await (await admin.GetAsync("/api/v1/school/settings")).JsonAsync();
        var response = await admin.PutAsync("/api/v1/school/settings", new
        {
            name = current.GetProperty("name").GetString(),
            houses = current.GetProperty("houses").EnumerateArray()
                .Select(h => new { code = h.GetProperty("code").GetString() }).ToArray(),
            updatedAtUtc = current.GetProperty("updatedAtUtc").GetString(),
            localAccounts,
            requireAuthenticator,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
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

/// <summary>Discovery served from memory; <see cref="Fetches"/> counts every use of it.</summary>
internal sealed class CountingConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly StaticConfigurationManager<OpenIdConnectConfiguration> _inner = new(new OpenIdConnectConfiguration
    {
        Issuer = "https://login.test/",
        AuthorizationEndpoint = LocalAccountsHostFactory.AuthorizationEndpoint,
        TokenEndpoint = "https://login.test/token",
        EndSessionEndpoint = "https://login.test/logout",
    });

    private int _fetches;

    public int Fetches => Volatile.Read(ref _fetches);

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        Interlocked.Increment(ref _fetches);
        return _inner.GetConfigurationAsync(cancel);
    }

    public void RequestRefresh() => _inner.RequestRefresh();
}

/// <summary>
/// One browser's session cookie, kept by hand so a test can read and replay it; requests carry
/// <c>Sec-Fetch-Site: same-origin</c> as the app's own fetches do.
/// </summary>
internal sealed class LocalSession(HttpClient client)
{
    public const string CookieName = CarnivalApp.Api.Identity.IdentitySetup.SessionCookieName;

    public HttpClient Client { get; } = client;

    public string? Cookie { get; set; }

    public List<string> SetCookies { get; } = [];

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (Cookie is not null) request.Headers.Add("Cookie", $"{CookieName}={Cookie}");
        if (!request.Headers.Contains("Sec-Fetch-Site")) request.Headers.Add("Sec-Fetch-Site", "same-origin");
        var response = await Client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values.Where(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal)))
            {
                SetCookies.Add(value);
                var cookie = value[(CookieName.Length + 1)..].Split(';', 2)[0];
                Cookie = cookie.Length == 0 ? null : cookie;
            }
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(new HttpRequestMessage(HttpMethod.Get, path));

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) });

    public Task<HttpResponseMessage> PutAsync(string path, object body) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) });

    public LocalSession CopyTo(HttpClient client) => new(client) { Cookie = Cookie };
}

internal static class ResponseJson
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

/// <summary>Open unless a test closes it; <see cref="Held"/> counts the checks waiting at it.</summary>
internal sealed class PasswordGate
{
    private readonly ManualResetEventSlim _open = new(initialState: true);
    private int _held;

    public int Held => Volatile.Read(ref _held);

    public void Close() => _open.Reset();

    public void Open() => _open.Set();

    public void Pass()
    {
        if (_open.IsSet) return;
        Interlocked.Increment(ref _held);
        _open.Wait(TimeSpan.FromMinutes(1));
        Interlocked.Decrement(ref _held);
    }
}

internal sealed class GatedPasswordHasher(
    PasswordGate gate, Microsoft.Extensions.Options.IOptions<PasswordHasherOptions> options)
    : IPasswordHasher<LocalAccount>
{
    private readonly PasswordHasher<LocalAccount> _inner = new(options);

    public string HashPassword(LocalAccount user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(LocalAccount user, string hashedPassword, string providedPassword)
    {
        gate.Pass();
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

internal sealed class RemoteAddressFilter(Func<IPAddress?> address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(
        Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, inner) =>
        {
            if (address() is { } remote) context.Connection.RemoteIpAddress = remote;
            await inner(context);
        });
        next(app);
    };
}
