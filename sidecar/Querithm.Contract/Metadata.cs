namespace Querithm.Contract;

public sealed record DatabaseInfo
{
    public required string Name { get; init; }
    public bool IsSystem { get; init; }
    public string? State { get; init; }
}

public sealed record SnapshotParams
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    /// <summary>
    /// Phase D0d: the session whose view this is. Required for an in-memory database (its objects exist only in that
    /// session) and adds a session's own objects (temporary tables) to a shared snapshot; it must belong to the profile.
    /// </summary>
    public string? SessionId { get; init; }
}

public sealed record SnapColumn
{
    public required string Name { get; init; }
    /// <summary>Display type, e.g. decimal(18,2), nvarchar(50), int.</summary>
    public required string Type { get; init; }
    public bool Nullable { get; init; }
    public bool Identity { get; init; }
    public bool Computed { get; init; }
    public bool Pk { get; init; }
    public string? Default { get; init; }
    /// <summary>Server column number (column_id / attnum); maps result columns back to their table column.</summary>
    public int Ordinal { get; init; }
}

public sealed record SnapParam
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int Ordinal { get; init; }
    public bool Output { get; init; }
    public bool HasDefault { get; init; }
}

public sealed record FkColumnPair
{
    public required string Parent { get; init; }
    public required string Referenced { get; init; }
}

public sealed record SnapFk
{
    public required string Name { get; init; }
    public long Parent { get; init; }
    public long Referenced { get; init; }
    public required FkColumnPair[] Columns { get; init; }
}

public sealed record SnapObject
{
    public long Id { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public ObjectKind Kind { get; init; }
    public SnapColumn[]? Columns { get; init; }
    public SnapParam[]? Params { get; init; }
    public string? ReturnType { get; init; }
    public long? ModifyDate { get; init; }
    /// <summary>Triggers: the table or view the trigger belongs to; null for every other kind.</summary>
    public long? ParentObjectId { get; init; }
    /// <summary>
    /// MySQL / MariaDB: the storage engine of a table that cannot roll its changes back (MyISAM, MEMORY, ARCHIVE, Aria …); null
    /// for a transactional table and every other engine. Grid editing refuses such a table: Apply runs in a transaction that
    /// is rolled back on any failure, which would leave the rows already written. Internal: sidecar-only, never in the UI
    /// contract (ContractGen and the JSON context read public properties only).
    /// </summary>
    internal string? NoRollbackEngine { get; init; }
    /// <summary>Phase D4: shown beside the name in the explorer — "invalid", "materialized view", "external table"; null for most objects.</summary>
    public string? Note { get; init; }
}

public sealed record SchemaSnapshot
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public SnapshotStatus Status { get; init; }
    public long? RefreshedAt { get; init; }
    public string? ServerVersion { get; init; }
    public string? Error { get; init; }
    public required SnapObject[] Objects { get; init; }
    public required SnapFk[] Fks { get; init; }
    /// <summary>Phase D0d: set when this view is a session's (an in-memory database, or a shared snapshot with the session's own objects).</summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// Phase D4: what the last refresh could not read (an object kind the login has no access to, with the reason): the
    /// snapshot is fresh but those objects are missing from it. Null when nothing was left out.
    /// </summary>
    public string[]? Notes { get; init; }
}

public sealed record ObjectsParams
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public required long[] Ids { get; init; }
    /// <summary>
    /// Phase D0d: the session whose view this is. Required for an in-memory database (its objects exist only in that
    /// session) and adds a session's own objects (temporary tables) to a shared snapshot; it must belong to the profile.
    /// </summary>
    public string? SessionId { get; init; }
}

public sealed record RefreshParams
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public bool Full { get; init; }
    /// <summary>
    /// Phase D0d: the session whose view this is. Required for an in-memory database (its objects exist only in that
    /// session) and adds a session's own objects (temporary tables) to a shared snapshot; it must belong to the profile.
    /// </summary>
    public string? SessionId { get; init; }
}

