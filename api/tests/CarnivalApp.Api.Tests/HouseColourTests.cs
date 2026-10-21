using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// ADR-0044 §2: what <c>School:HouseColours</c> accepts, and the two ways a
/// configured value degrades its house to text-only instead of stopping the API.
/// </summary>
public sealed class HouseColourTests
{
    private static readonly string[] Houses = ["BANKSIA", "WATTLE", "MOUNTAIN ASH", "GREVILLEA"];

    private static IReadOnlyList<HouseMark> Resolve(params (string Code, string? Colour)[] colours) =>
        HouseColour.Resolve(
            Houses,
            colours.ToDictionary(c => c.Code, c => c.Colour),
            NullLogger.Instance);

    private static string? ColourOf(IReadOnlyList<HouseMark> marks, string code) =>
        marks.Single(m => m.Code == code).Colour;

    [Theory]
    [InlineData("#3346c0", "#3346c0")]
    [InlineData("#3346C0", "#3346c0")]
    [InlineData("  #3346c0  ", "#3346c0")]
    [InlineData("#039", "#003399")]
    public void A_configured_colour_normalises_to_six_digit_hex(string configured, string expected) =>
        Assert.Equal(expected, ColourOf(Resolve(("BANKSIA", configured)), "BANKSIA"));

    [Theory]
    [InlineData("navy")]
    [InlineData("3346c0")]
    [InlineData("#3346c")]
    [InlineData("#gggggg")]
    [InlineData("")]
    [InlineData(null)]
    public void A_value_that_is_not_a_colour_leaves_the_house_text_only(string? configured) =>
        Assert.Null(ColourOf(Resolve(("BANKSIA", configured)), "BANKSIA"));

    [Fact]
    public void The_example_school_colours_are_all_kept()
    {
        var marks = Resolve(
            ("BANKSIA", "#3346c0"), ("WATTLE", "#2e9e52"),
            ("MOUNTAIN ASH", "#973023"), ("GREVILLEA", "#c962d3"));
        Assert.All(marks, mark => Assert.NotNull(mark.Colour));
    }

    /// <summary>Owner ruling 2026-09-03: no contrast floor — the swatch's border
    /// carries visibility, so the colours schools actually use are kept.</summary>
    [Theory]
    [InlineData("#ffff00")]
    [InlineData("#f5f5f5")]
    [InlineData("#00ff00")]
    public void A_pale_or_bright_colour_is_kept(string configured) =>
        Assert.Equal(configured, ColourOf(Resolve(("BANKSIA", configured)), "BANKSIA"));

    [Fact]
    public void A_duplicate_colour_drops_the_later_house_only()
    {
        // Normalisation runs first: #039 and #003399 are the same colour.
        var marks = Resolve(("BANKSIA", "#039"), ("WATTLE", "#2e9e52"), ("GREVILLEA", "#003399"));
        Assert.Equal("#003399", ColourOf(marks, "BANKSIA"));
        Assert.Equal("#2e9e52", ColourOf(marks, "WATTLE"));
        Assert.Null(ColourOf(marks, "GREVILLEA"));
    }

    [Fact]
    public void House_codes_match_case_insensitively() =>
        Assert.Equal("#3346c0", ColourOf(Resolve(("banksia", "#3346c0")), "BANKSIA"));

    [Fact]
    public void An_entry_naming_no_configured_house_is_ignored()
    {
        var marks = Resolve(("ATLANTIS", "#3346c0"), ("WATTLE", "#2e9e52"));
        Assert.Equal(Houses, marks.Select(m => m.Code));
        Assert.Equal("#2e9e52", ColourOf(marks, "WATTLE"));
        Assert.All(marks.Where(m => m.Code != "WATTLE"), mark => Assert.Null(mark.Colour));
    }

    /// <summary>
    /// The commonest misconfiguration: the house was renamed and the colour key
    /// was not. Silence there looks exactly like a colour that failed the gate.
    /// </summary>
    [Fact]
    public void A_colour_key_matching_no_house_is_warned_about()
    {
        var logger = new CountingLogger();
        HouseColour.Resolve(
            Houses,
            new Dictionary<string, string?> { ["ATLANTIS"] = "#3346c0", ["WATTLE"] = "#2e9e52" },
            logger);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public void An_empty_colour_key_matching_no_house_says_nothing()
    {
        var logger = new CountingLogger();
        HouseColour.Resolve(
            Houses, new Dictionary<string, string?> { ["ATLANTIS"] = "" }, logger);
        Assert.Equal(0, logger.Warnings);
    }

    [Fact]
    public void With_no_map_every_house_is_text_only()
    {
        var marks = HouseColour.Resolve(Houses, null, NullLogger.Instance);
        Assert.Equal(Houses, marks.Select(m => m.Code));
        Assert.All(marks, mark =>
        {
            Assert.Equal(mark.Code, mark.Name);
            Assert.Null(mark.Colour);
        });
    }

    [Fact]
    public void Each_dropped_house_is_warned_about_once()
    {
        var logger = new CountingLogger();
        HouseColour.Resolve(
            Houses,
            new Dictionary<string, string?>
            {
                ["BANKSIA"] = "#3346c0",
                ["WATTLE"] = "not-a-colour",
                ["MOUNTAIN ASH"] = "#973023",
                ["GREVILLEA"] = "#3346c0",
            },
            logger);
        Assert.Equal(2, logger.Warnings);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}
