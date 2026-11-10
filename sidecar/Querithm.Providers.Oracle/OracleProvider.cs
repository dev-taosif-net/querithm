using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.Oracle;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Oracle (Phase D4), a hidden engine until D4 S13. Since D4 S03 a profile connects: Easy Connect to one service over TCPS
/// with the certificate and server name checked by default (plain TCP is an explicit, warned downgrade, §13 rule 2), a
/// password from the keyring, no sqlnet.ora / tnsnames.ora (O1), Oracle 19c or later. Transactions (<see
/// cref="OracleTransactions"/>), cancel (OracleCommand.Cancel: a break on the command's own connection) and ORA- errors
/// with their position work; metadata, plans, scripting, grid edit and import still refuse with a reason until their
/// session (D4 S04–S12) replaces them.
/// </summary>
public sealed class OracleProvider : IDbProvider
{
    /// <summary>The oldest release Querithm connects to (the oldest in Oracle's premier or extended support).</summary>
    public const int MinimumMajor = 19;

    readonly string _oracleDir;

    /// <param name="dataDir">&lt;appDataDir&gt;/querithm: the empty TNS_ADMIN directory and CA trust wallets live under it. Tests may omit it (a temp directory).</param>
    public OracleProvider(string? dataDir = null)
    {
        _oracleDir = Path.Combine(dataDir ?? Path.Combine(Path.GetTempPath(), "querithm-tests"), "oracle");
    }

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
    /// <summary>Only the connection identity so far (§6 rule 7); the monitor itself is refused until D4 S12.</summary>
    public IActivityProvider? Activity { get; } = new OracleActivity();
    public ITransactionControl Transactions { get; } = new OracleTransactions();

    public EngineTraits Traits { get; } = new()
    {
        // The service is the connection target (Easy Connect host:port/service); FREEPDB1 is only the fixture's. TCPS is the
        // default, so the default port is the TCPS one (2484); a plain TCP profile defaults to 1521.
        Name = "Oracle", DefaultDatabase = null, DefaultSchema = null, DefaultPort = OracleConnectionStrings.TcpsPort, DefaultAuth = AuthKind.Password,
        AuthKinds = [AuthKind.Password], Locations = [LocationKind.Server],
        // Unquoted names fold to upper case and the dictionary holds the folded names: matching is exact (D0e).
        NameMatch = StringComparer.Ordinal,
    };

