using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

// Age codes must match the roster import's derived codes exactly: eligibility compares them by
// string equality.
public sealed class EventTemplate
{
    public required string Key { get; init; }
    public required string Name { get; init; }

    // The source file name, which never moves; ContentHash tells a rewritten document apart.
    public required string Version { get; init; }

    public required string ContentHash { get; init; }

    public required IReadOnlyList<string> AgeGroups { get; init; }

    public required IReadOnlyList<CompetitionCategory> Categories { get; init; }

    public required IReadOnlyList<EventTier> Tiers { get; init; }

    public required IReadOnlyList<EventTemplateDefinition> Definitions { get; init; }

    // Empty without a schedule section; the events are then simply unscheduled.
    public IReadOnlyList<EventTemplateStream> Streams { get; init; } = [];

    public IReadOnlyList<EventTier> TiersFor(EventTemplateDefinition definition) =>
        definition.Tiers ?? Tiers;

    public static EventTemplate LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, Path.GetFileName(path));
    }

    public static EventTemplate Load(Stream json, string sourceName)
    {
        using var buffer = new MemoryStream();
        json.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var doc = JsonSerializer.Deserialize<TemplateDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"Event template '{sourceName}' is empty.");
        if (string.IsNullOrWhiteSpace(doc.Template))
            throw new InvalidDataException($"Event template '{sourceName}' must declare a template key.");
        if (doc.Definitions is null or { Count: 0 })
            throw new InvalidDataException($"Event template '{sourceName}' defines no event definitions.");

        var ageGroups = ReadAgeGroups(doc, sourceName);
        var categories = ReadCategories(doc, sourceName);
        var tiers = doc.Divisions?.Tiers is null
            ? DefaultTiers
            : ReadTiers(doc.Divisions.Tiers, sourceName, "divisions.tiers");

        var definitions = doc.Definitions.Select(d =>
        {
            if (string.IsNullOrWhiteSpace(d.Code) || string.IsNullOrWhiteSpace(d.Name)
                || string.IsNullOrWhiteSpace(d.Sport) || string.IsNullOrWhiteSpace(d.Units))
                throw new InvalidDataException(
                    $"Event template '{sourceName}': every definition needs code, name, sport and units.");

            List<string>? legs = null;
            if (d.Format == EventFormat.Team)
            {
                if (d.TeamSize is not > 0)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': team event '{d.Code}' needs a positive teamSize.");
                legs = d.Legs?.Select(l => l.Trim()).ToList();
                if (legs is null || legs.Count != d.TeamSize)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': team event '{d.Code}' needs exactly teamSize ({d.TeamSize}) legs.");
            }
            else if (d.TeamSize is not null || d.Legs is not null)
            {
                throw new InvalidDataException(
                    $"Event template '{sourceName}': individual event '{d.Code}' must not set teamSize/legs.");
            }

            if (d.Attempts is { } attempts)
            {
                if (attempts is < 1 or > 6)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': event '{d.Code}' needs attempts between 1 and 6.");
                // Height uses the bar ladder, never a series.
                if (d.MeasureType is MeasureType.Time or MeasureType.Height)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': {d.MeasureType} event '{d.Code}' cannot set attempts.");
                // A relay is one performance by a house: no athlete for a series to belong to.
                if (d.Format != EventFormat.Individual)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': team event '{d.Code}' cannot set attempts.");
            }

            if (d.Ladder is { } ladder)
            {
                if (d.MeasureType != MeasureType.Height)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': event '{d.Code}' sets a ladder, which belongs to a Height event.");
                if (d.Format != EventFormat.Individual)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': team event '{d.Code}' cannot set a ladder.");
                if (!JumpCard.IsHeight(ladder.Start) || !JumpCard.IsStep(ladder.Step))
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': event '{d.Code}' needs a ladder start and step in whole centimetres.");
            }

            if (d.AttemptsPerHeight is { } perHeight)
            {
                if (d.MeasureType != MeasureType.Height)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': event '{d.Code}' sets attemptsPerHeight, which belongs to a Height event.");
                if (!JumpCard.IsAttemptsPerHeight(perHeight))
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': event '{d.Code}' needs attemptsPerHeight of 2 or 3.");
            }

            var tags = d.Tags?
                .Select(t => t?.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Select(t => t!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new EventTemplateDefinition
            {
                Code = d.Code.Trim(),
                Name = d.Name.Trim(),
                Sport = d.Sport.Trim(),
                MeasureType = d.MeasureType,
                AttemptCount = d.Attempts,
                Ladder = d.Ladder,
                AttemptsPerHeight = d.AttemptsPerHeight,
                Units = d.Units.Trim(),
                Active = d.Active,
                SchoolRecords = d.SchoolRecords,
                Format = d.Format,
                TeamSize = d.TeamSize,
                Legs = legs,
                ScoringWeight = d.ScoringWeight <= 0 ? 1 : d.ScoringWeight,
                OpenDivision = d.OpenDivision,
                Tags = tags is { Count: > 0 } ? tags : null,
                // Need not be a subset of the template's tiers: it states where this event runs.
                Tiers = d.Tiers is null
                    ? null
                    : ReadTiers(d.Tiers, sourceName, $"definition '{d.Code}' tiers"),
            };
        }).ToList();
        if (definitions.DistinctBy(d => d.Code, StringComparer.OrdinalIgnoreCase).Count() != definitions.Count)
            throw new InvalidDataException($"Event template '{sourceName}' contains duplicate definition codes.");

        return new EventTemplate
        {
            Key = doc.Template.Trim(),
            Name = string.IsNullOrWhiteSpace(doc.Name) ? doc.Template.Trim() : doc.Name.Trim(),
            Version = sourceName,
            ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            AgeGroups = ageGroups,
            Categories = categories,
            Tiers = tiers,
            Definitions = definitions,
            Streams = ReadStreams(doc, sourceName, definitions, ageGroups, categories, tiers),
        };
    }

    // Validated at load because every failure is otherwise silent until deck day. Labels must be
    // unique per stream: a re-apply recognises its slots by label.
    private static IReadOnlyList<EventTemplateStream> ReadStreams(
        TemplateDocument doc, string sourceName, IReadOnlyList<EventTemplateDefinition> definitions,
        IReadOnlyList<string> ageGroups, IReadOnlyList<CompetitionCategory> categories,
        IReadOnlyList<EventTier> tiers)
    {
        if (doc.Schedule?.Streams is not { } declared) return [];
        if (declared.Count == 0)
            throw new InvalidDataException($"Event template '{sourceName}': schedule.streams is empty.");

        var streams = declared.Select((stream, streamIndex) =>
        {
            var name = stream.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException(
                    $"Event template '{sourceName}': every schedule stream needs a name.");

            var slots = (stream.Slots ?? []).Select((slot, slotIndex) =>
            {
                var label = slot.Label?.Trim();
                if (string.IsNullOrEmpty(label))
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': every slot in stream '{name}' needs a label " +
                        "(it is both the line printed on the program and the slot's identity on re-apply).");

                var placements = (slot.Events ?? [])
                    .Select(e => ReadPlacement(e, sourceName, label, ageGroups, categories, tiers))
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .ToList();
                foreach (var placement in placements)
                    if (!definitions.Any(d =>
                            string.Equals(d.Code, placement.Code, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException(
                            $"Event template '{sourceName}': slot '{label}' names an unknown event " +
                            $"definition '{placement.Code}'.");
                if (placements.DistinctBy(p => p.Code, StringComparer.OrdinalIgnoreCase).Count()
                    != placements.Count)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': slot '{label}' names the same event definition twice.");

                var start = ReadTime(slot.Start, sourceName, $"slot '{label}' start");
                var end = ReadTime(slot.End, sourceName, $"slot '{label}' end");
                if (end is not null && start is null)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': slot '{label}' has an end time but no start time.");
                if (start is { } from && end is { } to && to < from)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': slot '{label}' ends before it starts.");

                var category = slot.AdjustmentCategory?.Trim();
                if (placements.Count > 0 && !string.IsNullOrEmpty(category))
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': slot '{label}' lists events AND an adjustment " +
                        "category — a novelty item scores through ADR-0005 adjustments, never through results.");

                return new EventTemplateSlot(
                    label, start, end, placements,
                    string.IsNullOrEmpty(category) ? null : category,
                    slotIndex);
            }).ToList();

            if (slots.DistinctBy(s => s.Label, StringComparer.OrdinalIgnoreCase).Count() != slots.Count)
                throw new InvalidDataException(
                    $"Event template '{sourceName}': stream '{name}' contains duplicate slot labels.");
            return new EventTemplateStream(name, streamIndex, slots);
        }).ToList();

        if (streams.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Count() != streams.Count)
            throw new InvalidDataException(
                $"Event template '{sourceName}' contains duplicate schedule stream names.");
        return streams;
    }

    // A bare code, or { code, ages?, tiers?, categories? } naming the template's own divisions;
    // a mistyped filter would otherwise print an empty block on carnival day.
    private static EventTemplateSlotPlacement? ReadPlacement(
        JsonElement entry, string sourceName, string label,
        IReadOnlyList<string> ageGroups, IReadOnlyList<CompetitionCategory> categories,
        IReadOnlyList<EventTier> tiers)
    {
        string Where(string what) => $"Event template '{sourceName}': slot '{label}' {what}";

        if (entry.ValueKind == JsonValueKind.String)
        {
            var bare = entry.GetString()?.Trim();
            return string.IsNullOrEmpty(bare)
                ? null
                : new EventTemplateSlotPlacement(bare, null, null, null);
        }
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(Where(
                "lists an event that is neither a definition code nor a " +
                "{ code, ages?, tiers?, categories? } object."));

        var code = entry.TryGetProperty("code", out var codeElement)
                   && codeElement.ValueKind == JsonValueKind.String
            ? codeElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(code))
            throw new InvalidDataException(Where("lists an event object with no code."));

        var ages = ReadFilter(entry, "ages", sourceName, label, code);
        var tierNames = ReadFilter(entry, "tiers", sourceName, label, code);
        var categoryNames = ReadFilter(entry, "categories", sourceName, label, code);
        if (ages is null && tierNames is null && categoryNames is null)
            return new EventTemplateSlotPlacement(code, null, null, null);

        IReadOnlyList<string>? scopedAges = null;
        if (ages is not null)
            scopedAges = ages
                .Select(a => ageGroups.FirstOrDefault(g =>
                        string.Equals(g, AgeScope.Normalise(a), StringComparison.Ordinal))
                    ?? throw new InvalidDataException(Where(
                        $"scopes '{code}' to age '{a}', which divisions.ageGroups does not " +
                        $"declare (declared: {string.Join(", ", ageGroups)}).")))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        IReadOnlyList<EventTier>? scopedTiers = null;
        if (tierNames is not null)
            scopedTiers = tierNames.Select(t =>
                Enum.TryParse<EventTier>(t, ignoreCase: true, out var tier) && Enum.IsDefined(tier)
                && tiers.Contains(tier)
                    ? tier
                    : throw new InvalidDataException(Where(
                        $"scopes '{code}' to tier '{t}', which the template does not contest " +
                        $"(declared: {string.Join(", ", tiers)})."))).Distinct().ToList();

        IReadOnlyList<CompetitionCategory>? scopedCategories = null;
        if (categoryNames is not null)
            scopedCategories = categoryNames.Select(c =>
                Enum.TryParse<CompetitionCategory>(c, ignoreCase: true, out var category)
                && Enum.IsDefined(category) && categories.Contains(category)
                    ? category
                    : throw new InvalidDataException(Where(
                        $"scopes '{code}' to category '{c}', which the template does not contest " +
                        $"(declared: {string.Join(", ", categories)})."))).Distinct().ToList();

        return new EventTemplateSlotPlacement(code, scopedAges, scopedTiers, scopedCategories);
    }

    // Absent is null, never empty.
    private static IReadOnlyList<string>? ReadFilter(
        JsonElement entry, string field, string sourceName, string label, string code)
    {
        if (!entry.TryGetProperty(field, out var element)) return null;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"Event template '{sourceName}': slot '{label}' scopes '{code}' with a '{field}' " +
                "that is not an array.");
        var values = element.EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();
        if (values.Count == 0)
            throw new InvalidDataException(
                $"Event template '{sourceName}': slot '{label}' scopes '{code}' with an empty " +
                $"'{field}' — omit the key to hold every instance.");
        return values;
    }

    // 24-hour only: "1:30" meaning the afternoon would silently schedule a relay before breakfast.
    private static TimeOnly? ReadTime(string? value, string sourceName, string where)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (!TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            throw new InvalidDataException(
                $"Event template '{sourceName}': {where} is not a 24-hour local time " +
                $"(expected \"HH:mm\", got '{value}').");
        return parsed;
    }

    private static readonly IReadOnlyList<EventTier> DefaultTiers = [EventTier.Competitive];

    // Validated at load: two spellings of one age would otherwise collide on the division unique
    // index mid-apply instead of naming the bad code.
    private static List<string> ReadAgeGroups(TemplateDocument doc, string sourceName)
    {
        if (doc.Divisions?.AgeGroups is not { } declared || declared.Count == 0)
            throw new InvalidDataException($"Event template '{sourceName}' defines no age groups.");

        var ages = declared
            .Select(a => a?.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .Select(a =>
            {
                if (AgeScope.Validate(a) is { } invalid)
                    throw new InvalidDataException($"Event template '{sourceName}': {invalid}");
                var code = AgeScope.Normalise(a);
                if (code == AgeScope.OpenKey)
                    throw new InvalidDataException(
                        $"Event template '{sourceName}': divisions.ageGroups may not name " +
                        $"'{AgeScope.OpenKey}' — an all-ages division comes from a definition's " +
                        "openDivision.");
                return code;
            })
            .ToList();
        if (ages.Count == 0)
            throw new InvalidDataException($"Event template '{sourceName}' defines no age groups.");
        if (ages.Distinct(StringComparer.Ordinal).Count() != ages.Count)
            throw new InvalidDataException($"Event template '{sourceName}' names the same age twice.");
        return ages;
    }

    private static IReadOnlyList<CompetitionCategory> ReadCategories(TemplateDocument doc, string sourceName)
    {
        if (doc.Divisions?.Categories is { } declared)
            return declared.Count > 0
                ? declared
                : throw new InvalidDataException($"Event template '{sourceName}': divisions.categories is empty.");
        return doc.Categories is { Count: > 0 } legacy
            ? legacy
            : throw new InvalidDataException($"Event template '{sourceName}' defines no categories.");
    }

    // Read by hand, not via the enum converter, so a typo names itself and the valid spellings;
    // numeric spellings are refused.
    private static IReadOnlyList<EventTier> ReadTiers(List<string> declared, string sourceName, string where)
    {
        var tiers = new List<EventTier>();
        foreach (var raw in declared)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || char.IsAsciiDigit(name[0])
                || !Enum.TryParse<EventTier>(name, ignoreCase: true, out var tier)
                || !Enum.IsDefined(tier))
                throw new InvalidDataException(
                    $"Event template '{sourceName}': {where} names an unknown tier '{raw}' " +
                    $"(expected {string.Join(" or ", Enum.GetNames<EventTier>())}).");
            if (tiers.Contains(tier))
                throw new InvalidDataException(
                    $"Event template '{sourceName}': {where} lists '{tier}' twice.");
            tiers.Add(tier);
        }
        if (tiers.Count == 0)
            throw new InvalidDataException($"Event template '{sourceName}': {where} is empty.");
        return tiers;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class TemplateDocument
    {
        public string? Template { get; set; }
        public string? Name { get; set; }

        // Legacy category axis, still honoured when the divisions section declares none.
        public List<CompetitionCategory>? Categories { get; set; }

        public DivisionsDocument? Divisions { get; set; }
        public List<DefinitionDocument>? Definitions { get; set; }

        public ScheduleDocument? Schedule { get; set; }
    }

    private sealed class ScheduleDocument
    {
        public List<StreamDocument>? Streams { get; set; }
    }

    private sealed class StreamDocument
    {
        public string? Name { get; set; }

        public List<SlotDocument>? Slots { get; set; }
    }

    private sealed class SlotDocument
    {
        public string? Start { get; set; }

        public string? End { get; set; }

        public string? Label { get; set; }

        // Absent or empty makes the slot a non-competition program item.
        public List<JsonElement>? Events { get; set; }

        public string? AdjustmentCategory { get; set; }
    }

    private sealed class DivisionsDocument
    {
        public List<string>? AgeGroups { get; set; }

        public List<CompetitionCategory>? Categories { get; set; }
        public List<string>? Tiers { get; set; }
    }

    private sealed class DefinitionDocument
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Sport { get; set; }
        public MeasureType MeasureType { get; set; }
        public string? Units { get; set; }

        // Absent means a single typed measure.
        public int? Attempts { get; set; }

        public LadderDto? Ladder { get; set; }

        public int? AttemptsPerHeight { get; set; }

        public bool Active { get; set; } = true;

        public bool SchoolRecords { get; set; }

        public EventFormat Format { get; set; } = EventFormat.Individual;
        public int? TeamSize { get; set; }
        public List<string>? Legs { get; set; }
        public int ScoringWeight { get; set; } = 1;

        // Also schedules an all-ages division on top of the per-age ones.
        public bool OpenDivision { get; set; }

        public List<string>? Tags { get; set; }

        public List<string>? Tiers { get; set; }
    }
}

