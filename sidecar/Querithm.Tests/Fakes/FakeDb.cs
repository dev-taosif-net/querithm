using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Tests.Fakes;

/// <summary>What a fake command returns: rows (one table per result set), a scalar, or an affected-row count.</summary>
public sealed record FakeResult(DataTable[]? Tables = null, object? Scalar = null, int Affected = 0)
{
    public static readonly FakeResult None = new();
    /// <summary>Thrown by the reader when the batch moves past its last table (a server error after the sets streamed).</summary>
    public Exception? FailAfterTables { get; init; }
    public static FakeResult Value(object? v) => new(Scalar: v);

    public static FakeResult Rows(params (string Name, Type Type)[] columns) => new([Table(columns)]);

    public static DataTable Table((string Name, Type Type)[] columns, params object?[][] rows)
    {
        var t = new DataTable();
        foreach (var (n, ty) in columns) t.Columns.Add(n, ty);
        foreach (var r in rows) t.Rows.Add(r.Select(v => v ?? DBNull.Value).ToArray());
        return t;
    }
}

/// <summary>Invariant violations seen by the fake connections a test created (overlapping commands, I/O under a pool lock, double dispose).</summary>
public sealed class InvariantSink
{
    public ConcurrentQueue<string> Violations { get; } = new();
    /// <summary>True when the current thread holds a pool's bookkeeping lock.</summary>
    public Func<bool>? LockHeld { get; set; }

    internal void CheckNoLock(string what)
    {
        if (LockHeld?.Invoke() == true) Violations.Enqueue($"database I/O ({what}) while holding the pool lock");
    }
}

public static class FakeDbInvariants
{
    static readonly AsyncLocal<InvariantSink?> Current = new();

    /// <summary>Starts collecting for the current test (flows into every task it starts).</summary>
    public static InvariantSink Capture() => Current.Value = new InvariantSink();

    internal static InvariantSink? Sink => Current.Value;
    internal static void CheckNoLock(string what) => Current.Value?.CheckNoLock(what);
}

/// <summary>A connection that runs every command through <see cref="Handler"/> and enforces one command at a time.</summary>
public sealed class FakeDbConnection : DbConnection
{
    static int _nextSpid = 100;
    int _active;
    int _disposed;
    ConnectionState _state = ConnectionState.Open;
    readonly InvariantSink? _sink = FakeDbInvariants.Sink;

    public FakeDbConnection(Func<FakeDbCommand, CancellationToken, Task<FakeResult>> handler, int? spid = null)
    {
        Handler = handler;
        Spid = spid ?? Interlocked.Increment(ref _nextSpid);
    }

    public Func<FakeDbCommand, CancellationToken, Task<FakeResult>> Handler { get; set; }
    public int Spid { get; private set; }

    /// <summary>What connection resiliency does: the same object, a new server session.</summary>
    public void Reconnect() => Spid = Interlocked.Increment(ref _nextSpid);
    public int TxDepth { get; set; }
    public int DisposeCount => _disposed;
    public ConcurrentQueue<string> Log { get; } = new();
    /// <summary>Set by tests: the next DisposeAsync waits for this.</summary>
    public TaskCompletionSource? DisposeGate { get; set; }

    public void Break() => _state = ConnectionState.Broken;

    [AllowNull] public override string ConnectionString { get; set; } = "";
    public override string Database => DatabaseName;
    public string DatabaseName { get; set; } = "db";
    public override string DataSource => "fake";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) => DatabaseName = databaseName;
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);

    internal async Task<FakeResult> RunAsync(FakeDbCommand cmd, CancellationToken ct)
    {
        _sink?.CheckNoLock(cmd.CommandText);
        if (_state != ConnectionState.Open) throw new InvalidOperationException("The connection is broken.");
        if (Interlocked.Increment(ref _active) > 1)
            _sink?.Violations.Enqueue($"overlapping commands on spid {Spid}: {cmd.CommandText}");
        try
        {
            Log.Enqueue(cmd.CommandText);
            return await Handler(cmd, ct).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public override async ValueTask DisposeAsync()
    {
        _sink?.CheckNoLock("dispose");
        if (DisposeGate is { } g) await g.Task.ConfigureAwait(false);
        if (Interlocked.Increment(ref _disposed) > 1) _sink?.Violations.Enqueue($"spid {Spid} disposed twice");
        _state = ConnectionState.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Increment(ref _disposed) > 1) _sink?.Violations.Enqueue($"spid {Spid} disposed twice");
        _state = ConnectionState.Closed;
        base.Dispose(disposing);
    }
}