public sealed record JobRef
{
    public required string JobId { get; init; }
}

public sealed record MetaUpdatedEvent
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public SnapshotStatus Status { get; init; }
    public int Changed { get; init; }
    public long[]? ChangedIds { get; init; }
    public long[]? RemovedIds { get; init; }
    public bool Full { get; init; }
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    /// <summary>Phase D0d: the session whose view changed; null = the shared snapshot every tab on the profile sees.</summary>
    public string? SessionId { get; init; }
    /// <summary>Phase D4: <see cref="SchemaSnapshot.Notes"/> after a fresh refresh (null: nothing was left out).</summary>
    public string[]? Notes { get; init; }
}

public sealed record ObjectRef
{
    public string? ProfileId { get; init; }
    public string? Database { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public ObjectKind Kind { get; init; }
    public long? ObjectId { get; init; }
}

public sealed record ObjectDetailsParams
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public long ObjectId { get; init; }
    /// <summary>
    /// Phase D0d: the session whose view this is. Required for an in-memory database (its objects exist only in that
    /// session) and adds a session's own objects (temporary tables) to a shared snapshot; it must belong to the profile.
    /// </summary>
    public string? SessionId { get; init; }
}

public sealed record IndexInfo
{
    public required string Name { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimary { get; init; }
    public required string[] Columns { get; init; }
    /// <summary>
    /// The index cannot prove a row unique (partial / filtered, an expression key part, disabled or invalid): grid editing never
    /// uses it as a row key. Internal: sidecar-only, never in the UI contract.
    /// </summary>
    internal bool NotAKey { get; init; }
}

public sealed record ObjectDetails
{
    public required SnapColumn[] Columns { get; init; }
    public required SnapParam[] Params { get; init; }
    public required IndexInfo[] Indexes { get; init; }
    public required SnapFk[] ForeignKeys { get; init; }
    public required SnapFk[] ReferencedBy { get; init; }
}

public sealed record DefinitionParams
{
    public required string SessionId { get; init; }
    public required ObjectRef Object { get; init; }
}

public sealed record DefinitionResult
{
    public required string Sql { get; init; }
    public required string DefinitionHash { get; init; }
    public long? ObjectId { get; init; }
    public long? ModifyDate { get; init; }
}

public enum DependencyDirection { Uses, UsedBy }

public sealed record Dependency
{
    public DependencyDirection Direction { get; init; }
    public required ObjectRef Object { get; init; }
}

public sealed record SearchParams
{
    public required string ProfileId { get; init; }
    public required string Database { get; init; }
    public required string Text { get; init; }
    public ObjectKind[]? Kinds { get; init; }
    public int? Limit { get; init; }
    /// <summary>
    /// Phase D0d: the session whose view this is. Required for an in-memory database (its objects exist only in that
    /// session) and adds a session's own objects (temporary tables) to a shared snapshot; it must belong to the profile.
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>Enable / Disable: triggers only; the statement is text for a new tab, never executed by the sidecar.</summary>
public enum ScriptKind { Create, Alter, DropCreate, Drop, Execute, SelectTop, Enable, Disable }

public sealed record ScriptParams
{
    public required string SessionId { get; init; }
    public required ObjectRef Object { get; init; }
    public ScriptKind Kind { get; init; }
}

public sealed record SqlText
{
    public required string Sql { get; init; }
}

public sealed record RowCountsParams
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    public required long[] ObjectIds { get; init; }
}

/// <summary>Rows per table for the ER diagram's "Load row counts"; null when the server has no count yet.</summary>
public sealed record RowCount
{
    public long ObjectId { get; init; }
    public long? Rows { get; init; }
    /// <summary>Planner statistics (PostgreSQL reltuples, SQL Server sys.partitions), not an exact count.</summary>
    public bool Estimated { get; init; }
    /// <summary>Phase D4: when the statistics the estimate comes from were gathered (Unix ms; Oracle LAST_ANALYZED), null when the engine does not say.</summary>
    public long? AnalyzedAt { get; init; }
}
