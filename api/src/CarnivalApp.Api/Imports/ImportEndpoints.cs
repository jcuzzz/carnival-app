using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;
using CarnivalApp.Api.School;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;

namespace CarnivalApp.Api.Imports;

public static class RosterUpload
{
    /// <summary>A CASES21 whole-school export is a few hundred KB.</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Sized with <c>ImportBatch.SourceFileName</c>.</summary>
    public const int MaxFileNameLength = 260;

    private static readonly string[] AllowedContentTypes =
        ["text/csv", "text/plain", "application/csv", "application/vnd.ms-excel"];

    // Cut on both separators: Path.GetFileName is platform-dependent, so a Windows path would
    // survive intact on the Linux deployment.
    public static string BaseName(string? fileName)
    {
        var value = (fileName ?? "").Trim();
        var cut = value.LastIndexOfAny(['/', '\\']);
        value = cut < 0 ? value : value[(cut + 1)..];
        return value.Length > MaxFileNameLength ? value[..MaxFileNameLength] : value;
    }

    // An absent content type passes: multipart parts
    // often carry none, and the extension is the gate.
    public static bool IsCsv(string fileName, string? contentType)
    {
        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        var mediaType = contentType.Split(';')[0].Trim();
        return AllowedContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }
}

public static class ImportAuthorization
{
    public const string ManageImportsPolicy = "imports:manage";
    public const string ViewImportsPolicy = "imports:view";

    public static readonly IReadOnlyList<PlatformRole> ManageImportsRoles =
        [PlatformRole.SystemAdministrator];

    /// <summary>
    /// Also SystemAdministrator-only: reconciliation relays staged roster rows verbatim, and
    /// imports are where real student data lands.
    /// </summary>
    public static readonly IReadOnlyList<PlatformRole> ViewImportsRoles =
        [PlatformRole.SystemAdministrator];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<PlatformRole>> PolicyRoles =
        new Dictionary<string, IReadOnlyList<PlatformRole>>
        {
            [ManageImportsPolicy] = ManageImportsRoles,
            [ViewImportsPolicy] = ViewImportsRoles,
        };

    /// <summary>Every import endpoint must appear here; the authorisation tests read it.</summary>
    public static readonly IReadOnlyDictionary<string, string> EndpointPolicies =
        new Dictionary<string, string>
        {
            ["POST /api/v1/imports"] = ManageImportsPolicy,
            ["GET /api/v1/imports"] = ViewImportsPolicy,
            ["GET /api/v1/imports/defaults"] = ViewImportsPolicy,
            ["GET /api/v1/imports/{id}/validation"] = ViewImportsPolicy,
            ["GET /api/v1/imports/{id}/reconciliation"] = ViewImportsPolicy,
            ["POST /api/v1/imports/{id}/approve"] = ManageImportsPolicy,
            ["POST /api/v1/imports/{id}/promote"] = ManageImportsPolicy,
        };
}

/// <summary>Lets the authorisation tests resolve each endpoint's policy.</summary>
public sealed record ImportPolicyMetadata(string PolicyName);

