using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>One verdict of the file-column vs table-column check.</summary>
public sealed record ImportFit(ImportVerdictKind Kind, string Message, string? WidenTo = null);

/// <summary>A target column of a load: its name and catalog type.</summary>
/// <param name="Identity">An identity column, loaded only when the user keeps the file's values (KeepIdentity).</param>
public sealed record BulkColumn(string Name, string? Type, bool Identity = false);

/// <summary>Where a load writes: schema.table and the mapped columns in source order.</summary>
public sealed record BulkTarget(string Schema, string Table, IReadOnlyList<BulkColumn> Columns);

/// <summary>
/// The rows of a load, already mapped to <see cref="BulkTarget.Columns"/>. A loader converts or validates each value
/// for its engine and calls <see cref="Reject"/> for rows that cannot be loaded (they go to the reject log).
/// </summary>
public interface IBulkRows
{
    /// <summary>Advances to the next row; false at the end.</summary>
    bool Read();
    /// <summary>Text values of the current row in target-column order (null = NULL).</summary>
    IReadOnlyList<string?> Current { get; }
    /// <summary>1-based file row number of the current row.</summary>
    long Row { get; }
    /// <summary>Records the current row as rejected; throws when the reject limit is exceeded.</summary>
    void Reject(string reason);
    /// <summary>Called by the loader with the total rows sent so far; <paramref name="committed"/> when they are durable (batches mode).</summary>
    void Loaded(long totalRows, bool committed);
}

public enum BulkTxMode
{
    /// <summary>The loader runs everything in one transaction of its own: all rows or none.</summary>
    Own,
    /// <summary>Inside the caller's open transaction (under its savepoint): the loader must not commit or roll back.</summary>
    Caller,
    /// <summary>No outer transaction: the loader commits every <see cref="BulkTransaction.CommitEvery"/> rows.</summary>
    Batches,
}

public sealed record BulkTransaction(BulkTxMode Mode, int CommitEvery = 10_000)
{
    volatile bool _inCommit;

    /// <summary>
    /// True from the moment the loader sends a COMMIT of its own (<see cref="BulkTxMode.Own"/>, each batch of
    /// <see cref="BulkTxMode.Batches"/>) until the server answered it. A failure while it is true — a lost connection, a
    /// cancel — may or may not have committed: the import reports the outcome as unknown, never "nothing was written".
    /// </summary>
    public bool InCommit => _inCommit;

    /// <summary>Runs one of the loader's own COMMITs under <see cref="InCommit"/> (left set when it throws).</summary>
    public async Task CommitAsync(Func<Task> commit)
    {
        _inCommit = true;
        await commit().ConfigureAwait(false);
        _inCommit = false;
    }
}

/// <summary>Writes rows into a table with the engine's native bulk path.</summary>
public interface IBulkLoader
{
    /// <summary>
    /// Loads every row of <paramref name="rows"/> under <paramref name="tx"/>. Errors are thrown (never left as
    /// informational messages), and in <see cref="BulkTxMode.Own"/> mode a failure leaves nothing behind.
    /// </summary>
    Task LoadAsync(DbConnection connection, BulkTarget target, IBulkRows rows, ImportRunOptions options, BulkTransaction tx, int? commandTimeoutS, CancellationToken ct);
}

/// <summary>Engine rules for importing files: types for new tables, the compatibility check, DDL, the loader.</summary>
public interface IImportDialect
{
    /// <summary>The column type a new table gets for an inferred file column.</summary>
    string TypeFor(ImportColumnProfile column);

    /// <summary>Whether the file column's values fit the table column's type, and the wider type when they do not.</summary>
    ImportFit Check(ImportColumnProfile column, SnapColumn target);

    /// <summary>True when the native loader puts the column default in place of NULL (SQL Server bulk copy without KeepNulls).</summary>
    bool NullUsesDefault { get; }

    /// <summary>
    /// Whether a whitespace-only cell loads as NULL into a column of this type: true for every type but character
    /// ones (where the blanks are the value). The profiler counts such cells as NULLs for non-text columns too.
    /// </summary>
    bool BlankIsNull(string? type);

    string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey);
    string AlterColumnType(string schema, string table, SnapColumn column, string newType);
    string AddColumn(string schema, string table, string column, string type);

    /// <summary>The native bulk path; null = the generic batched INSERT (engines without one, e.g. MySQL with LOAD DATA disabled).</summary>
    IBulkLoader? Loader { get; }
}
