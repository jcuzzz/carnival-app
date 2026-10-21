using System.Net;
using System.Text.Json;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0012 (K6): the OpenAPI document is the source the PWA generates its wire
/// types from, so it is API contract — an endpoint that answers with an
/// anonymous object contributes an empty schema and silently degenerates the
/// generated type to <c>unknown</c>. These tests fail on exactly that: the
/// document must exist and every projection the app reads must carry named
/// properties.
/// </summary>
public sealed class OpenApiDocumentTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("openapi-admin", "CarnivalAdministrator");

    private async Task<JsonElement> DocumentAsync()
    {
        var response = await Admin.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Document_is_served_and_describes_the_v1_surface()
    {
        var document = await DocumentAsync();
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/carnivals/{id}/events/{eventId}/results", out _));
        Assert.True(paths.TryGetProperty("/api/v1/carnivals/{id}/standings", out _));
        Assert.True(paths.TryGetProperty("/api/v1/school/settings", out _));
        // ADR-0054 §4: Manage's "Open all divisions" is generated against this route.
        Assert.True(paths.TryGetProperty("/api/v1/carnivals/{id}/events/open-scheduled", out _));
    }

    [Fact]
    public async Task The_include_inactive_flag_is_documented_as_a_boolean()
    {
        var parameter = (await DocumentAsync()).GetProperty("paths").GetProperty("/api/v1/event-definitions")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "includeInactive");
        Assert.Equal("boolean", parameter.GetProperty("schema").GetProperty("type").GetString());
    }

    [Theory]
    // The read projections the app is generated against — one property name per
    // schema that a hand-mirrored type would have had to guess.
    [InlineData("CarnivalDto", "status")]
    [InlineData("CarnivalEventDto", "definitionCode")]
    // ADR-0014 §4 classification, ADR-0058 §2's one age: the app renders the
    // division chips and the C/S badge from these, so an empty schema would type
    // them as unknown.
    [InlineData("CarnivalEventDto", "ageGroup")]
    [InlineData("CarnivalEventDto", "tier")]
    [InlineData("CarnivalEventDto", "effectiveScoringMode")]
    [InlineData("CarnivalEventDto", "scoresPoints")]
    // ADR-0014 §1 schedule: the app caches this whole payload for offline
    // navigation and the printed program, so an empty schema would type the
    // entire program as unknown.
    [InlineData("ScheduleDto", "streams")]
    [InlineData("ScheduleStreamDto", "slots")]
    [InlineData("ScheduleSlotDto", "eventIds")]
    [InlineData("ScheduleSlotDto", "startTime")]
    [InlineData("CarnivalEventDto", "scheduleSlotId")]
    [InlineData("ScheduleEventRequest", "ageGroup")]
    [InlineData("UpdateEventRequest", "ageGroup")]
    [InlineData("UpdateEventRequest", "tier")]
    [InlineData("GenerateDivisionRequest", "ageGroup")]
    [InlineData("DivisionProfileDto", "ageGroups")]
    [InlineData("ScoringRulesDto", "socialPerEntryPoints")]
    [InlineData("EntryDto", "overrideApplied")]
    // ADR-0038: the create response carries the placement the server chose, and
    // a lane names nothing without its race.
    [InlineData("EntryDto", "raceId")]
    // ADR-0039 §2: the create names its heat, so the app's outbox payload is
    // generated with the field on it — individual and team alike.
    [InlineData("CreateEntryRequest", "raceId")]
    [InlineData("TeamEntryRequest", "raceId")]
    // ADR-0038 amendment: the race says which division it was minted for, so an
    // empty one is still attributable.
    [InlineData("RaceDto", "eventId")]
    [InlineData("RelayLegDto", "legOrder")]
    [InlineData("EligibleStudentPage", "truncated")]
    [InlineData("ResultDto", "currentRevision")]
    // ADR-0036: the deck badges the heat's placings straight off this field.
    [InlineData("ResultDto", "place")]
    [InlineData("Standings", "resultsCounted")]
    // ADR-0043 §2: the Dashboard's progress card, completion spine, participation
    // row and records strip are generated straight from these.
    [InlineData("CarnivalProgress", "totals")]
    [InlineData("ProgressTotals", "eventsComplete")]
    [InlineData("EventProgress", "status")]
    [InlineData("EventProgress", "slotId")]
    [InlineData("EventProgress", "divisionLabel")]
    [InlineData("HouseParticipation", "competitors")]
    [InlineData("RecordToday", "kind")]
    [InlineData("RecordToday", "standing")]
    [InlineData("CarnivalSettingsDto", "adjustmentCategories")]
    // ADR-0038 §1: the Manage editor renders the checkbox from this.
    [InlineData("CarnivalSettingsDto", "autoFillLanes")]
    // ADR-0057: the age pickers and the roster-default line are generated from these.
    [InlineData("CarnivalSettingsDto", "rosterAgeGroups")]
    [InlineData("CarnivalSettingsDto", "divisionsDefault")]
    // ADR-0060: the checklist's year warning, the upload form's year and the review's cut-off.
    [InlineData("CarnivalSettingsDto", "rosterYear")]
    [InlineData("ImportDefaults", "competitionYear")]
    [InlineData("ImportDefaults", "earliestYear")]
    [InlineData("ImportDefaults", "latestYear")]
    [InlineData("ImportDefaults", "cutOff")]
    [InlineData("ReconciliationReport", "cutOff")]
    // ADR-0022: the division profile and the two new endpoints' payloads — the
    // grid and the generate button are generated straight from these.
    [InlineData("GenerateDivisionsRequest", "divisions")]
    [InlineData("GenerateDivisionsResponse", "skipped")]
    // ADR-0054 §4: the count for the toast, the ids for the audit trail.
    [InlineData("OpenScheduledResponse", "opened")]
    [InlineData("OpenScheduledResponse", "eventIds")]
    [InlineData("EventDefinitionUsageDto", "divisions")]
    // The event editor greys the frozen type off this count.
    [InlineData("EventDefinitionUsageDto", "results")]
    // ADR-0004 tier 1: the reports screen renders the results book straight from
    // these, and an empty schema would type the whole book as unknown.
    [InlineData("ResultsReport", "rows")]
    [InlineData("ResultRow", "division")]
    [InlineData("ResultRow", "place")]
    [InlineData("ResultRow", "heat")]
    // ADR-0028 §5: the book's badge compares this number, never the printed value.
    [InlineData("ResultRow", "measuredValue")]
    // ADR-0028 §5: the deck badge and the record book are generated from these,
    // and `higherBetter` above all — the client must never re-derive measure
    // semantics to decide whether a swim beat a record.
    [InlineData("SchoolRecordDto", "eventDefinitionId")]
    [InlineData("SchoolRecordDto", "ageGroup")]
    [InlineData("SchoolRecordDto", "higherBetter")]
    [InlineData("SchoolRecordDto", "formattedValue")]
    [InlineData("SchoolRecordDto", "setOn")]
    // ADR-0035: the id is what the void endpoint is addressed by, and the void
    // fields are how the custody screen shows a withdrawn record.
    [InlineData("SchoolRecordDto", "id")]
    [InlineData("SchoolRecordDto", "voided")]
    [InlineData("SchoolRecordDto", "voidReason")]
    [InlineData("AdjustmentDto", "voidReason")]
    [InlineData("EventDefinitionDto", "measureType")]
    // ADR-0052 §2: the deck reads the measure off the EVENT, and keys the capture
    // mask, the value formatter, the status chips and "lane pending" on it.
    [InlineData("CarnivalEventDto", "measureType")]
    // ADR-0053 §1: the deck renders the attempt cell strip off the EVENT, and
    // §2's series is what the row's best mark and NM are derived from.
    [InlineData("EventDefinitionDto", "attemptCount")]
    [InlineData("CarnivalEventDto", "attemptCount")]
    [InlineData("ResultDto", "attempts")]
    // ADR-0056 §3: the deck draws the card grid off the EVENT's measure and the
    // heights it has contested, reads the card off the result, and pre-fills the
    // raise box from the definition's ladder suggestions.
    [InlineData("EventDefinitionDto", "ladder")]
    [InlineData("CarnivalEventDto", "ladder")]
    [InlineData("CarnivalEventDto", "heights")]
    [InlineData("ResultDto", "jumps")]
    // ADR-0056 amendment 2026-09-25: "attempt n of N" and the header switch.
    [InlineData("EventDefinitionDto", "attemptsPerHeight")]
    [InlineData("CarnivalEventDto", "attemptsPerHeight")]
    [InlineData("ImportBatchSummary", "importedAtUtc")]
    // The roster import screen renders the row issues from the typed document
    // rather than a hand-written shape over an open blob.
    [InlineData("ValidationReport", "validation")]
    [InlineData("ValidationDocument", "rowIssues")]
    [InlineData("RowValidation", "issues")]
    [InlineData("RowIssue", "code")]
    // ADR-0038 amendment: the New carnival form sends both, pre-ticked.
    [InlineData("CarnivalRequest", "laneCount")]
    [InlineData("CarnivalRequest", "autoFillLanes")]
    // ADR-0040 §3: the client sets --brand from this before sign-in, so the
    // colour must be a named (and nullable) property, never an empty schema.
    [InlineData("BrandingResponse", "schoolName")]
    [InlineData("BrandingResponse", "brandColour")]
    // ADR-0045 §2: the School settings screen is generated against both halves
    // of this form, the concurrency stamp included.
    [InlineData("SchoolSettingsResponse", "name")]
    [InlineData("SchoolSettingsResponse", "brandColour")]
    [InlineData("SchoolSettingsResponse", "houses")]
    [InlineData("SchoolSettingsResponse", "updatedAtUtc")]
    [InlineData("SchoolSettingsRequest", "name")]
    [InlineData("SchoolSettingsRequest", "brandColour")]
    [InlineData("SchoolSettingsRequest", "houses")]
    [InlineData("SchoolSettingsRequest", "updatedAtUtc")]
    // ADR-0045 §6: Remove is disabled from `inUse` alone, so it is part of the
    // contract, not a hint the client may recompute.
    [InlineData("HouseSettingResponse", "code")]
    [InlineData("HouseSettingResponse", "name")]
    [InlineData("HouseSettingResponse", "colour")]
    [InlineData("HouseSettingResponse", "inUse")]
    [InlineData("HouseSettingRequest", "code")]
    [InlineData("HouseSettingRequest", "name")]
    [InlineData("HouseSettingRequest", "colour")]
    [InlineData("HouseMark", "colour")]
    [InlineData("ApiErrorBody", "detail")]
    public async Task Schema_carries_its_properties(string schema, string property)
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty(schema, out var shape), $"{schema} is missing from the document.");
        Assert.True(shape.GetProperty("properties").TryGetProperty(property, out _),
            $"{schema}.{property} is missing — is the endpoint still returning an anonymous object?");
    }

    [Theory]
    [InlineData("EntryDto", "snapshotCategory")]
    [InlineData("EntryDto", "categoryOverride")]
    [InlineData("EntryDto", "effectiveCategory")]
    [InlineData("EligibleStudentDto", "category")]
    // ADR-0004 tier 1: the results book names a DIVISION's category and nothing
    // else — `division` is the only category-shaped field it may carry.
    [InlineData("ResultRow", "category")]
    [InlineData("ResultRow", "snapshotCategory")]
    // ADR-0028 §5: the record book's `category` is the DIVISION's, exactly as
    // CarnivalEventDto's is. The holder is a name, and carries none of their own.
    [InlineData("SchoolRecordDto", "snapshotCategory")]
    [InlineData("SchoolRecordDto", "effectiveCategory")]
    // ADR-0043 §2: the only name on the progress aggregate is a record HOLDER's
    // (public by nature, ADR-0028); no row of it carries a student's category.
    [InlineData("RecordToday", "snapshotCategory")]
    [InlineData("EventProgress", "category")]
    // ADR-0045: school settings are about houses, never about a student.
    [InlineData("SchoolSettingsResponse", "category")]
    [InlineData("SchoolSettingsResponse", "snapshotCategory")]
    [InlineData("SchoolSettingsRequest", "category")]
    [InlineData("HouseMark", "category")]
    [InlineData("HouseSettingResponse", "category")]
    public async Task No_read_schema_carries_a_students_category(string schema, string property)
    {
        // ADR-0026 §2: the category is server-side only, and both of these
        // schemas are read by anything holding ViewCarnivals — Viewer included.
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty(schema, out var shape), $"{schema} is missing from the document.");
        Assert.False(shape.GetProperty("properties").TryGetProperty(property, out _),
            $"{schema}.{property} puts a student's category back on the wire.");
    }

    /// <summary>
    /// Enums travel as strings (Program.cs) and the document must say so with the
    /// exact spellings, because the generated client compares against them.
    /// </summary>
    [Fact]
    public async Task Enums_are_documented_as_their_wire_strings()
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");
        var values = schemas.GetProperty("ResultStatus").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Equal(["Ok", "Dns", "Dnf", "Dq", "Nm"], values);

        // ADR-0014 §4 — the client branches on these spellings to badge an
        // event C or S and to explain how it scores.
        Assert.Equal(["Competitive", "Social"], schemas.GetProperty("EventTier")
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(["Places", "Social"], schemas.GetProperty("EventScoringMode")
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
    }

    /// <summary>
    /// ADR-0057: the roster codes are a fact the client renders unconditionally,
    /// so the document must say they are always there and never null; the default
    /// profile is the one that is sometimes absent, exactly as `divisions` is.
    /// </summary>
    [Fact]
    public async Task Roster_age_groups_are_required_and_the_default_profile_is_nullable()
    {
        var settings = (await DocumentAsync()).GetProperty("components").GetProperty("schemas")
            .GetProperty("CarnivalSettingsDto");
        Assert.Contains("rosterAgeGroups",
            settings.GetProperty("required").EnumerateArray().Select(v => v.GetString()));

        var roster = settings.GetProperty("properties").GetProperty("rosterAgeGroups");
        Assert.Equal("array", roster.GetProperty("type").GetString());
        Assert.Equal("string", roster.GetProperty("items").GetProperty("type").GetString());

        // Same shape the already-nullable `divisions` carries: a $ref beside null.
        var defaults = settings.GetProperty("properties").GetProperty("divisionsDefault");
        Assert.Equal(
            settings.GetProperty("properties").GetProperty("divisions").GetRawText(),
            defaults.GetRawText());
    }

    /// <summary>
    /// A number must be documented as a number: ASP.NET's input tolerance
    /// (NumberHandling.AllowReadingFromString) otherwise types every numeric
    /// field as number-or-string, and the generated client would have to widen
    /// arithmetic — the concurrency token especially (ADR-0002).
    /// </summary>
    [Fact]
    public async Task Numbers_are_not_documented_as_number_or_string()
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");
        var revision = schemas.GetProperty("ResultDto").GetProperty("properties")
            .GetProperty("currentRevision");
        Assert.Equal("integer", revision.GetProperty("type").GetString());
    }

    /// <summary>
    /// ADR-0011's one error body is declared on the /api/v1 group, so every
    /// operation offers it rather than a bespoke (or missing) error schema.
    /// </summary>
    [Fact]
    public async Task Every_operation_declares_the_shared_error_body()
    {
        var document = await DocumentAsync();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var responses = operation.Value.GetProperty("responses");
                Assert.True(responses.TryGetProperty("409", out var conflict),
                    $"{operation.Name.ToUpperInvariant()} {path.Name} does not declare the ADR-0011 error body.");
                Assert.Contains("ApiErrorBody", conflict.GetRawText());
            }
        }
    }
}
