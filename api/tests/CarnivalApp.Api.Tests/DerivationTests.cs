using System.Text;
using CarnivalApp.Api.Carnivals;
using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

public class BirthdateParserTests
{
    [Theory]
    [InlineData("2013-01-20", 2013, 1, 20)]           // ISO (ST_CLEAN shape)
    [InlineData("2013-01-20 00:00:00", 2013, 1, 20)]  // ISO with time
    [InlineData("20/1/2013", 2013, 1, 20)]            // d/m/Y
    [InlineData("20/01/2013 0:00", 2013, 1, 20)]      // d/m/Y with time
    [InlineData("41294", 2013, 1, 20)]                // Excel serial (ST_SYNTH shape)
    [InlineData("36526", 2000, 1, 1)]                 // serial documented in the mapping example
    public void Parses_all_supported_encodings(string value, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), CompetitionDerivation.ParseBirthdate(value));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-date")]
    [InlineData("13/13/2013")] // invalid month, and not valid as m/d either
    [InlineData("5")]          // implausible serial
    public void Rejects_blank_and_garbage(string? value)
        => Assert.Null(CompetitionDerivation.ParseBirthdate(value));

    [Fact]
    public void Ambiguous_day_month_is_read_as_australian_dmy()
        => Assert.Equal(new DateOnly(2013, 2, 1), CompetitionDerivation.ParseBirthdate("1/2/2013"));
}

public class AgeGroupTests
{
    private static readonly AgeGroupRule Rule = new() { CutOffMonth = 12, CutOffDay = 31 };

    [Theory]
    [InlineData(2013, 1, 20, "13")]  // turns 13 during 2026
    [InlineData(2013, 12, 31, "13")] // birthday exactly on cut-off
    [InlineData(2014, 6, 15, "12")]
    [InlineData(2008, 3, 1, "18")]
    public void Age_as_at_31_december_of_competition_year(int y, int m, int d, string expected)
        => Assert.Equal(expected, Rule.DeriveAgeGroup(new DateOnly(y, m, d), Fixtures.CompetitionYear));

    [Theory]
    [InlineData(2026, "13")]
    [InlineData(2027, "14")]
    [InlineData(2025, "12")]
    public void The_competition_year_is_the_derivations_input_not_the_rules(int year, string expected)
        => Assert.Equal(expected, Rule.DeriveAgeGroup(new DateOnly(2013, 1, 20), year));

    [Theory]
    [InlineData(2026, 12, 31, "14")] // a leap-day birthday counts as passed by 31 December
    [InlineData(2026, 2, 28, "13")]  // not yet reached on 28 February of a common year
    [InlineData(2028, 2, 29, "16")]  // reached on the day in a leap year
    public void A_leap_day_birthday_ages_exactly(
        int year, int cutOffMonth, int cutOffDay, string expected)
    {
        var rule = new AgeGroupRule { CutOffMonth = cutOffMonth, CutOffDay = cutOffDay };
        Assert.Equal(expected, rule.DeriveAgeGroup(new DateOnly(2012, 2, 29), year));
    }

    [Fact]
    public void A_29_february_cut_off_falls_on_the_28th_in_a_common_year()
    {
        var rule = new AgeGroupRule { CutOffMonth = 2, CutOffDay = 29 };
        Assert.Equal(new DateOnly(2027, 2, 28), rule.CutOffIn(2027));
        Assert.Equal(new DateOnly(2028, 2, 29), rule.CutOffIn(2028));
        Assert.Equal("02-29", rule.CutOff);
    }
}

