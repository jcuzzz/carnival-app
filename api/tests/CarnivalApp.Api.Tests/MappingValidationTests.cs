using CarnivalApp.Api.Data.Entities;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Mapping + validation stages over the real-shape generated fixture
/// (ST_CLEAN.csv, 5198 rows) and the wide raw-shape fixture (ST_SYNTH.csv).
/// </summary>
public class MappingValidationTests
{
    private static List<StagedRow> StageClean() =>
        RosterImportService.MapAndValidate(Fixtures.ParseFixture("ST_CLEAN.csv"), Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses);

    [Fact]
    public void Clean_fixture_stages_all_rows()
    {
        var rows = StageClean();
        Assert.Equal(5198, rows.Count);
    }

    [Fact]
    public void Only_actv_rows_are_promotable_and_departed_rows_become_inactive_references()
    {
        var rows = StageClean();
        // 884 ACTV rows in the generated fixture, one of which has a blank
        // house → 883 promotable.
        Assert.Equal(883, rows.Count(r => r.Disposition == RowDisposition.New));
        // All 4314 non-ACTV rows become inactive identity references.
        Assert.Equal(4314, rows.Count(r => r.Disposition == RowDisposition.Inactive));
        // Only the blank-house ACTV row rejects.
        Assert.Equal(1, rows.Count(r => r.Disposition == RowDisposition.Rejected));
        Assert.Equal(5198, rows.Count);
    }

    [Fact]
    public void Legacy_house_codes_on_departed_students_do_not_fail_the_batch()
    {
        var rows = StageClean();
        var departedWithLegacyHouse = rows.Where(r =>
            r.Disposition == RowDisposition.Inactive &&
            r.HouseCode is "S12" or "MS9" or "JR7" or "MS10" or "JR8" or "S11").ToList();
        Assert.NotEmpty(departedWithLegacyHouse);
        Assert.All(departedWithLegacyHouse, r => Assert.Empty(r.Issues));
    }

    [Fact]
    public void The_single_blank_house_actv_student_surfaces_as_an_exception_requiring_resolution()
    {
        var rows = StageClean();
        var rejected = Assert.Single(rows, r => r.Issues.Any(i => i.Code == "MISSING_HOUSE"));
        Assert.Equal(RowDisposition.Rejected, rejected.Disposition);
        Assert.Equal("SYN4899572274", rejected.ExternalStudentId);
        var issue = Assert.Single(rejected.Issues);
        Assert.Equal("MISSING_HOUSE", issue.Code);
        Assert.Equal(IssueSeverity.Exception, issue.Severity);
    }

