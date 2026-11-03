namespace Querithm.Contract;

// Schema compare: two live databases or schemas, or a saved model file against a live one.

/// <summary>
/// One side of a compare: a live database on a session (optionally one schema of it), or a model file saved earlier
/// (<c>fileToken</c>; the shell swaps it for <c>filePath</c>, the webview never names a path).
/// </summary>
public sealed record CompareSide
{
    public string? SessionId { get; init; }
    public string? Database { get; init; }
    /// <summary>Compare only this schema. When both sides name one, objects are matched by name across the two schemas.</summary>
    public string? Schema { get; init; }
    public string? FileToken { get; init; }
    public string? FilePath { get; init; }
}

public sealed record SchemaCompareParams
{
    public required CompareSide Source { get; init; }
    public required CompareSide Target { get; init; }
}

public enum CompareClass { Identical, Different, SourceOnly, TargetOnly }

public enum DiffTag { Columns, Indexes, Constraints, Body, WhitespaceOnly, Options }

public enum CompareObjectKind { Schema, Extension, Type, Sequence, Table, View, Function, Procedure, Trigger }

public sealed record CompareItem
{
    /// <summary>Stable id within one compare (kind, target-side schema, name, signature).</summary>
    public required string Key { get; init; }
    public CompareObjectKind Kind { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    /// <summary>PostgreSQL function arguments (overloads), or the table of a trigger.</summary>
    public string? Detail { get; init; }
    public CompareClass Class { get; init; }
    public required DiffTag[] Tags { get; init; }
    /// <summary>What differs, one readable line each ("column qty: int → bigint").</summary>
    public required string[] Changes { get; init; }
    /// <summary>Applying this item to the target drops or narrows data (drop table/column, type change).</summary>
    public bool DataLoss { get; init; }
    /// <summary>Why the item cannot be compared or scripted in full (encrypted module, unsupported feature).</summary>
    public string? Note { get; init; }
}

/// <summary>A column only in the source and one only in the target of the same table, with the same type: maybe a rename.</summary>
public sealed record PossibleRename
{
    public required string TableKey { get; init; }
    /// <summary>The target's column (it would be renamed).</summary>
    public required string From { get; init; }
    /// <summary>The source's column (the new name).</summary>
    public required string To { get; init; }
    public required string Type { get; init; }
}

public sealed record CompareSideInfo
{
    public required string Label { get; init; }
    public Engine Engine { get; init; }
    public string? ServerVersion { get; init; }
    public long CapturedAt { get; init; }
    /// <summary>False for a saved model file.</summary>
    public bool Live { get; init; }
}

public sealed record SchemaCompareResult
{
    public required string CompareId { get; init; }
    public required CompareSideInfo Source { get; init; }
    public required CompareSideInfo Target { get; init; }
    public required CompareItem[] Items { get; init; }
    public required PossibleRename[] PossibleRenames { get; init; }
    /// <summary>Things left out of the compare on either side, with the reason.</summary>
    public required string[] Notes { get; init; }
    /// <summary>Null when a migration can be generated; otherwise why not (target is a saved file).</summary>
    public string? MigrationRefusal { get; init; }
    /// <summary>False where DDL commits implicitly (MySQL): the script cannot roll back, and the UI says so.</summary>
    public bool TransactionalDdl { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record CompareObjectParams
{
    public required string CompareId { get; init; }
    public required string Key { get; init; }
}

/// <summary>The object scripted from each side's model for the diff view; null where it does not exist.</summary>
public sealed record CompareObjectSql
{
    public string? Source { get; init; }
    public string? Target { get; init; }
}

public sealed record SchemaMigrationParams
{
    public required string CompareId { get; init; }
    /// <summary>The checked items; anything else stays as it is in the target.</summary>
    public required string[] Keys { get; init; }
    /// <summary>Possible renames the user confirmed (otherwise drop + add).</summary>
    public PossibleRename[]? Renames { get; init; }
    /// <summary>Include drops of tables and columns and narrowing type changes; when false they are listed as skipped.</summary>
    public bool IncludeDataLoss { get; init; }
}

public sealed record SchemaMigrationResult
{
    public required string Sql { get; init; }
    public int Statements { get; init; }
    /// <summary>Steps that need attention (dependents dropped and recreated, rebuilds that are not automated).</summary>
    public required string[] Warnings { get; init; }
    /// <summary>Data-loss steps left out because <see cref="SchemaMigrationParams.IncludeDataLoss"/> was false.</summary>
    public required string[] Skipped { get; init; }
    public bool Transactional { get; init; }
}

public sealed record SchemaExportParams
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    public string? Schema { get; init; }
}

/// <summary>A schema model for saving to a file (the webview saves it through the shell's save dialog).</summary>
public sealed record SchemaModelFile
{
    public required string FileName { get; init; }
    public required string Json { get; init; }
}
