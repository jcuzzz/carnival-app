using System.Text.Json;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Assertions against the ADR-0011 error contract. Tests assert on the stable
/// <c>error</c> CODE, never on the human <c>detail</c> sentence — the sentence is
/// free to be reworded (or moved into the client) without breaking the suite,
/// which is the point of the contract.
/// </summary>
public static class ApiErrorAssertions
{
    /// <summary>The machine code from a non-2xx body; fails if the body is not the ADR-0011 shape.</summary>
    public static async Task<string> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.TryGetProperty("error", out var error),
            $"Response body is not an ADR-0011 error: {body}");
        Assert.True(root.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString()),
            $"ADR-0011 error body carries no detail sentence: {body}");
        return error.GetString()!;
    }

    public static async Task AssertErrorAsync(this HttpResponseMessage response, string expectedCode) =>
        Assert.Equal(expectedCode, await response.ErrorCodeAsync());

    /// <summary>The human sentence, for asserting what it must NOT disclose
    /// (ADR-0026 §2 — a refusal string is a response body). Never for pinning wording.</summary>
    public static async Task<string> ErrorDetailAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("detail").GetString()!;

    /// <summary>Extra structured context a code is allowed to carry beside error/detail.</summary>
    public static async Task<JsonElement> ErrorContextAsync(this HttpResponseMessage response, string property)
    {
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.TryGetProperty(property, out var value),
            $"Error body carries no '{property}' context: {body}");
        return value;
    }
}
