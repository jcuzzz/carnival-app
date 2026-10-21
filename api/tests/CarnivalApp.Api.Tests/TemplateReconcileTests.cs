using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0050: previewing and applying a changed template onto the shared
/// catalogue. Every test writes its own template document to a temp store, so
/// the diff categories and the guards are exercised without editing the shipped
/// swimming document (which other suites pin byte-for-byte).
/// </summary>
public sealed class TemplateReconcileTests : IDisposable
{
    private const string Key = "recon";

    private readonly string _store;
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _admin;

    public TemplateReconcileTests()
    {
        _store = Directory.CreateTempSubdirectory("sports-reconcile-").FullName;
        _factory = new ApiTestFactory().With("EventTemplates:Path", _store);
        _admin = _factory.CreateClientAs("sys-recon", "SystemAdministrator");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_store, recursive: true); }
        catch (IOException) { /* the OS keeps the temp dir; nothing depends on it */ }
    }

    // ---- template documents ------------------------------------------------

    private static readonly JsonSerializerOptions TemplateJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static object Definition(
        string code, string name = "Freestyle 50m", string sport = "Swimming",
        string measureType = "Time", string units = "s", bool active = true,
        bool schoolRecords = false, string? format = null, int? teamSize = null,
        string[]? legs = null, int scoringWeight = 1, string[]? tags = null,
        int? attempts = null, object? ladder = null) => new
        {
            code,
            name,
            sport,
            measureType,
            units,
            attempts,
            ladder,
            active,
            schoolRecords,
            format,
            teamSize,
            legs,
            scoringWeight,
            tags,
        };

    /// <summary>Writes <c>{key}.json</c> into the temp store and loads it back.</summary>
    private EventTemplate WriteTemplate(params object[] definitions) => WriteTemplate(Key, definitions);

    private EventTemplate WriteTemplate(string key, params object[] definitions)
    {
        var document = new
        {
            template = key,
            name = "Reconcile test carnival",
            divisions = new
            {
                ageGroups = new[] { "12", "13" },
                categories = new[] { "M", "F" },
                tiers = new[] { "Competitive" },
            },
            definitions,
        };
        var path = Path.Combine(_store, $"{key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, TemplateJson));
        return EventTemplate.LoadFromFile(path);
    }

    // ---- catalogue seeding -------------------------------------------------

    private async Task<Guid> SeedDefinitionAsync(string code, Action<EventDefinition>? adjust = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = "Freestyle 50m",
            Sport = "Swimming",
            MeasureType = MeasureType.Time,
            Units = "s",
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        adjust?.Invoke(definition);
        db.EventDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition.Id;
    }

    /// <summary>A carnival event on the definition, plus optionally an entry and a result.</summary>
    private async Task<Guid> SeedEventAsync(
        Guid definitionId, CarnivalStatus status = CarnivalStatus.Open,
        bool withResult = false, bool teamEntry = false, bool individualEntry = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = "Reconcile carnival",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(2027, 2, 25),
            Status = status,
            RuleVersion = "v1",
            OwnerSubject = "seeder",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        db.Carnivals.Add(carnival);
        var carnivalEvent = new CarnivalEvent
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnival.Id,
            EventDefinitionId = definitionId,
            Category = CompetitionCategory.M,
            Status = CarnivalEventStatus.Open,
        };
        carnivalEvent.AgeGroup = "12";
        db.CarnivalEvents.Add(carnivalEvent);

        if (withResult || teamEntry || individualEntry)
        {
            Guid? studentId = null;
            if (!teamEntry)
            {
                var student = new Student
                {
                    Id = Guid.CreateVersion7(),
                    ExternalStudentId = $"REC{Guid.NewGuid():N}"[..12],
                    Surname = "Brook",
                    FirstName = "Aiden",
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };
                db.Students.Add(student);
                studentId = student.Id;
            }
            var entry = new Entry
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = carnivalEvent.Id,
                StudentId = studentId,
                HouseCode = teamEntry ? "BANKSIA" : null,
                SnapshotDisplayName = teamEntry ? "BANKSIA relay team" : "Aiden Brook",
                SnapshotYearLevel = 7,
                SnapshotHouseCode = "BANKSIA",
                SnapshotAgeGroup = "12",
                SnapshotCategory = CompetitionCategory.M,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "seeder",
            };
            db.Entries.Add(entry);
            if (withResult)
                db.Results.Add(new Result
                {
                    Id = Guid.CreateVersion7(),
                    EntryId = entry.Id,
                    MeasuredValue = 31.20m,
                    Status = ResultStatus.Ok,
                    RecordedBySubject = "seeder",
                    RecordedAtUtc = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
        }

        await db.SaveChangesAsync();
        return carnivalEvent.Id;
    }

    private async Task SeedSchoolRecordAsync(Guid definitionId)
    {
        await SeedEventAsync(definitionId, withResult: true);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
        var result = await db.Results.OrderByDescending(r => r.Id).FirstAsync();
        db.SchoolRecords.Add(new SchoolRecord
        {
            Id = Guid.CreateVersion7(),
            EventDefinitionId = definitionId,
            AgeGroup = "12",
            Category = CompetitionCategory.M,
            ResultId = result.Id,
            RecordValue = 31.20m,
            HolderDisplayName = "Aiden Brook",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // ---- request helpers ---------------------------------------------------

    private async Task<JsonElement> PreviewAsync(string key = Key)
    {
        var response = await _admin.GetAsync($"/api/v1/admin/event-templates/{key}/reconcile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<HttpResponseMessage> ApplyAsync(
        string? diffHash, string? reason = "Template updated for the 2027 carnival.",
        string[]? excludeCodes = null, string key = Key) =>
        _admin.PostAsJsonAsync($"/api/v1/admin/event-templates/{key}/reconcile",
            new { diffHash, reason, excludeCodes });

    private static JsonElement Row(JsonElement preview, string code) =>
        preview.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

    private static JsonElement Field(JsonElement row, string field) =>
        row.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("field").GetString() == field);

    private static string Hash(JsonElement preview) => preview.GetProperty("diffHash").GetString()!;

    private Task<EventDefinition?> DefinitionAsync(string code) =>
        CarnivalSeed.QueryAsync(_factory, db =>
            db.EventDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Code == code));

    // ---- the diff ----------------------------------------------------------

    [Fact]
    public async Task The_preview_names_every_diff_category()
    {
        await SeedDefinitionAsync("REC-SAME");
        await SeedDefinitionAsync("REC-MOVED", d => { d.Name = "50m Freestyle"; d.TagsJson = null; });
        await SeedDefinitionAsync("REC-ORPHAN");
        WriteTemplate(
            Definition("REC-SAME"),
            Definition("REC-MOVED", name: "Freestyle 50m", tags: ["discipline:Freestyle"]),
            Definition("REC-NEW", name: "Butterfly 50m"));

        var preview = await PreviewAsync();

        Assert.Equal("Unchanged", Row(preview, "REC-SAME").GetProperty("kind").GetString());
        Assert.Equal("Missing", Row(preview, "REC-NEW").GetProperty("kind").GetString());
        Assert.Equal("CatalogueOnly", Row(preview, "REC-ORPHAN").GetProperty("kind").GetString());

        var moved = Row(preview, "REC-MOVED");
        Assert.Equal("Update", moved.GetProperty("kind").GetString());
        Assert.Equal("50m Freestyle", Field(moved, "name").GetProperty("prior").GetString());
        Assert.Equal("Freestyle 50m", Field(moved, "name").GetProperty("new").GetString());
        Assert.Equal("[\"discipline:Freestyle\"]", Field(moved, "tagsJson").GetProperty("new").GetString());
        Assert.False(moved.GetProperty("guarded").GetBoolean());

        var counts = preview.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, counts.GetProperty("update").GetInt32());
        Assert.Equal(1, counts.GetProperty("missing").GetInt32());
        Assert.Equal(1, counts.GetProperty("catalogueOnly").GetInt32());
        Assert.Equal(0, counts.GetProperty("guarded").GetInt32());
        Assert.Equal("recon.json", preview.GetProperty("templateVersion").GetString());
    }

    [Fact]
    public async Task An_update_row_carries_its_stamp_and_whether_the_app_edited_it()
    {
        var edited = await SeedDefinitionAsync("REC-EDITED",
            d => { d.Name = "Old name"; d.UpdatedAtUtc = new DateTimeOffset(2026, 9, 4, 1, 2, 3, TimeSpan.Zero); });
        await SeedDefinitionAsync("REC-UNTOUCHED", d => d.Name = "Old name");
        // The in-app editor's own audit row is the only knowable signal (§8).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            new AuditService(db, Fixtures.Config()).Record(
                new AuditActor("admin-1", "SystemAdministrator"), AuditActions.EventDefinitionUpdate,
                nameof(EventDefinition), edited.ToString());
            await db.SaveChangesAsync();
        }
        WriteTemplate(Definition("REC-EDITED"), Definition("REC-UNTOUCHED"));

        var preview = await PreviewAsync();

        Assert.True(Row(preview, "REC-EDITED").GetProperty("editedInApp").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 1, 2, 3, TimeSpan.Zero),
            Row(preview, "REC-EDITED").GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.False(Row(preview, "REC-UNTOUCHED").GetProperty("editedInApp").GetBoolean());
    }

    [Fact]
    public async Task The_template_store_lists_each_document_with_its_row_count()
    {
        WriteTemplate("alpha", Definition("A-1"), Definition("A-2"));
        WriteTemplate("beta", Definition("B-1"));
        File.WriteAllText(Path.Combine(_store, "broken.json"), "{ not json");

        var response = await _admin.GetAsync("/api/v1/admin/event-templates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .EnumerateArray().ToList();

        var alpha = listed.Single(t => t.GetProperty("id").GetString() == "alpha");
        Assert.Equal("Reconcile test carnival", alpha.GetProperty("name").GetString());
        Assert.Equal("alpha.json", alpha.GetProperty("version").GetString());
        Assert.Equal(2, alpha.GetProperty("definitions").GetInt32());
        Assert.True(alpha.GetProperty("readable").GetBoolean());
        // An unreadable document is listed, not silently dropped.
        Assert.False(listed.Single(t => t.GetProperty("id").GetString() == "broken")
            .GetProperty("readable").GetBoolean());
    }

    // ---- applying ----------------------------------------------------------

    [Fact]
    public async Task Apply_writes_the_previewed_diff_and_leaves_everything_else_alone()
    {
        var template = WriteTemplate(
            Definition("REC-MOVED", name: "Freestyle 50m", schoolRecords: true,
                tags: ["discipline:Freestyle"], scoringWeight: 2),
            Definition("REC-NEW", name: "Medley Relay 4x50m", format: "Team", teamSize: 4,
                legs: ["Backstroke", "Breaststroke", "Butterfly", "Freestyle"], scoringWeight: 2));
        await SeedDefinitionAsync("REC-MOVED", d =>
        {
            d.Name = "50m Freestyle";
            d.EligibilityJson = "{\"minYear\":7}";
        });
        var orphan = await SeedDefinitionAsync("REC-ORPHAN");
        var priorCreated = (await DefinitionAsync("REC-MOVED"))!.CreatedAtUtc;

        var response = await ApplyAsync(Hash(await PreviewAsync()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, outcome.GetProperty("updated").GetInt32());
        Assert.Equal(1, outcome.GetProperty("created").GetInt32());
        Assert.Equal(1, outcome.GetProperty("catalogueOnly").GetInt32());

        var moved = (await DefinitionAsync("REC-MOVED"))!;
        Assert.Equal("Freestyle 50m", moved.Name);
        Assert.Equal("[\"discipline:Freestyle\"]", moved.TagsJson);
        Assert.Equal(2, moved.ScoringWeight);
        Assert.Equal(EventTemplateService.RecordCategoriesJsonFor(template), moved.RecordCategoriesJson);
        // Never touched (§4): the match key, the creation stamp, and the blob no
        // template can express.
        Assert.Equal("REC-MOVED", moved.Code);
        Assert.Equal(priorCreated, moved.CreatedAtUtc);
        Assert.Equal("{\"minYear\":7}", moved.EligibilityJson);
        Assert.NotNull(moved.UpdatedAtUtc);

        var created = (await DefinitionAsync("REC-NEW"))!;
        Assert.Equal(EventFormat.Team, created.Format);
        Assert.Equal(4, created.TeamSize);
        Assert.Equal("[\"Backstroke\",\"Breaststroke\",\"Butterfly\",\"Freestyle\"]", created.LegsJson);

        // Reconcile never deletes and never deactivates a catalogue-only row.
        var untouched = await CarnivalSeed.QueryAsync(_factory, db =>
            db.EventDefinitions.AsNoTracking().SingleAsync(d => d.Id == orphan));
        Assert.True(untouched.IsActive);
        Assert.Null(untouched.UpdatedAtUtc);
    }

    [Fact]
    public async Task An_activation_flip_is_never_guarded()
    {
        // The relay incident (§5): a placeholder definition flipped active while
        // the carnival that schedules it already holds results.
        var id = await SeedDefinitionAsync("REC-RELAY", d => d.IsActive = false);
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-RELAY", active: true));

        var preview = await PreviewAsync();
        Assert.Null(Field(Row(preview, "REC-RELAY"), "isActive").GetProperty("guard").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.True((await DefinitionAsync("REC-RELAY"))!.IsActive);
    }

    [Fact]
    public async Task A_second_apply_of_the_same_diff_is_all_unchanged()
    {
        await SeedDefinitionAsync("REC-IDEM", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-IDEM"), Definition("REC-FRESH"));

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(await PreviewAsync()))).StatusCode);

        var second = await PreviewAsync();
        Assert.All(second.GetProperty("rows").EnumerateArray(),
            row => Assert.Equal("Unchanged", row.GetProperty("kind").GetString()));
        var response = await ApplyAsync(Hash(second));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, outcome.GetProperty("updated").GetInt32());
        Assert.Equal(0, outcome.GetProperty("created").GetInt32());
        Assert.Equal(2, outcome.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task A_preview_taken_before_another_change_cannot_be_applied()
    {
        await SeedDefinitionAsync("REC-STALE", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-STALE"));
        var stale = Hash(await PreviewAsync());

        // Another administrator edits the row between preview and apply.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            var row = await db.EventDefinitions.SingleAsync(d => d.Code == "REC-STALE");
            row.Name = "Something else entirely";
            await db.SaveChangesAsync();
        }

        var response = await ApplyAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TemplateReconcileStale);
        Assert.Equal("Something else entirely", (await DefinitionAsync("REC-STALE"))!.Name);
    }

    [Fact]
    public async Task A_preview_taken_before_the_template_file_moved_cannot_be_applied()
    {
        await SeedDefinitionAsync("REC-DEPLOY", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-DEPLOY"));
        var stale = Hash(await PreviewAsync());

        WriteTemplate(Definition("REC-DEPLOY", name: "A different new name"));

        var response = await ApplyAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TemplateReconcileStale);
    }

    [Fact]
    public async Task A_reconcile_needs_a_reason()
    {
        await SeedDefinitionAsync("REC-REASON", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-REASON"));
        var hash = Hash(await PreviewAsync());

        var blank = await ApplyAsync(hash, reason: "   ");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        await blank.AssertErrorAsync(ErrorCodes.ReasonRequired);

        var tooLong = await ApplyAsync(hash, reason: new string('x', 501));
        await tooLong.AssertErrorAsync(ErrorCodes.ReasonRequired);
        Assert.Equal("Old name", (await DefinitionAsync("REC-REASON"))!.Name);
    }

    [Fact]
    public async Task An_unknown_template_key_is_404_and_an_unreadable_one_is_422()
    {
        var unknown = await _admin.GetAsync("/api/v1/admin/event-templates/nosuch/reconcile");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await unknown.AssertErrorAsync(ErrorCodes.TemplateNotFound);

        // A document repeating a code cannot be diffed by code.
        File.WriteAllText(Path.Combine(_store, "dupes.json"), JsonSerializer.Serialize(new
        {
            template = "dupes",
            divisions = new { ageGroups = new[] { "12" }, categories = new[] { "M" } },
            definitions = new[] { Definition("REC-DUPE"), Definition("REC-DUPE") },
        }, TemplateJson));

        var invalid = await _admin.GetAsync("/api/v1/admin/event-templates/dupes/reconcile");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        await invalid.AssertErrorAsync(ErrorCodes.TemplateInvalid);
    }

    // ---- guards (§5) -------------------------------------------------------

    private async Task AssertGuardedAsync(string code, string field, string expectedNew)
    {
        var preview = await PreviewAsync();
        var row = Row(preview, code);
        Assert.True(row.GetProperty("guarded").GetBoolean());
        Assert.Equal(expectedNew, Field(row, field).GetProperty("new").GetString());
        Assert.False(string.IsNullOrWhiteSpace(Field(row, field).GetProperty("guard").GetString()));
        Assert.Equal(1, preview.GetProperty("counts").GetProperty("guarded").GetInt32());

        var response = await ApplyAsync(Hash(preview));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(ErrorCodes.TemplateReconcileUnsafe, body.GetProperty("error").GetString());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Units_are_frozen_once_a_result_exists()
    {
        var id = await SeedDefinitionAsync("REC-UNITS");
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-UNITS", units: "ms"));

        await AssertGuardedAsync("REC-UNITS", "units", "ms");
        Assert.Equal("s", (await DefinitionAsync("REC-UNITS"))!.Units);
    }

    [Fact]
    public async Task Measure_type_is_frozen_once_a_result_exists()
    {
        var id = await SeedDefinitionAsync("REC-MEASURE");
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-MEASURE", measureType: "Distance", units: "s"));

        await AssertGuardedAsync("REC-MEASURE", "measureType", "Distance");
    }

    [Fact]
    public async Task The_attempt_count_is_frozen_once_a_result_exists()
    {
        // ADR-0053 §1: a series recorded against three attempts cannot be re-read
        // against two, so this joins measure and units behind the results guard.
        var id = await SeedDefinitionAsync("REC-ATTEMPTS", d =>
        {
            d.MeasureType = MeasureType.Distance;
            d.Units = "m";
            d.AttemptCount = 3;
        });
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-ATTEMPTS", measureType: "Distance", units: "m", attempts: 2));

        await AssertGuardedAsync("REC-ATTEMPTS", "attemptCount", "2");
        Assert.Equal(3, (await DefinitionAsync("REC-ATTEMPTS"))!.AttemptCount);
    }

    [Fact]
    public async Task The_attempt_count_moves_freely_before_the_first_result()
    {
        var id = await SeedDefinitionAsync("REC-ATTEMPTS-FREE", d =>
        {
            d.MeasureType = MeasureType.Distance;
            d.Units = "m";
        });
        await SeedEventAsync(id, withResult: false);
        WriteTemplate(Definition("REC-ATTEMPTS-FREE", measureType: "Distance", units: "m", attempts: 3));

        var preview = await PreviewAsync();
        Assert.False(Row(preview, "REC-ATTEMPTS-FREE").GetProperty("guarded").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.Equal(3, (await DefinitionAsync("REC-ATTEMPTS-FREE"))!.AttemptCount);
    }

    [Fact]
    public async Task The_heights_ladder_moves_even_once_a_result_exists()
    {
        // ADR-0056, amended 2026-09-17: nothing recorded is read against the two
        // numbers — they pre-fill the deck's raise box — so no results guard.
        var id = await SeedDefinitionAsync("REC-LADDER", d =>
        {
            d.MeasureType = MeasureType.Height;
            d.Units = "m";
            d.LadderJson = """{"start":1.20,"step":0.05}""";
        });
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-LADDER", measureType: "Height", units: "m",
            ladder: new { start = 1.30m, step = 0.05m }));

        var preview = await PreviewAsync();
        Assert.False(Row(preview, "REC-LADDER").GetProperty("guarded").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.Equal("""{"start":1.30,"step":0.05}""", (await DefinitionAsync("REC-LADDER"))!.LadderJson);
    }

    [Fact]
    public async Task The_heights_ladder_moves_freely_before_the_first_result()
    {
        var id = await SeedDefinitionAsync("REC-LADDER-FREE", d =>
        {
            d.MeasureType = MeasureType.Height;
            d.Units = "m";
        });
        await SeedEventAsync(id, withResult: false);
        WriteTemplate(Definition("REC-LADDER-FREE", measureType: "Height", units: "m",
            ladder: new { start = 1.20m, step = 0.05m }));

        var preview = await PreviewAsync();
        Assert.False(Row(preview, "REC-LADDER-FREE").GetProperty("guarded").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.Equal("""{"start":1.20,"step":0.05}""",
            (await DefinitionAsync("REC-LADDER-FREE"))!.LadderJson);
    }

    [Fact]
    public async Task A_ladder_the_editor_typed_at_one_decimal_place_is_not_drift()
    {
        var id = await SeedDefinitionAsync("REC-LADDER-SCALE", d =>
        {
            d.MeasureType = MeasureType.Height;
            d.Units = "m";
            d.LadderJson = """{"start":1.20,"step":0.05}""";
        });
        WriteTemplate(Definition("REC-LADDER-SCALE", measureType: "Height", units: "m",
            ladder: new { start = 1.20m, step = 0.05m }));
        Assert.Equal("Unchanged", Row(await PreviewAsync(), "REC-LADDER-SCALE")
            .GetProperty("kind").GetString());

        // The browser sends 1.2 where the template writes 1.20. Same ladder, so
        // the editor's PUT must not leave the catalogue looking template-drifted.
        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code = "REC-LADDER-SCALE", name = "Freestyle 50m", sport = "Swimming",
            measureType = "Height", units = "m", ladder = new { start = 1.2m, step = 0.05m },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("""{"start":1.20,"step":0.05}""",
            (await DefinitionAsync("REC-LADDER-SCALE"))!.LadderJson);
        Assert.Equal("Unchanged", Row(await PreviewAsync(), "REC-LADDER-SCALE")
            .GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_definition_the_catalogue_lacks_is_created_with_its_ladder()
    {
        WriteTemplate(Definition("REC-LADDER-NEW", measureType: "Height", units: "m",
            ladder: new { start = 1.15m, step = 0.05m }));

        var preview = await PreviewAsync();
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.Equal("""{"start":1.15,"step":0.05}""",
            (await DefinitionAsync("REC-LADDER-NEW"))!.LadderJson);
    }

    [Fact]
    public async Task Dropping_the_ladder_leaves_the_bar_heights_of_every_division()
    {
        var id = await SeedDefinitionAsync("REC-LADDER-DROP", d =>
        {
            d.MeasureType = MeasureType.Height;
            d.Units = "m";
            d.LadderJson = """{"start":1.20,"step":0.05}""";
        });
        var eventId = await SeedEventAsync(id);
        await CarnivalSeed.MutateAsync(_factory, db =>
            db.CarnivalEvents.Single(e => e.Id == eventId).HeightsJson = "[1.20,1.25]");

        WriteTemplate(Definition("REC-LADDER-DROP", measureType: "Height", units: "m"));
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(await PreviewAsync()))).StatusCode);
        Assert.Null((await DefinitionAsync("REC-LADDER-DROP"))!.LadderJson);
        // The bars are the division's, contested on the day; the suggestions are
        // the definition's, and dropping them deletes nothing.
        Assert.Equal("[1.20,1.25]", await HeightsJsonAsync(eventId));
    }

    private Task<string?> HeightsJsonAsync(Guid eventId) => CarnivalSeed.QueryAsync(_factory, db =>
        db.CarnivalEvents.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => e.HeightsJson).SingleAsync());

    [Fact]
    public async Task Team_shape_is_frozen_once_team_entries_exist()
    {
        var id = await SeedDefinitionAsync("REC-TEAM", d =>
        {
            d.Format = EventFormat.Team;
            d.TeamSize = 4;
            d.LegsJson = "[\"Freestyle\",\"Freestyle\",\"Freestyle\",\"Freestyle\"]";
        });
        await SeedEventAsync(id, teamEntry: true);
        WriteTemplate(Definition("REC-TEAM", format: "Team", teamSize: 6,
            legs: ["Freestyle", "Freestyle", "Freestyle", "Freestyle", "Freestyle", "Freestyle"]));

        await AssertGuardedAsync("REC-TEAM", "teamSize", "6");
        Assert.Equal(4, (await DefinitionAsync("REC-TEAM"))!.TeamSize);
    }

    [Fact]
    public async Task Format_is_frozen_once_individual_entries_exist()
    {
        var id = await SeedDefinitionAsync("REC-INDIV");
        await SeedEventAsync(id, individualEntry: true);
        WriteTemplate(Definition("REC-INDIV", format: "Team", teamSize: 4,
            legs: ["Freestyle", "Freestyle", "Freestyle", "Freestyle"]));

        await AssertGuardedAsync("REC-INDIV", "format", "Team");
        var definition = (await DefinitionAsync("REC-INDIV"))!;
        Assert.Equal(EventFormat.Individual, definition.Format);
        Assert.Null(definition.TeamSize);
    }

    [Fact]
    public async Task Scoring_weight_is_frozen_while_a_finalised_carnival_scores_the_event()
    {
        var id = await SeedDefinitionAsync("REC-WEIGHT");
        await SeedEventAsync(id, CarnivalStatus.Finalised);
        WriteTemplate(Definition("REC-WEIGHT", scoringWeight: 2));

        await AssertGuardedAsync("REC-WEIGHT", "scoringWeight", "2");
        Assert.Equal(1, (await DefinitionAsync("REC-WEIGHT"))!.ScoringWeight);
    }

    [Fact]
    public async Task Scoring_weight_is_free_while_no_carnival_is_finalised()
    {
        var id = await SeedDefinitionAsync("REC-WEIGHT-OPEN");
        await SeedEventAsync(id, CarnivalStatus.Open, withResult: true);
        WriteTemplate(Definition("REC-WEIGHT-OPEN", scoringWeight: 2));

        var preview = await PreviewAsync();
        Assert.False(Row(preview, "REC-WEIGHT-OPEN").GetProperty("guarded").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(preview))).StatusCode);
        Assert.Equal(2, (await DefinitionAsync("REC-WEIGHT-OPEN"))!.ScoringWeight);
    }

    [Fact]
    public async Task Record_categories_are_frozen_once_a_record_cites_the_definition()
    {
        var id = await SeedDefinitionAsync("REC-BOOK", d => d.RecordCategoriesJson = null);
        await SeedSchoolRecordAsync(id);
        WriteTemplate(Definition("REC-BOOK", schoolRecords: true));

        var preview = await PreviewAsync();
        var row = Row(preview, "REC-BOOK");
        Assert.True(row.GetProperty("guarded").GetBoolean());
        Assert.NotNull(Field(row, "recordCategoriesJson").GetProperty("guard").GetString());

        var response = await ApplyAsync(Hash(preview));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TemplateReconcileUnsafe);
        Assert.Null((await DefinitionAsync("REC-BOOK"))!.RecordCategoriesJson);
    }

    [Fact]
    public async Task Excluding_the_guarded_code_applies_the_rest()
    {
        var guarded = await SeedDefinitionAsync("REC-GUARDED");
        await SeedEventAsync(guarded, withResult: true);
        await SeedDefinitionAsync("REC-FREE", d => d.Name = "Old name");
        WriteTemplate(
            Definition("REC-GUARDED", units: "ms"),
            Definition("REC-FREE", name: "Freestyle 50m"),
            Definition("REC-ADDED", name: "Butterfly 50m"));

        var preview = await PreviewAsync();
        var response = await ApplyAsync(Hash(preview), excludeCodes: ["REC-GUARDED"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, outcome.GetProperty("updated").GetInt32());
        Assert.Equal(1, outcome.GetProperty("created").GetInt32());
        Assert.Equal("REC-GUARDED", outcome.GetProperty("excludedCodes").EnumerateArray().Single().GetString());

        Assert.Equal("s", (await DefinitionAsync("REC-GUARDED"))!.Units);
        Assert.Equal("Freestyle 50m", (await DefinitionAsync("REC-FREE"))!.Name);
        Assert.NotNull(await DefinitionAsync("REC-ADDED"));
    }

    [Fact]
    public async Task An_excluded_unguarded_code_is_left_alone_too()
    {
        await SeedDefinitionAsync("REC-KEEP-MINE", d => d.Name = "Our own name");
        await SeedDefinitionAsync("REC-TAKE-THEIRS", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-KEEP-MINE"), Definition("REC-TAKE-THEIRS"));

        var response = await ApplyAsync(Hash(await PreviewAsync()), excludeCodes: [" rec-keep-mine "]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Codes are matched case-insensitively and trimmed, as they are everywhere else.
        Assert.Equal("Our own name", (await DefinitionAsync("REC-KEEP-MINE"))!.Name);
        Assert.Equal("Freestyle 50m", (await DefinitionAsync("REC-TAKE-THEIRS"))!.Name);
    }

    // ---- audit (§7) --------------------------------------------------------

    [Fact]
    public async Task Every_change_is_audited_beside_one_summary_carrying_the_reason()
    {
        await SeedDefinitionAsync("REC-AUDIT", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-AUDIT"), Definition("REC-AUDIT-NEW", name: "Butterfly 50m"));
        const string reason = "Category-first rename, ADR-0018 §2.";

        Assert.Equal(HttpStatusCode.OK,
            (await ApplyAsync(Hash(await PreviewAsync()), reason)).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.EventDefinitionUpdate
                || a.Action == AuditActions.EventDefinitionCreate
                || a.Action == AuditActions.EventTemplateReconcile)
            .ToListAsync());

        var update = Assert.Single(rows, a => a.Action == AuditActions.EventDefinitionUpdate);
        Assert.Equal(reason, update.Reason);
        Assert.Contains("\"Name\":\"Old name\"", update.PriorValuesJson);
        Assert.Contains("\"Name\":\"Freestyle 50m\"", update.NewValuesJson);
        // ADR-0045 §4 shape: the whole projection, so one definition's history
        // reads as one stream whichever surface wrote it.
        Assert.Contains("\"Usage\"", update.NewValuesJson);

        var create = Assert.Single(rows, a => a.Action == AuditActions.EventDefinitionCreate);
        Assert.Contains("REC-AUDIT-NEW", create.NewValuesJson);

        var summary = Assert.Single(rows, a => a.Action == AuditActions.EventTemplateReconcile);
        Assert.Equal(reason, summary.Reason);
        Assert.Equal(Key, summary.EntityId);
        Assert.Contains("\"Updated\":1", summary.NewValuesJson);
        Assert.Contains("\"Created\":1", summary.NewValuesJson);
        Assert.Contains("\"DiffHash\"", summary.NewValuesJson);
        Assert.Contains("recon.json", summary.NewValuesJson);
    }

    [Fact]
    public async Task A_refused_reconcile_writes_no_audit_row()
    {
        var id = await SeedDefinitionAsync("REC-NOAUDIT");
        await SeedEventAsync(id, withResult: true);
        WriteTemplate(Definition("REC-NOAUDIT", units: "ms"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await ApplyAsync(Hash(await PreviewAsync()))).StatusCode);

        var written = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.EventTemplateReconcile
                || a.Action == AuditActions.EventDefinitionUpdate));
        Assert.False(written);
    }

    [Fact]
    public async Task A_preview_taken_before_the_template_rewrote_a_missing_row_cannot_be_applied()
    {
        // The create's values never appear as diff FIELDS, and the version is
        // only the file name, so the hash has to be content-derived (§2).
        WriteTemplate(Definition("REC-CREATE", name: "Butterfly 50m"));
        var stale = Hash(await PreviewAsync());

        WriteTemplate(Definition("REC-CREATE", name: "Butterfly 100m"));

        var response = await ApplyAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TemplateReconcileStale);
        Assert.Null(await DefinitionAsync("REC-CREATE"));
    }

    [Fact]
    public async Task Replaying_an_applied_hash_is_refused()
    {
        await SeedDefinitionAsync("REC-REPLAY", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-REPLAY"));
        var hash = Hash(await PreviewAsync());

        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(hash)).StatusCode);

        // The first apply committed, so the same hash no longer describes the
        // catalogue: the second POST must not write against a spent preview.
        var replay = await ApplyAsync(hash);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        await replay.AssertErrorAsync(ErrorCodes.TemplateReconcileStale);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .CountAsync(a => a.Action == AuditActions.EventTemplateReconcile)));
    }

    [Fact]
    public async Task A_code_created_between_the_plan_and_the_write_is_refused()
    {
        WriteTemplate(Definition("REC-COLLIDE", name: "Butterfly 50m"));
        var hash = Hash(await PreviewAsync());
        _factory.Race.ArmOnce("INSERT INTO \"EventDefinitions\"", transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            db.EventDefinitions.Add(new EventDefinition
            {
                Id = Guid.CreateVersion7(),
                Code = "REC-COLLIDE",
                Name = "Someone else's row",
                Sport = "Swimming",
                MeasureType = MeasureType.Time,
                Units = "s",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        });

        var response = await ApplyAsync(hash);
        _factory.Race.Disarm();

        // The unique Code index is the backstop the transaction cannot be: the
        // collision reads as a preview that no longer describes the catalogue.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.TemplateReconcileStale);
        Assert.False(await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents
            .AnyAsync(a => a.Action == AuditActions.EventTemplateReconcile)));
    }

    [Fact]
    public async Task A_blob_that_differs_only_by_whitespace_is_not_a_change()
    {
        await SeedDefinitionAsync("REC-SPACED",
            d => d.TagsJson = "[ \"discipline:Freestyle\" ,  \"Relay\" ]");
        WriteTemplate(Definition("REC-SPACED", tags: ["discipline:Freestyle", "Relay"]));

        var preview = await PreviewAsync();
        Assert.Equal("Unchanged", Row(preview, "REC-SPACED").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_reconcilers_own_audit_rows_are_not_an_in_app_edit()
    {
        var edited = await SeedDefinitionAsync("REC-HAND", d => d.Name = "Old name");
        await SeedDefinitionAsync("REC-BOT", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-HAND"), Definition("REC-BOT"));
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync(Hash(await PreviewAsync()))).StatusCode);

        var put = await _admin.PutAsJsonAsync($"/api/v1/event-definitions/{edited}", new
        {
            code = "REC-HAND",
            name = "Our own name",
            sport = "Swimming",
            measureType = "Time",
            units = "s",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var preview = await PreviewAsync();
        Assert.False(Row(preview, "REC-BOT").GetProperty("editedInApp").GetBoolean());
        Assert.True(Row(preview, "REC-HAND").GetProperty("editedInApp").GetBoolean());
    }

    [Fact]
    public async Task An_exclude_code_the_plan_does_not_name_changes_nothing()
    {
        await SeedDefinitionAsync("REC-LISTED", d => d.Name = "Old name");
        WriteTemplate(Definition("REC-LISTED"));

        var response = await ApplyAsync(Hash(await PreviewAsync()),
            excludeCodes: ["REC-TYPO", "REC-LISTED"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("REC-LISTED",
            outcome.GetProperty("excludedCodes").EnumerateArray().Single().GetString());
        Assert.Equal("REC-TYPO",
            outcome.GetProperty("ignoredCodes").EnumerateArray().Single().GetString());

        // An exclusion nobody could act on is not evidence of one (§7).
        var summary = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.EventTemplateReconcile));
        Assert.DoesNotContain("REC-TYPO", summary.NewValuesJson);
        Assert.Contains("REC-LISTED", summary.NewValuesJson);
        Assert.Equal("Old name", (await DefinitionAsync("REC-LISTED"))!.Name);
    }
}
