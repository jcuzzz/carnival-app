using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The shipped template document plus the counts DERIVED from it. Asserting
/// literal totals ("102 events") turned every legitimate catalogue edit —
/// activating a relay, adding an age group — into seven unrelated test failures
/// that said nothing about whether apply-template still worked. These helpers
/// state the scheduling rule instead (ADR-0003/0008 as amended by ADR-0058 §1:
/// one event per ACTIVE definition × age × category × tier, plus one OPEN
/// all-ages division per category and tier when the definition declares
/// one), so the expectation moves with the document. The one intentional pin on
/// today's catalogue lives in
/// <see cref="SwimmingTemplateDocumentTests.CANARY_current_swimming_catalogue_and_totals"/>.
/// </summary>
internal static class TemplateExpectation
{
    public static EventTemplate Load(string key = "swimming") =>
        EventTemplate.LoadFromFile(Fixtures.Path(Path.Combine("event-templates", $"{key}.json")));

    /// <summary>Divisions a single definition is contested in.</summary>
    public static int Divisions(EventTemplate template, EventTemplateDefinition definition) =>
        (template.AgeGroups.Count + (definition.OpenDivision ? 1 : 0))
        * template.Categories.Count
        * template.TiersFor(definition).Count;

    public static int Divisions(EventTemplate template, string code) =>
        Divisions(template, template.Definitions.Single(d => d.Code == code));

    /// <summary>Every definition is catalogued, active or not.</summary>
    public static int CataloguedDefinitions(EventTemplate template) => template.Definitions.Count;

    /// <summary>Only ACTIVE definitions are scheduled.</summary>
    public static int ScheduledEvents(EventTemplate template) =>
        template.Definitions.Where(d => d.Active).Sum(d => Divisions(template, d));

    public static IReadOnlyList<string> InactiveCodes(EventTemplate template) =>
        template.Definitions.Where(d => !d.Active).Select(d => d.Code).ToList();
}

/// <summary>
/// The swimming event template document itself (config/event-templates/
/// swimming.json — the REAL shipped file, copied into Fixtures at build):
/// catalogue contents per the owner spec of 2026-07-28, and the age-group
/// label agreement with the roster import derivation. The labels MUST agree
/// exactly or entries will never match the template's events.
/// </summary>
public class SwimmingTemplateDocumentTests
{
    private static EventTemplate Load() => TemplateExpectation.Load();

    /// <summary>
    /// CANARY — intentional change-detector, NOT a behaviour assertion.
    ///
    /// Every other count in this file is derived from the loaded document
    /// (<see cref="TemplateExpectation"/>) so a legitimate catalogue edit changes
    /// the expectation rather than breaking seven tests. This single test states
    /// what the swimming template ships TODAY, so an edit is never silent.
    ///
    /// If you deliberately changed config/event-templates/swimming.json, update
    /// this one test and move on — a failure here ALONE means the document
    /// changed, not that apply-template broke.
    /// </summary>
    [Fact]
    public void CANARY_current_swimming_catalogue_and_totals()
    {
        var t = Load();
        Assert.Equal(
            ["SW-50-FREE", "SW-50-BREAST", "SW-50-BACK", "SW-50-FLY", "SW-NOVELTY",
             "SW-4X50-FREE-RELAY", "SW-4X50-MEDLEY-RELAY"],
            t.Definitions.Select(d => d.Code));
        Assert.Equal(7, TemplateExpectation.CataloguedDefinitions(t));   // 4 strokes + novelty + 2 relays
        Assert.Equal(158, TemplateExpectation.ScheduledEvents(t));       // 4 × 28 + 14 + 2 × 16
        Assert.Equal(28, TemplateExpectation.Divisions(t, "SW-50-FREE"));            // 7 ages × 2 × C+S
        Assert.Equal(14, TemplateExpectation.Divisions(t, "SW-NOVELTY"));            // 7 ages × 2, C only
        Assert.Equal(16, TemplateExpectation.Divisions(t, "SW-4X50-FREE-RELAY"));    // + open, × 2, C only
        Assert.Empty(TemplateExpectation.InactiveCodes(t));              // nothing catalogue-only today

        // The default program (ADR-0014 §1): one stream, the 2026 running sheet's
        // fourteen slots — five competition blocks and nine items.
        Assert.Equal(["Pool"], t.Streams.Select(s => s.Name));
        Assert.Equal(14, t.Streams[0].Slots.Count);
        Assert.Equal(5, t.Streams[0].Slots.Count(s => s.HoldsEvents));
    }

    [Fact]
    public void Every_definition_is_a_timed_swimming_event()
    {
        var template = Load();
        Assert.Equal("swimming", template.Key);
        Assert.All(template.Definitions, d =>
        {
            Assert.Equal("Swimming", d.Sport);
            Assert.Equal(MeasureType.Time, d.MeasureType);
            Assert.Equal("s", d.Units);
        });
    }

    [Fact]
    public void Individual_strokes_carry_school_records_and_novelty_does_not()
    {
        var template = Load();
        foreach (var stroke in new[] { "SW-50-FREE", "SW-50-BREAST", "SW-50-BACK", "SW-50-FLY" })
        {
            var definition = template.Definitions.Single(d => d.Code == stroke);
            Assert.True(definition.Active);
            Assert.True(definition.SchoolRecords);
        }
        var novelty = template.Definitions.Single(d => d.Code == "SW-NOVELTY");
        Assert.True(novelty.Active);          // a real placed/scored pool event...
        Assert.False(novelty.SchoolRecords);  // ...with no school-record category.
    }

    [Fact]
    public void Relays_are_active_team_events_scoring_double_with_an_open_division()
    {
        var template = Load();
        foreach (var relay in new[] { "SW-4X50-FREE-RELAY", "SW-4X50-MEDLEY-RELAY" })
        {
            var d = template.Definitions.Single(x => x.Code == relay);
            Assert.True(d.Active);                        // ADR-0008: activated 2026-07-30
            Assert.Equal(EventFormat.Team, d.Format);
            Assert.Equal(4, d.TeamSize);
            Assert.Equal(4, d.Legs!.Count);
            Assert.Equal(2, d.ScoringWeight);             // relays score double
            Assert.True(d.OpenDivision);                  // plus one all-ages house relay
            Assert.True(d.SchoolRecords);                 // relay records are tracked
        }
        // Medley leg order (back → breast → fly → free); freestyle relay is 4 free legs.
        Assert.Equal(
            ["Backstroke", "Breaststroke", "Butterfly", "Freestyle"],
            template.Definitions.Single(d => d.Code == "SW-4X50-MEDLEY-RELAY").Legs);
        Assert.Equal(
            ["Freestyle", "Freestyle", "Freestyle", "Freestyle"],
            template.Definitions.Single(d => d.Code == "SW-4X50-FREE-RELAY").Legs);
    }

    [Fact]
    public void Definitions_carry_faceted_discipline_tags()
    {
        // Faceted convention: "facet:value" (discipline/distance/…) so a sport's
        // tags render as one filter dropdown per facet, sport-agnostically.
        var template = Load();
        Assert.Equal(["discipline:Freestyle", "distance:50m"],
            template.Definitions.Single(d => d.Code == "SW-50-FREE").Tags);
        Assert.Equal(["discipline:Backstroke", "distance:50m"],
            template.Definitions.Single(d => d.Code == "SW-50-BACK").Tags);
        Assert.Equal(["discipline:Medley"],
            template.Definitions.Single(d => d.Code == "SW-4X50-MEDLEY-RELAY").Tags);
        Assert.Contains("discipline:Freestyle",
            template.Definitions.Single(d => d.Code == "SW-4X50-FREE-RELAY").Tags!);
    }

    [Fact]
    public void Divisions_are_single_ages_by_boys_and_girls_at_both_tiers()
    {
        // Owner correction 2026-08-07: this school swims SINGLE-AGE events. The
        // printed program's "16+" line is a SESSION label, not a division — 16,
        // 17 and 18 are separate events with their own placings and their own
        // house points (three 1st places, not one), exactly as the program
        // prints one line for a stroke it in fact splits M/F. Ages that run
        // together when participation is low are a slot holding several
        // instances (ADR-0014 §4), never one merged event.
        var template = Load();
        var ages = new[] { "12", "13", "14", "15", "16", "17", "18" };
        Assert.Equal(ages, template.AgeGroups);
        Assert.Equal([CompetitionCategory.M, CompetitionCategory.F], template.Categories);
        Assert.Equal([EventTier.Competitive, EventTier.Social], template.Tiers);
    }