public sealed class FakeDbCommand(FakeDbConnection connection) : DbCommand
{
    readonly FakeParameterCollection _parameters = new();

    public FakeDbConnection Fake => connection;
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get => connection; set { } }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public bool CancelCalled { get; private set; }

    public IReadOnlyList<DbParameter> Params => _parameters.Items;
    public object? Param(string name) => _parameters.Items.FirstOrDefault(p => p.ParameterName.TrimStart('@', '$', ':') == name.TrimStart('@', '$', ':'))?.Value;

    public override void Cancel() => CancelCalled = true;
    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken ct) => (await connection.RunAsync(this, ct).ConfigureAwait(false)).Affected;

    public override async Task<object?> ExecuteScalarAsync(CancellationToken ct)
    {
        var r = await connection.RunAsync(this, ct).ConfigureAwait(false);
        if (r.Scalar != null) return r.Scalar;
        if (r.Tables is [{ Rows.Count: > 0 } t, ..]) return t.Rows[0][0];
        return null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct)
    {
        var r = await connection.RunAsync(this, ct).ConfigureAwait(false);
        var reader = new DataTableReader(r.Tables is { Length: > 0 } ts ? ts : [new DataTable()]);
        return r.FailAfterTables is { } fail ? new FailingReader(reader, fail) : reader;
    }
}

public sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    [AllowNull] public override string ParameterName { get; set; } = "";
    [AllowNull] public override string SourceColumn { get; set; } = "";
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() { }
}

public sealed class FakeParameterCollection : DbParameterCollection
{
    public List<DbParameter> Items { get; } = [];
    public override int Count => Items.Count;
    public override object SyncRoot => Items;
    public override int Add(object value) { Items.Add((DbParameter)value); return Items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => Items.Clear();
    public override bool Contains(object value) => Items.Contains((DbParameter)value);
    public override bool Contains(string value) => Items.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => Items.GetEnumerator();
    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => Items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => Items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => Items.RemoveAll(p => p.ParameterName == parameterName);
    protected override DbParameter GetParameter(int index) => Items[index];
    protected override DbParameter GetParameter(string parameterName) => Items.First(p => p.ParameterName == parameterName);
    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
}

/// <summary>
/// A provider whose connections are <see cref="FakeDbConnection"/>s sharing <see cref="Handler"/>. T-SQL language
/// services are real (classification drives execution); transaction state follows BEGIN / COMMIT / ROLLBACK seen.
/// Guard tests give it another engine's identity, language service and plan provider (<see cref="As"/>).
/// </summary>
public sealed class FakeDbProvider : IDbProvider
{
    /// <summary>The engine this fake stands for: its classifier and plans are that engine's real ones, over fake connections.</summary>
    public FakeDbProvider As(Engine engine, IDialectService language, IPlanProvider? plans = null, ITransactionControl? transactions = null)
    {
        Engine = engine;
        Dialect = language switch
        {
            Querithm.Intelligence.PgSql.PgLanguageService => Dialect.Pg,
            Querithm.Intelligence.MySql.MySqlLanguageService => Dialect.MySql,
            _ => Dialect.Tsql,
        };
        Language = language;
        PlansImpl = plans;
        if (transactions != null) Transactions = transactions;
        return this;
    }

