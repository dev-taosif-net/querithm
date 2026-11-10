using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>
/// One database engine. Core never references a driver type; everything engine-specific goes
/// through this interface (§7 Architecture: database-specific features stay explicit).
/// </summary>
/// <summary>A result column's source: a snapshot object and one of its column names.</summary>
public sealed record ColumnOrigin(long ObjectId, string Column);

public interface IDbProvider
{
    Engine Engine { get; }
    Dialect Dialect { get; }

    /// <summary>PostgreSQL: a database is a separate connection, so the pool key includes it (§6).</summary>
    bool DatabaseScopedConnections { get; }

    IDialectService Language { get; }

    /// <summary>
    /// SQL Server keeps running the next GO batch after an error (SSMS semantics); PostgreSQL scripts run one
    /// statement per batch and stop at the first error, since later statements in a failed transaction cannot run.
    /// </summary>
    bool StopOnBatchError { get; }

    /// <summary>
    /// True when closing the connection from another thread is a safe last resort for a command whose cancel the server
    /// never acknowledged: <c>Close()</c> returns at once and the pending driver call fails cleanly (SqlClient, measured:
    /// 24 ms / 36 ms). False by default: Npgsql's Close drains the running command (blocked over 25 s) and MySqlConnector's
    /// leaves the pending call with a NullReferenceException — both break such a connection themselves after their
    /// CancellationTimeout — and closing under a running native command (SQLite, DuckDB) is unsafe while their interrupt
    /// always stops it. A run that still does not stop is reported to the user instead (§7 step 4).
    /// </summary>
    bool CloseAbortsCommand => false;

    IMetadataProvider Metadata { get; }
    IPlanProvider Plans { get; }
    IObjectScripter Scripter { get; }
    ITraceProvider? Trace { get; }
    IDebugProvider? Debugger { get; }
    /// <summary>Writing grid edits back.</summary>
    IDataEditDialect DataEdit { get; }
    /// <summary>Importing files: types, compatibility checks, DDL and the native bulk loader.</summary>
    IImportDialect Import { get; }
    /// <summary>Schema compare and migration scripts; null where the engine has none yet (degrades with a reason).</summary>
    ISchemaCompareDialect? SchemaCompare { get; }
    /// <summary>Data compare; null where the engine has none yet (degrades with a reason).</summary>
    IDataCompareDialect? DataCompare { get; }
    /// <summary>Find usages and rename; null where the engine has no reference finder yet.</summary>
    IReferenceFinder? References { get; }
    /// <summary>Server activity monitor (Phase B); null degrades the monitor with a reason.</summary>
    IActivityProvider? Activity { get; }

    /// <summary>
    /// Null when the provider implements <paramref name="feature"/>, else the reason shown to the user (Phase D0a). Static:
    /// what depends on the server (version, permissions) is narrowed by <see cref="GetCapabilitiesAsync"/>. Every RPC that
    /// needs the feature refuses with this reason, whatever the UI shows.
    /// </summary>
    string? FeatureRefusal(Feature feature);

    /// <summary>
    /// Null when this engine accepts the profile's location and sign-in, else the reason (Phase D0a); checked on save,
    /// test and open, so an invalid combination never reaches the driver.
    /// </summary>
    string? ValidateProfile(Profile profile);

    /// <summary>The product behind an open connection (name and version as detected).</summary>
    ProductInfo Product(DbConnection connection);

    /// <summary>
    /// Phase D0d: who owns the profile's physical connections. Pooled: Core's per-profile pool lends one per execution
    /// (servers). SessionOwned: each session opens one lazily and keeps it until the tab closes (file and memory
    /// databases); nothing else borrows it, and helpers wait for the session's execution lock to use it.
    /// </summary>
    ConnectionLifetime LifetimeFor(Profile profile);

    /// <summary>Phase D0e: the engine's facts Core used to branch on (defaults, sign-ins, locations, name matching).</summary>
    EngineTraits Traits { get; }

    /// <summary>
    /// The profile rewritten to reach the server through a local SSH forward at host:port, keeping TLS
    /// checks against the real server name where the driver can; <c>Note</c> says what could not be kept.
    /// </summary>
    (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port);

    /// <summary>The server's TCP endpoint as the SSH bastion must reach it, or why it cannot be tunnelled.</summary>
    (string Host, int Port, string? Refusal) TunnelTarget(Profile profile);

    /// <summary>Connection string with driver pooling off (Core pools physical connections itself).</summary>
    string BuildConnectionString(Profile profile, string? secret, string? database);

    /// <summary>
    /// Drops whatever driver-level state the provider keeps for the profile's connections (L19: Npgsql data sources,
    /// which hold the connection string and its password). Called with the profile's pools: disconnect, delete, a
    /// changed secret or setting. Nothing by default: the other drivers keep no state of their own (pooling is off).
    /// </summary>
    void ForgetConnections(string profileId) { }