    [Fact]
    public void Novelty_and_relays_are_contested_at_the_competitive_tier_only()
    {
        // The 2026 program pairs each STROKE with a Social twin, but runs the
        // novelty items as year-level activities and has a single HOUSE RELAY
        // block. Generating Social twins of those would invent events the school
        // does not run, so the document overrides their tiers per definition.
        var template = Load();
        foreach (var stroke in new[] { "SW-50-FREE", "SW-50-BREAST", "SW-50-BACK", "SW-50-FLY" })
        {
            Assert.Null(template.Definitions.Single(d => d.Code == stroke).Tiers); // inherits both
            Assert.Equal(template.Tiers,
                template.TiersFor(template.Definitions.Single(d => d.Code == stroke)));
        }
        foreach (var competitiveOnly in
                 new[] { "SW-NOVELTY", "SW-4X50-FREE-RELAY", "SW-4X50-MEDLEY-RELAY" })
            Assert.Equal([EventTier.Competitive],
                template.TiersFor(template.Definitions.Single(d => d.Code == competitiveOnly)));
    }

    [Fact]
    public void Template_age_groups_agree_exactly_with_import_derivation_labels()
    {
        // Age as at 31 Dec of the competition year (SSV convention). The
        // derivation emits the plain age as the label ("12".."18"); the
        // template must use those exact strings.
        var rule = new AgeGroupRule { CutOffMonth = 12, CutOffDay = 31 };
        var derived = Enumerable.Range(12, 7) // ages 12..18
            .Select(age => rule.DeriveAgeGroup(
                new DateOnly(Fixtures.CompetitionYear - age, 6, 15), Fixtures.CompetitionYear))
            .ToList();
        Assert.Equal(Load().AgeGroups, derived);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(19)]
    public void Out_of_range_ages_simply_have_no_matching_division(int age)
    {
        var rule = new AgeGroupRule { CutOffMonth = 12, CutOffDay = 31 };
        var label = rule.DeriveAgeGroup(
            new DateOnly(Fixtures.CompetitionYear - age, 6, 15), Fixtures.CompetitionYear);
        Assert.DoesNotContain(label, Load().AgeGroups);
    }
}

/// <summary>
/// POST /api/v1/carnivals/{id}/apply-template: instantiates the template's
/// divisions idempotently (fresh factory per test — deterministic catalogue
/// counts). Authorisation rows live in CarnivalAuthorizationHttpTests.
///
/// Expected counts are computed from the loaded template document
/// (<see cref="TemplateExpectation"/>) so these tests assert the SCHEDULING RULE,
/// not the size of today's swimming catalogue; the pin on today's numbers is the
/// single canary in <see cref="SwimmingTemplateDocumentTests"/>.
/// </summary>
public sealed class ApplyTemplateEndpointTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly EventTemplate _template = TemplateExpectation.Load();

    public ApplyTemplateEndpointTests() =>
        _admin = _factory.CreateClientAs("event-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> CreateCarnivalAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Swimming Carnival 2027", type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> ApplyAsync(Guid carnivalId, string? template = "swimming") =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template", new { template });

    [Fact]
    public async Task Apply_catalogues_every_definition_and_schedules_every_active_division()
    {
        var carnivalId = await CreateCarnivalAsync();
        var response = await ApplyAsync(carnivalId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TemplateExpectation.CataloguedDefinitions(_template),
            body.GetProperty("definitionsCreated").GetInt32());
        Assert.Equal(0, body.GetProperty("definitionsExisting").GetInt32());
        Assert.Equal(TemplateExpectation.ScheduledEvents(_template),
            body.GetProperty("eventsCreated").GetInt32());
        Assert.Equal(0, body.GetProperty("eventsAlreadyScheduled").GetInt32());
        Assert.Equal(TemplateExpectation.InactiveCodes(_template),
            body.GetProperty("definitionsSkippedInactive").EnumerateArray()
                .Select(e => e.GetString()!).ToList());

        var events = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId)
            .ToListAsync());
        Assert.Equal(TemplateExpectation.ScheduledEvents(_template), events.Count);
        Assert.All(events, e => Assert.Equal(CarnivalEventStatus.Scheduled, e.Status));

        // Every ACTIVE definition, and only those, gets its full division set.
        Assert.Equal(
            _template.Definitions.Where(d => d.Active).Select(d => d.Code).Order(),
            events.Select(e => e.EventDefinition.Code).Distinct().Order());
        foreach (var group in events.GroupBy(e => e.EventDefinition.Code))
        {
            var definition = _template.Definitions.Single(d => d.Code == group.Key);
            Assert.Equal(_template.Categories,
                group.Select(e => e.Category!.Value).Distinct().OrderBy(c => c));
            Assert.Equal(TemplateExpectation.Divisions(_template, definition), group.Count());
            Assert.Equal(_template.AgeGroups.Order(),
                group.Select(e => e.AgeGroup).Where(a => a != AgeScope.OpenKey).Distinct().Order());
            Assert.Equal(_template.TiersFor(definition).Order(),
                group.Select(e => e.Tier).Distinct().Order());
            // ADR-0008: an open-division event adds one all-ages division per
            // category and tier on top; everything else is ages only.
            Assert.Equal(
                definition.OpenDivision
                    ? _template.Categories.Count * _template.TiersFor(definition).Count
                    : 0,
                group.Count(e => e.AgeGroup == AgeScope.OpenKey));
        }
    }

    [Fact]
    public async Task Every_declared_age_schedules_its_own_divisions()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var events = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Where(e => e.CarnivalId == carnivalId)
            .ToListAsync());
        foreach (var age in _template.AgeGroups)
            Assert.Contains(events, e => e.AgeGroup == age);
    }

    [Fact]
    public async Task Both_tiers_of_a_division_are_scheduled_and_score_differently()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var stroke = _template.Definitions.First(d => _template.TiersFor(d).Count > 1);
        var events = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId && e.EventDefinition.Code == stroke.Code)
            .ToListAsync());

        // The same stroke, age and category exists at each tier: only the tier
        // component of the uniqueness key separates the pair.
        foreach (var division in events.GroupBy(e => (e.AgeGroup, e.Category)))
            Assert.Equal(_template.TiersFor(stroke).Order(),
                division.Select(e => e.Tier).Order());
        // Neither carries an explicit mode — the tier decides it (ADR-0014 §4),
        // which is what keeps the UI from having to badge every event.
        Assert.All(events, e => Assert.Null(e.ScoringMode));
        Assert.All(events.Where(e => e.Tier == EventTier.Social),
            e => Assert.Equal(EventScoringMode.Social, e.EffectiveScoringMode));
        Assert.All(events.Where(e => e.Tier == EventTier.Competitive),
            e => Assert.Equal(EventScoringMode.Places, e.EffectiveScoringMode));
        // Both tiers score house points by default — Social is not a points-free
        // zone (that is what the ADR-0005 novelty adjustments are).
        Assert.All(events, e => Assert.True(e.ScoresPoints));
    }

    [Fact]
    public async Task Applied_result_names_the_ages_and_tiers_it_scheduled()
    {
        var carnivalId = await CreateCarnivalAsync();
        var body = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(_template.AgeGroups,
            body.GetProperty("ageGroups").EnumerateArray().Select(a => a.GetString()!));
        Assert.Equal(["Competitive", "Social"],
            body.GetProperty("tiers").EnumerateArray().Select(t => t.GetString()!));
    }

    [Fact]
    public async Task Catalogue_rows_mirror_the_template_definitions()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var definitions = await CarnivalSeed.QueryAsync(_factory, db =>
            db.EventDefinitions.ToDictionaryAsync(d => d.Code));
        foreach (var templated in _template.Definitions)
        {
            var row = definitions[templated.Code];
            Assert.Equal(templated.Active, row.IsActive);
            // Record categories are written only for definitions that declare
            // school records — the novelty event deliberately has none.
            if (templated.SchoolRecords) Assert.NotNull(row.RecordCategoriesJson);
            else Assert.Null(row.RecordCategoriesJson);
            // ADR-0008 team attributes ride the catalogue row unchanged.
            Assert.Equal(templated.Format, row.Format);
            Assert.Equal(templated.TeamSize, row.TeamSize);
            Assert.Equal(templated.ScoringWeight, row.ScoringWeight);
            // Faceted discipline tags persist onto the row (drives the event filter).
            foreach (var tag in templated.Tags ?? []) Assert.Contains(tag, row.TagsJson!);
        }
    }

    /// <summary>
    /// CANARY — what the shipped swimming template catalogues as its record
    /// categories, pinned byte for byte. The literal is the exact string the
    /// service writes; it is pinned rather than derived, because a value computed
    /// from the same helper the code uses would prove nothing at all.
    ///
    /// A failure here alone means the shipped template's divisions changed, or
    /// the serialisation did — not that apply-template broke.
    /// </summary>
    [Fact]
    public async Task CANARY_shipped_swimming_divisions_and_record_categories_byte_for_byte()
    {
        const string expected =
            """{"ageGroups":["12","13","14","15","16","17","18"],"categories":["M","F"]}""";

        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var definitions = await CarnivalSeed.QueryAsync(_factory, db => db.EventDefinitions.ToListAsync());
        foreach (var templated in _template.Definitions)
            // The schoolRecords flag alone decides it: nothing about the tier the
            // school contests the definition at enters the catalogue row.
            Assert.Equal(
                templated.SchoolRecords ? expected : null,
                definitions.Single(d => d.Code == templated.Code).RecordCategoriesJson);
    }

    [Fact]
    public async Task Reapply_is_idempotent()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var second = await ApplyAsync(carnivalId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        var events = TemplateExpectation.ScheduledEvents(_template);
        Assert.Equal(0, body.GetProperty("definitionsCreated").GetInt32());
        Assert.Equal(TemplateExpectation.CataloguedDefinitions(_template),
            body.GetProperty("definitionsExisting").GetInt32());
        Assert.Equal(0, body.GetProperty("eventsCreated").GetInt32());
        Assert.Equal(events, body.GetProperty("eventsAlreadyScheduled").GetInt32());

        Assert.Equal(events, await CarnivalSeed.QueryAsync(_factory, db =>
            db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId)));
    }

    [Fact]
    public async Task Apply_skips_divisions_that_were_scheduled_by_hand()
    {
        var carnivalId = await CreateCarnivalAsync();
        // An administrator has already catalogued the template's first individual
        // event and scheduled one of its divisions by hand before applying.
        var byHand = _template.Definitions.First(d => d.Active && d.Format == EventFormat.Individual);
        var definitionResponse = await _admin.PostAsJsonAsync("/api/v1/event-definitions",
            new
            {
                code = byHand.Code,
                name = byHand.Name,
                sport = byHand.Sport,
                measureType = byHand.MeasureType.ToString(),
                units = byHand.Units,
            });
        Assert.Equal(HttpStatusCode.Created, definitionResponse.StatusCode);
        var definitionId = (await definitionResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var scheduled = await _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events",
            new
            {
                eventDefinitionId = definitionId,
                ageGroup = _template.AgeGroups[0],
                category = _template.Categories[0].ToString(),
            });
        Assert.Equal(HttpStatusCode.Created, scheduled.StatusCode);

        var response = await ApplyAsync(carnivalId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var events = TemplateExpectation.ScheduledEvents(_template);
        Assert.Equal(TemplateExpectation.CataloguedDefinitions(_template) - 1,
            body.GetProperty("definitionsCreated").GetInt32());
        Assert.Equal(1, body.GetProperty("definitionsExisting").GetInt32());
        Assert.Equal(events - 1, body.GetProperty("eventsCreated").GetInt32()); // the hand-scheduled one
        Assert.Equal(1, body.GetProperty("eventsAlreadyScheduled").GetInt32());
        Assert.Equal(events, await CarnivalSeed.QueryAsync(_factory, db =>
            db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId)));
    }

    [Fact]
    public async Task Apply_refuses_a_closed_carnival()
    {
        var carnivalId = await CreateCarnivalAsync();
        foreach (var status in new[] { "Open", "Closed" })
            Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
                $"/api/v1/carnivals/{carnivalId}/status", new { status })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await ApplyAsync(carnivalId)).StatusCode);
    }

    [Fact]
    public async Task Unknown_or_missing_template_is_a_400()
    {
        var carnivalId = await CreateCarnivalAsync();

        var unknown = await ApplyAsync(carnivalId, "netball");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await unknown.AssertErrorAsync(ErrorCodes.TemplateInvalid);

        Assert.Equal(HttpStatusCode.BadRequest, (await ApplyAsync(carnivalId, null)).StatusCode);
        // A template key is a file-name component, never a path.
        Assert.Equal(HttpStatusCode.BadRequest, (await ApplyAsync(carnivalId, "../swimming")).StatusCode);
    }

    [Fact]
    public async Task Unknown_carnival_is_a_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await ApplyAsync(Guid.NewGuid())).StatusCode);

    [Fact]
    public async Task Apply_is_audited_with_the_template_and_counts()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var audit = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .SingleAsync(a => a.Action == AuditActions.CarnivalApplyTemplate));
        Assert.Equal("event-admin", audit.ActorSubject);
        Assert.Equal(carnivalId.ToString(), audit.EntityId);
        Assert.Contains(_template.Key, audit.NewValuesJson);
        Assert.Contains($"\"EventsCreated\":{TemplateExpectation.ScheduledEvents(_template)}",
            audit.NewValuesJson);
        // The catalogue creations are audited individually as well.
        Assert.Equal(TemplateExpectation.CataloguedDefinitions(_template),
            await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
                .CountAsync(a => a.Action == AuditActions.EventDefinitionCreate)));
    }

    [Fact]
    public async Task Relay_events_are_scheduled_per_age_group_and_open_per_gender()
    {
        var carnivalId = await CreateCarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var relay = _template.Definitions.First(d => d.Active && d.OpenDivision);
        var relayEvents = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId && e.EventDefinition.Code == relay.Code)
            .ToListAsync());

        // (age groups + one open) × categories.
        Assert.Equal(TemplateExpectation.Divisions(_template, relay), relayEvents.Count);
        // One OPEN (all-ages) house relay per gender.
        var open = relayEvents.Where(e => e.AgeGroup == AgeScope.OpenKey).ToList();
        Assert.Equal(_template.Categories.Count, open.Count);
        Assert.Equal(_template.Categories, open.Select(e => e.Category!.Value).OrderBy(c => c));
        Assert.All(relayEvents, e => Assert.Equal(relay.Format, e.EventDefinition.Format));
    }
}

