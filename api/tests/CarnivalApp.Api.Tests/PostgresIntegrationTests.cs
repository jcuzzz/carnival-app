using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Testcontainers.PostgreSql;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Real-database tests: EF migrations apply cleanly to PostgreSQL 17 and
/// promotion is transactional. They require Docker, which is not available on
/// the primary dev machine — they skip at runtime when Docker is absent and
/// carry Category=RequiresDocker so CI/VM runs can select them explicitly
/// (e.g. dotnet test --filter Category=RequiresDocker).
/// </summary>
[Trait("Category", "RequiresDocker")]
public class PostgresIntegrationTests
{
    private static readonly Lazy<bool> DockerAvailable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            return process.WaitForExit(15000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    private static async Task WithPostgresAsync(Func<DbContextOptions<SportsDbContext>, Task> test)
    {
        Skip.IfNot(DockerAvailable.Value, "Docker is not available on this machine.");
        // Image on the constructor: Testcontainers 4.14 obsoleted the parameterless one.
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<SportsDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options;
        await using (var db = new SportsDbContext(options))
            await db.Database.MigrateAsync(); // migrations, not EnsureCreated — this is the migration test
        await test(options);
    }

    [SkippableFact]
    public async Task Migrations_apply_cleanly_to_postgres_17()
    {
        await WithPostgresAsync(async options =>
        {
            await using var db = new SportsDbContext(options);
            var pending = await db.Database.GetPendingMigrationsAsync();
            Assert.Empty(pending);
            Assert.Equal(0, await db.Students.CountAsync());
        });
    }

    /// <summary>
    /// ADR-0027's data migration, which is the only one in the repo that moves
    /// data and the only place its SQL ever runs: roll back to the blob schema,
    /// write a carnival's profile the way the pre-move code wrote it, and migrate
    /// forward. The rows must say exactly what the blob said, the superseded key
    /// must survive, and <c>Down</c> must rebuild the key from the rows — for a
    /// profile the blob never held, too. Every band is single-age: ADR-0058's
    /// migration refuses to carry a merged one across, so a merged band here
    /// would fail the forward run by design.
    /// </summary>
    [SkippableFact]
    public async Task The_division_profile_migration_reads_the_blob_out_and_writes_it_back()
    {
        const string beforeTheMove = "20260818021332_DropEventDefinitionDivisionsJson";
        const string legacyBlob = """
            {"Scoring":{"Version":"v1","PlacePoints":[4,3,2],"ParticipationFloor":1,"DnfPoints":1,"DnsPoints":0,"DqPoints":0,"SocialPerEntryPoints":1},"AdjustmentCategories":["Novelty 1"],"Divisions":{"AgeBands":[{"Label":"12","Groups":["12"]},{"Label":"16","Groups":["16"]}],"Categories":["M"],"Tiers":["Competitive","Social"]}}
            """;

        await WithPostgresAsync(async options =>
        {
            var blobbed = Guid.CreateVersion7();
            await using (var db = new SportsDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync(beforeTheMove);
                db.Carnivals.Add(NewCarnival(blobbed, "Blob profile", legacyBlob));
                await db.SaveChangesAsync();
            }

            await using (var db = new SportsDbContext(options))
                await db.Database.MigrateAsync();

            Guid rowsOnly;
            await using (var db = new SportsDbContext(options))
            {
                var rows = await db.CarnivalDivisions
                    .Where(d => d.CarnivalId == blobbed).OrderBy(d => d.SortOrder).ToListAsync();
                Assert.Equal(4, rows.Count); // 2 ages × 1 category × 2 tiers
                Assert.Equal(["12", "12", "16", "16"], rows.Select(r => r.AgeGroup));
                Assert.All(rows, r => Assert.Equal(CompetitionCategory.M, r.Category));
                Assert.Equal(
                    [EventTier.Competitive, EventTier.Social, EventTier.Competitive, EventTier.Social],
                    rows.Select(r => r.Tier));

                // Kept for one release (ADR-0022 §5's precedent), so a code-only
                // rollback still finds a profile.
                Assert.Contains("\"Divisions\"",
                    await db.Carnivals.Where(c => c.Id == blobbed)
                        .Select(c => c.SettingsJson).SingleAsync());

                // A profile created AFTER the move: rows, and no key in any blob.
                rowsOnly = Guid.CreateVersion7();
                db.Carnivals.Add(NewCarnival(rowsOnly, "Rows only", null));
                db.CarnivalDivisions.Add(new CarnivalDivision
                {
                    Id = Guid.CreateVersion7(),
                    CarnivalId = rowsOnly,
                    AgeGroup = "13",
                    Category = CompetitionCategory.F,
                    Tier = EventTier.Social,
                    SortOrder = 0,
                });
                await db.SaveChangesAsync();
            }

            await using (var db = new SportsDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync(beforeTheMove);

            await using (var verify = new SportsDbContext(options))
            {
                var restored = await verify.Carnivals.Where(c => c.Id == rowsOnly)
                    .Select(c => c.SettingsJson).SingleAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(restored!);
                var divisions = doc.RootElement.GetProperty("Divisions");
                var band = divisions.GetProperty("AgeBands").EnumerateArray().Single();
                Assert.Equal("13", band.GetProperty("Label").GetString());
                Assert.Equal(["13"],
                    band.GetProperty("Groups").EnumerateArray().Select(g => g.GetString()!));
                Assert.Equal(["F"],
                    divisions.GetProperty("Categories").EnumerateArray().Select(c => c.GetString()!));
                Assert.Equal(["Social"],
                    divisions.GetProperty("Tiers").EnumerateArray().Select(t => t.GetString()!));

                // The blobbed carnival keeps exactly one Divisions key, rebuilt.
                var rebuilt = await verify.Carnivals.Where(c => c.Id == blobbed)
                    .Select(c => c.SettingsJson).SingleAsync();
                using var back = System.Text.Json.JsonDocument.Parse(rebuilt!);
                Assert.Equal(["12", "16"], back.RootElement.GetProperty("Divisions")
                    .GetProperty("AgeBands").EnumerateArray()
                    .Select(b => b.GetProperty("Label").GetString()!));
                Assert.True(back.RootElement.TryGetProperty("Scoring", out _));
            }
        });
    }

    private static Carnival NewCarnival(Guid id, string name, string? settingsJson) => new()
    {
        Id = id,
        Name = name,
        Type = CarnivalType.Swimming,
        Date = new DateOnly(2027, 2, 25),
        Status = CarnivalStatus.Draft,
        RuleVersion = "v1",
        OwnerSubject = "migration-test",
        SettingsJson = settingsJson,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Audit rows are append-only BELOW the API too (proposal §9.3). The
    /// application principal owns this schema, so a REVOKE it can grant back to
    /// itself is not a control; the trigger refuses the statement whatever the
    /// grant says. PostgreSQL-only by construction — the migration is guarded on
    /// the provider — so this is the only place it can be proved.
    /// </summary>
    [SkippableFact]
    public async Task An_audit_row_cannot_be_updated_or_deleted_on_postgres()
    {
        await WithPostgresAsync(async options =>
        {
            await using var db = new SportsDbContext(options);
            db.AuditEvents.Add(new AuditEvent
            {
                ActorSubject = "trigger-test",
                AtUtc = DateTimeOffset.UtcNow,
                Action = "test.append",
                EntityType = nameof(Carnival),
                EntityId = Guid.CreateVersion7().ToString(),
                Reason = "original",
            });
            await db.SaveChangesAsync();
            var id = await db.AuditEvents.Select(a => a.Id).SingleAsync();

            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                """UPDATE "AuditEvents" SET "Reason" = 'tampered' WHERE "Id" = {0}""", id));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "AuditEvents" WHERE "Id" = {0}""", id));

            await using var verify = new SportsDbContext(options);
            var row = await verify.AuditEvents.SingleAsync();
            Assert.Equal("original", row.Reason);
        });
    }

    [SkippableFact]
    public async Task Promotion_commits_atomically_on_postgres()
    {
        await WithPostgresAsync(async options =>
        {
            await using var db = new SportsDbContext(options);
            var service = Fixtures.ImportService(db);
            await using var stream = File.OpenRead(Fixtures.Path("ST_CLEAN.csv"));
            var outcome = await service.StageAsync(
                stream, "ST_CLEAN.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
            await service.ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer);
            await service.PromoteAsync(outcome.Batch.Id, Fixtures.Importer);

            // Atomicity means the database matches the batch's approved staging
            // summary exactly — derived, not hardcoded, so fixture regeneration
            // cannot silently break the only Docker-gated promotion test. Exact
            // fixture counts are pinned by ImportPipelineSqliteTests.
            var batch = outcome.Batch;
            Assert.True(batch.NewCount > 0);
            Assert.Equal(batch.NewCount, await db.RosterSnapshots.CountAsync(s => s.IsCurrent));
            Assert.Equal(batch.NewCount, await db.Students.CountAsync(s => s.IsActive));
            Assert.Equal(batch.InactiveCount, await db.Students.CountAsync(s => !s.IsActive));
        });
    }

    [SkippableFact]
    public async Task Failed_promotion_rolls_back_completely()
    {
        await WithPostgresAsync(async options =>
        {
            // Corrupt one staged row so promotion throws mid-way (missing
            // required snapshot data after approval), then verify no partial write.
            await using (var db = new SportsDbContext(options))
            {
                var service = Fixtures.ImportService(db);
                var csv = "STKEY,SURNAME,FIRST_NAME,PREF_NAME,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n" +
                          "A1,Smith,Ann,Ann,F,WATTLE,ACTV,2013-01-20,07A,07\n" +
                          "B2,Jones,Bo,Bo,M,BANKSIA,ACTV,2011-06-01,09B,09\n";
                var outcome = await service.StageAsync(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)),
                    "t.csv", Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses, Fixtures.Importer);
                await service.ApproveAsync(outcome.Batch.Id, true, null, Fixtures.Reviewer);

                var batch = await db.ImportBatches.SingleAsync(b => b.Id == outcome.Batch.Id);
                var doc = RosterImportService.GetDocument(batch);
                doc.Rows[1].YearLevel = null; // sabotage: NRE inside the transaction
                batch.StagedRowsJson = System.Text.Json.JsonSerializer.Serialize(doc,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    });
                await db.SaveChangesAsync();

                await Assert.ThrowsAnyAsync<Exception>(() => service.PromoteAsync(batch.Id, Fixtures.Importer));
            }

            await using var verify = new SportsDbContext(options);
            Assert.Equal(0, await verify.Students.CountAsync());
            Assert.Equal(0, await verify.RosterSnapshots.CountAsync());
            Assert.Equal(ImportBatchStatus.Approved,
                (await verify.ImportBatches.SingleAsync()).Status);
        });
    }
}
