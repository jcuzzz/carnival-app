namespace CarnivalApp.Api.Data.Entities;

public enum ScheduleSlotKind
{
    Events,

    /// <summary>
    /// A non-competition program item; it never produces results or house points.
    /// </summary>
    Item,
}

/// <summary>
/// Times are local clock times, not instants: the carnival owns the date and the school the
/// timezone, so a postponed carnival keeps its times. Advisory only — nothing gating reads a slot.
/// </summary>
public class ScheduleSlot
{
    public Guid Id { get; set; }

    public Guid StreamId { get; set; }
    public ScheduleStream Stream { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>Null when the block simply follows the previous one.</summary>
    public TimeOnly? StartTime { get; set; }

    public TimeOnly? EndTime { get; set; }

    public ScheduleSlotKind Kind { get; set; } = ScheduleSlotKind.Events;

    public string? Label { get; set; }

    /// <summary>
    /// A plain string matching a configured adjustment category; never a link to a result.
    /// </summary>
    public string? AdjustmentCategory { get; set; }

    /// <summary>Deleting a slot only unschedules its events.</summary>
    public ICollection<CarnivalEvent> Events { get; set; } = [];

    /// <summary>Deleting a slot only detaches its races.</summary>
    public ICollection<Race> Races { get; set; } = [];
}
