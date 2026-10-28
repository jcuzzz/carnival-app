using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Auditing;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.School;

public sealed record SchoolSettingsResponse(
    string Name,
    string? BrandColour,
    IReadOnlyList<HouseSettingResponse> Houses,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// <c>InUse</c> is the PUT's removal guard read ahead, so the screen can disable Remove.
/// </summary>
public sealed record HouseSettingResponse(string Code, string Name, string? Colour, bool InUse);

/// <summary><c>name</c> defaults to the code.</summary>
public sealed record HouseSettingRequest(string? Code, string? Name, string? Colour);

/// <summary>
/// Full replacement; a save not carrying the current <c>UpdatedAtUtc</c> is refused.
/// </summary>
public sealed record SchoolSettingsRequest(
    string? Name,
    string? BrandColour,
    List<HouseSettingRequest>? Houses,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// Refuses a bad value rather than degrading it, because a person is present to fix it.
/// </summary>
public static class SchoolSettingsEndpoints
{
    public const int MaxNameLength = 200;
    public const int MaxHouseCodeLength = 32;
    public const int MaxHouseNameLength = 60;

    public static void MapSchoolSettingsEndpoints(this IEndpointRouteBuilder apiV1)
    {
        apiV1.MapGet("/school/settings", GetAsync)
            .RequireAuthorization(SchoolAuthorization.AdministerSchoolPolicy)
            .Produces<SchoolSettingsResponse>();
        apiV1.MapPut("/school/settings", UpdateAsync)
            .RequireAuthorization(SchoolAuthorization.AdministerSchoolPolicy)
            .Produces<SchoolSettingsResponse>();
    }

    private static SchoolSettingsResponse Project(
        SchoolSettings settings, IReadOnlySet<string> referenced) =>
        new(settings.Name,
            settings.BrandColour,
            [.. settings.Houses.Select(house => new HouseSettingResponse(
                house.Code, house.Name, house.Colour, referenced.Contains(house.Code)))],
            settings.UpdatedAtUtc);

    private static async Task<IResult> GetAsync(
        [FromServices] SchoolSettingsProvider provider,
        [FromServices] CarnivalAppDbContext db,
        CancellationToken ct) =>
        Results.Ok(Project(await provider.GetAsync(ct), await ReferencedCodesAsync(db, ct)));

    private static IResult Invalid(string code, string field, string detail) =>
        ApiError.Unprocessable(code, detail, new Dictionary<string, object?> { ["field"] = field });

    private static async Task<IResult> UpdateAsync(
        SchoolSettingsRequest request,
        HttpContext ctx,
        [FromServices] CarnivalAppDbContext db,
        [FromServices] AuditService audit,
        [FromServices] SchoolSettingsProvider provider,
        [FromServices] ILoggerFactory loggers,
        CancellationToken ct)
    {
        var row = await db.SchoolConfigurations.TheSchool().FirstOrDefaultAsync(ct);
        if (row is null)
            return ApiError.Internal(ErrorCodes.InternalError, "The school is not configured yet.");

        var current = SchoolSettings.From(row, loggers.CreateLogger(typeof(SchoolSettingsEndpoints).FullName!));
        if (request.UpdatedAtUtc != current.UpdatedAtUtc)
            return ApiError.Conflict(ErrorCodes.SettingsStale,
                "Another administrator saved these settings first. Reload the screen and reapply your changes.");

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0)
            return Invalid(ErrorCodes.NameRequired, "name", "The school name is required.");
        if (name.Length > MaxNameLength)
            return Invalid(ErrorCodes.NameTooLong, "name",
                $"The school name must be at most {MaxNameLength} characters.");

        var brandColour = string.IsNullOrWhiteSpace(request.BrandColour) ? null : request.BrandColour.Trim();
        if (brandColour is not null)
        {
            if (BrandColour.Normalise(brandColour) is not { } normalisedBrand)
                return Invalid(ErrorCodes.BrandColourInvalid, "brandColour",
                    $"'{brandColour}' is not a colour. Write it as #rrggbb or #rgb.");
            if (BrandColour.BestContrast(normalisedBrand) is not { } contrast
                || contrast < BrandColour.MinimumContrastRatio)
                return Invalid(ErrorCodes.BrandColourUnreadable, "brandColour",
                    $"'{brandColour}' reaches 4.5:1 against neither black nor white, so no readable ink "
                    + "can be written on it. Choose a darker or lighter colour.");
            brandColour = normalisedBrand;
        }

        if (request.Houses is not { Count: > 0 })
            return Invalid(ErrorCodes.HousesRequired, "houses", "At least one house is required.");

        var houses = new List<HouseMark>(request.Houses.Count);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colours = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Houses.Count; index++)
        {
            var house = request.Houses[index];
            // Codes are stored upper-case, so a case-only edit is the same house, not a rename.
            var code = (house.Code ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0)
                return Invalid(ErrorCodes.HouseCodeRequired, $"houses[{index}].code",
                    "Every house needs a code.");
            if (code.Length > MaxHouseCodeLength)
                return Invalid(ErrorCodes.HouseCodeTooLong, $"houses[{index}].code",
                    $"A house code must be at most {MaxHouseCodeLength} characters.");
            if (!codes.Add(code))
                return Invalid(ErrorCodes.HouseCodeDuplicate, $"houses[{index}].code",
                    $"'{code}' is listed twice; house codes must be unique.");

            var houseName = (house.Name ?? "").Trim();
            if (houseName.Length > MaxHouseNameLength)
                return Invalid(ErrorCodes.HouseNameTooLong, $"houses[{index}].name",
                    $"A house name must be at most {MaxHouseNameLength} characters.");
            if (houseName.Length == 0) houseName = code;

            string? colour = null;
            if (!string.IsNullOrWhiteSpace(house.Colour))
            {
                if (HouseColour.Normalise(house.Colour) is not { } normalised)
                    return Invalid(ErrorCodes.HouseColourInvalid, $"houses[{index}].colour",
                        $"'{house.Colour.Trim()}' is not a colour. Write it as #rrggbb or #rgb.");
                if (colours.TryGetValue(normalised, out var owner))
                    return Invalid(ErrorCodes.HouseColourDuplicate, $"houses[{index}].colour",
                        $"'{normalised}' is already used by {owner}; each house needs its own colour.");
                colours[normalised] = code;
                colour = normalised;
            }

            houses.Add(new HouseMark(code, houseName, colour));
        }

        var referenced = await ReferencedCodesAsync(db, ct);
        if (FirstHouseInUse(current.Houses, codes, referenced) is { } inUse)
            return Invalid(ErrorCodes.HouseInUse, "houses",
                $"House '{inUse}' is referenced by roster or carnival data and cannot be removed or renamed.");

        var updated = current with
        {
            Name = name,
            BrandColour = brandColour,
            Houses = houses,
            UpdatedAtUtc = Stamp(),
        };
        row.Name = updated.Name;
        row.BrandingJson = SchoolSettings.SerialiseBranding(updated.BrandColour, row.BrandingJson);
        row.HousesJson = SchoolSettings.SerialiseHouses(updated.Houses);
        row.UpdatedAtUtc = updated.UpdatedAtUtc;

        audit.Record(SessionActor.From(ctx), AuditActions.SchoolSettings, nameof(SchoolConfiguration),
            row.Id.ToString(), priorValues: current, newValues: updated);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiError.Conflict(ErrorCodes.SettingsStale,
                "Another administrator saved these settings first. Reload the screen and reapply your changes.");
        }

        provider.Invalidate();
        return Results.Ok(Project(updated, referenced));
    }

    private static string? FirstHouseInUse(
        IReadOnlyList<HouseMark> currentHouses,
        HashSet<string> submittedCodes,
        IReadOnlySet<string> referenced)
    {
        // An unreadable HousesJson reads as no houses; the guard must not vanish with it.
        if (currentHouses.Count == 0)
            return referenced.FirstOrDefault(code => !submittedCodes.Contains(code));

        return currentHouses
            .Select(house => house.Code)
            .FirstOrDefault(code => !submittedCodes.Contains(code) && referenced.Contains(code));
    }

    // Shared by GET's inUse and PUT's refusal so they cannot disagree. Matched in memory:
    // provider-clean SQL cannot do a case-insensitive join.
    private static async Task<HashSet<string>> ReferencedCodesAsync(
        CarnivalAppDbContext db, CancellationToken ct)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        referenced.UnionWith(await db.RosterSnapshots.Select(r => r.HouseCode).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.Entries.Where(e => e.HouseCode != null).Select(e => e.HouseCode!)
            .Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.Entries.Select(e => e.SnapshotHouseCode).Distinct().ToListAsync(ct));
        referenced.UnionWith(await db.RelayLegs.Select(l => l.SnapshotHouseCode).Distinct().ToListAsync(ct));
        referenced.UnionWith(
            await db.HousePointsAdjustments.Select(a => a.HouseCode).Distinct().ToListAsync(ct));
        return referenced;
    }

    // Truncated to the millisecond: the client sends it back, and ticks do not survive JSON.
    private static DateTimeOffset Stamp()
    {
        var now = DateTimeOffset.UtcNow.UtcDateTime;
        return new DateTimeOffset(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero);
    }
}
