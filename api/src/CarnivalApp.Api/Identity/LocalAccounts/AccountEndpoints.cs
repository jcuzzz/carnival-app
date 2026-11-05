using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Identity.LocalAccounts;

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record StartAuthenticatorRequest(string? CurrentPassword);

public sealed record ConfirmAuthenticatorRequest(string? Code);

/// <summary>Shown once; only their hashes are kept.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>
/// A local account acting on itself. A change that rotates the security stamp re-issues the
/// caller's own cookie, so only the account's other sessions end.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder apiV1)
    {
        var account = apiV1.MapGroup("/account")
            .RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.User.TryGetAccountId(out _)
                    ? await next(context)
                    : ApiError.Forbidden(ErrorCodes.NotALocalAccount,
                        "This is for local accounts; your account is managed by the school's identity provider."));

        account.MapGet("", GetAsync).Produces<MyLocalAccount>();

        account.MapPost("/password", ChangePasswordAsync)
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesTooManyRequests();

        account.MapPost("/authenticator", StartAuthenticatorAsync)
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .Produces<AuthenticatorSetup>()
            .ProducesTooManyRequests();

        account.MapPost("/authenticator/confirm", ConfirmAuthenticatorAsync)
            .RequireRateLimiting(RateLimiting.SignInPolicy)
            .Produces<RecoveryCodesResponse>()
            .ProducesTooManyRequests();
    }

    private static Guid AccountId(HttpContext http) =>
        http.User.TryGetAccountId(out var id) ? id : throw new InvalidOperationException("Not a local session.");

    private static async Task<IResult> GetAsync(
        HttpContext http, [FromServices] LocalAccountSelfService self, CancellationToken ct) =>
        await self.GetAsync(AccountId(http), ct) is { } mine
            ? Results.Ok(mine)
            : LocalAccountErrors.ToResult(ErrorCodes.AccountNotFound);

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        [FromServices] LocalAccountSelfService self,
        CancellationToken ct)
    {
        var result = await self.ChangePasswordAsync(
            AccountId(http), request.CurrentPassword, request.NewPassword, http.Connection.RemoteIpAddress, ct);
        if (!result.Succeeded) return LocalAccountErrors.ToResult(result.ErrorCode!);
        await LocalSessions.RefreshAsync(http, result.Value!);
        return Results.NoContent();
    }

    private static async Task<IResult> StartAuthenticatorAsync(
        StartAuthenticatorRequest request,
        HttpContext http,
        [FromServices] LocalAccountSelfService self,
        [FromServices] CarnivalAppDbContext db,
        CancellationToken ct)
    {
        var id = AccountId(http);
        var result = await self.BeginAuthenticatorAsync(id, request.CurrentPassword, http.Connection.RemoteIpAddress, ct);
        if (!result.Succeeded) return LocalAccountErrors.ToResult(result.ErrorCode!);
        // Proving the password may have upgraded its hash, which rotates the stamp.
        await LocalSessions.RefreshAsync(http, await db.LocalAccounts.AsNoTracking().SingleAsync(a => a.Id == id, ct));
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ConfirmAuthenticatorAsync(
        ConfirmAuthenticatorRequest request,
        HttpContext http,
        [FromServices] LocalAccountSelfService self,
        CancellationToken ct)
    {
        var result = await self.ConfirmAuthenticatorAsync(
            AccountId(http), request.Code, http.Connection.RemoteIpAddress, ct);
        if (!result.Succeeded) return LocalAccountErrors.ToResult(result.ErrorCode!);
        await LocalSessions.RefreshAsync(http, result.Value!.Account);
        return Results.Ok(new RecoveryCodesResponse(result.Value.RecoveryCodes));
    }
}
