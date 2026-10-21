using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarnivalApp.Api.Scoring;

// Relay doubling is not here: it stays on the event (EventDefinition.ScoringWeight).
public sealed class ScoringRules
{
    public string Version { get; init; } = "v1";

    // Places beyond the list score the participation floor.
    public IReadOnlyList<int> PlacePoints { get; init; } = [4, 3, 2];

    public int ParticipationFloor { get; init; } = 1;

    public int DnfPoints { get; init; } = 1;

    public int DnsPoints { get; init; }

    public int DqPoints { get; init; }

    // Flat per entry in a Social-scored event; a DNS earns none.
    public int SocialPerEntryPoints { get; init; } = 1;

    public static readonly ScoringRules Default = new();

    // Before the event weight.
    public int PointsForRank(int rank) =>
        rank >= 1 && rank <= PlacePoints.Count ? PlacePoints[rank - 1] : ParticipationFloor;

    // Points are awards, never penalties. Enforced on write (400) and on read (Parse falls back to
    // the defaults), so a hand-edited blob can never score garbage.
    public static string? Validate(
        IReadOnlyList<int>? placePoints, int? participationFloor,
        int? dnfPoints, int? dnsPoints, int? dqPoints, int? socialPerEntryPoints = null)
    {
        if (placePoints is { Count: > 0 } && placePoints.Any(p => p < 0))
            return "placePoints must be non-negative.";
        if (participationFloor < 0) return "participationFloor must be non-negative.";
        if (dnfPoints < 0) return "dnfPoints must be non-negative.";
        if (dnsPoints < 0) return "dnsPoints must be non-negative.";
        if (dqPoints < 0) return "dqPoints must be non-negative.";
        if (socialPerEntryPoints < 0) return "socialPerEntryPoints must be non-negative.";
        return null;
    }
}

public sealed class CarnivalSettings
{
    public ScoringRules Scoring { get; init; } = ScoringRules.Default;

    public IReadOnlyList<string> AdjustmentCategories { get; init; } = DefaultAdjustmentCategories;

    // Null means no configured limit, never zero; deliberately not defaulted to a guessed pool.
    public int? LaneCount { get; init; }

    // Requires a LaneCount: "the race is full" is the only thing that starts the next one.
    public bool AutoFillLanes { get; init; }

    // Carried through untouched and never read; dropped from a write only when it pushes the blob
    // past MaxSettingsJsonLength.
    public JsonElement? LegacyDivisions { get; init; }

    // SQLite ignores HasMaxLength, so an over-long blob passes local tests and fails only
    // on PostgreSQL.
    public const int MaxSettingsJsonLength = 4000;

    // Also caps an entry's lane: an unbounded laneCount (int.MaxValue) disables lane validation.
    public const int MaxLaneCount = 64;

    // Enforced on write (400) and on read (Parse drops a bad stored value to null).
    public static string? ValidateLaneCount(int? laneCount) => laneCount switch
    {
        <= 0 => "laneCount must be at least 1.",
        > MaxLaneCount => $"laneCount must be no more than {MaxLaneCount}; a venue with more lanes than that is not a venue.",
        _ => null,
    };

    // Pass the resulting pair, so enabling auto-fill alongside the lane count is accepted.
    public static string? ValidateAutoFill(bool autoFillLanes, int? laneCount) =>
        autoFillLanes && laneCount is null
            ? "autoFillLanes needs laneCount: without the venue's lane count there is no full race, "
              + "and no lane count is assumed. Set laneCount in the same request."
            : null;

    public static readonly IReadOnlyList<string> DefaultAdjustmentCategories =
        ["Novelty 1", "Novelty 2", "Novelty 3", "Best Dressed House", "Loudest House"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CarnivalSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return new CarnivalSettings();
        SettingsDocument? doc;
        try { doc = JsonSerializer.Deserialize<SettingsDocument>(settingsJson, JsonOptions); }
        catch (JsonException) { return new CarnivalSettings(); }
        if (doc is null) return new CarnivalSettings();

        var scoring = ScoringRules.Default;
        // Out-of-bounds stored rules fall back whole to the defaults.
        if (doc.Scoring is { } s
            && ScoringRules.Validate(
                s.PlacePoints, s.ParticipationFloor, s.DnfPoints, s.DnsPoints, s.DqPoints,
                s.SocialPerEntryPoints) is null)
            scoring = new ScoringRules
            {
                Version = string.IsNullOrWhiteSpace(s.Version) ? "custom" : s.Version.Trim(),
                PlacePoints = s.PlacePoints is { Count: > 0 } ? s.PlacePoints : ScoringRules.Default.PlacePoints,
                ParticipationFloor = s.ParticipationFloor ?? ScoringRules.Default.ParticipationFloor,
                DnfPoints = s.DnfPoints ?? ScoringRules.Default.DnfPoints,
                DnsPoints = s.DnsPoints ?? ScoringRules.Default.DnsPoints,
                DqPoints = s.DqPoints ?? ScoringRules.Default.DqPoints,
                SocialPerEntryPoints = s.SocialPerEntryPoints ?? ScoringRules.Default.SocialPerEntryPoints,
            };

        var categories = doc.AdjustmentCategories?
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A hand-edited 0 must not become a limit that refuses every lane.
        var laneCount = ValidateLaneCount(doc.LaneCount) is null ? doc.LaneCount : null;
        var autoFillLanes = doc.AutoFillLanes == true;
        return new CarnivalSettings
        {
            Scoring = scoring,
            AdjustmentCategories = categories is { Count: > 0 } ? categories : DefaultAdjustmentCategories,
            LaneCount = laneCount,
            AutoFillLanes = ValidateAutoFill(autoFillLanes, laneCount) is null && autoFillLanes,
            LegacyDivisions = doc.Divisions,
        };
    }

    public string Serialise() => JsonSerializer.Serialize(new SettingsDocument
    {
        LaneCount = LaneCount,
        // Written only when on, so the key costs nothing against the length cap.
        AutoFillLanes = AutoFillLanes ? true : null,
        Scoring = new ScoringDocument
        {
            Version = Scoring.Version,
            PlacePoints = [.. Scoring.PlacePoints],
            ParticipationFloor = Scoring.ParticipationFloor,
            DnfPoints = Scoring.DnfPoints,
            DnsPoints = Scoring.DnsPoints,
            DqPoints = Scoring.DqPoints,
            SocialPerEntryPoints = Scoring.SocialPerEntryPoints,
        },
        AdjustmentCategories = [.. AdjustmentCategories],
        Divisions = LegacyDivisions,
    }, JsonOptions);

    public sealed class SettingsDocument
    {
        public ScoringDocument? Scoring { get; set; }
        public List<string>? AdjustmentCategories { get; set; }

        public int? LaneCount { get; set; }

        public bool? AutoFillLanes { get; set; }

        // Legacy division profile: preserved byte for byte and read by nothing.
        public JsonElement? Divisions { get; set; }
    }

    public sealed class ScoringDocument
    {
        public string? Version { get; set; }
        public List<int>? PlacePoints { get; set; }
        public int? ParticipationFloor { get; set; }
        public int? DnfPoints { get; set; }
        public int? DnsPoints { get; set; }
        public int? DqPoints { get; set; }
        public int? SocialPerEntryPoints { get; set; }
    }
}
