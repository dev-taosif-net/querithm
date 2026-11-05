using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.MySql;

namespace Querithm.Providers.MySql;

/// <summary>
/// MySQL and MariaDB (Phase D2): one provider family, two products told apart on the connection (<see cref="Product"/>).
/// Pooled server connections; TLS VerifyFull by default (Required is an explicit, warned downgrade); local-infile off on
/// every connection, whatever a profile or pasted string says; user variables allowed for the editor. Ships hidden behind the
/// dev setting until its release gates pass.
/// </summary>
public sealed class MySqlProvider : IDbProvider
{
    public Engine Engine => Engine.MySql;
    public Dialect Dialect => Dialect.MySql;
    /// <summary>A database switch is COM_INIT_DB on the same connection (one roundtrip).</summary>
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new MySqlLanguageService();
    public bool StopOnBatchError => true;
    public IMetadataProvider Metadata { get; } = new MySqlMetadata();
    public IPlanProvider Plans { get; } = new MySqlPlans();
    public IObjectScripter Scripter { get; } = new MySqlScripter();
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger => null;
    public IDataEditDialect DataEdit { get; } = new MySqlDataEdit();
    public IImportDialect Import { get; } = new MySqlImport();
    public ISchemaCompareDialect? SchemaCompare { get; } = new MySqlSchemaCompare();
    public IDataCompareDialect? DataCompare { get; } = new MySqlDataCompare();
    public IReferenceFinder? References { get; } = new MySqlReferenceFinder();
    public IActivityProvider? Activity { get; } = new MySqlActivity();
    public ITransactionControl Transactions { get; } = new MySqlTransactions();

    public EngineTraits Traits { get; } = new()
    {
        Name = "MySQL / MariaDB", DefaultDatabase = null, DefaultSchema = null, DefaultPort = 3306, DefaultAuth = AuthKind.Password,
        AuthKinds = [AuthKind.Password], Locations = [LocationKind.Server],
        // lower_case_table_names is a server setting fixed at initialization: until it is read, names match exactly.
        NameMatch = StringComparer.Ordinal,
    };

    public string? FeatureRefusal(Feature feature) => feature switch
    {
        Feature.ChangeDatabase or Feature.Transactions => null,
        Feature.DataEdit or Feature.Import or Feature.Monitor => null,
        Feature.SchemaCompare or Feature.SchemaCompareApply => null,
        Feature.DataCompare or Feature.DataCompareApply => null,
        Feature.References => null,
        _ => "Not available for MySQL / MariaDB.",
    };

    public string? ValidateProfile(Profile p)
    {
        if ((p.Location ?? LocationKind.Server) != LocationKind.Server) return "A MySQL / MariaDB profile connects to a server.";
        return p.Auth is AuthKind.Password or AuthKind.Sql ? null : "MySQL / MariaDB signs in with a user name and password.";
    }

