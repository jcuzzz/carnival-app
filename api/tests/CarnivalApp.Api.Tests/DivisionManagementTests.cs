using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>Shared setup for the ADR-0022 division-management surface.</summary>
internal static class DivisionFixture
{
    public static async Task<Guid> CreateCarnivalAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/carnivals",
            new { name, type = "Swimming", date = "2027-02-25" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>A catalogue row with a code unique to the calling test — the
    /// factory's database is shared across a class and the code index is not.</summary>
    public static async Task<Guid> CreateDefinitionAsync(HttpClient admin, string code)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"{code}-{Guid.CreateVersion7():N}"[..24],
            name = "Freestyle 50m", sport = "Swimming", measureType = "Time", units = "s",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;
}

/// <summary>
/// The division profile (ADR-0022 §2) as ADR-0027 stores it: one
/// CarnivalDivision row per contested age × category × tier, served on the
/// unchanged settings wire — validate-on-write, absent-field-means-unchanged,
/// and the carnival.settings audit action all survive the move.
/// </summary>
public sealed class DivisionProfileSettingsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("profile-admin", "CarnivalAdministrator");

    private static object Profile => new
    {
        ageGroups = new[] { "12", "16" },
        categories = new[] { "M", "F" },
        tiers = new[] { "Competitive", "Social" },
    };

    [Fact]
    public async Task A_carnival_with_no_divisions_key_reads_as_not_configured()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Unconfigured");