    /// <summary>
    /// Reads a connection string with this driver's own keyword rules into <paramref name="basis"/> (same engine).
    /// Fields the string does not name keep their basis values; throws ArgumentException when it is malformed.
    /// </summary>
    ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis);

    /// <summary>
    /// How typical a pasted connection string is of this engine, used only to rank drivers that read it equally well
    /// (Npgsql and MySqlConnector both accept Server / User ID / Port / Database). <paramref name="keywords"/> is the string
    /// split with ADO.NET quoting rules (case-insensitive keys, as typed); nothing here throws. None by default.
    /// </summary>
    ConnectionStringAffinity ConnectionStringAffinity(IReadOnlyDictionary<string, string> keywords) => default;

    /// <summary>
    /// The table column behind each column of the current result set (null where computed or unknown), used to
    /// mark keys in the grid. Exact or nothing: a guess would label the wrong column. <paramref name="resultInBatch"/>
    /// counts the batch's data result sets from 0.
    /// </summary>
    ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot);

    /// <summary>
    /// Why data result set <paramref name="resultInBatch"/> of <paramref name="batchText"/> cannot be edited in the grid
    /// whatever <see cref="ResultOrigins"/> reports (without the "Read-only: " prefix), or null. Grid-edit audit: a
    /// self-join gives both aliases one table id and SQLite names the leftmost table for a UNION's columns, so a row key
    /// could update another row than the one shown. Read from the engine's tokens or AST, never a regular expression.
    /// </summary>
    string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) => null;

    /// <summary>
    /// Creates and opens a physical connection. <paramref name="file"/> is the injected grant of a file profile (Phase
    /// D0c): the provider opens exactly that path in exactly that mode and never creates a file; Core checks the file's
    /// identity before and after. Server providers ignore it.
    /// </summary>
    Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct);

    Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct);
    string? CurrentDatabase(DbConnection connection);
    string ServerVersion(DbConnection connection);
    Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Begin, commit, rollback, savepoints and state for every caller (Phase D0b).</summary>
    ITransactionControl Transactions { get; }

    /// <summary>
    /// Phase D2: messages the driver does not push by itself, read after each successful batch (MySQL / MariaDB: SHOW
    /// WARNINGS). None by default.
    /// </summary>
    Task<IReadOnlyList<ProviderMessage>> AfterBatchMessagesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderMessage>>([]);

    /// <summary>
    /// Phase D4: <see cref="AfterBatchMessagesAsync(DbConnection, CancellationToken)"/> with the batch that just ran, for
    /// messages that depend on it (Oracle: the compile errors of the PL/SQL unit a <c>CREATE</c> made, which the driver
    /// does not raise). Lines are relative to <paramref name="batchText"/>; an Error marks the run failed.
    /// </summary>
    Task<IReadOnlyList<ProviderMessage>> AfterBatchMessagesAsync(DbConnection connection, string batchText, CancellationToken ct) =>
        AfterBatchMessagesAsync(connection, ct);

    /// <summary>
    /// Phase D4: messages to read after a batch that failed on a connection still open (Oracle: the DBMS_OUTPUT lines a
    /// block printed before it raised, which would otherwise surface after some later statement). None by default; never
    /// errors of their own (a failure here is logged and dropped).
    /// </summary>
    Task<IReadOnlyList<ProviderMessage>> FailedBatchMessagesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderMessage>>([]);

    /// <summary>
    /// Phase D4: turns the engine's server-output buffer on or off for this connection (Oracle DBMS_OUTPUT), so that
    /// <see cref="AfterBatchMessagesAsync(DbConnection, string, CancellationToken)"/> reads it back. Called before the
    /// first batch of every run with the session's setting, and where the script toggles it. Nothing by default.
    /// </summary>
    Task SetServerOutputAsync(DbConnection connection, bool on, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Phase D4: driver settings for a user command, applied after its parameters were added and before it runs (Oracle:
    /// bind by name, fetch sizes). Nothing by default.
    /// </summary>
    void PrepareCommand(DbCommand command) { }

    /// <summary>
    /// Phase D4: the shape of each column of the reader's current result set where the provider knows better than the
    /// reader's CLR type and <see cref="DbColumn"/> (ODP.NET reports NUMBER(12,3) as Double and no precision); null
    /// entries (or a null list) keep the default mapping. Read once per result set, before any row.
    /// </summary>
    IReadOnlyList<ColumnShape?>? DescribeColumns(DbDataReader reader) => null;

    /// <summary>Forwards server info/notice/print messages. Dispose to unsubscribe.</summary>
    IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage);

    /// <summary>Errors from a failed command, with server line numbers where known (relative to <paramref name="commandText"/>).</summary>
    IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null);

    /// <summary>Statements to run before/after the user's batches for the given options (SET STATISTICS ...).</summary>
    (string? Before, string? After) SessionOptionsSql(ExecuteOptions options);

    void AddParameter(DbCommand command, QueryParameter parameter);

    /// <summary>Lets the provider override the default CLR-type → Arrow mapping for one column.</summary>
    ColumnHint GetColumnHint(DbColumn column);

    /// <summary>Reads a column flagged <see cref="ColumnHint.ProviderString"/> or a decimal that overflows System.Decimal.</summary>
    string? ReadAsString(DbDataReader reader, int ordinal);

    /// <summary>True when this result set is an actual-plan XML set (SET STATISTICS XML ON).</summary>
    bool IsPlanResultSet(DbDataReader reader);

    Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct);
    Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct);
    Task ValidateAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Statement that cancels server work for this connection out of band.</summary>
    void Cancel(DbCommand command);
}

