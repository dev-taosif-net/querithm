using System.Data.Common;
using System.Globalization;
using System.Numerics;
using System.Text;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Importing files into PostgreSQL: types for new tables, the compatibility check, DDL, and COPY … FROM STDIN
/// (FORMAT csv) for every source format — the server's own input functions parse each value (enums, arrays, json…),
/// after common types are checked here so a bad value rejects its row instead of aborting the COPY.
/// </summary>
public sealed class PostgresImport : IImportDialect
{
    internal static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    public string TypeFor(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "boolean",
        ImportKind.Int32 => "integer",
        ImportKind.Int64 => "bigint",
        ImportKind.Decimal => $"numeric({Math.Clamp(c.Precision, 1, 1000)},{Math.Clamp(c.Scale, 0, 1000)})",
        ImportKind.Double => "double precision",
        ImportKind.Date => "date",
        ImportKind.DateTime => c.Precision is > 0 and <= 6 ? $"timestamp({c.Precision})" : "timestamp",
        ImportKind.DateTimeOffset => "timestamptz",
        ImportKind.Time => "time",
        ImportKind.Uuid => "uuid",
        _ => "text",
    };

    public bool NullUsesDefault => false;

    public bool BlankIsNull(string? type) =>
        type != null && Parse(type).Name is not ("text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "citext" or "name");

    static (string Name, int[] Args) Parse(string? type)
    {
        var t = (type ?? "text").Trim().ToLowerInvariant();
        var open = t.IndexOf('(');
        if (open < 0) return (t, []);
        var close = t.IndexOf(')', open);
        var args = t[(open + 1)..(close < 0 ? t.Length : close)].Split(',', StringSplitOptions.TrimEntries)
            .Select(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n >= 0).ToArray();
        var rest = close < 0 ? "" : t[(close + 1)..].Trim();
        return ((t[..open].Trim() + (rest.Length > 0 ? " " + rest : "")).Trim(), args);
    }

    static (BigInteger, BigInteger)? IntRange(string n) => n switch
    {
        "smallint" or "int2" or "smallserial" => (short.MinValue, short.MaxValue),
        "integer" or "int" or "int4" or "serial" => (int.MinValue, int.MaxValue),
        "bigint" or "int8" or "bigserial" => (long.MinValue, long.MaxValue),
        _ => null,
    };

    static int Digits(string? v) => v == null ? 0 : v.TrimStart('-', '+').TrimStart('0').Length;

    public ImportFit Check(ImportColumnProfile c, SnapColumn target)
    {
        var (name, args) = Parse(target.Type);
        var type = target.Type;
        if (name.EndsWith("[]")) return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server parses array literals when the rows are loaded");
        if (c.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        var fits = new ImportFit(ImportVerdictKind.Fits, $"Fits {type}");
        ImportFit Bad(string what) => new(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not {what}: those rows will be rejected");
        if (IntRange(name) is var (min, max))
        {
            if (c.Kind is not (ImportKind.Int32 or ImportKind.Int64))
                return c.Kind == ImportKind.Decimal && c.Scale == 0 ? new ImportFit(ImportVerdictKind.Widen, $"Whole numbers up to {c.Precision} digits do not fit {type}", $"numeric({c.Precision},0)") : Bad("whole numbers");
            var lo = BigInteger.Parse(c.Min ?? "0", CultureInfo.InvariantCulture);
            var hi = BigInteger.Parse(c.Max ?? "0", CultureInfo.InvariantCulture);
            if (lo >= min && hi <= max) return fits;
            return new ImportFit(ImportVerdictKind.Widen, $"Values range {c.Min}…{c.Max}, outside {type}", hi <= long.MaxValue && lo >= long.MinValue ? "bigint" : "numeric");
        }
        switch (name)
        {
            case "numeric" or "decimal":
            {
                if (c.Kind == ImportKind.Double) return args.Length == 0 ? fits : new ImportFit(ImportVerdictKind.Widen, "Values use exponent notation or are very long", "numeric");
                var (intDigits, scale) = c.Kind switch
                {
                    ImportKind.Int32 or ImportKind.Int64 => (Math.Max(Digits(c.Min), Digits(c.Max)), 0),
                    ImportKind.Decimal => (c.Precision - c.Scale, c.Scale),
                    _ => (-1, -1),
                };
                if (intDigits < 0) return Bad("numbers");
                if (args.Length == 0) return fits;
                var (p, s) = (args[0], args.Length > 1 ? args[1] : 0);
                if (intDigits <= p - s && scale <= s) return fits;
                var ns = Math.Max(s, scale);
                return new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point; {type} allows {p - s} and {s}", $"numeric({Math.Max(p - s, intDigits) + ns},{ns})");
            }
            case "real" or "double precision" or "float4" or "float8":
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double ? fits : Bad("numbers");
            case "boolean" or "bool":
                return c.Kind == ImportKind.Bool || c.Kind == ImportKind.Int32 && c.Min is "0" or "1" && c.Max is "0" or "1" ? fits : Bad("true/false");
            case "date":
                return c.Kind == ImportKind.Date ? fits
                    : c.Kind == ImportKind.DateTime ? new ImportFit(ImportVerdictKind.Widen, "Values have a time of day, which date would drop", "timestamp") : Bad("dates (yyyy-mm-dd)");
            case "timestamp" or "timestamp without time zone":
                if (c.Kind is not (ImportKind.Date or ImportKind.DateTime))
                    return c.Kind == ImportKind.DateTimeOffset ? new ImportFit(ImportVerdictKind.Widen, "Values carry a UTC offset, which timestamp would drop", "timestamptz") : Bad("dates and times");
                return c.Precision > 6 ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; the server rounds to microseconds") : fits;
            case "timestamptz" or "timestamp with time zone":
                if (c.Kind is ImportKind.Date or ImportKind.DateTime) return new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; values without an offset are read in the session time zone");
                return c.Kind == ImportKind.DateTimeOffset ? fits : Bad("dates and times");
            case "time" or "time without time zone":
                return c.Kind == ImportKind.Time ? fits : Bad("times of day");
            case "uuid":
                return c.Kind == ImportKind.Uuid ? fits : Bad("UUIDs");
            case "character varying" or "varchar" or "character" or "char" or "bpchar":
                if (args.Length == 0 || c.MaxLength <= args[0]) return fits;
                return new ImportFit(ImportVerdictKind.Widen, $"Values are up to {c.MaxLength:N0} characters; {type} holds {args[0]}", $"varchar({c.MaxLength})");
            case "text" or "json" or "jsonb" or "xml" or "citext":
                return fits;
            default:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server converts the text when the rows are loaded");
        }
    }

    static string Describe(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "true/false",
        ImportKind.Int32 or ImportKind.Int64 => "whole numbers",
        ImportKind.Decimal => "decimals",
        ImportKind.Double => "numbers",
        ImportKind.Date => "dates",
        ImportKind.DateTime => "dates and times",
        ImportKind.DateTimeOffset => "dates and times with offsets",
        ImportKind.Time => "times of day",
        ImportKind.Uuid => "UUIDs",
        _ => "text",
    };

    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var lines = columns.Select(c => $"    {Q(c.Name)} {c.Type}{(c.NotNull || primaryKey.Contains(c.Name) ? " NOT NULL" : "")}").ToList();
        if (primaryKey.Count > 0) lines.Add($"    CONSTRAINT {Q(table + "_pkey")} PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))})");
        return $"CREATE TABLE {Q(schema)}.{Q(table)} (\n{string.Join(",\n", lines)}\n);";
    }

    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        $"ALTER TABLE {Q(schema)}.{Q(table)} ALTER COLUMN {Q(column.Name)} TYPE {newType};";

    public string AddColumn(string schema, string table, string column, string type) => $"ALTER TABLE {Q(schema)}.{Q(table)} ADD COLUMN {Q(column)} {type};";

    public IBulkLoader? Loader { get; } = new PostgresBulkLoader();

    /// <summary>Why a value is not valid input for a common type (the server checks the rest), or null.</summary>
    public static string? Validate(string? type, string value)
    {
        var (name, args) = Parse(type);
        if (name.EndsWith("[]")) return null;
        var s = value.Trim();
        var shape = ValueClassifier.Classify(s);
        if (IntRange(name) is var (min, max))
            return shape.Kind is ImportKind.Int32 or ImportKind.Int64 && shape.Int >= min && shape.Int <= max ? null : $"'{Clip(value)}' is not a valid {name}";
        switch (name)
        {
            case "numeric" or "decimal" or "real" or "double precision" or "float4" or "float8":
                return shape.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double
                       || s.Equals("NaN", StringComparison.OrdinalIgnoreCase) || s.Equals("Infinity", StringComparison.OrdinalIgnoreCase) || s.Equals("-Infinity", StringComparison.OrdinalIgnoreCase)
                    ? null : $"'{Clip(value)}' is not a number";
            case "boolean" or "bool":
                return s.ToLowerInvariant() is "t" or "f" or "true" or "false" or "y" or "n" or "yes" or "no" or "on" or "off" or "1" or "0" ? null : $"'{Clip(value)}' is not a boolean";
            case "date":
                return shape.Kind == ImportKind.Date ? null : $"'{Clip(value)}' is not a date (yyyy-mm-dd)";
            case "timestamp" or "timestamp without time zone":
                return shape.Kind is ImportKind.Date or ImportKind.DateTime ? null : $"'{Clip(value)}' is not a timestamp";
            case "timestamptz" or "timestamp with time zone":
                return shape.Kind is ImportKind.Date or ImportKind.DateTime or ImportKind.DateTimeOffset ? null : $"'{Clip(value)}' is not a timestamp";
            case "uuid":
                return shape.Kind == ImportKind.Uuid ? null : $"'{Clip(value)}' is not a UUID";
            case "character varying" or "varchar" or "character" or "char" or "bpchar":
                return args.Length > 0 && value.Length > args[0] ? $"{value.Length} characters, the column holds {args[0]}" : null;
            default:
                return null;
        }
    }

    static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
}

/// <summary>COPY … FROM STDIN (FORMAT csv): one COPY for a single transaction, or one per batch (each commits) in batches mode.</summary>
public sealed class PostgresBulkLoader : IBulkLoader
{
    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions o, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)connection;
        if (tx.Mode != BulkTxMode.Own)
        {
            await CopyAsync(conn, target, rows, tx.Mode == BulkTxMode.Batches ? tx.CommitEvery : null, tx, ct).ConfigureAwait(false);
            return;
        }
        var tx2 = PostgresTransactions.Instance;
        await tx2.BeginAsync(conn, ct).ConfigureAwait(false);
        try
        {
            await CopyAsync(conn, target, rows, null, tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(() => tx2.CommitAsync(conn, ct)).ConfigureAwait(false);
        }
        catch
        {
            try { using var cleanup = Cleanup.Start(); await tx2.RollbackAsync(conn, cleanup.Token).ConfigureAwait(false); } catch { /* connection state is re-read afterwards */ }
            throw;
        }
    }

    /// <summary>
    /// One COPY for everything, or one per batch outside a transaction (each COPY commits on its own as it completes, so a
    /// batch's completion runs under <see cref="BulkTransaction.InCommit"/>).
    /// </summary>
    static async Task CopyAsync(NpgsqlConnection conn, BulkTarget target, IBulkRows rows, int? commitEvery, BulkTransaction tx, CancellationToken ct)
    {
        var copy = $"COPY {PostgresImport.Q(target.Schema)}.{PostgresImport.Q(target.Table)} ({string.Join(", ", target.Columns.Select(c => PostgresImport.Q(c.Name)))}) FROM STDIN (FORMAT csv)";
        var bytea = target.Columns.Select(c => PostgresDataEdit.BaseName(c.Type) == "bytea").ToArray();
        long sent = 0;
        var more = true;
        while (more)
        {
            long inChunk = 0;
            var writer = (NpgsqlCopyTextWriter)await conn.BeginTextImportAsync(copy, ct).ConfigureAwait(false);
            var aborted = false;
            try
            {
                var line = new StringBuilder();
                more = false;
                while (rows.Read())
                {
                    var cur = rows.Current;
                    string? bad = null;
                    line.Clear();
                    for (var i = 0; i < target.Columns.Count; i++)
                    {
                        if (i > 0) line.Append(',');
                        var v = i < cur.Count ? cur[i] : null;
                        if (v == null) continue; // unquoted empty = NULL
                        if (PostgresImport.Validate(target.Columns[i].Type, v) is { } why)
                        {
                            bad = $"{target.Columns[i].Name}: {why}";
                            break;
                        }
                        if (bytea[i]) v = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "\\x" + v[2..] : v;
                        // An empty string must be quoted, or COPY reads it as NULL.
                        line.Append(v.Length == 0 ? "\"\"" : Escape(v));
                    }
                    if (bad != null) { rows.Reject(bad); continue; }
                    line.Append('\n');
                    await writer.WriteAsync(line.ToString().AsMemory(), ct).ConfigureAwait(false);
                    sent++;
                    inChunk++;
                    if (sent % 10_000 == 0) rows.Loaded(sent, false);
                    if (commitEvery is { } n && inChunk >= n)
                    {
                        more = true;
                        break;
                    }
                }
            }
            catch
            {
                // Cancelled, a reject limit, or a write error: abort the COPY on this same flow (Cancel must not race
                // a running write), so the server ends it and the caller can roll back.
                aborted = true;
                try { await writer.CancelAsync().ConfigureAwait(false); } catch { /* the COPY already ended */ }
                throw;
            }
            finally
            {
                // Dispose completes the COPY (or raises the server's error); after Cancel it only releases the writer.
                if (aborted) { try { await writer.DisposeAsync().ConfigureAwait(false); } catch { /* aborted */ } }
                else if (commitEvery != null) await tx.CommitAsync(() => writer.DisposeAsync().AsTask()).ConfigureAwait(false);
                else await writer.DisposeAsync().ConfigureAwait(false);
            }
            rows.Loaded(sent, commitEvery != null);
        }
    }

    static string Escape(string v)
    {
        foreach (var ch in v)
            if (ch is ',' or '"' or '\n' or '\r' or '\\') return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
