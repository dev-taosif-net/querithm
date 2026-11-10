namespace Querithm.Contract;

public sealed record QueryParameter
{
    public required string Name { get; init; }
    /// <summary>Driver type name, e.g. int, nvarchar(50), datetime2; null = infer from value.</summary>
    public string? Type { get; init; }
    public string? Value { get; init; }
    public bool IsNull { get; init; }
}

public sealed record ExecuteOptions
{
    /// <summary>Per result set; 0 = unlimited. Default 50,000 (§7).</summary>
    public int? MaxRows { get; init; }
    public bool StatisticsIo { get; init; }
    public bool StatisticsTime { get; init; }
    public bool ActualPlan { get; init; }
    /// <summary>Set only after the user confirmed the destructive-statement dialog (§13 rule 7).</summary>
    public bool ConfirmedDestructive { get; init; }
    /// <summary>Set only after the user confirmed the first-execution-on-Prod dialog.</summary>
    public bool ConfirmedProd { get; init; }
    public QueryParameter[]? Parameters { get; init; }
    /// <summary>1-based editor line of the first character of Sql (for executing a selection).</summary>
    public int? LineOffset { get; init; }
    /// <summary>Do not write this execution to history (internal helper queries).</summary>
    public bool NoHistory { get; init; }
    /// <summary>
    /// Phase D4: the tab's server-output toggle (Oracle DBMS_OUTPUT). Set, it becomes the session's setting from this run
    /// on; null keeps the session's (off until turned on here or by <c>SET SERVEROUTPUT ON</c> in a script).
    /// </summary>
    public bool? ServerOutput { get; init; }
}

public sealed record QueryExecuteParams
{
    public required string SessionId { get; init; }
    public required string Sql { get; init; }
    public ExecuteOptions? Options { get; init; }
}

/// <summary>ProdWrite: a data change from a Querithm tool (grid edit, import, sync) on a Prod profile; confirmed every time.</summary>
public enum GuardReason { ProdFirstRun, Destructive, ProdWrite }

