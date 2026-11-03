namespace Querithm.Contract;

// Data compare: two tables or queries on any two sessions, merged on a key while both stream.

/// <summary>A table (resolved against the sidecar's own snapshot) or a read-only query.</summary>
public sealed record DataCompareSide
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    public string? Schema { get; init; }
    public string? Table { get; init; }
    /// <summary>One SELECT; run read-only. Mutually exclusive with <see cref="Table"/>.</summary>
    public string? Query { get; init; }
}

/// <summary>A source column and the target column it is compared with.</summary>
public sealed record ColumnPair
{
    public required string Source { get; init; }
    public required string Target { get; init; }
}

public sealed record DataCompareOptions
{
    /// <summary>Floating-point values within this relative difference are equal.</summary>
    public double FloatEpsilon { get; init; } = 1e-9;
    /// <summary>Date/time values within this many milliseconds are equal (datetime vs timestamp precision).</summary>
    public int TimeToleranceMs { get; init; }
    public bool IgnoreCase { get; init; }
    /// <summary>Trailing spaces of char/nchar values are not significant (SQL Server and PostgreSQL both pad them).</summary>
    public bool TrimCharPadding { get; init; } = true;
    /// <summary>Different rows kept for the grid and the sync script; the rest are only counted.</summary>
    public int MaxDiffRows { get; init; } = 50_000;
}

public sealed record DataCompareParams
{
    public required DataCompareSide Source { get; init; }
    public required DataCompareSide Target { get; init; }
    /// <summary>Source key columns (target names come from <see cref="Columns"/>); default: the source table's primary key.</summary>
    public string[]? Key { get; init; }
    /// <summary>Compared columns; default: every source column with a same-named target column (names compared case-insensitively).</summary>
    public ColumnPair[]? Columns { get; init; }
    public DataCompareOptions? Options { get; init; }
}

public sealed record DataCompareStart
{
    public required string CompareId { get; init; }
    /// <summary>Diff rows arrive on the data plane as Arrow batches under this execution.</summary>
    public required string ExecutionId { get; init; }
    public required ColumnPair[] Key { get; init; }
    public required ColumnPair[] Columns { get; init; }
    /// <summary>What is not compared, or compared loosely, and why (types, a hint on isolation).</summary>
    public required string[] Notes { get; init; }
}

public sealed record DataCompareProgressEvent
{
    public required string CompareId { get; init; }
    public long SourceRows { get; init; }
    public long TargetRows { get; init; }
    public long Identical { get; init; }
    public long Different { get; init; }
    public long SourceOnly { get; init; }
    public long TargetOnly { get; init; }
    public bool Done { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>
    /// More differences than the compare keeps — <see cref="DataCompareOptions.MaxDiffRows"/>, or the bytes one comparison
    /// may hold (L21): the grid and script hold the first ones, <see cref="Kept"/> of them; <see cref="TruncatedNote"/> says why.
    /// </summary>
    public bool Truncated { get; init; }
    /// <summary>Differences kept so far (streamed to the grid, used by the sync script).</summary>
    public int Kept { get; init; }
    /// <summary>Set with <see cref="Truncated"/>: which limit stopped keeping differences, for the user.</summary>
    public string? TruncatedNote { get; init; }
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record DataCompareRef
{
    public required string CompareId { get; init; }
}

public sealed record DataSyncSqlParams
{
    public required string CompareId { get; init; }
    /// <summary>Insert the rows only in the source.</summary>
    public bool Insert { get; init; } = true;
    /// <summary>Update the rows that differ (changed columns only).</summary>
    public bool Update { get; init; } = true;
    /// <summary>Delete the rows only in the target.</summary>
    public bool Delete { get; init; }
}

public sealed record DataSyncSqlResult
{
    public required string Sql { get; init; }
    public int Statements { get; init; }
    public long Rows { get; init; }
    public required string[] Notes { get; init; }
}
