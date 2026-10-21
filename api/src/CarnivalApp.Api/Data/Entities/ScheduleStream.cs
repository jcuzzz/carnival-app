namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// One concurrently-running column of the program. Streams run at the same time, so slot order is
/// meaningful only within a stream — never assume a total order across a carnival.
/// </summary>
public class ScheduleStream
{
    public Guid Id { get; set; }

    public Guid CarnivalId { get; set; }
    public Carnival Carnival { get; set; } = null!;

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public ICollection<ScheduleSlot> Slots { get; set; } = [];
}
