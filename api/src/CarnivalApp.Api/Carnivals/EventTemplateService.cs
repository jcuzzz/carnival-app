using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

// Re-applying never duplicates and never touches existing catalogue rows or scheduled events;
// reconciling a template change is TemplateReconciler's job.
public partial class EventTemplateService(
    SportsDbContext db, AuditService audit, IConfiguration config, IHostEnvironment env,
    ILogger<EventTemplateService> logger)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex TemplateKeyPattern();

    public sealed record TemplateResolution(EventTemplate? Template, string? Error, int ErrorStatus);

    public TemplateResolution Resolve(string? key)
    {
        key = key?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            return new(null, "template is required (e.g. \"swimming\").", StatusCodes.Status400BadRequest);
        // A template key is a file name component, never a path.
        if (!TemplateKeyPattern().IsMatch(key))
            return new(null, $"'{key}' is not a valid template key.", StatusCodes.Status400BadRequest);

        var directory = config["EventTemplates:Path"];
        if (string.IsNullOrWhiteSpace(directory))
            return new(null, "EventTemplates:Path is not configured.", StatusCodes.Status500InternalServerError);
        directory = Path.GetFullPath(Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(env.ContentRootPath, directory));
        // Server paths and exception text stay in the log; the caller gets a fixed sentence.
        if (!Directory.Exists(directory))
        {
            logger.LogError("Event template directory {Directory} does not exist.", directory);
            return new(null, "The event template store is not available on this server.",
                StatusCodes.Status500InternalServerError);
        }

        var path = Path.Combine(directory, $"{key}.json");
        if (!File.Exists(path))
        {
            var available = Directory.EnumerateFiles(directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension).Order().ToList();
            return new(null,
                $"Unknown template '{key}'. Available: {(available.Count > 0 ? string.Join(", ", available) : "(none)")}.",
                StatusCodes.Status400BadRequest);
        }

        try
        {
            return new(EventTemplate.LoadFromFile(path), null, 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            logger.LogError(ex, "Event template {Key} at {Path} could not be read.", key, path);
            return new(null, $"The '{key}' event template on this server is not readable.",
                StatusCodes.Status500InternalServerError);
        }
    }

    // Shared by apply and reconcile so they cannot disagree about the template's value.
    public static string RecordCategoriesJsonFor(EventTemplate template) => JsonSerializer.Serialize(new
    {
        ageGroups = template.AgeGroups,
        categories = template.Categories.Select(c => c.ToString()),
    });

    public sealed record TemplateSummary(string Id, string? Name, string Version, int Definitions, bool Readable);

    // An unreadable document is listed, not dropped: a silently missing template looks unshipped.
    public IReadOnlyList<TemplateSummary> List()
    {
        if (TemplateDirectory() is not { } directory) return [];
        var summaries = new List<TemplateSummary>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(key) || !TemplateKeyPattern().IsMatch(key)) continue;
            try
            {
                var template = EventTemplate.LoadFromFile(path);
                summaries.Add(new TemplateSummary(
                    key, template.Name, template.Version, template.Definitions.Count, Readable: true));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                logger.LogError(ex, "Event template {Path} could not be read.", path);
                summaries.Add(new TemplateSummary(key, null, Path.GetFileName(path), 0, Readable: false));
            }
        }
        return summaries;
    }

    public sealed record TemplateLookup(EventTemplate? Template, string? ErrorCode, string? Detail, int Status);

    // For the admin path, where the key is a path segment: unknown is 404, unreadable is 422.
    public TemplateLookup Lookup(string? key)
    {
        key = key?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key) || !TemplateKeyPattern().IsMatch(key))
            return new(null, ErrorCodes.TemplateNotFound, $"There is no '{key}' event template.",
                StatusCodes.Status404NotFound);
        if (TemplateDirectory() is not { } directory)
            return new(null, ErrorCodes.TemplateUnavailable,
                "The event template store is not available on this server.",
                StatusCodes.Status500InternalServerError);

        var path = Path.Combine(directory, $"{key}.json");
        if (!File.Exists(path))
            return new(null, ErrorCodes.TemplateNotFound, $"There is no '{key}' event template.",
                StatusCodes.Status404NotFound);
        try
        {
            return new(EventTemplate.LoadFromFile(path), null, null, 0);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            logger.LogError(ex, "Event template {Key} at {Path} could not be read.", key, path);
            return new(null, ErrorCodes.TemplateInvalid,
                $"The '{key}' event template on this server cannot be read. Fix the document and deploy it again.",
                StatusCodes.Status422UnprocessableEntity);
        }
    }

    private string? TemplateDirectory()
    {
        var directory = config["EventTemplates:Path"];
        if (string.IsNullOrWhiteSpace(directory)) return null;
        directory = Path.GetFullPath(Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(env.ContentRootPath, directory));
        if (Directory.Exists(directory)) return directory;
        logger.LogError("Event template directory {Directory} does not exist.", directory);
        return null;
    }

    // Also the audit payload, so it names the divisions, not only counts: "158 events" is
    // unreviewable a term later.
    public sealed record ApplicationResult(
        Guid CarnivalId,
        string Template,
        string TemplateName,
        string TemplateVersion,
        IReadOnlyList<string> AgeGroups,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> Tiers,
        int DefinitionsCreated,
        int DefinitionsExisting,
        IReadOnlyList<string> DefinitionsSkippedInactive,
        int EventsCreated,
        int EventsAlreadyScheduled,
        int StreamsCreated,
        int SlotsCreated,
        int EventsPlaced,
        bool DivisionProfileSeeded);

    // One SaveChanges, so the audit record commits with the apply.
    public async Task<ApplicationResult> ApplyAsync(
        Carnival carnival, EventTemplate template, AuditActor actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var codes = template.Definitions.Select(d => d.Code).ToList();
        var existing = await db.EventDefinitions
            .Where(d => codes.Contains(d.Code))
            .ToDictionaryAsync(d => d.Code, StringComparer.OrdinalIgnoreCase, ct);

        var definitionsCreated = 0;
        // No tier filter: record detection reads the tier off the mutable event instance, so gating
        // here would permanently disqualify a definition the school later runs Competitively.
        var recordCategoriesJson = RecordCategoriesJsonFor(template);

        foreach (var templated in template.Definitions)
        {
            if (existing.ContainsKey(templated.Code))
                continue;
            var definition = new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = templated.Code,
                Name = templated.Name,
                Sport = templated.Sport,
                MeasureType = templated.MeasureType,
                AttemptCount = templated.AttemptCount,
                LadderJson = templated.Ladder is null ? null : JumpCard.SerialiseLadder(templated.Ladder),
                AttemptsPerHeight = templated.AttemptsPerHeight,
                Units = templated.Units,
                RecordCategoriesJson = templated.SchoolRecords ? recordCategoriesJson : null,
                IsActive = templated.Active,
                Format = templated.Format,
                TeamSize = templated.TeamSize,
                LegsJson = templated.Legs is null ? null : JsonSerializer.Serialize(templated.Legs),
                ScoringWeight = templated.ScoringWeight,
                TagsJson = templated.Tags is null ? null : JsonSerializer.Serialize(templated.Tags),
                CreatedAtUtc = now,
            };
            db.EventDefinitions.Add(definition);
            existing[templated.Code] = definition;
            definitionsCreated++;
            audit.Record(actor, AuditActions.EventDefinitionCreate, nameof(EventDefinition), definition.Id.ToString(),
                newValues: new
                {
                    definition.Code, definition.Name, definition.Sport,
                    measureType = definition.MeasureType.ToString(),
                    definition.Units, definition.IsActive,
                    template = template.Key,
                });
        }

        // Inactive in the template or deactivated in the catalogue: catalogued, never scheduled.
        var schedulable = template.Definitions
            .Where(d => d.Active && existing[d.Code].IsActive)
            .ToList();
        var skippedInactive = template.Definitions
            .Where(d => !d.Active || !existing[d.Code].IsActive)
            .Select(d => d.Code)
            .ToList();

        var definitionIds = schedulable.Select(d => existing[d.Code].Id).ToList();
        var alreadyScheduled = (await db.CarnivalEvents
                .Where(e => e.CarnivalId == carnival.Id && definitionIds.Contains(e.EventDefinitionId))
                .Select(e => new { e.EventDefinitionId, e.AgeGroup, e.Category, e.Tier })
                .ToListAsync(ct))
            .Select(e => new DivisionGenerator.DivisionKey(
                e.EventDefinitionId, e.AgeGroup, e.Category, e.Tier))
            .ToHashSet();

        var eventsCreated = 0;
        var eventsSkipped = 0;
        // Tracked but unsaved, so schedule seeding must see them from here, not from a query.
        var createdEvents = new List<CarnivalEvent>();
        var templateAges = template.AgeGroups.Select(a => (string?)a).ToList();
        foreach (var templated in schedulable)
        {
            // A null age is the open (all-ages) division.
            var ages = new List<string?>(templateAges);
            if (templated.OpenDivision) ages.Add(null);
            var outcome = DivisionGenerator.Generate(
                carnival.Id, existing[templated.Code],
                DivisionGenerator.Matrix(ages, template.Categories, template.TiersFor(templated)),
                alreadyScheduled);
            db.CarnivalEvents.AddRange(outcome.Created);
            createdEvents.AddRange(outcome.Created);
            eventsCreated += outcome.Created.Count;
            eventsSkipped += outcome.Skipped;
        }

        var program = await SeedScheduleAsync(carnival, template, existing, createdEvents, ct);
        var profileSeeded = await SeedDivisionProfileAsync(carnival, template, ct);

        var result = new ApplicationResult(
            carnival.Id, template.Key, template.Name, template.Version,
            template.AgeGroups,
            template.Categories.Select(c => c.ToString()).ToList(),
            // The tiers actually reached: per-definition overrides can differ from the list.
            schedulable.SelectMany(template.TiersFor).Distinct().Order().Select(t => t.ToString()).ToList(),
            definitionsCreated, template.Definitions.Count - definitionsCreated,
            skippedInactive, eventsCreated, eventsSkipped,
            program.StreamsCreated, program.SlotsCreated, program.EventsPlaced,
            profileSeeded);
        var audited = audit.Record(actor, AuditActions.CarnivalApplyTemplate, nameof(Carnival),
            carnival.Id.ToString(), newValues: result, ruleVersion: carnival.RuleVersion);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (result.DivisionProfileSeeded && SeededConcurrently(ex))
        {
            // Another apply seeded the profile first; its rows come from a template too, so this
            // one drops its own and still succeeds.
            foreach (var entry in db.ChangeTracker.Entries<CarnivalDivision>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            db.AuditEvents.Remove(audited);
            result = result with { DivisionProfileSeeded = false };
            audit.Record(actor, AuditActions.CarnivalApplyTemplate, nameof(Carnival),
                carnival.Id.ToString(), newValues: result, ruleVersion: carnival.RuleVersion);
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    private static bool SeededConcurrently(DbUpdateException ex) =>
        ex is not DbUpdateConcurrencyException && ex.Entries.Any(e => e.Entity is CarnivalDivision);

    // Never overwrites a usable profile; unusable rows read as not configured and nothing else can
    // repair them, so the seed replaces those.
    private async Task<bool> SeedDivisionProfileAsync(
        Carnival carnival, EventTemplate template, CancellationToken ct)
    {
        var rows = await db.CarnivalDivisions.Where(d => d.CarnivalId == carnival.Id).ToListAsync(ct);
        if (DivisionProfile.FromRows(rows) is not null) return false;
        db.CarnivalDivisions.RemoveRange(rows);
        db.CarnivalDivisions.AddRange(DivisionProfile.FromTemplate(template).ToRows(carnival.Id));
        return true;
    }

    private readonly record struct ProgramResult(int StreamsCreated, int SlotsCreated, int EventsPlaced);

    // Existing streams (by name) and slots (by label) are left alone; only unscheduled events are
    // placed. Slot identity is the label, so a renamed or deleted slot returns on a re-apply.
    private async Task<ProgramResult> SeedScheduleAsync(
        Carnival carnival, EventTemplate template,
        IReadOnlyDictionary<string, EventDefinition> definitions,
        IReadOnlyList<CarnivalEvent> createdEvents, CancellationToken ct)
    {
        if (template.Streams.Count == 0) return default;

        var existingStreams = await db.ScheduleStreams
            .Include(s => s.Slots)
            .Where(s => s.CarnivalId == carnival.Id)
            .ToListAsync(ct);

        // Plus this apply's instances, tracked but unsaved and so invisible to a query.
        var blockDefinitionIds = template.Streams
            .SelectMany(s => s.Slots)
            .SelectMany(sl => sl.DefinitionCodes)
            .Where(definitions.ContainsKey)
            .Select(code => definitions[code].Id)
            .ToHashSet();
        List<CarnivalEvent> placeable = blockDefinitionIds.Count == 0
            ? []
            : (await db.CarnivalEvents
                    .Where(e => e.CarnivalId == carnival.Id && blockDefinitionIds.Contains(e.EventDefinitionId))
                    .ToListAsync(ct))
                .Concat(createdEvents.Where(e => blockDefinitionIds.Contains(e.EventDefinitionId)))
                .DistinctBy(e => e.Id)
                .ToList();

        var ageRank = template.AgeGroups
            .Select((age, index) => (Age: age, Index: index))
            .ToDictionary(a => a.Age, a => a.Index, StringComparer.Ordinal);
        int AgeRank(CarnivalEvent e) => ageRank.TryGetValue(e.AgeGroup, out var rank)
            ? rank
            : int.MaxValue;
        var categoryRank = template.Categories
            .Select((category, index) => (category, index))
            .ToDictionary(c => c.category, c => c.index);
        int CategoryRank(CarnivalEvent e) =>
            e.Category is { } category && categoryRank.TryGetValue(category, out var rank)
                ? rank
                : int.MaxValue;

        var streamsCreated = 0;
        var slotsCreated = 0;
        var eventsPlaced = 0;

        foreach (var templatedStream in template.Streams)
        {
            var stream = existingStreams.FirstOrDefault(s =>
                string.Equals(s.Name, templatedStream.Name, StringComparison.OrdinalIgnoreCase));
            if (stream is null)
            {
                stream = new ScheduleStream
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalId = carnival.Id,
                    Name = templatedStream.Name,
                    SortOrder = templatedStream.SortOrder,
                };
                db.ScheduleStreams.Add(stream);
                existingStreams.Add(stream);
                streamsCreated++;
            }

            foreach (var templatedSlot in templatedStream.Slots)
            {
                if (stream.Slots.Any(sl =>
                        string.Equals(sl.Label, templatedSlot.Label, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var slot = new ScheduleSlot
                {
                    Id = Guid.CreateVersion7(),
                    StreamId = stream.Id,
                    SortOrder = templatedSlot.SortOrder,
                    StartTime = templatedSlot.Start,
                    EndTime = templatedSlot.End,
                    Kind = templatedSlot.HoldsEvents ? ScheduleSlotKind.Events : ScheduleSlotKind.Item,
                    Label = templatedSlot.Label,
                    // Not validated here: the link is cosmetic and a mismatch must not abort a
                    // whole apply. The organiser-facing slot endpoints do validate it.
                    AdjustmentCategory = templatedSlot.AdjustmentCategory,
                };
                db.ScheduleSlots.Add(slot);
                stream.Slots.Add(slot);
                slotsCreated++;

                var order = 0;
                foreach (var placement in templatedSlot.Placements)
                {
                    if (!definitions.TryGetValue(placement.Code, out var definition)) continue;
                    // A definition may sit in several slots; the first whose filter matches claims
                    // the instance, because only an unplaced one is offered.
                    foreach (var instance in placeable
                                 .Where(e => e.EventDefinitionId == definition.Id && e.ScheduleSlotId is null
                                             && placement.Matches(e.AgeGroup, e.Tier, e.Category))
                                 .OrderBy(AgeRank)
                                 .ThenBy(CategoryRank)
                                 .ThenBy(e => e.Tier)
                                 .ThenBy(e => e.AgeGroup, StringComparer.Ordinal)
                                 .ToList())
                    {
                        instance.ScheduleSlotId = slot.Id;
                        instance.SlotOrder = order++;
                        eventsPlaced++;
                    }
                }
            }
        }

        return new ProgramResult(streamsCreated, slotsCreated, eventsPlaced);
    }
}
