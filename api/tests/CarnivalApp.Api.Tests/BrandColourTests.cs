using CarnivalApp.Api.School;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// The colour half of ADR-0040 §3: what <c>School:BrandColour</c> accepts, and
/// the WCAG 2.x contrast rule the startup gate applies to it.
/// </summary>
public sealed class BrandColourTests
{
    [Theory]
    [InlineData("#1f3a5f", "#1f3a5f")]
    [InlineData("#1F3A5F", "#1f3a5f")]
    [InlineData("  #1f3a5f  ", "#1f3a5f")]
    [InlineData("#fff", "#ffffff")]
    [InlineData("#000", "#000000")]
    [InlineData("#F00", "#ff0000")]
    public void Parsed_colours_normalise_to_six_digit_hex(string configured, string expected) =>
        Assert.Equal(expected, BrandColour.Normalise(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyz")]
    [InlineData("1f3a5f")]
    [InlineData("#1f3a5")]
    [InlineData("#1f3a5ff")]
    [InlineData("#xyzxyz")]
    [InlineData("navy")]
    [InlineData("rgb(31,58,95)")]
    public void Anything_that_is_not_a_hex_colour_normalises_to_null(string? configured) =>
        Assert.Null(BrandColour.Normalise(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_colour_is_accepted(string? configured) =>
        Assert.Null(BrandColour.DescribeRefusal(configured));

    [Theory]
    [InlineData("xyz")]
    [InlineData("1f3a5f")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    public void A_malformed_colour_is_refused_by_name(string configured)
    {
        var refusal = BrandColour.DescribeRefusal(configured);
        Assert.NotNull(refusal);
        Assert.Contains(BrandColour.ConfigKey, refusal);
        Assert.Contains(configured, refusal);
    }

    [Theory]
    // The navy the default palette already uses, plus the two extremes: each
    // reaches 4.5:1 against black or white, so each has a readable ink.
    [InlineData("#1f3a5f")]
    [InlineData("#fff")]
    [InlineData("#000")]
    [InlineData("#ffffff")]
    [InlineData("#000000")]
    [InlineData("#8b0000")]
    public void A_colour_with_a_readable_ink_is_accepted(string configured)
    {
        Assert.Null(BrandColour.DescribeRefusal(configured));
        Assert.True(BrandColour.BestContrast(configured) >= BrandColour.MinimumContrastRatio);
    }

    /// <summary>
    /// The rule's floor, pinned deliberately: under WCAG 2.x every sRGB colour
    /// reaches at least ~4.58:1 against black or white (the worst case is the
    /// mid-grey below, where the two ratios meet), so NO well-formed colour can
    /// fail the 4.5:1 test in both directions. The contrast branch of the gate
    /// is therefore defensive — malformed values are what it actually refuses.
    /// Raising the threshold above 4.58 is an ADR-0040 decision, not a code one.
    /// </summary>
    [Theory]
    [InlineData("#757575")]
    [InlineData("#767676")]
    [InlineData("#777777")]
    [InlineData("#7a7a7a")]
    public void The_worst_case_mid_grey_still_clears_the_bar_in_one_direction(string grey)
    {
        var best = BrandColour.BestContrast(grey);
        Assert.NotNull(best);
        Assert.InRange(best.Value, BrandColour.MinimumContrastRatio, 5.0);
        Assert.Null(BrandColour.DescribeRefusal(grey));
    }

    [Fact]
    public void Contrast_is_computed_from_relative_luminance()
    {
        // White on black is the 21:1 ceiling; a mid grey is nowhere near it.
        Assert.Equal(21.0, BrandColour.BestContrast("#000000")!.Value, 2);
        Assert.Equal(21.0, BrandColour.BestContrast("#ffffff")!.Value, 2);
        Assert.True(BrandColour.BestContrast("#1f3a5f") > 8.0);
    }
}
