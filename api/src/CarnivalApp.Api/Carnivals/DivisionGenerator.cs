using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

public sealed record DivisionSpec(string? AgeGroup, CompetitionCategory? Category, EventTier Tier);

// Pure: never touches the DbContext, so the caller keeps its own transaction and audit.
public static class DivisionGenerator
{
    // The unique index's columns.
    public readonly record struct DivisionKey(
        Guid DefinitionId, string AgeGroup, CompetitionCategory? Category, EventTier Tier);

    public sealed record Outcome(IReadOnlyList<CarnivalEvent> Created, int Skipped);

    public static IEnumerable<DivisionSpec> Matrix(
        IEnumerable<string?> ageGroups,
        IEnumerable<CompetitionCategory> categories,
        IEnumerable<EventTier> tiers) =>
        from age in ageGroups
        from category in categories
        from tier in tiers
        select new DivisionSpec(age, category, tier);

    public static Outcome Generate(
        Guid carnivalId, EventDefinition definition, IEnumerable<DivisionSpec> divisions,
        HashSet<DivisionKey> alreadyScheduled,
        EventScoringMode? scoringMode = null, bool scoresPoints = true)
    {
        var created = new List<CarnivalEvent>();
        var skipped = 0;
        foreach (var division in divisions)
        {
            var age = AgeScope.Normalise(division.AgeGroup);
            // Add, not Contains: a division listed twice in one request must also skip.
            if (!alreadyScheduled.Add(
                    new DivisionKey(definition.Id, age, division.Category, division.Tier)))
            {
                skipped++;
                continue;
            }

            var scheduled = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnivalId,
                EventDefinitionId = definition.Id,
                // An apply schedules against definitions no query can see yet.
                EventDefinition = definition,
                AgeGroup = age,
                Category = division.Category,
                Tier = division.Tier,
                ScoringMode = scoringMode,
                ScoresPoints = scoresPoints,
                AttemptsPerHeight = JumpCard.AttemptsPerHeightFor(definition),
                Status = CarnivalEventStatus.Scheduled,
            };
            created.Add(scheduled);
        }
        return new Outcome(created, skipped);
    }
}
