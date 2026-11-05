using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Identity.LocalAccounts;

/// <summary>
/// A local account's session is the same cookie a provider sign-in writes. Unlike a provider's, it
/// is checked against the account on every request, and signing out ends it on the server.
/// </summary>
public static class LocalSessions
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static ClaimsPrincipal PrincipalFor(LocalAccount account, string sessionId) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", LocalAccount.SubjectFor(account.Id)),
                new Claim(ClaimTypes.Name, account.DisplayName),
                new Claim(PlatformClaims.LocalUsername, account.UserName!),
                new Claim(PlatformClaims.LocalStamp, account.SecurityStamp ?? ""),
                new Claim(PlatformClaims.LocalSessionId, sessionId),
            ],
            PlatformClaims.LocalAuthenticationType, ClaimTypes.Name, PlatformClaims.Role));

    public static bool TryGetAccountId(this ClaimsPrincipal user, out Guid id)
    {
        id = default;
        return user.IsLocalSession() && LocalAccount.TryParseSubject(user.FindFirstValue("sub"), out id);
    }

    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Returns the new session's id.</summary>
    public static async Task<string> SignInAsync(HttpContext context, LocalAccount account)
    {
        var sessionId = NewSessionId();
        await context.SignInAsync(Scheme, PrincipalFor(account, sessionId));
        return sessionId;
    }

    /// <summary>
    /// Re-issues the caller's cookie after their own change rotated the stamp, keeping the session id
    /// and the original issue and expiry times, so the session does not outlive its 12 hours.
    /// </summary>
    public static async Task RefreshAsync(HttpContext context, LocalAccount account)
    {
        var current = await context.AuthenticateAsync(Scheme);
        var properties = new AuthenticationProperties
        {
            IssuedUtc = current.Properties?.IssuedUtc,
            ExpiresUtc = current.Properties?.ExpiresUtc,
            IsPersistent = current.Properties?.IsPersistent ?? false,
        };
        var sessionId = current.Principal?.FindFirstValue(PlatformClaims.LocalSessionId) ?? NewSessionId();
        await context.SignInAsync(Scheme, PrincipalFor(account, sessionId), properties);
    }

    /// <summary>
    /// Records the caller's local session as ended, so a copy of its cookie is refused, and clears the
    /// cookie. Any other session clears its cookie only.
    /// </summary>
    public static async Task SignOutAsync(HttpContext context)
    {
        if (context.User.IsLocalSession()
            && context.User.FindFirstValue(PlatformClaims.LocalSessionId) is { Length: > 0 } sessionId)
        {
            var services = context.RequestServices;
            var expires = (await context.AuthenticateAsync(Scheme)).Properties?.ExpiresUtc
                ?? services.GetRequiredService<TimeProvider>().GetUtcNow()
                + services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Scheme).ExpireTimeSpan;
            var db = services.GetRequiredService<CarnivalAppDbContext>();
            db.EndedLocalSessions.Add(new EndedLocalSession { SessionId = sessionId, ExpiresAtUtc = expires });
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateException)
            {
                // Two sign-outs of one session raced; the first one's row stands.
                db.ChangeTracker.Clear();
            }
        }
        await context.SignOutAsync(Scheme);
    }

    /// <summary>
    /// Whether the request's cookie was issued to a local account, read even when it has expired or
    /// been refused, so signing out never sends a local user to the provider.
    /// </summary>
    public static bool CookieIsLocal(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(Scheme);
        var cookie = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        if (string.IsNullOrEmpty(cookie)) return false;
        return options.TicketDataFormat.Unprotect(cookie)?.Principal.Identity?.AuthenticationType
            == PlatformClaims.LocalAuthenticationType;
    }

    /// <summary>Chained after any validator the provider installed; never renews the cookie.</summary>
    public static void Apply(CookieAuthenticationOptions options)
    {
        var inner = options.Events.OnValidatePrincipal;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (inner is not null) await inner(context);
            if (context.Principal?.IsLocalSession() == true) await ValidateAsync(context);
        };
    }

    // A database fault propagates as a 500 rather than signing the person out.
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var ct = context.HttpContext.RequestAborted;
        var sessionId = context.Principal!.FindFirstValue(PlatformClaims.LocalSessionId);
        if (context.Principal!.TryGetAccountId(out var id) && !string.IsNullOrEmpty(sessionId))
        {
            var db = services.GetRequiredService<CarnivalAppDbContext>();
            var account = await db.LocalAccounts.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => new { a.SecurityStamp, a.DisabledAtUtc })
                .FirstOrDefaultAsync(ct);
            var stamp = context.Principal!.FindFirstValue(PlatformClaims.LocalStamp);
            if (account is { DisabledAtUtc: null }
                && !string.IsNullOrEmpty(stamp)
                && string.Equals(account.SecurityStamp, stamp, StringComparison.Ordinal)
                && !await db.EndedLocalSessions.AsNoTracking().AnyAsync(e => e.SessionId == sessionId, ct)
                && await services.GetRequiredService<LocalAccountPolicy>().IsPermittedAsync(id, ct))
                return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(Scheme);
    }
}
