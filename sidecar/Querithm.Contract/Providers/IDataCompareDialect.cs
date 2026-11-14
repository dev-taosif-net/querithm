namespace Querithm.Contract.Providers;

/// <summary>How values of a column type are compared (and, for keys, ordered) once read as the grid's text form.</summary>
public enum ValueKind
{
    Text,
    /// <summary>char / nchar: trailing spaces are padding.</summary>
    PaddedText,
    Integer,
    Decimal,
    Float,
    Bool,
    Guid,
    Date,
    DateTime,
    DateTimeOffset,
    Time,
    Binary,
    /// <summary>No order the server and the compare agree on (xml, json, spatial): never a key.</summary>
    Unordered,
}

/// <summary>
/// A provider's data-compare rules: every engine orders keys in an order the compare reproduces in .NET —
/// text by code point (a binary collation), GUIDs by their canonical text — and runs custom queries read-only.
/// </summary>
public interface IDataCompareDialect
{
    ValueKind KindOf(string? columnType);

    /// <summary>ORDER BY expression for a key column in code-point / canonical order.</summary>
    string OrderExpression(string columnExpr, ValueKind kind);

    /// <summary>The engine compares text keys as if trailing spaces were absent (SQL Server pads; PostgreSQL text does not).</summary>
    bool PadsTrailingSpaces { get; }

    /// <summary>Why a custom query cannot be used (not exactly one SELECT), or null.</summary>
    string? QueryRefusal(string sql);

    /// <summary>
    /// An accepted custom query as a derived table: <c>{Prefix}SELECT … FROM (\n{Body}\n) AS q</c>. The body has no
    /// trailing semicolon or comment; what cannot sit inside a derived table moves out (SQL Server: the WITH clause goes
    /// to the prefix, and an ORDER BY without TOP/OFFSET is dropped, as the compare orders by the key itself).
    /// </summary>
    (string Prefix, string Body) DerivedQuery(string sql);

    /// <summary>Shown with the result: how to get a consistent read of a busy table.</summary>
    string IsolationHint { get; }

    /// <summary>Statements around inserts that write an identity column (SQL Server IDENTITY_INSERT), or null.</summary>
    (string On, string Off)? IdentityInsert(string table);

    /// <summary>Clause after the column list for inserts into an identity column (PostgreSQL OVERRIDING SYSTEM VALUE), or null.</summary>
    string? InsertOverriding { get; }

    /// <summary>Rows per multi-row VALUES list (SQL Server allows 1,000).</summary>
    int InsertBatch { get; }

    /// <summary>The sync script's transaction: begin (with any session settings) and commit.</summary>
    (string Begin, string Commit) SyncTransaction { get; }

    /// <summary>
    /// Phase D4 S11: the engine stores an empty string as NULL (Oracle), so a compare with it on either side counts '' and
    /// NULL as one value — the difference could never be synced away.
    /// </summary>
    bool EmptyTextIsNull => false;

    /// <summary>
    /// Phase D4 S11: why values of this type are not compared at all (they are read in part, or as text that says nothing
    /// of the value: Oracle LOBs, XMLTYPE, objects), or null. Such a column is left out with the reason, and is never a key.
    /// </summary>
    string? ValueRefusal(string? columnType) => null;

    /// <summary>Phase D4 S11: a custom query's column types come from the provider's <c>DescribeColumns</c> (the catalog's names), not the driver's.</summary>
    bool QueryTypesFromProvider => false;

    /// <summary>
    /// The read's snapshot is older than the table's last DDL (Oracle ORA-01466: a table created or altered seconds before a
    /// READ ONLY transaction): the read ends its transaction, waits <see cref="SnapshotRetryDelay"/> and runs once more.
    /// Asked only when the SELECT itself fails, before any row is read.
    /// </summary>
    bool SnapshotTooOld(Exception ex) => false;

    /// <summary>How long a <see cref="SnapshotTooOld"/> read waits before its one retry.</summary>
    TimeSpan SnapshotRetryDelay => TimeSpan.FromSeconds(3);
}
