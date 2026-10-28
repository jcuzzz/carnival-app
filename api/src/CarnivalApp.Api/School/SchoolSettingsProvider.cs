using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;

namespace CarnivalApp.Api.School;

public sealed record SchoolSettings(
    string Name,
    string? BrandColour,
    IReadOnlyList<HouseMark> Houses,
    DateTimeOffset? UpdatedAtUtc)
{
    public static SchoolSettings From(SchoolConfiguration row, ILogger logger) => new(
        row.Name,
        ReadBrandColour(row.BrandingJson),
        ReadHouses(row.HousesJson, row.Id, logger),
        row.UpdatedAtUtc);

    // Merges rather than replaces: unknown keys belong to whoever wrote them.
    public static string SerialiseBranding(string? brandColour, string? existingJson = null)
    {
        var blob = ParseObject(existingJson);
        blob.Remove("BrandColour");
        blob["brandColour"] = brandColour is null ? null : JsonValue.Create(brandColour);
        return blob.ToJsonString();
    }

    public static string SerialiseHouses(IReadOnlyList<HouseMark> houses) =>
        JsonSerializer.Serialize(houses, JsonSerializerOptions.Web);

    // A blob an earlier version or a DBA wrote degrades
    // to no value rather than 500 the sign-in read.
    private static string? ReadBrandColour(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var blob = ParseObject(json);
            // Qualified: the record's own BrandColour property shadows the type.
            return CarnivalApp.Api.School.BrandColour.Normalise(
                (blob["brandColour"] ?? blob["BrandColour"])?.GetValue<string>());
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static IReadOnlyList<HouseMark> ReadHouses(string? json, Guid rowId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            return Unreadable(rowId, logger, error.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Unreadable(rowId, logger, $"the blob is a JSON {document.RootElement.ValueKind}");

            var houses = new List<HouseMark>();
            foreach (var element in document.RootElement.EnumerateArray())
                if (ReadHouse(element) is { } house)
                    houses.Add(house);
            return houses;
        }
    }

    // Older rows hold a bare string array: the code is the name and there is no colour.
    private static HouseMark? ReadHouse(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return Text(element) is { } bare ? new HouseMark(bare, bare, null) : null;
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (Property(element, "code") is not { } code) return null;
        return new HouseMark(code, Property(element, "name") ?? code, Property(element, "colour"));
    }

    private static string? Property(JsonElement element, string name)
    {
        // Web naming on the way out, but a hand-written row may be PascalCase.
        if (!element.TryGetProperty(name, out var value)
            && !element.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out value))
            return null;
        return Text(value);
    }

    private static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static IReadOnlyList<HouseMark> Unreadable(Guid rowId, ILogger logger, string cause)
    {
        logger.LogWarning(
            "School settings row {SchoolConfigurationId}: HousesJson could not be read ({Cause}), so no "
            + "houses are configured. Set them on the School settings screen (ADR-0045 §1).", rowId, cause);
        return [];
    }
}

/// <summary>
/// Single-instance deployment, so <see cref="Invalidate"/> after a save is enough. One slot per
/// database: a demo host serves one database per visitor (ADR-0061 §1).
/// </summary>
public sealed class SchoolSettingsProvider(
    IServiceScopeFactory scopes, IDatabaseKey database, ILogger<SchoolSettingsProvider> logger)
{
    private sealed class Slot
    {
        public SchoolSettings? Cached;
        public long Generation;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();

    public async Task<SchoolSettings> GetAsync(CancellationToken ct = default)
    {
        var slot = _slots.GetOrAdd(database.Current, _ => new Slot());
        if (Volatile.Read(ref slot.Cached) is { } cached) return cached;

        var generation = Volatile.Read(ref slot.Generation);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarnivalAppDbContext>();
        var row = await db.SchoolConfigurations.AsNoTracking().TheSchool().FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(
                "No SchoolConfiguration row exists. SchoolSettingsBootstrap creates one at startup "
                + "(ADR-0045 §1); the row must not be deleted from a running deployment.");
        var settings = SchoolSettings.From(row, logger);

        // Never cache a read an Invalidate overtook, or a save committing mid-query is cached away.
        if (Volatile.Read(ref slot.Generation) == generation) Volatile.Write(ref slot.Cached, settings);
        return settings;
    }

    public void Invalidate()
    {
        if (!_slots.TryGetValue(database.Current, out var slot)) return;
        Interlocked.Increment(ref slot.Generation);
        Volatile.Write(ref slot.Cached, null);
    }

    public void Forget(string databaseName) => _slots.TryRemove(databaseName, out _);
}