        var settings = await DivisionFixture.Json(await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings"));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisions").ValueKind);
        // Not an empty matrix, and nothing else about the settings has changed.
        Assert.NotEmpty(settings.GetProperty("adjustmentCategories").EnumerateArray());
    }

    [Fact]
    public async Task The_profile_round_trips_and_a_later_write_that_omits_it_leaves_it_alone()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Profile round trip");

        var written = await admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings",
            new { divisions = Profile });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var divisions = (await DivisionFixture.Json(written)).GetProperty("divisions");
        Assert.Equal(["12", "16"], divisions.GetProperty("ageGroups").EnumerateArray()
            .Select(g => g.GetString()!));
        Assert.Equal(["M", "F"], divisions.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetString()!));
        Assert.Equal(["Competitive", "Social"], divisions.GetProperty("tiers").EnumerateArray()
            .Select(t => t.GetString()!));

        // Absent means unchanged, exactly as the lane count and the scoring block behave.
        var laneOnly = await admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings", new { laneCount = 8 });
        Assert.Equal(HttpStatusCode.OK, laneOnly.StatusCode);
        var after = await DivisionFixture.Json(
            await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings"));
        Assert.Equal(8, after.GetProperty("laneCount").GetInt32());
        Assert.Equal(2, after.GetProperty("divisions").GetProperty("ageGroups").GetArrayLength());

        Assert.True(await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.AnyAsync(a =>
            a.EntityId == carnivalId.ToString() && a.Action == AuditActions.CarnivalSettings)));
    }

    /// <summary>
    /// The audit blob is the settings a write can change, and nothing else: the
    /// roster fields of the read DTO are facts about the roster, and recorded
    /// beside a settings change an auditor would take them for evidence that the
    /// roster was empty at the time.
    /// </summary>
    [Fact]
    public async Task The_audit_blob_carries_the_settings_and_not_the_roster_fields()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Audited settings");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { divisions = Profile })).StatusCode);

        var blobs = await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents
            .Where(a => a.EntityId == carnivalId.ToString()
                && a.Action == AuditActions.CarnivalSettings)
            .Select(a => new { a.PriorValuesJson, a.NewValuesJson })
            .ToListAsync());
        var blob = Assert.Single(blobs);

        foreach (var json in new[] { blob.PriorValuesJson, blob.NewValuesJson })
        {
            using var document = JsonDocument.Parse(Assert.IsType<string>(json));
            Assert.Equal(
                ["Scoring", "AdjustmentCategories", "LaneCount", "AutoFillLanes", "Divisions"],
                document.RootElement.EnumerateObject().Select(m => m.Name));
        }
        // The change itself is still there to read, both sides of it.
        Assert.Equal(JsonValueKind.Null,
            JsonDocument.Parse(blob.PriorValuesJson!).RootElement.GetProperty("Divisions").ValueKind);
        Assert.Equal(2, JsonDocument.Parse(blob.NewValuesJson!).RootElement
            .GetProperty("Divisions").GetProperty("AgeGroups").GetArrayLength());
    }

    /// <summary>
    /// A profile code must satisfy the same AgeScope rules a division's does, or
    /// the divisions generated from it match no student at all — a failure that
    /// would otherwise surface as every entry being refused on the day. OPEN is
    /// refused here too: a profile row is a CONTESTED age (ADR-0058 §1).
    /// </summary>
    [Theory]
    [InlineData("13-14")]
    [InlineData("OPEN")]
    public async Task An_age_a_division_could_not_carry_is_refused(string age)
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, $"Bad age {age}");

        var response = await admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings", new
        {
            divisions = new
            {
                ageGroups = new[] { age },
                categories = new[] { "M" },
                tiers = new[] { "Competitive" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AgeScopeInvalid);

        var settings = await DivisionFixture.Json(await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings"));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisions").ValueKind);
    }

    [Theory]
    // No ages at all, one age named twice (which would generate two divisions
    // colliding on one unique key), and no categories.
    [InlineData("""{"ageGroups":[],"categories":["M"],"tiers":["Competitive"]}""")]
    [InlineData("""{"ageGroups":["13","13"],"categories":["M"],"tiers":["Competitive"]}""")]
    [InlineData("""{"ageGroups":["13"],"categories":[],"tiers":["Competitive"]}""")]
    public async Task An_unusable_profile_is_refused(string profile)
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Unusable profile");

        var body = new StringContent($$"""{"divisions":{{profile}}}""",
            System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync($"/api/v1/carnivals/{carnivalId}/settings", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Applying_a_template_seeds_the_profile_and_a_reapply_leaves_an_edited_one_alone()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Seeded profile");
        var applied = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template",
            new { template = "swimming" });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((await DivisionFixture.Json(applied)).GetProperty("divisionProfileSeeded").GetBoolean());

        var seeded = (await DivisionFixture.Json(
                await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings")))
            .GetProperty("divisions");
        Assert.Equal(7, seeded.GetProperty("ageGroups").GetArrayLength());
        Assert.Equal(["Competitive", "Social"],
            seeded.GetProperty("tiers").EnumerateArray().Select(t => t.GetString()!));

        // The profile is the carnival's own statement of what it contests, so a
        // re-apply must not overwrite one an administrator has narrowed.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { divisions = Profile })).StatusCode);
        var reapplied = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template",
            new { template = "swimming" });
        Assert.Equal(HttpStatusCode.OK, reapplied.StatusCode);
        Assert.False((await DivisionFixture.Json(reapplied)).GetProperty("divisionProfileSeeded").GetBoolean());
        Assert.Equal(2, (await DivisionFixture.Json(
                await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings")))
            .GetProperty("divisions").GetProperty("ageGroups").GetArrayLength());
    }

    /// <summary>
    /// ADR-0027: the profile is rows now, and the settings blob is no longer
    /// where it lives — a write must not put it back.
    /// </summary>
    [Fact]
    public async Task The_profile_is_stored_as_one_row_per_contested_combination()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Profile rows");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { divisions = Profile })).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalDivisions
            .Where(d => d.CarnivalId == carnivalId).OrderBy(d => d.SortOrder).ToListAsync());
        Assert.Equal(8, rows.Count); // 2 ages × 2 categories × 2 tiers
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], rows.Select(r => r.SortOrder));
        // Age-major, then category, then tier — the order the axes are read back from.
        Assert.Equal(
            ["12", "12", "12", "12", "16", "16", "16", "16"],
            rows.Select(r => r.AgeGroup));
        Assert.Equal(
            [CompetitionCategory.M, CompetitionCategory.M, CompetitionCategory.F, CompetitionCategory.F],
            rows.Take(4).Select(r => r.Category));
        Assert.Equal(
            [EventTier.Competitive, EventTier.Social], rows.Take(2).Select(r => r.Tier));

        var settingsJson = await CarnivalSeed.QueryAsync(factory, db => db.Carnivals
            .Where(c => c.Id == carnivalId).Select(c => c.SettingsJson).SingleAsync());
        Assert.DoesNotContain("ivisions", settingsJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Saving the same profile twice replaces its rows rather than colliding with
    /// them: the unique index is (carnival, age, category, tier), so the
    /// delete and the insert have to land in one SaveChanges in that order.
    /// </summary>
    [Fact]
    public async Task Re_saving_a_profile_replaces_its_rows()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Profile replace");
        var path = $"/api/v1/carnivals/{carnivalId}/settings";
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(path, new { divisions = Profile })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(path, new { divisions = Profile })).StatusCode);

        Assert.Equal(8, await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalDivisions.CountAsync(d => d.CarnivalId == carnivalId)));

        // Narrowing it removes the rows the narrower profile no longer names.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(path, new
        {
            divisions = new
            {
                ageGroups = new[] { "12" },
                categories = new[] { "M" },
                tiers = new[] { "Competitive" },
            },
        })).StatusCode);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalDivisions.CountAsync(d => d.CarnivalId == carnivalId)));
    }

    /// <summary>
    /// The Up half of ADR-0027's data migration, at the level SQLite can reach:
    /// a carnival whose profile was written into the blob, and the rows the
    /// migration reads out of it, must serve the SAME profile the blob declared —
    /// and the superseded key stays in the blob afterwards (ADR-0022 §5's
    /// keep-it-one-release precedent), untouched by a later settings write.
    /// </summary>
    [Fact]
    public async Task A_carnival_whose_profile_was_a_blob_reads_the_same_profile_back_from_rows()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Migrated profile");

        // Exactly what the pre-ADR-0027 writer produced (PascalCase; the settings
        // serializer never had a naming policy).
        const string legacyBlob = """
            {"Scoring":{"Version":"v1","PlacePoints":[4,3,2],"ParticipationFloor":1,"DnfPoints":1,"DnsPoints":0,"DqPoints":0,"SocialPerEntryPoints":1},"AdjustmentCategories":["Novelty 1"],"Divisions":{"AgeBands":[{"Label":"12","Groups":["12"]},{"Label":"16+","Groups":["16","17","18"]}],"Categories":["M"],"Tiers":["Competitive","Social"]}}
            """;
        // The rows that blob becomes, written out longhand rather than through
        // DivisionProfile.ToRows — the point is the shape, not a round trip.
        await CarnivalSeed.MutateAsync(factory, db =>
        {
            db.Carnivals.Single(c => c.Id == carnivalId).SettingsJson = legacyBlob;
            var order = 0;
            foreach (var age in new[] { "12", "16" })
                foreach (var tier in new[] { EventTier.Competitive, EventTier.Social })
                    db.CarnivalDivisions.Add(new CarnivalDivision
                    {
                        Id = Guid.CreateVersion7(),
                        CarnivalId = carnivalId,
                        AgeGroup = age,
                        Category = CompetitionCategory.M,
                        Tier = tier,
                        SortOrder = order++,
                    });
        });

        var divisions = (await DivisionFixture.Json(
            await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings"))).GetProperty("divisions");
        Assert.Equal(["12", "16"],
            divisions.GetProperty("ageGroups").EnumerateArray().Select(g => g.GetString()!));
        Assert.Equal(["M"], divisions.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetString()!));
        Assert.Equal(["Competitive", "Social"], divisions.GetProperty("tiers").EnumerateArray()
            .Select(t => t.GetString()!));

        // A settings write carries the superseded key through untouched.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { laneCount = 8 })).StatusCode);
        var after = await CarnivalSeed.QueryAsync(factory, db => db.Carnivals
            .Where(c => c.Id == carnivalId).Select(c => c.SettingsJson).SingleAsync());
        Assert.Contains(
            """Divisions":{"AgeBands":[{"Label":"12","Groups":["12"]}""",
            after!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The frozen legacy key must not become a permanent refusal: nothing can
    /// shrink it, so a settings write that would overflow the column drops it and
    /// re-checks (ADR-0027 — the rows are the truth, and Down rebuilds the key
    /// from them). A write that fits leaves it verbatim.
    /// </summary>
    [Fact]
    public async Task A_settings_write_over_the_ceiling_drops_the_legacy_key_rather_than_refusing()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Fat legacy key");
        var path = $"/api/v1/carnivals/{carnivalId}/settings";

        // A pre-ADR-0027 blob that fits the column with the divisions key in it.
        var categories = Enumerable.Range(0, 40)
            .Select(i => $"\"Novelty {i:00} {new string('x', 55)}\"");
        const string head = """
            {"Scoring":{"Version":"v1","PlacePoints":[4,3,2],"ParticipationFloor":1,"DnfPoints":1,"DnsPoints":0,"DqPoints":0,"SocialPerEntryPoints":1},"AdjustmentCategories":[
            """;
        const string tail = """
            ],"Divisions":{"AgeBands":[{"Label":"12","Groups":["12"]},{"Label":"13","Groups":["13"]},{"Label":"14","Groups":["14"]},{"Label":"15","Groups":["15"]},{"Label":"16","Groups":["16"]},{"Label":"17","Groups":["17"]},{"Label":"18","Groups":["18"]}],"Categories":["M","F"],"Tiers":["Competitive","Social"]}}
            """;
        var legacyBlob = head + string.Join(",", categories) + tail;
        Assert.InRange(legacyBlob.Length, 3000, CarnivalSettings.MaxSettingsJsonLength);
        await CarnivalSeed.MutateAsync(factory,
            db => db.Carnivals.Single(c => c.Id == carnivalId).SettingsJson = legacyBlob);

        // A write that fits carries the key through untouched.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(path, new { laneCount = 8 })).StatusCode);
        Assert.Contains("\"Divisions\":", await StoredSettingsAsync(carnivalId), StringComparison.Ordinal);

        // One that does not: the legacy key goes, and the write is not refused.
        var longer = Enumerable.Range(0, 52).Select(i => $"Novelty {i:00} {new string('x', 55)}").ToList();
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(path, new { adjustmentCategories = longer })).StatusCode);
        var stored = await StoredSettingsAsync(carnivalId);
        Assert.DoesNotContain("ivisions", stored, StringComparison.Ordinal);
        Assert.InRange(stored.Length, 3000, CarnivalSettings.MaxSettingsJsonLength);
        Assert.Equal(52, (await DivisionFixture.Json(await admin.GetAsync(path)))
            .GetProperty("adjustmentCategories").GetArrayLength());
    }

    private async Task<string> StoredSettingsAsync(Guid carnivalId) =>
        (await CarnivalSeed.QueryAsync(factory, db => db.Carnivals
            .Where(c => c.Id == carnivalId).Select(c => c.SettingsJson).SingleAsync()))!;

    /// <summary>
    /// The defect ADR-0027 §2 says dies with the cause: a template seed that
    /// would have overflowed the shared settings column wrote NOTHING and
    /// reported success, leaving the carnival reading "profile not configured".
    /// Rows have no shared ceiling, so a big profile seeds in full.
    /// </summary>
    [Fact]
    public async Task A_profile_that_could_not_have_fitted_the_settings_column_seeds_in_full()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Crowded settings");
        // Fills the blob to just under its 4000-character limit, so the swimming
        // profile could not have been added to it.
        var categories = Enumerable.Range(0, 55).Select(i => $"Novelty {i:00} {new string('x', 55)}").ToList();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new { adjustmentCategories = categories })).StatusCode);
        var blobLength = (await CarnivalSeed.QueryAsync(factory, db => db.Carnivals
            .Where(c => c.Id == carnivalId).Select(c => c.SettingsJson).SingleAsync()))!.Length;
        Assert.InRange(blobLength, 3700, 4000);

        var applied = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template",
            new { template = "swimming" });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((await DivisionFixture.Json(applied)).GetProperty("divisionProfileSeeded").GetBoolean());

        var seeded = (await DivisionFixture.Json(
                await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings")))
            .GetProperty("divisions");
        Assert.Equal(7, seeded.GetProperty("ageGroups").GetArrayLength());
        Assert.Equal(28, await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalDivisions.CountAsync(d => d.CarnivalId == carnivalId)));
    }

    /// <summary>
    /// ADR-0022 §3's one-place-only property, across the move: the profile's rows
    /// and the divisions a template apply schedules are the same age × category
    /// × tier product, and generating that product again creates nothing.
    /// </summary>
    [Fact]
    public async Task The_profile_rows_the_apply_and_the_generate_endpoint_agree_on_the_matrix()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "One loop");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/apply-template", new { template = "swimming" })).StatusCode);

        var rows = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalDivisions
            .Where(d => d.CarnivalId == carnivalId).OrderBy(d => d.SortOrder).ToListAsync());
        Assert.Equal(28, rows.Count); // 7 ages × M/F × Competitive/Social

        // SW-50-FREE contests every profile combination and nothing else: it
        // overrides neither the tier list nor the age axis.
        var definitionId = await CarnivalSeed.QueryAsync(factory, db => db.EventDefinitions
            .Where(d => d.Code == "SW-50-FREE").Select(d => d.Id).SingleAsync());
        var scheduled = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalEvents
            .Where(e => e.CarnivalId == carnivalId && e.EventDefinitionId == definitionId)
            .ToListAsync());
        Assert.Equal(
            rows.Select(r => (r.AgeGroup, (CompetitionCategory?)r.Category, r.Tier)).ToHashSet(),
            scheduled.Select(e => (e.AgeGroup, e.Category, e.Tier)).ToHashSet());

        var generated = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events/generate", new
        {
            eventDefinitionId = definitionId,
            divisions = rows.Select(r => new
            {
                ageGroup = r.AgeGroup,
                category = r.Category.ToString(),
                tier = r.Tier.ToString(),
            }).ToList(),
        });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var body = await DivisionFixture.Json(generated);
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(28, body.GetProperty("skipped").GetInt32());
    }

    /// <summary>
    /// Rows that FromRows refuses read as "profile not configured" and no write
    /// path can reach them, so an apply must REPAIR rather than skip: seeding
    /// only on "no rows at all" would strand such a carnival permanently.
    /// </summary>
    [Fact]
    public async Task Applying_a_template_repairs_rows_that_do_not_read_as_a_profile()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Junk rows");
        // A 20-character age code: storable on SQLite, but longer than AgeScope
        // allows, so the profile these rows describe fails Validate.
        const string junk = "AAAAAAAAAAAAAAAAAAAA";
        await CarnivalSeed.MutateAsync(factory, db => db.CarnivalDivisions.Add(new CarnivalDivision
        {
            Id = Guid.CreateVersion7(),
            CarnivalId = carnivalId,
            AgeGroup = junk,
            Category = CompetitionCategory.M,
            Tier = EventTier.Competitive,
            SortOrder = 0,
        }));
        Assert.Equal(JsonValueKind.Null, (await DivisionFixture.Json(
                await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings")))
            .GetProperty("divisions").ValueKind);

        var applied = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template",
            new { template = "swimming" });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True((await DivisionFixture.Json(applied)).GetProperty("divisionProfileSeeded").GetBoolean());

        var rows = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalDivisions
            .Where(d => d.CarnivalId == carnivalId).ToListAsync());
        Assert.Equal(28, rows.Count);
        Assert.DoesNotContain(rows, r => r.AgeGroup == junk);
        Assert.Equal(7, (await DivisionFixture.Json(
                await admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings")))
            .GetProperty("divisions").GetProperty("ageGroups").GetArrayLength());
    }

    /// <summary>
    /// Two applies racing one carnival: the loser's rows collide on the unique
    /// index, and it must still return 200 with divisionProfileSeeded false —
    /// pre-ADR-0027 the blob write was last-write-wins. The competing insert runs
    /// on the handler's own transaction (see <see cref="RaceInterceptor"/>), so
    /// it disappears with the rollback; the branch under test is the loser's.
    /// </summary>
    [Fact]
    public async Task An_apply_that_loses_the_profile_seed_race_still_succeeds()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Seed race");
        factory.Race.ArmOnce("INSERT INTO \"CarnivalDivisions\"", transaction =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            db.CarnivalDivisions.Add(new CarnivalDivision
            {
                Id = Guid.CreateVersion7(),
                CarnivalId = carnivalId,
                AgeGroup = "12",
                Category = CompetitionCategory.M,
                Tier = EventTier.Competitive,
                SortOrder = 0,
            });
            db.SaveChanges();
        });

        var applied = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/apply-template",
            new { template = "swimming" });
        factory.Race.Disarm();
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var body = await DivisionFixture.Json(applied);
        Assert.False(body.GetProperty("divisionProfileSeeded").GetBoolean());
        // The rest of the apply still landed, and exactly one audit row describes it.
        Assert.True(body.GetProperty("eventsCreated").GetInt32() > 0);
        Assert.Equal(1, await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.CountAsync(a =>
            a.EntityId == carnivalId.ToString() && a.Action == AuditActions.CarnivalApplyTemplate)));
    }
}

