namespace CarnivalApp.Api.Data.Entities;

/// <summary>
/// One age × category × tier the carnival contests. The rows
/// must be the full cross product to be read back as axes.
/// </summary>
public class CarnivalDivision
{
    public Guid Id { get; set; }

    public Guid CarnivalId { get; set; }
    public Carnival? Carnival { get; set; }

    /// <summary>Never <see cref="Carnivals.AgeScope.OpenKey"/>.</summary>
    public required string AgeGroup { get; set; }

    public CompetitionCategory Category { get; set; }

    public EventTier Tier { get; set; }

    /// <summary>Position in the age → category → tier enumeration.</summary>
    public int SortOrder { get; set; }
}
