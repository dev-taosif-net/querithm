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
/// with their position work. Since D4 S04 every Oracle type reaches the grid typed or as exact text (<see cref="OracleTypes"/>),
/// DBMS_OUTPUT and compile errors are read after each batch (<see cref="OracleOutput"/>) and <c>:name</c> binds by name.
/// Since D4 S05 the catalog is read (<see cref="OracleMetadata"/>). Since D4 S07 a profile goes through an SSH tunnel, Verify
/// full still checking the real server name, and a lost connection says so. Since D4 S10 results of one plain table are
/// edited in the grid (<see cref="OracleDataEdit"/>) and files are imported with array binding (<see cref="OracleImport"/>);
/// the features still refusing say so until their session (D4 S11–S12).
/// </summary>
public sealed class OracleProvider : IDbProvider
{
    /// <summary>The oldest release Querithm connects to (the oldest in Oracle's premier or extended support).</summary>
    public const int MinimumMajor = 19;

    readonly string _oracleDir;

    /// <param name="dataDir">&lt;appDataDir&gt;/querithm: the empty TNS_ADMIN directory and CA trust wallets live under it. Tests may omit it (a temp directory).</param>
    public OracleProvider(string? dataDir = null)
        : this(dataDir, PlatformRefusal(OperatingSystem.IsMacOS(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)) { }

    /// <param name="unavailable">Tests: the platform refusal this computer would give (<see cref="PlatformRefusal"/>).</param>
    internal OracleProvider(string? dataDir, string? unavailable)
    {
        _oracleDir = Path.Combine(dataDir ?? Path.Combine(Path.GetTempPath(), "querithm-tests"), "oracle");
        Traits = BaseTraits with { Unavailable = unavailable };
    }

    public Engine Engine => Engine.Oracle;
    public Dialect Dialect => Dialect.Oracle;
    /// <summary>A connection reaches one service (Easy Connect); schemas live inside it, and switching schema is not switching database.</summary>
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new OracleLanguageService();
    public bool StopOnBatchError => true;
    public IMetadataProvider Metadata => _metadata;
    readonly OracleMetadata _metadata = new();
    /// <summary>EXPLAIN PLAN / PLAN_TABLE and GATHER_PLAN_STATISTICS / V$SQL_PLAN_STATISTICS_ALL since D4 S09.</summary>
    public IPlanProvider Plans { get; } = new OraclePlans();
    /// <summary>Packages since D4 S08; every other kind refuses until D4 S11.</summary>
    public IObjectScripter Scripter => _scripter ??= new OracleScripter(_metadata);
    OracleScripter? _scripter;
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger => null;
    /// <summary>Grid edits since D4 S10.</summary>
    public IDataEditDialect DataEdit { get; } = new OracleDataEdit();
    /// <summary>Imports since D4 S10 (array-bound INSERT, never OracleBulkCopy).</summary>
    public IImportDialect Import { get; } = new OracleImport();
    public ISchemaCompareDialect? SchemaCompare => null;
    public IDataCompareDialect? DataCompare => null;
    public IReferenceFinder? References => null;
    /// <summary>Only the connection identity so far (§6 rule 7); the monitor itself is refused until D4 S12.</summary>
    public IActivityProvider? Activity { get; } = new OracleActivity();
    public ITransactionControl Transactions { get; } = new OracleTransactions();

    public EngineTraits Traits { get; }

    static readonly EngineTraits BaseTraits = new()
    {
        // The service is the connection target (Easy Connect host:port/service); FREEPDB1 is only the fixture's. TCPS is the
        // default, so the default port is the TCPS one (2484); a plain TCP profile defaults to 1521.
        Name = "Oracle", DefaultDatabase = null, DefaultSchema = null, DefaultPort = OracleConnectionStrings.TcpsPort, DefaultAuth = AuthKind.Password,
        AuthKinds = [AuthKind.Password], Locations = [LocationKind.Server],
        // Unquoted names fold to upper case and the dictionary holds the folded names: matching is exact (D0e).
        NameMatch = StringComparer.Ordinal,
    };

    /// <summary>
    /// O6: ODP.NET Core runs on Windows, Linux and Apple-silicon macOS only (Oracle's platform list for 23.26), so on an Intel
    /// Mac the engine is listed disabled with this reason and its profiles refuse with it; null everywhere else.
    /// </summary>
    public static string? PlatformRefusal(bool macOs, System.Runtime.InteropServices.Architecture architecture) =>
        macOs && architecture != System.Runtime.InteropServices.Architecture.Arm64
            ? "Oracle's driver (ODP.NET) does not run on Intel Macs. Oracle connections work in Querithm on Apple-silicon Macs, Windows and Linux."
            : null;

    /// <summary>Each feature still refusing names the reason; the sessions that implement them (D4 S04–S12) clear their entry.</summary>
    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.Transactions => null,
        Feature.ChangeDatabase => ChangeDatabaseRefusal,
        Feature.DataEdit or Feature.Import => null,
        Feature.SchemaCompare or Feature.SchemaCompareApply => "Schema compare is not available for Oracle yet.",
        Feature.DataCompare or Feature.DataCompareApply => "Data compare is not available for Oracle yet.",
        Feature.References => "Find usages and rename are not available for Oracle yet.",
        Feature.Monitor => OracleActivity.MonitorNotYet,
        _ => "Not available for Oracle yet.",
    };