/// <summary>
/// Applying a template seeds the carnival's default PROGRAM (ADR-0014 §1): the
/// streams, their time slots, and the placement of each block's event instances.
///
/// The program is school CONFIGURATION, so every time, label and block name in
/// these tests comes from config/event-templates/swimming.json and never from
/// C# — that is the point of the JSON section, and asserting against the loaded
/// document rather than against literals is what keeps it true.
///
/// It is also ADVISORY: seeding it is additive, so the assertions here are about
/// what the printed program gained, never about what the carnival can do. A
/// template with no schedule section, or an event left in no slot, is a fully
/// working carnival.
/// </summary>
public sealed class TemplateScheduleSeedingTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly EventTemplate _template = TemplateExpectation.Load();

    public TemplateScheduleSeedingTests() =>
        _admin = _factory.CreateClientAs("event-admin-program", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private EventTemplateStream Pool => _template.Streams.Single();

    private async Task<Guid> CarnivalAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Program Carnival", type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> ApplyAsync(Guid carnivalId) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "swimming" });

    private Task<List<ScheduleSlot>> SlotsAsync(Guid carnivalId) =>
        CarnivalSeed.QueryAsync(_factory, db => db.ScheduleSlots
            .Include(s => s.Events).ThenInclude(e => e.EventDefinition)
            .Where(s => s.Stream.CarnivalId == carnivalId)
            .ToListAsync());

    [Fact]
    public async Task Apply_seeds_the_templates_stream_and_every_slot_it_declares()
    {
        var carnivalId = await CarnivalAsync();
        var body = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_template.Streams.Count, body.GetProperty("streamsCreated").GetInt32());
        Assert.Equal(Pool.Slots.Count, body.GetProperty("slotsCreated").GetInt32());

        var stream = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleStreams
            .SingleAsync(s => s.CarnivalId == carnivalId));
        Assert.Equal(Pool.Name, stream.Name);

        var slots = await SlotsAsync(carnivalId);
        // Labels and times come from the document, in its order — the slot's
        // SortOrder is the program's running order down the stream.
        Assert.Equal(
            Pool.Slots.Select(s => s.Label),
            slots.OrderBy(s => s.SortOrder).Select(s => s.Label));
        Assert.Equal(
            Pool.Slots.Select(s => s.Start),
            slots.OrderBy(s => s.SortOrder).Select(s => s.StartTime));
        // Kind is derived from whether the slot lists events, never declared.
        Assert.Equal(
            Pool.Slots.Select(s => s.HoldsEvents ? ScheduleSlotKind.Events : ScheduleSlotKind.Item),
            slots.OrderBy(s => s.SortOrder).Select(s => s.Kind));
    }

    [Fact]
    public async Task A_stroke_block_holds_that_strokes_instances_in_age_category_tier_order()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);
        var slots = await SlotsAsync(carnivalId);

        foreach (var templated in Pool.Slots.Where(s => s.HoldsEvents))
        {
            var slot = slots.Single(s => s.Label == templated.Label);
            var listed = slot.Events.OrderBy(e => e.SlotOrder).ToList();
            // Every instance of every definition the block names, and nothing else.
            Assert.Equal(
                templated.DefinitionCodes.Sum(code =>
                    TemplateExpectation.Divisions(_template, code)),
                listed.Count);
            Assert.Equal(templated.DefinitionCodes,
                listed.Select(e => e.EventDefinition.Code).Distinct());
            // Contiguous listed order from 0 — this is the order the block prints
            // in and the order "next event →" walks.
            Assert.Equal(Enumerable.Range(0, listed.Count), listed.Select(e => e.SlotOrder!.Value));
        }

        // Age, then category, then tier — the order the printed program reads
        // in ("12 boys competitive, 12 boys social, 12 girls…").
        var freestyle = slots.Single(s => s.Events.Any(e => e.EventDefinition.Code == "SW-50-FREE"));
        var head = freestyle.Events.OrderBy(e => e.SlotOrder)
            .Take(4)
            .Select(e => $"{e.AgeGroup}/{e.Category}/{e.Tier}")
            .ToList();
        var youngest = _template.AgeGroups[0];
        Assert.Equal(
            [$"{youngest}/M/Competitive", $"{youngest}/M/Social",
             $"{youngest}/F/Competitive", $"{youngest}/F/Social"],
            head);
    }

    [Fact]
    public async Task Novelty_and_logistics_slots_are_items_that_hold_nothing()
    {
        // ADR-0005: a novelty item names its adjustment category so the program
        // and the adjustments panel agree, and produces no results ever.
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);
        var slots = await SlotsAsync(carnivalId);

        foreach (var templated in Pool.Slots.Where(s => !s.HoldsEvents))
        {
            var slot = slots.Single(s => s.Label == templated.Label);
            Assert.Equal(ScheduleSlotKind.Item, slot.Kind);
            Assert.Empty(slot.Events);
            Assert.Equal(templated.AdjustmentCategory, slot.AdjustmentCategory);
        }

        // Every category the document's items name is one the carnival actually
        // has configured, or the program would point at adjustments that do not exist.
        var configured = CarnivalApp.Api.Scoring.CarnivalSettings.DefaultAdjustmentCategories;
        foreach (var category in Pool.Slots.Select(s => s.AdjustmentCategory).Where(c => c is not null))
            Assert.Contains(category!, configured);
    }

    [Fact]
    public async Task Events_the_program_does_not_list_stay_unscheduled_and_fully_usable()
    {
        // The 2026 running sheet has no block for the scored novelty race, so its
        // instances start in no slot. That is an ORDINARY state, not a gap: they
        // take entries and results exactly like a blocked event (ADR-0014 §1).
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var unscheduled = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId && e.ScheduleSlotId == null)
            .ToListAsync());
        Assert.NotEmpty(unscheduled);
        var listedCodes = Pool.Slots.SelectMany(s => s.DefinitionCodes).ToHashSet();
        Assert.All(unscheduled, e => Assert.DoesNotContain(e.EventDefinition.Code, listedCodes));

        // They are ordinary events: open one and it accepts an entry.
        var target = unscheduled.First(e => e.EventDefinition.Format == EventFormat.Individual);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/{target.Id}/status", new { status = "Open" })).StatusCode);
    }

    [Fact]
    public async Task Reapply_creates_no_second_stream_slot_or_placement()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);
        var placedFirst = await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId && e.ScheduleSlotId != null));

        var second = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, second.GetProperty("streamsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("slotsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("eventsPlaced").GetInt32());

        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory,
            db => db.ScheduleStreams.CountAsync(s => s.CarnivalId == carnivalId)));
        Assert.Equal(Pool.Slots.Count, (await SlotsAsync(carnivalId)).Count);
        Assert.Equal(placedFirst, await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId && e.ScheduleSlotId != null)));
    }

    [Fact]
    public async Task Reapply_never_overwrites_the_organisers_program()
    {
        // The organiser owns their program; the template is only its starting
        // point. Re-applying must not resurrect a time they corrected, nor drag
        // an event back out of the block they moved it to.
        //
        // What it does NOT protect, deliberately: slot identity is the LABEL, so
        // a renamed or deleted slot IS re-created by a re-apply. That is the
        // limit of seeding, and closing it needs template provenance and the
        // diffed `reconcile-template` action on the backlog — not a wider claim
        // here.
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);
        var slots = await SlotsAsync(carnivalId);
        var freestyle = slots.Single(s => s.Events.Any(e => e.EventDefinition.Code == "SW-50-FREE"));
        var backstroke = slots.Single(s => s.Events.Any(e => e.EventDefinition.Code == "SW-50-BACK"));
        var moved = freestyle.Events.OrderBy(e => e.SlotOrder).First().Id;
        var schedulePath = $"/api/v1/carnivals/{carnivalId}/schedule";

        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"{schedulePath}/slots/{freestyle.Id}",
            new { label = freestyle.Label, startTime = "10:15" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync(
            $"{schedulePath}/slots/{backstroke.Id}/events", new { eventId = moved })).StatusCode);

        var reapplied = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, reapplied.GetProperty("slotsCreated").GetInt32());
        Assert.Equal(0, reapplied.GetProperty("eventsPlaced").GetInt32());

        var after = await SlotsAsync(carnivalId);
        Assert.Equal(new TimeOnly(10, 15), after.Single(s => s.Id == freestyle.Id).StartTime);
        Assert.Equal(backstroke.Id, (await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.SingleAsync(e => e.Id == moved))).ScheduleSlotId);
    }

    [Fact]
    public async Task Apply_seeds_nothing_when_the_template_declares_no_program()
    {
        // Every school's template is configuration this project does not control,
        // and the schedule section is optional: a document without one applies
        // exactly as it did before ADR-0014, with its events unscheduled.
        var template = EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes("""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": { "ageGroups": ["13"], "categories": ["M"] },
              "definitions": [
                { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Time", "units": "s" }
              ]
            }
            """)), "no-schedule.json");
        Assert.Empty(template.Streams);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "No-program carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Draft,
            RuleVersion = "v1",
            OwnerSubject = "program-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        await db.SaveChangesAsync();

        var applied = await scope.ServiceProvider.GetRequiredService<EventTemplateService>()
            .ApplyAsync(carnival, template, new AuditActor("program-test", "CarnivalAdministrator"), default);
        Assert.Equal(1, applied.EventsCreated);
        Assert.Equal(0, applied.StreamsCreated);
        Assert.Equal(0, applied.SlotsCreated);
        Assert.Equal(0, applied.EventsPlaced);
        Assert.Equal(0, await db.ScheduleStreams.CountAsync(s => s.CarnivalId == carnival.Id));
        Assert.All(await db.CarnivalEvents.Where(e => e.CarnivalId == carnival.Id).ToListAsync(),
            e => Assert.Null(e.ScheduleSlotId));
    }

    [Theory]
    // Every one of these is silent until deck day if it loads: a block that holds
    // nothing, a program line with no text, a slot that cannot be told apart from
    // its neighbour on re-apply, a house relay scheduled before breakfast.
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "start": "09:45", "label": "FREESTYLE", "events": ["SW-NOPE"] }] }] }
        """, "unknown event definition 'SW-NOPE'")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [{ "start": "09:45" }] }] }
        """, "needs a label")]
    [InlineData("""
        "schedule": { "streams": [{ "slots": [{ "label": "X" }] }] }
        """, "every schedule stream needs a name")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "label": "Buses" }, { "label": "buses" }] }] }
        """, "duplicate slot labels")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [] },
          { "name": "pool", "slots": [] }] }
        """, "duplicate schedule stream names")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "start": "1:30", "label": "HOUSE RELAY" }] }] }
        """, "not a 24-hour local time")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "start": "11:00", "end": "10:00", "label": "SHOT PUT" }] }] }
        """, "ends before it starts")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "end": "10:00", "label": "SHOT PUT" }] }] }
        """, "end time but no start time")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "label": "Novelty", "events": ["TT-A"], "adjustmentCategory": "Novelty 1" }] }] }
        """, "lists events AND an adjustment category")]
    [InlineData("""
        "schedule": { "streams": [] }
        """, "schedule.streams is empty")]
    [InlineData("""
        "schedule": { "streams": [{ "name": "Pool", "slots": [
          { "label": "X", "events": [12] }] }] }
        """, "neither a definition code nor a")]
    public void A_malformed_schedule_section_is_refused_by_name(string schedule, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""
                {
                  "template": "test",
                  "name": "Test sport",
                  "divisions": { "ageGroups": ["13"], "categories": ["M"] },
                  {{schedule}},
                  "definitions": [
                    { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Time", "units": "s" }
                  ]
                }
                """)), "schedule-test.json"));
        Assert.Contains("schedule-test.json", error.Message);
        Assert.Contains(expected, error.Message);
    }
}

/// <summary>
/// The load-bearing test behind the de-brittling: it proves
/// <see cref="TemplateExpectation"/>'s arithmetic is the scheduler's, against a
/// catalogue deliberately unlike swimming's (non-numeric ages, three categories,
/// a per-definition tier override, one inactive definition, one open-division
/// team event). Without this, the derived counts elsewhere in
/// this file would only be restating whatever <see cref="EventTemplateService"/>
/// happens to do. If the scheduling rule ever changes, this fails first and
/// explicitly.
/// </summary>
public sealed class TemplateExpectationTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static EventTemplateDefinition Definition(
        string code, bool active, IReadOnlyList<EventTier>? tiers = null) => new()
    {
        Code = code,
        Name = code,
        Sport = "Synthetic",
        MeasureType = MeasureType.Time,
        Units = "s",
        Active = active,
        Tiers = tiers,
    };

    private static EventTemplate Synthetic() => new()
    {
        Key = "synthetic",
        Name = "Synthetic sport",
        Version = "synthetic (in-memory)",
        ContentHash = "synthetic",
        AgeGroups = ["J", "I"],
        Categories = [CompetitionCategory.M, CompetitionCategory.F, CompetitionCategory.S],
        Tiers = [EventTier.Competitive, EventTier.Social],
        Definitions =
        [
            Definition("SY-A", active: true),
            Definition("SY-B", active: true, tiers: [EventTier.Competitive]),
            new EventTemplateDefinition
            {
                Code = "SY-RELAY",
                Name = "Relay",
                Sport = "Synthetic",
                MeasureType = MeasureType.Time,
                Units = "s",
                Active = true,
                Format = EventFormat.Team,
                TeamSize = 4,
                Legs = ["A", "B", "C", "D"],
                ScoringWeight = 2,
                OpenDivision = true,
                Tiers = [EventTier.Competitive],
            },
            Definition("SY-DEFERRED", active: false),
        ],
    };

    [Fact]
    public async Task Derived_counts_are_exactly_what_the_scheduler_creates()
    {
        var template = Synthetic();
        // 2 ages × 3 categories = 6 divisions per tier. SY-A inherits both tiers
        // (12); SY-B overrides to Competitive only (6); the open-division relay is
        // Competitive only and adds one all-ages row per category (9); the
        // inactive definition is catalogued, never scheduled.
        Assert.Equal(4, TemplateExpectation.CataloguedDefinitions(template));
        Assert.Equal(12 + 6 + 9, TemplateExpectation.ScheduledEvents(template));
        Assert.Equal(["SY-DEFERRED"], TemplateExpectation.InactiveCodes(template));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Synthetic carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Draft,
            RuleVersion = "v1",
            OwnerSubject = "template-expectation",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<EventTemplateService>();
        var applied = await service.ApplyAsync(
            carnival, template, new AuditActor("template-expectation", "CarnivalAdministrator"), default);

        Assert.Equal(TemplateExpectation.CataloguedDefinitions(template), applied.DefinitionsCreated);
        Assert.Equal(TemplateExpectation.ScheduledEvents(template), applied.EventsCreated);
        Assert.Equal(TemplateExpectation.InactiveCodes(template), applied.DefinitionsSkippedInactive);
        Assert.Equal(TemplateExpectation.ScheduledEvents(template),
            await db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnival.Id));

        // One division per age × category × tier, and only SY-A reached Social.
        var intermediate = await db.CarnivalEvents
            .Where(e => e.CarnivalId == carnival.Id && e.AgeGroup == "I")
            .ToListAsync();
        Assert.Equal(3 * 2 + 3 + 3, intermediate.Count);    // SY-A both tiers, SY-B, SY-RELAY
        Assert.Equal(["SY-A"], await db.CarnivalEvents
            .Where(e => e.CarnivalId == carnival.Id && e.Tier == EventTier.Social)
            .Select(e => e.EventDefinition.Code).Distinct().ToListAsync());
    }
}

/// <summary>
/// The template DOCUMENT's divisions section (ADR-0014 phase 2, ADR-0058 §3),
/// read straight from JSON rather than through a fixture file, because what
/// matters here is which documents the loader accepts.
///
/// Malformation is rejected HERE, at load, for the reason the loader has always
/// rejected a relay with the wrong number of legs: every one of these mistakes
/// is otherwise silent until deck day, when an age that matches no student or a
/// division scheduled twice is discovered by staff rather than by a sentence.
/// </summary>
public sealed class TemplateDivisionsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static EventTemplate Load(string json) =>
        EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "divisions-test.json");

    /// <summary>A document whose only interesting part is the axis under test.</summary>
    private static string Document(string axes) => $$"""
        {
          "template": "test",
          "name": "Test sport",
          {{axes}},
          "definitions": [
            { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Time", "units": "s" }
          ]
        }
        """;

    private const string Axes = """
        "divisions": { "ageGroups": ["12", "13"], "categories": ["M", "F"] }
        """;

    [Fact]
    public void A_document_that_says_nothing_about_tiers_is_competitive_only()
    {
        var template = Load(Document(Axes));
        Assert.Equal(["12", "13"], template.AgeGroups);
        Assert.Equal([CompetitionCategory.M, CompetitionCategory.F], template.Categories);
        Assert.Equal([EventTier.Competitive], template.Tiers);
        Assert.Null(template.Definitions[0].Tiers);
    }

    [Fact]
    public async Task One_division_is_scheduled_per_age_and_category()
    {
        var template = Load(Document(Axes));
        var events = await ApplyAsync(template);

        Assert.Equal(4, events.Count); // 2 ages × 2 categories, one tier
        Assert.All(events, e => Assert.Equal(EventTier.Competitive, e.Tier));
        Assert.Equal(["12", "13"], events.Select(e => e.AgeGroup).Distinct().Order());
    }

    [Fact]
    public void The_v1_category_axis_still_serves_a_divisions_section_that_omits_it()
    {
        // A school adopting tiers should not have to restate its categories.
        var template = Load(Document("""
            "categories": ["M", "F"],
              "divisions": { "ageGroups": ["12", "13"], "tiers": ["Competitive", "Social"] }
            """));
        Assert.Equal(["12", "13"], template.AgeGroups);
        Assert.Equal([CompetitionCategory.M, CompetitionCategory.F], template.Categories);
        Assert.Equal([EventTier.Competitive, EventTier.Social], template.Tiers);
    }

    [Fact]
    public async Task A_per_definition_tier_override_wins_over_the_templates_tiers()
    {
        var template = Load("""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": {
                "ageGroups": ["13"],
                "categories": ["M"],
                "tiers": ["Competitive", "Social"]
              },
              "definitions": [
                { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Time", "units": "s" },
                { "code": "TT-B", "name": "B", "sport": "Test", "measureType": "Time", "units": "s",
                  "tiers": ["Competitive"] }
              ]
            }
            """);
        Assert.Equal([EventTier.Competitive, EventTier.Social],
            template.TiersFor(template.Definitions.Single(d => d.Code == "TT-A")));
        Assert.Equal([EventTier.Competitive],
            template.TiersFor(template.Definitions.Single(d => d.Code == "TT-B")));

        var events = await ApplyAsync(template);
        Assert.Equal(3, events.Count); // TT-A at both tiers, TT-B at one
        Assert.Equal(["TT-A"], events.Where(e => e.Tier == EventTier.Social)
            .Select(e => e.EventDefinition.Code).Distinct());
    }

    [Fact]
    public async Task Reapplying_creates_nothing_at_either_tier()
    {
        var template = Load(Document("""
            "divisions": {
                "ageGroups": ["13", "16"],
                "categories": ["M", "F"],
                "tiers": ["Competitive", "Social"]
              }
            """));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<EventTemplateService>();
        var carnival = await CarnivalAsync(db);
        var actor = new AuditActor("divisions-test", "CarnivalAdministrator");

        var first = await service.ApplyAsync(carnival, template, actor, default);
        Assert.Equal(8, first.EventsCreated); // 2 ages × 2 categories × 2 tiers
        Assert.Equal(0, first.EventsAlreadyScheduled);

        var second = await service.ApplyAsync(carnival, template, actor, default);
        Assert.Equal(0, second.EventsCreated);
        Assert.Equal(8, second.EventsAlreadyScheduled);
        Assert.Equal(0, second.DefinitionsCreated);
        Assert.Equal(8, await db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnival.Id));
    }

    [Fact]
    public async Task A_record_category_is_one_age_and_tier_is_not_a_factor()
    {
        // TT-SOCIAL is the counter-case. ADR-0014's resolved question 9 says records
        // ignore Social — but that is a rule for record-CANDIDATE DETECTION, which
        // reads the tier off the event INSTANCE at result time, where it is mutable
        // (an EventAdministrator can flip an instance to Competitive, the ADR's own
        // escape path for a swimmer who wants a time to count). This catalogue row
        // is written once at first creation and never reconciled, so encoding the
        // template's tiers into it would silently and permanently disqualify a
        // definition the school later runs Competitively. Declaring schoolRecords is
        // the whole test.
        var template = Load("""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": {
                "ageGroups": ["12", "13"],
                "categories": ["M", "F"],
                "tiers": ["Competitive", "Social"]
              },
              "definitions": [
                { "code": "TT-RECORDS", "name": "Records", "sport": "Test", "measureType": "Time",
                  "units": "s", "schoolRecords": true },
                { "code": "TT-SOCIAL", "name": "Social only", "sport": "Test", "measureType": "Time",
                  "units": "s", "schoolRecords": true, "tiers": ["Social"] },
                { "code": "TT-NONE", "name": "No records", "sport": "Test", "measureType": "Time",
                  "units": "s" }
              ]
            }
            """);
        var (definitions, events) = await ApplyForCatalogueAsync(template);

        // ONE record category per age × category, named by the code the
        // CarnivalEvent rows carry.
        var records = JsonDocument.Parse(definitions["TT-RECORDS"].RecordCategoriesJson!).RootElement;
        Assert.Equal(["12", "13"],
            records.GetProperty("ageGroups").EnumerateArray().Select(a => a.GetString()!));
        Assert.Equal(["M", "F"],
            records.GetProperty("categories").EnumerateArray().Select(c => c.GetString()!));
        // It is exactly the age axis the scheduler used, so a result can be joined
        // to its category by AgeGroup with no translation step.
        Assert.Equal(
            records.GetProperty("ageGroups").EnumerateArray().Select(a => a.GetString()!).Order(),
            events.Where(e => e.EventDefinition.Code == "TT-RECORDS")
                .Select(e => e.AgeGroup).Distinct().Order());

        // A definition this template contests at Social ONLY still catalogues the
        // same record categories: the tier decision is not this layer's to make.
        Assert.Equal(
            definitions["TT-RECORDS"].RecordCategoriesJson,
            definitions["TT-SOCIAL"].RecordCategoriesJson);
        // Null means one thing only: the definition declares no school records.
        Assert.Null(definitions["TT-NONE"].RecordCategoriesJson);
    }

    [Theory]
    // Tiers: a typo, an empty list, and the same tier twice (which would schedule
    // the same division twice and collide on the uniqueness index).
    [InlineData("""
        "divisions": { "ageGroups": ["13"], "categories": ["M"], "tiers": ["Recreational"] }
        """, "unknown tier 'Recreational'")]
    [InlineData("""
        "divisions": { "ageGroups": ["13"], "categories": ["M"], "tiers": [] }
        """, "divisions.tiers is empty")]
    [InlineData("""
        "divisions": { "ageGroups": ["13"], "categories": ["M"], "tiers": ["Social", "Social"] }
        """, "lists 'Social' twice")]
    // Ages: none at all, a merged scope no division can carry, the OPEN
    // sentinel (which is a definition's openDivision, never a contested age),
    // one age twice, and no categories.
    [InlineData("""
        "divisions": { "ageGroups": [], "categories": ["M"] }
        """, "defines no age groups")]
    [InlineData("""
        "divisions": { "ageGroups": ["15-16"], "categories": ["M"] }
        """, "may contain only letters and digits")]
    [InlineData("""
        "divisions": { "ageGroups": ["OPEN"], "categories": ["M"] }
        """, "may not name 'OPEN'")]
    [InlineData("""
        "divisions": { "ageGroups": ["16", "16"], "categories": ["M"] }
        """, "names the same age twice")]
    [InlineData("""
        "divisions": { "ageGroups": ["13"], "categories": [] }
        """, "divisions.categories is empty")]
    public void A_malformed_divisions_section_is_refused_by_name(string axes, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(Document(axes)));
        Assert.Contains("divisions-test.json", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_malformed_per_definition_tier_override_names_the_definition()
    {
        // Held to the same standard as the template-level list: an override is
        // the more likely place for a typo, being written once per event.
        var error = Assert.Throws<InvalidDataException>(() => Load("""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": { "ageGroups": ["13"], "categories": ["M"] },
              "definitions": [
                { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Time",
                  "units": "s", "tiers": ["Compettitive"] }
              ]
            }
            """));
        Assert.Contains("definition 'TT-A' tiers", error.Message);
        Assert.Contains("unknown tier 'Compettitive'", error.Message);
        Assert.Contains("Competitive or Social", error.Message);
    }

    private async Task<IReadOnlyList<CarnivalEvent>> ApplyAsync(EventTemplate template) =>
        (await ApplyForCatalogueAsync(template)).Events;

    /// <summary>Both halves of one apply: the catalogue rows and the instances.</summary>
    private async Task<(Dictionary<string, EventDefinition> Definitions, List<CarnivalEvent> Events)>
        ApplyForCatalogueAsync(EventTemplate template)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var carnival = await CarnivalAsync(db);
        await scope.ServiceProvider.GetRequiredService<EventTemplateService>().ApplyAsync(
            carnival, template, new AuditActor("divisions-test", "CarnivalAdministrator"), default);
        return (
            await db.EventDefinitions.ToDictionaryAsync(d => d.Code),
            await db.CarnivalEvents
                .Include(e => e.EventDefinition)
                .Where(e => e.CarnivalId == carnival.Id)
                .ToListAsync());
    }

    private static async Task<Carnival> CarnivalAsync(CarnivalAppDbContext db)
    {
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Divisions carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = CarnivalStatus.Draft,
            RuleVersion = "v1",
            OwnerSubject = "divisions-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        await db.SaveChangesAsync();
        return carnival;
    }
}

/// <summary>
/// ADR-0052 §7: a slot's <c>events</c> entry may narrow a definition to a
/// division set, so a field station's day is cut into the school's own
/// "C 15/16/17" then "S 13/14" sessions. Every filter names the template's OWN
/// ages, tiers and categories and is validated at LOAD — a mistyped age would
/// otherwise print an empty block on carnival day.
/// </summary>
public sealed class TemplateSlotDivisionFilterTests
{
    private static EventTemplate Load(string events) =>
        EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": {
                "ageGroups": ["13", "16"],
                "categories": ["M", "F"],
                "tiers": ["Competitive", "Social"]
              },
              "schedule": { "streams": [{ "name": "Shot Put", "slots": [
                { "start": "10:00", "end": "11:00", "label": "SESSION", "events": {{events}} }] }] },
              "definitions": [
                { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Distance", "units": "m" }
              ]
            }
            """)), "filter-test.json");

    private static EventTemplateSlotPlacement Placement(string events) =>
        Assert.Single(Load(events).Streams.Single().Slots.Single().Placements);

    [Fact]
    public void A_bare_code_holds_every_instance()
    {
        var placement = Placement("""["TT-A"]""");
        Assert.Equal("TT-A", placement.Code);
        Assert.False(placement.Filtered);
        Assert.True(placement.Matches("13", EventTier.Social, CompetitionCategory.F));
    }

    [Fact]
    public void A_filter_narrows_every_axis_it_names()
    {
        var placement = Placement(
            """[{ "code": "TT-A", "ages": ["16"], "tiers": ["Competitive"], "categories": ["M"] }]""");
        Assert.True(placement.Filtered);
        Assert.Equal(["16"], placement.Ages);
        Assert.Equal([EventTier.Competitive], placement.Tiers);
        Assert.Equal([CompetitionCategory.M], placement.Categories);
        Assert.True(placement.Matches("16", EventTier.Competitive, CompetitionCategory.M));
        Assert.False(placement.Matches("13", EventTier.Competitive, CompetitionCategory.M));
        Assert.False(placement.Matches("16", EventTier.Social, CompetitionCategory.M));
        Assert.False(placement.Matches("16", EventTier.Competitive, CompetitionCategory.F));
    }

    [Fact]
    public void An_omitted_axis_is_not_narrowed()
    {
        var placement = Placement("""[{ "code": "TT-A", "ages": ["13"] }]""");
        Assert.Null(placement.Tiers);
        Assert.Null(placement.Categories);
        Assert.True(placement.Matches("13", EventTier.Social, CompetitionCategory.F));
        Assert.False(placement.Matches("16", EventTier.Social, CompetitionCategory.F));
    }

    [Theory]
    [InlineData("""[{ "code": "TT-A", "ages": ["14"] }]""", "divisions.ageGroups does not declare")]
    [InlineData("""[{ "code": "TT-A", "tiers": ["Recreational"] }]""", "the template does not contest")]
    [InlineData("""[{ "code": "TT-A", "categories": ["S"] }]""", "the template does not contest")]
    [InlineData("""[{ "code": "TT-A", "ages": [] }]""", "empty 'ages'")]
    [InlineData("""[{ "code": "TT-A", "ages": "13" }]""", "'ages' that is not an array")]
    [InlineData("""[{ "ages": ["13"] }]""", "event object with no code")]
    public void A_filter_the_template_cannot_honour_is_refused_at_load(string events, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(events));
        Assert.Contains("filter-test.json", error.Message);
        Assert.Contains(expected, error.Message);
    }
}