public sealed record DestructiveFinding
{
    /// <summary>deleteWithoutWhere | updateWithoutWhere | drop | truncate | alter</summary>
    public required string Rule { get; init; }
    public required string Statement { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public required string Description { get; init; }
}

public sealed record GuardBlock
{
    public GuardReason Reason { get; init; }
    public required string ProfileName { get; init; }
    public required string Server { get; init; }
    public string? Database { get; init; }
    public EnvironmentKind Environment { get; init; }
    public required DestructiveFinding[] Findings { get; init; }
}

public sealed record QueryExecuteResult
{
    public string? ExecutionId { get; init; }
    public GuardBlock? Blocked { get; init; }
}

public sealed record ExecutionRef
{
    public required string ExecutionId { get; init; }
}

public sealed record CancelResult
{
    public bool Cancelled { get; init; }
}

public sealed record CellParams
{
    public required string ExecutionId { get; init; }
    public int ResultIndex { get; init; }
    public long Row { get; init; }
    public int Col { get; init; }
}

public sealed record CellResult
{
    public string? Value { get; init; }
    /// <summary>Value is base64 when true.</summary>
    public bool IsBinary { get; init; }
    public bool Unavailable { get; init; }
}

// ---- notifications -------------------------------------------------------------------------

public sealed record QueryQueuedEvent
{
    public required string ExecutionId { get; init; }
    public required string SessionId { get; init; }
}

/// <summary>
/// The data plane gained or lost its reader (§5): false when a frame found no reader within the wait (rows cannot be
/// shown until one attaches), true when one attaches.
/// </summary>
public sealed record DataPlaneStateEvent
{
    public required bool Connected { get; init; }
}

public sealed record QueryStartedEvent
{
    public required string ExecutionId { get; init; }
    public required string SessionId { get; init; }
    public int BatchCount { get; init; }
    public long StartedAt { get; init; }
}

public sealed record ServerMessage
{
    public required string ExecutionId { get; init; }
    public Severity Severity { get; init; }
    public required string Message { get; init; }
    /// <summary>Line as reported by the server (batch-relative for SQL Server).</summary>
    public int? Line { get; init; }
    /// <summary>Line mapped to the editor (batch start + reported line).</summary>
    public int? EditorLine { get; init; }
    /// <summary>
    /// Phase D4: the 1-based column on <see cref="EditorLine"/> the server named (Oracle parse and compile errors), mapped to
    /// the editor (a batch starting mid-line shifts its first line); null when the server named none.
    /// </summary>
    public int? EditorColumn { get; init; }
    public string? Procedure { get; init; }
    public int? Number { get; init; }
    public int? State { get; init; }
    public int? Class { get; init; }
    public int BatchIndex { get; init; }
    public long Ts { get; init; }
}

/// <summary>
/// M13: an execution's server messages in arrival order, coalesced by the sidecar (sent at most about every 100 ms, or
/// every 500 messages, and always before the execution's next result set, plan or end). A run forwards at most 20,000
/// server messages; the rest are counted and reported in one closing line.
/// </summary>
public sealed record QueryMessagesEvent
{
    public required string ExecutionId { get; init; }
    public required ServerMessage[] Messages { get; init; }
}

public sealed record ResultColumn
{
    public required string Name { get; init; }
    /// <summary>Server type name (nvarchar, int, timestamptz...).</summary>
    public required string TypeName { get; init; }
    /// <summary>Arrow type summary, e.g. int32, utf8, decimal128(18,2), timestamp[us,UTC].</summary>
    public required string ArrowType { get; init; }
    public bool Nullable { get; init; }
    public int Ordinal { get; init; }
    /// <summary>Name of the Boolean sibling column that marks truncated cells.</summary>
    public string? TruncatedFlagColumn { get; init; }
    /// <summary>Name of the Int16 sibling column with datetimeoffset minutes.</summary>
    public string? OffsetColumn { get; init; }
    /// <summary>Helper column (flag/offset) the grid does not show.</summary>
    public bool Hidden { get; init; }
    /// <summary>False when neither the driver nor the source table says whether the column allows NULL.</summary>
    public bool NullableKnown { get; init; }
    /// <summary>The table column this result column reads, "schema.table.column", when known exactly.</summary>
    public string? Source { get; init; }
    /// <summary>Source is part of the table's primary key.</summary>
    public bool Pk { get; init; }
    /// <summary>"schema.table.column" that Source references through a foreign key.</summary>
    public string? Fk { get; init; }
    /// <summary>
    /// Phase D0f: the engine does not guarantee one type for this column (SQLite storage classes). Its values travel as
    /// lossless text — integers in decimal, reals in round-trip form, text as is, blobs in lowercase hex — and each cell's
    /// storage class in the hidden <see cref="ClassColumn"/> (u8: 0 null, 1 integer, 2 real, 3 text, 4 blob). Grid edits
    /// refuse such a column; charts and sorting read the class.
    /// </summary>
    public bool Dynamic { get; init; }
    /// <summary>Name of the UInt8 sibling column holding each cell's storage class (see <see cref="Dynamic"/>).</summary>
    public string? ClassColumn { get; init; }
}

public sealed record QueryResultSetEvent
{
    public required string ExecutionId { get; init; }
    public int ResultIndex { get; init; }
    public required ResultColumn[] Schema { get; init; }
    public long? RowCountHint { get; init; }
    public int BatchIndex { get; init; }
    /// <summary>Whether the grid may edit this result set, and why not.</summary>
    public ResultEditInfo? Edit { get; init; }
}

public sealed record QueryPlanEvent
{
    public required string ExecutionId { get; init; }
    public int BatchIndex { get; init; }
    public required PlanTree Plan { get; init; }
    public required string Raw { get; init; }
}

public sealed record ErrorInfo
{
    public required string Message { get; init; }
    public int? Number { get; init; }
    public int? Line { get; init; }
    public int? EditorLine { get; init; }
    /// <summary>See <see cref="ServerMessage.EditorColumn"/>.</summary>
    public int? EditorColumn { get; init; }
    public string? Procedure { get; init; }
    public int? BatchIndex { get; init; }
}

public sealed record StatementStats
{
    public int BatchIndex { get; init; }
    /// <summary>io | time | compile</summary>
    public required string Kind { get; init; }
    public string? Table { get; init; }
    public long? ScanCount { get; init; }
    public long? LogicalReads { get; init; }
    public long? PhysicalReads { get; init; }
    public long? ReadAheadReads { get; init; }
    public long? LobLogicalReads { get; init; }
    public double? CpuMs { get; init; }
    public double? ElapsedMs { get; init; }
}

public sealed record QueryDoneEvent
{
    public required string ExecutionId { get; init; }
    public required string SessionId { get; init; }
    public ExecState State { get; init; }
    public long? AffectedRows { get; init; }
    /// <summary>Client-side wall time.</summary>
    public double ElapsedMs { get; init; }
    /// <summary>Server-reported elapsed from STATISTICS TIME; null when not reported. Never the client time.</summary>
    public double? ServerElapsedMs { get; init; }
    public double? ServerCpuMs { get; init; }
    public ErrorInfo? Error { get; init; }
    public bool Truncated { get; init; }
    public int ResultSetCount { get; init; }
    public StatementStats[]? Statistics { get; init; }
    /// <summary>Session database after the execution (USE inside the batch).</summary>
    public string? Database { get; init; }
    public bool HadDdl { get; init; }
}
