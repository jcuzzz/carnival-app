using Microsoft.AspNetCore.Mvc;
using CarnivalApp.Api.Auditing;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public sealed record CreateLocalAccountRequest(string? Username, string? DisplayName);

/// <summary>School → People. Reset and disable end the account's sessions at their next request.</summary>
public static class LocalAccountAdminEndpoints
{
    public static void MapLocalAccountAdminEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var group = apiV1.MapGroup("/admin/local-accounts")
            .RequireAuthorization(LocalAccountAuthorization.AdministerAccountsPolicy);

        group.MapGet("", async ([FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
                Results.Ok(await accounts.ListAsync(ct)))
            .Produces<IReadOnlyList<LocalAccountDto>>();

        group.MapPost("", async (
                CreateLocalAccountRequest request, HttpContext http,
                [FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
            {
                var result = await accounts.CreateAsync(SessionActor.From(http), request.Username, request.DisplayName, ct);
                return result.Succeeded
                    ? Results.Created($"/api/v1/admin/local-accounts/{result.Value!.Account.Id}", result.Value)
                    : LocalAccountErrors.ToResult(result.ErrorCode!);
            })
            .Produces<IssuedAccountLink>(StatusCodes.Status201Created);

        group.MapPost("/{id:guid}/reset", async (
                Guid id, HttpContext http, [FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
                Answer(await accounts.ResetAsync(SessionActor.From(http), id, ct)))
            .Produces<IssuedAccountLink>();

        group.MapPost("/{id:guid}/disable", async (
                Guid id, HttpContext http, [FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
                Answer(await accounts.DisableAsync(SessionActor.From(http), id, ct)))
            .Produces<LocalAccountDto>();

        group.MapPost("/{id:guid}/enable", async (
                Guid id, HttpContext http, [FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
                Answer(await accounts.EnableAsync(SessionActor.From(http), id, ct)))
            .Produces<LocalAccountDto>();

        group.MapPost("/{id:guid}/clear-lockout", async (
                Guid id, HttpContext http, [FromServices] LocalAccountAdministration accounts, CancellationToken ct) =>
                Answer(await accounts.ClearLockoutAsync(
                    SessionActor.From(http), id, http.Connection.RemoteIpAddress, ct)))
            .Produces<LocalAccountDto>();
    }

    private static IResult Answer<T>(LocalAccountResult<T> result) =>
        result.Succeeded ? Results.Ok(result.Value) : LocalAccountErrors.ToResult(result.ErrorCode!);
}
