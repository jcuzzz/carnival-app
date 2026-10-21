using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CarnivalApp.Api.Http;

/// <summary>The document the PWA generates its wire types from.</summary>
public static class OpenApiConventions
{
    private static readonly int[] ErrorStatusCodes =
    [
        StatusCodes.Status400BadRequest,
        StatusCodes.Status401Unauthorized,
        StatusCodes.Status403Forbidden,
        StatusCodes.Status404NotFound,
        StatusCodes.Status409Conflict,
        StatusCodes.Status422UnprocessableEntity,
        StatusCodes.Status500InternalServerError,
    ];

    // Produces<T>() exists only for a single route handler, so the metadata is attached directly.
    public static TBuilder ProducesApiErrors<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (var statusCode in ErrorStatusCodes)
            builder.WithMetadata(new ProducesResponseTypeMetadata(
                statusCode, typeof(ApiErrorBody), ["application/json"]));
        return builder;
    }

    /// <summary>For a flag bound as a string so a bad value can be refused as a 400.</summary>
    public static RouteHandlerBuilder DocumentsBooleanQuery(this RouteHandlerBuilder builder, string name) =>
        builder.AddOpenApiOperationTransformer((operation, _, _) =>
        {
            if (operation.Parameters?.FirstOrDefault(p => p.Name == name) is OpenApiParameter parameter)
                parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean };
            return Task.CompletedTask;
        });

    public static void Configure(OpenApiOptions options) =>
        options.AddSchemaTransformer((schema, _, _) =>
        {
            // AllowReadingFromString types every number as number-or-string; responses are real
            // numbers, so the string alternative is dropped from the document only.
            const JsonSchemaType numeric = JsonSchemaType.Number | JsonSchemaType.Integer;
            if (schema.Type is { } type && (type & numeric) != 0 && (type & JsonSchemaType.String) != 0)
            {
                schema.Type = type & ~JsonSchemaType.String;
                schema.Pattern = null;
            }

            // An enum used nullably anywhere gets null folded into the shared component; nullable
            // uses carry their own null beside the $ref.
            if (schema.Enum is { Count: > 0 } values && values.Any(v => v is null))
            {
                schema.Enum = [.. values.Where(v => v is not null)];
                if (schema.Type is { } enumType) schema.Type = enumType & ~JsonSchemaType.Null;
            }
            return Task.CompletedTask;
        });
}
