using System.Text;
using System.Text.Json;
using Querithm.Contract;

namespace Querithm.Core.Import;

/// <summary>Rows of an import file, one at a time (bounded memory). Values are text; null = SQL NULL.</summary>
public interface IRowSource : IDisposable
{
    /// <summary>Column names; a JSON source may add columns while reading (profiling pass only).</summary>
    IReadOnlyList<string> Columns { get; }
    /// <summary>The next data row aligned to <see cref="Columns"/> (shorter rows are padded with null), or null at the end.</summary>
    string?[]? Next();
    /// <summary>1-based data row number of the row just returned (header excluded).</summary>
    long Row { get; }
    long BytesRead { get; }
    long TotalBytes { get; }
    /// <summary>Things worth telling the user (extra fields ignored, formulas imported as values…).</summary>
    IReadOnlyList<string> Notes { get; }
}

/// <summary>How a file is read: detected once by the inspector, reused by the load.</summary>
public sealed record ReadPlan(ImportFormat Format, Encoding Encoding, char Delimiter, bool HasHeader, string? Sheet, string[] NullValues, string[] Columns);

public static class RowSourceFactory
{
    public static readonly string[] DefaultNulls = ["", "NULL"];

    public static IRowSource Open(string path, ReadPlan plan) => plan.Format switch
    {
        ImportFormat.Xlsx => new XlsxSource(path, plan),
        ImportFormat.Json or ImportFormat.Ndjson => new JsonSource(path, plan),
        _ => new DelimitedSource(path, plan),
    };

    /// <summary>Unique, non-empty column names: blanks become ColumnN, repeats get a _2 suffix.</summary>
    public static string[] NameColumns(IReadOnlyList<string?> header, int count)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var n = i < header.Count ? header[i]?.Trim() : null;
            if (string.IsNullOrEmpty(n)) n = $"Column{i + 1}";
            var name = n;
            for (var k = 2; !used.Add(name); k++) name = $"{n}_{k}";
            names[i] = name;
        }
        return names;
    }
}

abstract class FileSource : IRowSource
{
    protected readonly FileStream Stream;
    protected readonly List<string> Cols = [];
    protected readonly List<string> NoteList = [];
    protected readonly HashSet<string> Nulls;

    protected FileSource(string path, ReadPlan plan)
    {
        Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        Nulls = new HashSet<string>(plan.NullValues, StringComparer.Ordinal);
        Cols.AddRange(plan.Columns);
    }

    public IReadOnlyList<string> Columns => Cols;
    public long Row { get; protected set; }
    public long BytesRead => Stream.CanRead ? Stream.Position : Stream.Length;
    public long TotalBytes => Stream.Length;
    public IReadOnlyList<string> Notes => NoteList;
    public abstract string?[]? Next();

    protected string? Value(string? v) => v == null || Nulls.Contains(v) ? null : v;

    public virtual void Dispose() => Stream.Dispose();
}

sealed class DelimitedSource : FileSource
{
    readonly DelimitedReader _reader;
    long _longRows;

    public DelimitedSource(string path, ReadPlan plan) : base(path, plan)
    {
        var text = new StreamReader(Stream, plan.Encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        _reader = new DelimitedReader(text, plan.Delimiter);
        if (plan.HasHeader) _reader.ReadRecord();
    }

    public override string?[]? Next()
    {
        while (true)
        {
            var rec = _reader.ReadRecord();
            if (rec == null)
            {
                if (_longRows > 0) NoteList.Add($"{_longRows:N0} row(s) had more fields than there are columns; the extra fields were not imported.");
                _longRows = 0;
                return null;
            }
            if (rec.Length == 1 && rec[0].Length == 0) continue; // blank line
            Row++;
            if (rec.Length > Cols.Count) _longRows++;
            var row = new string?[Cols.Count];
            for (var i = 0; i < row.Length; i++) row[i] = i < rec.Length ? Value(rec[i]) : null;
            return row;
        }
    }
}

/// <summary>
/// A JSON array of objects, or NDJSON (one object per line). Nested values import as their JSON text. L8: at most
/// <see cref="ImportLimits.MaxColumns"/> distinct keys, <see cref="ImportLimits.MaxRecordChars"/> per object and
/// <see cref="ImportLimits.MaxFieldChars"/> per value.
/// </summary>
sealed class JsonSource : FileSource
{
    readonly IEnumerator<JsonElement> _items;
    readonly bool _fixedColumns;
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    readonly List<JsonDocument> _docs = [];
    readonly int _maxField = ImportLimits.FieldChars, _maxRecord = ImportLimits.RecordChars;
    long _unknownKeys;

    public JsonSource(string path, ReadPlan plan) : base(path, plan)
    {
        _fixedColumns = plan.Columns.Length > 0;
        for (var i = 0; i < Cols.Count; i++) _index[Cols[i]] = i;
        _items = (plan.Format == ImportFormat.Ndjson ? Lines(new StreamReader(Stream, plan.Encoding)) : ArrayItems()).GetEnumerator();
    }