    public const string ChangeDatabaseRefusal = "An Oracle connection reaches one service; open another service in a connection of its own.";

    public string? ValidateProfile(Profile p) =>
        Traits.Unavailable
        ?? (p.Options is { } o && (o.ContainsKey(OracleConnectionStrings.TunnelHostOption) || o.ContainsKey(OracleConnectionStrings.TunnelPortOption))
            ? "The tunnelHost and tunnelPort options are Querithm's own (set while an SSH tunnel is open); remove them from the profile."
            : null)
        ?? OracleConnectionStrings.Validate(p);

    static readonly ConditionalWeakTable<DbConnection, ProductInfo> Products = new();

    /// <summary>Read once at open (<see cref="ReadProductAsync"/>); the driver's handshake version until then.</summary>
    public ProductInfo Product(DbConnection connection) =>
        Products.TryGetValue(connection, out var p) ? p : new ProductInfo { Name = "Oracle", Version = CleanVersion(connection.ServerVersion) };

    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    /// <summary>
    /// The listener's host and port as the bastion reaches them (the profile's own: Querithm writes one address). The port
    /// follows the transport, as for a direct connection (2484 TCPS, 1521 TCP).
    /// </summary>
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile p)
    {
        if (OracleConnectionStrings.Validate(p) is { } why) return ("", 0, why);
        var host = (p.Host ?? "").Trim();
        if (host.Length == 0) host = "localhost";
        var (transport, _) = OracleConnectionStrings.TransportFor(p);
        return (host, p.Port ?? (transport == OracleConnectionStrings.Transport.Tcp ? OracleConnectionStrings.TcpPort : OracleConnectionStrings.TcpsPort), null);
    }

    /// <summary>
    /// The driver dials the forward; the real host and port ride along (<see cref="OracleConnectionStrings.TunnelOf"/>) so
    /// Verify full still checks the certificate against the server's own name (<see cref="OracleTunnelTls"/>, PT5-4) and a
    /// listener redirect is named for what it is.
    /// </summary>
    public (Profile Profile, string? Note) RouteThrough(Profile p, string host, int port)
    {
        var (realHost, realPort, _) = TunnelTarget(p);
        var options = new Dictionary<string, string>(p.Options ?? [])
        {
            [OracleConnectionStrings.TunnelHostOption] = realHost,
            [OracleConnectionStrings.TunnelPortOption] = realPort.ToString(CultureInfo.InvariantCulture),
        };
        return (p with { Host = host, Port = port, Options = options }, null);
    }

    /// <summary>Verify full through a tunnel opens two connections through the forward: Querithm's own TLS check, then the driver's.</summary>
    public int TunnelConnectsPerOpen(Profile routed) =>
        OracleConnectionStrings.TunnelOf(routed) != null && OracleConnectionStrings.TransportFor(routed).Transport == OracleConnectionStrings.Transport.Tcps ? 2 : 1;

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

    string BuildChecked(Profile profile, string? secret, string? certDn) =>
        OracleConnectionStrings.Build(profile, secret, EmptyTnsAdmin(), TrustWallet(profile), certDn);

    public ConnectionStringAffinity ConnectionStringAffinity(IReadOnlyDictionary<string, string> keywords) => OracleConnectionStrings.Affinity(keywords);

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis) => OracleConnectionStrings.Parse(connectionString, basis);

    /// <summary>Without the connection the current schema is unknown: nothing is predicted (Core calls the overload below).</summary>
    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) => null;

    /// <summary>
    /// D4 S10: predicted from the statement's tokens and the snapshot (<see cref="OracleResultSources"/>): ODP.NET's schema
    /// table names a base table without its schema, and the same table for both sides of a self-join. Only the first data
    /// result of a plain SQL batch; an unqualified name resolves in the connection's current schema as last read
    /// (<see cref="OracleSessionSchema"/>).
    /// </summary>
    public ColumnOrigin?[]? ResultOrigins(DbConnection connection, DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot) =>
        snapshot == null || resultInBatch != 0 ? null
            : OracleResultSources.Predict(batchText, snapshot, OracleSessionSchema.Of(connection), columns.Select(c => c.ColumnName).ToList());

    /// <summary>A set operator (MINUS too), the same table twice, a database link, a synonym for a remote object.</summary>
    public string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) => OracleResultSources.EditRefusal(batchText, snapshot);

    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        if (OracleConnectionStrings.Validate(profile) is { } why) throw new ArgumentException(why);
        var tunnel = OracleConnectionStrings.TunnelOf(profile);
        string? certDn = null;
        if (tunnel is { } real && OracleConnectionStrings.TransportFor(profile).Transport == OracleConnectionStrings.Transport.Tcps)
            certDn = await OracleTunnelTls.ServerSubjectAsync(profile.Host!, profile.Port!.Value, real.Host, profile.CaPath, profile.TimeoutS ?? 0, ct).ConfigureAwait(false);
        var c = new OracleConnection(BuildChecked(profile, secret, certDn));
        try
        {
            try { await c.OpenAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (tunnel is { } t && OracleErrors.RedirectRefusal(ex, profile.Port!.Value, t.Host, t.Port) is { } redirect)
            {
                throw new InvalidOperationException(redirect, ex);
            }
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
        // D4 S10: the session's current schema rides along (the grid resolves unqualified table names there).
        cmd.CommandText = "SELECT PRODUCT, VERSION_FULL, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM PRODUCT_COMPONENT_VERSION WHERE PRODUCT LIKE 'Oracle%' FETCH FIRST 1 ROWS ONLY";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                OracleSessionSchema.Set(c, r.IsDBNull(2) ? null : r.GetString(2));
                var name = Printable(r.IsDBNull(0) ? null : r.GetString(0));
                var version = CleanVersion(r.IsDBNull(1) ? c.ServerVersion : r.GetString(1));
                return new ProductInfo { Name = string.IsNullOrEmpty(name) ? "Oracle" : name, Version = version };
            }
        }
        catch (OracleException) { /* a locked-down dictionary: the handshake version stays */ }
        await OracleSessionSchema.RefreshAsync(c, ct).ConfigureAwait(false);
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

    /// <summary>The driver raises no info messages of its own for SQL: DBMS_OUTPUT and compile errors are read after each batch.</summary>
    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage) => Nop.Instance;

    /// <summary>
    /// DBMS_OUTPUT lines (while server output is on) and the compile errors of a PL/SQL unit the batch created (<see cref="OracleOutput"/>).
    /// Also reads the current schema again after a batch that may have changed it (<see cref="OracleSessionSchema"/>).
    /// </summary>
    public async Task<IReadOnlyList<ProviderMessage>> AfterBatchMessagesAsync(DbConnection connection, string batchText, CancellationToken ct)
    {
        var messages = await OracleOutput.AfterBatchAsync(connection, batchText, ct).ConfigureAwait(false);
        await OracleSessionSchema.RefreshAsync(connection, ct).ConfigureAwait(false);
        return messages;
    }

    /// <summary>The DBMS_OUTPUT lines a failed block printed before it raised (and the current schema, read again when the block may have changed it).</summary>
    public async Task<IReadOnlyList<ProviderMessage>> FailedBatchMessagesAsync(DbConnection connection, CancellationToken ct)
    {
        var messages = await OracleOutput.AfterFailureAsync(connection, ct).ConfigureAwait(false);
        await OracleSessionSchema.RefreshAsync(connection, ct).ConfigureAwait(false);
        return messages;
    }

    /// <summary>
    /// DBMS_OUTPUT.ENABLE(NULL) / DISABLE, only when the connection's state changes (one round trip). Core calls it before
    /// every run on the connection it borrowed, so the current schema is read here too when a batch left it unknown and
    /// nothing read it after (a cancelled PL/SQL block): the run's results then resolve their table names (D4 S10).
    /// </summary>
    public async Task SetServerOutputAsync(DbConnection connection, bool on, CancellationToken ct)
    {
        await OracleSessionSchema.RefreshAsync(connection, ct).ConfigureAwait(false);
        await OracleOutput.SetAsync(connection, on, ct).ConfigureAwait(false);
    }

    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) =>
        ex is OracleException o ? OracleErrors.Translate(o, commandText) : [new ProviderMessage(Severity.Error, ex.Message)];

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    /// <summary>
    /// A <c>:name</c> / <c>:1</c> bind (the name with or without its colon; a quoted name keeps its quotes), its value as
    /// VARCHAR2 text that the server converts where the statement needs another type (a CLOB past 32,767 characters).
    /// The type name is not used: Oracle converts from text by the column or expression it is compared with.
    /// </summary>
    public void AddParameter(DbCommand command, QueryParameter parameter)
    {
        var name = parameter.Name.Trim();
        if (name.StartsWith(':')) name = name[1..];
        if (name.Length == 0 || name.Length > OracleIdentifiers.MaxBytes + 2) throw new ArgumentException($"\"{parameter.Name}\" is not an Oracle bind variable name.");
        var value = parameter.IsNull || parameter.Value == null ? null : parameter.Value;
        var type = value is { Length: > 32767 } ? OracleDbType.Clob : OracleDbType.Varchar2;
        command.Parameters.Add(new OracleParameter(name, type, (object?)value ?? DBNull.Value, System.Data.ParameterDirection.Input));
    }

    /// <summary>Fetch buffer per round trip, chosen by measurement (ARCHITECTURE §14 metric 11b): 200,000 rows × 5 columns read in 176–192 ms at 1 MB against 422–674 ms at the driver's 128 KB default, 153–166 ms at 4 MB.</summary>
    public const long FetchSizeBytes = 1024 * 1024;
    /// <summary>LOB bytes sent with each row (measured, 5,000 × 200-character CLOBs read whole: 44–63 ms at 4 KB, 3.5–3.8 s at 0, 169–195 ms at 32 KB; in chunks to their length, as the encoder reads them: 80–90 ms at 4 KB).</summary>
    public const int InitialLobFetchSize = 4096;

    /// <summary>
    /// Binds by name (ODP.NET binds by position by default), the fetch sizes above, LONG / LONG RAW fetched inline (−1: the
    /// driver otherwise needs the row's ROWID in the select list to read them), and only the parameters the statement
    /// names — a <c>CREATE TRIGGER</c>'s <c>:NEW</c> is no bind, so the run's parameters never reach DDL.
    /// </summary>
    public void PrepareCommand(DbCommand command)
    {
        if (command is not OracleCommand c) return;
        if (c.Connection != null) OracleSessionSchema.BeforeBatch(c.Connection, c.CommandText ?? "");
        c.BindByName = true;
        c.FetchSize = FetchSizeBytes;
        c.InitialLOBFetchSize = InitialLobFetchSize;
        c.InitialLONGFetchSize = -1;
        if (c.Parameters.Count == 0) return;
        var used = new HashSet<string>(OracleLanguageService.Binds(c.CommandText ?? ""), StringComparer.Ordinal);
        for (var i = c.Parameters.Count - 1; i >= 0; i--)
            if (!used.Contains(OracleIdentifiers.Identity(c.Parameters[i].ParameterName))) c.Parameters.RemoveAt(i);
    }

    /// <summary>Oracle types from the reader's schema table (<see cref="OracleTypes.Map"/>): DbColumn carries no Oracle type, precision or scale.</summary>
    public IReadOnlyList<ColumnShape?>? DescribeColumns(DbDataReader reader) => OracleTypes.Describe(reader);

    /// <summary>Only reached for a column <see cref="DescribeColumns"/> did not describe.</summary>
    public ColumnHint GetColumnHint(DbColumn column) => ColumnHint.ProviderString;

    public string? ReadAsString(DbDataReader reader, int ordinal) => OracleTypes.ReadAsString(reader, ordinal);

    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(new Capabilities
    {
        Trace = CapabilityInfo.No("Querithm does not implement tracing for Oracle."),
        Debugger = CapabilityInfo.No("Querithm does not implement a PL/SQL debugger."),
        // Grants are checked when a plan is asked for (PLAN_TABLE, V$SESSION + V$SQL_PLAN_STATISTICS_ALL): the refusal names them.
        EstimatedPlan = CapabilityInfo.Yes,
        ActualPlan = CapabilityInfo.Yes,
        CreateOrAlter = false,
        EncryptStrict = false,
        MajorVersion = MajorOf(connection.ServerVersion),
        EngineEdition = Product(connection).Name,
        CompletionTier = "tier1+tier2",
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
