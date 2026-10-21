using System.Globalization;
using System.Text;

namespace CarnivalApp.Api.Reporting;

/// <summary>
/// CRLF and a UTF-8 BOM for Excel, which otherwise decodes
/// the file as the ANSI codepage and mangles non-ASCII names.
/// </summary>
public static class Csv
{
    private const string LineEnding = "\r\n";

    private static readonly char[] MustQuote = [',', '"', '\r', '\n'];

    private static readonly char[] FormulaLead = ['=', '+', '-', '@', '\t', '\r'];

    public static byte[] Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var text = new StringBuilder();
        Append(text, header);
        foreach (var row in rows) Append(text, row);
        // GetBytes never emits the preamble, so the BOM is prepended by hand.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static void Append(StringBuilder text, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(Quote(fields[i]));
        }
        text.Append(LineEnding);
    }

    private static string Quote(string? field)
    {
        var value = Neutralise(field ?? "");
        return value.IndexOfAny(MustQuote) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"")}\"";
    }

    // Spreadsheets evaluate a field opening with =/+/-/@/tab/CR; an apostrophe forces text. Numbers
    // are left alone: a bonus adjustment may legitimately be negative.
    private static string Neutralise(string value) =>
        value.Length > 0
        && Array.IndexOf(FormulaLead, value[0]) >= 0
        && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
            ? "'" + value
            : value;
}
