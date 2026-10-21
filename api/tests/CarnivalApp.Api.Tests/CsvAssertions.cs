using System.Net;
using System.Text;
using Xunit;

namespace CarnivalApp.Api.Tests;

/// <summary>
/// Reading a controlled export back the way Excel does (ADR-0004 tier 1): BOM
/// asserted and stripped, CRLF lines, RFC 4180 fields — the last of which is
/// what proves a column is ABSENT rather than merely unread.
/// </summary>
public static class CsvAssertions
{
    public static async Task<string[]> CsvLinesAsync(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        // Excel decodes a BOM-less file as the machine's ANSI codepage.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    public static List<string> CsvFields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields;
    }
}