    public Func<FakeDbCommand, CancellationToken, Task<FakeResult>> Handler { get; set; } = (_, _) => Task.FromResult(FakeResult.None);
    /// <summary>Called for each open; may block (gate) or throw (connect failure).</summary>
    public Func<CancellationToken, Task>? OnOpen { get; set; }
    public Func<FakeDbConnection, CancellationToken, Task>? OnValidate { get; set; }
    public ConcurrentQueue<FakeDbConnection> Opened { get; } = new();
    /// <summary>The file grant each open was given (Phase D0c).</summary>
    public ConcurrentQueue<FileGrant?> OpenedFiles { get; } = new();
    /// <summary>The profile each open was made with (e.g. the monitor's own options).</summary>
    public ConcurrentQueue<Profile> OpenedProfiles { get; } = new();
    public IActivityProvider? Activity { get; set; }

    public Engine Engine { get; private set; } = Engine.Mssql;
    public Dialect Dialect { get; private set; } = Dialect.Tsql;
    /// <summary>Features this fake refuses, with the reason (Phase D0a capability tests).</summary>
    public Dictionary<Feature, string> Refused { get; } = [];
    public string? FeatureRefusal(Feature feature) => Refused.GetValueOrDefault(feature);
    public Func<Profile, string?> Validate { get; set; } = _ => null;
    public string? ValidateProfile(Profile profile) => Validate(profile);
    public ProductInfo Product(DbConnection connection) => new() { Name = "Fake", Version = "1" };
    /// <summary>Phase D0d tests: SessionOwned makes every session keep one connection of its own.</summary>
    public ConnectionLifetime Lifetime { get; set; } = ConnectionLifetime.Pooled;
    public ConnectionLifetime LifetimeFor(Profile profile) => Lifetime;
    public EngineTraits Traits { get; set; } = new()
    {
        Name = "Fake", DefaultSchema = "dbo", DefaultAuth = AuthKind.Sql, AuthKinds = Enum.GetValues<AuthKind>(), Locations = Enum.GetValues<LocationKind>(),
        NameMatch = StringComparer.OrdinalIgnoreCase,
    };
    /// <summary>Catalog reads for metadata tests; null = not supported, as before.</summary>
    public IMetadataProvider? MetadataImpl { get; set; }
    /// <summary>PostgreSQL-like: a database is a separate connection (Phase D0e identity tests).</summary>
    public bool DatabaseScopedConnections { get; set; }
    /// <summary>How the fake server names a database it switched to (SQL Server's case-insensitive collations return their own spelling).</summary>
    public Func<string, string> CanonicalDatabase { get; set; } = d => d;
    public IDialectService Language { get; private set; } = new Querithm.Intelligence.TSql.TSqlLanguageService();
    public bool StopOnBatchError => false;
    public bool CloseAbortsCommand { get; set; } = true;
    public IMetadataProvider Metadata => MetadataImpl ?? throw new NotSupportedException();
    public IPlanProvider? PlansImpl { get; set; }
    public IPlanProvider Plans => PlansImpl ?? throw new NotSupportedException();
    public IObjectScripter Scripter => throw new NotSupportedException();
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger { get; set; }
    /// <summary>Data compare tests: a real engine's dialects over the fake connections; null = not supported, as before.</summary>
    public IDataEditDialect? DataEditImpl { get; set; }
    public IDataCompareDialect? DataCompareImpl { get; set; }
    public IDataEditDialect DataEdit => DataEditImpl ?? throw new NotSupportedException();
    /// <summary>Import tests: an engine's import rules over the fake connections; null = not supported, as before.</summary>
    public IImportDialect? ImportImpl { get; set; }
    public IImportDialect Import => ImportImpl ?? throw new NotSupportedException();
    public ISchemaCompareDialect? SchemaCompare => null;
    public IDataCompareDialect? DataCompare => DataCompareImpl;
    public IReferenceFinder? References => null;
    public string BuildConnectionString(Profile profile, string? secret, string? database) => "";
    public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => (profile, null);
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => (profile.Host ?? "", profile.Port ?? 0, null);
    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) => throw new NotSupportedException();
    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) => null;
    /// <summary>Grid-edit audit tests: the statement-shape refusal this fake engine reports (null = none).</summary>
    public Func<string, int, string?>? EditRefusal { get; set; }
    public string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) => EditRefusal?.Invoke(batchText, resultInBatch);

    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        FakeDbInvariants.CheckNoLock("open");
        OpenedFiles.Enqueue(file);
        if (OnOpen != null) await OnOpen(ct).ConfigureAwait(false);
        OpenedProfiles.Enqueue(profile);
        var c = new FakeDbConnection((cmd, t) => Track(cmd, t));
        if (database != null) c.DatabaseName = CanonicalDatabase(database); // the server's spelling after login
        Opened.Enqueue(c);
        return c;
    }

    async Task<FakeResult> Track(FakeDbCommand cmd, CancellationToken ct)
    {
        var text = cmd.CommandText.Trim();
        var r = await Handler(cmd, ct).ConfigureAwait(false);
        // Transaction depth from the statements seen (after the handler: a cancelled statement changes nothing).
        foreach (var part in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("BEGIN TRAN", StringComparison.OrdinalIgnoreCase)) cmd.Fake.TxDepth++;
            else if (part.StartsWith("ROLLBACK TRANSACTION ", StringComparison.OrdinalIgnoreCase)) { /* back to a savepoint: the transaction stays open */ }
            else if (part.Contains("COMMIT", StringComparison.OrdinalIgnoreCase) || part.Contains("ROLLBACK", StringComparison.OrdinalIgnoreCase)) cmd.Fake.TxDepth = 0;
        }
        return r;
    }

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct)
    {
        FakeDbInvariants.CheckNoLock("changeDatabase");
        ((FakeDbConnection)connection).Log.Enqueue($"USE {database}");
        connection.ChangeDatabase(CanonicalDatabase(database));
        return Task.CompletedTask;
    }

    /// <summary>M10 tests: runs after each batch completed, with the run's token (MySQL reads its warnings there).</summary>
    public Func<CancellationToken, Task>? AfterBatch { get; set; }

    public async Task<IReadOnlyList<ProviderMessage>> AfterBatchMessagesAsync(DbConnection connection, CancellationToken ct)
    {
        if (AfterBatch != null) await AfterBatch(ct).ConfigureAwait(false);
        return [];
    }

    public string? CurrentDatabase(DbConnection connection) => connection.Database;
    public string ServerVersion(DbConnection connection) => "Fake 1";
    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(((FakeDbConnection)connection).Spid);

    public ITransactionControl Transactions { get; set; } = new FakeTransactions();
    /// <summary>M13 tests: the run's message callback while it is subscribed; a handler calls it as a server's PRINT would.</summary>
    public Action<ProviderMessage>? Emit { get; private set; }

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage)
    {
        Emit = onMessage;
        return new Unsubscribe(() => Emit = null);
    }

    sealed class Unsubscribe(Action done) : IDisposable
    {
        public void Dispose() => done();
    }
    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) => [new ProviderMessage(Severity.Error, ex.Message)];
    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);
    public void AddParameter(DbCommand command, QueryParameter parameter) => throw new NotSupportedException();
    /// <summary>Phase D0f tests: which columns the fake engine does not trust (Dynamic).</summary>
    public Func<DbColumn, ColumnHint> Hint { get; set; } = _ => ColumnHint.Default;
    public ColumnHint GetColumnHint(DbColumn column) => Hint(column);
    public string? ReadAsString(DbDataReader reader, int ordinal) => null;
    public bool IsPlanResultSet(DbDataReader reader) => false;
    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("fake"), Debugger = CapabilityInfo.No("fake"), EstimatedPlan = CapabilityInfo.Yes, ActualPlan = CapabilityInfo.Yes, CompletionTier = "tier1",
    });
    public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(Array.Empty<DatabaseInfo>());

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        var c = (FakeDbConnection)connection;
        if (OnValidate != null) await OnValidate(c, ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public void Cancel(DbCommand command) => command.Cancel();

    sealed class Nop : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// T-SQL-worded transaction control over fake connections: statements go through the handler (so tests can fail or hold
/// them), and the depth the fake provider tracks from BEGIN / COMMIT / ROLLBACK is the state.
/// </summary>
public sealed class FakeTransactions : SqlTransactionControl
{
    public override TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = true, ReleaseSavepoint = false, ReadOnlyTransactions = false,
        FailedStatementAbortsTransaction = false, UserStatements = ["BEGIN TRAN"],
    };
    protected override string BeginSql => "BEGIN TRANSACTION";
    protected override string? BeginReadOnlySql => null;
    protected override string CommitSql => "COMMIT TRANSACTION";
    protected override string RollbackSql => "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION";
    protected override string SavepointSql(string name) => $"SAVE TRANSACTION {name}";
    protected override string RollbackToSavepointSql(string name) => $"ROLLBACK TRANSACTION {name}";
    protected override string? ReleaseSavepointSql(string name) => null;

    public override async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        var c = (FakeDbConnection)connection;
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT @@TRANCOUNT";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return c.TxDepth > 0 ? TxState.Open : TxState.None;
    }
}

