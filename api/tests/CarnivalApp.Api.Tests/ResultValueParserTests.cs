using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Time entry parsing (proposal §5.1): officials type mm:ss.ff or ss.ff; the
/// canonical stored value is decimal seconds.
/// </summary>
public sealed class ResultValueParserTests
{
    [Theory]
    [InlineData("34.56", "34.56")]
    [InlineData("34", "34")]
    [InlineData("59.9", "59.9")]
    [InlineData("90.5", "90.5")]        // seconds may exceed 59 without minutes
    [InlineData("1:02.34", "62.34")]
    [InlineData("01:02.34", "62.34")]
    [InlineData("1:23.456", "83.456")]
    [InlineData("2:05", "125")]
    [InlineData("10:00.01", "600.01")]
    [InlineData(" 34.56 ", "34.56")]    // whitespace tolerated
    public void Valid_times_parse_to_canonical_seconds(string raw, string expected)
    {
        Assert.True(ResultValueParser.TryParse(MeasureType.Time, raw, out var value, out var error));
        Assert.Null(error);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:75.00")]   // 75 seconds is not valid alongside minutes
    [InlineData("1:2.3")]     // seconds must be two digits with minutes
    [InlineData("-34.56")]
    [InlineData("34,56")]     // no locale comma
    [InlineData("1:02:03")]   // no hours
    [InlineData("34.5678")]   // more than 3 fraction digits
    [InlineData("0")]         // zero time is not a result
    [InlineData("0:00.00")]
    public void Invalid_times_are_rejected_with_an_error(string raw)
    {
        Assert.False(ResultValueParser.TryParse(MeasureType.Time, raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(MeasureType.Distance, "4.32", "4.32")]
    [InlineData(MeasureType.Distance, "10.5", "10.5")]
    [InlineData(MeasureType.Distance, "999.99", "999.99")]
    [InlineData(MeasureType.Distance, "12", "12")]
    [InlineData(MeasureType.Height, "1.35", "1.35")]
    [InlineData(MeasureType.Height, "1.55", "1.55")]
    [InlineData(MeasureType.Height, "9.99", "9.99")]
    [InlineData(MeasureType.Count, "12", "12")]
    [InlineData(MeasureType.Count, " 12 ", "12")]
    public void Field_measurements_parse_to_the_kinds_precision(MeasureType type, string raw, string expected)
    {
        Assert.True(ResultValueParser.TryParse(type, raw, out var value, out _));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData(MeasureType.Distance, "1:02.34")] // time syntax is not a distance
    [InlineData(MeasureType.Distance, "-4.32")]
    [InlineData(MeasureType.Distance, "0")]
    [InlineData(MeasureType.Distance, "4.325")]   // ADR-0052 §1: the centimetre is the floor
    [InlineData(MeasureType.Distance, "1000.00")] // three whole metres digits at most
    [InlineData(MeasureType.Height, "1.355")]
    [InlineData(MeasureType.Height, "10.00")]     // one whole metre digit at most
    [InlineData(MeasureType.Height, "0")]
    [InlineData(MeasureType.Count, "abc")]
    [InlineData(MeasureType.Count, "12.5")]       // a count is whole
    [InlineData(MeasureType.Count, "-3")]
    [InlineData(MeasureType.Count, "0")]
    public void Invalid_measurements_are_rejected(MeasureType type, string raw)
    {
        Assert.False(ResultValueParser.TryParse(type, raw, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(MeasureType.Distance, "centimetre")]
    [InlineData(MeasureType.Height, "centimetre")]
    [InlineData(MeasureType.Count, "whole number")]
    public void The_refusal_names_the_kinds_own_precision(MeasureType type, string hint)
    {
        Assert.False(ResultValueParser.TryParse(type, "4.3215", out _, out var error));
        Assert.Contains(hint, error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("62.34", "1:02.34")]
    [InlineData("34.5", "34.50")]
    [InlineData("600.01", "10:00.01")]
    public void Canonical_times_format_back_to_display(string seconds, string expected) =>
        Assert.Equal(expected, ResultValueParser.FormatTime(
            decimal.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
}
