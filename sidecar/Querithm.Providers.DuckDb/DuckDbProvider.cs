using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using DuckDB.NET.Native;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// DuckDB (Phase D3): a granted database file (read-only unless the user allowed writes in the shell's dialog) or an
/// in-memory database, one connection per tab (session-owned, D0d), opened under the file and configuration policy of
/// <see cref="DuckDbInstances"/> before any user SQL. Ships hidden behind the dev setting until its release gates pass.
/// </summary>
public sealed class DuckDbProvider(string? dataDir = null, long? memoryLimitBytes = null) : IDbProvider
{
    public Engine Engine => Engine.DuckDb;
    public Dialect Dialect => Dialect.DuckDb;
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new DuckDbLanguageService();
    /// <summary>One statement per batch; the first error stops the script (later statements may depend on it).</summary>
    public bool StopOnBatchError => true;
    public IMetadataProvider Metadata { get; } = new DuckDbMetadata();
    public IPlanProvider Plans { get; } = new DuckDbPlans();
    public IObjectScripter Scripter { get; } = new DuckDbScripter();
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger => null;
    public IDataEditDialect DataEdit { get; } = new DuckDbDataEdit();
    public IImportDialect Import { get; } = new DuckDbImport();
    public ISchemaCompareDialect? SchemaCompare => null;
    public IDataCompareDialect? DataCompare => null;
    public IReferenceFinder? References => null;
    public IActivityProvider? Activity => null;
    public ITransactionControl Transactions { get; } = new DuckDbTransactions();

