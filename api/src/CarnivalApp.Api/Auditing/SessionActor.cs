using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Auditing;

public static class SessionActor
{
    // "unknown" rather than throwing: a missing subject claim is an
    // IdP misconfiguration that must still leave audit evidence.
    public static AuditActor From(HttpContext ctx) => new(
        ctx.User.GetSubject() ?? "unknown",
        ctx.User.GetPlatformRoles());
}
