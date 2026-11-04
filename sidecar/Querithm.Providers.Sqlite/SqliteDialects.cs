using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Providers.Sqlite;

/// <summary>A declared SQLite column type's affinity (sqlite.org/datatype3.html §3.1, in rule order).</summary>
public enum SqliteAffinity { Integer, Text, Blob, Real, Numeric }

public static class SqliteTypes
{
    public static SqliteAffinity AffinityOf(string? declared)
    {
        var t = (declared ?? "").ToUpperInvariant();
        if (t.Contains("INT")) return SqliteAffinity.Integer;
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return SqliteAffinity.Text;
        if (t.Length == 0 || t.Contains("BLOB")) return SqliteAffinity.Blob;
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return SqliteAffinity.Real;
        return SqliteAffinity.Numeric;
    }
}

/// <summary>
/// Grid edits on SQLite (Phase D1). Values travel as text parameters and the column's affinity converts them as SQLite does
/// for any INSERT or UPDATE; a column without a numeric or text affinity (BLOB / no declared type) compares and stores the
/// text as given. There is no row version: lost updates are caught by comparing the original values of the changed columns
/// with IS (affinity applies, and the grid shows reals in round-trip form, so an unchanged value matches exactly); a
/// mismatch is reported as a conflict and nothing is written. Keys must be proven non-null and unique (see SqliteMetadata:
/// a nullable declared PRIMARY KEY is not reported as primary).
/// </summary>
public sealed class SqliteDataEdit : IDataEditDialect
{
    public string Quote(string identifier) => SqliteTokenizer.Quote(identifier);
    public string Placeholder(int index) => "$p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string TypedPlaceholder(int index, string? columnType) => Placeholder(index);

    public void AddValue(DbCommand command, int index, string? columnType, GridValue value) =>
        command.Parameters.Add(new SqliteParameter(Placeholder(index), value.IsNull || value.Text == null ? DBNull.Value : value.Text));

    public (string? Kind, string? Column) Token(SnapObject table) => (null, null);
    public string TokenText(string alias, string column) => throw new NotSupportedException("SQLite has no row version.");
    public string TokenMatch(string alias, string column, string valueExpr) => throw new NotSupportedException("SQLite has no row version.");
    public string? TokenType => null;

    /// <summary>IS is null-safe and applies the column's affinity to the text parameter, like =.</summary>
    public string NullSafeEquals(string left, string right) => $"{left} IS {right}";

    /// <summary>The value as the grid shows it: blobs as 0x + hex, everything else as SQLite renders it as text.</summary>
    public string ValueText(string expr, string? columnType) => $"CASE typeof({expr}) WHEN 'blob' THEN '0x' || hex({expr}) ELSE CAST({expr} AS TEXT) END";

    public string? LockReason(SnapColumn column) =>
        column.Type.Contains("BLOB", StringComparison.OrdinalIgnoreCase) ? "binary values are shown as hex: change them with an UPDATE statement" : null;

    /// <summary>Text, integer and real affinities convert the grid's text back to the stored value; BLOB / untyped columns do not.</summary>
    public bool Comparable(string? columnType) => SqliteTypes.AffinityOf(columnType) != SqliteAffinity.Blob;

    /// <summary>Integer and text keys match exactly; reals and untyped keys do not identify a row safely.</summary>
    public bool ExactKey(string? columnType) => SqliteTypes.AffinityOf(columnType) is SqliteAffinity.Integer or SqliteAffinity.Text;

    /// <summary>sqlite3_changes(): rows the statement itself changed, trigger changes excluded.</summary>
    public async Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct) => await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

    public string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key)
    {
        var sb = new StringBuilder("INSERT INTO ").Append(table);
        if (columns.Count > 0)
            sb.Append(" (").Append(string.Join(", ", columns.Select(c => Quote(c.Column)))).Append(") VALUES (").Append(string.Join(", ", columns.Select(c => c.ValueExpr))).Append(')');
        else sb.Append(" DEFAULT VALUES");
        return sb.Append(" RETURNING ").Append(string.Join(", ", key.Select(k => ValueText(Quote(k.Column), k.Type)))).ToString();
    }

    public string PreviewLiteral(string? columnType, GridValue value)
    {
        if (value.UseDefault) return "DEFAULT";
        if (value.IsNull || value.Text == null) return "NULL";
        return "'" + value.Text.Replace("'", "''") + "'";
    }

    public string PreviewTerminator => ";";

    public string? ConcurrencyNote(string? tokenKind) => tokenKind == null
        ? "SQLite keeps no row version: concurrent changes are found by comparing the original values of the changed columns."
        : null;

    /// <summary>SQLite results are dynamically typed (D0f), and edits write text that the column's affinity converts.</summary>
    public bool EditsDynamicColumns => true;

    public string ValuesRelation(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> names, string alias) =>
        IDataEditDialect.UnionAllRelation(rows, names, alias);

    /// <summary>A compound SELECT holds at most 500 terms (SQLITE_MAX_COMPOUND_SELECT): one per row of the relation.</summary>
    public int MaxRelationRows => 500;
}