// Streams run concurrently: SortOrder is a left-to-right print order, never a sequence.
public sealed record EventTemplateStream(string Name, int SortOrder, IReadOnlyList<EventTemplateSlot> Slots);

// Times are local clock times: the carnival owns the date.
public sealed record EventTemplateSlot(
    string Label,
    TimeOnly? Start,
    TimeOnly? End,
    IReadOnlyList<EventTemplateSlotPlacement> Placements,
    string? AdjustmentCategory,
    int SortOrder)
{
    public bool HoldsEvents => Placements.Count > 0;

    public IReadOnlyList<string> DefinitionCodes => [.. Placements.Select(p => p.Code)];
}

// Each filter is null for every instance.
public sealed record EventTemplateSlotPlacement(
    string Code,
    IReadOnlyList<string>? Ages,
    IReadOnlyList<EventTier>? Tiers,
    IReadOnlyList<CompetitionCategory>? Categories)
{
    public bool Filtered => Ages is not null || Tiers is not null || Categories is not null;

    public bool Matches(string ageGroup, EventTier tier, CompetitionCategory? category) =>
        (Ages is null || Ages.Contains(ageGroup, StringComparer.Ordinal))
        && (Tiers is null || Tiers.Contains(tier))
        && (Categories is null || (category is { } c && Categories.Contains(c)));
}

public sealed class EventTemplateDefinition
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Sport { get; init; }
    public MeasureType MeasureType { get; init; }

    public int? AttemptCount { get; init; }

    public LadderDto? Ladder { get; init; }

    public int? AttemptsPerHeight { get; init; }

    public required string Units { get; init; }
    public bool Active { get; init; }
    public bool SchoolRecords { get; init; }
    public EventFormat Format { get; init; }
    public int? TeamSize { get; init; }
    public IReadOnlyList<string>? Legs { get; init; }
    public int ScoringWeight { get; init; } = 1;
    public bool OpenDivision { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    // Null inherits the template's tiers (EventTemplate.TiersFor).
    public IReadOnlyList<EventTier>? Tiers { get; init; }
}
