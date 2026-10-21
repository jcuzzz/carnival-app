using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace CarnivalApp.Api.Http;

public sealed record ApiErrorBody
{
    public required string Error { get; init; }

    /// <summary>Free copy: nothing may branch on it.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Serialised flat beside <c>error</c>/<c>detail</c>; keys are already camelCase.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Context { get; init; }
}

/// <summary>
/// Every non-2xx JSON response goes through here; never Results.Problem or a bodyless 4xx.
/// </summary>
public static class ApiError
{
    public static IResult BadRequest(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status400BadRequest, code, detail, context);

    public static IResult Unauthorized(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status401Unauthorized, code, detail, context);

    public static IResult Forbidden(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status403Forbidden, code, detail, context);

    public static IResult NotFound(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status404NotFound, code, detail, context);

    public static IResult Conflict(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status409Conflict, code, detail, context);

    public static IResult Unprocessable(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status422UnprocessableEntity, code, detail, context);

    /// <summary>
    /// Server-side fault or misconfiguration; the detail must not leak internals.
    /// </summary>
    public static IResult Internal(string code, string detail, Dictionary<string, object?>? context = null) =>
        Status(StatusCodes.Status500InternalServerError, code, detail, context);

    public static IResult Status(
        int statusCode, string code, string detail, Dictionary<string, object?>? context = null) =>
        Results.Json(Body(code, detail, context), statusCode: statusCode);

    public static ApiErrorBody Body(string code, string detail, Dictionary<string, object?>? context = null) =>
        new() { Error = code, Detail = detail, Context = context };

    /// <summary>
    /// Terminal only when positively identified (JSON parse error, Kestrel 413); 40001 is 409.
    /// All else is a retryable 500 — a wrong terminal answer loses a capture. Never echo the fault.
    /// </summary>
    public static ApiFault DescribeFault(Exception? fault) => fault switch
    {
        BadHttpRequestException { InnerException: JsonException } => new ApiFault(
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "The request body could not be read as JSON. Check it against the documented shape."),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => new ApiFault(
            StatusCodes.Status413PayloadTooLarge,
            ErrorCodes.RequestBodyTooLarge,
            "The request body exceeded the maximum size this server accepts."),
        var serializable when IsSerializationFailure(serializable) => new ApiFault(
            StatusCodes.Status409Conflict,
            ErrorCodes.ConcurrencyConflict,
            "Concurrent change conflict: another request changed the same data at the same time. "
            + "Re-read it and retry."),
        _ => new ApiFault(
            StatusCodes.Status500InternalServerError,
            ErrorCodes.InternalError,
            "The server could not complete this request."),
    };

    /// <summary>The framework's own refusals, which it answers with no body.</summary>
    public static ApiFault? DescribeBodylessStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status404NotFound => new ApiFault(statusCode, ErrorCodes.RouteNotFound,
            "No endpoint exists at this path."),
        StatusCodes.Status405MethodNotAllowed => new ApiFault(statusCode, ErrorCodes.MethodNotAllowed,
            "This endpoint does not accept that HTTP method."),
        StatusCodes.Status415UnsupportedMediaType => new ApiFault(statusCode, ErrorCodes.UnsupportedMediaType,
            "This endpoint does not accept that Content-Type."),
        _ => null,
    };

    // EF wraps it in DbUpdateException from SaveChanges; CommitAsync throws it bare.
    private static bool IsSerializationFailure(Exception? fault)
    {
        for (var e = fault; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
                return true;
        return false;
    }
}

/// <summary>
/// A record rather than an IResult so the classification is assertable directly: a Kestrel
/// body timeout cannot be produced through the in-process test host.
/// </summary>
public readonly record struct ApiFault(int StatusCode, string Code, string Detail);