/// <summary>
/// POST /carnivals/{id}/events/generate (ADR-0022 §3): one call, one
/// transaction, idempotent on definition + age + category + tier.
/// </summary>
public sealed class GenerateDivisionsEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("generate-admin", "CarnivalAdministrator");

    private static object MatrixRequest(Guid definitionId) => new
    {
        eventDefinitionId = definitionId,
        divisions = new object[]
        {
            new { ageGroup = "13", category = "M", tier = "Competitive" },
            new { ageGroup = "13", category = "F", tier = "Competitive" },
            new { ageGroup = "16", category = "M", tier = "Competitive" },
            new { ageGroup = "16", category = "F", tier = "Social" },
        },
    };

    [Fact]
    public async Task Generate_materialises_the_matrix_in_one_call()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Generate");
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "GEN");

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/generate", MatrixRequest(definitionId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await DivisionFixture.Json(response);
        Assert.Equal(4, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());

        var events = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalEvents
            .Where(e => e.CarnivalId == carnivalId).ToListAsync());
        Assert.Equal(4, events.Count);
        Assert.Equal(["13", "13", "16", "16"], events.Select(e => e.AgeGroup).Order());
        Assert.Single(events, e => e.Tier == EventTier.Social);

        Assert.Equal(4, await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.CountAsync(a =>
            a.Action == AuditActions.CarnivalEventSchedule
            && events.Select(e => e.Id.ToString()).Contains(a.EntityId))));
    }

    [Fact]
    public async Task A_second_press_creates_nothing()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Re-press");
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "GEN2");
        var path = $"/api/v1/carnivals/{carnivalId}/events/generate";

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(path, MatrixRequest(definitionId))).StatusCode);
        var second = await admin.PostAsJsonAsync(path, MatrixRequest(definitionId));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body = await DivisionFixture.Json(second);
        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(4, body.GetProperty("skipped").GetInt32());
        Assert.Equal(4, await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId)));
    }

    /// <summary>A division listed twice in ONE request is the same "already
    /// scheduled" case as one the carnival already holds.</summary>
    [Fact]
    public async Task A_division_listed_twice_in_one_request_is_created_once()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Duplicated");
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "GEN3");

        var response = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events/generate", new
        {
            eventDefinitionId = definitionId,
            divisions = new object[]
            {
                new { ageGroup = "13", category = "M" },
                new { ageGroup = "13", category = "M" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await DivisionFixture.Json(response);
        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task An_inactive_definition_is_refused_exactly_as_scheduling_one_is()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Inactive");
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "GEN4");
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/v1/event-definitions/{definitionId}")).StatusCode);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/generate", MatrixRequest(definitionId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EventDefinitionInactive);
        Assert.Equal(0, await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalEvents.CountAsync(e => e.CarnivalId == carnivalId)));
    }

    [Fact]
    public async Task An_age_no_entry_could_match_is_refused()
    {
        var admin = Admin;
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, "Bad scope");
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "GEN5");

        var response = await admin.PostAsJsonAsync($"/api/v1/carnivals/{carnivalId}/events/generate", new
        {
            eventDefinitionId = definitionId,
            divisions = new object[] { new { ageGroup = "13-14", category = "M" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.AgeScopeInvalid);
    }

    [Fact]
    public async Task Generation_is_refused_on_a_carnival_that_is_no_longer_editable()
    {
        var admin = Admin;
        var seeded = await CarnivalSeed.SeedAsync(factory, CarnivalStatus.Closed);
        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{seeded.CarnivalId}/events/generate", MatrixRequest(seeded.DefinitionId));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);
    }
}

/// <summary>
/// DELETE /carnivals/{id}/events/{eventId} (ADR-0022 §4): deletion is for a
/// division created in error; a contested one is closed instead.
/// </summary>
public sealed class UnscheduleDivisionTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("unschedule-admin", "CarnivalAdministrator");

    private static string EventPath(SeededCarnival seeded) =>
        $"/api/v1/carnivals/{seeded.CarnivalId}/events/{seeded.EventId}";

    [Fact]
    public async Task An_empty_division_is_removed_and_audited_with_its_prior_projection()
    {
        var admin = Admin;
        var seeded = await CarnivalSeed.SeedAsync(factory);
        var scheduled = await admin.PostAsJsonAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events", new
        {
            eventDefinitionId = seeded.DefinitionId,
            ageGroup = seeded.JuniorAgeGroup,
            category = "F",
        });
        Assert.Equal(HttpStatusCode.Created, scheduled.StatusCode);
        var eventId = (await DivisionFixture.Json(scheduled)).GetProperty("id").GetGuid();

        var response = await admin.DeleteAsync($"/api/v1/carnivals/{seeded.CarnivalId}/events/{eventId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await CarnivalSeed.QueryAsync(factory, db => db.CarnivalEvents.AnyAsync(e => e.Id == eventId)));

        var audit = await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.SingleAsync(a =>
            a.EntityId == eventId.ToString() && a.Action == AuditActions.CarnivalEventUnschedule));
        Assert.Contains(seeded.JuniorAgeGroup, audit.PriorValuesJson!);
    }

    [Fact]
    public async Task A_division_with_entries_is_refused_and_kept()
    {
        var admin = Admin;
        var seeded = await CarnivalSeed.SeedAsync(factory);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(
            $"{EventPath(seeded)}/entries", new { studentId = seeded.EligibleBoyId })).StatusCode);

        var response = await admin.DeleteAsync(EventPath(seeded));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EventHasEntries);
        Assert.Contains("Close it instead", await response.Content.ReadAsStringAsync());
        Assert.True(await CarnivalSeed.QueryAsync(factory,
            db => db.CarnivalEvents.AnyAsync(e => e.Id == seeded.EventId)));
    }

    /// <summary>
    /// The case a bare entries check would turn into a raw foreign-key error:
    /// Entry → CarnivalEvent cascades, Result → Entry restricts.
    /// </summary>
    [Fact]
    public async Task A_division_whose_entries_carry_results_is_refused_with_both_counts()
    {
        var admin = Admin;
        var official = factory.CreateClientAs(CarnivalSeed.AssignedOfficial, "Recorder");
        var seeded = await CarnivalSeed.SeedAsync(factory);
        var entryId = (await DivisionFixture.Json(await admin.PostAsJsonAsync(
                $"{EventPath(seeded)}/entries", new { studentId = seeded.EligibleBoyId })))
            .GetProperty("id").GetGuid();
        var submission = new HttpRequestMessage(
            HttpMethod.Post, $"{EventPath(seeded)}/entries/{entryId}/results")
        {
            Content = JsonContent.Create(new { value = "34.56" }),
            Headers = { { "Idempotency-Key", Guid.CreateVersion7().ToString() } },
        };
        Assert.Equal(HttpStatusCode.Created, (await official.SendAsync(submission)).StatusCode);

        var response = await admin.DeleteAsync(EventPath(seeded));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.EventHasEntries);
        var body = await DivisionFixture.Json(response);
        Assert.Equal(1, body.GetProperty("entryCount").GetInt32());
        Assert.Equal(1, body.GetProperty("resultCount").GetInt32());
        Assert.True(await CarnivalSeed.QueryAsync(factory, db => db.Results.AnyAsync()));
    }

    [Fact]
    public async Task Unscheduling_is_refused_on_a_carnival_that_is_no_longer_editable()
    {
        var seeded = await CarnivalSeed.SeedAsync(factory, CarnivalStatus.Closed);
        var response = await Admin.DeleteAsync(EventPath(seeded));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);
    }
}

