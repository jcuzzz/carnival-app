using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CarnivalApp.Api.Identity;

/// <summary>
/// Development-only header authentication; honours the headers only when
/// <c>Identity:DevAuth:Enabled</c> is true. Never enable in a school deployment.
/// </summary>
public static class DevAuthentication
{
    public const string Scheme = "DevHeader";
    public const string EnabledKey = "Identity:DevAuth:Enabled";
    public const string SubjectHeader = "X-Dev-Subject";
    public const string RolesHeader = "X-Dev-Roles";
    public const string GroupsHeader = "X-Dev-Groups";

    public const string UpnHeader = "X-Dev-Upn";
}

public sealed class DevAuthenticationBanner(
    IConfiguration configuration, ILogger<DevAuthenticationBanner> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogCritical(
            "DEVELOPMENT HEADER AUTHENTICATION IS REGISTERED ({Scheme}). {Key}={Enabled}. "
            + "Any caller sending {Header} is that user. This must never be a school deployment.",
            DevAuthentication.Scheme, DevAuthentication.EnabledKey,
            configuration.GetValue(DevAuthentication.EnabledKey, false),
            DevAuthentication.SubjectHeader);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Authenticates nobody, so an unconfigured non-Development host answers 401 rather than a
/// "no authentication scheme" 500.
/// </summary>
public static class UnconfiguredIdentity
{
    public const string Scheme = "UnconfiguredIdentity";
}

public class UnconfiguredIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

public class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!configuration.GetValue(DevAuthentication.EnabledKey, false))
            return Task.FromResult(AuthenticateResult.NoResult());

        var subject = Request.Headers[DevAuthentication.SubjectHeader].ToString();
        if (string.IsNullOrWhiteSpace(subject))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject), new(ClaimTypes.Name, subject) };
        // Assignments bind by UPN; a subject that is already an address is its own UPN.
        var upn = Request.Headers[DevAuthentication.UpnHeader].ToString();
        if (string.IsNullOrWhiteSpace(upn) && subject.Contains('@')) upn = subject;
        if (!string.IsNullOrWhiteSpace(upn)) claims.Add(new Claim("upn", upn.Trim()));
        // Same parser as the group→role transformation,
        // so a role name resolves identically in both.
        claims.AddRange(Split(Request.Headers[DevAuthentication.RolesHeader].ToString())
            .Select(r => PlatformRoles.TryParse(r, out var role, out _) ? role.ToString() : null)
            .Where(r => r is not null)
            .Select(r => new Claim(PlatformClaims.Role, r!)));
        claims.AddRange(Split(Request.Headers[DevAuthentication.GroupsHeader].ToString())
            .Select(g => new Claim(PlatformClaims.Groups, g)));

        var identity = new ClaimsIdentity(claims, DevAuthentication.Scheme, ClaimTypes.Name, PlatformClaims.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), DevAuthentication.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
