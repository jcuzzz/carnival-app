namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// What goes off together; owns the lanes (<see cref="Entry.Lane"/> is unique per race) and may
/// hold heats of several events. Named generically so field flights fit too.
/// </summary>
public class Race
{
    public Guid Id { get; set; }

    /// <summary>
    /// The owning relationship, not the slot: a race must exist before any program does.
    /// </summary>
    public Guid CarnivalId { get; set; }
    public Carnival Carnival { get; set; } = null!;

    /// <summary>
    /// Advisory only: nothing in entry, result, eligibility or lifecycle may read it.
    /// </summary>
    public Guid? ScheduleSlotId { get; set; }
    public ScheduleSlot? ScheduleSlot { get; set; }

    /// <summary>Display order only; races run out of order as a matter of course.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Null lets the client name it from its position; nothing derives meaning from it.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The division it was minted for, so an empty race is attributable; not an exclusive claim,
    /// and nothing about placing, scoring or lanes reads it. Null races are found via entries.
    /// </summary>
    public Guid? CarnivalEventId { get; set; }
    public CarnivalEvent? CarnivalEvent { get; set; }

    public ICollection<Entry> Entries { get; set; } = [];
}