    [Fact]
    public void Active_rows_get_derived_age_group_and_category_and_no_birthdate_is_retained()
    {
        var rows = StageClean();
        var active = rows.Where(r => r.Disposition == RowDisposition.New).ToList();
        Assert.All(active, r =>
        {
            Assert.NotNull(r.CompetitionAgeGroup);
            Assert.NotNull(r.CompetitionCategory);
            Assert.InRange(r.YearLevel!.Value, 7, 12);
        });
        // The staged model has no birthdate member at all — assert at the type level.
        Assert.DoesNotContain(typeof(StagedRow).GetProperties(),
            p => p.Name.Contains("birth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Synth_fixture_excel_serial_birthdates_derive_age_groups()
    {
        var fixture = Fixtures.ParseFixture("ST_SYNTH.csv");
        var rows = RosterImportService.MapAndValidate(
            fixture, Fixtures.Mapping(), Fixtures.CompetitionYear, ["WATTLE", "BANKSIA", "MOUNTAIN ASH", "GREVILLEA"]);
        string Cell(IReadOnlyList<string> raw, string column) => raw[fixture.IndexOf(column)];

        // The generator duplicates its first row; derivation still runs on both copies.
        var duplicated = rows.Where(r => r.ExternalStudentId == Cell(fixture.Rows[0], "STKEY")).ToList();
        Assert.Equal(2, duplicated.Count);
        Assert.All(duplicated, r => Assert.Contains(r.Issues, i => i.Code == "DUPLICATE_STKEY"));

        var raw = fixture.Rows.Skip(4).First(r =>
            Cell(r, "STATUS") == "ACTV" && Cell(r, "GENDER") == "F" && int.TryParse(Cell(r, "BIRTHDATE"), out _));
        var born = DateOnly.FromDateTime(DateTime.FromOADate(int.Parse(Cell(raw, "BIRTHDATE"))));
        var row = rows.First(r => r.ExternalStudentId == Cell(raw, "STKEY"));
        Assert.Equal((Fixtures.CompetitionYear - born.Year).ToString(), row.CompetitionAgeGroup);
        Assert.Equal(CompetitionCategory.F, row.CompetitionCategory);
    }

    [Fact]
    public void Duplicate_stkeys_are_errors_on_every_affected_row()
    {
        // STKEY is unique in CASES21; a repeated key is corrupt source data.
        // All affected rows error (regardless of status mix) and never
        // promote; unaffected rows are untouched and the batch is not failed.
        var csv = CsvTable.Parse(
            "STKEY,SURNAME,FIRST_NAME,PREF_NAME,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n" +
            "A1,Smith,Ann,Ann,F,WATTLE,ACTV,2013-01-20,07A,07\n" +
            "A1,Smith,Ann,Ann,,S12,LEFT,,,\n" +
            "B2,Jones,Bo,Bo,M,BANKSIA,ACTV,2012-05-05,08B,08\n");
        var rows = RosterImportService.MapAndValidate(csv, Fixtures.Mapping(), Fixtures.CompetitionYear, Fixtures.Houses);
        var dupes = rows.Where(r => r.ExternalStudentId == "A1").ToList();
        Assert.Equal(2, dupes.Count);
        Assert.All(dupes, r =>
        {
            Assert.Equal(RowDisposition.Rejected, r.Disposition);
            Assert.Contains(r.Issues,
                i => i.Code == "DUPLICATE_STKEY" && i.Severity == IssueSeverity.Error);
        });
        Assert.Equal(RowDisposition.New, rows.Single(r => r.ExternalStudentId == "B2").Disposition);
    }

    [Fact]
    public void Regenerated_fixture_is_collision_free()
    {
        // ST_CLEAN.csv is generator-produced with unique STKEYs: no STKEY may
        // duplicate and no row may carry a DUPLICATE_STKEY error. The duplicate
        // error path itself stays covered by
        // Duplicate_stkeys_are_errors_on_every_affected_row.
        var rows = StageClean();
        var duplicatedKeys = rows.Where(r => r.ExternalStudentId is not null)
            .GroupBy(r => r.ExternalStudentId!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicatedKeys);
        Assert.DoesNotContain(rows, r => r.Issues.Any(i => i.Code == "DUPLICATE_STKEY"));
    }

    [Theory]
    [InlineData("06", "INVALID_YEAR_LEVEL")] // below range
    [InlineData("13", "INVALID_YEAR_LEVEL")] // above range
    [InlineData("", "INVALID_YEAR_LEVEL")]
    public void Year_level_outside_07_to_12_rejects_active_rows(string year, string code)
    {
        var rows = Stage($"A1,Smith,Ann,Ann,F,WATTLE,ACTV,2013-01-20,07A,{year}");
        Assert.Contains(rows[0].Issues, i => i.Code == code);
        Assert.True(rows[0].IsRejected);
    }

    [Fact]
    public void Unknown_house_on_active_row_is_an_error()
    {
        var rows = Stage("A1,Smith,Ann,Ann,F,S12,ACTV,2013-01-20,07A,07");
        Assert.Contains(rows[0].Issues, i => i.Code == "UNKNOWN_HOUSE" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Missing_birthdate_on_active_row_is_an_error()
    {
        var rows = Stage("A1,Smith,Ann,Ann,F,WATTLE,ACTV,,07A,07");
        Assert.Contains(rows[0].Issues, i => i.Code == "MISSING_BIRTHDATE");
    }

    /// <summary>
    /// Data minimisation: issues persist in <c>ImportBatch.ValidationResultsJson</c>
    /// and are served by <c>GET /imports/{id}/validation</c>, so a rejected batch
    /// that echoed the value kept STKEY↔date-of-birth pairs indefinitely. The row
    /// number and the STKEY already locate the row.
    /// </summary>
    [Fact]
    public void An_unparseable_birthdate_is_reported_without_echoing_the_value()
    {
        var rows = Stage("A1,Smith,Ann,Ann,F,WATTLE,ACTV,31/31/2013,07A,07");

        var issue = Assert.Single(rows[0].Issues, i => i.Code == "INVALID_BIRTHDATE");
        Assert.DoesNotContain("31", issue.Message);
        Assert.All(rows[0].Issues, i => Assert.DoesNotContain("2013", i.Message));
    }

    [Fact]
    public void A_birthdate_after_the_cut_off_rejects_the_row_without_echoing_the_value()
    {
        var rows = Stage(
            "A1,Smith,Ann,Ann,F,WATTLE,ACTV,2027-01-01,07A,07\n" +
            "B2,Jones,Bo,Bo,M,WATTLE,ACTV,2026-12-31,07A,07");

        var issue = Assert.Single(rows[0].Issues);
        Assert.Equal("BIRTHDATE_AFTER_CUT_OFF", issue.Code);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.True(rows[0].IsRejected);
        Assert.Null(rows[0].CompetitionAgeGroup);
        Assert.DoesNotContain("2027", issue.Message);

        // Born on the cut-off day is age 0, not negative.
        Assert.DoesNotContain(rows[1].Issues, i => i.Code == "BIRTHDATE_AFTER_CUT_OFF");
    }

    [Fact]
    public void Missing_required_identity_fields_reject_the_row()
    {
        var rows = Stage(",,,,F,WATTLE,ACTV,2013-01-20,07A,07");
        var codes = rows[0].Issues.Select(i => i.Code).ToList();
        Assert.Contains("MISSING_STKEY", codes);
        Assert.Contains("MISSING_SURNAME", codes);
        Assert.Contains("MISSING_FIRST_NAME", codes);
    }

    [Fact]
    public void Unrecognised_status_is_an_error()
    {
        var rows = Stage("A1,Smith,Ann,Ann,F,WATTLE,WEIRD,2013-01-20,07A,07");
        Assert.Contains(rows[0].Issues, i => i.Code == "UNKNOWN_STATUS");
    }

    [Fact]
    public void Inactive_rows_skip_house_birthdate_gender_and_year_checks()
    {
        var rows = Stage("A1,Smith,Ann,Ann,,,LEFT,,,");
        Assert.Empty(rows[0].Issues);
        Assert.Equal(RowDisposition.Inactive, rows[0].Disposition);
    }

    private static List<StagedRow> Stage(string dataLine) =>
        RosterImportService.MapAndValidate(
            CsvTable.Parse(
                "STKEY,SURNAME,FIRST_NAME,PREF_NAME,GENDER,HOUSE,STATUS,BIRTHDATE,HOME_GROUP,SCHOOL_YEAR\n" +
                dataLine + "\n"),
            Fixtures.Mapping(),
            Fixtures.CompetitionYear,
            Fixtures.Houses);
}
