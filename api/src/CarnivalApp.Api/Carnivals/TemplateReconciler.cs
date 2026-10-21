using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Carnivals;

public enum TemplateDiffKind
{
    Unchanged,
    Update,
    Missing,
    CatalogueOnly,
}

// Guard is the sentence saying why the field must not move; null means the change is free.
public sealed record TemplateFieldDiff(string Field, string? Prior, string? New, string? Guard);

// UpdatedAtUtc and EditedInApp show what is known: an admin's edit and an older template's value
// are indistinguishable.
public sealed record TemplateDiffRow(
    string Code,
    string Name,
    TemplateDiffKind Kind,
    IReadOnlyList<TemplateFieldDiff> Fields,
    DateTimeOffset? UpdatedAtUtc,
    bool EditedInApp,
    bool Guarded);

public sealed record TemplateDiffCounts(
    int Unchanged, int Update, int Missing, int CatalogueOnly, int Guarded);

public sealed record TemplateDiff(
    string TemplateId,
    string TemplateName,
    string TemplateVersion,
    string DiffHash,
    IReadOnlyList<TemplateDiffRow> Rows,
    TemplateDiffCounts Counts);

public sealed record TemplateUnsafeChange(string Code, string Field, string Guard);

// IgnoredCodes: requested exclusions the plan does not name; they change nothing, so go unaudited.
public sealed record TemplateReconcileOutcome(
    string TemplateId,
    string TemplateName,
    string TemplateVersion,
    string DiffHash,
    string Reason,
    int Updated,
    int Created,
    int Unchanged,
    int CatalogueOnly,
    IReadOnlyList<string> UpdatedCodes,
    IReadOnlyList<string> CreatedCodes,
    IReadOnlyList<string> ExcludedCodes,
    IReadOnlyList<string> IgnoredCodes);

internal sealed record TemplateReconcileSummary(
    string TemplateId,
    string TemplateName,
    string TemplateVersion,
    string DiffHash,
    string Reason,
    int Updated,
    int Created,
    int Unchanged,
    int CatalogueOnly,
    IReadOnlyList<string> UpdatedCodes,
    IReadOnlyList<string> CreatedCodes,
    IReadOnlyList<string> ExcludedCodes);

// Never deletes, never deactivates a catalogue-only row, and never schedules anything.
public sealed class TemplateReconciler(SportsDbContext db, AuditService audit)
{
    public sealed record Plan(TemplateDiff Diff, IReadOnlyList<PlannedRow> Rows);

    public sealed record PlannedRow(
        TemplateDiffRow Row, EventDefinition? Existing, TemplateValues? Target);

    // AttemptsPerHeight is the effective value on a Height event, so a stored null and a stored 3
    // are not drift.
    public sealed record TemplateValues(
        string Name,
        string Sport,
        MeasureType MeasureType,
        int? AttemptCount,
        string? LadderJson,
        int? AttemptsPerHeight,
        string Units,
        bool IsActive,
        EventFormat Format,
        int? TeamSize,
        string? LegsJson,
        int ScoringWeight,
        string? TagsJson,
        string? RecordCategoriesJson);

    // Stamped on this reconciler's audit rows so the edited-in-app signal excludes its own work.
    public const string ReconcileRuleVersion = "template-reconcile";

    // Rendered verbatim beside the row they refuse.
    private const string ResultsGuard =
        "Results are recorded here, so the measure type and units are frozen.";
    private const string AttemptsGuard =
        "Results are recorded here, so the attempt count is frozen.";
    private const string TeamGuard = "Team entries exist here, so the team shape is frozen.";
    private const string EntriesGuard = "Entries exist here, so the event format is frozen.";
    private const string ScoringGuard =
        "A finalised carnival scores this, so the scoring weight is frozen.";
    private const string RecordGuard =
        "The record book cites this, so its record categories are frozen.";