/// <summary>
/// PostgreSQL-worded transaction control over fake connections (PostgreSQL's traits: savepoints with RELEASE, a failed
/// statement aborts the block). Statements go through the handler, so tests see and can fail them; the depth is kept
/// here, so ROLLBACK TO SAVEPOINT leaves the transaction open.
/// </summary>
public sealed class FakePgTransactions : ITransactionControl
{
    public TransactionTraits Traits { get; } = Querithm.Providers.Postgres.PostgresTransactions.Instance.Traits;

    static async Task Run(DbConnection c, string sql, CancellationToken ct)
    {
        var f = (FakeDbConnection)c;
        var depth = f.TxDepth;
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        f.TxDepth = depth; // the fake's T-SQL depth tracking does not apply
    }

    static void Depth(DbConnection c, int depth) => ((FakeDbConnection)c).TxDepth = depth;

    public async Task BeginAsync(DbConnection c, CancellationToken ct) { await Run(c, "BEGIN", ct); Depth(c, 1); }
    public async Task<bool> BeginReadOnlyAsync(DbConnection c, CancellationToken ct) { await Run(c, "BEGIN TRANSACTION READ ONLY", ct); Depth(c, 1); return true; }

    public async Task CommitAsync(DbConnection c, CancellationToken ct)
    {
        if (((FakeDbConnection)c).TxDepth == 0) throw new InvalidOperationException("no transaction");
        await Run(c, "COMMIT", ct);
        Depth(c, 0);
    }

