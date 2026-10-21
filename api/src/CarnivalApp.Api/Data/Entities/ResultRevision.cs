namespace CarnivalApp.Api.Data.Entities;

/// <summary>Append-only: never updated or deleted by application code.</summary>
public class ResultRevision
{
    public Guid Id { get; set; }

    public Guid ResultId { get; set; }
    public Result Result { get; set; } = null!;

    /// <summary>Matches Result.CurrentRevision after the change.</summary>
    public int RevisionNumber { get; set; }

    public required string PriorValuesJson { get; set; }

    public required string NewValuesJson { get; set; }

    public required string ActorSubject { get; set; }

    public string? ActorRole { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public required string Reason { get; set; }
}