/// <summary>
/// Loading the import mapping DOCUMENT (config/import-mapping.example.json's
/// shape). The age-group rule is validated at load: a bad value here is silent
/// until deck day, when it shows as entries admitted from the wrong ages.
/// </summary>
public class ImportMappingDocumentTests
{
    private static ImportMapping Load(string ageGroupRule) =>
        ImportMapping.Load(new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
              "columns": { "STKEY": { "maps_to": "external_student_id", "required": true } },
              "ageGroupRule": {{ageGroupRule}}
            }
            """)), "mapping-test.json");

    [Fact]
    public void The_cut_off_is_a_month_and_day()
    {
        var rule = Load("""{ "cutOff": "12-31" }""").AgeGroupRule;
        Assert.Equal(12, rule.CutOffMonth);
        Assert.Equal(31, rule.CutOffDay);
        Assert.Equal("12-31", rule.CutOff);
    }

    [Fact]
    public void A_legacy_cut_off_date_is_read_for_its_month_and_day_and_its_year_is_ignored()
    {
        var legacy = Load("""{ "cutOffDate": "2019-12-31" }""").AgeGroupRule;
        Assert.Equal("12-31", legacy.CutOff);
        // The year comes from the import, so a stale 2019 cannot age anyone for 2027.
        Assert.Equal("14", legacy.DeriveAgeGroup(new DateOnly(2013, 1, 20), 2027));
    }

    [Theory]
    [InlineData("""{ "cutOff": "31-12" }""")]
    [InlineData("""{ "cutOff": "2026-12-31" }""")]
    [InlineData("""{ "cutOffDate": "31/12/2026" }""")]
    public void A_missing_or_malformed_cut_off_is_refused_at_load(string ageGroupRule)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(ageGroupRule));
        Assert.Contains("mapping-test.json", error.Message);
        Assert.Contains("cutOff", error.Message);
    }

    [Fact]
    public void The_shipped_mapping_configures_the_cut_off_without_a_year()
        => Assert.Equal("12-31", Fixtures.Mapping().AgeGroupRule.CutOff);

    [Theory]
    [InlineData("""{ "cutOff": "12-31", "openFromAge": 17 }""", "openFromAge")]
    [InlineData("""{ "cutOff": "12-31", "openLabel": "SNR" }""", "openLabel")]
    [InlineData("""{ "cutOff": "12-31", "openFromAge": 17, "openLabel": "SNR" }""", "openFromAge")]
    public void The_removed_senior_band_is_refused_at_load(string ageGroupRule, string key)
    {
        var error = Assert.Throws<InvalidDataException>(() => Load(ageGroupRule));
        Assert.Contains("mapping-test.json", error.Message);
        Assert.Contains(key, error.Message);
        Assert.Contains("School settings", error.Message);
    }

    [Fact]
    public void A_mapping_file_with_the_senior_band_fails_startup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapping-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "columns": { "STKEY": {} }, "ageGroupRule": { "cutOff": "12-31", "openFromAge": 17 } }
            """);
        try
        {
            using var factory = new ApiTestFactory().With("Imports:MappingPath", path);
            var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            var refusal = error as InvalidOperationException ?? error.InnerException;
            Assert.Contains("openFromAge", refusal?.Message ?? error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_student_derives_a_plain_age()
    {
        var mapping = Fixtures.Mapping();
        var derived = Enumerable.Range(11, 9) // ages 11..19
            .Select(age => mapping.AgeGroupRule.DeriveAgeGroup(
                new DateOnly(Fixtures.CompetitionYear - age, 6, 15), Fixtures.CompetitionYear))
            .ToList();
        Assert.Equal(["11", "12", "13", "14", "15", "16", "17", "18", "19"], derived);
        Assert.DoesNotContain(AgeScope.OpenKey, derived);
    }
}

public class CategoryTests
{
    [Theory]
    [InlineData("M", CompetitionCategory.M)]
    [InlineData("F", CompetitionCategory.F)]
    [InlineData("f", CompetitionCategory.F)]
    [InlineData("X", CompetitionCategory.S)]
    [InlineData("S", CompetitionCategory.S)] // the generated fixture already uses S
    public void Gender_maps_to_default_competition_category(string gender, CompetitionCategory expected)
        => Assert.Equal(expected, CompetitionDerivation.DeriveCategory(gender));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Blank_gender_is_a_validation_error_not_a_default(string? gender)
        => Assert.Null(CompetitionDerivation.DeriveCategory(gender));
}
