using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Demo;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Idempotency;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.Imports;
using CarnivalApp.Api.School;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.School.json", optional: true, reloadOnChange: true);

// SQLite is a Development-only EnsureCreated loop; the Npgsql migrations never run on it.
var useSqlite = string.Equals(
    builder.Configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);

var demoMode = DemoMode.IsEnabled(builder.Configuration);
if (demoMode && useSqlite)
    throw new InvalidOperationException(
        "Demo:Enabled needs PostgreSQL: each visitor is a database cloned from a template (ADR-0061).");
if (demoMode && IdentitySetup.ResolveProvider(builder.Configuration) != IdentitySetup.IdentityProvider.Demo)
    throw new InvalidOperationException(
        "Demo:Enabled requires Identity:Provider=Demo: a visitor database is chosen by the demo session (ADR-0061).");

if (useSqlite && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "Database:Provider=Sqlite is a Development-only loop (docs/development/local-dev.md). "
        + $"This host is running as {builder.Environment.EnvironmentName}; configure PostgreSQL instead.");

if (useSqlite)
{
    var sqlitePath = builder.Configuration["Database:SqlitePath"];
    if (string.IsNullOrWhiteSpace(sqlitePath))
        sqlitePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CarnivalApp", "dev.db");
    builder.Services.AddDbContext<CarnivalAppDbContext>(options =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sqlitePath))!);
        options.UseSqlite($"Data Source={sqlitePath}");
    });
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<CarnivalAppDbContext>(name: "sqlite");
}
else
{
    // The missing-string check lives after Build(): host-builder configuration is not readable yet.
    var connectionString = DatabaseConnectionStrings.Application(builder.Configuration);
    if (demoMode)
    {
        var poolSize = DemoMode.PoolSize(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<VisitorDatabase>();
        builder.Services.AddSingleton<IDatabaseKey>(sp => sp.GetRequiredService<VisitorDatabase>());
        builder.Services.AddDbContext<CarnivalAppDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<VisitorDatabase>().ConnectionString(connectionString, poolSize)));
        builder.Services.AddDbContext<DemoControlDbContext>(options =>
            options.UseNpgsql(DemoMode.ControlConnectionString(builder.Configuration)));
        builder.Services.AddSingleton<IVisitorDatabases, PostgresVisitorDatabases>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DemoSessions>();
        builder.Services.AddHostedService<DemoCleanupService>();
    }
    else
    {
        builder.Services.AddDbContext<CarnivalAppDbContext>(options =>
            options.UseNpgsql(connectionString));
        builder.Services.AddRetention();
    }
    builder.Services.AddHealthChecks()
        .AddNpgSql(connectionString, name: "postgres");
}

// Nothing validates a bound enum, so an integer outside the declared set must fail at binding.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            namingPolicy: null, allowIntegerValues: false)));

builder.AddPlatformIdentity();

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiErrorAuthorizationResultHandler>();

// Defaults to IsDevelopment(); without it a deployed host answers a bad body with a bodyless 400.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddOpenApi(CarnivalApp.Api.Http.OpenApiConventions.Configure);

builder.Services.Configure<KestrelServerOptions>(options =>
    options.Limits.MaxRequestBodySize = 10L * 1024 * 1024);

builder.Services.AddRateLimiter(options => RateLimiting.Configure(options, demoMode));

builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<IdempotencyService>();
builder.Services.AddScoped<RosterImportService>();
builder.Services.AddScoped<EventTemplateService>();
builder.Services.AddScoped<TemplateReconciler>();
builder.Services.AddScoped<CarnivalApp.Api.Scoring.ScoringService>();

if (!demoMode) builder.Services.AddSingleton<IDatabaseKey, SingleDatabase>();
builder.Services.AddSingleton<SchoolSettingsProvider>();

builder.Services.AddSingleton<RoleAssignmentProvider>();
builder.Services.AddSingleton<IRoleAssignmentSource>(
    sp => sp.GetRequiredService<RoleAssignmentProvider>());

var app = builder.Build();

// The recovery path for a locked-out deployment; a mis-shaped .env entry reads as zero here.
app.Logger.LogInformation(
    "Identity:BootstrapAdministrators: {Count} configured system administrator(s).",
    GroupToRoleClaimsTransformation.BootstrapAdministrators(app.Configuration).Count);

// The Development fallback carries a literal localhost password, so nothing else may run on it.
if (!app.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("CarnivalAppDb")))
    throw new InvalidOperationException(
        "ConnectionStrings:CarnivalAppDb is not configured. Set it in appsettings.School.json or as "
        + "ConnectionStrings__CarnivalAppDb before starting the API.");

var seedSynthetic = useSqlite && app.Environment.IsDevelopment()
    && app.Configuration.GetValue("Database:SeedSyntheticData", false);

if (useSqlite)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
    await db.Database.EnsureCreatedAsync();
}
// Demo: the template is migrated and bootstrapped when it is built; a clone is never migrated in place.
else if (!demoMode && app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
{
    // A configured migrations string means the request-path role cannot create tables.
    var migrationsConnectionString = DatabaseConnectionStrings.MigrationsOverride(app.Configuration);
    if (migrationsConnectionString is not null)
    {
        await using var db = new CarnivalAppDbContext(MigrationContextOptions.Build(
            migrationsConnectionString, app.Services.GetRequiredService<ILoggerFactory>()));
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        app.Logger.LogInformation(
            "Startup migrations pending on the migrations context: {Count} [{Migrations}].",
            pending.Length, string.Join(", ", pending));
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Startup migrations applied: {Count}.", pending.Length);
    }
    else
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        await db.Database.MigrateAsync();
    }
}

