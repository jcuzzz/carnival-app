namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// Corrections use optimistic concurrency on <see cref="CurrentRevision"/>; every material change
/// appends an immutable <see cref="ResultRevision"/>.
/// </summary>
public class Result
{
    /// <summary>
    /// Client-generated UUIDv7, doubling as the idempotency key. Never database-generated.
    /// </summary>
    public Guid Id { get; set; }

    public Guid EntryId { get; set; }
    public Entry Entry { get; set; } = null!;

    /// <summary>Canonical unit (seconds, metres or count). Null when Status is not Ok.</summary>
    public decimal? MeasuredValue { get; set; }

    /// <summary>
    /// Ordinal series <c>[{"value":4.32},{"foul":true},null]</c> or, on Height, the card
    /// <c>[{"height":1.25,"marks":"O"}]</c> (told apart by shape); MeasuredValue and Status derive.
    /// </summary>
    public string? AttemptsJson { get; set; }

    public int? Place { get; set; }

    public ResultStatus Status { get; set; } = ResultStatus.Ok;

    public ResultLifecycleState LifecycleState { get; set; } = ResultLifecycleState.Verified;

    /// <summary>Also the optimistic-concurrency token.</summary>
    public int CurrentRevision { get; set; } = 1;

    public required string RecordedBySubject { get; set; }

    /// <summary>Device clock at capture; server receipt time is CreatedAtUtc.</summary>
    public DateTimeOffset RecordedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? DeviceId { get; set; }

    public ICollection<ResultRevision> Revisions { get; set; } = [];
}
