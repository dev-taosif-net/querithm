using System.Globalization;
using Querithm.Core.Execution;

namespace Querithm.Core.Import;

/// <summary>
/// L8: what one import file may hold. Profiling reads every row before anything is loaded, so each limit bounds the
/// memory or the work one crafted file can cost; a file past one is refused with the limit named (§7 Import), never cut
/// short without a word.
/// </summary>
public static class ImportLimits
{
    /// <summary>
    /// Columns of one file: CSV / TSV fields per record, distinct JSON keys, XLSX columns that hold a value. The targets'
    /// own table limits are SQL Server 1,024 (30,000 in a wide table), PostgreSQL 1,600, MySQL 4,096 (InnoDB 1,017),
    /// SQLite 2,000 (its default build), DuckDB far more. A file may be wider than its target table (unmapped columns are
    /// skipped), so this is the widest common limit, MySQL's: 4,096 profilers of 4 KB HyperLogLog registers each, 16 MB.
    /// </summary>
    public const int MaxColumns = 4_096;

    /// <summary>
    /// One value, in characters: 64 Mi (8a review: it was the grid's 1 Mi text limit, which refused real files — a JSON
    /// document or a base64 image of a megabyte). Profiling holds one record at a time, so this is sized by memory: a value
    /// is 128 MB as a .NET string; and past it the servers' own defaults refuse the value anyway (MySQL's
    /// max_allowed_packet is 64 MB). The grid still shows the first <see cref="ResultSetEncoder.TextLimitChars"/> of a
    /// longer value, and so does the import preview.
    /// </summary>
    public const int MaxFieldChars = 64 * 1024 * 1024;

    /// <summary>
    /// One record — a CSV / TSV record with its quoted line breaks, one NDJSON line, one object of a JSON array — in
    /// characters (bytes for a JSON array): twice <see cref="MaxFieldChars"/>, so a record with one value at the limit
    /// still fits beside its other fields (256 MB as .NET strings, held once).
    /// </summary>
    public const int MaxRecordChars = 2 * MaxFieldChars;

    /// <summary>
    /// The limits in force: <see cref="MaxFieldChars"/> / <see cref="MaxRecordChars"/>, or smaller ones a test set for its own
    /// flow (<see cref="Scale"/>; an AsyncLocal, so parallel tests never see each other's).
    /// </summary>
    internal static int FieldChars => Scaled.Value?.Field ?? MaxFieldChars;
    internal static int RecordChars => Scaled.Value?.Record ?? MaxRecordChars;

    static readonly AsyncLocal<(int Field, int Record)?> Scaled = new();

    /// <summary>Test seam: the value and record limits for the calling flow until disposed (the shipped ones need 100s of MB per test).</summary>
    internal static IDisposable Scale(int fieldChars, int recordChars)
    {
        var before = Scaled.Value;
        Scaled.Value = (fieldChars, recordChars);
        return new Restore(() => Scaled.Value = before);
    }

    sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    /// <summary>Characters the preview keeps (the first 200 rows, fewer when they are this long): it stays in memory until the import runs.</summary>
    public const int MaxPreviewChars = 16 * 1024 * 1024;

    /// <summary>Entries of an .xlsx archive (a real workbook has tens).</summary>
    public const int MaxZipEntries = 10_000;

    /// <summary>What one worksheet may expand to, uncompressed (it is streamed, so this bounds the work, not memory).</summary>
    public const long MaxSheetBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// xl/sharedStrings.xml, uncompressed: the shared strings stay in memory for the whole pass (as .NET strings, up to about
    /// twice the XML's text). A sixteenth of the memory the runtime may use, between 128 MB and 512 MB (8a review: a fixed
    /// 128 MB refused real workbooks); a larger workbook is refused with the advice to save the sheet as CSV.
    /// </summary>
    public static readonly long MaxSharedStringsBytes = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16, 128L * 1024 * 1024, 512L * 1024 * 1024);

    /// <summary>The workbook's other parts (workbook, relationships, styles), uncompressed.</summary>
    public const long MaxPartBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Uncompressed / compressed size past which a part of at least <see cref="RatioCheckFromBytes"/> is refused as a
    /// decompression bomb: sheet XML compresses about 5–30 times, a bomb a thousand or more.
    /// </summary>
    public const int MaxCompressionRatio = 200;

    public const long RatioCheckFromBytes = 16L * 1024 * 1024;

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// A file past one of <see cref="ImportLimits"/>: refused at inspection with this message (an "importFile" error, as
/// every file the inspector cannot read; <see cref="InvalidDataException"/> is sealed, so this is an IOException).
/// </summary>
public sealed class ImportLimitException(string message) : IOException(message);

/// <summary>Reads at most the length an archive declared for its entry: an entry that holds more is refused, never read on.</summary>
sealed class DeclaredLengthStream(Stream inner, long declared, string what) : Stream
{
    long _read;

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

    int Count(int n)
    {
        _read += n;
        if (_read > declared)
            throw new ImportLimitException($"{what} holds more data than the workbook declares for it ({ImportLimits.Size(declared)}); the file is damaged or crafted.");
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => declared;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
