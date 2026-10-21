using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace CarnivalApp.Api.Http;

/// <summary>
/// Fills the framework's bodyless 401/403 with the error body under <c>/api</c>. The built-in
/// handler runs first so scheme-specific challenge behaviour is untouched.
/// </summary>
public sealed class ApiErrorAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Challenged && !authorizeResult.Forbidden)
        {
            await _inner.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
        if (context.Response.HasStarted
            || !context.Request.Path.StartsWithSegments("/api"))
            return;

        var body = context.Response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => ApiError.Body(
                ErrorCodes.AuthenticationRequired,
                "Sign in to use this endpoint."),
            StatusCodes.Status403Forbidden => ApiError.Body(
                ErrorCodes.Forbidden,
                "Your role does not permit this action."),
            _ => null,
        };
        if (body is null) return;
        await Results.Json(body, statusCode: context.Response.StatusCode).ExecuteAsync(context);
    }
}
