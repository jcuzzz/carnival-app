using System.Text;

namespace CarnivalApp.Api.Imports;

/// <summary>
/// Minimal RFC 4180 reader; no third-party CSV dependency, so the surface stays auditable.
/// </summary>
public static class CsvTable
{
    static CsvTable()
    {
        // Windows-1252 support outside Windows / trimmed runtimes.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Rows are padded to header width.</summary>
    public sealed record Parsed(IReadOnlyList<string> Header, IReadOnlyList<string[]> Rows)
    {
        public int IndexOf(string column)
        {
            for (var i = 0; i < Header.Count; i++)
                if (string.Equals(Header[i], column, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }

    public static Parsed Parse(Stream stream)
    {
        var bytes = ReadAll(stream);
        return Parse(Decode(bytes));
    }

    // CASES21 exports arrive as UTF-8 with BOM or Windows-1252.
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static Parsed Parse(string text)
    {
        var records = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var i = 0;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }
        void EndRecord()
        {
            EndField();
            // Skip fully blank trailing lines.
            if (record.Count > 1 || record[0].Length > 0)
                records.Add(record.ToArray());
            record.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }
            switch (c)
            {
                case '"' when field.Length == 0: inQuotes = true; i++; break;
                case ',': EndField(); i++; break;
                case '\r': if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRecord(); i++; break;
                case '\n': EndRecord(); i++; break;
                default: field.Append(c); i++; break;
            }
        }
        if (field.Length > 0 || record.Count > 0) EndRecord();

        if (records.Count == 0)
            throw new InvalidDataException("CSV file contains no header row.");

        var header = records[0].Select(h => h.Trim()).ToArray();
        var rows = new List<string[]>(records.Count - 1);
        foreach (var raw in records.Skip(1))
        {
            var row = new string[header.Length];
            for (var c = 0; c < header.Length; c++)
                row[c] = c < raw.Length ? raw[c].Trim() : string.Empty;
            rows.Add(row);
        }
        return new Parsed(header, rows);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