    /// <summary>Each feature still refusing names the reason; the sessions that implement them (D4 S04–S12) clear their entry.</summary>
    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.Transactions => null,
        Feature.ChangeDatabase => ChangeDatabaseRefusal,
        Feature.DataEdit => "Editing Oracle data in the grid is not available in this build of Querithm yet.",
        Feature.Import => "Importing into Oracle is not available in this build of Querithm yet.",
        Feature.SchemaCompare or Feature.SchemaCompareApply => "Schema compare is not available for Oracle yet.",
        Feature.DataCompare or Feature.DataCompareApply => "Data compare is not available for Oracle yet.",
        Feature.References => "Find usages and rename are not available for Oracle yet.",
        Feature.Monitor => OracleActivity.MonitorNotYet,
        _ => "Not available for Oracle yet.",
    };

    public const string ChangeDatabaseRefusal = "An Oracle connection reaches one service; open another service in a connection of its own.";

    public string? ValidateProfile(Profile p) => OracleConnectionStrings.Validate(p);

    static readonly ConditionalWeakTable<DbConnection, ProductInfo> Products = new();

    /// <summary>Read once at open (<see cref="ReadProductAsync"/>); the driver's handshake version until then.</summary>
    public ProductInfo Product(DbConnection connection) =>
        Products.TryGetValue(connection, out var p) ? p : new ProductInfo { Name = "Oracle", Version = CleanVersion(connection.ServerVersion) };

    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    /// <summary>SSH for Oracle arrives in D4 S07 (with TCPS checked against the real server name through the forward).</summary>
    public const string NoTunnelYet = "SSH tunnels for Oracle are not available in this build of Querithm yet.";
    public (Profile Profile, string? Note) RouteThrough(Profile profile, string host, int port) => throw new NotSupportedException(NoTunnelYet);
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile profile) => ("", 0, NoTunnelYet);

    /// <summary>
    /// The directory named as TNS_ADMIN: Querithm's own and empty, so the driver finds no sqlnet.ora / tnsnames.ora (and
    /// does not fall back to the TNS_ADMIN environment variable or the application folder). A file someone put there is
    /// refused rather than read.
    /// </summary>
    internal string EmptyTnsAdmin()
    {
        var dir = Path.Combine(_oracleDir, "tns-admin");
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException($"Querithm's Oracle network folder ({dir}) must be empty: Querithm does not read sqlnet.ora or tnsnames.ora. Remove what is in it.");
        return dir;
    }

    /// <summary>
    /// The profile's CA file as the driver's trust store: a PEM wallet (ewallet.pem holding only the CA certificates, no key)
    /// in a directory named by the certificates' hash under &lt;appDataDir&gt;/querithm/oracle/trust. Null without a CA file:
    /// the driver then checks the chain against the system's trusted roots.
    /// </summary>
    internal string? TrustWallet(Profile p)
    {
        if (string.IsNullOrEmpty(p.CaPath)) return null;
        X509Certificate2Collection roots = [];
        try
        {
            roots.ImportFromPemFile(p.CaPath);
            if (roots.Count == 0) roots.Add(X509CertificateLoader.LoadCertificateFromFile(p.CaPath)); // DER
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException($"The CA file {Path.GetFileName(p.CaPath)} could not be read as a certificate: {ex.Message}", ex);
        }
        var pem = new StringBuilder();
        foreach (var cert in roots) pem.Append(cert.ExportCertificatePem()).Append('\n');
        var text = pem.ToString();
        var dir = Path.Combine(_oracleDir, "trust", Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(text)))[..32].ToLowerInvariant());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "ewallet.pem");
        if (!File.Exists(file) || File.ReadAllText(file) != text) File.WriteAllText(file, text);
        return dir;
    }

    /// <summary>Pooling off (Core pools); <paramref name="database"/> is ignored: the service is the profile's (<see cref="ChangeDatabaseAsync"/> refuses).</summary>
    public string BuildConnectionString(Profile profile, string? secret, string? database) =>
        OracleConnectionStrings.Build(profile, secret, EmptyTnsAdmin(), TrustWallet(profile));

    public ConnectionStringAffinity ConnectionStringAffinity(IReadOnlyDictionary<string, string> keywords) => OracleConnectionStrings.Affinity(keywords);

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) => OracleConnectionStrings.Parse(connectionString, basis);

    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) => null;

    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        var c = new OracleConnection(BuildConnectionString(profile, secret, database));
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            var product = await ReadProductAsync(c, ct).ConfigureAwait(false);
            if (MajorOf(c.ServerVersion) is { } major && major < MinimumMajor)
                throw new NotSupportedException($"This server runs Oracle {product.Version}. Querithm connects to Oracle {MinimumMajor}c or later.");
            Products.AddOrUpdate(c, product);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static int? MajorOf(string? version) =>
        version != null && int.TryParse(version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null;

    /// <summary>
    /// PRODUCT_COMPONENT_VERSION (readable by every account): the product line ("Oracle AI Database 26ai Free", "Oracle
    /// Database 19c Enterprise Edition") and the full version. Kept to printable characters and 96 of them.
    /// </summary>
    static async Task<ProductInfo> ReadProductAsync(OracleConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT PRODUCT, VERSION_FULL FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' FETCH FIRST 1 ROWS ONLY";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = Printable(r.IsDBNull(0) ? null : r.GetString(0));
                var version = CleanVersion(r.IsDBNull(1) ? c.ServerVersion : r.GetString(1));
                return new ProductInfo { Name = string.IsNullOrEmpty(name) ? "Oracle" : name, Version = version };
            }
        }
        catch (OracleException) { /* a locked-down dictionary: the handshake version stays */ }
        return new ProductInfo { Name = "Oracle", Version = CleanVersion(c.ServerVersion) };
    }

    static string Printable(string? text)
    {
        var s = new string((text ?? "").Where(ch => ch is >= ' ' and <= '~').Take(96).ToArray()).Trim();
        return s;
    }

    /// <summary>The leading version token ("23.26.0.0.0"), digits and dots only, at most 32 characters.</summary>
    internal static string CleanVersion(string? raw)
    {
        var text = (raw ?? "").Trim();
        var n = 0;
        while (n < text.Length && n < 32 && (char.IsAsciiDigit(text[n]) || text[n] == '.')) n++;
        return n == 0 ? "unknown" : text[..n];
    }

    /// <summary>
    /// Nothing to do for the service the connection already reached (service names are case-insensitive: the listener
    /// reports "freepdb1" for a profile's "FREEPDB1"); any other service is refused with the reason.
    /// </summary>
    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) =>
        string.Equals(CurrentDatabase(connection), database, StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : Task.FromException(new NotSupportedException(ChangeDatabaseRefusal));

    /// <summary>The service the connection reached (the profile's target), upper case as Oracle shows service names.</summary>
    public string? CurrentDatabase(DbConnection connection)
    {
        if (connection is not OracleConnection { State: System.Data.ConnectionState.Open } c) return null;
        var name = c.ServiceName;
        if (string.IsNullOrEmpty(name)) name = c.PDBName;
        if (string.IsNullOrEmpty(name)) name = c.DatabaseName;
        return string.IsNullOrEmpty(name) ? null : name.ToUpperInvariant();
    }

    public string ServerVersion(DbConnection connection) => $"{Product(connection).Name} {Product(connection).Version}";

    public async Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = OracleActivity.SidSql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>DBMS_OUTPUT is read after each batch in D4 S04; the driver raises no info messages of its own for SQL.</summary>
    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => Nop.Instance;

    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) =>
        ex is OracleException o ? OracleErrors.Translate(o, commandText) : [new ProviderMessage(Severity.Error, ex.Message)];

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    public void AddParameter(DbCommand command, QueryParameter parameter) => throw new NotSupportedException(OracleLanguageService.NotReadYet);

    /// <summary>Every column as the driver's own lossless text until D4 S04 maps Oracle types to Arrow (NUMBER(38) exceeds System.Decimal).</summary>
    public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.ProviderString;

    public string? ReadAsString(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        if (reader is OracleDataReader r)
        {
            var v = r.GetOracleValue(ordinal);
            if (v is global::Oracle.ManagedDataAccess.Types.OracleBinary b) return "0x" + Convert.ToHexString(b.Value);
            if (v is global::Oracle.ManagedDataAccess.Types.OracleDecimal d) return d.ToString();
            if (v is global::Oracle.ManagedDataAccess.Types.OracleDate dt) return dt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (v is global::Oracle.ManagedDataAccess.Types.OracleTimeStamp ts) return ts.Value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        }
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("Querithm does not implement tracing for Oracle."),
        Debugger = CapabilityInfo.No("Querithm does not implement a PL/SQL debugger."),
        EstimatedPlan = CapabilityInfo.No("Plans for Oracle are not available in this build of Querithm yet."),
        ActualPlan = CapabilityInfo.No("Plans for Oracle are not available in this build of Querithm yet."),
        CreateOrAlter = false,
        EncryptStrict = false,
        MajorVersion = MajorOf(connection.ServerVersion),
        EngineEdition = Product(connection).Name,
        CompletionTier = "none",
        Namespaces = NamespaceModel.Schemas,
        Product = Product(connection),
    });

    /// <summary>One connection reaches one service (or PDB): that service is the only "database".</summary>
    public Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult(CurrentDatabase(connection) is { } name ? new[] { new DatabaseInfo { Name = name, State = "ONLINE" } } : []);

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM DUAL";
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) == null) throw new InvalidOperationException("The server did not answer.");
    }

    /// <summary>
    /// OracleCommand.Cancel sends a break on the command's own connection; the server answers ORA-01013 and the connection
    /// stays usable. <see cref="IDbProvider.CloseAbortsCommand"/> stays false: measured on the fixture, Close() under a
    /// running <c>DBMS_SESSION.SLEEP(30)</c> returned only when the block ended (28.5 s, 3 of 3), so a run whose break is
    /// never acknowledged is reported to the user instead (§7 step 4, ARCHITECTURE §16).
    /// </summary>
    public void Cancel(DbCommand command) => command.Cancel();

    /// <summary>The driver assembly's version, for the trimmed Host self-test (proves the untrimmed assembly loads).</summary>
    public static string DriverVersion => typeof(OracleConnection).Assembly.GetName().Version?.ToString() ?? "";

    sealed class Nop : IDisposable
    {
        public static readonly Nop Instance = new();
        public void Dispose() { }
    }
}
