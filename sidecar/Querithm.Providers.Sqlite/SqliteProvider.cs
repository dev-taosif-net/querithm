using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Providers.Sqlite;

/// <summary>
/// SQLite (Phase D1): a granted database file (read-only unless the user allowed writes in the shell's dialog) or an
/// in-memory database, one connection per tab (session-owned, D0d), hardened before any user SQL. Ships hidden behind the
/// dev setting until its release gates pass.
/// </summary>
public sealed class SqliteProvider : IDbProvider
{
    /// <summary>How long a statement waits for another connection's lock before SQLITE_BUSY (the busy budget, seconds).</summary>
    public const int BusyTimeoutSeconds = 5;

    public Engine Engine => Engine.Sqlite;
    public Dialect Dialect => Dialect.Sqlite;
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new SqliteLanguageService();
    /// <summary>One statement per batch; the first error stops the script (later statements may depend on it).</summary>
    public bool StopOnBatchError => true;
    public IMetadataProvider Metadata { get; } = new SqliteMetadata();
    public IPlanProvider Plans { get; } = new SqlitePlans();
    public IObjectScripter Scripter { get; } = new SqliteScripter();
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger => null;
    public IDataEditDialect DataEdit { get; } = new SqliteDataEdit();
    public IImportDialect Import { get; } = new SqliteImport();
    public ISchemaCompareDialect? SchemaCompare => null;
    public IDataCompareDialect? DataCompare => null;
    public IReferenceFinder? References => null;
    public IActivityProvider? Activity => null;
    public ITransactionControl Transactions { get; } = new SqliteTransactions();

