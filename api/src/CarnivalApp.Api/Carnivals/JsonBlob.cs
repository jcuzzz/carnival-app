using System.Text.Json;
using CarnivalApp.Api.Http;

namespace CarnivalApp.Api.Carnivals;

public enum JsonBlobShape
{
    Object,

    StringArray,
}

// Read is tolerant (a malformed stored blob degrades to no value, never a 500 on a whole list);
// write is checked for well-formedness and rough shape only.
public static class JsonBlob
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    // Null when absent; empty when malformed.
    public static IReadOnlyList<string>? StringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Absent is acceptable: an omitted blob means unchanged.
    public static string? Validate(string? json, string field, JsonBlobShape shape)
    {
        if (json is null) return null;
        if (string.IsNullOrWhiteSpace(json))
            return $"{field} must be JSON; omit it rather than sending a blank string.";

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { return $"{field} is not valid JSON: {ex.Message}"; }

        using (document)
        {
            var root = document.RootElement;
            return shape switch
            {
                JsonBlobShape.Object when root.ValueKind != JsonValueKind.Object =>
                    $"{field} must be a JSON object.",
                JsonBlobShape.StringArray when root.ValueKind != JsonValueKind.Array =>
                    $"{field} must be a JSON array of strings.",
                JsonBlobShape.StringArray when root.EnumerateArray()
                    .Any(e => e.ValueKind != JsonValueKind.String) =>
                    $"{field} must contain strings only.",
                _ => null,
            };
        }
    }

    public static IResult? Refuse(params (string? Json, string Field, JsonBlobShape Shape)[] blobs)
    {
        foreach (var (json, field, shape) in blobs)
            if (Validate(json, field, shape) is { } detail)
                return ApiError.BadRequest(ErrorCodes.ValidationFailed, detail);
        return null;
    }
}
