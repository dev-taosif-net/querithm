using System.Data.Common;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Import into Oracle (D4 S10): a parameterized INSERT with ODP.NET array binding (<see cref="OracleArrayLoader"/>) inside
/// the transaction rules of import.run. Native bulk copy (OracleBulkCopy) is not used: it runs on direct-path loads that
/// commit or lock the table on their own terms (it has no part in an open transaction, a direct-path load leaves the
/// table unreadable to the session until it commits, and it cannot be interrupted at a row) — it stays out until its
/// transaction and cancel behaviour is verified (ARCHITECTURE §16). New tables get types from the file's profile; a
/// widening is offered as MODIFY.
/// </summary>
public sealed class OracleImport : IImportDialect
{
    static string Q(string s) => OracleIdentifiers.Quote(s);

    /// <summary>Text up to 1,000 characters fits VARCHAR2(n CHAR) in 4,000 bytes at 4 bytes a character (AL32UTF8); longer is CLOB.</summary>
    internal const int MaxVarcharChars = 1000;

    public string TypeFor(ImportColumnProfile c) => c.Kind switch
    {
        ImportKind.Bool => "NUMBER(1)", // BOOLEAN only since 23ai: a 0 / 1 flag every supported release reads
        ImportKind.Int32 => "NUMBER(10)",
        ImportKind.Int64 => "NUMBER(19)",
        ImportKind.Decimal => c.Precision <= 38 && c.Scale <= c.Precision ? $"NUMBER({Math.Max(1, c.Precision)},{c.Scale})" : "NUMBER",
        ImportKind.Double => "BINARY_DOUBLE",
        ImportKind.Date => "DATE",
        ImportKind.DateTime => c.Precision is > 0 ? $"TIMESTAMP({Math.Min(9, c.Precision)})" : "DATE",
        ImportKind.DateTimeOffset => $"TIMESTAMP({Math.Clamp(c.Precision, 0, 9)}) WITH TIME ZONE",
        ImportKind.Time => "VARCHAR2(32 CHAR)", // no time-of-day type: the text is kept as it is
        ImportKind.Uuid => "CHAR(36)",
        _ => TextType(c.MaxLength),
    };

    static string TextType(int maxLength) => maxLength switch
    {
        <= 255 => "VARCHAR2(255 CHAR)",
        <= MaxVarcharChars => $"VARCHAR2({MaxVarcharChars} CHAR)",
        _ => "CLOB",
    };

    public bool NullUsesDefault => false;

    /// <summary>A blank cell is NULL except in a character column, where the blanks are the value.</summary>
    public bool BlankIsNull(string? type) => !OracleColumnType.Parse(type).IsText && OracleColumnType.Parse(type).Kind != OracleValueKind.Clob;

