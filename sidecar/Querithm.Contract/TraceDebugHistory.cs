namespace Querithm.Contract;

// ---- SQL Server Trace (Extended Events, §11) --------------------------------------------------

public sealed record TraceStartParams
{
    public required string SessionId { get; init; }
    /// <summary>Only "spid" in v1.</summary>
    public string? Scope { get; init; }
}

public sealed record TraceRef
{
    public required string TraceId { get; init; }
}

public sealed record TraceEvent
{
    public long Seq { get; init; }
    /// <summary>Epoch microseconds.</summary>
    public long Ts { get; init; }
    public required string EventName { get; init; }
    public string? ProcName { get; init; }
    public long? ObjectId { get; init; }
    public int? Line { get; init; }
    public int? OffsetStart { get; init; }
    public int? OffsetEnd { get; init; }
    public long? DurationUs { get; init; }
    public long? RowCount { get; init; }
    public string? Statement { get; init; }
    public string? Error { get; init; }
}

public sealed record TraceEventsEvent
{
    public required string TraceId { get; init; }
    public required string SessionId { get; init; }
    public required TraceEvent[] Events { get; init; }
    public bool Stopped { get; init; }
    public string? Error { get; init; }
}

// ---- PostgreSQL debugger (pldebugger, §11) ----------------------------------------------------

public sealed record DebugAttachParams
{
    public required string SessionId { get; init; }
    public long FunctionOid { get; init; }
    /// <summary>The statement the target connection runs, e.g. SELECT public.f(1,2).</summary>
    public required string CallSql { get; init; }
    public int[]? Breakpoints { get; init; }
    /// <summary>The query guard's confirmations for the call (§13 rule 7), as for query.execute.</summary>
    public bool ConfirmedProd { get; init; }
    public bool ConfirmedDestructive { get; init; }
}

public sealed record DebugRef
{
    public required string DebugId { get; init; }
}

/// <summary>debug.attach: the debug session, or the guard's block (the call was not run).</summary>
public sealed record DebugAttachResult
{
    public string? DebugId { get; init; }
    public GuardBlock? Blocked { get; init; }
}

public sealed record DebugBreakpointParams
{
    public required string DebugId { get; init; }
    public long FunctionOid { get; init; }
    public int Line { get; init; }
}

public enum DebugStepKind { Over, Into, Continue }

public sealed record DebugStepParams
{
    public required string DebugId { get; init; }
    public DebugStepKind Kind { get; init; }
}

public sealed record DebugFrame
{
    public int Level { get; init; }
    public required string Function { get; init; }
    public long FunctionOid { get; init; }
    public int Line { get; init; }
    public string? Args { get; init; }
}

public sealed record DebugVariable
{
    public required string Name { get; init; }
    public string? Value { get; init; }
    public string? Type { get; init; }
    /// <summary>A = argument, L = local.</summary>
    public string? VarClass { get; init; }
    public bool IsNull { get; init; }
}

public enum DebugEventKind { Attached, Paused, Running, Finished, Error }

public sealed record DebugEvent
{
    public required string DebugId { get; init; }
    public DebugEventKind Kind { get; init; }
    public long? FunctionOid { get; init; }
    public int? Line { get; init; }
    public string? Message { get; init; }
    public DebugFrame[]? Stack { get; init; }
    public DebugVariable[]? Variables { get; init; }
}

public sealed record DebugSourceParams
{
    public required string DebugId { get; init; }
    public long FunctionOid { get; init; }
}

public sealed record DebugSource
{
    public required string Source { get; init; }
}

// ---- History ----------------------------------------------------------------------------------

public sealed record HistoryListParams
{
    public string? ProfileId { get; init; }
    public string? Database { get; init; }
    public int? Limit { get; init; }
    public long? BeforeId { get; init; }
}

public sealed record HistoryGetParams
{
    public long Id { get; init; }
}

/// <summary>
/// L17: how much history the store keeps — entries older than <see cref="Days"/>, and the oldest beyond
/// <see cref="MaxEntries"/>, are deleted (at start, after every <see cref="HistoryLimits.PruneEvery"/> runs, and when this
/// changes). Validated in the sidecar against <see cref="HistoryLimits"/>.
/// </summary>
public sealed record HistoryRetention
{
    public int Days { get; init; } = HistoryLimits.DefaultDays;
    public int MaxEntries { get; init; } = HistoryLimits.DefaultMaxEntries;
}

/// <summary>History limits (L17, §8).</summary>
public static class HistoryLimits
{
    public const int DefaultDays = 90;
    public const int DefaultMaxEntries = 50_000;
    public const int MinDays = 1;
    public const int MaxDays = 3_650;
    public const int MinEntries = 1_000;
    public const int MaxEntries = 1_000_000;
    /// <summary>Characters of each statement history.list / history.search send.</summary>
    public const int PreviewChars = 500;
    /// <summary>Runs recorded between two retention passes.</summary>
    public const int PruneEvery = 500;
    /// <summary>Rows one delete statement of a retention pass removes (one short write transaction each).</summary>
    public const int PruneBatch = 1_000;
}

public sealed record HistorySearchParams
{
    public required string Text { get; init; }
    public string? ProfileId { get; init; }
    public int? Limit { get; init; }
}

public sealed record HistoryEntry
{
    public long Id { get; init; }
    public string? ProfileId { get; init; }
    public string? Database { get; init; }
    public long ExecutedAt { get; init; }
    /// <summary>
    /// The statement text. history.list / history.search send a preview — its first <see cref="HistoryLimits.PreviewChars"/>
    /// characters (L17) — and <see cref="SqlTruncated"/> says when the rest is left out; history.get sends all of it.
    /// </summary>
    public required string Sql { get; init; }
    /// <summary>The whole text's length in characters.</summary>
    public long SqlLength { get; init; }
    /// <summary><see cref="Sql"/> is a preview: fetch the whole text with history.get.</summary>
    public bool SqlTruncated { get; init; }
    public double? ElapsedMs { get; init; }
    public long? RowsAffected { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
}
