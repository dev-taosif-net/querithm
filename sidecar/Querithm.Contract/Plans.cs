namespace Querithm.Contract;

public enum PlanMode { Estimated, Actual }

public sealed record PlanParams
{
    public required string SessionId { get; init; }
    public required string Sql { get; init; }
    public PlanMode Mode { get; init; }
    /// <summary>Actual plans execute the query; the UI must confirm first (§11).</summary>
    public bool ConfirmedRun { get; init; }
    /// <summary>
    /// The query guard's confirmations, as for query.execute (§13 rule 7): the sidecar classifies the text and answers
    /// <c>{blocked}</c> until the user confirmed the first run on Prod / the destructive statements found.
    /// </summary>
    public bool ConfirmedProd { get; init; }
    public bool ConfirmedDestructive { get; init; }
}

public sealed record PlanWarning
{
    /// <summary>missingIndex | implicitConversion | spill | noJoinPredicate | cardinality | other</summary>
    public required string Kind { get; init; }
    public required string Message { get; init; }
}

public sealed record PlanNode
{
    public required string Id { get; init; }
    public required string Operator { get; init; }
    public required string PhysicalOp { get; init; }
    public string? LogicalOp { get; init; }
    public string? ObjectName { get; init; }
    public double EstRows { get; init; }
    public double? ActualRows { get; init; }
    public double? ActualExecutions { get; init; }
    /// <summary>This operator's own cost.</summary>
    public double EstCost { get; init; }
    public double SubtreeCost { get; init; }
    /// <summary>EstCost as a percentage of the statement cost.</summary>
    public double? CostPercent { get; init; }
    public double? ElapsedMs { get; init; }
    public long? LogicalReads { get; init; }
    public required PlanWarning[] Warnings { get; init; }
    public required Dictionary<string, string> Properties { get; init; }
    public required PlanNode[] Children { get; init; }
}

public sealed record MissingIndex
{
    public double Impact { get; init; }
    public required string Table { get; init; }
    public required string[] EqualityColumns { get; init; }
    public required string[] InequalityColumns { get; init; }
    public required string[] IncludeColumns { get; init; }
    public required string CreateStatement { get; init; }
}

public sealed record PlanStatement
{
    public string? Text { get; init; }
    public double? SubtreeCost { get; init; }
    public PlanNode? Root { get; init; }
    public required MissingIndex[] MissingIndexes { get; init; }
    public required string[] Warnings { get; init; }
    public double? ElapsedMs { get; init; }
    public double? CpuMs { get; init; }
}

public sealed record PlanTree
{
    public required PlanStatement[] Statements { get; init; }
}

public sealed record PlanResult
{
    public required PlanTree Plan { get; init; }
    public required string Raw { get; init; }
    /// <summary>xml | json</summary>
    public required string Format { get; init; }
}

/// <summary>
/// A plan the user already has as text (a result cell holding SQL Server showplan XML or a PostgreSQL EXPLAIN (FORMAT JSON)
/// value) parsed into the plan view's tree. Untrusted: size-capped and parsed without DTDs; nothing runs on a server.
/// </summary>
public sealed record PlanParseParams
{
    public required Engine Engine { get; init; }
    public required string Raw { get; init; }
}

public sealed record PlanParseResult
{
    public required PlanTree Plan { get; init; }
    /// <summary>xml | json</summary>
    public required string Format { get; init; }
}