    public ImportFit Check(ImportColumnProfile c, SnapColumn target)
    {
        var t = OracleColumnType.Parse(target.Type);
        var type = target.Type;
        if (c.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        var fits = new ImportFit(ImportVerdictKind.Fits, $"Fits {type}");
        ImportFit Bad(string what) => new(ImportVerdictKind.Mismatch, $"Values are {Describe(c)}, not {what}: those rows will be rejected");
        switch (t.Kind)
        {
            case OracleValueKind.Number:
            {
                if (c.Kind == ImportKind.Bool) return t is { Precision: 1, Scale: 0 } ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type} as 1 / 0") : Bad("numbers");
                var (intDigits, scale) = c.Kind switch
                {
                    ImportKind.Int32 or ImportKind.Int64 => (Math.Max(Digits(c.Min), Digits(c.Max)), 0),
                    ImportKind.Decimal => (c.Precision - c.Scale, c.Scale),
                    ImportKind.Double => (-2, -2),
                    _ => (-1, -1),
                };
                if (intDigits == -1) return Bad("numbers");
                if (t.Precision is not { } p || t.Name == "FLOAT")
                    return intDigits == -2 ? new ImportFit(ImportVerdictKind.Fits, $"Values use exponent notation; {type} keeps 38 significant digits") : fits;
                var s = t.Scale ?? 0;
                if (intDigits == -2) return new ImportFit(ImportVerdictKind.Fits, $"Values use exponent notation; {type} rounds them to {s} decimal place(s) and rejects rows past {p - s} digit(s) before the point");
                if (intDigits <= p - s && scale <= Math.Max(0, s)) return fits;
                if (intDigits <= p - s) return new ImportFit(ImportVerdictKind.Fits, $"Values have {scale} digit(s) after the point; {type} rounds them to {Math.Max(0, s)}");
                var ns = Math.Max(Math.Max(0, s), scale);
                var np = Math.Max(p - s, intDigits) + ns;
                return np <= 38
                    ? new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point; {type} allows {p - s} and {Math.Max(0, s)}", $"NUMBER({np},{ns})")
                    : new ImportFit(ImportVerdictKind.Widen, $"Values need {intDigits} digit(s) before and {scale} after the point, more than NUMBER(p,s) holds (38 digits)", "NUMBER");
            }
            case OracleValueKind.BinaryFloat or OracleValueKind.BinaryDouble:
                return c.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double
                    ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type} (binary floating point: decimals are rounded to the nearest binary value)") : Bad("numbers");
            case OracleValueKind.Date:
                return c.Kind is ImportKind.Date ? fits
                    : c.Kind is ImportKind.DateTime
                        ? c.Precision > 0 ? new ImportFit(ImportVerdictKind.Widen, "Values have fractions of a second, which DATE does not keep: those rows will be rejected", $"TIMESTAMP({Math.Min(9, c.Precision)})") : fits
                        : Bad("dates (yyyy-mm-dd hh:mi:ss)");
            case OracleValueKind.Timestamp or OracleValueKind.TimestampLtz:
                if (c.Kind is not (ImportKind.Date or ImportKind.DateTime)) return Bad("dates and times without an offset");
                if (c.Kind == ImportKind.DateTime && c.Precision > (t.Scale ?? 6))
                    return new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; the server rounds to {(t.Scale is 0 ? "whole seconds" : $"{t.Scale ?? 6} fractional digit(s)")}");
                return t.Kind == OracleValueKind.TimestampLtz ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type}, read in the session time zone") : fits;
            case OracleValueKind.TimestampTz:
                return c.Kind == ImportKind.DateTimeOffset ? fits
                    : c.Kind is ImportKind.Date or ImportKind.DateTime ? new ImportFit(ImportVerdictKind.Fits, $"Fits {type}; values without an offset take the session time zone")
                    : Bad("dates and times");
            case OracleValueKind.IntervalYm or OracleValueKind.IntervalDs:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: each value must be written as Oracle writes intervals, or the row is rejected");
            case OracleValueKind.Boolean:
                return c.Kind == ImportKind.Bool || c.Kind is ImportKind.Int32 && c.Min is "0" or "1" && c.Max is "0" or "1" ? fits : Bad("true / false");
            case OracleValueKind.Char or OracleValueKind.NChar or OracleValueKind.VarChar or OracleValueKind.NVarChar:
            {
                var n = t.Length ?? 4000;
                if (c.MaxLength <= n) return t.CharSemantics || t.IsNational ? fits : new ImportFit(ImportVerdictKind.Fits, $"Fits {type} while each value is at most {n:N0} bytes in UTF-8; a longer one is rejected");
                return c.MaxLength <= MaxVarcharChars
                    ? new ImportFit(ImportVerdictKind.Widen, $"Values are up to {c.MaxLength:N0} characters; {type} holds {n:N0}", $"{(t.IsNational ? "NVARCHAR2" : "VARCHAR2")}({c.MaxLength}{(t.IsNational ? "" : " CHAR")})")
                    : new ImportFit(ImportVerdictKind.Mismatch, $"Values are up to {c.MaxLength:N0} characters, more than {type} or any VARCHAR2 holds (4,000 bytes): load them into a CLOB column");
            }
            case OracleValueKind.Clob:
                return fits;
            case OracleValueKind.Raw:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: each value must be 0x followed by hex digits, or the row is rejected");
            case OracleValueKind.RowId:
                return new ImportFit(ImportVerdictKind.Fits, $"{type}: the server checks each value is a valid ROWID");
            default:
                return new ImportFit(ImportVerdictKind.Mismatch, $"{type} values cannot be loaded from text");
        }
    }

    static int Digits(string? v) => v == null ? 0 : v.TrimStart('-', '+').TrimStart('0').Length;

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

    /// <summary>A CLOB cannot be a key column: a key column gets VARCHAR2(1000 CHAR) (4,000 bytes, under the index key limit of an 8 KB block).</summary>
    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var lines = columns.Select(c =>
        {
            var key = primaryKey.Contains(c.Name);
            var type = key && OracleColumnType.Parse(c.Type).Kind == OracleValueKind.Clob ? $"VARCHAR2({MaxVarcharChars} CHAR)" : c.Type;
            return $"    {Q(c.Name)} {type}{(c.NotNull || key ? " NOT NULL" : "")}";
        }).ToList();
        if (primaryKey.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", primaryKey.Select(Q))})");
        return $"CREATE TABLE {Q(schema)}.{Q(table)} (\n{string.Join(",\n", lines)}\n);";
    }

    /// <summary>MODIFY keeps the column's NULL / NOT NULL and default. VARCHAR2 to CLOB is not a MODIFY in Oracle (ORA-22858): the comment says so.</summary>
    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        (OracleColumnType.Parse(newType).Kind == OracleValueKind.Clob && OracleColumnType.Parse(column.Type).Kind != OracleValueKind.Clob
            ? "-- Oracle cannot MODIFY a column to CLOB (ORA-22858): add a CLOB column, copy the values over, then drop the old column.\n"
            : "") +
        $"ALTER TABLE {Q(schema)}.{Q(table)} MODIFY ({Q(column.Name)} {newType});";

    public string AddColumn(string schema, string table, string column, string type) => $"ALTER TABLE {Q(schema)}.{Q(table)} ADD ({Q(column)} {type});";

    public IBulkLoader? Loader { get; } = new OracleArrayLoader(new OracleTransactions());
}

