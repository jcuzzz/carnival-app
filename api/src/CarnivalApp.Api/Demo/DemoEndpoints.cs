using System.Globalization;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Demo;

public sealed record DemoSessionResponse(DateTimeOffset ExpiresAtUtc);

public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/demo/sessions").AllowAnonymous();

        group.MapPost("", StartAsync)
            .RequireRateLimiting(RateLimiting.DemoProvisionPolicy)
            .Produces<DemoSessionResponse>(StatusCodes.Status200OK)
            .Produces<DemoSessionResponse>(StatusCodes.Status201Created);

        group.MapGet("/me", Me).Produces<DemoSessionResponse>();
    }

    private static async Task<IResult> StartAsync(HttpContext http, DemoSessions sessions, CancellationToken ct)
    {
        http.Request.Cookies.TryGetValue(DemoAuthentication.CookieName, out var presented);
        var result = await sessions.ProvisionAsync(presented, ct);

        switch (result.Outcome)
        {
            case ProvisionOutcome.Existing:
                return Results.Ok(new DemoSessionResponse(DemoSessions.ExpiresAt(result.Session!)));

            case ProvisionOutcome.Created:
                var expiresAt = DemoSessions.ExpiresAt(result.Session!);
                http.Response.Cookies.Append(DemoAuthentication.CookieName, result.Token!,
                    DemoAuthentication.CookieOptions(result.Session!.CreatedAtUtc + DemoMode.AbsoluteLifetime));
                return Results.Json(new DemoSessionResponse(expiresAt), statusCode: StatusCodes.Status201Created);

            case ProvisionOutcome.AtCapacity:
                http.Response.Headers.RetryAfter = "300";
                return ApiError.Status(StatusCodes.Status503ServiceUnavailable, ErrorCodes.DemoAtCapacity,
                    "The demo is full right now. Try again in a few minutes.");

            default:
                http.Response.Headers.RetryAfter = "60";
                return ApiError.Status(StatusCodes.Status429TooManyRequests, ErrorCodes.TooManyRequests,
                    "Many demos are starting at once. Try again in a minute.");
        }
    }

    private static IResult Me(HttpContext http) =>
        http.User.FindFirst(DemoAuthentication.ExpiresAtClaim)?.Value is { } expiresAt
            ? Results.Ok(new DemoSessionResponse(
                DateTimeOffset.Parse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))
            : ApiError.Unauthorized(ErrorCodes.DemoSessionRequired, "No live demo session.");
}

/// <summary>ADR-0061 §3: no scheduled wipe — expired and orphaned sandboxes go on this cadence.</summary>
public sealed class DemoCleanupService(
    DemoSessions sessions, IConfiguration configuration, ILogger<DemoCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, configuration.GetValue(DemoMode.CleanupIntervalMinutesKey, 10));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try
            {
                var ended = await sessions.SweepAsync(stoppingToken);
                if (ended > 0) logger.LogInformation("Demo cleanup dropped {Count} sandbox(es).", ended);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "Demo cleanup failed; retrying next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
