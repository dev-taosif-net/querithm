using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>Cached schema for (profile, database), read by tier-2 intelligence. Never hits a server.</summary>
public interface ISchemaSource
{
    SchemaSnapshot? Get(string? profileId, string? database);
    IReadOnlyList<string> Databases(string? profileId);
}

/// <summary>Dialect intelligence: AST/token based, never regex over query text (§15 rule 6).</summary>
public interface IDialectService
{
    Classification Classify(string text);
    CompletionItem[] Complete(CompleteParams p, ISchemaSource schema);
    Diagnostic[] Diagnostics(DiagnosticsParams p);
    FormatResult Format(FormatParams p);
    SignatureHelp Signature(SignatureParams p, ISchemaSource schema);
    FoldingRange[] Folding(FoldingParams p);

    /// <summary>
    /// The text with every secret literal (a password, a credential's or secret's key) masked as <c>&lt;redacted&gt;</c> (not a literal, so a re-run fails), on the
    /// engine's tokens (L4); applied before query text is kept (history, the monitor's text store).
    /// </summary>
    Redaction RedactSecrets(string text);

    /// <summary>
    /// M12, lang.forget: the document (a closed tab, a deleted notebook cell) is gone, so every cache this engine keeps
    /// for it (parse, tokens, statement splits, USE / SET scans) is dropped now instead of waiting for its bound.
    /// </summary>
    void Forget(string docId);
}

/// <summary>
/// One catalog object and its change marker; <paramref name="ParentObjectId"/> is a trigger's table or view.
/// <paramref name="SessionLocal"/> (Phase D0d): the object exists only for the connection that read it (a temporary
/// table): it goes to that session's view, never into the snapshot other tabs share.
/// <paramref name="NoRollbackEngine"/> (MySQL / MariaDB): the storage engine of a table whose changes cannot be rolled back.
/// <paramref name="Note"/> (Phase D4): what the explorer shows beside the name — "invalid", "materialized view", "external table".
/// </summary>
public sealed record ObjectStamp(long Id, string Schema, string Name, ObjectKind Kind, long Marker, string? ReturnType, long? ParentObjectId = null, bool SessionLocal = false,
    string? NoRollbackEngine = null, string? Note = null, long? SpecObjectId = null);

public sealed record ColumnRow(
    long ObjectId, int Ordinal, string Name, string TypeName, int? MaxLen, int? Precision, int? Scale,
    bool Nullable, bool Identity, bool Computed, string? Default);

public sealed record ParamRow(long ObjectId, int Ordinal, string Name, string TypeName, int? MaxLen, bool Output, bool HasDefault);

/// <summary>One index; <paramref name="NotAKey"/>: partial / filtered, an expression key part, disabled or invalid — never a grid row key.</summary>
public sealed record IndexRow(long ObjectId, string Name, bool Unique, bool Primary, string[] Columns, bool NotAKey = false);

public sealed record FkRow(string Name, long Parent, long Referenced, FkColumnPair[] Columns);

public sealed record MetadataBundle(
    IReadOnlyList<ColumnRow> Columns,
    IReadOnlyList<ParamRow> Params,
    IReadOnlyList<IndexRow> Indexes,
    IReadOnlyList<FkRow> Fks,
    IReadOnlyList<MemberRow>? Members = null);

/// <summary>Phase D4 (Oracle): one subprogram of a package specification as declared, display text only (<see cref="SnapObject.Members"/>).</summary>
public sealed record MemberRow(long ObjectId, int Ordinal, string Text);

