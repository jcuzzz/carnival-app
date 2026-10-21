namespace CarnivalApp.Api.Data.Entities;

public class EventDefinition
{
    public Guid Id { get; set; }

    /// <summary>Short stable code, e.g. "SW-50-FREE". Unique.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public required string Sport { get; set; }

    public MeasureType MeasureType { get; set; }

    public required string Units { get; set; }

    /// <summary>
    /// 1-6, or null for a single typed measure. A value makes the series the source of the
    /// result's value and status, so it freezes once results exist.
    /// </summary>
    public int? AttemptCount { get; set; }

    /// <summary>
    /// A Height event's ladder suggestions, <c>{"start":1.20,"step":0.05}</c> metres. The measure,
    /// not this, says the event is contested on the bar.
    /// </summary>
    public string? LadderJson { get; set; }

    /// <summary>2 or 3; null reads as 3. Stamped onto each division when it is created.</summary>
    public int? AttemptsPerHeight { get; set; }

    public EventFormat Format { get; set; } = EventFormat.Individual;

    /// <summary>Null for Individual.</summary>
    public int? TeamSize { get; set; }

    /// <summary>
    /// Ordered relay legs, e.g.
    /// ["Backstroke","Breaststroke","Butterfly","Freestyle"]; null for Individual.
    /// </summary>
    public string? LegsJson { get; set; }

    /// <summary>Multiplies a placing's base points (relays 2).</summary>
    public int ScoringWeight { get; set; } = 1;

    public string? TagsJson { get; set; }

    public string? EligibilityJson { get; set; }

    /// <summary>
    /// Ages × categories keyed by <see cref="CarnivalEvent.AgeGroup"/> code; null when no records.
    /// Never encodes tier: this row is not reconciled, so detection reads the event's tier.
    /// </summary>
    public string? RecordCategoriesJson { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