    public EngineTraits Traits { get; } = new()
    {
        Name = "DuckDB", DefaultDatabase = null, DefaultSchema = "main", DefaultPort = null, DefaultAuth = AuthKind.None,
        AuthKinds = [AuthKind.None], Locations = [LocationKind.File, LocationKind.Memory],
        // DuckDB keeps the case of names but compares them case-insensitively, quoted or not.
        NameMatch = StringComparer.OrdinalIgnoreCase,
    };

    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.ChangeDatabase => "A DuckDB connection is one database; open another file in a connection of its own.",
        Feature.Transactions => null,
        Feature.DataEdit => null,
        Feature.Import => null,
        Feature.SchemaCompare or Feature.SchemaCompareApply => "Schema compare is not available for DuckDB yet.",
        Feature.DataCompare or Feature.DataCompareApply => "Data compare is not available for DuckDB yet.",
        Feature.References => "Find usages and rename are not available for DuckDB yet.",
        Feature.Monitor => "A DuckDB database runs inside Querithm: there is no server activity to monitor.",
        _ => "Not available for DuckDB.",
    };

    public string? ValidateProfile(Profile p)
    {
        if (p.Location is not (LocationKind.File or LocationKind.Memory)) return "A DuckDB connection is a database file or an in-memory database.";
        return p.Auth != AuthKind.None ? "A DuckDB database takes no sign-in." : null;
    }

    public ProductInfo Product(DbConnection connection) => new() { Name = "DuckDB", Version = connection.ServerVersion };
    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.SessionOwned;

    public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => (profile, "A DuckDB database is a file on this computer; there is no tunnel.");
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => ("", 0, "A DuckDB database is a file on this computer; it cannot go through an SSH tunnel.");

    public string BuildConnectionString(Profile profile, string? secret, string? database) =>
        throw new NotSupportedException("A DuckDB connection is opened from its file grant, never from a connection string.");

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) =>
        throw new ArgumentException("A DuckDB database is picked with the file dialog, not read from a connection string.");

    /// <summary>The driver reports no base table or column: origins are predicted from the statement's tokens.</summary>
    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) =>
        snapshot == null || resultInBatch != 0 ? null
            : DuckDbResultSources.Predict(batchText, snapshot, Traits.DefaultSchema!, columns.Select(c => c.ColumnName).ToList());

    /// <summary>Exactly the granted file in exactly its mode, or a private in-memory database, under the policy (DuckDbInstances).</summary>
    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        if (profile.Location != LocationKind.Memory && file == null) throw new InvalidOperationException("A DuckDB file opens only through its grant.");
        var c = await DuckDbInstances.OpenAsync(dataDir, profile.Location == LocationKind.Memory ? null : file, ct, memoryLimitBytes).ConfigureAwait(false);
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT current_database()";
            DuckDbMetadata.Remember(c, Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "memory");
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) =>
        string.Equals(database, DuckDbMetadata.DatabaseOf(connection), StringComparison.OrdinalIgnoreCase) ? Task.CompletedTask : throw new NotSupportedException(FeatureRefusal(Feature.ChangeDatabase));

    public string? CurrentDatabase(DbConnection connection) => DuckDbMetadata.DatabaseOf(connection);
    public string ServerVersion(DbConnection connection) => $"DuckDB {connection.ServerVersion}";
    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(null);

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => Nop.Instance;

    /// <summary>The engine's error, with what to do about the cases a user meets (refusals, locks, read-only, cancel, driver limits).</summary>
    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null)
    {
        var text = ex switch
        {
            DuckDBException { ErrorType: DuckDBErrorType.Interrupt } => "The statement was cancelled.",
            DuckDBException { ErrorType: DuckDBErrorType.Permission } e =>
                $"Not allowed here: Querithm opens only the picked database, with file access, extensions and settings locked. ({First(e.Message)})",
            DuckDBException e when e.Message.Contains("Could not set lock on file", StringComparison.OrdinalIgnoreCase) =>
                $"The database file is open in another program, and DuckDB lets only one program write to a file at a time. Close it there, or open the file read-only. ({First(e.Message)})",
            DuckDBException e when e.Message.Contains("read-only mode", StringComparison.OrdinalIgnoreCase) =>
                $"The database is open read-only. Allow writes to this file for this connection to change it. ({First(e.Message)})",
            DuckDBException e when e.Message.Contains("not a valid DuckDB database", StringComparison.OrdinalIgnoreCase)
                                   || e.Message.Contains("Corrupt", StringComparison.OrdinalIgnoreCase) =>
                $"The file is not a valid DuckDB database, or it is damaged. ({First(e.Message)})",
            DuckDBException e when e.Message.Contains("configuration has been locked", StringComparison.OrdinalIgnoreCase) =>
                $"DuckDB settings are fixed by Querithm for this connection: only schema and search_path can change. ({First(e.Message)})",
            DuckDBException e => First(e.Message),
            ArgumentException e when e.Message.Contains("Unrecognised type", StringComparison.OrdinalIgnoreCase) =>
                $"The DuckDB driver cannot read a column of this result ({e.Message}). Cast it to VARCHAR in the query to see it as text.",
            InvalidOperationException e when e.Message.Contains("infinite", StringComparison.OrdinalIgnoreCase) =>
                "The DuckDB driver cannot read an infinite date or timestamp. Cast that column to VARCHAR in the query to see it as text.",
            _ => ex.Message,
        };
        return [new ProviderMessage(Severity.Error, text)];
    }

    /// <summary>DuckDB errors carry a JSON tail and context lines after the first line; the first line is the message.</summary>
    static string First(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length == 0 ? message : line;
    }

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    /// <summary>
    /// $name binds by name; ?, ?1 (the run dialog's name for the first ?) and $1 bind by position, in the order given.
    /// Values travel as VARCHAR and DuckDB casts them to the type the statement expects (never string substitution).
    /// </summary>
    public void AddParameter(DbCommand command, QueryParameter parameter)
    {
        var value = parameter.IsNull ? DBNull.Value : (object?)parameter.Value ?? DBNull.Value;
        var name = parameter.Name.TrimStart('$', ':', '@');
        var positional = name.Length == 0 || name[0] == '?' || char.IsAsciiDigit(name[0]);
        command.Parameters.Add(positional ? new DuckDBParameter(value) : new DuckDBParameter(name, value));
    }

    public ColumnHint GetColumnHint(DbColumn column) => DuckDbValues.Hint(column);
    public string? ReadAsString(DbDataReader reader, int ordinal) => DuckDbValues.Text(reader, ordinal);
    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("Querithm does not implement tracing for DuckDB."),
        Debugger = CapabilityInfo.No("DuckDB has no stored procedures to debug."),
        EstimatedPlan = CapabilityInfo.Yes,
        ActualPlan = CapabilityInfo.Yes,
        CreateOrAlter = false,
        EncryptStrict = false,
        MajorVersion = int.TryParse(connection.ServerVersion.TrimStart('v').Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : null,
        EngineEdition = "DuckDB",
        CompletionTier = "tier1+tier2",
        Namespaces = NamespaceModel.Schemas,
        Product = Product(connection),
    });

    public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult(new[] { new DatabaseInfo { Name = DuckDbMetadata.DatabaseOf(connection), IsSystem = false, State = "ONLINE" } });

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>DuckDBCommand.Cancel interrupts the connection's running query (duckdb_interrupt), out of band.</summary>
    public void Cancel(DbCommand command) => command.Cancel();

    sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}