/// <summary>Catalog queries (§8). Implementations batch by object id (chunks of 500).</summary>
public interface IMetadataProvider
{
    /// <summary>All objects with their change marker (modify_date ticks / xmin) in one query.</summary>
    Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(DbConnection connection, CancellationToken ct);
    Task<MetadataBundle> GetDetailsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct);
    Task<string?> GetDefinitionAsync(DbConnection connection, ObjectRef obj, CancellationToken ct);
    /// <summary>Definitions of many modules in one query per 500 ids; null for an encrypted module.</summary>
    Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct);
    Task<Dependency[]> GetDependenciesAsync(DbConnection connection, ObjectRef obj, CancellationToken ct);
    /// <summary>Turns a fetched definition into an editable script (CREATE → CREATE OR ALTER / OR REPLACE).</summary>
    string ToEditableScript(string definition, Capabilities caps);
    /// <summary>Row counts of tables from catalog statistics in one query (no table scans), chunked by 500 ids.</summary>
    Task<RowCount[]> GetRowCountsAsync(DbConnection connection, IReadOnlyList<long> objectIds, CancellationToken ct);

    /// <summary>
    /// Phase D4: what the last <see cref="GetStampsAsync"/> / <see cref="GetDetailsAsync"/> on this connection could not read
    /// — an object kind the login has no access to, with the reason — shown with the snapshot instead of failing the
    /// refresh. Empty for an engine whose catalog is read whole or not at all.
    /// </summary>
    IReadOnlyList<string> Notes(DbConnection connection) => [];
}

/// <summary>
/// Plans (§11). Core runs <see cref="GetPlanAsync"/> as one execution on the session (queueing, cancel escalation,
/// history) after the query guard, and wraps an actual plan in a transaction it always rolls back — a savepoint inside
/// the user's open transaction — through the provider's <see cref="ITransactionControl"/>, so the provider only plans.
/// </summary>
public interface IPlanProvider
{
    Task<PlanResult> GetPlanAsync(DbConnection connection, string sql, PlanMode mode, CancellationToken ct);
    PlanTree Parse(string raw);

    /// <summary>
    /// Why this engine will not plan <paramref name="sql"/> in <paramref name="mode"/> (shown to the user as is), or null.
    /// Checked by Core before anything runs: an engine without actual plans, text that would turn the plan mode off and
    /// run what follows, a statement whose actual plan could not be undone. Tokens or AST only, never regex over SQL.
    /// </summary>
    string? Refusal(string sql, PlanMode mode) => null;
}

/// <summary>Script as CREATE/ALTER/DROP/EXEC. SMO first; catalog-view fallback implements the same interface (§2).</summary>
public interface IObjectScripter
{
    Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, CancellationToken ct);

    /// <summary>Phase D4 (Oracle): <paramref name="withBody"/> — a package specification's script also carries its body. Other engines ignore it.</summary>
    Task<string> ScriptAsync(DbConnection connection, ObjectRef obj, ScriptKind kind, bool withBody, CancellationToken ct) => ScriptAsync(connection, obj, kind, ct);
}

/// <summary>SQL Server Extended Events trace (§11). Not a debugger.</summary>
public interface ITraceProvider
{
    Task<CapabilityInfo> CheckAsync(DbConnection connection, CancellationToken ct);
    Task StartAsync(DbConnection control, string sessionName, int spid, CancellationToken ct);
    Task<IReadOnlyList<TraceEvent>> PollAsync(DbConnection control, string sessionName, long afterSeq, CancellationToken ct);
    Task StopAsync(DbConnection control, string sessionName, CancellationToken ct);
    Task DropOrphansAsync(DbConnection control, string prefix, CancellationToken ct);
}

/// <summary>Step debugger (PostgreSQL pldebugger only in v1, §11).</summary>
public interface IDebugProvider
{
    Task<CapabilityInfo> CheckAsync(DbConnection connection, CancellationToken ct);
    Task<IDebugSession> AttachAsync(Func<CancellationToken, Task<DbConnection>> open, long functionOid, string callSql, Action<DebugEvent> onEvent, string debugId, CancellationToken ct);
}

public interface IDebugSession : IAsyncDisposable
{
    Task SetBreakpointAsync(long functionOid, int line, CancellationToken ct);
    Task DropBreakpointAsync(long functionOid, int line, CancellationToken ct);
    Task StepAsync(DebugStepKind kind, CancellationToken ct);
    Task<string> GetSourceAsync(long functionOid, CancellationToken ct);
}
