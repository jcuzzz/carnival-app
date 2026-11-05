namespace CarnivalApp.Api.Data.Entities;

public class LocalAccountSetupLink
{
    public Guid Id { get; set; }

    /// <summary>Null for a first-administrator link.</summary>
    public Guid? LocalAccountId { get; set; }

    /// <summary>SHA-256 of the token, lower-case hex; the token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    public SetupLinkPurpose Purpose { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public required string CreatedBySubject { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? UsedAtUtc { get; set; }
    public string? UsedFromAddress { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}