public static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var group = apiV1.MapGroup("/imports");

        group.MapPost("/", StageAsync)
            .RequireAuthorization(ImportAuthorization.ManageImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ManageImportsPolicy))
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces<StageResponse>(StatusCodes.Status201Created);
        group.MapGet("/", ListAsync)
            .RequireAuthorization(ImportAuthorization.ViewImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ViewImportsPolicy))
            .Produces<IReadOnlyList<ImportBatchSummary>>();
        group.MapGet("/defaults", GetDefaultsAsync)
            .RequireAuthorization(ImportAuthorization.ViewImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ViewImportsPolicy))
            .Produces<ImportDefaults>();
        group.MapGet("/{id:guid}/validation", GetValidationAsync)
            .RequireAuthorization(ImportAuthorization.ViewImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ViewImportsPolicy))
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces<ValidationReport>();
        group.MapGet("/{id:guid}/reconciliation", GetReconciliationAsync)
            .RequireAuthorization(ImportAuthorization.ViewImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ViewImportsPolicy))
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces<ReconciliationReport>();
        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(ImportAuthorization.ManageImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ManageImportsPolicy))
            .Produces<BatchStatusResponse>();
        group.MapPost("/{id:guid}/promote", PromoteAsync)
            .RequireAuthorization(ImportAuthorization.ManageImportsPolicy)
            .WithMetadata(new ImportPolicyMetadata(ImportAuthorization.ManageImportsPolicy))
            .Produces<PromoteResponse>();
        return group;
    }

    public sealed record StageResponse(
        Guid Id, ImportBatchStatus Status, int CompetitionYear,
        IReadOnlyList<string> StructuralErrors, ReconciliationSummary Summary);

    public sealed record ImportBatchSummary(
        Guid Id, string SourceFileName, ImportBatchStatus Status,
        int NewCount, int ChangedCount, int InactiveCount, int UnmatchedCount, int RejectedCount,
        DateTimeOffset ImportedAtUtc, string ImportedBySubject);

    /// <summary>
    /// <see cref="Validation"/> is null when no document was stored or its blob is unreadable.
    /// </summary>
    public sealed record ValidationReport(
        Guid Id, ImportBatchStatus Status, string SourceFileName,
        string? SourceFileHash, string? MappingVersion, ValidationDocument? Validation);

    /// <summary>CutOff is MM-DD: ages are as at that day of the competition year.</summary>
    public sealed record ReconciliationReport(
        Guid Id, ImportBatchStatus Status, int CompetitionYear, string CutOff,
        ReconciliationSummary Summary,
        IReadOnlyList<StagedRow> Rows, IReadOnlyList<UnmatchedStudent> UnmatchedStudents);

    /// <summary>
    /// What an upload that names no year would use, and the years it may name instead. CutOff is
    /// MM-DD.
    /// </summary>
    public sealed record ImportDefaults(
        int CompetitionYear, int EarliestYear, int LatestYear, string CutOff);

    public sealed record BatchStatusResponse(Guid Id, ImportBatchStatus Status);

    public sealed record PromoteResponse(Guid Id, ImportBatchStatus Status, DateTimeOffset? PromotedAtUtc);

    private static IResult BatchNotFound() =>
        ApiError.NotFound(ErrorCodes.ImportBatchNotFound, "No import batch exists with that id.");

    private sealed record MappingLoad(ImportMapping? Mapping, string Problem);

    private static MappingLoad LoadMapping(IConfiguration config)
    {
        if (config["Imports:MappingPath"] is not { } path || !File.Exists(path))
            return new(null, "Imports:MappingPath is not configured or the mapping file does not exist.");
        try
        {
            return new(ImportMapping.LoadFromFile(path), "");
        }
        catch (InvalidDataException ex)
        {
            return new(null, ex.Message);
        }
        catch (JsonException)
        {
            return new(null, "The import mapping file is not valid JSON.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, "The import mapping file could not be read.");
        }
    }

    private static IResult MappingUnavailable(MappingLoad load) =>
        ApiError.Internal(ErrorCodes.ImportMappingUnavailable, load.Problem);

    private static async Task<IResult> GetDefaultsAsync(
        IConfiguration config, [FromServices] SportsDbContext db, CancellationToken ct)
    {
        var load = LoadMapping(config);
        if (load.Mapping is not { } mapping) return MappingUnavailable(load);
        var today = CompetitionYears.SchoolToday(config, DateTimeOffset.UtcNow);
        return Results.Ok(new ImportDefaults(
            await CompetitionYears.DefaultAsync(db, today, ct),
            CompetitionYears.Earliest(today), CompetitionYears.Latest(today),
            mapping.AgeGroupRule.CutOff));
    }

    private static async Task<IResult> StageAsync(
        HttpContext ctx, [FromServices] RosterImportService service, IConfiguration config,
        [FromServices] SchoolSettingsProvider school, [FromServices] SportsDbContext db,
        CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType)
            return ApiError.BadRequest(ErrorCodes.ImportFileRequired,
                "Send the roster export as multipart/form-data with a 'file' part.");
        // Set before the body is read so Kestrel refuses mid-read.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = RosterUpload.MaxBytes;
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
            return ApiError.BadRequest(ErrorCodes.ImportFileRequired, "A non-empty 'file' part is required.");
        if (file.Length > RosterUpload.MaxBytes)
            return ApiError.Status(StatusCodes.Status413PayloadTooLarge, ErrorCodes.RequestBodyTooLarge,
                $"A roster export must be at most {RosterUpload.MaxBytes / (1024 * 1024)} MB.");

        // Rendered client-side, so only the basename is kept; anything else is a crafted header.
        var sourceFileName = RosterUpload.BaseName(file.FileName);
        if (!RosterUpload.IsCsv(sourceFileName, file.ContentType))
            return ApiError.BadRequest(ErrorCodes.ImportFileTypeUnsupported,
                "The roster export must be a .csv file sent as CSV or plain text.");

        var today = CompetitionYears.SchoolToday(config, DateTimeOffset.UtcNow);
        int competitionYear;
        var declared = form["competitionYear"].ToString().Trim();
        if (declared.Length == 0)
            competitionYear = await CompetitionYears.DefaultAsync(db, today, ct);
        else if (!int.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture,
                     out competitionYear)
                 || !CompetitionYears.InRange(competitionYear, today))
            return ApiError.BadRequest(ErrorCodes.ImportCompetitionYearInvalid,
                $"competitionYear must be a year from {CompetitionYears.Earliest(today)} to " +
                $"{CompetitionYears.Latest(today)}.",
                new()
                {
                    ["earliestYear"] = CompetitionYears.Earliest(today),
                    ["latestYear"] = CompetitionYears.Latest(today),
                });

        var load = LoadMapping(config);
        if (load.Mapping is not { } mapping) return MappingUnavailable(load);
        var houses = (await school.GetAsync(ct)).Houses.Select(house => house.Code).ToArray();

        await using var stream = file.OpenReadStream();
        var outcome = await service.StageAsync(
            stream, sourceFileName, mapping, competitionYear, houses, SessionActor.From(ctx), ct);
        var summary = RosterImportService.Summarise(outcome.Document);
        return Results.Created($"/api/v1/imports/{outcome.Batch.Id}", new StageResponse(
            outcome.Batch.Id, outcome.Batch.Status, competitionYear, outcome.StructuralErrors,
            summary));
    }

    private static async Task<IResult> ListAsync([FromServices] SportsDbContext db, CancellationToken ct)
    {
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        var batches = await db.ImportBatches.AsNoTracking().ToListAsync(ct);
        return Results.Ok(batches
            .OrderByDescending(b => b.ImportedAtUtc)
            .Select(b => new ImportBatchSummary(
                b.Id, b.SourceFileName, b.Status,
                b.NewCount, b.ChangedCount, b.InactiveCount, b.UnmatchedCount, b.RejectedCount,
                b.ImportedAtUtc, b.ImportedBySubject))
            .ToList());
    }

    private static async Task<IResult> GetValidationAsync(Guid id, [FromServices] SportsDbContext db, CancellationToken ct)
    {
        var batch = await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return BatchNotFound();
        return Results.Ok(new ValidationReport(
            batch.Id,
            batch.Status,
            batch.SourceFileName,
            batch.SourceFileHash,
            batch.MappingVersion,
            ReadValidation(batch.ValidationResultsJson)));
    }

    private static readonly JsonSerializerOptions ValidationReadOptions = new()
    {
        // Blobs stored before the document was typed are mixed-case.
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // An unparseable blob is no document, not a 500.
    private static ValidationDocument? ReadValidation(string? json)
    {
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ValidationDocument>(json, ValidationReadOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task<IResult> GetReconciliationAsync(Guid id, [FromServices] SportsDbContext db, CancellationToken ct)
    {
        var batch = await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null) return BatchNotFound();
        if (batch.StagedRowsJson is null)
            return batch.Status switch
            {
                ImportBatchStatus.Promoted => ApiError.Conflict(ErrorCodes.ImportRowsPurged,
                    "The staged roster was discarded when this batch was promoted (ADR-0032). "
                    + "The batch counts and the promotion audit record are the retained evidence."),
                ImportBatchStatus.Rejected => ApiError.Conflict(ErrorCodes.ImportRowsPurged,
                    "The staged roster was discarded when this batch was rejected. "
                    + "The batch counts and the review audit record are the retained evidence."),
                _ => ApiError.Conflict(ErrorCodes.ImportBatchExpired, RosterImportService.ExpiredDetail),
            };
        var document = RosterImportService.GetDocument(batch);
        return Results.Ok(new ReconciliationReport(
            batch.Id, batch.Status, document.CompetitionYear,
            // Every mapping before the cut-off was recorded shipped 31 December.
            document.CutOff ?? "12-31",
            RosterImportService.Summarise(document),
            document.Rows, document.UnmatchedStudents));
    }

    public sealed record ApproveRequest(bool Approve, string? Note);

    private static async Task<IResult> ApproveAsync(
        Guid id, ApproveRequest request, HttpContext ctx, [FromServices] RosterImportService service, CancellationToken ct)
    {
        try
        {
            var batch = await service.ApproveAsync(id, request.Approve, request.Note, SessionActor.From(ctx), ct);
            return Results.Ok(new BatchStatusResponse(batch.Id, batch.Status));
        }
        catch (ImportPipelineException ex)
        {
            return ApiError.Status(ex.StatusCode, ex.Code, ex.Message);
        }
    }

    private static async Task<IResult> PromoteAsync(
        Guid id, HttpContext ctx, [FromServices] RosterImportService service, CancellationToken ct)
    {
        try
        {
            var batch = await service.PromoteAsync(id, SessionActor.From(ctx), ct);
            return Results.Ok(new PromoteResponse(batch.Id, batch.Status, batch.PromotedAtUtc));
        }
        catch (ImportPipelineException ex)
        {
            return ApiError.Status(ex.StatusCode, ex.Code, ex.Message);
        }
    }
}
