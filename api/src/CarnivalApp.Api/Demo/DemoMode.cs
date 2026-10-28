using CarnivalApp.Api.Data;

namespace CarnivalApp.Api.Demo;

/// <summary>Configuration of a public demo host (ADR-0061).</summary>
public static class DemoMode
{
    public const string EnabledKey = "Demo:Enabled";
    public const string VisitorPoolSizeKey = "Demo:VisitorPoolSize";
    public const string TemplateDatabaseKey = "Demo:TemplateDatabase";
    public const string MaxSessionsKey = "Demo:MaxSessions";
    public const string ProvisionsPerIpPerHourKey = "Demo:ProvisionsPerIpPerHour";
    public const string ProvisionsPerMinuteKey = "Demo:ProvisionsPerMinute";
    public const string RequestsPerIpPerMinuteKey = "Demo:RequestsPerIpPerMinute";
    public const string CleanupIntervalMinutesKey = "Demo:CleanupIntervalMinutes";

    /// <summary>The provisioner role on the control database: CREATEDB, no visitor table grants.</summary>
    public const string ControlConnectionName = "DemoControl";

    public const string DefaultTemplateDatabase = "carnival_demo_template";
    public const int DefaultPoolSize = 3;
    public const int DefaultMaxSessions = 100;

    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan EvictableAfterIdle = TimeSpan.FromMinutes(30);

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, false);

    public static int PoolSize(IConfiguration configuration) =>
        Math.Max(1, configuration.GetValue(VisitorPoolSizeKey, DefaultPoolSize));

    public static string ControlConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ControlConnectionName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"ConnectionStrings:{ControlConnectionName} is required when {EnabledKey} is true.");

    public static string VisitorConnectionString(IConfiguration configuration, string database) =>
        VisitorDatabase.ForDatabase(DatabaseConnectionStrings.Application(configuration), database, PoolSize(configuration));
}
