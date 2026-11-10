using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Oracle (Phase D4) as a hidden engine skeleton (D4 S01): the driver ships and loads, the engine is listed behind the dev
/// setting, and every feature refuses with a reason. Nothing connects yet — connect, TLS and sign-in arrive in D4 S03 — so
/// a profile can be saved (server location, password sign-in) but never opens, and no SQL is read (the language service
/// refuses every statement until D4 S02).
/// </summary>
public sealed class OracleProvider : IDbProvider
{
    /// <summary>Why an Oracle profile does not open in this build.</summary>
    public const string NotConnectingYet = "Connecting to Oracle is not available in this build of Querithm yet.";

    public Engine Engine => Engine.Oracle;
    public Dialect Dialect => Dialect.Oracle;
    /// <summary>A connection reaches one service (Easy Connect); schemas live inside it, and switching schema is not switching database.</summary>
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new OracleLanguageService();
    public bool StopOnBatchError => true;
    public IMetadataProvider Metadata { get; } = OracleUnavailable.Instance;
    public IPlanProvider Plans { get; } = OracleUnavailable.Instance;
    public IObjectScripter Scripter { get; } = OracleUnavailable.Instance;
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger => null;
    public IDataEditDialect DataEdit { get; } = OracleUnavailable.Instance;
    public IImportDialect Import { get; } = OracleUnavailable.Instance;
    public ISchemaCompareDialect? SchemaCompare => null;
    public IDataCompareDialect? DataCompare => null;
    public IReferenceFinder? References => null;
    public IActivityProvider? Activity => null;
    public ITransactionControl Transactions { get; } = OracleUnavailable.Instance;

    public EngineTraits Traits { get; } = new()
    {
        // The service is the connection target (Easy Connect host:port/service); FREEPDB1 is only the fixture's.
        Name = "Oracle", DefaultDatabase = null, DefaultSchema = null, DefaultPort = 1521, DefaultAuth = AuthKind.Password,
        AuthKinds = [AuthKind.Password], Locations = [LocationKind.Server],
        // Unquoted names fold to upper case and the dictionary holds the folded names: matching is exact (D0e).
        NameMatch = StringComparer.Ordinal,
    };

    /// <summary>Every feature refuses until the session that implements it (D4 S03–S12) replaces its entry.</summary>
    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.ChangeDatabase => "An Oracle connection reaches one service; open another service in a connection of its own.",
        Feature.Transactions => "Transactions on Oracle are not available in this build of Querithm yet.",
        Feature.DataEdit => "Editing Oracle data in the grid is not available in this build of Querithm yet.",
        Feature.Import => "Importing into Oracle is not available in this build of Querithm yet.",
        Feature.SchemaCompare or Feature.SchemaCompareApply => "Schema compare is not available for Oracle yet.",
        Feature.DataCompare or Feature.DataCompareApply => "Data compare is not available for Oracle yet.",
        Feature.References => "Find usages and rename are not available for Oracle yet.",
        Feature.Monitor => "The activity monitor is not available for Oracle yet.",
        _ => "Not available for Oracle yet.",
    };

    public string? ValidateProfile(Profile p)
    {
        if ((p.Location ?? LocationKind.Server) != LocationKind.Server) return "An Oracle connection is a server: host, port and service name.";
        return p.Auth != AuthKind.Password ? "Oracle connections sign in with a user name and password in this build." : null;
    }

    public ProductInfo Product(DbConnection connection) => new() { Name = "Oracle", Version = connection.ServerVersion };
    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => throw new NotSupportedException(NotConnectingYet);
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => ("", 0, NotConnectingYet);

    public string BuildConnectionString(Profile profile, string? secret, string? database) => throw new NotSupportedException(NotConnectingYet);

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) =>
        throw new ArgumentException("Reading an Oracle connection string is not available in this build of Querithm yet.");

    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) => null;

    public Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct) =>
        Task.FromException<DbConnection>(new NotSupportedException(NotConnectingYet));

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(FeatureRefusal(Feature.ChangeDatabase)));

    public string? CurrentDatabase(DbConnection connection) => null;
    public string ServerVersion(DbConnection connection) => $"Oracle {connection.ServerVersion}";
    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(null);

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => Nop.Instance;

    /// <summary>The driver's message as is (ORA-nnnnn text); S03 adds the cases a user meets (sign-in, TLS, listener).</summary>
    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) =>
        [new ProviderMessage(Severity.Error, ex.Message, Number: ex is OracleException o ? o.Number : null)];

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    public void AddParameter(DbCommand command, QueryParameter parameter) => throw new NotSupportedException(OracleLanguageService.NotReadYet);

    public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.ProviderString;
    public string? ReadAsString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("Querithm does not implement tracing for Oracle."),
        Debugger = CapabilityInfo.No("Querithm does not implement a PL/SQL debugger."),
        EstimatedPlan = CapabilityInfo.No("Plans for Oracle are not available in this build of Querithm yet."),
        ActualPlan = CapabilityInfo.No("Plans for Oracle are not available in this build of Querithm yet."),
        CreateOrAlter = false,
        EncryptStrict = false,
        EngineEdition = "Oracle",
        CompletionTier = "none",
        Namespaces = NamespaceModel.Schemas,
        Product = Product(connection),
    });

    public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(Array.Empty<DatabaseInfo>());

    public Task ValidateAsync(DbConnection connection, CancellationToken ct) => Task.FromException(new NotSupportedException(NotConnectingYet));

    /// <summary>OracleCommand.Cancel sends the break out of band on the command's own connection.</summary>
    public void Cancel(DbCommand command) => command.Cancel();

    /// <summary>The driver assembly's version, for the trimmed Host self-test (proves the untrimmed assembly loads).</summary>
    public static string DriverVersion => typeof(OracleConnection).Assembly.GetName().Version?.ToString() ?? "";

    sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}