    public async Task RollbackAsync(DbConnection c, CancellationToken ct) { await Run(c, "ROLLBACK", ct); Depth(c, 0); }

    public async Task SavepointAsync(DbConnection c, string name, CancellationToken ct)
    {
        if (((FakeDbConnection)c).TxDepth == 0) throw new InvalidOperationException("SAVEPOINT can only be used in transaction blocks");
        await Run(c, $"SAVEPOINT {name}", ct);
    }

    public Task RollbackToSavepointAsync(DbConnection c, string name, CancellationToken ct) => Run(c, $"ROLLBACK TO SAVEPOINT {name}", ct);
    public Task ReleaseSavepointAsync(DbConnection c, string name, CancellationToken ct) => Run(c, $"RELEASE SAVEPOINT {name}", ct);

    public async Task<TxState> StateAsync(DbConnection c, CancellationToken ct)
    {
        await Run(c, "SELECT pg_current_xact_id_if_assigned()", ct);
        return ((FakeDbConnection)c).TxDepth > 0 ? TxState.Open : TxState.None;
    }
}

/// <summary>A reader that streams its tables, then fails where the next result set would start (a later statement's error).</summary>
public sealed class FailingReader(DataTableReader inner, Exception fail) : DbDataReader
{
    public override bool NextResult() => inner.NextResult() ? true : throw fail;
    public override Task<bool> NextResultAsync(CancellationToken ct) => Task.FromResult(NextResult());
    public override bool Read() => inner.Read();
    public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(inner.Read());
    public override object this[int ordinal] => inner[ordinal];
    public override object this[string name] => inner[name];
    public override int Depth => inner.Depth;
    public override int FieldCount => inner.FieldCount;
    public override bool HasRows => inner.HasRows;
    public override bool IsClosed => inner.IsClosed;
    public override int RecordsAffected => inner.RecordsAffected;
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => inner.GetName(ordinal);
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    public override int GetValues(object[] values) => inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
}
