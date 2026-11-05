namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// A local session signed out before its cookie expired, refused from then on. Kept until the
/// cookie could no longer be valid anyway.
/// </summary>
public class EndedLocalSession
{
    public const int MaxSessionIdLength = 64;

    public required string SessionId { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }
}