    IEnumerable<JsonElement> Lines(StreamReader r)
    {
        // One line at a time, read in chunks (LF, CRLF or CR ends it, as ReadLine), so a line without an end is refused
        // at the limit instead of read whole.
        var buffer = new char[1 << 16];
        var line = new StringBuilder();
        long number = 0;
        int n = 0, at = 0;
        bool skipLf = false, eof = false;
        while (!eof)
        {
            line.Clear();
            number++;
            while (true)
            {
                if (at == n)
                {
                    n = r.Read(buffer, 0, buffer.Length);
                    at = 0;
                    if (n == 0) { eof = true; break; }
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (buffer[at] == '\n') { at++; continue; }
                }
                var rel = buffer.AsSpan(at, n - at).IndexOfAny('\n', '\r');
                var end = rel < 0 ? n : at + rel;
                if (line.Length + (end - at) > _maxRecord)
                    throw new ImportLimitException($"Line {number:N0} of the NDJSON file is longer than {_maxRecord:N0} characters.");
                line.Append(buffer, at, end - at);
                if (rel < 0) { at = n; continue; }
                skipLf = buffer[end] == '\r';
                at = end + 1;
                break;
            }
            var text = line.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var doc = JsonDocument.Parse(text);
            Track(doc);
            yield return doc.RootElement;
        }
    }

    /// <summary>Elements of the top-level array, parsed one at a time from the stream.</summary>
    IEnumerable<JsonElement> ArrayItems()
    {
        // Utf8JsonReader cannot span an IEnumerable's yields, so read element by element with a buffered scanner.
        var buffer = new byte[1 << 16];
        var data = new MemoryStream();
        var depth = 0;
        var inString = false;
        var escape = false;
        var started = false;
        int n;
        while ((n = Stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var b = buffer[i];
                if (!started)
                {
                    if (b == (byte)'[') started = true;
                    else if (b is (byte)'{') throw new InvalidDataException("The JSON file is one object, not an array of objects (use NDJSON for one object per line).");
                    continue;
                }
                if (depth == 0 && !inString)
                {
                    if (b == (byte)'{' || b == (byte)'[')
                    {
                        depth = 1;
                        data.SetLength(0);
                        data.WriteByte(b);
                    }
                    else if (b == (byte)']') yield break;
                    continue;
                }
                data.WriteByte(b);
                if (data.Length > _maxRecord)
                    throw new ImportLimitException($"Object {Row + 1:N0} of the JSON array is larger than {_maxRecord / (1024 * 1024)} MB.");
                if (inString)
                {
                    if (escape) escape = false;
                    else if (b == (byte)'\\') escape = true;
                    else if (b == (byte)'"') inString = false;
                    continue;
                }
                if (b == (byte)'"') inString = true;
                else if (b == (byte)'{' || b == (byte)'[') depth++;
                else if (b == (byte)'}' || b == (byte)']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var doc = JsonDocument.Parse(data.ToArray());
                        Track(doc);
                        yield return doc.RootElement;
                    }
                }
            }
        }
    }

    void Track(JsonDocument doc)
    {
        // Keep the current element's document alive until the next row.
        foreach (var d in _docs) d.Dispose();
        _docs.Clear();
        _docs.Add(doc);
    }

    public override string?[]? Next()
    {
        if (!_items.MoveNext())
        {
            if (_unknownKeys > 0) NoteList.Add($"{_unknownKeys:N0} value(s) under keys that were not seen while profiling were not imported.");
            _unknownKeys = 0;
            return null;
        }
        Row++;
        var el = _items.Current;
        if (el.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Row {Row} of the JSON file is not an object.");
        var values = new Dictionary<int, string?>();
        foreach (var prop in el.EnumerateObject())
        {
            if (!_index.TryGetValue(prop.Name, out var i))
            {
                if (_fixedColumns) { _unknownKeys++; continue; }
                if (Cols.Count >= ImportLimits.MaxColumns)
                    throw new ImportLimitException($"Row {Row:N0} of the JSON file brings its keys past {ImportLimits.MaxColumns:N0} distinct ones; Querithm imports at most {ImportLimits.MaxColumns:N0} columns.");
                i = Cols.Count;
                Cols.Add(prop.Name);
                _index[prop.Name] = i;
            }
            values[i] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => Value(prop.Value.GetString()),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => prop.Value.GetRawText(),
            };
            if (values[i] is { } value && value.Length > _maxField)
                throw new ImportLimitException($"Row {Row:N0} of the JSON file: the value of {prop.Name} is longer than {_maxField:N0} characters, the longest value Querithm imports.");
        }
        var row = new string?[Cols.Count];
        foreach (var (i, v) in values) row[i] = v;
        return row;
    }

    public override void Dispose()
    {
        foreach (var d in _docs) d.Dispose();
        base.Dispose();
    }
}

sealed class XlsxSource : FileSource
{
    readonly XlsxReader _xlsx;
    bool _noted;

    public XlsxSource(string path, ReadPlan plan) : base(path, plan)
    {
        _xlsx = new XlsxReader(Stream);
        _xlsx.OpenSheet(plan.Sheet);
        if (plan.HasHeader) _xlsx.ReadRow();
    }

    public override string?[]? Next()
    {
        while (true)
        {
            var r = _xlsx.ReadRow();
            if (r == null)
            {
                if (_xlsx.HadFormulas && !_noted)
                {
                    NoteList.Add("Some cells hold formulas: their last calculated values are imported.");
                    _noted = true;
                }
                return null;
            }
            if (r.All(v => v == null)) continue; // empty row
            Row++;
            var row = new string?[Cols.Count];
            for (var i = 0; i < row.Length && i < r.Length; i++) row[i] = Value(r[i]);
            return row;
        }
    }

    public override void Dispose()
    {
        _xlsx.Dispose();
        base.Dispose();
    }
}