/// <summary>
/// Import into SQLite (Phase D1): <see cref="SqliteBulkLoader"/> inside the transaction rules of import.run, with new tables
/// typed by affinity. SQLite cannot change a column's type in place, so a column that needs widening is reported, not
/// altered.
/// </summary>
public sealed class SqliteImport : IImportDialect
{
    public string TypeFor(ImportColumnProfile column) => column.Kind switch
    {
        ImportKind.Int32 or ImportKind.Int64 or ImportKind.Bool => "INTEGER",
        ImportKind.Double => "REAL",
        // A decimal keeps every digit as text in a NUMERIC column only when it fits a 64-bit integer or a double exactly;
        // longer values stay text (SQLite has no decimal type).
        ImportKind.Decimal => column.Precision > 15 ? "TEXT" : "NUMERIC",
        _ => "TEXT", // dates, times and UUIDs as ISO text, SQLite's convention
    };

    public ImportFit Check(ImportColumnProfile column, SnapColumn target)
    {
        if (column.Kind == ImportKind.Empty) return new ImportFit(ImportVerdictKind.Fits, "Every value is NULL");
        var numeric = column.Kind is ImportKind.Int32 or ImportKind.Int64 or ImportKind.Decimal or ImportKind.Double or ImportKind.Bool;
        return SqliteTypes.AffinityOf(target.Type) switch
        {
            SqliteAffinity.Integer or SqliteAffinity.Real or SqliteAffinity.Numeric when !numeric =>
                new ImportFit(ImportVerdictKind.Fits, $"Values are text; SQLite stores what does not look like a number as text in {target.Type} (a STRICT table rejects those rows)"),
            SqliteAffinity.Integer when column.Kind is ImportKind.Decimal or ImportKind.Double =>
                new ImportFit(ImportVerdictKind.Fits, $"Values have decimals; {target.Type} keeps them as reals"),
            _ => new ImportFit(ImportVerdictKind.Fits, $"Fits {(target.Type.Length == 0 ? "(no declared type)" : target.Type)}"),
        };
    }

    public bool NullUsesDefault => false;

    /// <summary>A blank cell is NULL except in a text column, where the blanks are the value.</summary>
    public bool BlankIsNull(string? type) => SqliteTypes.AffinityOf(type) != SqliteAffinity.Text;

    public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey)
    {
        var parts = columns.Select(c => $"    {SqliteTokenizer.Quote(c.Name)} {c.Type}{(c.NotNull ? " NOT NULL" : "")}").ToList();
        if (primaryKey.Count > 0) parts.Add($"    PRIMARY KEY ({string.Join(", ", primaryKey.Select(SqliteTokenizer.Quote))})");
        return $"CREATE TABLE {SqliteTokenizer.Quote(schema)}.{SqliteTokenizer.Quote(table)} (\n{string.Join(",\n", parts)}\n);";
    }

    public string AlterColumnType(string schema, string table, SnapColumn column, string newType) =>
        throw new NotSupportedException($"SQLite cannot change the type of {column.Name} in place; its affinity ({column.Type}) stores the values anyway, or rebuild the table.");

    public string AddColumn(string schema, string table, string column, string type) =>
        $"ALTER TABLE {SqliteTokenizer.Quote(schema)}.{SqliteTokenizer.Quote(table)} ADD COLUMN {SqliteTokenizer.Quote(column)} {type};";

    public IBulkLoader? Loader { get; } = new SqliteBulkLoader(new SqliteTransactions());
}

/// <summary>
/// SQLite's bulk path: one prepared single-row INSERT whose parameter values change per row. The generic multi-row INSERT
/// binds 2,000 named parameters per statement and SQLite finds each name by a linear search, so 100k × 10 spent 7.9 s in
/// prepare + bind; a prepared row took 0.65 s (Stopwatch profile of both shapes on Microsoft.Data.Sqlite, 2026-10-31,
/// bench #23.sqlite). Own: one transaction, all rows or none; Caller: inside the user's transaction (the service holds a
/// savepoint); Batches: a transaction committed every <see cref="BulkTransaction.CommitEvery"/> rows.
/// </summary>
public sealed class SqliteBulkLoader(ITransactionControl transactions) : IBulkLoader
{
    public async Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct)
    {
        var n = target.Columns.Count;
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = commandTimeoutS ?? 0;
        var values = new SqliteParameter[n];
        for (var i = 0; i < n; i++) cmd.Parameters.Add(values[i] = new SqliteParameter("$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), DBNull.Value));
        cmd.CommandText = $"INSERT INTO {SqliteTokenizer.Quote(target.Schema)}.{SqliteTokenizer.Quote(target.Table)} ({string.Join(", ", target.Columns.Select(c => SqliteTokenizer.Quote(c.Name)))}) " +
                          $"VALUES ({string.Join(", ", values.Select(v => v.ParameterName))})";
        var own = tx.Mode != BulkTxMode.Caller;
        var batches = tx.Mode == BulkTxMode.Batches;
        long sent = 0, inBatch = 0;
        if (own) await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
        try
        {
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                var row = rows.Current;
                for (var i = 0; i < n; i++) values[i].Value = i < row.Count && row[i] is { } v ? v : DBNull.Value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                sent++;
                if (batches && ++inBatch >= tx.CommitEvery)
                {
                    await tx.CommitAsync(() => transactions.CommitAsync(connection, ct)).ConfigureAwait(false);
                    rows.Loaded(sent, true);
                    inBatch = 0;
                    await transactions.BeginAsync(connection, ct).ConfigureAwait(false);
                }
                else if (sent % 500 == 0) rows.Loaded(sent, false);
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
}
