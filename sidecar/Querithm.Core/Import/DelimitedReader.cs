using System.Text;

namespace Querithm.Core.Import;

/// <summary>
/// Streaming RFC 4180 reader for CSV/TSV: any single-character delimiter, quoted fields with doubled
/// quotes, newlines inside quotes, CRLF/LF/CR line ends. Bounded memory: one record at a time, and (L8) a record of at
/// most <see cref="ImportLimits.MaxColumns"/> fields, <see cref="ImportLimits.MaxFieldChars"/> characters per field and
/// <see cref="ImportLimits.MaxRecordChars"/> in all — an unterminated quote is refused, not read to the end of the file.
/// </summary>
public sealed class DelimitedReader(TextReader reader, char delimiter, char quote = '"')
{
    readonly StringBuilder _field = new();
    // Read once: the per-character checks below must not look the limits up each time.
    readonly int _maxField = ImportLimits.FieldChars, _maxRecord = ImportLimits.RecordChars;
    int _peek = -2;

    /// <summary>1-based physical line of the record just read (a quoted field can span lines).</summary>
    public long Line { get; private set; }
    long _nextLine = 1;

    int Read()
    {
        if (_peek != -2)
        {
            var p = _peek;
            _peek = -2;
            return p;
        }
        return reader.Read();
    }

    int Peek()
    {
        if (_peek == -2) _peek = reader.Read();
        return _peek;
    }

    /// <summary>The next record, or null at the end. Fields keep their text exactly (no trimming).</summary>
    public string[]? ReadRecord()
    {
        var fields = new List<string>();
        _field.Clear();
        Line = _nextLine;
        var c = Read();
        if (c == -1) return null;
        var inQuotes = false;
        var quoted = false;
        long quoteLine = 0, recordChars = 0;
        void Append(char ch)
        {
            if (_field.Length >= _maxField)
                throw new ImportLimitException(inQuotes
                    ? $"Line {quoteLine:N0}: a quoted field runs past {_maxField:N0} characters, the longest value Querithm imports — is its closing quote missing?"
                    : $"Line {Line:N0}: a field is longer than {_maxField:N0} characters, the longest value Querithm imports.");
            if (++recordChars > _maxRecord)
                throw new ImportLimitException($"Line {Line:N0}: one record is longer than {_maxRecord:N0} characters.");
            _field.Append(ch);
        }
        void EndField()
        {
            if (fields.Count >= ImportLimits.MaxColumns)
                throw new ImportLimitException($"Line {Line:N0} has more than {ImportLimits.MaxColumns:N0} fields; Querithm imports at most {ImportLimits.MaxColumns:N0} columns.");
            fields.Add(_field.ToString());
        }
        while (true)
        {
            if (c == -1)
            {
                EndField();
                return fields.ToArray();
            }
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == quote)
                {
                    if (Peek() == quote)
                    {
                        Read();
                        Append(quote);
                    }
                    else inQuotes = false;
                }
                else
                {
                    if (ch == '\n' || (ch == '\r' && Peek() != '\n')) _nextLine++;
                    Append(ch);
                }
            }
            else if (ch == quote && _field.Length == 0 && !quoted)
            {
                inQuotes = true;
                quoted = true;
                quoteLine = _nextLine;
            }
            else if (ch == delimiter)
            {
                EndField();
                _field.Clear();
                quoted = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && Peek() == '\n') Read();
                _nextLine++;
                EndField();
                return fields.ToArray();
            }
            else Append(ch);
            c = Read();
        }
    }

    /// <summary>Quotes a value for CSV output when it needs it (delimiter, quote, CR/LF, or leading/trailing space).</summary>
    public static string Escape(string value, char delimiter, char quote = '"')
    {
        var needs = value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        foreach (var ch in value)
            if (ch == delimiter || ch == quote || ch == '\r' || ch == '\n') { needs = true; break; }
        return needs ? quote + value.Replace(quote.ToString(), new string(quote, 2)) + quote : value;
    }

    /// <summary>
    /// Picks the delimiter whose per-line field count is most consistent (and above one) over the sample, counting only
    /// characters outside quotes.
    /// </summary>
    public static char DetectDelimiter(string sample, char quote = '"')
    {
        char[] candidates = [',', '\t', ';', '|'];
        var best = ',';
        var bestScore = -1.0;
        var lines = SplitLines(sample, quote).Take(50).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return best;
        foreach (var d in candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, d, quote)).ToList();
            var mode = counts.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First();
            if (mode.Key == 0) continue;
            var score = mode.Count() / (double)counts.Count + mode.Key * 0.001;
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }

    static int CountOutsideQuotes(string line, char d, char quote)
    {
        var n = 0;
        var inQ = false;
        foreach (var ch in line)
        {
            if (ch == quote) inQ = !inQ;
            else if (ch == d && !inQ) n++;
        }
        return n;
    }

    /// <summary>Logical lines of a sample (a newline inside quotes does not end one). The last, possibly cut, line is dropped.</summary>
    static IEnumerable<string> SplitLines(string sample, char quote)
    {
        var sb = new StringBuilder();
        var inQ = false;
        foreach (var ch in sample)
        {
            if (ch == quote) inQ = !inQ;
            if ((ch == '\n' || ch == '\r') && !inQ)
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
    }
}
