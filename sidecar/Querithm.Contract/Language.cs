namespace Querithm.Contract;

public enum CompletionKind
{
    Keyword, Table, View, Column, Proc, Func, Variable, Parameter, Schema, Database, Alias, Cte,
    Snippet, Type, TempTable, Cursor
}

public sealed record CompleteParams
{
    public required string DocId { get; init; }
    public required string Text { get; init; }
    /// <summary>UTF-16 offset of the cursor.</summary>
    public int Offset { get; init; }
    public Dialect Dialect { get; init; }
    public string? ProfileId { get; init; }
    public string? Database { get; init; }
    /// <summary>Phase D0d: the tab's session, so completion sees its in-memory database or temporary tables.</summary>
    public string? SessionId { get; init; }
}

public sealed record CompletionItem
{
    public required string Label { get; init; }
    public CompletionKind Kind { get; init; }
    public string? Detail { get; init; }
    public string? InsertText { get; init; }
    public bool IsSnippet { get; init; }
    public string? SortText { get; init; }
    public string? Documentation { get; init; }
}

/// <summary>
/// M12: the webview disposed a document (a closed tab, a deleted notebook cell); every engine drops what it cached for
/// that docId (a document can change dialect, so all of them).
/// </summary>
public sealed record LangForgetParams
{
    public required string DocId { get; init; }
}

public sealed record DiagnosticsParams
{
    public required string DocId { get; init; }
    public required string Text { get; init; }
    public Dialect Dialect { get; init; }
}

public sealed record Diagnostic
{
    /// <summary>1-based.</summary>
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public required string Message { get; init; }
    public Severity Severity { get; init; }
}

public enum KeywordCase { Upper, Lower, Preserve }

public sealed record FormatOptions
{
    public KeywordCase KeywordCase { get; init; }
    public int? IndentSize { get; init; }
    public bool LeadingCommas { get; init; }
    public bool? NewlineBeforeJoin { get; init; }
}

public sealed record FormatParams
{
    public required string Text { get; init; }
    public Dialect Dialect { get; init; }
    public FormatOptions? Options { get; init; }
}

public sealed record FormatResult
{
    public required string Text { get; init; }
    public string? Error { get; init; }
}

public sealed record SignatureParams
{
    public required string DocId { get; init; }
    public required string Text { get; init; }
    public int Offset { get; init; }
    public Dialect Dialect { get; init; }
    public string? ProfileId { get; init; }
    public string? Database { get; init; }
    /// <summary>Phase D0d: the tab's session (see <see cref="CompleteParams.SessionId"/>).</summary>
    public string? SessionId { get; init; }
}

public sealed record ParameterInfo
{
    public required string Label { get; init; }
    public string? Documentation { get; init; }
}

public sealed record SignatureInfo
{
    public required string Label { get; init; }
    public string? Documentation { get; init; }
    public required ParameterInfo[] Parameters { get; init; }
}

public sealed record SignatureHelp
{
    public required SignatureInfo[] Signatures { get; init; }
    public int ActiveSignature { get; init; }
    public int ActiveParameter { get; init; }
}

public sealed record FoldingParams
{
    public required string DocId { get; init; }
    public required string Text { get; init; }
    public Dialect Dialect { get; init; }
}

public sealed record FoldingRange
{
    /// <summary>1-based.</summary>
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string? Kind { get; init; }
}

public sealed record ClassifyParams
{
    public required string Text { get; init; }
    public Dialect Dialect { get; init; }
}

public sealed record BatchInfo
{
    public int Index { get; init; }
    /// <summary>1-based line of the batch's first character.</summary>
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int StartOffset { get; init; }
    public int Length { get; init; }
    /// <summary>GO n repeat count.</summary>
    public int RepeatCount { get; init; } = 1;
}

public sealed record Classification
{
    public required DestructiveFinding[] Destructive { get; init; }
    public bool HasDdl { get; init; }
    public bool BeginsTransaction { get; init; }
    public bool EndsTransaction { get; init; }
    public string? UseDatabase { get; init; }
    public required BatchInfo[] Batches { get; init; }
    public required Diagnostic[] ParseErrors { get; init; }
    /// <summary>
    /// Phase D0c/D1: a statement the engine's file policy refuses outright (ATTACH, VACUUM INTO, extension loading, a
    /// filesystem-changing pragma). A hard error, never a confirmable guard: <c>confirmedDestructive</c> does not bypass it.
    /// </summary>
    public string? Refusal { get; init; }
}
