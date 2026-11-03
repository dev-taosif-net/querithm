namespace Querithm.Contract;

// Find usages and safe rename.

public enum RefactorKind { Table, View, Procedure, Function, Column, Type, Sequence }

/// <summary>A schema object, or a column (<see cref="Schema"/>.<see cref="Name"/> is then its table).</summary>
public sealed record RefactorTarget
{
    public RefactorKind Kind { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public string? Column { get; init; }
}

/// <summary>An open editor tab: searched as it is now, unsaved text included.</summary>
public sealed record OpenTabText
{
    public required string TabId { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
}

public sealed record TextSpan
{
    public int Offset { get; init; }
    public int Length { get; init; }
}

public sealed record SymbolAtParams
{
    /// <summary>Resolves names against this session's database; without one only local symbols are found.</summary>
    public string? SessionId { get; init; }
    public string? Database { get; init; }
    public Dialect Dialect { get; init; }
    public required string Text { get; init; }
    public int Offset { get; init; }
}

public sealed record SymbolAtResult
{
    /// <summary>The schema object or column at the cursor.</summary>
    public RefactorTarget? Target { get; init; }
    /// <summary>A local symbol (variable, parameter, CTE, alias): its occurrences, renamed in the editor only.</summary>
    public TextSpan[]? Local { get; init; }
}

public enum UsageConfidence { Exact, Possible }

public sealed record UsageHit
{
    /// <summary>Stable within one search: the object (or tab) and the offset.</summary>
    public required string Id { get; init; }
    public string? TabId { get; init; }
    public ObjectRef? Object { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public required string Snippet { get; init; }
    public UsageConfidence Confidence { get; init; }
    public string? Reason { get; init; }
    /// <summary>The object's own name in its CREATE header.</summary>
    public bool Definition { get; init; }
}

/// <summary>An object that may reference the target but cannot be checked (dynamic SQL, encrypted), and why.</summary>
public sealed record UncheckedObject
{
    public required ObjectRef Object { get; init; }
    public required string Reason { get; init; }
}

public sealed record FindUsagesParams
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    public required RefactorTarget Target { get; init; }
    public OpenTabText[]? Tabs { get; init; }
}

public sealed record FindUsagesResult
{
    public required RefactorTarget Target { get; init; }
    public required UsageHit[] Hits { get; init; }
    public required UncheckedObject[] Unchecked { get; init; }
    /// <summary>Objects the server stores parsed (PostgreSQL views, BEGIN ATOMIC functions): they follow a rename by themselves.</summary>
    public required ObjectRef[] ServerUpdated { get; init; }
    public int ObjectsScanned { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record RenameParams
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    public required RefactorTarget Target { get; init; }
    public required string NewName { get; init; }
    public OpenTabText[]? Tabs { get; init; }
    /// <summary>Possible hits the user checked (they are left out by default).</summary>
    public string[]? Include { get; init; }
    /// <summary>Exact hits the user unchecked.</summary>
    public string[]? Exclude { get; init; }
}

public sealed record RenameObjectPreview
{
    public required string Key { get; init; }
    public required ObjectRef Object { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }
    public required UsageHit[] Hits { get; init; }
    /// <summary>The server rewrites it itself; shown, not scripted.</summary>
    public bool ServerUpdated { get; init; }
}

public sealed record TextEdit
{
    public int Offset { get; init; }
    public int Length { get; init; }
    public required string Text { get; init; }
}

public sealed record TabEdits
{
    public required string TabId { get; init; }
    public required TextEdit[] Edits { get; init; }
}

public sealed record RenamePreviewResult
{
    public required RenameObjectPreview[] Objects { get; init; }
    public required TabEdits[] Tabs { get; init; }
    /// <summary>The rename and every rewritten object in one guarded transaction; text only, run from a query tab on the database.</summary>
    public required string Sql { get; init; }
    public int Statements { get; init; }
    public required string[] Warnings { get; init; }
    public required UncheckedObject[] Unchecked { get; init; }
    public bool Transactional { get; init; }
}
