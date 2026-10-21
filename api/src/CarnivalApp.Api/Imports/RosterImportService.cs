using System.Security.Cryptography;
using System.Text.Json;
using CarnivalApp.Api.Auditing;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Imports;

/// <summary>
/// Only ACTV rows promote; others stay inactive identity references with no house validation
/// (legacy codes exist only on departed students). The birthdate is never stored.
/// </summary>
public class RosterImportService(SportsDbContext db, AuditService audit)
{
    public const int MinYearLevel = 7;
    public const int MaxYearLevel = 12;

    private static readonly HashSet<string> ActiveStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "ACTV" };

    private static readonly HashSet<string> InactiveStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "LEFT", "DEL", "INAC", "FUT", "LVNG" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // StagedRowsJson keeps JsonOptions: its PascalCase blobs are already stored.
    private static readonly JsonSerializerOptions ValidationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public sealed record StageOutcome(ImportBatch Batch, StagedBatchDocument Document, List<string> StructuralErrors);

    // Nothing touches Students or RosterSnapshots until promotion.
    public async Task<StageOutcome> StageAsync(
        Stream csvStream,
        string fileName,
        ImportMapping mapping,
        int competitionYear,
        IReadOnlyCollection<string> configuredHouses,
        AuditActor actor,
        CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var structuralErrors = new List<string>();
        var document = new StagedBatchDocument
        {
            CompetitionYear = competitionYear,
            CutOff = mapping.AgeGroupRule.CutOff,
        };

        try
        {
            var parsed = CsvTable.Parse(CsvTable.Decode(bytes));
            structuralErrors.AddRange(CheckRequiredColumns(parsed, mapping));
            if (structuralErrors.Count == 0)
            {
                document.Rows = MapAndValidate(parsed, mapping, competitionYear, configuredHouses);
                if (document.Rows.Count == 0)
                    structuralErrors.Add("File contains no data rows.");
                else
                    await ReconcileAsync(document, ct);
            }
        }
        catch (InvalidDataException ex)
        {
            structuralErrors.Add(ex.Message);
        }

        var summary = Summarise(document);
        var batch = new ImportBatch
        {
            SourceFileName = fileName,
            SourceFileHash = hash,
            MappingVersion = mapping.Version,
            ImportedBySubject = actor.Subject,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            Status = structuralErrors.Count > 0 ? ImportBatchStatus.ValidationFailed : ImportBatchStatus.Validated,
            NewCount = summary.New,
            ChangedCount = summary.Changed,
            InactiveCount = summary.Inactive,
            UnmatchedCount = summary.Unmatched,
            RejectedCount = summary.Rejected,
            ValidationResultsJson = JsonSerializer.Serialize(new ValidationDocument(
                structuralErrors,
                [.. document.Rows
                    .Where(r => r.Issues.Count > 0)
                    .Select(r => new RowValidation(r.RowNumber, r.ExternalStudentId, r.Issues))]),
                ValidationJsonOptions),
            StagedRowsJson = JsonSerializer.Serialize(document, JsonOptions),
        };
        db.ImportBatches.Add(batch);
        audit.Record(actor, AuditActions.ImportStage, nameof(ImportBatch), batch.Id.ToString(),
            newValues: new
            {
                batch.SourceFileName,
                batch.SourceFileHash,
                batch.MappingVersion,
                status = batch.Status.ToString(),
                competitionYear,
                summary,
            },
            importBatchId: batch.Id);
        await db.SaveChangesAsync(ct);
        return new StageOutcome(batch, document, structuralErrors);
    }

    private static IEnumerable<string> CheckRequiredColumns(CsvTable.Parsed parsed, ImportMapping mapping) =>
        mapping.Columns.Values
            .Where(c => c.Required && parsed.IndexOf(c.SourceColumn) < 0)
            .Select(c => $"Required column '{c.SourceColumn}' is missing from the file.");

    public static List<StagedRow> MapAndValidate(
        CsvTable.Parsed parsed, ImportMapping mapping, int competitionYear,
        IReadOnlyCollection<string> configuredHouses)
    {
        var houses = new HashSet<string>(configuredHouses, StringComparer.OrdinalIgnoreCase);
        int Col(string name) => parsed.IndexOf(name);
        int stkey = Col("STKEY"), surname = Col("SURNAME"), first = Col("FIRST_NAME"),
            pref = Col("PREF_NAME"), gender = Col("GENDER"), house = Col("HOUSE"),
            status = Col("STATUS"), birth = Col("BIRTHDATE"), homeGroup = Col("HOME_GROUP"),
            year = Col("SCHOOL_YEAR");

        string Get(string[] row, int idx) => idx >= 0 && idx < row.Length ? row[idx] : string.Empty;

        var rows = new List<StagedRow>();
        var byKey = new Dictionary<string, List<StagedRow>>(StringComparer.OrdinalIgnoreCase);

        var rowNumber = 1; // header is row 1
        foreach (var raw in parsed.Rows)
        {
            rowNumber++;
            var row = new StagedRow
            {
                RowNumber = rowNumber,
                ExternalStudentId = NullIfEmpty(Get(raw, stkey)),
                Surname = NullIfEmpty(Get(raw, surname)),
                FirstName = NullIfEmpty(Get(raw, first)),
                PreferredName = NullIfEmpty(Get(raw, pref)),
                EnrolmentStatus = NullIfEmpty(Get(raw, status))?.ToUpperInvariant(),
                HouseCode = NullIfEmpty(Get(raw, house)),
                HomeGroup = NullIfEmpty(Get(raw, homeGroup)),
            };
            rows.Add(row);

            void Fail(string code, string message, IssueSeverity severity = IssueSeverity.Error)
                => row.Issues.Add(new RowIssue(code, severity, message));

            if (row.ExternalStudentId is null)
                Fail("MISSING_STKEY", "STKEY is required.");
            else
            {
                if (!byKey.TryGetValue(row.ExternalStudentId, out var list))
                    byKey[row.ExternalStudentId] = list = [];
                list.Add(row);
            }
            if (row.Surname is null) Fail("MISSING_SURNAME", "SURNAME is required.");
            if (row.FirstName is null) Fail("MISSING_FIRST_NAME", "FIRST_NAME is required.");

            var isActive = row.EnrolmentStatus is not null && ActiveStatuses.Contains(row.EnrolmentStatus);
            if (row.EnrolmentStatus is null)
                Fail("MISSING_STATUS", "STATUS is required.");
            else if (!isActive && !InactiveStatuses.Contains(row.EnrolmentStatus))
                Fail("UNKNOWN_STATUS", $"Unrecognised enrolment status '{row.EnrolmentStatus}'.");

            if (isActive)
            {
                var yearRaw = Get(raw, year);
                if (int.TryParse(yearRaw, out var yearLevel) && yearLevel is >= MinYearLevel and <= MaxYearLevel)
                    row.YearLevel = yearLevel;
                else
                    Fail("INVALID_YEAR_LEVEL",
                        $"SCHOOL_YEAR '{yearRaw}' is not a year level between {MinYearLevel:00} and {MaxYearLevel:00}.");

                if (row.HouseCode is null)
                    Fail("MISSING_HOUSE",
                        "Active student has no house assigned. Resolve the house in the source system and re-import; this row is excluded from promotion.",
                        IssueSeverity.Exception);
                else if (!houses.Contains(row.HouseCode))
                    Fail("UNKNOWN_HOUSE", $"House '{row.HouseCode}' is not a configured house.");

                var birthRaw = Get(raw, birth);
                if (string.IsNullOrWhiteSpace(birthRaw))
                    Fail("MISSING_BIRTHDATE", "BIRTHDATE is required for active students (used transiently to derive the competition age group; not stored).");
                else if (CompetitionDerivation.ParseBirthdate(birthRaw) is DateOnly birthdate)
                {
                    if (birthdate > mapping.AgeGroupRule.CutOffIn(competitionYear))
                        Fail("BIRTHDATE_AFTER_CUT_OFF",
                            $"BIRTHDATE falls after the {competitionYear} competition cut-off, so no age can be derived. Check the date in the source system or the competition year chosen at upload.");
                    else
                        row.CompetitionAgeGroup =
                            mapping.AgeGroupRule.DeriveAgeGroup(birthdate, competitionYear);
                }
                else
                    // Never echo the value: issues are persisted and served back.
                    Fail("INVALID_BIRTHDATE", "BIRTHDATE is not a recognised date (Excel serial, d/m/Y, or ISO).");

                var genderRaw = Get(raw, gender);
                if (CompetitionDerivation.DeriveCategory(genderRaw) is CompetitionCategory category)
                    row.CompetitionCategory = category;
                else
                    Fail("MISSING_GENDER", "GENDER is required for active students (derives the default competition category).");
            }

            row.Disposition =
                row.Issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Exception) ? RowDisposition.Rejected
                : isActive ? RowDisposition.New // refined during reconciliation
                : RowDisposition.Inactive;
        }

        // A repeated STKEY means corrupt source data: every
        // affected row is an error, the batch is not.
        foreach (var dupes in byKey.Values.Where(l => l.Count > 1))
            foreach (var dupe in dupes)
            {
                dupe.Issues.Add(new RowIssue("DUPLICATE_STKEY", IssueSeverity.Error,
                    $"STKEY '{dupe.ExternalStudentId}' appears {dupes.Count} times in the file; STKEY must be unique. Investigate the source export."));
                dupe.Disposition = RowDisposition.Rejected;
            }

        return rows;
    }

    private async Task ReconcileAsync(StagedBatchDocument document, CancellationToken ct)
    {
        var keys = document.Rows
            .Where(r => r.ExternalStudentId is not null)
            .Select(r => r.ExternalStudentId!)
            .Distinct()
            .ToList();

        var existing = await db.Students
            .Where(s => keys.Contains(s.ExternalStudentId))
            .Include(s => s.RosterSnapshots.Where(rs => rs.IsCurrent))
            .ToDictionaryAsync(s => s.ExternalStudentId, StringComparer.OrdinalIgnoreCase, ct);

        foreach (var row in document.Rows.Where(r =>
                     r.Disposition == RowDisposition.New && r.ExternalStudentId is not null))
        {
            if (!existing.TryGetValue(row.ExternalStudentId!, out var student))
                continue;
            var current = student.RosterSnapshots.FirstOrDefault();
            var unchanged =
                student.Surname == row.Surname &&
                student.FirstName == row.FirstName &&
                student.PreferredName == row.PreferredName &&
                student.IsActive &&
                current is not null &&
                current.YearLevel == row.YearLevel &&
                string.Equals(current.HouseCode, row.HouseCode, StringComparison.OrdinalIgnoreCase) &&
                current.CompetitionAgeGroup == row.CompetitionAgeGroup &&
                current.CompetitionCategory == row.CompetitionCategory &&
                current.HomeGroup == row.HomeGroup &&
                current.SchoolYear == document.CompetitionYear;
            row.Disposition = unchanged ? RowDisposition.Unchanged : RowDisposition.Changed;
        }

        // Existing active students absent from the file — report-only.
        var fileKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        document.UnmatchedStudents = await db.Students
            .Where(s => s.IsActive && !fileKeys.Contains(s.ExternalStudentId))
            .OrderBy(s => s.ExternalStudentId)
            .Select(s => new UnmatchedStudent(s.ExternalStudentId, s.Surname, s.FirstName))
            .ToListAsync(ct);
    }

    public static ReconciliationSummary Summarise(StagedBatchDocument document) => new(
        document.Rows.Count(r => r.Disposition == RowDisposition.New),
        document.Rows.Count(r => r.Disposition == RowDisposition.Changed),
        document.Rows.Count(r => r.Disposition == RowDisposition.Unchanged),
        document.Rows.Count(r => r.Disposition == RowDisposition.Inactive),
        document.UnmatchedStudents.Count,
        document.Rows.Count(r => r.Disposition == RowDisposition.Rejected));

    public async Task<ImportBatch> ApproveAsync(
        Guid batchId, bool approve, string? note, AuditActor actor, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new ImportPipelineException(404, ErrorCodes.ImportBatchNotFound, "Import batch not found.");
        if (batch.Status != ImportBatchStatus.Validated)
            throw new ImportPipelineException(409, ErrorCodes.ImportBatchWrongState,
                $"Batch is {batch.Status}; only Validated batches can be reviewed.");
        // Rejecting an expired batch only records the decision; its rows are already gone.
        if (approve && batch.StagedRowsJson is null) throw Expired();

        var priorStatus = batch.Status;
        batch.Status = approve ? ImportBatchStatus.Approved : ImportBatchStatus.Rejected;
        batch.ReviewedBySubject = actor.Subject;
        batch.ReviewedAtUtc = DateTimeOffset.UtcNow;
        batch.ReviewNote = note;
        if (!approve)
        {
            batch.StagedRowsJson = null;
            batch.ValidationResultsJson = null;
        }
        audit.Record(actor, approve ? AuditActions.ImportApprove : AuditActions.ImportReject,
            nameof(ImportBatch), batch.Id.ToString(),
            priorValues: new { status = priorStatus.ToString() },
            newValues: new { status = batch.Status.ToString() },
            reason: note,
            importBatchId: batch.Id);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<ImportBatch> PromoteAsync(Guid batchId, AuditActor actor, CancellationToken ct = default)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == batchId, ct)
            ?? throw new ImportPipelineException(404, ErrorCodes.ImportBatchNotFound, "Import batch not found.");
        if (batch.Status != ImportBatchStatus.Approved)
            throw new ImportPipelineException(409, ErrorCodes.ImportBatchWrongState,
                $"Batch is {batch.Status}; only Approved batches can be promoted.");
        if (batch.StagedRowsJson is null) throw Expired();
        var document = GetDocument(batch);

        // No execution strategy is configured. If retries are ever enabled, clear the ChangeTracker
        // and re-read the batch per attempt, or the promotion double-writes.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var keys = document.Rows
            .Where(r => !r.IsRejected && r.ExternalStudentId is not null)
            .Select(r => r.ExternalStudentId!)
            .ToList();
        var students = await db.Students
            .Where(s => keys.Contains(s.ExternalStudentId))
            .Include(s => s.RosterSnapshots.Where(rs => rs.IsCurrent))
            .ToDictionaryAsync(s => s.ExternalStudentId, StringComparer.OrdinalIgnoreCase, ct);
        var now = DateTimeOffset.UtcNow;
        var effectiveDate = DateOnly.FromDateTime(now.UtcDateTime);

        foreach (var row in document.Rows.Where(r => !r.IsRejected))
        {
            var isActive = row.Disposition != RowDisposition.Inactive;
            if (!students.TryGetValue(row.ExternalStudentId!, out var student))
            {
                student = new Student
                {
                    ExternalStudentId = row.ExternalStudentId!,
                    Surname = row.Surname!,
                    FirstName = row.FirstName!,
                    PreferredName = row.PreferredName,
                    IsActive = isActive,
                    CreatedAtUtc = now,
                };
                db.Students.Add(student);
                students[student.ExternalStudentId] = student;
            }
            else
            {
                student.Surname = row.Surname!;
                student.FirstName = row.FirstName!;
                student.PreferredName = row.PreferredName;
                student.IsActive = isActive;
                student.UpdatedAtUtc = now;
            }

            if (!isActive)
                continue; // inactive identity reference only — no snapshot.

            foreach (var previous in student.RosterSnapshots.Where(s => s.IsCurrent))
                previous.IsCurrent = false;
            db.RosterSnapshots.Add(new RosterSnapshot
            {
                Student = student,
                ImportBatch = batch,
                SchoolYear = document.CompetitionYear,
                YearLevel = row.YearLevel!.Value,
                HouseCode = row.HouseCode!,
                CompetitionAgeGroup = row.CompetitionAgeGroup!,
                CompetitionCategory = row.CompetitionCategory!.Value,
                HomeGroup = row.HomeGroup,
                EffectiveDate = effectiveDate,
                IsCurrent = true,
            });
        }

        var priorStatus = ImportBatchStatus.Approved;
        batch.Status = ImportBatchStatus.Promoted;
        batch.PromotedAtUtc = now;
        // A second roster copy; dropped inside the transaction so a rolled-back promotion keeps it.
        batch.StagedRowsJson = null;
        batch.ValidationResultsJson = null;
        audit.Record(actor, AuditActions.ImportPromote, nameof(ImportBatch), batch.Id.ToString(),
            priorValues: new { status = priorStatus.ToString() },
            newValues: new
            {
                status = batch.Status.ToString(),
                promoted = Summarise(document),
                competitionYear = document.CompetitionYear,
            },
            importBatchId: batch.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch;
    }

    public static readonly TimeSpan UnpromotedRetention = TimeSpan.FromDays(7);

    public const string ExpiredDetail =
        "This batch expired: a batch that is not promoted within 7 days of upload has its staged rows "
        + "discarded. Upload the file again.";

    // Intake always stores rows, so an unpromoted, unrejected batch without them was purged by age.
    private static ImportPipelineException Expired() =>
        new(409, ErrorCodes.ImportBatchExpired, ExpiredDetail);

    // Callers answer a batch without staged rows (expired or purged) before reading it.
    public static StagedBatchDocument GetDocument(ImportBatch batch) =>
        JsonSerializer.Deserialize<StagedBatchDocument>(
            batch.StagedRowsJson ?? throw new InvalidOperationException("Batch has no staged rows."), JsonOptions)
        ?? throw new ImportPipelineException(500, ErrorCodes.ImportBatchUnreadable, "Staged rows are unreadable.");

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public class ImportPipelineException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}