/// <summary>The two catalogue gaps Stage A left (ADR-0022 §6): the request DTO
/// could not set tags, and the projection carried no usage counts.</summary>
public sealed class EventDefinitionCatalogueTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("catalogue-admin", "CarnivalAdministrator");

    [Fact]
    public async Task Tags_are_settable_on_create_and_update()
    {
        var admin = Admin;
        var code = $"TAG-{Guid.CreateVersion7():N}"[..20];
        var created = await admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code, name = "Tagged", sport = "Swimming", measureType = "Time", units = "s",
            tagsJson = """["discipline:Freestyle","50m"]""",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await DivisionFixture.Json(created)).GetProperty("id").GetGuid();

        var updated = await admin.PutAsJsonAsync($"/api/v1/event-definitions/{id}", new
        {
            code, name = "Tagged", sport = "Swimming", measureType = "Time", units = "s",
            tagsJson = """["discipline:Butterfly"]""",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains("Butterfly", await CarnivalSeed.QueryAsync(factory,
            db => db.EventDefinitions.Where(d => d.Id == id).Select(d => d.TagsJson!).SingleAsync()));
    }

    [Fact]
    public async Task Tags_must_be_a_json_array_not_an_object()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/event-definitions", new
        {
            code = $"TAG-BAD-{Guid.CreateVersion7():N}"[..20],
            name = "Tagged", sport = "Swimming", measureType = "Time", units = "s",
            tagsJson = """{"discipline":"Freestyle"}""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ValidationFailed);
        Assert.Contains("array", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The counts behind the editor's rename notice: renaming relabels every
    /// division of the event in every carnival, and this is what that costs.
    /// </summary>
    [Fact]
    public async Task Usage_counts_the_carnivals_divisions_and_records_a_rename_would_touch()
    {
        var admin = Admin;
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "USE");
        foreach (var name in new[] { "Usage A", "Usage B" })
        {
            var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, name);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
                $"/api/v1/carnivals/{carnivalId}/events/generate", new
                {
                    eventDefinitionId = definitionId,
                    divisions = new object[]
                    {
                        new { ageGroup = "13", category = "M" },
                        new { ageGroup = "13", category = "F" },
                    },
                })).StatusCode);
        }

        var listed = await DivisionFixture.Json(
            await admin.GetAsync("/api/v1/event-definitions/?includeInactive=true"));
        var usage = listed.EnumerateArray()
            .Single(d => d.GetProperty("id").GetGuid() == definitionId)
            .GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("carnivals").GetInt32());
        Assert.Equal(4, usage.GetProperty("divisions").GetInt32());
        Assert.Equal(0, usage.GetProperty("schoolRecords").GetInt32());
    }
}


/// <summary>
/// POST /carnivals/{id}/events/open-scheduled (ADR-0054 §4): one press opens
/// every Scheduled division of the carnival, touches nothing else, and is
/// idempotent.
/// </summary>
public sealed class OpenScheduledDivisionsTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private HttpClient Admin => factory.CreateClientAs("open-all-admin", "CarnivalAdministrator");

    private static object FiveDivisions(Guid definitionId) => new
    {
        eventDefinitionId = definitionId,
        divisions = new object[]
        {
            new { ageGroup = "12", category = "M" },
            new { ageGroup = "12", category = "F" },
            new { ageGroup = "13", category = "M" },
            new { ageGroup = "13", category = "F" },
            new { ageGroup = "14", category = "M" },
        },
    };

    /// <summary>Five divisions, three of them moved off Scheduled by hand.</summary>
    private async Task<(Guid CarnivalId, List<Guid> Scheduled)> SeedAsync(HttpClient admin, string name)
    {
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(admin, name);
        var definitionId = await DivisionFixture.CreateDefinitionAsync(admin, "OPENALL");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/generate", FiveDivisions(definitionId))).StatusCode);

        var ids = await CarnivalSeed.QueryAsync(factory, db => db.CarnivalEvents
            .Where(e => e.CarnivalId == carnivalId).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
        Assert.Equal(5, ids.Count);
        await CarnivalSeed.MutateAsync(factory, db =>
        {
            var rows = db.CarnivalEvents.Where(e => e.CarnivalId == carnivalId).OrderBy(e => e.Id).ToList();
            rows[2].Status = CarnivalEventStatus.Open;
            rows[3].Status = CarnivalEventStatus.Closed;
            rows[4].Status = CarnivalEventStatus.Finalised;
        });
        return (carnivalId, [ids[0], ids[1]]);
    }

    private async Task<Dictionary<Guid, CarnivalEventStatus>> StatusesAsync(Guid carnivalId) =>
        await CarnivalSeed.QueryAsync(factory, db => db.CarnivalEvents
            .Where(e => e.CarnivalId == carnivalId).ToDictionaryAsync(e => e.Id, e => e.Status));

    [Fact]
    public async Task Open_all_opens_every_scheduled_division_and_leaves_the_rest_alone()
    {
        var admin = Admin;
        var (carnivalId, scheduled) = await SeedAsync(admin, "Open all");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/status", new { status = "Open" })).StatusCode);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/open-scheduled", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await DivisionFixture.Json(response);
        Assert.Equal(2, body.GetProperty("opened").GetInt32());
        Assert.Equal(scheduled, body.GetProperty("eventIds").EnumerateArray().Select(e => e.GetGuid()).ToList());

        var statuses = await StatusesAsync(carnivalId);
        Assert.All(scheduled, id => Assert.Equal(CarnivalEventStatus.Open, statuses[id]));
        Assert.Equal(
            [CarnivalEventStatus.Open, CarnivalEventStatus.Open, CarnivalEventStatus.Open,
             CarnivalEventStatus.Closed, CarnivalEventStatus.Finalised],
            statuses.OrderBy(s => s.Key).Select(s => s.Value));
    }

    [Fact]
    public async Task One_audit_row_names_the_count_and_the_ids()
    {
        var admin = Admin;
        var (carnivalId, scheduled) = await SeedAsync(admin, "Open all audit");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/status", new { status = "Open" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/open-scheduled", new { })).StatusCode);

        var audit = await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.SingleAsync(a =>
            a.EntityId == carnivalId.ToString() && a.Action == AuditActions.CarnivalEventOpenAll));
        Assert.Equal(nameof(Carnival), audit.EntityType);
        Assert.Equal("open-all-admin", audit.ActorSubject);
        Assert.Null(audit.PriorValuesJson);
        var values = JsonDocument.Parse(audit.NewValuesJson!).RootElement;
        Assert.Equal(2, values.GetProperty("opened").GetInt32());
        Assert.Equal(scheduled, values.GetProperty("eventIds").EnumerateArray().Select(e => e.GetGuid()).ToList());
    }

    [Fact]
    public async Task A_second_press_opens_nothing_and_audits_nothing()
    {
        var admin = Admin;
        var (carnivalId, _) = await SeedAsync(admin, "Open all twice");
        var path = $"/api/v1/carnivals/{carnivalId}/events/open-scheduled";
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/status", new { status = "Open" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(path, new { })).StatusCode);

        var second = await admin.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await DivisionFixture.Json(second);
        Assert.Equal(0, body.GetProperty("opened").GetInt32());
        Assert.Empty(body.GetProperty("eventIds").EnumerateArray());
        Assert.Equal(1, await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.CountAsync(a =>
            a.EntityId == carnivalId.ToString() && a.Action == AuditActions.CarnivalEventOpenAll)));
    }

    /// <summary>The state gate is the single-event one (ADR-0054 §4, amended):
    /// a Draft carnival's divisions can be opened ahead of the day.</summary>
    [Fact]
    public async Task A_draft_carnival_opens_its_scheduled_divisions()
    {
        var admin = Admin;
        var (carnivalId, scheduled) = await SeedAsync(admin, "Open all on a draft");

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/open-scheduled", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await DivisionFixture.Json(response)).GetProperty("opened").GetInt32());

        var statuses = await StatusesAsync(carnivalId);
        Assert.All(scheduled, id => Assert.Equal(CarnivalEventStatus.Open, statuses[id]));
    }

    [Fact]
    public async Task A_finalised_carnival_refuses_and_opens_nothing()
    {
        var admin = Admin;
        var (carnivalId, scheduled) = await SeedAsync(admin, "Open all on a finalised");
        await CarnivalSeed.MutateAsync(factory, db =>
            db.Carnivals.Single(c => c.Id == carnivalId).Status = CarnivalStatus.Finalised);

        var response = await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/open-scheduled", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotEditable);

        var statuses = await StatusesAsync(carnivalId);
        Assert.All(scheduled, id => Assert.Equal(CarnivalEventStatus.Scheduled, statuses[id]));
        Assert.False(await CarnivalSeed.QueryAsync(factory, db => db.AuditEvents.AnyAsync(a =>
            a.EntityId == carnivalId.ToString() && a.Action == AuditActions.CarnivalEventOpenAll)));
    }

    [Fact]
    public async Task An_unknown_carnival_is_a_404()
    {
        var response = await Admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{Guid.NewGuid()}/events/open-scheduled", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalNotFound);
    }

    /// <summary>The refusal on a carnival that really has divisions to open, so
    /// a widened policy cannot pass unnoticed by opening them.</summary>
    [Fact]
    public async Task A_recorder_is_refused_and_nothing_opens()
    {
        var admin = Admin;
        var (carnivalId, scheduled) = await SeedAsync(admin, "Open all as recorder");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/status", new { status = "Open" })).StatusCode);

        var recorder = factory.CreateClientAs("open-all-recorder", "Recorder");
        var response = await recorder.PostAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/events/open-scheduled", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var statuses = await StatusesAsync(carnivalId);
        Assert.All(scheduled, id => Assert.Equal(CarnivalEventStatus.Scheduled, statuses[id]));
    }
}

/// <summary>
/// ADR-0057 §1-§2: the settings read carries the current roster's age codes and,
/// while no profile is saved, the profile those codes describe. Each test owns
/// its factory — the roster is school-wide, so a shared database would let one
/// test's students turn up in another's codes.
/// </summary>
public sealed class RosterAgeGroupsSettingsTests : IDisposable
{
    private readonly ApiTestFactory _factory = new();
    private readonly HttpClient _admin;

    public RosterAgeGroupsSettingsTests() =>
        _admin = _factory.CreateClientAs("roster-default-admin", "CarnivalAdministrator");

    public void Dispose() => _factory.Dispose();

    private Task SeedRosterAsync(params (string AgeGroup, bool Current, bool Active)[] rows) =>
        SeedRosterAsync(rows.Select(r => (r.AgeGroup, r.Current, r.Active, 2027)).ToArray());

    private async Task SeedRosterAsync(
        params (string AgeGroup, bool Current, bool Active, int SchoolYear)[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var batch = new ImportBatch
        {
            Id = Guid.CreateVersion7(),
            SourceFileName = "roster.csv",
            SourceFileHash = "roster-hash",
            MappingVersion = "test",
            ImportedBySubject = "seeder",
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = ImportBatchStatus.Promoted,
        };
        db.ImportBatches.Add(batch);

        var n = 0;
        foreach (var (ageGroup, current, active, schoolYear) in rows)
        {
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = $"ROSTER-{++n}",
                Surname = "Synthetic",
                FirstName = $"Student{n}",
                IsActive = active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Students.Add(student);
            db.RosterSnapshots.Add(new RosterSnapshot
            {
                Id = Guid.CreateVersion7(),
                StudentId = student.Id,
                SchoolYear = schoolYear,
                YearLevel = 7,
                HouseCode = "BANKSIA",
                CompetitionAgeGroup = ageGroup,
                CompetitionCategory = CompetitionCategory.M,
                ImportBatchId = batch.Id,
                EffectiveDate = new DateOnly(2027, 1, 30),
                IsCurrent = current,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> SettingsAsync(Guid carnivalId) => await DivisionFixture.Json(
        await _admin.GetAsync($"/api/v1/carnivals/{carnivalId}/settings"));

    private static void AssertRosterDefault(JsonElement settings, params string[] codes)
    {
        var profile = settings.GetProperty("divisionsDefault");
        Assert.Equal(codes,
            profile.GetProperty("ageGroups").EnumerateArray().Select(g => g.GetString()!));
        Assert.Equal(["M", "F"],
            profile.GetProperty("categories").EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(["Competitive"],
            profile.GetProperty("tiers").EnumerateArray().Select(t => t.GetString()!));
    }

    [Fact]
    public async Task The_codes_are_the_current_rosters_youngest_first_and_they_become_the_default()
    {
        await SeedRosterAsync(
            ("13", true, true), ("12", true, true), ("SNR", true, true), ("13", true, true),
            // Neither of these is a student an entry could match, so neither
            // contributes a code: one superseded snapshot, one departed student.
            ("11", false, true), ("11", true, false));
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "Roster default");

        var settings = await SettingsAsync(carnivalId);
        Assert.Equal(["12", "13", "SNR"],
            settings.GetProperty("rosterAgeGroups").EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisions").ValueKind);
        AssertRosterDefault(settings, "12", "13", "SNR");
    }

    [Fact]
    public async Task A_saved_profile_leaves_no_default_but_the_codes_are_still_read()
    {
        await SeedRosterAsync(("12", true, true), ("13", true, true));
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "Saved profile");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PutAsJsonAsync(
            $"/api/v1/carnivals/{carnivalId}/settings", new
            {
                divisions = new
                {
                    ageGroups = new[] { "12" },
                    categories = new[] { "M" },
                    tiers = new[] { "Competitive" },
                },
            })).StatusCode);

        var settings = await SettingsAsync(carnivalId);
        Assert.Equal(["12", "13"],
            settings.GetProperty("rosterAgeGroups").EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisionsDefault").ValueKind);
        Assert.Equal(1, settings.GetProperty("divisions").GetProperty("ageGroups").GetArrayLength());
    }

    [Fact]
    public async Task No_roster_means_no_codes_and_no_default()
    {
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "No roster");

        var settings = await SettingsAsync(carnivalId);
        Assert.Empty(settings.GetProperty("rosterAgeGroups").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisionsDefault").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisions").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("rosterYear").ValueKind);
    }

    /// <summary>
    /// A student missing from the latest file keeps last year's current snapshot, so the roster's
    /// year is the one most current snapshots carry — what the setup checklist compares against
    /// the carnival's date.
    /// </summary>
    [Fact]
    public async Task The_roster_year_is_the_year_most_of_the_current_roster_carries()
    {
        await SeedRosterAsync(
            ("12", true, true, 2027), ("13", true, true, 2027), ("14", true, true, 2026),
            // Neither counts: a superseded snapshot and a departed student.
            ("15", false, true, 2025), ("16", true, false, 2025), ("16", true, false, 2025));
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "Roster year");

        Assert.Equal(2027, (await SettingsAsync(carnivalId)).GetProperty("rosterYear").GetInt32());
    }

    /// <summary>
    /// An implausible birthdate past the cut-off derives a negative age, which
    /// AgeScope refuses: the editor must not offer an age the settings PUT would
    /// then reject, and the default must not name one either.
    /// </summary>
    [Fact]
    public async Task A_code_no_division_could_carry_is_neither_offered_nor_defaulted()
    {
        await SeedRosterAsync(("12", true, true), ("-12", true, true), ("13", true, true));
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "Impossible code");

        var settings = await SettingsAsync(carnivalId);
        Assert.Equal(["12", "13"],
            settings.GetProperty("rosterAgeGroups").EnumerateArray().Select(c => c.GetString()!));
        AssertRosterDefault(settings, "12", "13");
    }

    /// <summary>Both fields are read-only: a client that echoes the whole settings
    /// document back is accepted and stores neither.</summary>
    [Fact]
    public async Task The_put_ignores_the_two_read_only_fields()
    {
        await SeedRosterAsync(("12", true, true));
        var carnivalId = await DivisionFixture.CreateCarnivalAsync(_admin, "Echoed fields");

        var put = await _admin.PutAsJsonAsync($"/api/v1/carnivals/{carnivalId}/settings", new
        {
            laneCount = 6,
            rosterAgeGroups = new[] { "99" },
            divisionsDefault = new
            {
                ageGroups = new[] { "99" },
                categories = new[] { "M", "F" },
                tiers = new[] { "Competitive" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var settings = await SettingsAsync(carnivalId);
        Assert.Equal(6, settings.GetProperty("laneCount").GetInt32());
        Assert.Equal(["12"],
            settings.GetProperty("rosterAgeGroups").EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("divisions").ValueKind);
        AssertRosterDefault(settings, "12");
        Assert.Equal(0, await CarnivalSeed.QueryAsync(_factory,
            db => db.CarnivalDivisions.CountAsync(d => d.CarnivalId == carnivalId)));
    }
}

/// <summary>ADR-0057 §2: the roster default is a profile the editor could save,
/// or nothing at all — never an invalid one.</summary>
public sealed class DivisionProfileFromRosterTests
{
    [Fact]
    public void A_roster_past_the_age_ceiling_has_no_default()
    {
        var codes = Enumerable.Range(1, DivisionProfile.MaxAgeGroups).Select(i => $"A{i}").ToList();

        var atCeiling = DivisionProfile.FromRoster(codes);
        Assert.NotNull(atCeiling);
        Assert.Equal(DivisionProfile.MaxAgeGroups, atCeiling.AgeGroups.Count);
        Assert.Null(DivisionProfile.Validate(atCeiling.ToDto()));

        Assert.Null(DivisionProfile.FromRoster([.. codes, "A33"]));
        Assert.Null(DivisionProfile.FromRoster([]));
    }

    /// <summary>A birthdate the import accepts but no school has ("2039") derives
    /// a negative age, which no division may carry — one such row must not cost
    /// every carnival in the school its default.</summary>
    [Fact]
    public void A_code_no_division_could_carry_is_skipped_rather_than_refusing_the_default()
    {
        var profile = DivisionProfile.FromRoster(["12", "-9", "13"]);
        Assert.NotNull(profile);
        Assert.Equal(["12", "13"], profile.AgeGroups);
        Assert.Null(DivisionProfile.FromRoster(["-9"]));
    }

    /// <summary>The stored code upper-cases, so two casings of one age are one
    /// row; as two they would share a key and refuse the whole default.</summary>
    [Fact]
    public void Two_casings_of_one_code_are_one_age()
    {
        var profile = DivisionProfile.FromRoster(["SNR", "snr"]);
        Assert.NotNull(profile);
        Assert.Single(profile.AgeGroups);
    }
}
