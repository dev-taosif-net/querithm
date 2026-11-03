using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace Querithm.Core.Import;

/// <summary>
/// Minimal streaming .xlsx reader on System.IO.Compression + XmlReader, no package: workbook sheets,
/// shared and inline strings, numbers, booleans, dates (by the cell style's number format, 1900 or 1904 system) and the
/// cached value of formulas. Rows are returned densely (missing cells are null), one at a time. L8: every part is read
/// within <see cref="ImportLimits"/> — entry count, declared and actual uncompressed size, compression ratio, columns and
/// value length — so a crafted workbook is refused instead of filling memory.
/// </summary>
public sealed class XlsxReader : IDisposable
{
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    readonly ZipArchive _zip;
    readonly List<string> _shared = [];
    readonly List<bool> _dateStyles = [];
    readonly bool _date1904;
    XmlReader? _sheet;
    Stream? _sheetStream;
    readonly char[] _chunk = new char[8192];
    readonly int _maxField = ImportLimits.FieldChars;

    public IReadOnlyList<(string Name, string Path)> Sheets { get; }
    /// <summary>Some cells held formulas; their cached results are imported.</summary>
    public bool HadFormulas { get; private set; }

    public XlsxReader(Stream file)
    {
        _zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (_zip.Entries.Count > ImportLimits.MaxZipEntries)
            throw new ImportLimitException($"The workbook archive has {_zip.Entries.Count:N0} entries; Querithm reads workbooks of at most {ImportLimits.MaxZipEntries:N0}.");
        var rels = ReadRelationships();
        var sheets = new List<(string, string)>();
        using (var r = Open("xl/workbook.xml") ?? throw new InvalidDataException("Not an Excel workbook (xl/workbook.xml is missing)."))
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.LocalName == "workbookPr" && r.GetAttribute("date1904") is "1" or "true") _date1904 = true;
                if (r.LocalName == "sheet" && r.GetAttribute("id", RelNs) is { } rid && rels.TryGetValue(rid, out var target))
                    sheets.Add((r.GetAttribute("name") ?? "Sheet", target));
            }
        Sheets = sheets;
        ReadSharedStrings();
        ReadStyles();
    }

    XmlReader? Open(string path, long cap = ImportLimits.MaxPartBytes)
    {
        var e = _zip.GetEntry(path);
        return e == null ? null : XmlReader.Create(OpenEntry(e, cap), new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
    }

    /// <summary>
    /// An entry's stream within the limits: its declared uncompressed size at most <paramref name="cap"/>, a ratio no
    /// decompression bomb has, and never more bytes than it declared.
    /// </summary>
    static Stream OpenEntry(ZipArchiveEntry e, long cap)
    {
        var what = $"The workbook part {e.FullName}";
        if (e.Length > cap)
            throw new ImportLimitException($"{what} expands to {ImportLimits.Size(e.Length)}, more than the {ImportLimits.Size(cap)} Querithm reads from one part.");
        if (e.Length >= ImportLimits.RatioCheckFromBytes && e.Length / Math.Max(1, e.CompressedLength) > ImportLimits.MaxCompressionRatio)
            throw new ImportLimitException($"{what} expands {e.Length / Math.Max(1, e.CompressedLength):N0} times ({ImportLimits.Size(e.CompressedLength)} to {ImportLimits.Size(e.Length)}): far more than a real sheet, so the file is refused as a decompression bomb.");
        return new DeclaredLengthStream(e.Open(), e.Length, what);
    }

    Dictionary<string, string> ReadRelationships()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var r = Open("xl/_rels/workbook.xml.rels");
        if (r == null) return map;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
            var id = r.GetAttribute("Id");
            var target = r.GetAttribute("Target");
            if (id == null || target == null) continue;
            target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            map[id] = target.Replace("xl/./", "xl/");
        }
        return map;
    }

    void ReadSharedStrings()
    {
        // The shared strings stay in memory for the whole pass: past the limit the workbook is refused with the way out.
        if (_zip.GetEntry("xl/sharedStrings.xml") is { } entry && entry.Length > ImportLimits.MaxSharedStringsBytes)
            throw new ImportLimitException($"The workbook's shared strings expand to {ImportLimits.Size(entry.Length)}, more than the {ImportLimits.Size(ImportLimits.MaxSharedStringsBytes)} Querithm keeps in memory while it reads a workbook. Save the sheet as CSV in your spreadsheet program and import that file instead.");
        using var r = Open("xl/sharedStrings.xml", ImportLimits.MaxSharedStringsBytes);
        if (r == null) return;
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") _shared.Add(ReadRichText(r)); // leaves the reader on </si>
    }

    /// <summary>
    /// Concatenates the &lt;t&gt; runs of an &lt;si&gt; or &lt;is&gt; element (phonetic runs excluded). Leaves the reader
    /// on the element's end tag. ReadContent already moves to the next node, so the loop only calls Read() itself when it
    /// did not read content.
    /// </summary>
    string ReadRichText(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        var depth = r.Depth;
        var sb = new System.Text.StringBuilder();
        var inPhonetic = false;
        r.Read();
        while (!r.EOF && r.Depth > depth)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh") inPhonetic = !r.IsEmptyElement;
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "rPh") inPhonetic = false;
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && !inPhonetic)
            {
                ReadContent(r, sb);
                continue;
            }
            r.Read();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The element's text appended to <paramref name="into"/>, as ReadElementContentAsString reads it (the reader ends
    /// past the end tag), read in chunks and refused past <see cref="ImportLimits.MaxFieldChars"/> instead of building
    /// one string of whatever length the file holds.
    /// </summary>
    void ReadContent(XmlReader r, System.Text.StringBuilder into)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }
        var depth = r.Depth;
        r.Read();
        while (!r.EOF && r.Depth > depth)
        {
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (r.CanReadValueChunk)
                {
                    int n;
                    while ((n = r.ReadValueChunk(_chunk, 0, _chunk.Length)) > 0)
                    {
                        if (into.Length + n > _maxField) throw FieldTooLong();
                        into.Append(_chunk, 0, n);
                    }
                }
                else
                {
                    if (into.Length + r.Value.Length > _maxField) throw FieldTooLong();
                    into.Append(r.Value);
                }
            }
            r.Read();
        }
        if (!r.EOF) r.Read(); // past the end tag
    }

    ImportLimitException FieldTooLong() =>
        new($"A cell of the workbook holds more than {_maxField:N0} characters, the longest value Querithm imports.");

    // Built-in number formats that are dates or times (ECMA-376 §18.8.30).
    static readonly HashSet<int> BuiltInDates = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 30, 36, 45, 46, 47, 50, 57];

    void ReadStyles()
    {
        using var r = Open("xl/styles.xml");
        if (r == null) return;
        var custom = new Dictionary<int, bool>();
        var inXfs = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "numFmt"
                && int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                custom[id] = IsDateFormat(r.GetAttribute("formatCode") ?? "");
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "cellXfs") inXfs = !r.IsEmptyElement;
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs") inXfs = false;
            else if (inXfs && r.NodeType == XmlNodeType.Element && r.LocalName == "xf")
            {
                var fmt = int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0;
                _dateStyles.Add(custom.TryGetValue(fmt, out var isDate) ? isDate : BuiltInDates.Contains(fmt));
            }
        }
    }

    /// <summary>A format code shows a date or time when it has d/m/y/h/s outside quotes and [colour] brackets.</summary>
    public static bool IsDateFormat(string code)
    {
        var inQuote = false;
        var inBracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"') inQuote = !inQuote;
            else if (inQuote) continue;
            else if (ch == '\\') i++;
            else if (ch == '[') inBracket = true;
            else if (ch == ']') inBracket = false;
            else if (!inBracket && "dmyhsDMYHS".Contains(ch)) return true;
        }
        return false;
    }

    /// <summary>Starts reading a sheet (null = the first).</summary>
    public void OpenSheet(string? name)
    {
        CloseSheet();
        var sheet = name == null ? Sheets.FirstOrDefault() : Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sheet.Path == null) throw new InvalidDataException(name == null ? "The workbook has no sheets." : $"The workbook has no sheet named '{name}'.");
        var entry = _zip.GetEntry(sheet.Path) ?? throw new InvalidDataException($"The sheet '{sheet.Name}' is missing from the workbook.");
        _sheetStream = OpenEntry(entry, ImportLimits.MaxSheetBytes);
        _sheet = XmlReader.Create(_sheetStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
    }

    void CloseSheet()
    {
        _sheet?.Dispose();
        _sheetStream?.Dispose();
        _sheet = null;
        _sheetStream = null;
    }

    /// <summary>The next row as text values (null = empty cell), or null at the end of the sheet.</summary>
    public string?[]? ReadRow()
    {
        var r = _sheet ?? throw new InvalidOperationException("OpenSheet first.");
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "row") continue;
            var cells = new List<string?>();
            if (r.IsEmptyElement) return [];
            var depth = r.Depth;
            var next = 0;
            r.Read();
            while (!r.EOF && r.Depth > depth)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                {
                    var col = r.GetAttribute("r") is { } a ? ColumnIndex(a) : next;
                    var type = r.GetAttribute("t");
                    var style = int.TryParse(r.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
                    var value = ReadCell(r, type, style);
                    next = col + 1;
                    // Empty cells (formatting only, often to the sheet's last column) add no width.
                    if (value == null) { r.Read(); continue; }
                    if (col >= ImportLimits.MaxColumns)
                        throw new ImportLimitException($"The sheet has a value in column {col + 1:N0}; Querithm imports at most {ImportLimits.MaxColumns:N0} columns.");
                    while (cells.Count < col) cells.Add(null);
                    if (col < cells.Count) cells[col] = value; else cells.Add(value);
                }
                r.Read();
            }
            return cells.ToArray();
        }
        return null;
    }

    /// <summary>Reads one &lt;c&gt; and leaves the reader on its end tag (or on the element itself when empty).</summary>
    string? ReadCell(XmlReader r, string? type, int style)
    {
        if (r.IsEmptyElement) return null;
        var depth = r.Depth;
        string? v = null;
        string? inline = null;
        r.Read();
        while (!r.EOF && r.Depth > depth)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "v")
            {
                var sb = new System.Text.StringBuilder();
                ReadContent(r, sb);
                v = sb.ToString();
                continue;
            }
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "is") inline = ReadRichText(r);
            else if (r.NodeType == XmlNodeType.Element && r.LocalName == "f") HadFormulas = true;
            r.Read();
        }
        switch (type)
        {
            case "s":
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < _shared.Count ? _shared[i] : null;
            case "inlineStr":
                return inline;
            case "str":
                return v ?? inline;
            case "b":
                return v == "1" ? "true" : v == "0" ? "false" : v;
            case "e":
                return null; // #N/A, #DIV/0! … import as NULL
            case "d":
                return v; // ISO 8601 date cell
            default:
                if (v == null) return inline;
                if (style < _dateStyles.Count && _dateStyles[style] && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                    return SerialToText(serial, _date1904);
                return v;
        }
    }

    /// <summary>Excel serial date → ISO text (date only when there is no time part; 1900 leap-year bug honoured).</summary>
    public static string SerialToText(double serial, bool date1904)
    {
        if (serial < 1 && !date1904) return TimeSpan.FromDays(serial).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        var epoch = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);
        var dt = epoch.AddMilliseconds(Math.Round(serial * 86_400_000));
        return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss.FFF", CultureInfo.InvariantCulture).TrimEnd('.');
    }

    /// <summary>"AB12" → 27 (0-based column); a reference past any sheet's width saturates (no overflow).</summary>
    public static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
            if (n > 1 << 24) return 1 << 24;
        }
        return n - 1;
    }

    public void Dispose()
    {
        CloseSheet();
        _zip.Dispose();
    }
}
