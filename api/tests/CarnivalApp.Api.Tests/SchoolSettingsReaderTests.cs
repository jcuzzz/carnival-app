using Microsoft.Extensions.Logging;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Reading the two JSON blobs on the school-settings row. Both are tolerant by
/// rule (the JsonBlob rule), and <c>HousesJson</c> has to stay readable in the
/// pre-ADR-0044 string-array shape a deployed database may still hold.
/// </summary>
public sealed class SchoolSettingsReaderTests
{
    private static SchoolConfiguration Row(
        string? housesJson = null, string? brandingJson = null, string? settingsJson = null) => new()
    {
        Id = Guid.CreateVersion7(),
        SchoolIdentifier = "SCHOOL",
        Name = "Reader College",
        Timezone = "Australia/Melbourne",
        BrandingJson = brandingJson,
        HousesJson = housesJson,
        SettingsJson = settingsJson,
    };

    [Fact]
    public void A_pre_ADR_0044_string_array_reads_as_text_only_houses()
    {
        var logger = new CapturingLogger();
        var settings = SchoolSettings.From(
            Row("""["BANKSIA","WATTLE","MOUNTAIN ASH","GREVILLEA"]"""), logger);

        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"], settings.Houses.Select(h => h.Code));
        Assert.Equal(["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"], settings.Houses.Select(h => h.Name));
        Assert.All(settings.Houses, house => Assert.Null(house.Colour));
        Assert.Equal(0, logger.Warnings);
    }

    [Fact]
    public void The_object_shape_still_reads_and_a_blank_name_falls_back_to_the_code()
    {
        var logger = new CapturingLogger();
        var settings = SchoolSettings.From(
            Row("""[{"code":"BANKSIA","name":"Banksia","colour":"#3346c0"},{"code":"WATTLE","name":"  "}]"""),
            logger);

        Assert.Equal(["BANKSIA", "WATTLE"], settings.Houses.Select(h => h.Code));
        Assert.Equal(["Banksia", "WATTLE"], settings.Houses.Select(h => h.Name));
        Assert.Equal(new string?[] { "#3346c0", null }, settings.Houses.Select(h => h.Colour));
        Assert.Equal(0, logger.Warnings);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"houses":["BANKSIA"]}""")]
    [InlineData("[")]
    public void An_unreadable_blob_yields_no_houses_and_one_warning(string json)
    {
        var logger = new CapturingLogger();
        var row = Row(json);

        Assert.Empty(SchoolSettings.From(row, logger).Houses);
        Assert.Equal(1, logger.Warnings);
        Assert.Contains(row.Id.ToString(), logger.Last);
    }

    [Fact]
    public void Serialising_the_brand_colour_keeps_the_keys_it_does_not_own()
    {
        var merged = SchoolSettings.SerialiseBranding("#1f3a5f", """{"logo":"x","brandColour":"#000000"}""");

        Assert.Contains("\"logo\":\"x\"", merged);
        Assert.Equal("#1f3a5f", SchoolSettings.From(Row(brandingJson: merged), new CapturingLogger()).BrandColour);
    }

    [Fact]
    public void The_sign_in_settings_round_trip_and_keep_the_keys_they_do_not_own()
    {
        var merged = SchoolSettings.SerialiseSignIn(true, LocalAccountAccess.Everyone, """{"other":[1,2]}""");

        Assert.Contains("\"other\":[1,2]", merged);
        var settings = SchoolSettings.From(Row(settingsJson: merged), new CapturingLogger());
        Assert.True(settings.RequireAuthenticator);
        Assert.Equal(LocalAccountAccess.Everyone, settings.LocalAccounts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"requireAuthenticator":"yes","localAccounts":"Anyone"}""")]
    [InlineData("""{"requireAuthenticator":1,"localAccounts":"1"}""")]
    public void An_unreadable_sign_in_setting_reads_as_the_default(string? json)
    {
        var settings = SchoolSettings.From(Row(settingsJson: json), new CapturingLogger());
        Assert.False(settings.RequireAuthenticator);
        Assert.Equal(LocalAccountAccess.SystemAdministrators, settings.LocalAccounts);
    }

    [Fact]
    public void The_sign_in_settings_read_case_insensitively()
    {
        var settings = SchoolSettings.From(
            Row(settingsJson: """{"requireAuthenticator":true,"localAccounts":"everyone"}"""), new CapturingLogger());
        Assert.True(settings.RequireAuthenticator);
        Assert.Equal(LocalAccountAccess.Everyone, settings.LocalAccounts);
    }

    private sealed class CapturingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public string Last { get; private set; } = "";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Warnings++;
            Last = formatter(state, exception);
        }
    }
}
