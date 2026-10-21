using System.Text;
using CarnivalApp.Api.Imports;
using Xunit;

namespace CarnivalApp.Api.Tests;

public class CsvTableTests
{
    [Fact]
    public void Decodes_utf8_with_bom()
    {
        var withBom = Encoding.UTF8.GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes("STKEY,SURNAME\nA1,Nguyễn\n"))
            .ToArray();

        var parsed = CsvTable.Parse(CsvTable.Decode(withBom));
        Assert.Equal("STKEY", parsed.Header[0]); // BOM must not stick to the first header
        Assert.Equal("Nguyễn", parsed.Rows[0][1]);
    }

    [Fact]
    public void Decodes_cp1252_when_not_valid_utf8()
    {
        // 0xE9 = é and 0x92 = ’ in Windows-1252; the sequence is invalid UTF-8.
        var text = "STKEY,SURNAME\nA1,Beauché-O’Brien\n";
        var cp1252 = System.Text.Encoding.GetEncoding(1252).GetBytes(text);

        var parsed = CsvTable.Parse(CsvTable.Decode(cp1252));
        Assert.Equal("Beauché-O’Brien", parsed.Rows[0][1]);
    }

    [Fact]
    public void Parses_quoted_fields_with_commas_and_crlf()
    {
        var parsed = CsvTable.Parse("A,B,C\r\n\"Smith, Jones\",\"say \"\"hi\"\"\",x\r\n");
        Assert.Equal(["Smith, Jones", "say \"hi\"", "x"], parsed.Rows[0]);
    }

    [Fact]
    public void Pads_short_rows_and_skips_blank_lines()
    {
        var parsed = CsvTable.Parse("A,B,C\n1,2\n\n3,4,5\n");
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal("", parsed.Rows[0][2]);
    }

    [Fact]
    public void Parses_full_clean_fixture()
    {
        var parsed = Fixtures.ParseFixture("ST_CLEAN.csv");
        Assert.Equal(5198, parsed.Rows.Count);
        Assert.True(parsed.IndexOf("stkey") == 0); // case-insensitive header lookup
    }

    [Fact]
    public void Parses_wide_synth_fixture()
    {
        var parsed = Fixtures.ParseFixture("ST_SYNTH.csv");
        Assert.True(parsed.IndexOf("BIRTHDATE") > 0);
        Assert.True(parsed.Rows.Count > 100);
    }
}