    private sealed record Guards(
        bool Results, bool Entries, bool TeamEntries, bool Finalised, bool SchoolRecords)
    {
        public static readonly Guards None = new(false, false, false, false, false);
    }

    public static TemplateValues ValuesOf(EventTemplate template, EventTemplateDefinition definition) => new(
        definition.Name,
        definition.Sport,
        definition.MeasureType,
        definition.AttemptCount,
        definition.Ladder is null ? null : JumpCard.SerialiseLadder(definition.Ladder),
        definition.MeasureType == MeasureType.Height
            ? definition.AttemptsPerHeight ?? JumpCard.DefaultAttemptsPerHeight
            : null,
        definition.Units,
        definition.Active,
        definition.Format,
        definition.TeamSize,
        definition.Legs is null ? null : JsonSerializer.Serialize(definition.Legs),
        definition.ScoringWeight,
        definition.Tags is null ? null : JsonSerializer.Serialize(definition.Tags),
        definition.SchoolRecords ? EventTemplateService.RecordCategoriesJsonFor(template) : null);

    public async Task<Plan> PlanAsync(EventTemplate template, CancellationToken ct)
    {
        var catalogue = await db.EventDefinitions.ToListAsync(ct);
        var byCode = catalogue
            .GroupBy(d => d.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var guards = await GuardsAsync(catalogue, ct);
        var edited = await EditedInAppAsync(ct);

        var rows = new List<PlannedRow>();
        foreach (var templated in template.Definitions)
        {
            var target = ValuesOf(template, templated);
            if (!byCode.TryGetValue(templated.Code, out var existing))
            {
                rows.Add(new PlannedRow(
                    new TemplateDiffRow(templated.Code, templated.Name, TemplateDiffKind.Missing,
                        [], null, EditedInApp: false, Guarded: false),
                    null, target));
                continue;
            }

            var guard = guards.GetValueOrDefault(existing.Id, Guards.None);
            var fields = FieldDiffs(existing, target, guard);
            rows.Add(new PlannedRow(
                new TemplateDiffRow(
                    existing.Code,
                    templated.Name,
                    fields.Count == 0 ? TemplateDiffKind.Unchanged : TemplateDiffKind.Update,
                    fields,
                    existing.UpdatedAtUtc,
                    edited.Contains(existing.Id.ToString()),
                    fields.Any(f => f.Guard is not null)),
                existing, target));
        }

        var templatedCodes = template.Definitions
            .Select(d => d.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orphan in catalogue.Where(d => !templatedCodes.Contains(d.Code)))
            rows.Add(new PlannedRow(
                new TemplateDiffRow(orphan.Code, orphan.Name, TemplateDiffKind.CatalogueOnly,
                    [], orphan.UpdatedAtUtc, edited.Contains(orphan.Id.ToString()), Guarded: false),
                orphan, null));

        var ordered = rows.OrderBy(r => r.Row.Code, StringComparer.Ordinal).ToList();
        var counts = new TemplateDiffCounts(
            ordered.Count(r => r.Row.Kind == TemplateDiffKind.Unchanged),
            ordered.Count(r => r.Row.Kind == TemplateDiffKind.Update),
            ordered.Count(r => r.Row.Kind == TemplateDiffKind.Missing),
            ordered.Count(r => r.Row.Kind == TemplateDiffKind.CatalogueOnly),
            ordered.Count(r => r.Row.Guarded));
        var diff = new TemplateDiff(
            template.Key, template.Name, template.Version,
            HashOf(template, ordered),
            [.. ordered.Select(r => r.Row)],
            counts);
        return new Plan(diff, ordered);
    }

    public static TemplateUnsafeChange? FirstUnsafe(Plan plan, IReadOnlySet<string> excluded) =>
        plan.Rows
            .Where(r => r.Row.Kind == TemplateDiffKind.Update && !excluded.Contains(r.Row.Code))
            .SelectMany(r => r.Row.Fields
                .Where(f => f.Guard is not null)
                .Select(f => new TemplateUnsafeChange(r.Row.Code, f.Field, f.Guard!)))
            .FirstOrDefault();

    // One SaveChanges: the catalogue writes and every audit row commit together or not at all.
    public async Task<TemplateReconcileOutcome> ApplyAsync(
        Plan plan, IReadOnlySet<string> excluded, IReadOnlyList<string> ignored,
        string reason, AuditActor actor, CancellationToken ct)
    {
        var usage = await EventDefinitionEndpoints.UsageAsync(db, null, ct);
        var now = DateTimeOffset.UtcNow;
        var updated = new List<string>();
        var created = new List<string>();

        foreach (var planned in plan.Rows)
        {
            if (excluded.Contains(planned.Row.Code)) continue;
            if (planned.Target is not { } target) continue;

            if (planned.Row.Kind == TemplateDiffKind.Missing)
            {
                var definition = new EventDefinition
                {
                    Id = Guid.CreateVersion7(),
                    Code = planned.Row.Code,
                    Name = target.Name,
                    Sport = target.Sport,
                    MeasureType = target.MeasureType,
                    AttemptCount = target.AttemptCount,
                    LadderJson = target.LadderJson,
                    AttemptsPerHeight = target.AttemptsPerHeight,
                    Units = target.Units,
                    IsActive = target.IsActive,
                    Format = target.Format,
                    TeamSize = target.TeamSize,
                    LegsJson = target.LegsJson,
                    ScoringWeight = target.ScoringWeight,
                    TagsJson = target.TagsJson,
                    RecordCategoriesJson = target.RecordCategoriesJson,
                    CreatedAtUtc = now,
                };
                db.EventDefinitions.Add(definition);
                created.Add(definition.Code);
                audit.Record(actor, AuditActions.EventDefinitionCreate, nameof(EventDefinition),
                    definition.Id.ToString(),
                    newValues: EventDefinitionEndpoints.Project(
                        definition, EventDefinitionEndpoints.EventDefinitionUsageDto.None),
                    reason: reason, ruleVersion: ReconcileRuleVersion);
                continue;
            }

            if (planned.Row.Kind != TemplateDiffKind.Update || planned.Existing is not { } existing) continue;

            var counted = usage.GetValueOrDefault(
                existing.Id, EventDefinitionEndpoints.EventDefinitionUsageDto.None);
            var prior = EventDefinitionEndpoints.Project(existing, counted);
            // A guarded field is never in this list, so it cannot be written here by accident.
            foreach (var field in planned.Row.Fields) Assign(existing, field.Field, target);
            existing.UpdatedAtUtc = now;
            updated.Add(existing.Code);
            audit.Record(actor, AuditActions.EventDefinitionUpdate, nameof(EventDefinition),
                existing.Id.ToString(),
                priorValues: prior,
                newValues: EventDefinitionEndpoints.Project(existing, counted),
                reason: reason, ruleVersion: ReconcileRuleVersion);
        }

        string[] excludedCodes = [.. excluded.Order(StringComparer.Ordinal)];
        var summary = new TemplateReconcileSummary(
            plan.Diff.TemplateId, plan.Diff.TemplateName, plan.Diff.TemplateVersion,
            plan.Diff.DiffHash, reason,
            updated.Count, created.Count,
            plan.Diff.Counts.Unchanged, plan.Diff.Counts.CatalogueOnly,
            updated, created, excludedCodes);
        audit.Record(actor, AuditActions.EventTemplateReconcile, nameof(EventTemplate),
            plan.Diff.TemplateId, newValues: summary, reason: reason,
            ruleVersion: ReconcileRuleVersion);
        await db.SaveChangesAsync(ct);
        return new TemplateReconcileOutcome(
            summary.TemplateId, summary.TemplateName, summary.TemplateVersion, summary.DiffHash,
            summary.Reason, summary.Updated, summary.Created, summary.Unchanged,
            summary.CatalogueOnly, summary.UpdatedCodes, summary.CreatedCodes,
            summary.ExcludedCodes, ignored);
    }

    private static void Assign(EventDefinition definition, string field, TemplateValues target)
    {
        switch (field)
        {
            case "name": definition.Name = target.Name; break;
            case "sport": definition.Sport = target.Sport; break;
            case "measureType": definition.MeasureType = target.MeasureType; break;
            case "attemptCount": definition.AttemptCount = target.AttemptCount; break;
            case "ladderJson": definition.LadderJson = target.LadderJson; break;
            case "attemptsPerHeight": definition.AttemptsPerHeight = target.AttemptsPerHeight; break;
            case "units": definition.Units = target.Units; break;
            case "isActive": definition.IsActive = target.IsActive; break;
            case "format": definition.Format = target.Format; break;
            case "teamSize": definition.TeamSize = target.TeamSize; break;
            case "legsJson": definition.LegsJson = target.LegsJson; break;
            case "scoringWeight": definition.ScoringWeight = target.ScoringWeight; break;
            case "tagsJson": definition.TagsJson = target.TagsJson; break;
            case "recordCategoriesJson":
                definition.RecordCategoriesJson = target.RecordCategoriesJson; break;
            default: throw new InvalidOperationException($"Unknown template-owned field '{field}'.");
        }
    }

    private static List<TemplateFieldDiff> FieldDiffs(
        EventDefinition existing, TemplateValues target, Guards guard)
    {
        var diffs = new List<TemplateFieldDiff>();
        void Compare(string field, string? prior, string? updated, string? refusal = null)
        {
            if (prior == updated) return;
            diffs.Add(new TemplateFieldDiff(field, prior, updated, refusal));
        }

        // A blob whose only difference is whitespace is not a change to overwrite.
        void CompareJson(string field, string? prior, string? updated, string? refusal = null)
        {
            if (Canonical(prior) == Canonical(updated)) return;
            diffs.Add(new TemplateFieldDiff(field, prior, updated, refusal));
        }

        Compare("name", existing.Name, target.Name);
        Compare("sport", existing.Sport, target.Sport);
        Compare("measureType", existing.MeasureType.ToString(), target.MeasureType.ToString(),
            guard.Results ? ResultsGuard : null);
        Compare("attemptCount", Text(existing.AttemptCount), Text(target.AttemptCount),
            guard.Results ? AttemptsGuard : null);
        CompareJson("ladderJson", existing.LadderJson, target.LadderJson);
        // A default like the ladder: a division stamped from it keeps its own value.
        Compare("attemptsPerHeight", Text(JumpCard.AttemptsPerHeightFor(existing)),
            Text(target.AttemptsPerHeight));
        Compare("units", existing.Units, target.Units, guard.Results ? ResultsGuard : null);
        Compare("isActive", Text(existing.IsActive), Text(target.IsActive));
        // Any entry, not only a team: an individual entry in a relay division is unscorable.
        var formatGuard = guard.TeamEntries ? TeamGuard : guard.Entries ? EntriesGuard : null;
        Compare("format", existing.Format.ToString(), target.Format.ToString(), formatGuard);
        Compare("teamSize", Text(existing.TeamSize), Text(target.TeamSize), formatGuard);
        CompareJson("legsJson", existing.LegsJson, target.LegsJson,
            guard.TeamEntries ? TeamGuard : null);
        Compare("scoringWeight", Text(existing.ScoringWeight), Text(target.ScoringWeight),
            guard.Finalised ? ScoringGuard : null);
        CompareJson("tagsJson", existing.TagsJson, target.TagsJson);
        CompareJson("recordCategoriesJson", existing.RecordCategoriesJson,
            target.RecordCategoriesJson, guard.SchoolRecords ? RecordGuard : null);
        return diffs;
    }

    private static string? Canonical(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try { return JsonNode.Parse(json)?.ToJsonString() ?? json; }
        catch (JsonException) { return json; }
    }

    private static string Text(bool value) => value ? "true" : "false";
    private static string? Text(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Text(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Grouped reads rather than per-row: the catalogue is small but the joins behind it are not.
    private async Task<Dictionary<Guid, Guards>> GuardsAsync(
        IReadOnlyList<EventDefinition> catalogue, CancellationToken ct)
    {
        var withResults = (await db.Results.AsNoTracking()
            .Select(r => r.Entry.CarnivalEvent.EventDefinitionId).Distinct().ToListAsync(ct)).ToHashSet();
        var withEntries = (await db.Entries.AsNoTracking()
            .Select(e => e.CarnivalEvent.EventDefinitionId).Distinct().ToListAsync(ct)).ToHashSet();
        var withTeamEntries = (await db.Entries.AsNoTracking()
            .Where(e => e.HouseCode != null)
            .Select(e => e.CarnivalEvent.EventDefinitionId).Distinct().ToListAsync(ct)).ToHashSet();
        var finalised = (await db.CarnivalEvents.AsNoTracking()
            .Where(e => e.Carnival.Status == CarnivalStatus.Finalised)
            .Select(e => e.EventDefinitionId).Distinct().ToListAsync(ct)).ToHashSet();
        // Voided records count too: the row still cites the categories.
        var recorded = (await db.SchoolRecords.AsNoTracking()
            .Select(r => r.EventDefinitionId).Distinct().ToListAsync(ct)).ToHashSet();

        return catalogue.ToDictionary(d => d.Id, d => new Guards(
            withResults.Contains(d.Id), withEntries.Contains(d.Id), withTeamEntries.Contains(d.Id),
            finalised.Contains(d.Id), recorded.Contains(d.Id)));
    }

    private async Task<HashSet<string>> EditedInAppAsync(CancellationToken ct) =>
        (await db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.EventDefinitionUpdate
                        && a.EntityType == nameof(EventDefinition)
                        && (a.RuleVersion == null || a.RuleVersion != ReconcileRuleVersion))
            .Select(a => a.EntityId)
            .Distinct()
            .ToListAsync(ct))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Guards are hashed too: a result arriving between preview and apply freezes a field without
    // moving a value, and must still invalidate the preview. The version is only the file name.
    private static string HashOf(EventTemplate template, IEnumerable<PlannedRow> rows) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new HashInput(template.Version, template.ContentHash, [.. rows.Select(HashRowOf)]))));

    private static HashRow HashRowOf(PlannedRow planned) => new(
        planned.Row.Code,
        planned.Row.Kind.ToString(),
        planned.Row.Kind == TemplateDiffKind.Missing && planned.Target is { } target
            ? [.. CreatedValues(target)]
            : [.. planned.Row.Fields.Select(f =>
                new HashField(f.Field, f.Prior, f.New, f.Guard is not null))]);

    private static IEnumerable<HashField> CreatedValues(TemplateValues t) =>
    [
        new("name", null, t.Name, false),
        new("sport", null, t.Sport, false),
        new("measureType", null, t.MeasureType.ToString(), false),
        new("attemptCount", null, Text(t.AttemptCount), false),
        new("ladderJson", null, t.LadderJson, false),
        new("attemptsPerHeight", null, Text(t.AttemptsPerHeight), false),
        new("units", null, t.Units, false),
        new("isActive", null, Text(t.IsActive), false),
        new("format", null, t.Format.ToString(), false),
        new("teamSize", null, Text(t.TeamSize), false),
        new("legsJson", null, t.LegsJson, false),
        new("scoringWeight", null, Text(t.ScoringWeight), false),
        new("tagsJson", null, t.TagsJson, false),
        new("recordCategoriesJson", null, t.RecordCategoriesJson, false),
    ];

    private sealed record HashField(string Field, string? Prior, string? New, bool Guarded);

    private sealed record HashRow(string Code, string Kind, IReadOnlyList<HashField> Fields);

    private sealed record HashInput(
        string Version, string ContentHash, IReadOnlyList<HashRow> Rows);
}
