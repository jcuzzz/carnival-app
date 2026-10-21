using Microsoft.AspNetCore.Mvc;
using CarnivalApp.Api.Carnivals;

namespace CarnivalApp.Api.Scoring;

public static class StandingsEndpoints
{
    public static void MapStandingsEndpoints(this IEndpointRouteBuilder apiV1)
    {
        apiV1.MapGet("/carnivals/{id:guid}/standings", GetStandingsAsync)
            .RequireAuthorization(CarnivalAuthorization.ViewCarnivalsPolicy)
            .Produces<ScoringService.Standings>();
    }

    private static async Task<IResult> GetStandingsAsync(
        Guid id, [FromServices] ScoringService scoring, CancellationToken ct)
    {
        var standings = await scoring.ComputeAsync(id, ct);
        return standings is null ? CarnivalGuards.CarnivalNotFound() : Results.Ok(standings);
    }
}
