namespace CarnivalApp.Api.Data.Entities;

public class SchoolConfiguration
{
    public Guid Id { get; set; }

    public required string SchoolIdentifier { get; set; }

    public required string Name { get; set; }

    /// <summary>IANA timezone id, e.g. "Australia/Melbourne".</summary>
    public required string Timezone { get; set; }

    public string? BrandingJson { get; set; }

    public string? HousesJson { get; set; }

    public string? SettingsJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
