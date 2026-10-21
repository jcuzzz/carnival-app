using System.Net;
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
/// Record-candidate detection (ADR-0028 §1/§2): finalisation mints the book, and
/// nothing else does. What is pinned is every rule that decides whether a swim
/// reaches the book at all, the shape of a supersession, and the audit evidence.
/// </summary>
public sealed class RecordDetectionTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory = factory;

    /// <summary>The blob a templated 13/14 swimming definition carries.</summary>
    private static string BoysAndGirls(params string[] bands) =>
        RecordsSeed.Categories(bands, CompetitionCategory.M, CompetitionCategory.F);

    /// <summary>Every row of a definition's book, oldest first. Ordered in memory:
    /// SQLite cannot ORDER BY a DateTimeOffset.</summary>
    private async Task<List<SchoolRecord>> RecordsAsync(Guid definitionId)
    {
        var rows = await CarnivalSeed.QueryAsync(_factory, db => db.SchoolRecords.AsNoTracking()
            .Where(r => r.EventDefinitionId == definitionId)
            .ToListAsync());
        return [.. rows.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)];
    }

    /// <summary>The mint rows for the records THIS carnival set — record provenance
    /// is the existing Result → Entry → CarnivalEvent path (§1, no CarnivalId column).</summary>
    private async Task<List<AuditEvent>> MintAuditAsync(Guid carnivalId)
    {
        var ids = await CarnivalSeed.QueryAsync(_factory, db => db.SchoolRecords.AsNoTracking()
            .Where(r => r.Result.Entry.CarnivalEvent.CarnivalId == carnivalId)
            .Select(r => r.Id)
            .ToListAsync());
        // Formatted client-side: SQLite stores a Guid as UPPERCASE text, and the
        // audit row's EntityId is what Guid.ToString() wrote.
        var minted = ids.Select(id => id.ToString()).ToList();
        return await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.SchoolRecordMint && minted.Contains(a.EntityId))
            .ToListAsync());
    }

    // ---- Seeding and superseding ------------------------------------------

    [Fact]
    public async Task The_first_finalisation_seeds_the_book_having_beaten_nothing()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m, Heat: 1),
                new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 30.5m, Heat: 2))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId, reason: "Published")).StatusCode);

        var record = Assert.Single(await RecordsAsync(seeded.DefinitionIds["FREE"]));
        // The EVENT's best, not heat 1's winner (ADR-0015 §2).
        Assert.Equal("Cora Diaz", record.HolderDisplayName);
        Assert.Equal(30.5m, record.RecordValue);
        Assert.Equal("13", record.AgeGroup);
        Assert.Equal(CompetitionCategory.M, record.Category);
        Assert.Equal(SchoolRecordStatus.Ratified, record.Status);
        Assert.Null(record.PreviousRecordId);
        Assert.Null(record.EffectiveTo);
        Assert.Equal(seeded.Date, record.EffectiveFrom);
        // The approval fields name the finaliser and the act (§2).
        Assert.Equal("records-finaliser", record.ApprovedBySubject);
        Assert.NotNull(record.ApprovedAtUtc);
        Assert.Equal("Published", record.ApprovalNote);
    }

    [Fact]
    public async Task A_stored_blob_that_also_names_open_still_mints_its_other_ages()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE-OPEN", """{"ageGroups":["13","OPEN"],"categories":["M","F"]}""")
            .Division("FREE-OPEN", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var record = Assert.Single(await RecordsAsync(seeded.DefinitionIds["FREE-OPEN"]));
        Assert.Equal(("13", CompetitionCategory.M), (record.AgeGroup, record.Category));
    }

    [Fact]
    public async Task A_better_time_supersedes_with_a_new_row_and_the_old_one_closed()
    {
        var first = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        var second = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 30.9m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, second.CarnivalId)).StatusCode);

        var rows = await RecordsAsync(first.DefinitionIds["FREE"]);
        Assert.Equal(2, rows.Count);
        var old = rows[0];
        var current = rows[1];
        // Nothing is overwritten: the old row keeps its value and gains an end.
        Assert.Equal(31.2m, old.RecordValue);
        Assert.Equal(SchoolRecordStatus.Superseded, old.Status);
        Assert.Equal(second.Date, old.EffectiveTo);
        Assert.Equal(SchoolRecordStatus.Ratified, current.Status);
        Assert.Equal(30.9m, current.RecordValue);
        Assert.Equal("Blake Chen", current.HolderDisplayName);
        Assert.Equal(old.Id, current.PreviousRecordId);
        Assert.Null(current.EffectiveTo);
    }

    [Fact]
    public async Task A_back_dated_carnival_supersedes_without_inverting_the_closed_period()
    {
        var later = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, later.CarnivalId)).StatusCode);

        // Dated earlier, finalised afterwards: the standing row would otherwise
        // be closed before the day it opened.
        var backDated = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 30.9m))
            .BuildAsync(_factory, reusing: later, date: new DateOnly(2027, 3, 6));
        Assert.Equal(HttpStatusCode.OK,
            (await RecordsSeed.FinaliseAsync(_factory, backDated.CarnivalId)).StatusCode);

        var rows = await RecordsAsync(later.DefinitionIds["FREE"]);
        Assert.Equal(2, rows.Count);
        var old = rows[0];
        Assert.Equal(new DateOnly(2028, 3, 4), old.EffectiveFrom);
        Assert.Equal(old.EffectiveFrom, old.EffectiveTo);
        Assert.Equal(SchoolRecordStatus.Superseded, old.Status);
        Assert.Equal(backDated.Date, rows[1].EffectiveFrom);
    }

    [Fact]
    public async Task Two_identical_best_times_mint_exactly_one_record_deterministically()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Zara Young", "WATTLE", 30.5m, Heat: 2),
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 30.5m, Heat: 1))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        // The ladder's total order is rank → holder name → entry id, so a tie
        // for a NEW record resolves to one row and the same one every run (§1).
        var record = Assert.Single(await RecordsAsync(seeded.DefinitionIds["FREE"]));
        Assert.Equal("Aiden Brook", record.HolderDisplayName);
        var mint = Assert.Single(await MintAuditAsync(seeded.CarnivalId));
        Assert.Contains("\"holder\":\"Aiden Brook\"", mint.NewValuesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_equal_time_does_not_supersede_the_standing_record()
    {
        var first = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        var second = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 31.2m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, second.CarnivalId)).StatusCode);

        // The earlier holder keeps the book entry; the equalling swim is an
        // ordinary result (§1).
        var record = Assert.Single(await RecordsAsync(first.DefinitionIds["FREE"]));
        Assert.Equal("Aiden Brook", record.HolderDisplayName);
        Assert.Null(record.EffectiveTo);
        Assert.Empty(await MintAuditAsync(second.CarnivalId));
    }

    // ---- What never reaches the book --------------------------------------

    [Fact]
    public async Task A_social_division_mints_nothing_however_fast_it_was()
    {
        // Tier is a RESULT-time rule read off the event INSTANCE, never off the
        // catalogue blob (ruling 3) — the blob here declares the same band.
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M, EventTier.Social,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 20.0m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    [Fact]
    public async Task An_open_category_division_mints_nothing_by_design()
    {
        // Owner ruling 2, and the blob lists only M and F, so the Open division's
        // best is skipped entirely rather than filed under some other category.
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.S,
                new RecordsSeed.Swim("Emery Frost", "GREVILLEA", 25.0m))
            .Division("FREE", "13", null,
                new RecordsSeed.Swim("Finn Gray", "WATTLE", 26.0m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    [Fact]
    public async Task A_definition_declaring_no_records_mints_nothing()
    {
        var seeded = await new RecordsSeed()
            .Definition("NOVELTY", recordCategories: null)
            .Division("NOVELTY", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["NOVELTY"]));
    }

    [Fact]
    public async Task An_unreadable_blob_mints_nothing_and_does_not_fail_the_finalisation()
    {
        // Fail CLOSED (§3): a missing record is repairable at the next carnival,
        // a false one is in the book.
        var seeded = await new RecordsSeed()
            .Definition("FREE", "{\"ageGroups\": [\"13\"], \"categories\": ")
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    [Fact]
    public async Task A_division_the_blob_does_not_list_mints_nothing()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "15", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    [Fact]
    public async Task A_division_of_non_results_alone_mints_nothing()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Dale Egan", "GREVILLEA", null, ResultStatus.Dns),
                new RecordsSeed.Swim("Emery Frost", "WATTLE", null, ResultStatus.Dnf),
                new RecordsSeed.Swim("Finn Gray", "MOUNTAIN ASH", null, ResultStatus.Dq))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    // ---- Ages and relays ---------------------------------------------------

    [Fact]
    public async Task A_category_is_one_age_so_a_second_age_of_the_same_definition_mints_its_own()
    {
        // ADR-0058 §1: (definition, age, category), always — 13 and 14 hold
        // separate records off separate ladders.
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13", "14"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .Division("FREE", "14", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 30.4m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var records = await RecordsAsync(seeded.DefinitionIds["FREE"]);
        Assert.Equal(["13", "14"], records.Select(r => r.AgeGroup).Order());
        Assert.Equal("Aiden Brook", Assert.Single(records, r => r.AgeGroup == "13").HolderDisplayName);
        Assert.Equal("Blake Chen", Assert.Single(records, r => r.AgeGroup == "14").HolderDisplayName);
    }

    [Fact]
    public async Task An_age_group_relay_mints_with_the_team_as_holder_and_the_all_ages_one_mints_nothing()
    {
        // The age relay's code is in the blob; the all-ages relay's is OPEN, which
        // is not an age and never reaches it — a book indexed by age has no shelf
        // for an all-ages contest (§1).
        var seeded = await new RecordsSeed()
            .Definition("RELAY", BoysAndGirls("13"), format: EventFormat.Team)
            .Division("RELAY", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("BANKSIA team", "BANKSIA", 121.4m, Team: true),
                new RecordsSeed.Swim("WATTLE team", "WATTLE", 120.2m, Team: true))
            .Division("RELAY", null, CompetitionCategory.M,
                new RecordsSeed.Swim("GREVILLEA team", "GREVILLEA", 118.0m, Team: true))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var record = Assert.Single(await RecordsAsync(seeded.DefinitionIds["RELAY"]));
        Assert.Equal("13", record.AgeGroup);
        // The team's entry snapshot; the four swimmers stay reachable through
        // ResultId → Entry → RelayLegs.
        Assert.Equal("WATTLE team", record.HolderDisplayName);
    }

    // ---- Audit and atomicity -----------------------------------------------

    [Fact]
    public async Task One_audit_row_per_mint_and_the_finalisation_row_counts_them()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .Division("FREE", "13", CompetitionCategory.F,
                new RecordsSeed.Swim("Cora Diaz", "MOUNTAIN ASH", 33.4m))
            .BuildAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId)).StatusCode);

        var mints = await MintAuditAsync(seeded.CarnivalId);
        Assert.Equal(2, mints.Count);
        Assert.All(mints, m => Assert.Equal(nameof(SchoolRecord), m.EntityType));
        Assert.All(mints, m => Assert.Equal("records-finaliser", m.ActorSubject));
        Assert.Contains(mints, m => m.NewValuesJson!.Contains("\"holder\":\"Aiden Brook\"", StringComparison.Ordinal));
        Assert.Contains(mints, m => m.NewValuesJson!.Contains($"\"definitionCode\":\"{seeded.DefinitionCodes["FREE"]}\"",
            StringComparison.Ordinal));
        // A seed superseded nothing, so there is no prior value to record.
        Assert.All(mints, m => Assert.Null(m.PriorValuesJson));

        var status = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.CarnivalStatus && a.EntityId == seeded.CarnivalId.ToString())
            .ToListAsync());
        Assert.Contains(status, a => a.NewValuesJson!.Contains("\"recordsSet\":2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_supersession_records_the_record_it_replaced()
    {
        var first = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, first.CarnivalId)).StatusCode);

        var second = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Blake Chen", "WATTLE", 30.9m))
            .BuildAsync(_factory, reusing: first, date: new DateOnly(2028, 3, 4));
        Assert.Equal(HttpStatusCode.OK, (await RecordsSeed.FinaliseAsync(_factory, second.CarnivalId)).StatusCode);

        var mint = Assert.Single(await MintAuditAsync(second.CarnivalId));
        Assert.Contains("\"value\":31.2", mint.PriorValuesJson!, StringComparison.Ordinal);
        Assert.Contains("\"holder\":\"Aiden Brook\"", mint.PriorValuesJson!, StringComparison.Ordinal);
        Assert.Contains("\"previousRecordId\":", mint.NewValuesJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_finalisation_mints_nothing()
    {
        // The completeness gate runs first, so an entry still owing a result stops
        // the whole act — and the book must not have moved.
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m),
                new RecordsSeed.Swim("Blake Chen", "WATTLE", null, Resulted: false))
            .BuildAsync(_factory);

        var response = await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.CarnivalFinalisationBlocked);
        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
    }

    [Fact]
    public async Task A_finalisation_refused_at_the_re_check_rolls_the_whole_mint_back()
    {
        var seeded = await new RecordsSeed()
            .Definition("FREE", BoysAndGirls("13"))
            .Division("FREE", "13", CompetitionCategory.M,
                new RecordsSeed.Swim("Aiden Brook", "BANKSIA", 31.2m))
            .BuildAsync(_factory);

        // The mint runs BEFORE the TOCTOU re-check, so an entry landing behind
        // the gate has to take the staged records and their audit rows with it.
        _factory.Race.ArmOnce(CarnivalEndpoints.CarnivalGateRecheckTag, transaction =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SportsDbContext>();
            if (transaction is not null) db.Database.UseTransaction(transaction);
            var student = new Student
            {
                Id = Guid.CreateVersion7(),
                ExternalStudentId = $"LATE-{Guid.NewGuid():N}",
                Surname = "Chen",
                FirstName = "Blake",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Students.Add(student);
            db.Entries.Add(new Entry
            {
                Id = Guid.CreateVersion7(),
                CarnivalEventId = seeded.EventIds[0],
                StudentId = student.Id,
                SnapshotDisplayName = "Blake Chen",
                SnapshotYearLevel = 7,
                SnapshotHouseCode = "WATTLE",
                SnapshotAgeGroup = "13",
                SnapshotCategory = CompetitionCategory.M,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedBySubject = "late-official",
            });
            db.SaveChanges();
        });

        var response = await RecordsSeed.FinaliseAsync(_factory, seeded.CarnivalId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await response.AssertErrorAsync(ErrorCodes.ConcurrencyConflict);

        Assert.Empty(await RecordsAsync(seeded.DefinitionIds["FREE"]));
        var mints = await CarnivalSeed.QueryAsync(_factory, db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == AuditActions.SchoolRecordMint
                        && a.NewValuesJson!.Contains(seeded.DefinitionCodes["FREE"]))
            .ToListAsync());
        Assert.Empty(mints);
    }
}
