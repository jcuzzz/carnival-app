using System.Text;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using CarnivalApp.Api.School;

namespace CarnivalApp.Api.Data;

/// <summary>
/// Synthetic seed for the Development SQLite loop, only against an empty database. The roster goes
/// through the real import pipeline so batch, audit and snapshot behaviour match production.
/// </summary>
public static class DevelopmentSeeder
{
    public const string SeedSubject = "dev-seed";

    /// <summary>Stamped on the school row so a synthetic database is recognisable.</summary>
    public const string SchoolIdentifier = "DEV-SYNTH";

    private const int TargetStudents = 40;

    /// <summary>
    /// Fixed, not today's: the seeded ages, carnival and fixed-mode screenshots must not move with
    /// the calendar. The carnival shares it, so the setup checklist raises no year mismatch.
    /// </summary>
    public const int SyntheticCompetitionYear = 2026;

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<CarnivalAppDbContext>();
        // The school row is the bootstrap's; a roster or carnival means seeded, or a real dataset.
        if (await db.Students.AnyAsync(ct) || await db.Carnivals.AnyAsync(ct))
            return;

        var config = services.GetRequiredService<IConfiguration>();
        var env = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DevelopmentSeeder");
        var houses = (await services.GetRequiredService<SchoolSettingsProvider>().GetAsync(ct))
            .Houses.Select(house => house.Code).ToArray();
        var now = DateTimeOffset.UtcNow;

        var actor = new AuditActor(SeedSubject, nameof(PlatformRole.SystemAdministrator));
        var importService = services.GetRequiredService<RosterImportService>();
        var mapping = ImportMapping.LoadFromFile(Resolve(env,
            config["Imports:MappingPath"] ?? "../../../config/import-mapping.example.json"));
        var rosterPath = Resolve(env,
            config["Database:Seed:RosterCsvPath"] ?? "../../../data/synthetic/ST_SYNTH.csv");
        var subsetCsv = BuildActiveSubset(await File.ReadAllBytesAsync(rosterPath, ct), houses, mapping);

        using var csvStream = new MemoryStream(Encoding.UTF8.GetBytes(subsetCsv));
        var outcome = await importService.StageAsync(csvStream, "ST_SYNTH.dev-seed.csv", mapping,
            SyntheticCompetitionYear, houses, actor, ct);
        if (outcome.StructuralErrors.Count > 0)
            throw new InvalidOperationException(
                $"Dev seed roster failed validation: {string.Join("; ", outcome.StructuralErrors)}");
        await importService.ApproveAsync(outcome.Batch.Id, approve: true, "Development seed", actor, ct);
        await importService.PromoteAsync(outcome.Batch.Id, actor, ct);

        var definition = new EventDefinition
        {
            Id = Guid.CreateVersion7(),
            Code = "SW-50-FREE",
            Name = "Freestyle 50m",
            Sport = "Swimming",
            MeasureType = MeasureType.Time,
            Units = "s",
            CreatedAtUtc = now,
        };
        db.EventDefinitions.Add(definition);

        var carnival = new Carnival
        {
            Id = Guid.CreateVersion7(),
            Name = $"{SyntheticCompetitionYear} Swimming Carnival (synthetic)",
            Type = CarnivalType.Swimming,
            Date = new DateOnly(SyntheticCompetitionYear, 2, 26),
            Venue = "Synthetic Aquatic Centre",
            Status = CarnivalStatus.Open,
            RuleVersion = "dev-seed-v1",
            OwnerSubject = SeedSubject,
            CreatedAtUtc = now,
        };
        db.Carnivals.Add(carnival);

        // Two event blocks either side of an item slot, so "Next event →" can walk off one block,
        // past an empty item, into the next.
        var stream = new ScheduleStream
        {
            Id = Guid.CreateVersion7(),
            Carnival = carnival,
            Name = "Pool",
            SortOrder = 0,
        };
        db.ScheduleStreams.Add(stream);

        var freestyleSlot = new ScheduleSlot
        {
            Id = Guid.CreateVersion7(),
            Stream = stream,
            SortOrder = 0,
            StartTime = new TimeOnly(9, 45),
            Kind = ScheduleSlotKind.Events,
            Label = "FREESTYLE EVENTS",
        };
        var noveltySlot = new ScheduleSlot
        {
            Id = Guid.CreateVersion7(),
            Stream = stream,
            SortOrder = 1,
            StartTime = new TimeOnly(11, 0),
            Kind = ScheduleSlotKind.Item,
            Label = "Novelty — staff v student relay",
        };
        var socialSlot = new ScheduleSlot
        {
            Id = Guid.CreateVersion7(),
            Stream = stream,
            SortOrder = 2,
            StartTime = new TimeOnly(11, 30),
            Kind = ScheduleSlotKind.Events,
            Label = "SOCIAL EVENTS",
        };
        db.ScheduleSlots.AddRange(freestyleSlot, noveltySlot, socialSlot);

