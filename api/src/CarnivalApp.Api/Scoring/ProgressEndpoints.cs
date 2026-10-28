using Microsoft.AspNetCore.Mvc;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data;

namespace CarnivalApp.Api.Scoring;

public static class ProgressEndpoints
{
    public static void MapProgressEndpoints(this IEndpointRouteBuilder apiV1)
    {
        apiV1.MapGet("/carnivals/{id:guid}/progress", GetProgressAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<ProgressService.CarnivalProgress>();
    }

    private static async Task<IResult> GetProgressAsync(
        Guid id, [FromServices] CarnivalAppDbContext db, CancellationToken ct)
    {
        var progress = await ProgressService.ComputeAsync(db, id, ct);
        return progress is null ? CarnivalGuards.CarnivalNotFound() : Results.Ok(progress);
    }
}