    public static bool IsMariaDb(DbConnection c) => c.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);

    public ProductInfo Product(DbConnection connection) => new() { Name = IsMariaDb(connection) ? "MariaDB" : "MySQL", Version = CleanVersion(connection.ServerVersion, IsMariaDb(connection)) };

    /// <summary>
    /// PT9-1: the handshake's version text is whatever the server (or a MITM before TLS) sends, and it is shown in the
    /// status bar and the Test result: only its leading version token is kept ("8.0.36", "10.11.6-MariaDB-1"). MariaDB
    /// sends "5.5.5-" first for old clients; that prefix is dropped.
    /// </summary>
    internal static string CleanVersion(string? raw, bool mariaDb)
    {
        var text = raw ?? "";
        if (mariaDb && text.StartsWith("5.5.5-", StringComparison.Ordinal)) text = text[6..];
        var n = 0;
        while (n < text.Length && n < 64 && (char.IsAsciiLetterOrDigit(text[n]) || text[n] is '.' or '-' or '+' or '_' or '~')) n++;
        return n == 0 ? "unknown" : text[..n];
    }
    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    public (string Host, int Port, string? Refusal) TunnelTarget(Profile p)
    {
        var host = (p.Host ?? "localhost").Trim();
        if (host.StartsWith('/')) return ("", 0, "A Unix socket is a file on the server's machine; it cannot go through an SSH tunnel. Use the TCP host and port.");
        return (host, p.Port ?? 3306, null);
    }

    /// <summary>
    /// The driver dials the forward on 127.0.0.1; VerifyFull still checks the certificate against the real server name
    /// (PT5-4) through <see cref="VerifyFullFor"/>, set on the connection in <see cref="OpenAsync"/>.
    /// </summary>
    public (Profile Profile, string? Note) RouteThrough(Profile p, string host, int port)
    {
        var routed = p with { Host = host, Port = port };
        if (MapSsl(p.TlsMode) != MySqlSslMode.VerifyFull) return (routed, null);
        var options = new Dictionary<string, string>(p.Options ?? []) { [TunnelTlsHostOption] = (p.Host ?? "localhost").Trim() };
        return (routed with { Options = options }, null);
    }

    /// <summary>
    /// Set by <see cref="RouteThrough"/> only: the real server name. Read only for a loopback host, so a stored option
    /// cannot retarget a direct connection.
    /// </summary>
    internal const string TunnelTlsHostOption = "tunnelTlsHost";

    internal static string? TunnelTlsHost(Profile p) =>
        MapSsl(p.TlsMode) == MySqlSslMode.VerifyFull && p.Options?.GetValueOrDefault(TunnelTlsHostOption) is { Length: > 0 } h
            && IPAddress.TryParse(p.Host, out var ip) && IPAddress.IsLoopback(ip) ? h : null;

    /// <summary>
    /// VerifyFull for a server dialled under another name: the chain must lead to a trusted root (the profile's CA file
    /// when one is set, else the system's) for a server-auth certificate, and the certificate must be issued for
    /// <paramref name="host"/>. MySqlConnector calls a connection's callback only in Required mode (it then trusts the
    /// callback alone), so <see cref="OpenAsync"/> pairs the two.
    /// </summary>
    internal static RemoteCertificateValidationCallback VerifyFullFor(string host, string? caPath)
    {
        X509Certificate2Collection? roots = null;
        if (!string.IsNullOrEmpty(caPath))
        {
            roots = [];
            try
            {
                roots.ImportFromPemFile(caPath);
                if (roots.Count == 0) roots.Add(X509CertificateLoader.LoadCertificateFromFile(caPath)); // DER
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new InvalidOperationException($"The CA file {Path.GetFileName(caPath)} could not be read as a certificate: {ex.Message}", ex);
            }
        }
        return (_, certificate, chain, errors) =>
        {
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
            using var leaf = new X509Certificate2(certificate);
            if (roots == null)
            {
                // SslStream built the chain against the system store; only the name (127.0.0.1) is expected to mismatch.
                if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
            }
            else
            {
                using var custom = new X509Chain();
                custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                custom.ChainPolicy.CustomTrustStore.AddRange(roots);
                custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                custom.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // server authentication
                if (chain != null)
                    foreach (var e in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(e.Certificate);
                if (!custom.Build(leaf)) return false;
            }
            return leaf.MatchesHostname(host);
        };
    }

    static MySqlSslMode MapSsl(TlsMode m) => m switch
    {
        TlsMode.Default or TlsMode.VerifyFull or TlsMode.Strict => MySqlSslMode.VerifyFull,
        TlsMode.VerifyCa => MySqlSslMode.VerifyCA,
        TlsMode.Require or TlsMode.Mandatory => MySqlSslMode.Required,
        TlsMode.Prefer or TlsMode.Optional => MySqlSslMode.Preferred,
        TlsMode.Disable => MySqlSslMode.None,
        _ => MySqlSslMode.VerifyFull,
    };

    /// <summary>
    /// The connection string Querithm builds itself: nothing from a profile's extras reaches it, so AllowLoadLocalInfile
    /// stays false (with a verified TLS connection MySqlConnector would open any file the server names).
    /// </summary>
    public string BuildConnectionString(Profile p, string? secret, string? database)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = string.IsNullOrWhiteSpace(p.Host) ? "localhost" : p.Host,
            Port = (uint)(p.Port ?? 3306),
            UserID = p.Username ?? "",
            Password = secret ?? "",
            Database = database ?? p.DatabaseDefault ?? "",
            SslMode = MapSsl(p.TlsMode),
            Pooling = false,
            AllowUserVariables = true,
            AllowLoadLocalInfile = false,
            AllowPublicKeyRetrieval = false,
            // PT1-3: a negative value would wrap to 4294967295 s; it means the default instead (0 stays "no limit").
            ConnectionTimeout = (uint)(p.TimeoutS is { } t && t >= 0 ? t : 15),
            DefaultCommandTimeout = (uint)Math.Max(0, p.CommandTimeoutS ?? 0),
            ApplicationName = "Querithm",
            AllowZeroDateTime = true,
        };
        if (!string.IsNullOrEmpty(p.CaPath)) b.SslCa = p.CaPath;
        if (p.Host?.StartsWith('/') == true) b.ConnectionProtocol = MySqlConnectionProtocol.UnixSocket;
        return b.ConnectionString;
    }

    // Keywords ParseConnectionString maps onto profile fields (MySqlConnector's canonical spellings); anything else the
    // driver knows is reported as not applied.
    static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase) { "Server", "Port", "User ID", "Password", "Database", "SSL Mode", "SSL CA" };

    // MySqlConnector's documented spelling where Npgsql's differs (SslMode vs SSL Mode), and its default port.
    static readonly HashSet<string> OwnSpellings = new(StringComparer.OrdinalIgnoreCase) { "SslMode", "SslCa" };
    static readonly HashSet<string> Conventional = new(StringComparer.OrdinalIgnoreCase) { "Server", "Uid", "User ID", "User", "Pwd", "Password", "Database", "Port", "SslMode" };

    public ConnectionStringAffinity ConnectionStringAffinity(IReadOnlyDictionary<string, string> keywords) => new(
        ConnectionStringKeywords.CountKeys(keywords, OwnSpellings) + (ConnectionStringKeywords.HasPort(keywords, 3306) ? 1 : 0),
        ConnectionStringKeywords.CountKeys(keywords, Conventional));

    // MySqlConnector's ContainsKey is true only for a keyword already set; reading an unknown one throws ("Option 'x' not
    // supported."), a known one gives its default.
    static bool Knows(string keyword)
    {
        try { _ = new MySqlConnectionStringBuilder()[keyword]; return true; }
        catch (ArgumentException) { return false; }
    }

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis)
    {
        // ADO.NET quoting rules (shared with the other drivers): a quoted or braced value holding ";" or "=" stays whole, so
        // no part of a password can turn up as an unknown keyword.
        var k = ConnectionStringKeywords.Apply(connectionString, () => new MySqlConnectionStringBuilder(), Knows);
        var b = k.Builder;
        var notApplied = k.Named.Where(n => !Mapped.Contains(n)).ToList();
        // M3: the builder reads an absent SslMode as Preferred, so only a string that names it (any spelling the driver
        // accepts) changes the mode; otherwise the editor's (VerifyFull by default) stays.
        var tls = basis.TlsMode;
        var notes = new List<string>();
        if (k.Has("SSL Mode"))
        {
            tls = b.SslMode switch { MySqlSslMode.Required => TlsMode.Require, MySqlSslMode.VerifyCA => TlsMode.VerifyCa, MySqlSslMode.None => TlsMode.Disable, MySqlSslMode.Preferred => TlsMode.Prefer, _ => TlsMode.Default };
            var setting = $"SslMode={b.SslMode}";
            if (b.SslMode == MySqlSslMode.None) notes.Add(WeakTls.NotEncrypted(setting));
            else if (b.SslMode == MySqlSslMode.Preferred) notes.Add(WeakTls.Downgradable(setting));
            else if (b.SslMode == MySqlSslMode.Required) notes.Add(WeakTls.CertNotChecked(setting));
            else if (b.SslMode == MySqlSslMode.VerifyCA) notes.Add(WeakTls.HostNotChecked(setting));
        }
        if (k.Has("Server") && FailoverNote(b.Server) is { } failover) notes.Add(failover);
        if (k.Has("SSL CA") && !string.IsNullOrEmpty(b.SslCa))
            notes.Add($"SslCa={b.SslCa}: the server's certificate is checked against the CA file {b.SslCa} instead of the system's trusted CAs; remove it unless you know that file is your server's CA.");
        int? PortOf() => b.Port == 3306 ? null : (int)b.Port;
        // A field the string does not name keeps the editor's value (as PostgreSQL and SQL Server do); a new server
        // without a port takes the default one.
        var (host, port) = k.Has("Server") ? (b.Server, k.Has("Port") ? PortOf() : null)
            : k.Has("Port") ? (basis.Host, PortOf())
            : (basis.Host, basis.Port);
        var p = basis with
        {
            Engine = Engine.MySql, Host = host, Port = port, Username = k.Has("User ID") && !string.IsNullOrEmpty(b.UserID) ? b.UserID : basis.Username,
            DatabaseDefault = k.Has("Database") && !string.IsNullOrEmpty(b.Database) ? b.Database : basis.DatabaseDefault, Auth = AuthKind.Password,
            TlsMode = tls,
            CaPath = k.Has("SSL CA") && !string.IsNullOrEmpty(b.SslCa) ? b.SslCa : basis.CaPath,
        };
        return new ParseConnectionStringResult
        {
            Profile = p, Secret = k.Has("Password") && !string.IsNullOrEmpty(b.Password) ? b.Password : null, Unknown = [.. k.Unknown], NotApplied = [.. notApplied], Notes = [.. notes],
        };
    }

    /// <summary>
    /// PT9-3: MySqlConnector tries each host of a comma-separated list, so decoys can hide the server actually used; the
    /// note names every host.
    /// </summary>
    internal static string? FailoverNote(string? hostList)
    {
        if (hostList == null || !hostList.Contains(',')) return null;
        var hosts = hostList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return $"This connection string names {hosts.Length} servers, tried until one answers: {string.Join(", ", hosts)}. Check that each one is a server you mean to use.";
    }

    /// <summary>
    /// The protocol names each column's database, table and column as stored (org_table / org_name), so origins are exact.
    /// One table read under two aliases (a self-join) gives no origins: a row key could name either row.
    /// </summary>
    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        var names = Traits.NameMatch;
        var origins = new ColumnOrigin?[columns.Count];
        var aliasOf = new Dictionary<long, string?>();
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] is not MySqlDbColumn c || string.IsNullOrEmpty(c.BaseTableName) || string.IsNullOrEmpty(c.BaseColumnName)) continue;
            var obj = snapshot.Objects.FirstOrDefault(o => o.Kind is ObjectKind.Table or ObjectKind.View && names.Equals(o.Schema, c.BaseSchemaName) && names.Equals(o.Name, c.BaseTableName));
            if (obj == null) continue;
            if (aliasOf.TryGetValue(obj.Id, out var alias) && !string.Equals(alias, c.TableName, StringComparison.Ordinal)) return null;
            aliasOf[obj.Id] = c.TableName;
            // Column names are case-insensitive in MySQL / MariaDB.
            var col = obj.Columns?.FirstOrDefault(x => string.Equals(x.Name, c.BaseColumnName, StringComparison.OrdinalIgnoreCase));
            origins[i] = new ColumnOrigin(obj.Id, col?.Name ?? c.BaseColumnName);
        }
        return origins;
    }

    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        var connectionString = BuildConnectionString(profile, secret, database);
        RemoteCertificateValidationCallback? verify = null;
        if (TunnelTlsHost(profile) is { } realHost)
        {
            // Through an SSH forward: VerifyFull against the real name (PT5-4). The callback does the whole check
            // (VerifyFullFor); MySqlConnector consults it only in Required mode.
            verify = VerifyFullFor(realHost, profile.CaPath);
            var b = new MySqlConnectionStringBuilder(connectionString) { SslMode = MySqlSslMode.Required };
            b.Remove("SslCa");
            connectionString = b.ConnectionString;
        }
        var c = new MySqlConnection(connectionString);
        if (verify != null) c.RemoteCertificateValidationCallback = verify;
        try
        {
            await c.OpenAsync(ct).ConfigureAwait(false);
            // The catalog markers hash GROUP_CONCAT output: the default 1 KB limit would truncate it silently.
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SET SESSION group_concat_max_len = 16777216";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return c;
        }
        catch
        {
            await c.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) => ((MySqlConnection)connection).ChangeDatabaseAsync(database, ct);
    public string? CurrentDatabase(DbConnection connection) => string.IsNullOrEmpty(connection.Database) ? null : connection.Database;
    public string ServerVersion(DbConnection connection) => $"{Product(connection).Name} {Product(connection).Version}";
    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(((MySqlConnection)connection).ServerThread);

    /// <summary>
    /// Warnings MySqlConnector already raised through InfoMessage during the current batch (it does for statements without
    /// a result set, not for SELECTs), per connection: SHOW WARNINGS after the batch leaves those out, so each reaches the
    /// tab once.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbConnection, List<(int Number, string Message)>> Raised = new();

    static List<(int Number, string Message)> RaisedOn(DbConnection c) => Raised.GetValue(c, _ => []);

    /// <summary>
    /// The warnings of the batch that just ran: SHOW WARNINGS after each successful statement (a diagnostic statement: it
    /// reads, and does not clear, the list), minus those the driver raised itself (<see cref="Raised"/>).
    /// </summary>
    public async Task<IReadOnlyList<ProviderMessage>> AfterBatchMessagesAsync(DbConnection connection, CancellationToken ct)
    {
        var raised = RaisedOn(connection);
        List<(int Number, string Message)> already;
        lock (raised)
        {
            already = [.. raised];
            raised.Clear();
        }
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SHOW WARNINGS LIMIT 50";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ProviderMessage>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var level = r.GetString(0);
            var number = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
            var message = r.GetString(2);
            var at = already.IndexOf((number, message));
            if (at >= 0) { already.RemoveAt(at); continue; } // sent once already, through InfoMessage
            list.Add(new ProviderMessage(level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? Severity.Error : level.Equals("Note", StringComparison.OrdinalIgnoreCase) ? Severity.Info : Severity.Warning,
                message, Number: number));
        }
        return list;
    }

    /// <summary>Any InfoMessage MySqlConnector does raise, forwarded as is and remembered for <see cref="AfterBatchMessagesAsync"/>.</summary>
    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage)
    {
        var c = (MySqlConnection)connection;
        var raised = RaisedOn(connection);
        lock (raised) raised.Clear();
        void Handler(object? sender, MySqlInfoMessageEventArgs e)
        {
            foreach (var err in e.Errors)
            {
                lock (raised) raised.Add(((int)err.ErrorCode, err.Message));
                onMessage(new ProviderMessage(err.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? Severity.Error : err.Level.Equals("Note", StringComparison.OrdinalIgnoreCase) ? Severity.Info : Severity.Warning,
                    err.Message, Number: (int)err.ErrorCode));
            }
        }
        c.InfoMessage += Handler;
        return new Unsubscribe(() => c.InfoMessage -= Handler);
    }

    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null) => ex is MySqlException m
        ? [new ProviderMessage(Severity.Error, m.Number is 1148 or 3948 or 2068
            ? "LOAD DATA LOCAL is turned off: it lets the server name any file on this computer to read. Use Import data… instead."
            : SignInReason(m.Message) ?? m.Message, Number: m.Number)]
        : [new ProviderMessage(Severity.Error, SignInReason(ex.Message) ?? ex.Message)];

    /// <summary>
    /// Sign-in failures the driver words in its own options (ServerRSAPublicKeyFile, AllowPublicKeyRetrieval), said in
    /// Querithm's terms. These are the driver's messages, not SQL.
    /// </summary>
    public static string? SignInReason(string driverMessage)
    {
        if (driverMessage.Contains("caching_sha2_password", StringComparison.Ordinal) && driverMessage.Contains("secure connection", StringComparison.Ordinal))
            return "The account signs in with caching_sha2_password, and the server asked for the full password: Querithm sends it only over TLS (or to this computer itself). " +
                   "Turn TLS on for this connection. Once a TLS sign-in succeeded, plain connections may work until the server's sign-in cache is cleared (a restart or FLUSH PRIVILEGES) — why this can look intermittent.";
        // "…not supported by any of the available plugins" or "You must install the MySqlConnector.Authentication.Ed25519 package…".
        foreach (var plugin in new[] { "client_ed25519", "parsec" })
            if (driverMessage.Contains(plugin, StringComparison.Ordinal))
                return $"The account signs in with MariaDB's {(plugin == "parsec" ? "parsec" : "ed25519")} method, which is not supported by Querithm yet. Use an account with a password method (mysql_native_password).";
        return null;
    }

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    public void AddParameter(DbCommand command, QueryParameter parameter)
    {
        var name = parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name;
        command.Parameters.Add(new MySqlParameter(name, parameter.IsNull ? DBNull.Value : (object?)parameter.Value ?? DBNull.Value));
    }

    /// <summary>
    /// MySQL reports stable column types. Unsigned BIGINT, zero dates (0000-00-00), BIT, spatial values and DECIMAL beyond
    /// 28 digits are read losslessly as text (the generic encoder's fallback, or <see cref="ReadAsString"/>).
    /// </summary>
    public ColumnHint GetColumnHint(DbColumn column)
    {
        var t = (column.DataTypeName ?? "").ToUpperInvariant();
        return t is "DATETIME" or "DATE" or "TIMESTAMP" or "BIT" or "GEOMETRY" or "POINT" or "LINESTRING" or "POLYGON" or "YEAR" ? ColumnHint.ProviderString : ColumnHint.Default;
    }

    public string? ReadAsString(DbDataReader reader, int ordinal)
    {
        var r = (MySqlDataReader)reader;
        var type = r.GetDataTypeName(ordinal).ToUpperInvariant();
        if (type.StartsWith("DECIMAL", StringComparison.Ordinal)) return r.GetMySqlDecimal(ordinal).ToString();
        if (type is "DATETIME" or "DATE" or "TIMESTAMP") return r.GetMySqlDateTime(ordinal) is var d && d.IsValidDateTime
            ? (type == "DATE" ? d.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture))
            : d.ToString(); // a zero date stays "0000-00-00 …", never a made-up value
        var v = r.GetValue(ordinal);
        return v switch
        {
            byte[] b => "0x" + Convert.ToHexString(b),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }

    public bool IsPlanResultSet(DbDataReader reader) => false;

    public Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct)
    {
        var product = Product(connection);
        return Task.FromResult(new Capabilities
        {
            Trace = CapabilityInfo.No("Querithm does not implement tracing for MySQL / MariaDB."),
            Debugger = CapabilityInfo.No("Querithm does not implement debugging for MySQL / MariaDB."),
            EstimatedPlan = CapabilityInfo.Yes,
            // SELECT and TABLE statements only (MySqlPlans.WriteRefusal says why for the rest).
            ActualPlan = IsMariaDb(connection) || MySqlPlans.SupportsExplainAnalyze(connection.ServerVersion)
                ? CapabilityInfo.Yes : CapabilityInfo.No("EXPLAIN ANALYZE needs MySQL 8.0.18 or later."),
            CreateOrAlter = false,
            EncryptStrict = false,
            MajorVersion = int.TryParse(connection.ServerVersion.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : null,
            EngineEdition = product.Name,
            CompletionTier = "tier1+tier2",
            Namespaces = NamespaceModel.Databases,
            Product = product,
        });
    }

    public async Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            list.Add(new DatabaseInfo { Name = name, IsSystem = name is "information_schema" or "mysql" or "performance_schema" or "sys", State = "ONLINE" });
        }
        return list.OrderBy(d => d.IsSystem).ThenBy(d => d.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        if (!await ((MySqlConnection)connection).PingAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("The server did not answer.");
    }

    /// <summary>MySqlConnector cancels with KILL QUERY on a side connection, then closes the connection after its CancellationTimeout.</summary>
    public void Cancel(DbCommand command) => command.Cancel();

    sealed class Unsubscribe(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
