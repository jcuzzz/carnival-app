using CarnivalApp.Api.Carnivals;

namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// One division of an event definition at a carnival. Placings are computed per heat
/// (<see cref="Entry.Heat"/>); the event itself is a time-based ladder with no house points.
/// </summary>
public class CarnivalEvent
{
    public Guid Id { get; set; }

    public Guid CarnivalId { get; set; }
    public Carnival Carnival { get; set; } = null!;

    public Guid EventDefinitionId { get; set; }
    public EventDefinition EventDefinition { get; set; } = null!;

    /// <summary>
    /// One age code, or <see cref="AgeScope.OpenKey"/> for a division open across ages.
    /// </summary>
    public string AgeGroup { get; set; } = AgeScope.OpenKey;

    public CompetitionCategory? Category { get; set; }

    public EventTier Tier { get; set; } = EventTier.Competitive;

    /// <summary>Null (the norm) means the tier decides.</summary>
    public EventScoringMode? ScoringMode { get; set; }

    public bool ScoresPoints { get; set; } = true;

    public EventScoringMode EffectiveScoringMode =>
        ScoringMode ?? (Tier == EventTier.Social ? EventScoringMode.Social : EventScoringMode.Places);

    /// <summary>
    /// Advisory only: no entry, result, eligibility or lifecycle path may read it.
    /// </summary>
    public Guid? ScheduleSlotId { get; set; }
    public ScheduleSlot? ScheduleSlot { get; set; }

    public int? SlotOrder { get; set; }

    /// <summary>
    /// Contested bar heights, ascending metres; append-only — the bar never comes down.
    /// </summary>
    public string? HeightsJson { get; set; }

    /// <summary>2 or 3; null reads as 3. Fixed once the first mark lands.</summary>
    public int? AttemptsPerHeight { get; set; }

    public CarnivalEventStatus Status { get; set; } = CarnivalEventStatus.Scheduled;

    /// <summary>
    /// Concurrency token bumped by every status transition. Also serialises heat minting, since a
    /// heat number belongs to the event — a new writer here adds contention to marshalling.
    /// </summary>
    public int Version { get; set; }

    public ICollection<Entry> Entries { get; set; } = [];

    public ICollection<CarnivalEventOfficial> Officials { get; set; } = [];
}
