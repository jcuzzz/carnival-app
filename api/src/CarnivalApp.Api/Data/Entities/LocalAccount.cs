using Microsoft.AspNetCore.Identity;

namespace CarnivalApp.Api.Data.Entities;

public static class LocalAccountLimits
{
    public const int MaxDisplayNameLength = 100;
}

/// <summary>
/// Never deleted, so history stays attributable. The email and phone columns Identity brings are
/// never written.
/// </summary>
public class LocalAccount : IdentityUser<Guid>
{
    public const string SubjectPrefix = "local:";

    public string DisplayName { get; set; } = "";

    public DateTimeOffset? DisabledAtUtc { get; set; }

    /// <summary>The newest accepted authenticator time step; a code at or before it is a replay.</summary>
    public long? LastTotpStep { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string CreatedBySubject { get; set; } = "";

    public static string SubjectFor(Guid id) => SubjectPrefix + id.ToString("D");

    public static bool TryParseSubject(string? subject, out Guid id)
    {
        id = default;
        return subject is not null
            && subject.StartsWith(SubjectPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(subject[SubjectPrefix.Length..], "D", out id);
    }
}