        var divisions = await db.RosterSnapshots.AsNoTracking()
            .Where(s => s.IsCurrent
                && (s.CompetitionCategory == CompetitionCategory.M
                    || s.CompetitionCategory == CompetitionCategory.F))
            .GroupBy(s => new { s.CompetitionAgeGroup, s.CompetitionCategory })
            .Select(g => new { g.Key.CompetitionAgeGroup, g.Key.CompetitionCategory, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.CompetitionAgeGroup)
            .Take(4)
            .ToListAsync(ct);
        var slotOrder = 0;
        foreach (var division in divisions)
        {
            var carnivalEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                Carnival = carnival,
                EventDefinition = definition,
                AgeGroup = division.CompetitionAgeGroup,
                Category = division.CompetitionCategory,
                Status = CarnivalEventStatus.Open,
                ScheduleSlot = freestyleSlot,
                SlotOrder = slotOrder++,
            };
            db.CarnivalEvents.Add(carnivalEvent);
        }

        // A Social event exercises per-entry scoring,
        // easy to break unnoticed when every event ranks.
        var socialEvents = 0;
        if (divisions.Count > 0)
        {
            var socialEvent = new CarnivalEvent
            {
                Id = Guid.CreateVersion7(),
                Carnival = carnival,
                EventDefinition = definition,
                AgeGroup = AgeScope.OpenKey,
                Category = divisions[0].CompetitionCategory,
                Tier = EventTier.Social,
                Status = CarnivalEventStatus.Open,
                ScheduleSlot = socialSlot,
                SlotOrder = 0,
            };
            db.CarnivalEvents.Add(socialEvent);
            socialEvents = 1;
        }
        var eventCount = divisions.Count + socialEvents;

        var audit = services.GetRequiredService<AuditService>();
        audit.Record(actor, AuditActions.DevSeed, nameof(Carnival), carnival.Id.ToString(),
            newValues: new
            {
                carnival.Name,
                status = carnival.Status.ToString(),
                events = eventCount,
                socialEvents,
                eventDefinition = definition.Code,
                program = new { stream = stream.Name, slots = 3 },
            });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded development database: {Students} synthetic students, carnival '{Carnival}' with {Events} events ({Social} Social-tier).",
            await db.Students.CountAsync(ct), carnival.Name, eventCount, socialEvents);
    }

    // Re-emitted as CSV so the subset still flows through the real intake parser and validators.
    internal static string BuildActiveSubset(byte[] sourceCsv, IReadOnlyCollection<string> houses, ImportMapping mapping)
    {
        string[] columns = ["STKEY", "SURNAME", "FIRST_NAME", "PREF_NAME", "GENDER",
            "BIRTHDATE", "STATUS", "HOME_GROUP", "SCHOOL_YEAR", "HOUSE"];
        var parsed = CsvTable.Parse(CsvTable.Decode(sourceCsv));
        var indices = columns.Select(parsed.IndexOf).ToArray();
        if (indices.Any(i => i < 0))
            throw new InvalidOperationException("Synthetic roster file is missing an expected CASES21 column.");
        var houseSet = new HashSet<string>(houses, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columns));
        var taken = 0;
        foreach (var row in parsed.Rows)
        {
            var values = indices.Select(i => row[i]).ToArray();
            var (stkey, surname, firstName, gender, birthdate, status, schoolYear, house) =
                (values[0], values[1], values[2], values[4], values[5], values[6], values[8], values[9]);
            var clean =
                !string.IsNullOrWhiteSpace(stkey)
                && !string.IsNullOrWhiteSpace(surname)
                && !string.IsNullOrWhiteSpace(firstName)
                && string.Equals(status, "ACTV", StringComparison.OrdinalIgnoreCase)
                && houseSet.Contains(house)
                && int.TryParse(schoolYear, out var year)
                && year is >= RosterImportService.MinYearLevel and <= RosterImportService.MaxYearLevel
                && CompetitionDerivation.ParseBirthdate(birthdate) is not null
                && CompetitionDerivation.DeriveCategory(gender) is not null;
            if (!clean)
                continue;
            sb.AppendLine(string.Join(',', values.Select(Escape)));
            if (++taken >= TargetStudents)
                break;
        }
        if (taken == 0)
            throw new InvalidOperationException("Synthetic roster file contains no promotable ACTV rows.");
        return sb.ToString();

        static string Escape(string value) =>
            value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
    }

    private static string Resolve(IHostEnvironment env, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
}