/// <summary>
/// Per-engine facts as data (Phase D0e), so Core never branches on <see cref="Engine"/> for them: a new engine states its
/// own instead of silently taking SQL Server's or PostgreSQL's.
/// </summary>
public sealed record EngineTraits
{
    /// <summary>Display name ("SQL Server").</summary>
    public required string Name { get; init; }
    /// <summary>The database a session opens when the profile names none (PostgreSQL "postgres"; null = the login's default).</summary>
    public string? DefaultDatabase { get; init; }
    /// <summary>The schema unqualified names resolve to ("dbo", "public"); null where the engine has none.</summary>
    public string? DefaultSchema { get; init; }
    public int? DefaultPort { get; init; }
    /// <summary>The sign-in a new profile (or a connection string read for this engine) starts with.</summary>
    public required AuthKind DefaultAuth { get; init; }
    public required AuthKind[] AuthKinds { get; init; }
    public required LocationKind[] Locations { get; init; }
    /// <summary>
    /// How user-given names are matched against catalog names (refactor targets, compare column pairing) — not identity:
    /// identity keys (pools, caches, the store) always keep exact names, so distinct objects are never merged.
    /// SQL Server: case-insensitive (its default collations); PostgreSQL: exact (the catalog holds folded names).
    /// </summary>
    public required StringComparer NameMatch { get; init; }
    /// <summary>
    /// Phase D1: a command timeout the engine's driver needs whatever the profile says — Microsoft.Data.Sqlite uses the
    /// command timeout only as its lock-wait (SQLITE_BUSY) budget and retries forever at 0, so SQLite fixes it. Null = the
    /// profile's command timeout.
    /// </summary>
    public int? FixedCommandTimeoutS { get; init; }
    /// <summary>
    /// Phase D4 (O6): why the engine cannot run on this computer at all (Oracle's driver has no Intel macOS build), or null.
    /// The engine is listed with the reason (the picker shows it disabled), and every profile of it is refused with it.
    /// </summary>
    public string? Unavailable { get; init; }
}

/// <summary>See <see cref="IDbProvider.LifetimeFor"/>.</summary>
public enum ConnectionLifetime { Pooled, SessionOwned }

/// <summary>Engine features that a provider may not implement (Phase D0a); see <see cref="IDbProvider.FeatureRefusal"/>.</summary>
public enum Feature
{
    ChangeDatabase, Transactions, DataEdit, Import, SchemaCompare, SchemaCompareApply, DataCompare, DataCompareApply, References, Monitor,
}

public enum ColumnHint
{
    Default,
    /// <summary>Read with <see cref="IDbProvider.ReadAsString"/> into Utf8.</summary>
    ProviderString,
    /// <summary>Read raw bytes (UDTs such as geography) into Binary.</summary>
    ProviderBytes,
    /// <summary>Unbounded string type (nvarchar(max), text, xml): LargeUtf8, truncated at 1 MB.</summary>
    LargeText,
    /// <summary>
    /// Phase D0f: the reported type is not a guarantee (SQLite declared types and affinity): one stable Utf8 column with
    /// lossless text per value and each value's storage class in a sibling column, never a typed builder a later row
    /// could break.
    /// </summary>
    Dynamic,
}

/// <summary>
/// Phase D4: one result column as the provider describes it (<see cref="IDbProvider.DescribeColumns"/>). The encoder
/// dispatches on <see cref="Clr"/> alone (a DateTime is a timestamp, whatever the type name says) and shows
/// <see cref="TypeName"/>. <see cref="OpenText"/> / <see cref="OpenBinary"/> stream a large value in chunks (checked for
/// cancel between them) instead of the reader's whole-value default; <see cref="TimestampMicros"/> reads a timestamp as
/// microseconds since 1970-01-01 (dates outside DateTime's range, such as Oracle's BC dates).
/// </summary>
public sealed record ColumnShape(Type Clr, string TypeName, ColumnHint Hint = ColumnHint.Default, int? Precision = null, int? Scale = null)
{
    public Func<DbDataReader, int, TextReader>? OpenText { get; init; }
    public Func<DbDataReader, int, Stream>? OpenBinary { get; init; }
    public Func<DbDataReader, int, long>? TimestampMicros { get; init; }
}

/// <summary>A dynamically typed value's storage class, sent per cell in the <c>&lt;col&gt;__class</c> sibling (Phase D0f).</summary>
public enum StorageClass : byte { Null = 0, Integer = 1, Real = 2, Text = 3, Blob = 4 }

public sealed record ProviderMessage(
    Severity Severity,
    string Message,
    int? Line = null,
    string? Procedure = null,
    int? Number = null,
    int? State = null,
    int? Class = null,
    int? Column = null);