/// <summary>
/// Oracle's import path: one INSERT with ODP.NET array binding (ArrayBindCount rows per execute, each column's values
/// converted once for its parsed type; a value the type cannot take rejects its row before anything is sent) — chosen over
/// multi-row INSERT ALL by measurement (<see cref="MaxRows"/>: about 10× faster on the #23 rows). Up to
/// <see cref="MaxRows"/> rows or about 4 MB of values per execute. Own: one transaction, all rows or none; Caller: inside
/// the user's transaction (the service holds a savepoint); Batches: a transaction committed every
/// <see cref="BulkTransaction.CommitEvery"/> rows. A server error (a key violation, a value the server refuses) fails the
/// whole execute: Own and Caller then leave nothing of the import, Batches keeps the batches committed before it. Cancel
/// breaks the running execute (OracleCommand.Cancel), as for a query.
/// </summary>
public sealed class OracleArrayLoader(ITransactionControl transactions) : IBulkLoader
{
    /// <summary>
    /// Rows per execute, measured (D4 S10, the #23 CSV's 100k × 10 rows already converted, one transaction, median of 3):
    /// 668 ms at 1,000 rows, 449 ms at 5,000; multi-row INSERT ALL with binds at 100 rows a statement took 7,285 ms.
    /// </summary>
    internal const int MaxRows = 5000;
    const int MaxChars = 4 << 20;

    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var n = target.Columns.Count;
        var types = target.Columns.Select(c => OracleColumnType.Parse(c.Type)).ToArray();
        var dbTypes = types.Select(OracleDataEdit.DbTypeOf).ToArray();
        var sql = $"INSERT INTO {OracleIdentifiers.Quote(target.Schema)}.{OracleIdentifiers.Quote(target.Table)} ({string.Join(", ", target.Columns.Select(c => OracleIdentifiers.Quote(c.Name)))}) " +
                  $"VALUES ({string.Join(", ", Enumerable.Range(0, n).Select(i => ":b" + i.ToString(CultureInfo.InvariantCulture)))})";
        var own = tx.Mode != BulkTxMode.Caller;
        var batches = tx.Mode == BulkTxMode.Batches;
        var columns = new object[n][];
        for (var i = 0; i < n; i++) columns[i] = new object[MaxRows];
        var row = new object[n];
        var fileRows = new long[MaxRows];
        long sent = 0, sinceCommit = 0;
        if (own) await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var limit = batches ? (int)Math.Min(MaxRows, tx.CommitEvery - sinceCommit) : MaxRows;
                int count = 0, chars = 0;
                while (count < limit && chars < MaxChars && rows.Read())
                {
                    var values = rows.Current;
                    string? bad = null;
                    var size = 0;
                    for (var i = 0; i < n && bad == null; i++)
                    {
                        var text = i < values.Count ? values[i] : null;
                        size += text?.Length ?? 0;
                        try { row[i] = text == null ? DBNull.Value : OracleDataEdit.ToParameter(types[i], text).Value; }
                        catch (FormatException fe) { bad = $"{target.Columns[i].Name}: {fe.Message}"; }
                    }
                    if (bad != null) { rows.Reject(bad); continue; }
                    for (var i = 0; i < n; i++) columns[i][count] = row[i];
                    fileRows[count] = rows.Row;
                    count++;
                    chars += size;
                }
                if (count == 0) break;
                await ExecuteAsync(connection, sql, dbTypes, columns, fileRows, count, commandTimeoutS, ct).ConfigureAwait(false);
                sent += count;
                sinceCommit += count;
                if (batches && sinceCommit >= tx.CommitEvery)
                {
                    await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
                    rows.Loaded(sent, true);
                    sinceCommit = 0;
                    await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
                }
                else rows.Loaded(sent, false);
            }
            if (own) await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
            rows.Loaded(sent, batches);
        }
        catch
        {
            if (own) try { using var cleanup = Cleanup.Start(); await transactions.RollbackAsync(connection, cleanup.Token).ConfigureAwait(false); } catch { /* connection state is re-read afterwards */ }
            throw;
        }
    }

    /// <summary>
    /// One array-bound execute of <paramref name="count"/> rows; Cancel breaks it on the server (ORA-01013). A row the server
    /// refuses fails the execute as ORA-24381 ("error(s) in array DML") with the row's own error under it: that error is
    /// what the user is told, with the file row it came from.
    /// </summary>
    static async Task ExecuteAsync(DbConnection connection, string sql, OracleDbType[] dbTypes, object[][] columns, long[] fileRows, int count, int? commandTimeoutS, CancellationToken ct)
    {
        await using var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.CommandTimeout = commandTimeoutS ?? 0;
        cmd.ArrayBindCount = count;
        for (var i = 0; i < dbTypes.Length; i++)
        {
            var values = count == columns[i].Length ? columns[i] : columns[i][..count];
            cmd.Parameters.Add(new OracleParameter("b" + i.ToString(CultureInfo.InvariantCulture), dbTypes[i], values, System.Data.ParameterDirection.Input));
        }
        await using var reg = ct.Register(() => { try { cmd.Cancel(); } catch { /* best effort: the token also ends the call */ } });
        try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        catch (OracleException ex) when (ex.Number == ArrayDmlErrors && RowError(ex, fileRows, count) is { } message)
        {
            throw new OracleArrayRowException(message, ex);
        }
    }

    internal const int ArrayDmlErrors = 24381;

    /// <summary>"File row 1,800: ORA-00001: unique constraint … violated" for the first row the server refused; null when the error names none.</summary>
    internal static string? RowError(OracleException ex, long[] fileRows, int count)
    {
        OracleError? first = null;
        foreach (OracleError e in ex.Errors)
            if (e.ArrayBindIndex >= 0 && e.ArrayBindIndex < count && e.Number != ArrayDmlErrors && (first == null || e.ArrayBindIndex < first.ArrayBindIndex)) first = e;
        if (first == null) return null;
        var text = first.Message.Split('\n')[0].Trim();
        return $"File row {fileRows[first.ArrayBindIndex]:N0}: {text}";
    }
}

/// <summary>A row of an array-bound INSERT the server refused, said with its file row (the import's error text).</summary>
public sealed class OracleArrayRowException(string message, OracleException inner) : DbException(message, inner)
{
    public override int ErrorCode => inner.Number;
}