if (demoMode)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DemoControlDbContext>().Database.EnsureCreatedAsync();
}
else
    await SchoolSettingsBootstrap.EnsureAsync(
        app.Services, seedSynthetic ? DevelopmentSeeder.SchoolIdentifier : null);

if (seedSynthetic)
{
    using var scope = app.Services.CreateScope();
    await DevelopmentSeeder.SeedAsync(scope.ServiceProvider);
}

// TLS ends at the proxy; the OIDC redirect_uri must be https. Trust only private ranges — an empty
// known-network list accepts a forwarded header from any peer.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
foreach (var network in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8" })
    forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
app.UseForwardedHeaders(forwardedHeaders);

app.UseExceptionHandler(fault => fault.Run(context =>
{
    var (status, code, detail) =
        ApiError.DescribeFault(context.Features.Get<IExceptionHandlerFeature>()?.Error);
    return Results.Json(ApiError.Body(code, detail), statusCode: status).ExecuteAsync(context);
}));

app.UseStatusCodePages(pages => ApiError.DescribeBodylessStatus(pages.HttpContext.Response.StatusCode)
    is { } fault
    ? Results.Json(ApiError.Body(fault.Code, fault.Detail), statusCode: fault.StatusCode)
        .ExecuteAsync(pages.HttpContext)
    : Task.CompletedTask);

if (demoMode) app.UseDemoGuard();

app.UseIdempotencyBuffering();
app.UseAuthentication();

// An unmatched route is a 404, not the fallback policy's 401: the outbox pauses its whole drain on
// AUTHENTICATION_REQUIRED. After UseAuthentication so the endpoint-less OIDC callbacks are handled.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});

app.UseAuthorization();
app.UseRateLimiter(); // After authorisation: every partition key must be a real subject.

var apiV1 = app.MapGroup("/api/v1");

apiV1.AddEndpointFilter(CapturedActor.RejectMalformedHeader);

apiV1.ProducesApiErrors();

apiV1.MapGet("/version", async (SchoolSettingsProvider school, CancellationToken ct) =>
    Results.Ok(new VersionResponse("CarnivalApp", "v1", (await school.GetAsync(ct)).Name)))
    .RequireAuthorization()
    .Produces<VersionResponse>();

apiV1.MapGet("/school/houses", async (SchoolSettingsProvider school, CancellationToken ct) =>
    {
        var marks = (await school.GetAsync(ct)).Houses;
        return Results.Ok(new HousesResponse(marks.Select(mark => mark.Code).ToArray(), marks));
    })
    .RequireAuthorization().Produces<HousesResponse>();

// Anonymous because the sign-in screen paints before a
// session exists; nothing beyond name and colour may join it.
apiV1.MapGet("/branding", async (
        SchoolSettingsProvider school, IDatabaseKey database, IConfiguration config, CancellationToken ct) =>
    {
        // The demo landing page has no visitor database yet, and the template is never connected to.
        if (demoMode && database.Current.Length == 0)
            return Results.Ok(new BrandingResponse(
                config["Demo:SchoolName"] ?? "Example Synthetic College", null));
        var settings = await school.GetAsync(ct);
        return Results.Ok(new BrandingResponse(settings.Name, settings.BrandColour));
    })
    .AllowAnonymous()
    .Produces<BrandingResponse>();

apiV1.MapSchoolSettingsEndpoints();

apiV1.MapRoleAssignmentEndpoints();

apiV1.MapTemplateReconcileEndpoints();

apiV1.MapImportEndpoints();

apiV1.MapEventDefinitionEndpoints();
apiV1.MapCarnivalEndpoints();
apiV1.MapCarnivalEventEndpoints();
apiV1.MapDeviceEndpoints();
apiV1.MapScheduleEndpoints();
apiV1.MapRaceEndpoints();
apiV1.MapEntryEndpoints();
apiV1.MapResultEndpoints();
CarnivalApp.Api.Scoring.StandingsEndpoints.MapStandingsEndpoints(apiV1);
CarnivalApp.Api.Scoring.ProgressEndpoints.MapProgressEndpoints(apiV1);
CarnivalApp.Api.Scoring.AdjustmentEndpoints.MapAdjustmentEndpoints(apiV1);
CarnivalApp.Api.Reporting.ReportEndpoints.MapReportEndpoints(apiV1);
// Outside /reports/ so the service worker may cache it.
CarnivalApp.Api.Records.RecordEndpoints.MapRecordEndpoints(apiV1);

if (demoMode) apiV1.MapDemoEndpoints();

// The dev-auth fallback has no interactive redirect flow.
var oidcEnabled = IdentitySetup.ResolveProvider(app.Configuration)
    is IdentitySetup.IdentityProvider.Entra or IdentitySetup.IdentityProvider.Adfs;
apiV1.MapAuthEndpoints(oidcEnabled);

app.MapHealthChecks("/health").AllowAnonymous();

// A development artefact: the generated client types are committed, so deployments never serve it.
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

app.Run();

public partial class Program;

public sealed record VersionResponse(string Application, string ApiVersion, string School);

public sealed record HousesResponse(IReadOnlyList<string> Houses, IReadOnlyList<HouseMark> HouseMarks);

public sealed record BrandingResponse(string SchoolName, string? BrandColour);
