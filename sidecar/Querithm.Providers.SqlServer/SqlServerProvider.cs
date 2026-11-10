using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.TSql;

namespace Querithm.Providers.SqlServer;

/// <summary>Microsoft SQL Server via Microsoft.Data.SqlClient (§2). Deepest support in v1.</summary>
public sealed partial class SqlServerProvider : IDbProvider
{
    /// <summary>Raised for Entra device-code prompts so the UI can show the code (the driver would print to stdout).</summary>
    public static event Action<AuthPromptEvent>? AuthPrompt;

    // Saved profile id → when Strict failed and Mandatory then worked on its server (see OpenAsync).
    static readonly ConcurrentDictionary<string, DateTime> StrictUnsupported = new();

    static SqlServerProvider()
    {
        try
        {
            SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
                new ActiveDirectoryAuthenticationProvider(result =>
                {
                    AuthPrompt?.Invoke(new AuthPromptEvent { Message = result.Message, VerificationUrl = result.VerificationUrl, UserCode = result.UserCode });
                    return Task.CompletedTask;
                }));
        }
        catch { /* provider already set */ }
    }

    public Engine Engine => Engine.Mssql;
    public Dialect Dialect => Dialect.Tsql;
    public bool StopOnBatchError => false;
    /// <summary>SqlClient: Close() from another thread returns at once and fails the parked call (measured 24 ms / 36 ms); it has no cancellation timeout of its own.</summary>
    public bool CloseAbortsCommand => true;
    public bool DatabaseScopedConnections => false;
    public IDialectService Language { get; } = new TSqlLanguageService();
    public IMetadataProvider Metadata { get; } = new SqlServerMetadata();
    public IPlanProvider Plans { get; } = new SqlServerPlans();
    public IObjectScripter Scripter { get; } = new SqlServerScripter();
    public ITraceProvider? Trace { get; } = new SqlServerTrace();
    public IDebugProvider? Debugger => null; // §1: SQL Server has no step debugger; Trace instead.
    public IDataEditDialect DataEdit { get; } = new SqlServerDataEdit();
    public IImportDialect Import { get; } = new SqlServerImport();
    public ISchemaCompareDialect? SchemaCompare { get; } = new SqlServerSchemaCompare();
    public IDataCompareDialect? DataCompare { get; } = new SqlServerDataCompare();
    public IReferenceFinder? References { get; } = new TSqlReferenceFinder();
    public IActivityProvider? Activity { get; }

    public SqlServerProvider() => Activity = new SqlServerActivity(Plans, DataEdit);

    /// <summary>Every Phase D0a feature is implemented for SQL Server; server-dependent limits come from <see cref="GetCapabilitiesAsync"/>.</summary>
    public string? FeatureRefusal(Feature feature) => null;

    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    public EngineTraits Traits { get; } = new()
    {
        Name = "SQL Server", DefaultDatabase = null, DefaultSchema = "dbo", DefaultPort = 1433, DefaultAuth = AuthKind.Sql,
        AuthKinds = [AuthKind.Sql, AuthKind.Windows, AuthKind.Kerberos, AuthKind.EntraInteractive, AuthKind.EntraDeviceCode, AuthKind.EntraToken],
        Locations = [LocationKind.Server], NameMatch = StringComparer.OrdinalIgnoreCase,
    };

    public string? ValidateProfile(Profile p)
    {
        if ((p.Location ?? LocationKind.Server) != LocationKind.Server) return "A SQL Server profile connects to a server; it has no file or memory location.";
        // Sql and Password are the same SQL login (profiles from connection strings may carry either).
        return p.Auth is AuthKind.None ? "SQL Server needs a sign-in: a SQL login, Windows, Kerberos or Microsoft Entra ID." : null;
    }

    public ProductInfo Product(DbConnection connection) => new() { Name = "SQL Server", Version = connection.ServerVersion };

    public string BuildConnectionString(Profile p, string? secret, string? database) => Build(p, secret, database, strict: null).ConnectionString;

    /// <summary>"host", "tcp:host", "host,port" → the TCP endpoint; a named instance needs SQL Browser (UDP), which SSH cannot carry.</summary>
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile p)
    {
        var host = (p.Host ?? "localhost").Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var port = p.Port is > 0 ? p.Port.Value : 1433;
        var comma = host.LastIndexOf(',');
        if (comma > 0 && int.TryParse(host[(comma + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hp)) (host, port) = (host[..comma].Trim(), hp);
        if (host.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || host.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
            return (host, port, "Named pipes and shared memory cannot go through an SSH tunnel; use TCP.");
        if (host.Contains('\\'))
            return (host, port, "A named instance (host\\instance) is found through SQL Browser over UDP, which an SSH tunnel cannot carry. Enter the instance's TCP port instead.");
        return (host, port, null);
    }

    /// <summary>The certificate is still checked against the real server name (HostNameInCertificate), not 127.0.0.1.</summary>
    public (Profile Profile, string? Note) RouteThrough(Profile p, string host, int port)
    {
        var (real, _, _) = TunnelTarget(p);
        var options = new Dictionary<string, string>(p.Options ?? []);
        if (!options.ContainsKey("hostNameInCertificate")) options["hostNameInCertificate"] = real;
        options.Remove("multiSubnetFailover");
        return (p with { Host = host, Port = port, Options = options }, null);
    }

    // Keywords ParseConnectionString maps onto profile fields; anything else the driver knows is reported as not applied.
    static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "Initial Catalog", "User ID", "Password", "Integrated Security", "Authentication", "Encrypt",
        "Trust Server Certificate", "Host Name In Certificate", "Application Intent", "Multi Subnet Failover",
        "Connect Timeout", "Command Timeout", "Application Name",
    };

    // SqlClient-only keywords (the other drivers do not know them, or Npgsql only as an ignored compatibility key).
    static readonly HashSet<string> OwnSpellings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "Initial Catalog", "Integrated Security", "Trusted_Connection", "Encrypt", "TrustServerCertificate", "Trust Server Certificate",
        "MultiSubnetFailover", "Multi Subnet Failover", "ApplicationIntent", "Application Intent", "Authentication", "HostNameInCertificate",
    };
    static readonly HashSet<string> Conventional = new(StringComparer.OrdinalIgnoreCase) { "Server", "Data Source", "Initial Catalog", "Database", "User ID", "Uid", "Pwd", "Password" };

    public ConnectionStringAffinity ConnectionStringAffinity(IReadOnlyDictionary<string, string> keywords)
    {
        var distinctive = ConnectionStringKeywords.CountKeys(keywords, OwnSpellings);
        // "host,port", "tcp:host", "host\instance": the server's address in SQL Server's own form (no driver of the others
        // reads a port or a protocol there).
        foreach (var key in new[] { "Server", "Data Source", "Address", "Addr", "Network Address" })
        {
            if (!keywords.TryGetValue(key, out var server)) continue;
            var comma = server.LastIndexOf(',');
            if ((comma > 0 && int.TryParse(server[(comma + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                || server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) || server.StartsWith("np:", StringComparison.OrdinalIgnoreCase)
                || server.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase) || server.Contains('\\'))
                distinctive++;
        }
        return new(distinctive, ConnectionStringKeywords.CountKeys(keywords, Conventional));
    }

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis)
    {
        var k = ConnectionStringKeywords.Apply(connectionString, () => new SqlConnectionStringBuilder());
        var b = k.Builder;
        var p = basis;
        var options = new Dictionary<string, string>(basis.Options ?? []);
        var notApplied = k.Named.Where(n => !Mapped.Contains(n)).ToList();
        string? secret = null;

        if (k.Has("Data Source"))
        {
            // "host,port" (optionally tcp:) splits into the two fields; host\instance stays whole.
            var ds = b.DataSource.Trim();
            var comma = ds.LastIndexOf(',');
            p = comma > 0 && int.TryParse(ds[(comma + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                ? p with { Host = ds[..comma].Trim(), Port = port }
                : p with { Host = ds, Port = null };
        }
        if (k.Has("Initial Catalog")) p = p with { DatabaseDefault = NullIfEmpty(b.InitialCatalog) };

        if (k.Has("Authentication") && b.Authentication != SqlAuthenticationMethod.NotSpecified)
        {
            AuthKind? auth = b.Authentication switch
            {
                SqlAuthenticationMethod.SqlPassword => AuthKind.Sql,
                SqlAuthenticationMethod.ActiveDirectoryInteractive => AuthKind.EntraInteractive,
                SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow => AuthKind.EntraDeviceCode,
                _ => null,
            };
            if (auth is { } a) p = p with { Auth = a };
            else notApplied.Add($"Authentication={b.Authentication}");
        }
        else if (k.Has("Integrated Security") && b.IntegratedSecurity) p = p with { Auth = AuthKind.Windows };
        else if (k.Has("User ID") || k.Has("Password")) p = p with { Auth = AuthKind.Sql };
        if (k.Has("User ID")) p = p with { Username = NullIfEmpty(b.UserID) };
        if (k.Has("Password")) secret = NullIfEmpty(b.Password);

        // A keyword the string does not name keeps the editor's choice (M3); one that weakens TLS is reported.
        var notes = new List<string>();
        if (k.Has("Encrypt"))
        {
            p = p with
            {
                TlsMode = b.Encrypt == SqlConnectionEncryptOption.Strict ? TlsMode.Strict
                    : b.Encrypt == SqlConnectionEncryptOption.Optional ? TlsMode.Optional
                    : TlsMode.Mandatory,
            };
            if (b.Encrypt == SqlConnectionEncryptOption.Optional)
                notes.Add("Encrypt=False: only the sign-in is encrypted; queries and results cross the network in clear text unless the server forces encryption.");
        }
        if (k.Has("Trust Server Certificate"))
        {
            p = p with { TrustCert = b.TrustServerCertificate };
            if (b.TrustServerCertificate) notes.Add(WeakTls.CertNotChecked("TrustServerCertificate=True"));
        }
        if (k.Has("Host Name In Certificate"))
        {
            var hn = NullIfEmpty(b.HostNameInCertificate);
            SetOption(options, "hostNameInCertificate", hn);
            // PT1-1: the editor has no field for it, so the override is named where the user sees it.
            if (hn != null)
                notes.Add($"HostNameInCertificate={hn}: the server's certificate is checked for the name {hn} instead of the server name {p.Host ?? "(none)"}; remove it unless that is the name on the server's certificate.");
        }
        if (k.Has("Application Intent")) options["applicationIntent"] = b.ApplicationIntent == ApplicationIntent.ReadOnly ? "readOnly" : "readWrite";
        if (k.Has("Multi Subnet Failover")) options["multiSubnetFailover"] = b.MultiSubnetFailover ? "true" : "false";
        if (k.Has("Application Name")) SetOption(options, "appName", NullIfEmpty(b.ApplicationName));
        if (k.Has("Connect Timeout")) p = p with { TimeoutS = b.ConnectTimeout };
        if (k.Has("Command Timeout")) p = p with { CommandTimeoutS = b.CommandTimeout };

        return new ParseConnectionStringResult
        {
            Profile = p with { Options = options.Count > 0 ? options : null },
            Secret = secret,
            Unknown = [.. k.Unknown],
            NotApplied = [.. notApplied],
            Notes = [.. notes],
        };
    }

    // Predicted from the batch's AST, then checked against the real columns: a statement that failed at run time
    // shifts later result sets, and a stale snapshot changes SELECT * — either way the names stop matching.
    /// <summary>A self-join, an ambiguous unqualified table name or a set operator (ScriptDom; <see cref="ResultSources.Refusal"/>).</summary>
    public string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) =>
        snapshot == null ? null : ResultSources.Refusal(batchText, snapshot, resultInBatch);

    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot)
    {
        if (snapshot == null || ResultSources.Predict(batchText, snapshot) is not { } sets || resultInBatch >= sets.Count || sets[resultInBatch] is not { } predicted) return null;
        if (predicted.Length != reader.FieldCount) return null;
        for (var i = 0; i < predicted.Length; i++)
            if (predicted[i].Name is { } name && !string.Equals(name, reader.GetName(i), StringComparison.OrdinalIgnoreCase)) return null;
        return predicted.Select(x => x.Origin).ToArray();
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static void SetOption(Dictionary<string, string> options, string key, string? value)
    {
        if (value == null) options.Remove(key);
        else options[key] = value;
    }

    SqlConnectionStringBuilder Build(Profile p, string? secret, string? database, bool? strict)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = p.Port is { } port and > 0 && !(p.Host ?? "").Contains(',') ? $"{p.Host},{port}" : p.Host ?? "localhost",
            ApplicationName = p.Options?.GetValueOrDefault("appName") ?? "Querithm",
            ConnectTimeout = p.TimeoutS ?? 15,
            Pooling = false, // Core pools physical connections (§6)
            MultipleActiveResultSets = false,
            TrustServerCertificate = p.TrustCert,
            CommandTimeout = p.CommandTimeoutS ?? 0,
        };
        if (!string.IsNullOrEmpty(database)) b.InitialCatalog = database;
        // The activity monitor turns transparent reconnects off: a drop must be seen (reconnect, re-probe, new identity).
        if (p.Options?.GetValueOrDefault("connectRetryCount") is { } retry && int.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) b.ConnectRetryCount = n;
        // §13 rule 2: TLS on by default. Strict where supported (2022+), else Mandatory.
        b.Encrypt = p.TlsMode switch
        {
            TlsMode.Strict => SqlConnectionEncryptOption.Strict,
            TlsMode.Optional or TlsMode.Disable => SqlConnectionEncryptOption.Optional,
            TlsMode.Mandatory => SqlConnectionEncryptOption.Mandatory,
            _ => strict == true && !p.TrustCert ? SqlConnectionEncryptOption.Strict : SqlConnectionEncryptOption.Mandatory,
        };
        if (p.Options?.GetValueOrDefault("hostNameInCertificate") is { Length: > 0 } hn) b.HostNameInCertificate = hn;
        if (p.Options?.GetValueOrDefault("applicationIntent") == "readOnly") b.ApplicationIntent = ApplicationIntent.ReadOnly;
        if (p.Options?.GetValueOrDefault("multiSubnetFailover") == "true") b.MultiSubnetFailover = true;
        switch (p.Auth)
        {
            case AuthKind.Sql:
            case AuthKind.Password:
                b.UserID = p.Username ?? "";
                b.Password = secret ?? "";
                break;
            case AuthKind.Windows:
            case AuthKind.Kerberos:
                b.IntegratedSecurity = true;
                break;
            case AuthKind.EntraInteractive:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrEmpty(p.Username)) b.UserID = p.Username;
                break;
            case AuthKind.EntraDeviceCode:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow;
                break;
            case AuthKind.EntraToken:
                break; // AccessToken is set on the connection
        }
        return b;
    }

    /// <summary>
    /// Opens within the profile's connect timeout in total (plus <see cref="OpenMargin"/>): with Default TLS it tries
    /// Strict (TDS 8) first and, when that fails on a server it did reach, Mandatory in the time left. SqlClient's own
    /// timeout does not bound everything (on Windows a host without a port is also tried over named pipes, measured at
    /// 30 s against an unroutable address with Connect Timeout=3), so a timer cancels the open and the error names the
    /// endpoint instead of "A task was canceled.".
    /// </summary>
    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        var budgetS = Math.Max(0, profile.TimeoutS ?? 15); // 0: no limit of our own (SqlClient's meaning); ct still ends it
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budgetS > 0) limit.CancelAfter(TimeSpan.FromSeconds(budgetS) + OpenMargin);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var tryStrict = profile.TlsMode == TlsMode.Default && !profile.TrustCert && !StrictKnownUnsupported(profile.Id);
        try
        {
            try
            {
                return await OpenOnceAsync(profile, secret, database, tryStrict, budgetS, limit.Token).ConfigureAwait(false);
            }
            catch (SqlException ex) when (tryStrict && !limit.IsCancellationRequested && !IsLoginFailure(ex) && !IsNotReachable(ex))
            {
                // The server answered but Strict failed (a server before 2022, or a certificate Strict rejects): try Mandatory
                // in the time left, and remember only when that works, so a passing outage never costs a profile Strict.
                var left = budgetS == 0 ? 0 : budgetS - (int)started.Elapsed.TotalSeconds;
                if (budgetS > 0 && left < 1) throw;
                var conn = await OpenOnceAsync(profile, secret, database, false, left, limit.Token).ConfigureAwait(false);
                MarkStrictUnsupported(profile.Id, DateTime.UtcNow);
                return conn;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw new TimeoutException(NotReachedMessage(profile, budgetS));
        }
        catch (SqlException ex) when (IsNotReachable(ex) || IsTimeout(ex))
        {
            ex.Data[EndpointKey] = NotReachedMessage(profile, budgetS);
            throw;
        }
    }

    internal static readonly TimeSpan OpenMargin = TimeSpan.FromSeconds(2);

    /// <summary>How long a "Strict unsupported" finding holds before Strict is tried again (an upgraded server).</summary>
    internal static readonly TimeSpan StrictProbeTtl = TimeSpan.FromHours(1);

    const string EndpointKey = "Querithm.NotReached";

    internal static bool StrictKnownUnsupported(string? profileId, DateTime? now = null)
    {
        if (string.IsNullOrEmpty(profileId) || !StrictUnsupported.TryGetValue(profileId, out var at)) return false;
        if ((now ?? DateTime.UtcNow) - at < StrictProbeTtl) return true;
        StrictUnsupported.TryRemove(profileId, out _);
        return false;
    }

    /// <summary>Never for an unsaved profile: they all share the id "", and one Test must not set the rule for every new one.</summary>
    internal static void MarkStrictUnsupported(string? profileId, DateTime now)
    {
        if (!string.IsNullOrEmpty(profileId)) StrictUnsupported[profileId] = now;
    }

    /// <summary>A changed or deleted profile (new host, new TLS settings) is probed afresh.</summary>
    public void ForgetConnections(string profileId) => StrictUnsupported.TryRemove(profileId, out _);

    string NotReachedMessage(Profile profile, int budgetS)
    {
        var (host, port, _) = TunnelTarget(profile);
        var endpoint = host.Contains('\\') ? host : $"{host}:{port}";
        var within = budgetS > 0 ? $" within {budgetS} s" : "";
        return $"Could not reach SQL Server at {endpoint}{within}. Check the server name and port, that the server accepts TCP connections, and any firewall in between.";
    }

    static bool IsLoginFailure(SqlException ex) => ex.Number is 18456 or 4060 or 18452 or 18488;

    /// <summary>
    /// Nothing answered: name or instance not found (53, 40, 26, 11001, 11004), refused (10061), network or host unreachable
    /// (10051, 10065, 1231). Strict cannot be the cause, so it is not retried as Mandatory.
    /// </summary>
    static bool IsNotReachable(SqlException ex) => ex.Number is 53 or 40 or 26 or 2 or 11001 or 11004 or 10061 or 10051 or 10065 or 1231
        // Managed SNI on Unix can report Number=0 and even SocketError.Success after a failed TCP connect.
        // Inspect the socket cause as well; a generic Number=0 alone can also be a TLS error and must stay distinct.
        || (ex.Number == 0 && ex.InnerException is System.Net.Sockets.SocketException socket && socket.SocketErrorCode is
            System.Net.Sockets.SocketError.Success or System.Net.Sockets.SocketError.ConnectionRefused
            or System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData
            or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable
            or System.Net.Sockets.SocketError.TimedOut);

    /// <summary>Connect or pre-login timeouts (258, -2, 10060).</summary>
    static bool IsTimeout(SqlException ex) => ex.Number is 258 or -2 or 10060;

    async Task<DbConnection> OpenOnceAsync(Profile profile, string? secret, string? database, bool strict, int connectTimeoutS, CancellationToken ct)
    {
        var conn = new SqlConnection(Build(profile with { TimeoutS = connectTimeoutS }, secret, database, strict).ConnectionString)
        {
            // Errors up to severity 16 arrive as messages, so later statements' results are still read (SSMS behaviour).
            FireInfoMessageEventOnUserErrors = true,
        };
        if (profile.Auth == AuthKind.EntraToken) conn.AccessToken = secret;
        // SqlClient's OpenAsync without its pooling (Core pools, §6) lets only 2 x CPU opens run at once per process and
        // queues the rest behind them: one dial to a server that never answers held a slot for the OS connect timeout
        // (minutes on Linux) after the caller gave up, and every other SQL Server open then failed with "too many
        // simultaneous non-pooled connection attempts". The synchronous Open has no such queue; it runs on a thread of
        // its own, bounded by Connect Timeout, and the caller's token (with OpenAsync's timer) ends the wait.
        var open = Task.Factory.StartNew(conn.Open, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await open.WaitAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch when (!open.IsCompleted)
        {
            // Given up while the dial still runs: the connection closes when it ends, whichever way.
            _ = open.ContinueWith(_ => conn.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct)
    {
        // FireInfoMessageEventOnUserErrors turns "Database 'x' does not exist" (severity 16) into an info message, so the
        // driver does not throw and the connection silently stays where it was; surface that as the failure it is.
        string? serverMessage = null;
        void OnInfo(object sender, SqlInfoMessageEventArgs e) => serverMessage = string.Join(" ", e.Errors.Cast<SqlError>().Select(x => x.Message));
        var sql = connection as SqlConnection;
        if (sql != null) sql.InfoMessage += OnInfo;
        try { await connection.ChangeDatabaseAsync(database, ct).ConfigureAwait(false); }
        finally { if (sql != null) sql.InfoMessage -= OnInfo; }
        if (!string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(serverMessage ?? $"Could not change the database to '{database}'.");
    }

    public string? CurrentDatabase(DbConnection connection) => string.IsNullOrEmpty(connection.Database) ? null : connection.Database;

    public string ServerVersion(DbConnection connection) => $"SQL Server {connection.ServerVersion}";

    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult<int?>(((SqlConnection)connection).ServerProcessId is var id and > 0 ? id : null);

    public ITransactionControl Transactions { get; } = new SqlServerTransactions();

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage)
    {
        var sql = (SqlConnection)connection;
        void Handler(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                onMessage(new ProviderMessage(err.Class > 10 ? Severity.Error : Severity.Info, err.Message,
                    err.LineNumber > 0 ? err.LineNumber : null, string.IsNullOrEmpty(err.Procedure) ? null : err.Procedure,
                    err.Number, err.State, err.Class));
        }
        sql.InfoMessage += Handler;
        return new Unsubscribe(() => sql.InfoMessage -= Handler);
    }

    sealed class Unsubscribe(Action a) : IDisposable
    {
        public void Dispose() => a();
    }

    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null)
    {
        // An open that reached nothing (OpenAsync): say which endpoint, not SqlClient's generic network paragraph.
        if (ex is SqlException { Data: var data } notReached && data[EndpointKey] is string reason)
            return [new ProviderMessage(Severity.Error, reason, null, null, notReached.Number, notReached.State, notReached.Class)];
        if (ex is SqlException sql)
            return sql.Errors.Cast<SqlError>()
                .Select(e => new ProviderMessage(Severity.Error, e.Message, e.LineNumber > 0 ? e.LineNumber : null,
                    string.IsNullOrEmpty(e.Procedure) ? null : e.Procedure, e.Number, e.State, e.Class))
                .ToList();
        return [new ProviderMessage(Severity.Error, ex.Message)];
    }

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions o)
    {
        var on = new List<string>();
        if (o.StatisticsIo) on.Add("SET STATISTICS IO ON;");
        if (o.StatisticsTime) on.Add("SET STATISTICS TIME ON;");
        if (o.ActualPlan) on.Add("SET STATISTICS XML ON;");
        if (on.Count == 0) return (null, null);
        return (string.Join(" ", on), string.Join(" ", on).Replace(" ON;", " OFF;"));
    }

    [GeneratedRegex(@"^(?<name>[a-z0-9_ ]+?)\s*(\(\s*(?<a>max|\d+)\s*(,\s*(?<b>\d+)\s*)?\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeSpec();

    public void AddParameter(DbCommand command, QueryParameter p)
    {
        var name = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
        var prm = new SqlParameter { ParameterName = name };
        var m = TypeSpec().Match((p.Type ?? "nvarchar(max)").Trim());
        if (m.Success && Enum.TryParse<SqlDbType>(m.Groups["name"].Value.Replace(" ", ""), true, out var t))
        {
            prm.SqlDbType = t;
            var a = m.Groups["a"].Value;
            if (a.Equals("max", StringComparison.OrdinalIgnoreCase)) prm.Size = -1;
            else if (int.TryParse(a, out var n))
            {
                if (t is SqlDbType.Decimal) { prm.Precision = (byte)n; if (int.TryParse(m.Groups["b"].Value, out var sc)) prm.Scale = (byte)sc; }
                else prm.Size = n;
            }
        }
        else prm.SqlDbType = SqlDbType.NVarChar;
        prm.Value = p.IsNull || p.Value == null ? DBNull.Value : p.Value;
        command.Parameters.Add(prm);
    }

    public ColumnHint GetColumnHint(DbColumn column)
    {
        var tn = (column.DataTypeName ?? "").ToLowerInvariant();
        if (column.UdtAssemblyQualifiedName != null || IsUdtName(tn) || tn == "sql_variant") return ColumnHint.ProviderString;
        if (tn is "xml" or "text" or "ntext") return ColumnHint.LargeText;
        return ColumnHint.Default;
    }

    static bool IsUdtName(string tn) => tn.EndsWith("geography") || tn.EndsWith("geometry") || tn.EndsWith("hierarchyid");

    public string? ReadAsString(DbDataReader reader, int ordinal)
    {
        var tn = reader.GetDataTypeName(ordinal).ToLowerInvariant();
        if (reader is SqlDataReader r)
        {
            if (tn is "decimal" or "numeric") return r.GetSqlDecimal(ordinal).ToString();
            if (IsUdtName(tn) || tn.Contains('.'))
            {
                var bytes = r.GetSqlBytes(ordinal);
                return bytes.IsNull ? null : "0x" + Convert.ToHexString(bytes.Value);
            }
        }
        var v = reader.GetValue(ordinal);
        return v switch
        {
            byte[] b => "0x" + Convert.ToHexString(b),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v?.ToString(),
        };
    }

    public bool IsPlanResultSet(DbDataReader reader) =>
        reader.FieldCount == 1 && reader.GetName(0) == "Microsoft SQL Server 2005 XML Showplan";

    public async Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('EngineEdition') AS int),
                   CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'SHOWPLAN')
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        var version = Version.TryParse(r.GetString(0), out var v) ? v : new Version(0, 0);
        var engineEdition = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        var edition = r.IsDBNull(2) ? null : r.GetString(2);
        var showplan = !r.IsDBNull(3) && r.GetInt32(3) == 1;
        // CREATE OR ALTER: SQL Server 2016 SP1 (13.0.4001) and later; always on Azure SQL.
        var createOrAlter = engineEdition is 5 or 8 || version >= new Version(13, 0, 4001);
        var planCap = showplan
            ? new CapabilityInfo { Available = true }
            : new CapabilityInfo { Available = false, Reason = "The login lacks SHOWPLAN permission in this database." };
        return new Capabilities
        {
            Trace = new CapabilityInfo { Available = false, Reason = "Not checked" },
            Debugger = new CapabilityInfo { Available = false, Reason = "SQL Server has no step debugger; use Trace (Extended Events)." },
            EstimatedPlan = planCap,
            ActualPlan = planCap,
            CreateOrAlter = createOrAlter,
            EncryptStrict = version.Major >= 16,
            MajorVersion = version.Major,
            EngineEdition = edition,
            CompletionTier = "tier1+tier2",
            // EngineEdition 5 = Azure SQL Database, 8 = Azure SQL Managed Instance.
            Product = new ProductInfo { Name = engineEdition switch { 5 => "Azure SQL Database", 8 => "Azure SQL Managed Instance", _ => "SQL Server" }, Version = r.GetString(0) },
        };
    }

    public async Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT name, CAST(CASE WHEN database_id <= 4 THEN 1 ELSE 0 END AS bit), state_desc
            FROM sys.databases WHERE HAS_DBACCESS(name) = 1 OR database_id <= 4 ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new DatabaseInfo { Name = r.GetString(0), IsSystem = r.GetBoolean(1), State = r.GetString(2) });
        return list.ToArray();
    }

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = 5;
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public void Cancel(DbCommand command) => command.Cancel(); // TDS Attention, out of band (§7 step 4)
}
