using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using CarnivalApp.Api.Data;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Demo;

namespace CarnivalApp.Api.School;

/// <summary>The school's age range; the oldest age takes everyone older.</summary>
public sealed record SchoolAges(int Youngest, int Oldest)
{
    public const int Minimum = 1;
    public const int Maximum = 25;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Minimum <= Youngest && Youngest <= Oldest && Oldest <= Maximum;
}

public sealed record SchoolSettings(
    string Name,
    string? BrandColour,
    IReadOnlyList<HouseMark> Houses,
    bool RequireAuthenticator,
    LocalAccountAccess LocalAccounts,
    DateTimeOffset? UpdatedAtUtc,
    bool SetupConfirmed,
    SchoolAges? Ages)
{
    private const string SetupConfirmedKey = "setupConfirmedAtUtc";
    private const string AgesKey = "ages";

    public static SchoolSettings From(SchoolConfiguration row, ILogger logger)
    {
        var signIn = ReadSignIn(row.SettingsJson);
        return new(
            row.Name,
            ReadBrandColour(row.BrandingJson),
            ReadHouses(row.HousesJson, row.Id, logger),
            signIn.RequireAuthenticator,
            signIn.LocalAccounts,
            row.UpdatedAtUtc,
            ReadSetupConfirmed(row.SettingsJson) is not null,
            ReadAges(row.SettingsJson));
    }

    // Merges rather than replaces: unknown keys belong to whoever wrote them.
    public static string SerialiseBranding(string? brandColour, string? existingJson = null)
    {
        var blob = ParseObject(existingJson);
        blob.Remove("BrandColour");
        blob["brandColour"] = brandColour is null ? null : JsonValue.Create(brandColour);
        return blob.ToJsonString();
    }

    public static string SerialiseSignIn(
        bool requireAuthenticator, LocalAccountAccess localAccounts, string? existingJson = null)
    {
        var blob = ParseObject(existingJson);
        blob["requireAuthenticator"] = requireAuthenticator;
        blob["localAccounts"] = localAccounts.ToString();
        return blob.ToJsonString();
    }

    /// <summary>Keeps the first confirmation: a later save does not move it.</summary>
    public static string SerialiseSetupConfirmed(DateTimeOffset at, string? existingJson)
    {
        var blob = ParseObject(existingJson);
        if (ReadSetupConfirmed(existingJson) is null)
            blob[SetupConfirmedKey] = at.ToString("O", CultureInfo.InvariantCulture);
        return blob.ToJsonString();
    }

    public static string SerialiseAges(SchoolAges? ages, string? existingJson)
    {
        var blob = ParseObject(existingJson);
        blob[AgesKey] = ages is null
            ? null
            : new JsonObject { ["youngest"] = ages.Youngest, ["oldest"] = ages.Oldest };
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

    // An unreadable value reads as the default: off, and System administrators only.
    private static (bool RequireAuthenticator, LocalAccountAccess LocalAccounts) ReadSignIn(string? json)
    {
        var blob = ParseObject(json);
        var require = blob["requireAuthenticator"] is JsonValue flag
            && flag.TryGetValue<bool>(out var on) && on;
        var access = blob["localAccounts"] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !text.Any(char.IsAsciiDigit)
            && Enum.TryParse<LocalAccountAccess>(text, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : LocalAccountAccess.SystemAdministrators;
        return (require, access);
    }

    // Set by an administrator's first save of School settings; a seeded row has none.
    private static DateTimeOffset? ReadSetupConfirmed(string? json) =>
        ParseObject(json)[SetupConfirmedKey] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    // An unreadable or out-of-range value reads as no ages: the form then offers its default.
    private static SchoolAges? ReadAges(string? json) =>
        ParseObject(json)[AgesKey] is JsonObject ages
        && ages["youngest"] is JsonValue youngest && youngest.TryGetValue<int>(out var from)
        && ages["oldest"] is JsonValue oldest && oldest.TryGetValue<int>(out var to)
        && new SchoolAges(from, to) is { IsValid: true } read
            ? read
            : null;

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
