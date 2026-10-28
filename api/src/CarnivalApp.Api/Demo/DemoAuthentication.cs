using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Demo;

public static class DemoAuthentication
{
    public const string Scheme = "DemoSession";
    public const string CookieName = "carnival_demo";
    public const string ExpiresAtClaim = "demo_expires_at";

    public static CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        // Strict is the CSRF control: the demo has no cross-site sign-in callback.
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
        IsEssential = true,
    };

    /// <summary>
    /// Refuses every other identity source: a demo host must have exactly one (ADR-0061 §2).
    /// </summary>
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        if (!DemoMode.IsEnabled(configuration))
            throw new InvalidOperationException(
                $"Identity:Provider=Demo requires {DemoMode.EnabledKey}=true (ADR-0061).");

        string?[] others =
        [
            configuration[$"{IdentitySetup.AzureAdSection}:TenantId"],
            configuration[$"{IdentitySetup.AzureAdSection}:ClientId"],
            configuration[$"{IdentitySetup.AdfsSection}:Authority"],
            configuration[$"{IdentitySetup.AdfsSection}:ClientId"],
        ];
        if (configuration.GetValue(DevAuthentication.EnabledKey, false)
            || others.Any(value => !string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException(
                "DEMO_COEXISTENCE_FORBIDDEN: Identity:Provider=Demo cannot run with dev header "
                + "authentication or any Entra/ADFS configuration (ADR-0061 §2).");

        services
            .AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, DemoSessionHandler>(Scheme, null);
        services.AddHostedService<DemoIdentityBanner>();
    }
}

public sealed class DemoSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DemoSessions sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(DemoAuthentication.CookieName, out var token)
            || string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var session = await sessions.FindLiveAsync(token, Context.RequestAborted);
        if (session is null) return AuthenticateResult.NoResult();

        // Before the claims transformation, which reads role assignments from this database.
        VisitorDatabase.Select(Context, session.DatabaseName);

        var subject = $"demo-{session.Id:N}";
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Name, "Demo visitor"),
            new(PlatformClaims.Role, nameof(PlatformRole.SystemAdministrator)),
            new(DemoAuthentication.ExpiresAtClaim,
                DemoSessions.ExpiresAt(session).ToString("O", CultureInfo.InvariantCulture)),
        ];
        var identity = new ClaimsIdentity(claims, DemoAuthentication.Scheme, ClaimTypes.Name, PlatformClaims.Role);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), DemoAuthentication.Scheme));
    }
}

public sealed class DemoIdentityBanner(ILogger<DemoIdentityBanner> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogCritical(
            "DEMO IDENTITY ACTIVE ({Scheme}) — no real authentication. Every visitor is "
            + "SystemAdministrator of their own database. This must never be a school deployment.",
            DemoAuthentication.Scheme);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
