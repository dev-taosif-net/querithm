namespace Querithm.Contract;

public enum ImportFormat { Auto, Csv, Tsv, Json, Ndjson, Xlsx }

/// <summary>Engine-neutral type of a file column, inferred from every value.</summary>
public enum ImportKind { Empty, Bool, Int32, Int64, Decimal, Double, Date, DateTime, DateTimeOffset, Time, Uuid, Text }

public enum ImportVerdictKind { Fits, Widen, Mismatch, NullIntoNotNull, MissingInTarget, RequiredUnmapped }

public enum ImportTransactionMode { Single, Batches }

public sealed record ImportReadOptions
{
    public ImportFormat Format { get; init; }
    /// <summary>Delimited text: the separator; null = detect.</summary>
    public string? Delimiter { get; init; }
    public bool? HasHeader { get; init; }
    /// <summary>utf-8, utf-16le, utf-16be, latin1; null = detect from the BOM / content.</summary>
    public string? Encoding { get; init; }
    /// <summary>XLSX: the sheet name; null = the first sheet.</summary>
    public string? Sheet { get; init; }
    /// <summary>Values read as NULL (default: empty and NULL).</summary>
    public string[]? NullValues { get; init; }
}

public sealed record ImportInspectParams
{
    /// <summary>Opaque token from the shell's file dialog. The shell swaps it for <see cref="FilePath"/>; a path sent by the webview is dropped.</summary>
    public required string FileToken { get; init; }
    /// <summary>Injected by the shell (Rust / dev bridge), never by the webview.</summary>
    public string? FilePath { get; init; }
    public ImportReadOptions? Options { get; init; }
}

public sealed record ImportColumnProfile
{
    public required string Name { get; init; }
    public int Ordinal { get; init; }
    public ImportKind Kind { get; init; }
    /// <summary>Decimal: digits before and after the point; temporal: fractional-second digits.</summary>
    public int Precision { get; init; }
    public int Scale { get; init; }
    public int MaxLength { get; init; }
    public long NullCount { get; init; }
    /// <summary>File row numbers (1-based, header excluded) of the first NULLs.</summary>
    public long[] FirstNullRows { get; init; } = [];
    /// <summary>HyperLogLog estimate of distinct non-null values.</summary>
    public long DistinctEstimate { get; init; }
    /// <summary>Smallest and largest whole number (Int32/Int64 columns), as text.</summary>
    public string? Min { get; init; }
    public string? Max { get; init; }
    /// <summary>Every row has a value and the values look unique: a candidate key.</summary>
    public bool CandidateKey { get; init; }
}

public sealed record ImportInspectResult
{
    public required string InspectionId { get; init; }
    public required string FileName { get; init; }
    public long FileBytes { get; init; }
    public ImportFormat Format { get; init; }
    public string? Delimiter { get; init; }
    public bool HasHeader { get; init; }
    public required string Encoding { get; init; }
    public string[] Sheets { get; init; } = [];
    public string? Sheet { get; init; }
    public long RowCount { get; init; }
    public required ImportColumnProfile[] Columns { get; init; }
    /// <summary>The first 200 rows as Arrow frames on the data plane (read like an execution).</summary>
    public required string PreviewExecutionId { get; init; }
    public string[] Notes { get; init; } = [];
}

public sealed record ImportMapping
{
    public required string FileColumn { get; init; }
    /// <summary>Null = skip the file column.</summary>
    public string? TargetColumn { get; init; }
}

public sealed record ImportTargetParams
{
    public required string SessionId { get; init; }
    public required string InspectionId { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    /// <summary>True = a new table (the result carries its CREATE TABLE); false = an existing one (compatibility verdicts).</summary>
    public bool NewTable { get; init; }
    /// <summary>Null = map file columns to target columns by name.</summary>
    public ImportMapping[]? Mapping { get; init; }
    /// <summary>New table: per file column, the type the user picked instead of the inferred one.</summary>
    public Dictionary<string, string>? TypeOverrides { get; init; }
    /// <summary>New table: file columns that form the primary key.</summary>
    public string[]? PrimaryKey { get; init; }
}

public sealed record ImportColumnVerdict
{
    public required string FileColumn { get; init; }
    public string? TargetColumn { get; init; }
    public ImportVerdictKind Kind { get; init; }
    public required string Message { get; init; }
    /// <summary>DDL that would resolve it (ALTER COLUMN / ADD), to review before running.</summary>
    public string? SuggestedSql { get; init; }
}

public sealed record ImportTargetResult
{
    public bool TableExists { get; init; }
    public required ImportMapping[] Mapping { get; init; }
    public required ImportColumnVerdict[] Verdicts { get; init; }
    /// <summary>New table: the CREATE TABLE statement (editable in the wizard).</summary>
    public string? CreateTableSql { get; init; }
    /// <summary>Per file column, the type the new table would use.</summary>
    public Dictionary<string, string>? ProposedTypes { get; init; }
}

public sealed record ImportRunOptions
{
    public ImportTransactionMode TransactionMode { get; init; }
    /// <summary>Batches mode: rows per committed batch.</summary>
    public int? CommitEvery { get; init; }
    /// <summary>Rows that fail conversion go to the reject log; the import stops when more than this many fail (0 = stop at the first).</summary>
    public int MaxRejects { get; init; }
    public bool KeepIdentity { get; init; }
    public bool FireTriggers { get; init; }
    public bool CheckConstraints { get; init; } = true;
    public bool TableLock { get; init; }
}

public sealed record ImportRunParams
{
    public required string SessionId { get; init; }
    public required string InspectionId { get; init; }
    public required string FileToken { get; init; }
    /// <summary>Injected by the shell (one-time: the token cannot run a second import; a Production guard block does not spend it).</summary>
    public string? FilePath { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public ImportMapping[]? Mapping { get; init; }
    public ImportRunOptions? Options { get; init; }
    /// <summary>Set only after the user confirmed the production data-change dialog (§13 rule 7).</summary>
    public bool ConfirmedProd { get; init; }
}

public sealed record ImportRunResult
{
    public string? JobId { get; init; }
    public GuardBlock? Blocked { get; init; }
}

public sealed record ImportJobRef
{
    public required string JobId { get; init; }
}

public sealed record ImportCancelResult
{
    public bool Cancelled { get; init; }
}

/// <summary>Profiling (inspect) and loading (run) progress, every 250 ms and once at the end.</summary>
public sealed record ImportProgressEvent
{
    /// <summary>The inspection id while profiling, the job id while loading.</summary>
    public required string JobId { get; init; }
    /// <summary>profiling | loading</summary>
    public required string Phase { get; init; }
    public long RowsProcessed { get; init; }
    public long RowsLoaded { get; init; }
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public long Rejected { get; init; }
    public double ElapsedMs { get; init; }
    public bool Done { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    /// <summary>Where the rejected rows were written (CSV with the reason per row).</summary>
    public string? RejectFile { get; init; }
    /// <summary>Batches mode after a failure: rows already committed (resume after this row).</summary>
    public long? CommittedRows { get; init; }
    /// <summary>
    /// With <see cref="Error"/>: <c>outcomeUnknown</c> when the import's own COMMIT was cut short (a lost connection, a
    /// cancel), so whether its rows were written is unknown — the code grid Apply uses; the wizard then refuses a blind
    /// retry into that table.
    /// </summary>
    public string? ErrorCode { get; init; }
}