/// <summary>
/// The athletics event template document (config/event-templates/athletics.json
/// — the REAL shipped file, copied into Fixtures at build) and what applying it
/// produces: ADR-0052 §8's catalogue, and the seven-stream program whose field
/// stations use §7 division filters to keep the school's own running order.
/// </summary>
public sealed class AthleticsTemplateTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;
    private readonly EventTemplate _template = TemplateExpectation.Load("athletics");

    public AthleticsTemplateTests() =>
        _admin = _factory.CreateClientAs("athletics-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> CarnivalAsync()
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name = "Athletics Carnival 2027", type = "Athletics", date = "2027-04-22" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> ApplyAsync(Guid carnivalId) =>
        _admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "athletics" });

    /// <summary>
    /// CANARY — intentional change-detector, exactly as the swimming one is. If
    /// you deliberately edited config/event-templates/athletics.json, update this
    /// test and move on.
    /// </summary>
    [Fact]
    public void CANARY_current_athletics_catalogue_and_totals()
    {
        Assert.Equal(
            ["AT-100", "AT-200", "AT-400", "AT-800", "AT-1500", "AT-4X100-RELAY",
             "AT-LONG-JUMP", "AT-TRIPLE-JUMP", "AT-HIGH-JUMP", "AT-SHOT-PUT", "AT-DISCUS", "AT-JAVELIN"],
            _template.Definitions.Select(d => d.Code));
        Assert.Equal(12, TemplateExpectation.CataloguedDefinitions(_template)); // 11 individual + the relay
        Assert.Equal(322, TemplateExpectation.ScheduledEvents(_template));      // 11 × 28 + 14
        Assert.Equal(28, TemplateExpectation.Divisions(_template, "AT-SHOT-PUT"));    // 7 ages × 2 × C+S
        Assert.Equal(14, TemplateExpectation.Divisions(_template, "AT-4X100-RELAY")); // 7 ages × 2, C only
        Assert.Empty(TemplateExpectation.InactiveCodes(_template));

        // One Track column beside six field stations (ADR-0052 §8).
        Assert.Equal(
            ["Track", "Shot Put", "Long Jump", "Javelin", "High Jump", "Triple Jump", "Discus"],
            _template.Streams.Select(s => s.Name));
    }

    [Fact]
    public void Measures_follow_the_discipline_and_every_event_carries_records()
    {
        Assert.Equal("athletics", _template.Key);
        Assert.All(_template.Definitions, d =>
        {
            Assert.Equal("Athletics", d.Sport);
            Assert.True(d.Active);
            Assert.True(d.SchoolRecords);
        });
        foreach (var track in new[] { "AT-100", "AT-200", "AT-400", "AT-800", "AT-1500", "AT-4X100-RELAY" })
        {
            var d = _template.Definitions.Single(x => x.Code == track);
            Assert.Equal(MeasureType.Time, d.MeasureType);
            Assert.Equal("s", d.Units);
        }
        foreach (var field in new[] { "AT-LONG-JUMP", "AT-TRIPLE-JUMP", "AT-SHOT-PUT", "AT-DISCUS", "AT-JAVELIN" })
        {
            var d = _template.Definitions.Single(x => x.Code == field);
            Assert.Equal(MeasureType.Distance, d.MeasureType);
            Assert.Equal("m", d.Units);
        }
        var high = _template.Definitions.Single(d => d.Code == "AT-HIGH-JUMP");
        Assert.Equal(MeasureType.Height, high.MeasureType);
        Assert.Equal("m", high.Units);
    }

    [Fact]
    public void The_house_relay_is_a_competitive_only_team_event_with_no_open_division()
    {
        // ADR-0052 §8: the program prints one RELAYS block, so there is no Social
        // twin and no all-ages relay to invent (ADR-0021).
        var relay = _template.Definitions.Single(d => d.Code == "AT-4X100-RELAY");
        Assert.Equal(EventFormat.Team, relay.Format);
        Assert.Equal(4, relay.TeamSize);
        Assert.Equal(4, relay.Legs!.Count);
        Assert.Equal(2, relay.ScoringWeight);
        Assert.False(relay.OpenDivision);
        Assert.Equal([EventTier.Competitive], _template.TiersFor(relay));
    }

    [Fact]
    public void The_five_horizontal_field_events_contest_three_attempts_and_nothing_else_does()
    {
        // ADR-0053 §1: high jump stays a single typed best height, and no track
        // event may carry a count at all.
        foreach (var field in new[] { "AT-LONG-JUMP", "AT-TRIPLE-JUMP", "AT-SHOT-PUT", "AT-DISCUS", "AT-JAVELIN" })
            Assert.Equal(3, _template.Definitions.Single(d => d.Code == field).AttemptCount);
        Assert.Null(_template.Definitions.Single(d => d.Code == "AT-HIGH-JUMP").AttemptCount);
        Assert.All(_template.Definitions.Where(d => d.MeasureType == MeasureType.Time),
            d => Assert.Null(d.AttemptCount));
    }

    [Fact]
    public void High_jump_is_contested_on_a_ladder_and_nothing_else_is()
    {
        // ADR-0056 §5: the one Height event carries the ladder suggestions the
        // deck's raise box pre-fills from, and no other definition may.
        var highJump = _template.Definitions.Single(d => d.Code == "AT-HIGH-JUMP");
        Assert.Equal(new LadderDto(1.20m, 0.05m), highJump.Ladder);
        Assert.Null(highJump.AttemptCount);
        Assert.All(_template.Definitions.Where(d => d.Code != "AT-HIGH-JUMP"),
            d => Assert.Null(d.Ladder));
    }

    [Fact]
    public async Task Apply_writes_the_ladder_onto_the_catalogue()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        Assert.Equal("""{"start":1.20,"step":0.05}""", await CarnivalSeed.QueryAsync(_factory,
            db => db.EventDefinitions.Where(d => d.Code == "AT-HIGH-JUMP")
                .Select(d => d.LadderJson).SingleAsync()));
    }

    [Fact]
    public async Task Apply_writes_the_attempt_count_onto_the_catalogue()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var counts = await CarnivalSeed.QueryAsync(_factory, db => db.EventDefinitions
            .Where(d => d.Sport == "Athletics")
            .ToDictionaryAsync(d => d.Code, d => d.AttemptCount));
        Assert.Equal(3, counts["AT-SHOT-PUT"]);
        Assert.Null(counts["AT-HIGH-JUMP"]);
        Assert.Null(counts["AT-100"]);
    }

    [Fact]
    public void Definitions_carry_the_three_faceted_athletics_tags()
    {
        Assert.Equal(["discipline:Sprint", "distance:100m", "surface:Track"],
            _template.Definitions.Single(d => d.Code == "AT-100").Tags);
        Assert.Equal(["discipline:Middle distance", "distance:1500m", "surface:Track"],
            _template.Definitions.Single(d => d.Code == "AT-1500").Tags);
        Assert.Equal(["discipline:Throw", "surface:Field"],
            _template.Definitions.Single(d => d.Code == "AT-SHOT-PUT").Tags);
        Assert.Equal(["discipline:Jump", "surface:Field"],
            _template.Definitions.Single(d => d.Code == "AT-HIGH-JUMP").Tags);
    }

    [Fact]
    public void Divisions_are_single_ages_12_to_18_by_boys_and_girls_at_both_tiers()
    {
        var ages = new[] { "12", "13", "14", "15", "16", "17", "18" };
        Assert.Equal(ages, _template.AgeGroups);
        Assert.Equal([CompetitionCategory.M, CompetitionCategory.F], _template.Categories);
        Assert.Equal([EventTier.Competitive, EventTier.Social], _template.Tiers);
    }

    [Fact]
    public async Task Apply_schedules_the_whole_catalogue_and_seeds_the_seven_stream_program()
    {
        var carnivalId = await CarnivalAsync();
        var body = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TemplateExpectation.CataloguedDefinitions(_template),
            body.GetProperty("definitionsCreated").GetInt32());
        Assert.Equal(TemplateExpectation.ScheduledEvents(_template),
            body.GetProperty("eventsCreated").GetInt32());
        Assert.Equal(_template.Streams.Count, body.GetProperty("streamsCreated").GetInt32());
        Assert.Equal(_template.Streams.Sum(s => s.Slots.Count), body.GetProperty("slotsCreated").GetInt32());

        var streams = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleStreams
            .Where(s => s.CarnivalId == carnivalId).OrderBy(s => s.SortOrder).ToListAsync());
        Assert.Equal(_template.Streams.Select(s => s.Name), streams.Select(s => s.Name));
    }

    [Fact]
    public async Task A_filtered_station_slot_holds_exactly_the_sessions_divisions()
    {
        // The program's own running order: "SHOT PUT 10:00-11:00 C 15/16/17" is
        // one slot, "S 13/14" another, and the SAME definition sits in four slots
        // of the one stream (ADR-0052 §7). Ages 12 and 18 ride with the 13/14 and
        // 15/16/17 sessions respectively.
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var slots = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleSlots
            .Include(s => s.Events).ThenInclude(e => e.EventDefinition)
            .Where(s => s.Stream.CarnivalId == carnivalId && s.Stream.Name == "Shot Put")
            .OrderBy(s => s.SortOrder)
            .ToListAsync());
        Assert.Equal(["C 15/16/17", "S 15/16/17", "C 13/14", "S 13/14"], slots.Select(s => s.Label));

        var senior = slots[0];
        Assert.Equal(
            ["15/M/Competitive", "15/F/Competitive", "16/M/Competitive", "16/F/Competitive",
             "17/M/Competitive", "17/F/Competitive", "18/M/Competitive", "18/F/Competitive"],
            senior.Events.OrderBy(e => e.SlotOrder)
                .Select(e => $"{e.AgeGroup}/{e.Category}/{e.Tier}"));
        Assert.All(senior.Events, e => Assert.Equal("AT-SHOT-PUT", e.EventDefinition.Code));

        // 12 rides with 13/14, and the tier filter keeps the sessions apart.
        Assert.Equal(
            ["12", "12", "13", "13", "14", "14"],
            slots[2].Events.OrderBy(e => e.SlotOrder).Select(e => e.AgeGroup));
        Assert.All(slots[2].Events, e => Assert.Equal(EventTier.Competitive, e.Tier));
        Assert.All(slots[3].Events, e => Assert.Equal(EventTier.Social, e.Tier));

        // An unfiltered block is unchanged: the track slot takes every instance.
        var track = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleSlots
            .Include(s => s.Events)
            .SingleAsync(s => s.Stream.CarnivalId == carnivalId && s.Label == "100m"));
        Assert.Equal(TemplateExpectation.Divisions(_template, "AT-100"), track.Events.Count);
    }

    [Fact]
    public async Task Only_what_the_program_itself_omits_is_left_unscheduled()
    {
        // The 2026 discus column stops at "S 13/14/15/16", so Social 17 and 18
        // discus are the only instances in no slot — an ordinary, usable state.
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var unscheduled = await CarnivalSeed.QueryAsync(_factory, db => db.CarnivalEvents
            .Include(e => e.EventDefinition)
            .Where(e => e.CarnivalId == carnivalId && e.ScheduleSlotId == null)
            .ToListAsync());
        Assert.Equal(
            ["AT-DISCUS/17/F/Social", "AT-DISCUS/17/M/Social",
             "AT-DISCUS/18/F/Social", "AT-DISCUS/18/M/Social"],
            unscheduled
                .Select(e => $"{e.EventDefinition.Code}/{e.AgeGroup}/{e.Category}/{e.Tier}")
                .Order());
    }

    [Fact]
    public async Task Reapply_places_nothing_a_second_time()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);
        var placedFirst = await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId && e.ScheduleSlotId != null));

        var second = await (await ApplyAsync(carnivalId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, second.GetProperty("definitionsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("eventsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("streamsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("slotsCreated").GetInt32());
        Assert.Equal(0, second.GetProperty("eventsPlaced").GetInt32());
        Assert.Equal(placedFirst, await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId && e.ScheduleSlotId != null)));
    }

    [Fact]
    public async Task Novelty_items_float_in_the_track_stream_with_no_events_and_a_configured_category()
    {
        var carnivalId = await CarnivalAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(carnivalId)).StatusCode);

        var items = await CarnivalSeed.QueryAsync(_factory, db => db.ScheduleSlots
            .Include(s => s.Events)
            .Where(s => s.Stream.CarnivalId == carnivalId && s.AdjustmentCategory != null)
            .OrderBy(s => s.SortOrder)
            .ToListAsync());
        Assert.Equal(["Novelty 1", "Novelty 2", "Novelty 3"], items.Select(s => s.AdjustmentCategory));
        Assert.All(items, s =>
        {
            Assert.Equal(ScheduleSlotKind.Item, s.Kind);
            Assert.Empty(s.Events);
            // The program gives them no time: "listen for announcements".
            Assert.Null(s.StartTime);
            Assert.Contains(s.AdjustmentCategory!,
                CarnivalApp.Api.Scoring.CarnivalSettings.DefaultAdjustmentCategories);
        });
    }
}

