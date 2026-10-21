namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Inserted in the same transaction as the write it protects, and only for a 2xx — a refused
/// submission records nothing, so its key stays reusable.
/// </summary>
public class IdempotencyKey
{
    public Guid Key { get; set; }

    /// <summary>SHA-256 of the canonicalised request payload.</summary>
    public required string PayloadHash { get; set; }

    public int ResponseStatusCode { get; set; }

    /// <summary>Returned verbatim on retry.</summary>
    public required string ResponseBody { get; set; }

    public required string ActorSubject { get; set; }

    public string? DeviceId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
