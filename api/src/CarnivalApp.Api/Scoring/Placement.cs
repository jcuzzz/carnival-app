using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Scoring;

// The one ranking implementation: scoring pays points from these ranks and the book prints them.
public static class Placement
{
    public static bool HigherIsBetter(MeasureType measureType) => measureType != MeasureType.Time;

    // Standard competition ranking: ties share the higher place and the next place is skipped.
    public static int[] Rank(IReadOnlyList<decimal> values, bool higherBetter) =>
        Rank([.. values.Select(v => (IReadOnlyList<decimal>)new[] { v })], higherBetter);

    // Each key is an entrant's valid marks best-first, so a tie on the best breaks on the next.
    // Quadratic by design: a field is one heat.
    public static int[] Rank(IReadOnlyList<IReadOnlyList<decimal>> keys, bool higherBetter)
    {
        var ranks = new int[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var rank = 1;
            foreach (var other in keys)
                if (Better(other, keys[i], higherBetter))
                    rank++;
            ranks[i] = rank;
        }
        return ranks;
    }

    private static bool Better(decimal a, decimal b, bool higherBetter) =>
        higherBetter ? a > b : a < b;

    // Lexicographic countback: where one key runs out first, the other's extra valid mark wins.
    private static bool Better(
        IReadOnlyList<decimal> a, IReadOnlyList<decimal> b, bool higherBetter)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i >= a.Count) return false;
            if (i >= b.Count) return true;
            if (Better(a[i], b[i], higherBetter)) return true;
            if (Better(b[i], a[i], higherBetter)) return false;
        }
        return false;
    }
}
