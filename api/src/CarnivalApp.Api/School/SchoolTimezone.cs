namespace CarnivalApp.Api.School;

public static class SchoolTimezone
{
    public const string ConfigKey = "School:Timezone";
    public const string Default = "Australia/Melbourne";

    // Compose passes an unset variable as "".
    public static string From(IConfiguration config) =>
        config[ConfigKey] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : Default;
}
