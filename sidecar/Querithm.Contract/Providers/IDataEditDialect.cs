using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>
/// Engine rules for writing grid edits back: identifier quoting, typed parameters, row-version tokens,
/// savepoints and per-statement row counts. Statements are built from catalog names only; values always travel as
/// driver parameters (§7 Parameterized execution), never inlined.
/// </summary>
public interface IDataEditDialect
{
    /// <summary>Quotes one identifier part ([x] / "x"), escaping the quote character.</summary>
    string Quote(string identifier);

    /// <summary>Placeholder for the parameter added at <paramref name="index"/> (@p0 / $1).</summary>
    string Placeholder(int index);

    /// <summary>The placeholder as a value of <paramref name="columnType"/> in SQL text (PostgreSQL casts; SQL Server types the parameter instead).</summary>
    string TypedPlaceholder(int index, string? columnType);

    /// <summary>Adds the parameter for <paramref name="index"/>; throws <see cref="FormatException"/> when the text is not a valid <paramref name="columnType"/>.</summary>
    void AddValue(DbCommand command, int index, string? columnType, GridValue value);

    /// <summary>"xmin", "rowversion" or null, and the column that holds it.</summary>
    (string? Kind, string? Column) Token(SnapObject table);

    /// <summary>SQL text expression reading the token of <paramref name="alias"/> as text (empty alias = unqualified).</summary>
    string TokenText(string alias, string column);

    /// <summary>Predicate matching the token column against a value expression (placeholder or preview literal; empty alias = unqualified).</summary>
    string TokenMatch(string alias, string column, string valueExpr);

    /// <summary>Column type the token parameter is sent as (SQL Server binary(8)); null = untyped text.</summary>
    string? TokenType { get; }

    /// <summary>Null-safe equality of two SQL expressions (NULL matches NULL).</summary>
    string NullSafeEquals(string left, string right);

    /// <summary>SQL text expression reading <paramref name="expr"/> (a column of <paramref name="columnType"/>) as text the grid displays and the parser reads back.</summary>
    string ValueText(string expr, string? columnType);

    /// <summary>Null when the column can be written from the grid, else why not (UDTs, sql_variant, rowversion).</summary>
    string? LockReason(SnapColumn column);

    /// <summary>Equality on this type is exact for the grid's text form (false for float, LOB, xml/json and 7-digit time precision).</summary>
    bool Comparable(string? columnType);

    /// <summary>The grid's text form identifies a key value exactly (false for float and 7-digit time precision).</summary>
    bool ExactKey(string? columnType);

    /// <summary>Runs one write statement and returns the rows it changed itself (trigger DML excluded).</summary>
    Task<long> ExecuteCountedAsync(DbCommand command, CancellationToken ct);

    /// <summary>
    /// An INSERT of <paramref name="columns"/> (each with its value expression: a placeholder or a preview literal) that
    /// returns the row-key columns as text in one result row. No columns = DEFAULT VALUES. <c>Generated</c> marks a key
    /// column the server fills (identity / AUTO_INCREMENT): an engine without RETURNING reads it back from the session.
    /// </summary>
    string InsertReturningKey(string table, IReadOnlyList<(string Column, string ValueExpr)> columns, IReadOnlyList<(string Column, string? Type, bool Generated)> key);

    /// <summary>A literal put in place of the placeholder in the read-only preview script (never executed).</summary>
    string PreviewLiteral(string? columnType, GridValue value);

    /// <summary>Statement separator for the preview script.</summary>
    string PreviewTerminator { get; }

    /// <summary>Engine-specific notes for the preview (e.g. MySQL counts changed rows only).</summary>
    string? ConcurrencyNote(string? tokenKind);

    /// <summary>
    /// Phase D1: the engine writes dynamically typed result columns (D0f) back safely from the grid's text — SQLite, whose
    /// column affinity converts the text as for any INSERT or UPDATE. False everywhere else: such a column stays locked.
    /// </summary>
    bool EditsDynamicColumns => false;

    /// <summary>
    /// A derived table of literal rows, <paramref name="alias"/>(<paramref name="names"/>), to JOIN against (data.rowTokens).
    /// Default: VALUES with a column list (SQL Server, PostgreSQL); see <see cref="UnionAllRelation"/> for engines without one.
    /// </summary>
    string ValuesRelation(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> names, string alias) =>
        $"(VALUES {string.Join(", ", rows.Select(r => "(" + string.Join(", ", r) + ")"))}) AS {alias}({string.Join(", ", names)})";

    /// <summary>
    /// The most rows one <see cref="ValuesRelation"/> may hold. SQLite builds it as a compound SELECT, which the engine caps
    /// at 500 terms (SQLITE_MAX_COMPOUND_SELECT); the default only bounds the statement's size.
    /// </summary>
    int MaxRelationRows => 1000;

    /// <summary>
    /// Phase D4 S10: the pseudo-column that addresses a row (Oracle's ROWID), used as the row key when the table has no
    /// proven key in the result but the result reads this column from the same table. Null (the default): no fallback.
    /// Its edit column is always locked (<see cref="LockReason"/> names it) and never required for an insert.
    /// </summary>
    string? RowAddress => null;

    /// <summary>
    /// Why <see cref="RowAddress"/> cannot identify the rows of <paramref name="table"/> (an index-organized table, row
    /// movement enabled, metadata cached before the engine reported it), or null when it can. <paramref name="rowAddress"/>
    /// is the catalog's <see cref="SnapObject.RowAddress"/>. Only asked when <see cref="RowAddress"/> is set.
    /// </summary>
    string? RowAddressRefusal(SnapObject table, string? rowAddress) => "the engine has no row address";

    /// <summary>The same rows as SELECT … UNION ALL SELECT …: SQLite and MariaDB have no column list on a derived table.</summary>
    static string UnionAllRelation(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<string> names, string alias) =>
        "(" + string.Join(" UNION ALL ", rows.Select((r, i) => "SELECT " + string.Join(", ", r.Select((e, k) => i == 0 ? $"{e} AS {names[k]}" : e)))) + $") AS {alias}";
}