/// <summary>
/// ADR-0056 §5: a definition's <c>ladder</c> as the template document declares it.
/// The rules are enforced at LOAD for the reason the attempt count's are: a
/// document the loader accepted is one the reconciler will freeze onto the
/// catalogue the moment a card is recorded against it.
/// </summary>
public sealed class TemplateLadderTests
{
    private static EventTemplate Load(string definition) =>
        EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": { "ageGroups": ["13"], "categories": ["M"] },
              "definitions": [ {{definition}} ]
            }
            """)), "ladder-test.json");

    [Fact]
    public void A_height_definition_carries_its_ladder_and_others_carry_none()
    {
        var template = Load("""
            { "code": "TT-HJ", "name": "High Jump", "sport": "Test", "measureType": "Height",
              "units": "m", "ladder": { "start": 1.20, "step": 0.05 } },
            { "code": "TT-B", "name": "B", "sport": "Test", "measureType": "Time", "units": "s" }
            """);
        Assert.Equal(new LadderDto(1.20m, 0.05m),
            template.Definitions.Single(d => d.Code == "TT-HJ").Ladder);
        Assert.Null(template.Definitions.Single(d => d.Code == "TT-B").Ladder);
    }

    [Theory]
    [InlineData("Distance", "", "1.20", "0.05", "belongs to a Height event")]
    [InlineData("Height", "", "1.234", "0.05", "whole centimetres")]
    [InlineData("Height", "", "1.20", "0", "whole centimetres")]
    [InlineData("Height", "", "1.20", "1.50", "whole centimetres")]
    public void A_ladder_the_definition_cannot_hold_is_refused_at_load(
        string measureType, string extra, string start, string step, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load($$"""
            { "code": "TT-A", "name": "A", "sport": "T", "measureType": "{{measureType}}",
              "units": "m", {{extra}}"ladder": { "start": {{start}}, "step": {{step}} } }
            """));
        Assert.Contains("ladder-test.json", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_team_definition_cannot_set_a_ladder()
    {
        var error = Assert.Throws<InvalidDataException>(() => Load("""
            { "code": "TT-R", "name": "Relay", "sport": "T", "measureType": "Height",
              "units": "m", "format": "Team", "teamSize": 2, "legs": ["One", "Two"],
              "ladder": { "start": 1.20, "step": 0.05 } }
            """));
        Assert.Contains("team event 'TT-R' cannot set a ladder", error.Message);
    }
}

/// <summary>
/// ADR-0053 §1: a definition's <c>attempts</c> as the template document declares
/// it. The bounds are enforced at LOAD because a series recorded against three
/// attempts cannot be re-read against two — a document the loader accepted is
/// one the reconciler will freeze onto the catalogue.
/// </summary>
public sealed class TemplateAttemptsTests
{
    private static EventTemplate Load(string definition) =>
        EventTemplate.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
              "template": "test",
              "name": "Test sport",
              "divisions": { "ageGroups": ["13"], "categories": ["M"] },
              "definitions": [ {{definition}} ]
            }
            """)), "attempts-test.json");

    [Fact]
    public void A_field_definition_carries_its_attempt_count_and_others_carry_none()
    {
        var template = Load("""
            { "code": "TT-A", "name": "A", "sport": "Test", "measureType": "Distance",
              "units": "m", "attempts": 2 },
            { "code": "TT-B", "name": "B", "sport": "Test", "measureType": "Time", "units": "s" }
            """);
        Assert.Equal(2, template.Definitions.Single(d => d.Code == "TT-A").AttemptCount);
        Assert.Null(template.Definitions.Single(d => d.Code == "TT-B").AttemptCount);
    }

    [Theory]
    [InlineData("Distance", "m", 0, "attempts between 1 and 6")]
    [InlineData("Distance", "m", 7, "attempts between 1 and 6")]
    [InlineData("Time", "s", 3, "cannot set attempts")]
    // A Height event is contested on the bar (ADR-0056, amended 2026-09-17).
    [InlineData("Height", "m", 3, "cannot set attempts")]
    public void An_attempt_count_the_measure_cannot_hold_is_refused_at_load(
        string measureType, string units, int attempts, string expected)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(
            $$"""
              { "code": "TT-A", "name": "A", "sport": "T", "measureType": "{{measureType}}",
                "units": "{{units}}", "attempts": {{attempts}} }
              """));
        Assert.Contains("attempts-test.json", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_team_definition_cannot_set_attempts()
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(
            """
            { "code": "TT-R", "name": "Relay", "sport": "T", "measureType": "Distance",
              "units": "m", "format": "Team", "teamSize": 2, "legs": ["One", "Two"],
              "attempts": 3 }
            """));
        Assert.Contains("team event 'TT-R' cannot set attempts", error.Message);
    }
}
