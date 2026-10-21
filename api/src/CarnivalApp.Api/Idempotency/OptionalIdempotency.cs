using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Idempotency;

/// <summary>
/// Idempotent only when a key is supplied. The outbox's key becomes the row's id, so a queued
/// entry and the result captured against it line up when they drain.
/// </summary>
public static class OptionalIdempotency
{
    /// <summary>
    /// The handler adopts the client id (null without a key) as the new row's key, and must use the
    /// token it is given — the idempotent path has its own.
    /// </summary>
    public static async Task<IResult> ExecuteAsync(
        HttpContext ctx,
        IdempotencyService idempotency,
        Func<Guid?, CancellationToken, Task<IResult>> handler,
        CancellationToken ct)
    {
        var header = ctx.Request.Headers[IdempotencyService.HeaderName].ToString();
        if (string.IsNullOrEmpty(header))
            return await handler(null, ct);
        // Present but unparseable is a client bug, not "no key": the unkeyed path would mint a
        // second row for a retry the device believes is deduplicated.
        if (!Guid.TryParse(header, out var clientId))
            return ApiError.BadRequest(ErrorCodes.IdempotencyKeyRequired,
                $"The '{IdempotencyService.HeaderName}' header must be a client-generated UUID.");

        return await IdempotencyEndpointFilter.ExecuteIdempotentAsync(ctx, idempotency, clientId,
            async innerCt => IdempotencyEndpointFilter.MaterialiseResult(await handler(clientId, innerCt)));
    }
}
