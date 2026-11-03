using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// Imports into DuckDB through its appender (Phase D3, §14 metric 23): batched INSERTs took 24 s for 100k × 10 rows on the
/// fixture, the appender a fraction of it. The appender takes typed values only, so each file value is converted here to
/// the table column's type (a value that does not convert rejects its row, as the other engines' loaders do); columns the
/// file does not fill get their defaults. Tables with a column type the appender cannot write from text (lists, structs,
/// maps, unions, intervals, enums, bit strings) are loaded with one prepared INSERT per row, values cast by DuckDB.
/// Constraint errors end the load; in <see cref="BulkTxMode.Own"/> mode nothing is left behind.
/// </summary>
public sealed class DuckDbBulkLoader : IBulkLoader
{
    delegate bool Convert(IDuckDBAppenderRow row, string text);

    sealed record TableColumn(string Name, string Type, int Source);

    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var c = (DuckDBConnection)connection;
        var columns = await ColumnsAsync(c, target, ct).ConfigureAwait(false);
        var converters = columns.Select(col => col.Source < 0 ? null : Converter(col.Type)).ToArray();
        var typed = columns.Where((col, i) => col.Source >= 0 && converters[i] == null).Select(col => col.Name).ToList() is { Count: 0 };
        var transactions = new DuckDbTransactions();
        var commitEvery = tx.Mode == BulkTxMode.Batches ? Math.Max(1, tx.CommitEvery) : (int?)null;
        long sent = 0;
        var more = true;
        while (more)
        {
            var own = tx.Mode != BulkTxMode.Caller;
            if (own) await transactions.BeginAsync(c, ct).ConfigureAwait(false);
            try
            {
                (sent, more) = typed
                    ? Append(c, target, columns, converters, rows, sent, commitEvery, ct)
                    : await InsertAsync(c, target, columns, rows, sent, commitEvery, ct).ConfigureAwait(false);
                if (own) await tx.CommitAsync(() => transactions.CommitAsync(c, ct)).ConfigureAwait(false);
            }
            catch
            {
                if (own) { try { using var cleanup = Cleanup.Start(); await transactions.RollbackAsync(c, cleanup.Token).ConfigureAwait(false); } catch (DbException) { /* state is re-read afterwards */ } }
                throw;
            }
            rows.Loaded(sent, own);
        }
    }

    /// <summary>The table's columns in table order, each with the file column that fills it (-1: the column's default).</summary>
    static async Task<List<TableColumn>> ColumnsAsync(DuckDBConnection c, BulkTarget target, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, data_type FROM duckdb_columns()
             WHERE database_name = $db AND schema_name = $schema AND table_name = $table ORDER BY column_index
            """;
        var temp = target.Schema == "temp";
        cmd.Parameters.Add(new DuckDBParameter("db", temp ? "temp" : DuckDbMetadata.DatabaseOf(c)));
        cmd.Parameters.Add(new DuckDBParameter("schema", temp ? "main" : target.Schema));
        cmd.Parameters.Add(new DuckDBParameter("table", target.Table));
        var list = new List<TableColumn>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            var source = -1;
            for (var i = 0; i < target.Columns.Count; i++)
                if (string.Equals(target.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) { source = i; break; }
            list.Add(new TableColumn(name, r.GetString(1), source));
        }
        if (list.Count == 0) throw new InvalidOperationException($"{target.Schema}.{target.Table} no longer exists.");
        var missing = target.Columns.Where(tc => list.All(l => !string.Equals(l.Name, tc.Name, StringComparison.OrdinalIgnoreCase))).Select(tc => tc.Name).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"{target.Schema}.{target.Table} has no column {string.Join(", ", missing)}.");
        return list;
    }

    static (long Sent, bool More) Append(DuckDBConnection c, BulkTarget target, List<TableColumn> columns, Convert?[] converters, IBulkRows rows, long sent, int? commitEvery,
        CancellationToken ct)
    {
        using var appender = target.Schema == "temp" ? c.CreateAppender("temp", "main", target.Table) : c.CreateAppender(target.Schema, target.Table);
        long inChunk = 0;
        while (rows.Read())
        {
            if ((sent & 1023) == 0) ct.ThrowIfCancellationRequested();
            var cur = rows.Current;
            // Convert first: a row is appended whole or not at all.
            string? bad = null;
            for (var i = 0; i < columns.Count && bad == null; i++)
            {
                var src = columns[i].Source;
                if (src < 0) continue;
                var v = src < cur.Count ? cur[src] : null;
                if (v != null && !Probe(columns[i].Type, v)) bad = $"{columns[i].Name}: '{Clip(v)}' is not a valid {columns[i].Type}";
            }
            if (bad != null) { rows.Reject(bad); continue; }
            var row = appender.CreateRow();
            for (var i = 0; i < columns.Count; i++)
            {
                var src = columns[i].Source;
                if (src < 0) { row.AppendDefault(); continue; }
                var v = src < cur.Count ? cur[src] : null;
                if (v == null) row.AppendNullValue();
                else converters[i]!(row, v);
            }
            row.EndRow();
            sent++;
            inChunk++;
            if (sent % 10_000 == 0) rows.Loaded(sent, false);
            if (commitEvery is { } n && inChunk >= n) { appender.Close(); return (sent, true); }
        }
        appender.Close();
        return (sent, false);
    }

    /// <summary>One prepared INSERT per row, each value cast by DuckDB to the column's type (columns the appender cannot fill from text).</summary>
    static async Task<(long Sent, bool More)> InsertAsync(DuckDBConnection c, BulkTarget target, List<TableColumn> columns, IBulkRows rows, long sent, int? commitEvery,
        CancellationToken ct)
    {
        var filled = columns.Where(col => col.Source >= 0).ToList();
        var name = target.Schema == "temp" ? $"temp.main.{DuckDbTokenizer.Quote(target.Table)}" : $"{DuckDbTokenizer.Quote(target.Schema)}.{DuckDbTokenizer.Quote(target.Table)}";
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {name} ({string.Join(", ", filled.Select(f => DuckDbTokenizer.Quote(f.Name)))}) VALUES ({string.Join(", ", filled.Select((f, i) => DuckDbTypes.Safe(f.Type) is { } t ? $"CAST($p{i} AS {t})" : $"$p{i}"))})";
        var parameters = filled.Select((_, i) => new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), DBNull.Value)).ToArray();
        cmd.Parameters.AddRange(parameters);
        long inChunk = 0;
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var cur = rows.Current;
            for (var i = 0; i < filled.Count; i++)
                parameters[i].Value = filled[i].Source < cur.Count && cur[filled[i].Source] is { } v ? v : DBNull.Value;
            try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            catch (DuckDBException e) when (e.Message.StartsWith("Conversion Error", StringComparison.Ordinal))
            {
                rows.Reject(e.Message.Split('\n')[0]);
                continue;
            }
            sent++;
            inChunk++;
            if (sent % 10_000 == 0) rows.Loaded(sent, false);
            if (commitEvery is { } n && inChunk >= n) return (sent, true);
        }
        return (sent, false);
    }

    static string Clip(string v) => v.Length > 40 ? v[..40] + "…" : v;

    static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
    ];

    static bool Bool(string s, out bool v)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "true" or "t" or "1" or "yes" or "y" or "on": v = true; return true;
            case "false" or "f" or "0" or "no" or "n" or "off": v = false; return true;
            default: v = false; return false;
        }
    }

    const NumberStyles Int = NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
    const NumberStyles Real = NumberStyles.Float;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Whether the text converts to the column's type (the same parse the converter does).</summary>
    static bool Probe(string type, string s) => DuckDbTypes.BaseName(type) switch
    {
        "BOOLEAN" or "BOOL" or "LOGICAL" => Bool(s, out _),
        "TINYINT" or "INT1" => sbyte.TryParse(s, Int, Inv, out _),
        "SMALLINT" or "INT2" or "SHORT" => short.TryParse(s, Int, Inv, out _),
        "INTEGER" or "INT" or "INT4" or "SIGNED" => int.TryParse(s, Int, Inv, out _),
        "BIGINT" or "INT8" or "LONG" => long.TryParse(s, Int, Inv, out _),
        "HUGEINT" or "INT128" => BigInteger.TryParse(s, Int, Inv, out var h) && BigInteger.Abs(h) < BigInteger.One << 127,
        "UTINYINT" => byte.TryParse(s, Int, Inv, out _),
        "USMALLINT" => ushort.TryParse(s, Int, Inv, out _),
        "UINTEGER" => uint.TryParse(s, Int, Inv, out _),
        "UBIGINT" => ulong.TryParse(s, Int, Inv, out _),
        "FLOAT" or "REAL" or "FLOAT4" => float.TryParse(s, Real, Inv, out _),
        "DOUBLE" or "FLOAT8" => double.TryParse(s, Real, Inv, out _),
        "DECIMAL" or "NUMERIC" => decimal.TryParse(s, Real, Inv, out var d) && Fits(d, type),
        "DATE" => DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out _),
        "TIME" => TimeOnly.TryParse(s, Inv, DateTimeStyles.None, out _),
        "TIMESTAMP" or "DATETIME" or "TIMESTAMP_US" or "TIMESTAMP_MS" or "TIMESTAMP_S" or "TIMESTAMP_NS" =>
            DateTime.TryParseExact(s.Trim(), TimestampFormats, Inv, DateTimeStyles.None, out _),
        "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" => DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out _),
        "UUID" => Guid.TryParse(s, out _),
        "BLOB" or "BYTEA" => !s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || IsHex(s.AsSpan(2)),
        _ => true,
    };

    static bool IsHex(ReadOnlySpan<char> s) => s.Length % 2 == 0 && !s.ContainsAnyExcept("0123456789abcdefABCDEF");

    static bool Fits(decimal d, string type)
    {
        if (DuckDbTypes.DecimalArgs(type) is not var (p, s)) return true;
        var abs = Math.Abs(d);
        var scaled = decimal.Round(abs, s);
        if (scaled != abs) return false;
        var digits = decimal.Truncate(abs).ToString(Inv).TrimStart('0').Length;
        return digits <= p - s;
    }

    /// <summary>The typed append for a column type, or null when the appender cannot fill it from text.</summary>
    static Convert? Converter(string type) => DuckDbTypes.BaseName(type) switch
    {
        "BOOLEAN" or "BOOL" or "LOGICAL" => (r, s) => { Bool(s, out var v); r.AppendValue(v); return true; },
        "TINYINT" or "INT1" => (r, s) => { r.AppendValue((sbyte?)sbyte.Parse(s, Int, Inv)); return true; },
        "SMALLINT" or "INT2" or "SHORT" => (r, s) => { r.AppendValue((short?)short.Parse(s, Int, Inv)); return true; },
        "INTEGER" or "INT" or "INT4" or "SIGNED" => (r, s) => { r.AppendValue((int?)int.Parse(s, Int, Inv)); return true; },
        "BIGINT" or "INT8" or "LONG" => (r, s) => { r.AppendValue((long?)long.Parse(s, Int, Inv)); return true; },
        "HUGEINT" or "INT128" => (r, s) => { r.AppendValue((BigInteger?)BigInteger.Parse(s, Int, Inv)); return true; },
        "UTINYINT" => (r, s) => { r.AppendValue((byte?)byte.Parse(s, Int, Inv)); return true; },
        "USMALLINT" => (r, s) => { r.AppendValue((ushort?)ushort.Parse(s, Int, Inv)); return true; },
        "UINTEGER" => (r, s) => { r.AppendValue((uint?)uint.Parse(s, Int, Inv)); return true; },
        "UBIGINT" => (r, s) => { r.AppendValue((ulong?)ulong.Parse(s, Int, Inv)); return true; },
        "FLOAT" or "REAL" or "FLOAT4" => (r, s) => { r.AppendValue((float?)float.Parse(s, Real, Inv)); return true; },
        "DOUBLE" or "FLOAT8" => (r, s) => { r.AppendValue((double?)double.Parse(s, Real, Inv)); return true; },
        "DECIMAL" or "NUMERIC" => (r, s) => { r.AppendValue((decimal?)decimal.Parse(s, Real, Inv)); return true; },
        "DATE" => (r, s) => { r.AppendValue((DateOnly?)DateOnly.ParseExact(s.Trim(), "yyyy-MM-dd", Inv)); return true; },
        "TIME" => (r, s) => { r.AppendValue((TimeOnly?)TimeOnly.Parse(s, Inv)); return true; },
        "TIMESTAMP" or "DATETIME" or "TIMESTAMP_US" or "TIMESTAMP_MS" or "TIMESTAMP_S" or "TIMESTAMP_NS" =>
            (r, s) => { r.AppendValue((DateTime?)DateTime.ParseExact(s.Trim(), TimestampFormats, Inv, DateTimeStyles.None)); return true; },
        "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" => (r, s) => { r.AppendValue((DateTimeOffset?)DateTimeOffset.Parse(s, Inv, DateTimeStyles.AssumeLocal)); return true; },
        "UUID" => (r, s) => { r.AppendValue((Guid?)Guid.Parse(s)); return true; },
        "VARCHAR" or "TEXT" or "STRING" or "CHAR" or "BPCHAR" or "JSON" => (r, s) => { r.AppendValue(s); return true; },
        "BLOB" or "BYTEA" => (r, s) =>
        {
            r.AppendValue(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Convert.FromHexString(s.AsSpan(2)) : Encoding.UTF8.GetBytes(s));
            return true;
        },
        _ => null,
    };
}
