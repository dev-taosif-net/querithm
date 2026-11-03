using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Import;

/// <summary>What the inspector learned: how to read the file, every column's profile, and the first rows for the preview.</summary>
public sealed record Inspection(string Id, string FilePath, ReadPlan Plan, ImportColumnProfile[] Columns, long RowCount, List<string?[]> Preview, string[] Sheets, string[] Notes);

/// <summary>
/// Import step 1: detects format, encoding, delimiter and header from the first 64 KB, then profiles the
/// whole file in one streaming pass — a long value at row 48,392 is found here, not by the server mid-import. A file past
/// one of <see cref="ImportLimits"/> is refused here with the limit it crossed (L8).
/// </summary>
public static class FileInspector
{
    public const int PreviewRows = 200;
    const int SampleBytes = 64 * 1024;

    public static ImportFormat FormatOf(string path, ImportFormat requested)
    {
        if (requested != ImportFormat.Auto) return requested;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => ImportFormat.Xlsx,
            ".json" => ImportFormat.Json,
            ".ndjson" or ".jsonl" => ImportFormat.Ndjson,
            ".tsv" or ".tab" => ImportFormat.Tsv,
            _ => ImportFormat.Csv,
        };
    }

    /// <summary>BOM first; else strict UTF-8 if the sample decodes; else Latin-1 (every byte is a character).</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> sample, string? requested)
    {
        switch (requested?.ToLowerInvariant())
        {
            case "utf-8" or "utf8": return new UTF8Encoding(false);
            case "utf-16le" or "utf-16": return new UnicodeEncoding(false, false);
            case "utf-16be": return new UnicodeEncoding(true, false);
            case "latin1" or "iso-8859-1": return Encoding.Latin1;
        }
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return new UTF8Encoding(false);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return new UnicodeEncoding(false, false);
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return new UnicodeEncoding(true, false);
        // The sample may end inside a multi-byte character: decode without flushing, so only an incomplete final
        // sequence is tolerated, never an invalid byte.
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder().GetCharCount(sample, flush: false);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    public static string EncodingName(Encoding e) => e switch
    {
        UTF8Encoding => "utf-8",
        UnicodeEncoding u when u.GetPreamble() is [0xFE, 0xFF] || u.CodePage == 1201 => "utf-16be",
        UnicodeEncoding => "utf-16le",
        _ => "latin1",
    };

    /// <summary>
    /// A first row is a header when none of its values is a number, date or other typed value and it has no blanks;
    /// a typed first row is data. (All-text files: a header is assumed, as nearly every export has one.)
    /// </summary>
    public static bool LooksLikeHeader(IReadOnlyList<string?> first, IReadOnlyList<string?>? second)
    {
        if (first.Count == 0) return false;
        if (first.Any(v => string.IsNullOrWhiteSpace(v))) return false;
        if (first.Any(v => ValueClassifier.Classify(v!).Kind != ImportKind.Text)) return false;
        if (first.Distinct(StringComparer.OrdinalIgnoreCase).Count() != first.Count) return false;
        return true;
    }

    public static Inspection Inspect(string id, string path, ImportReadOptions? o, Action<long, long, long> progress, CancellationToken ct)
    {
        o ??= new ImportReadOptions();
        var format = FormatOf(path, o.Format);
        var nulls = o.NullValues ?? RowSourceFactory.DefaultNulls;
        var notes = new List<string>();
        var sheets = Array.Empty<string>();
        ReadPlan plan;
        if (format == ImportFormat.Xlsx)
        {
            using var fs = File.OpenRead(path);
            using var x = new XlsxReader(fs);
            sheets = x.Sheets.Select(s => s.Name).ToArray();
            var sheet = o.Sheet ?? sheets.FirstOrDefault();
            x.OpenSheet(sheet);
            string?[]? first = x.ReadRow();
            while (first != null && first.All(v => v == null)) first = x.ReadRow();
            var second = x.ReadRow();
            var header = o.HasHeader ?? (first != null && LooksLikeHeader(first, second));
            var width = Math.Max(first?.Length ?? 0, second?.Length ?? 0);
            plan = new ReadPlan(format, Encoding.UTF8, ',', header, sheet, nulls, RowSourceFactory.NameColumns(header && first != null ? first : [], width));
        }
        else
        {
            var sample = new byte[Math.Min(SampleBytes, new FileInfo(path).Length)];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(sample);
            var encoding = DetectEncoding(sample, o.Encoding);
            if (format is ImportFormat.Json or ImportFormat.Ndjson)
            {
                var text = encoding.GetString(sample).TrimStart('﻿').TrimStart();
                if (format == ImportFormat.Json && !text.StartsWith('[') && text.StartsWith('{')) format = ImportFormat.Ndjson;
                plan = new ReadPlan(format, encoding, ',', false, null, nulls, []);
            }
            else
            {
                var text = encoding.GetString(sample).TrimStart('﻿');
                var delimiter = o.Delimiter is { Length: > 0 } d ? (d == "\\t" ? '\t' : d[0]) : format == ImportFormat.Tsv ? '\t' : DelimitedReader.DetectDelimiter(text);
                var r = new DelimitedReader(new StringReader(text), delimiter);
                var first = r.ReadRecord();
                var second = r.ReadRecord();
                var header = o.HasHeader ?? (first != null && LooksLikeHeader(first, second));
                var width = Math.Max(first?.Length ?? 0, second?.Length ?? 0);
                plan = new ReadPlan(format == ImportFormat.Tsv && delimiter != '\t' ? ImportFormat.Csv : format, encoding, delimiter, header, null, nulls,
                    RowSourceFactory.NameColumns(header && first != null ? first : [], width));
            }
        }

        if (plan.Columns.Length > ImportLimits.MaxColumns)
            throw new ImportLimitException($"The file has {plan.Columns.Length:N0} columns; Querithm imports at most {ImportLimits.MaxColumns:N0}.");
        var profilers = plan.Columns.Select((c, i) => new ColumnProfiler(c, i)).ToList();
        var preview = new List<string?[]>();
        long previewChars = 0;
        using var src = RowSourceFactory.Open(path, plan);
        long rows = 0;
        var lastTick = Environment.TickCount64;
        while (src.Next() is { } row)
        {
            ct.ThrowIfCancellationRequested();
            // A JSON key first seen now: earlier rows had no value for it.
            while (profilers.Count < src.Columns.Count)
            {
                var p = new ColumnProfiler(src.Columns[profilers.Count], profilers.Count);
                p.AddNulls(rows);
                profilers.Add(p);
            }
            rows++;
            for (var i = 0; i < profilers.Count; i++) profilers[i].Add(i < row.Length ? row[i] : null, src.Row);
            if (preview.Count < PreviewRows && (preview.Count == 0 || previewChars < ImportLimits.MaxPreviewChars))
            {
                // A value the grid cannot show whole (import accepts longer ones, 8a review) is previewed as the grid would show it.
                var shown = row;
                for (var i = 0; i < row.Length; i++)
                    if (row[i] is { Length: > Execution.ResultSetEncoder.TextLimitChars } longValue)
                    {
                        if (ReferenceEquals(shown, row)) shown = (string?[])row.Clone();
                        shown[i] = longValue[..Execution.ResultSetEncoder.TextLimitChars];
                    }
                foreach (var v in shown) previewChars += v?.Length ?? 0;
                preview.Add(shown);
            }
            if (Environment.TickCount64 - lastTick >= 250)
            {
                lastTick = Environment.TickCount64;
                progress(rows, src.BytesRead, src.TotalBytes);
            }
        }
        progress(rows, src.TotalBytes, src.TotalBytes);
        notes.AddRange(src.Notes);
        var columns = src.Columns.ToArray();
        for (var i = 0; i < preview.Count; i++)
            if (preview[i].Length < columns.Length) preview[i] = [.. preview[i], .. new string?[columns.Length - preview[i].Length]];
        if (rows == 0) notes.Add("The file has no data rows.");
        return new Inspection(id, path, plan with { Columns = columns }, profilers.Select(p => p.Build()).ToArray(), rows, preview, sheets, notes.ToArray());
    }
}
