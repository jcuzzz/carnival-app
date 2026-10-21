using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Auditing;

/// <summary>
/// A queued write drains under whoever is signed in at sync time, so the recorder is the actor the
/// client captured at entry time; audit rows carry both identities.
/// </summary>
public static class CapturedActor
{
    public const string SubjectHeader = "X-Captured-Actor";

    /// <summary>URI-encoded.</summary>
    public const string NameHeader = "X-Captured-Actor-Name";

    /// <summary>Matches <c>AuditEvent.ActorSubject</c>.</summary>
    public const int MaxSubjectLength = 128;

    /// <summary>
    /// Device-asserted, not a claim: control characters would forge log lines. No directory check —
    /// the official may have left the tenant by drain time.
    /// </summary>
    public static bool IsWellFormedSubject(string? subject) =>
        subject is null
        || (subject.Length <= MaxSubjectLength && !subject.Any(char.IsControl));

    public static async ValueTask<object?> RejectMalformedHeader(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        IsWellFormedSubject(Subject(context.HttpContext))
            ? await next(context)
            : ApiError.BadRequest(ErrorCodes.CapturedActorInvalid,
                $"{SubjectHeader} must be at most {MaxSubjectLength} characters and carry no "
                + "control characters.");

    // A differing captured actor's role at capture time
    // is unknown, so it is null rather than guessed.
    public static AuditActor Resolve(HttpContext ctx, AuditActor sessionActor)
    {
        var subject = Subject(ctx) ?? sessionActor.Subject;
        return new AuditActor(subject, subject == sessionActor.Subject ? sessionActor.Role : null);
    }

    public static string? Subject(HttpContext ctx) =>
        NullIfBlank(ctx.Request.Headers[SubjectHeader].ToString());

    public static string? DisplayName(HttpContext ctx)
    {
        var raw = NullIfBlank(ctx.Request.Headers[NameHeader].ToString());
        if (raw is null) return null;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            decoded = raw; // malformed escape — keep the raw evidence
        }
        decoded = new string(decoded.Where(c => !char.IsControl(c)).ToArray());
        return NullIfBlank(
            decoded.Length <= MaxSubjectLength ? decoded : decoded[..MaxSubjectLength]);
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
