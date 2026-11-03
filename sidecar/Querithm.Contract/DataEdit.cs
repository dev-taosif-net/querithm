namespace Querithm.Contract;

/// <summary>One cell value sent by the grid. Text is the invariant-culture form the grid displays (ISO-8601 dates, '.' decimals, 0x-hex binary).</summary>
public sealed record GridValue
{
    public bool IsNull { get; init; }
    /// <summary>Insert only: leave the column to its server default (identity, sequence, DEFAULT constraint).</summary>
    public bool UseDefault { get; init; }
    public string? Text { get; init; }
}

/// <summary>Per result column: whether the grid may edit it and how the sidecar writes it.</summary>
public sealed record EditColumn
{
    /// <summary>Result column ordinal (ResultColumn.Ordinal).</summary>
    public int Ordinal { get; init; }
    /// <summary>Table column it writes; null for expressions.</summary>
    public string? Column { get; init; }
    /// <summary>Table column type, e.g. nvarchar(50), integer.</summary>
    public string? Type { get; init; }
    public bool Nullable { get; init; }
    /// <summary>Has a server default (DEFAULT constraint, identity, sequence), so an inserted row may omit it.</summary>
    public bool HasDefault { get; init; }
    /// <summary>Identity / generated: never written on existing rows, always left to the server on inserts.</summary>
    public bool Generated { get; init; }
    public bool Locked { get; init; }
    public string? LockReason { get; init; }
    /// <summary>Takes part in the original-value concurrency check (false for float, LOB and xml/json types).</summary>
    public bool Comparable { get; init; }
    public bool Key { get; init; }
}

/// <summary>Whether a result set can be edited in the grid, decided by the sidecar from column origins and cached metadata (plan §1.1).</summary>
public sealed record ResultEditInfo
{
    public bool Editable { get; init; }
    /// <summary>Why the result set is read-only, shown on the grid's badge.</summary>
    public string? Reason { get; init; }
    public long? ObjectId { get; init; }
    public string? Schema { get; init; }
    public string? Table { get; init; }
    /// <summary>Row-key table columns in key order (primary key, else a NOT NULL unique index).</summary>
    public string[] KeyColumns { get; init; } = [];
    /// <summary>Result ordinals of the row-key columns, in key order.</summary>
    public int[] KeyOrdinals { get; init; } = [];
    /// <summary>"xmin" (PostgreSQL), "rowversion" (SQL Server table with a rowversion column) or null (original-value comparison).</summary>
    public string? TokenKind { get; init; }
    /// <summary>The column holding the token (xmin, or the rowversion column's name).</summary>
    public string? TokenColumn { get; init; }
    /// <summary>Why inserting rows is not possible (a required column is missing from the SELECT list), or null.</summary>
    public string? InsertBlockedReason { get; init; }
    /// <summary>How lost updates are detected, shown in the DML preview.</summary>
    public string? ConcurrencyNote { get; init; }
    public EditColumn[] Columns { get; init; } = [];
}

public sealed record GridRowUpdate
{
    public int RowIndex { get; init; }
    /// <summary>Row-key values in KeyColumns order.</summary>
    public required GridValue[] Key { get; init; }
    /// <summary>xmin / rowversion read by data.rowTokens; null when the table has none.</summary>
    public string? Token { get; init; }
    /// <summary>Displayed values of the changed columns (by table column name), compared when there is no token.</summary>
    public required Dictionary<string, GridValue> Original { get; init; }
    public required Dictionary<string, GridValue> Changes { get; init; }
}

public sealed record GridRowInsert
{
    public int TempIndex { get; init; }
    public required Dictionary<string, GridValue> Values { get; init; }
}

public sealed record GridRowDelete
{
    public int RowIndex { get; init; }
    public required GridValue[] Key { get; init; }
    public string? Token { get; init; }
    /// <summary>Displayed values of the comparable columns, checked when there is no token.</summary>
    public Dictionary<string, GridValue>? Original { get; init; }
}

public sealed record GridPatchParams
{
    public required string SessionId { get; init; }
    /// <summary>The execution whose result set is edited; the sidecar takes the table and key from what it recorded.</summary>
    public required string ExecutionId { get; init; }
    public int ResultIndex { get; init; }
    public GridRowUpdate[] Updates { get; init; } = [];
    public GridRowInsert[] Inserts { get; init; } = [];
    public GridRowDelete[] Deletes { get; init; } = [];
    /// <summary>Set only after the user confirmed the production data-change dialog (§13 rule 7).</summary>
    public bool ConfirmedProd { get; init; }
    /// <summary>Deleting rows on Prod needs this second confirmation.</summary>
    public bool ConfirmedDestructive { get; init; }
    /// <summary>Write the changes even where the row changed on the server (rows deleted meanwhile still conflict).</summary>
    public bool ForceOverwriteConflicts { get; init; }
}

public sealed record GridPreviewResult
{
    public required string Sql { get; init; }
    public int StatementCount { get; init; }
    public string[] Notes { get; init; } = [];
}

public sealed record GridConflictRow
{
    /// <summary>RowIndex of the update/delete, or TempIndex of an insert.</summary>
    public int RowIndex { get; init; }
    public bool Insert { get; init; }
    /// <summary>changedOnServer | deletedOnServer | error</summary>
    public required string Reason { get; init; }
    public string? Message { get; init; }
    /// <summary>The row as the server has it now (by table column name); null when it no longer exists.</summary>
    public Dictionary<string, GridValue>? Current { get; init; }
}

public sealed record GridApplyResult
{
    public bool Ok { get; init; }
    public int AppliedUpdates { get; init; }
    public int AppliedInserts { get; init; }
    public int AppliedDeletes { get; init; }
    public GuardBlock? Blocked { get; init; }
    public GridConflictRow[]? Conflicts { get; init; }
    public string? Error { get; init; }
    /// <summary>Row-key values the server generated for inserted rows, by TempIndex (identity / sequence).</summary>
    public Dictionary<string, GridValue[]>? InsertedKeys { get; init; }
    /// <summary>The patch execution (it appears in history and the tab's messages).</summary>
    public string? ExecutionId { get; init; }
}

public sealed record RowTokensParams
{
    public required string SessionId { get; init; }
    public required string ExecutionId { get; init; }
    public int ResultIndex { get; init; }
    /// <summary>Row keys in KeyColumns order.</summary>
    public required GridValue[][] Keys { get; init; }
    /// <summary>Per key: displayed values that must still hold on the server (the row has not changed since the query), by table column name.</summary>
    public Dictionary<string, GridValue>[]? Expect { get; init; }
}

public sealed record RowTokensResult
{
    /// <summary>Per requested key: the token, "" when the table has none, or null when the row is gone or no longer matches.</summary>
    public required string?[] Tokens { get; init; }
}
