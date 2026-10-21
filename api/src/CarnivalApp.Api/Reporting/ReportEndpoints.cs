using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using Microsoft.AspNetCore.RateLimiting;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Scoring;

namespace CarnivalApp.Api.Reporting;

/// <summary>
/// Computed on read in every carnival status. Each CSV writes one audit row — the evidence that a
/// file of student names left the platform; the screen read is not audited.
/// </summary>
public static class ReportEndpoints
{
    private const string CsvContentType = "text/csv; charset=utf-8";

    private static readonly string[] StandingsHeader =
    [
        "Rank", "House", "Placement points", "Participation points", "Bonus points", "Total",
    ];

    public static void MapReportEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var reports = apiV1.MapGroup("/carnivals/{id:guid}/reports")
            .RequireAuthorization(CarnivalAuthorization.AdministerCarnivalsPolicy);

        reports.MapGet("/results", GetResultsAsync)
            .Produces<ResultsBook.ResultsReport>();
        reports.MapGet("/standings.csv", ExportStandingsAsync)
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces(StatusCodes.Status200OK, contentType: CsvContentType);
        reports.MapGet("/results.csv", ExportResultsAsync)
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces(StatusCodes.Status200OK, contentType: CsvContentType);
        reports.MapGet("/district.csv", ExportDistrictAsync)
            .RequireRateLimiting(RateLimiting.BulkDataPolicy)
            .Produces(StatusCodes.Status200OK, contentType: CsvContentType);
    }

    private static async Task<IResult> GetResultsAsync(
        Guid id, [FromServices] SportsDbContext db, CancellationToken ct)
    {
        var report = await ResultsBook.BuildAsync(db, id, ct);
        return report is null ? CarnivalGuards.CarnivalNotFound() : Results.Ok(report);
    }

    private static async Task<IResult> ExportStandingsAsync(
        Guid id, HttpContext ctx, [FromServices] SportsDbContext db,
        [FromServices] ScoringService scoring, [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        var standings = await scoring.ComputeAsync(id, ct);
        if (standings is null) return CarnivalGuards.CarnivalNotFound();

        var rows = standings.Houses.Select(house => new string?[]
        {
            house.Rank.ToString(CultureInfo.InvariantCulture),
            house.HouseCode,
            house.PlacementPoints.ToString(CultureInfo.InvariantCulture),
            house.ParticipationPoints.ToString(CultureInfo.InvariantCulture),
            house.BonusPoints.ToString(CultureInfo.InvariantCulture),
            house.Total.ToString(CultureInfo.InvariantCulture),
        });

        var file = Csv.Build(StandingsHeader, rows);
        await RecordExportAsync(ctx, db, audit, carnival, "standings", standings.Houses.Count, ct);
        return Results.File(file, CsvContentType, FileName("standings", carnival));
    }

    private static async Task<IResult> ExportResultsAsync(
        Guid id, HttpContext ctx, [FromServices] SportsDbContext db,
        [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        var report = await ResultsBook.BuildAsync(db, carnival, ct);

        var file = Csv.Build(ResultsBook.CsvHeader, report.Rows.Select(ResultsBook.CsvRow));
        await RecordExportAsync(ctx, db, audit, carnival, "results", report.Rows.Count, ct);
        return Results.File(file, CsvContentType, FileName("results", carnival));
    }

    // The only gated export: it leaves the school, and a
    // selection process cannot see a later correction.
    private static async Task<IResult> ExportDistrictAsync(
        Guid id, HttpContext ctx, [FromServices] SportsDbContext db,
        [FromServices] AuditService audit, CancellationToken ct)
    {
        var carnival = await db.Carnivals.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (carnival is null) return CarnivalGuards.CarnivalNotFound();
        if (carnival.Status != CarnivalStatus.Finalised)
            return ApiError.Conflict(ErrorCodes.CarnivalNotFinalised,
                "The district list is produced from published results. Finalise the carnival " +
                "to produce the district list.");

        var rows = await DistrictSelection.BuildAsync(db, id, ct);
        var file = Csv.Build(DistrictSelection.CsvHeader, rows.Select(DistrictSelection.CsvRow));
        await RecordExportAsync(ctx, db, audit, carnival, "district", rows.Count, ct);
        return Results.File(file, CsvContentType, FileName("district", carnival));
    }

    private static async Task RecordExportAsync(
        HttpContext ctx, SportsDbContext db, AuditService audit, Carnival carnival,
        string report, int rows, CancellationToken ct)
    {
        audit.Record(SessionActor.From(ctx), AuditActions.ReportExport,
            nameof(Carnival), carnival.Id.ToString(),
            newValues: new { report, format = "csv", rows },
            ruleVersion: carnival.RuleVersion);
        await db.SaveChangesAsync(ct);
    }

    private static string FileName(string report, Carnival carnival) =>
        // Invariant: a non-Gregorian CurrentCulture calendar would date the file in another era.
        $"{report}-{Slug(carnival.Name)}-{carnival.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv";

    private static string Slug(string name)
    {
        var slug = string.Concat(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? "carnival" : slug;
    }
}
