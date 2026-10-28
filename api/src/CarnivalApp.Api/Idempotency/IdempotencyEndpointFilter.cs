using System.Text.Json;
using System.Text.Json.Serialization;
using CarnivalApp.Api.Http;
using CarnivalApp.Api.Identity;

namespace CarnivalApp.Api.Idempotency;

public static class IdempotencyBuffering
{
    /// <summary>
    /// Lets the filter hash the raw payload after model binding
    /// consumed the stream. Must run before endpoint execution.
    /// </summary>
    public static IApplicationBuilder UseIdempotencyBuffering(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(IdempotencyService.HeaderName))
                context.Request.EnableBuffering();
            await next();
        });
}

/// <summary>
/// The wrapped handler must return a JSON value result and mutate only through the request-scoped
/// CarnivalAppDbContext, so its writes ride the idempotency transaction.
/// </summary>
public class IdempotencyEndpointFilter(IdempotencyService idempotency) : IEndpointFilter
{
    public const string DeviceIdHeader = "X-Device-Id";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers[IdempotencyService.HeaderName].ToString();
        if (!Guid.TryParse(header, out var key))
            return ApiError.BadRequest(ErrorCodes.IdempotencyKeyRequired,
                $"A client-generated UUID '{IdempotencyService.HeaderName}' header is required on this endpoint.");

        return await ExecuteIdempotentAsync(http, idempotency, key,
            async ct => Materialise(await next(context)));
    }

    internal static async Task<IResult> ExecuteIdempotentAsync(
        HttpContext http,
        IdempotencyService idempotency,
        Guid key,
        Func<CancellationToken, Task<(int StatusCode, string Body)>> action)
    {
        if (!http.Request.Body.CanSeek)
        {
            // The cause names an internal method, so it goes to the log, not the response.
            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(IdempotencyEndpointFilter))
                .LogError("Request body is not seekable: UseIdempotencyBuffering() is missing from the pipeline.");
            return ApiError.Internal(ErrorCodes.IdempotencyBufferingUnavailable,
                "This server could not process an idempotent submission. The item is still queued; retry later.");
        }
        http.Request.Body.Position = 0;
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        var payloadHash = IdempotencyService.HashPayload(buffer.ToArray());

        var outcome = await idempotency.ExecuteAsync(
            key,
            payloadHash,
            http.User.GetSubject() ?? "unknown",
            NullIfEmpty(http.Request.Headers[DeviceIdHeader].ToString()),
            action,
            http.RequestAborted);

        http.Response.Headers["Idempotency-Replayed"] = outcome.Replayed ? "true" : "false";
        return Results.Content(outcome.Body, "application/json", statusCode: outcome.StatusCode);
    }

    internal static (int StatusCode, string Body) MaterialiseResult(object? result) => Materialise(result);

    private static (int StatusCode, string Body) Materialise(object? result)
    {
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var value = (result as IValueHttpResult)?.Value ?? result switch
        {
            IResult => null,
            _ => result, // handler returned a plain value → 200 OK
        };
        var body = value is null ? "{}" : JsonSerializer.Serialize(value, JsonOptions);
        return (status, body);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