    public EngineTraits Traits { get; } = new()
    {
        Name = "SQLite", DefaultDatabase = "main", DefaultSchema = "main", DefaultPort = null, DefaultAuth = AuthKind.None,
        AuthKinds = [AuthKind.None], Locations = [LocationKind.File, LocationKind.Memory],
        // SQLite compares identifiers case-insensitively (ASCII).
        NameMatch = StringComparer.OrdinalIgnoreCase,
        // The driver's command timeout is its busy-retry budget only (a running statement is never timed out by it).
        FixedCommandTimeoutS = BusyTimeoutSeconds,
    };

    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.ChangeDatabase => "A SQLite connection is one database file; open another file in a connection of its own.",
        Feature.Transactions => null,
        Feature.DataEdit => null,
        Feature.Import => null,
        Feature.SchemaCompare or Feature.SchemaCompareApply => "Schema compare is not available for SQLite yet.",
        Feature.DataCompare or Feature.DataCompareApply => "Data compare is not available for SQLite yet.",
        Feature.References => "Find usages and rename are not available for SQLite yet.",
        Feature.Monitor => "A SQLite database is a file on this computer: there is no server activity to monitor.",
        _ => "Not available for SQLite.",
    };

    public string? ValidateProfile(Profile p)
    {
        if (p.Location is not (LocationKind.File or LocationKind.Memory)) return "A SQLite connection is a database file or an in-memory database.";
        return p.Auth != AuthKind.None ? "A SQLite database takes no sign-in." : null;
    }

    public ProductInfo Product(DbConnection connection) => new() { Name = "SQLite", Version = connection.ServerVersion };
    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.SessionOwned;

    public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => (profile, "A SQLite database is a file on this computer; there is no tunnel.");
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => ("", 0, "A SQLite database is a file on this computer; it cannot go through an SSH tunnel.");

    public string BuildConnectionString(Profile profile, string? secret, string? database) =>
        throw new NotSupportedException("A SQLite connection is opened from its file grant, never from a connection string.");

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) =>
        throw new ArgumentException("A SQLite database is picked with the file dialog, not read from a connection string.");

    /// <summary>
    /// The base table and column SQLite reports for each result column (sqlite3_column_table_name / _origin_name), matched to
    /// the snapshot's table of that name. The schema (main / temp) is not reported: a name in both gives no origin, so such a
    /// result stays read-only rather than editing the wrong table.
    /// </summary>
    /// <summary>A self-join (one base table for every alias) or a compound SELECT (the leftmost table for every column).</summary>
    public string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) => SqliteResultShape.EditRefusal(batchText);

    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        var origins = new ColumnOrigin?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var table = columns[i].BaseTableName;
            var column = columns[i].BaseColumnName;
            if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(column)) continue;
            var matches = snapshot.Objects.Where(o => o.Kind == ObjectKind.Table && string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count != 1) continue;
            var name = matches[0].Columns?.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Name;
            if (name != null) origins[i] = new ColumnOrigin(matches[0].Id, name);
        }
        return origins;
    }

    /// <summary>
    /// Exactly the granted file in exactly its mode — never ReadWriteCreate, so a missing file is never created — or a
    /// private in-memory database. Pooling off (Core owns the one connection), private cache, a busy budget, then hardening.
    /// </summary>
    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        var b = new SqliteConnectionStringBuilder { Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = BusyTimeoutSeconds, ForeignKeys = true };
        if (profile.Location == LocationKind.Memory)
        {
            b.DataSource = ":memory:";
            b.Mode = SqliteOpenMode.Memory;
        }
        else
        {
            if (file == null) throw new InvalidOperationException("A SQLite file opens only through its grant.");
            b.DataSource = file.Path;
            b.Mode = file.Mode == FileAccessMode.ReadWrite ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly;
        }
        var c = new SqliteConnection(b.ToString());
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            SqliteHardening.Apply(c);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) =>
        string.Equals(database, "main", StringComparison.OrdinalIgnoreCase) ? Task.CompletedTask : throw new NotSupportedException(FeatureRefusal(Feature.ChangeDatabase));

    public string? CurrentDatabase(DbConnection connection) => "main";
    public string ServerVersion(DbConnection connection) => $"SQLite {connection.ServerVersion}";
    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(null);

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => Nop.Instance;

    /// <summary>The engine's error, with what to do about the cases a user meets (locks, read-only, refusals, cancel).</summary>
    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null)
    {
        if (ex is not SqliteException e) return [new ProviderMessage(Severity.Error, ex.Message)];
        var text = (e.SqliteErrorCode & 0xFF) switch
        {
            5 or 6 => $"The database is locked by another connection (waited {BusyTimeoutSeconds} s). Try again when it has finished writing. ({e.Message})",
            8 => $"The database is open read-only. Allow writes to this file for this connection to change it. ({e.Message})",
            9 => "The statement was cancelled.",
            SqliteHardening.SqliteAuth => $"Not allowed here: Querithm opens only the picked database file and keeps its protections on. ({e.Message})",
            11 or 26 => $"The file is not a valid SQLite database, or it is damaged. ({e.Message})",
            _ => e.Message,
        };
        return [new ProviderMessage(Severity.Error, text, Number: e.SqliteErrorCode)];
    }

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    public void AddParameter(DbCommand command, QueryParameter parameter)
    {
        // A bare ? has no name to bind to: number them the way SQLite does (?1, ?2, …), as the run dialog names them.
        if (command.Parameters.Count == 0) command.CommandText = SqliteTokenizer.NumberAnonymousParameters(command.CommandText);
        var name = parameter.Name.Length > 0 && parameter.Name[0] is ':' or '@' or '$' or '?' ? parameter.Name : ":" + parameter.Name;
        command.Parameters.Add(new SqliteParameter(name, parameter.IsNull ? DBNull.Value : (object?)parameter.Value ?? DBNull.Value));
    }

    /// <summary>Phase D0f: no SQLite column's reported type is a guarantee (affinity, mixed storage classes): all are dynamic.</summary>
    public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.Dynamic;

    public string? ReadAsString(DbDataReader reader, int ordinal) => Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("Querithm does not implement tracing for SQLite."),
        Debugger = CapabilityInfo.No("Querithm does not implement debugging for SQLite."),
        EstimatedPlan = CapabilityInfo.Yes,
        ActualPlan = CapabilityInfo.No("SQLite has no actual plans: EXPLAIN QUERY PLAN shows the planner's choice only."),
        CreateOrAlter = false,
        EncryptStrict = false,
        MajorVersion = int.TryParse(connection.ServerVersion.Split('.')[0], out var major) ? major : null,
        EngineEdition = "SQLite",
        CompletionTier = "tier1+tier2",
        Namespaces = NamespaceModel.Attached,
        Product = Product(connection),
    });

    public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult(new[] { new DatabaseInfo { Name = "main", IsSystem = false, State = "ONLINE" } });

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>sqlite3_interrupt on the connection (SqliteCommand.Cancel does nothing): out of band, connection-wide.</summary>
    public void Cancel(DbCommand command)
    {
        if (command.Connection is SqliteConnection c) SqliteHardening.Interrupt(c);
    }

    sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}
