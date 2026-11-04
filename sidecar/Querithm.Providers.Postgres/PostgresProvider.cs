using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;

namespace Querithm.Providers.Postgres;

/// <summary>PostgreSQL via Npgsql (§2). A database is a separate connection, so pools are per (profile, database) (§6).</summary>
public sealed class PostgresProvider : IDbProvider
{
    /// <summary>
    /// A data source (each with the resolver that reads user composites Npgsql cannot map) per profile and connection
    /// identity: the connection string without its password (L19). The profile is part of the key (8a review), so two
    /// profiles with the same settings never replace each other's entry. The password is never in the source's connection
    /// string: a password provider sends the entry's current one at each open, so a changed password or a refreshed Entra
    /// token (a new value every time) updates the entry instead of replacing the source. A source leaves the cache when its
    /// profile is forgotten or it is the least recently used past <see cref="MaxDataSources"/>, and is disposed only once
    /// the last connection made from it has closed (<see cref="SourceEntry.Open"/>): disposing it under an open connection
    /// failed that connection's next command.
    /// </summary>
    internal sealed class SourceEntry
    {
        public NpgsqlDataSource Source { get; set; } = null!;
        /// <summary>What the password provider sends for the next open (null = none in the connection string).</summary>
        public volatile string? Password;
        public long LastUse { get; set; }
        /// <summary>Connections made from the source and not closed yet, opens in flight included.</summary>
        public int Open;
        public bool Retired;
        public bool Disposed;
    }

    /// <summary>At most this many data sources stay cached; the least recently used one goes first.</summary>
    internal const int MaxDataSources = 64;

    static readonly object SourcesGate = new();
    static readonly Dictionary<string, SourceEntry> Sources = new(StringComparer.Ordinal);
    static long _uses;

    internal static int CachedDataSources { get { lock (SourcesGate) return Sources.Count; } }

    internal static string[] CacheSnapshot() { lock (SourcesGate) return [.. Sources.Keys]; }

    static string KeyPrefix(string profileId) => profileId + "\u001f";

    /// <summary>The cached source for the profile's connection string (made on first use), with its password set to the string's.</summary>
    internal static NpgsqlDataSource DataSourceFor(string profileId, string connectionString) => EntryFor(profileId, connectionString, lease: false).Source;

    /// <summary>Test seam: the password the source's provider sends for the next open.</summary>
    internal static string? PasswordOf(NpgsqlDataSource source) { lock (SourcesGate) return Sources.Values.FirstOrDefault(e => e.Source == source)?.Password; }

    static SourceEntry EntryFor(string profileId, string connectionString, bool lease)
    {
        var identity = new NpgsqlConnectionStringBuilder(connectionString);
        var password = identity.Password;
        identity.Remove("Password");
        var key = KeyPrefix(profileId) + identity.ConnectionString;
        var retired = new List<SourceEntry>();
        SourceEntry entry;
        lock (SourcesGate)
        {
            if (!Sources.TryGetValue(key, out entry!))
            {
                while (Sources.Count >= MaxDataSources)
                {
                    var oldest = Sources.MinBy(kv => kv.Value.LastUse);
                    Sources.Remove(oldest.Key);
                    oldest.Value.Retired = true;
                    retired.Add(oldest.Value);
                }
                // Building is in-memory work (no I/O), so it may happen under the lock: concurrent first opens build only one.
                var made = new SourceEntry();
                var b = new NpgsqlDataSourceBuilder(identity.ConnectionString);
#pragma warning disable NPG9001 // experimental plugin API (see UnreadableUserTypesAsTextResolverFactory)
                b.AddTypeInfoResolverFactory(new UnreadableUserTypesAsTextResolverFactory());
#pragma warning restore NPG9001
                // Npgsql allows one source of passwords: a Passfile named in the profile's options stays the only one.
                if (string.IsNullOrEmpty(identity.Passfile))
                    b.UsePasswordProvider(_ => made.Password ?? "", (_, _) => ValueTask.FromResult(made.Password ?? ""));
                made.Source = b.Build();
                Sources[key] = entry = made;
            }
            entry.Password = password;
            entry.LastUse = ++_uses;
            if (lease) entry.Open++;
        }
        foreach (var r in retired) DisposeIfIdle(r);
        return entry;
    }

    /// <summary>
    /// A connection made from the cached source, which stays alive until the connection, once opened, closes — or, for one
    /// whose open failed, until <paramref name="release"/> runs (once either way).
    /// </summary>
    internal static NpgsqlConnection CreateConnection(string profileId, string connectionString, out Action release)
    {
        var entry = EntryFor(profileId, connectionString, lease: true);
        var done = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            lock (SourcesGate) entry.Open--;
            DisposeIfIdle(entry);
        }
        release = Release;
        NpgsqlConnection conn;
        try { conn = entry.Source.CreateConnection(); }
        catch
        {
            Release();
            throw;
        }
        // An opened connection releases its lease when it closes (Close, Dispose, or Npgsql breaking it all leave Open);
        // one that never opened is released by the caller (Npgsql has no Disposed event).
        conn.StateChange += (_, e) => { if (e.OriginalState == System.Data.ConnectionState.Open && e.CurrentState != System.Data.ConnectionState.Open) Release(); };
        return conn;
    }

    static void DisposeIfIdle(SourceEntry e)
    {
        lock (SourcesGate)
        {
            if (!e.Retired || e.Open > 0 || e.Disposed) return;
            e.Disposed = true;
        }
        e.Source.Dispose();
    }

    /// <summary>
    /// Drops every data source the profile used (disconnect, delete, a changed setting); each is disposed once its last
    /// connection has closed, so a connection still open (an idle pooled one, a tab's transaction) keeps working.
    /// </summary>
    public void ForgetConnections(string profileId)
    {
        List<SourceEntry> retired;
        lock (SourcesGate)
        {
            var keys = Sources.Keys.Where(k => k.StartsWith(KeyPrefix(profileId), StringComparison.Ordinal)).ToList();
            retired = keys.Select(k => Sources[k]).ToList();
            foreach (var k in keys) Sources.Remove(k);
            foreach (var e in retired) e.Retired = true;
        }
        foreach (var r in retired) DisposeIfIdle(r);
    }

    public Engine Engine => Engine.Pg;
    public Dialect Dialect => Dialect.Pg;
    public bool StopOnBatchError => true;
    public bool DatabaseScopedConnections => true;
    public IDialectService Language { get; } = new PgLanguageService();
    public IMetadataProvider Metadata { get; } = new PostgresMetadata();
    public IPlanProvider Plans { get; } = new PostgresPlans();
    public IObjectScripter Scripter { get; } = new PostgresScripter();
    public ITraceProvider? Trace => null;
    public IDebugProvider? Debugger { get; } = new PlDebugger();
    public IDataEditDialect DataEdit { get; } = new PostgresDataEdit();
    public IImportDialect Import { get; } = new PostgresImport();
    public ISchemaCompareDialect? SchemaCompare { get; } = new PostgresSchemaCompare();
    public IDataCompareDialect? DataCompare { get; } = new PostgresDataCompare();
    public IReferenceFinder? References { get; } = new Querithm.Intelligence.PgSql.PgReferenceFinder();
    public IActivityProvider? Activity { get; }

    public PostgresProvider() => Activity = new PostgresActivity(DataEdit);

    /// <summary>Every Phase D0a feature is implemented for PostgreSQL; server-dependent limits come from <see cref="GetCapabilitiesAsync"/>.</summary>
    public string? FeatureRefusal(Feature feature) => null;

    public ConnectionLifetime LifetimeFor(Profile profile) => ConnectionLifetime.Pooled;

    public EngineTraits Traits { get; } = new()
    {
        Name = "PostgreSQL", DefaultDatabase = "postgres", DefaultSchema = "public", DefaultPort = 5432, DefaultAuth = AuthKind.Password,
        AuthKinds = [AuthKind.Password, AuthKind.Kerberos, AuthKind.EntraToken], Locations = [LocationKind.Server], NameMatch = StringComparer.Ordinal,
    };

    public string? ValidateProfile(Profile p)
    {
        if ((p.Location ?? LocationKind.Server) != LocationKind.Server) return "A PostgreSQL profile connects to a server; it has no file or memory location.";
        // Password with an empty secret still covers trust/peer authentication; Sql is accepted as the same password sign-in.
        return p.Auth is AuthKind.None ? "PostgreSQL needs a sign-in: password/SCRAM (an empty password covers trust or peer), Kerberos or an Entra token." : null;
    }

    public ProductInfo Product(DbConnection connection) => new() { Name = "PostgreSQL", Version = ((NpgsqlConnection)connection).PostgreSqlVersion.ToString() };

    public string BuildConnectionString(Profile p, string? secret, string? database)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = p.Host ?? "localhost",
            Port = p.Port is > 0 ? p.Port.Value : 5432,
            Database = database ?? p.DatabaseDefault ?? "postgres",
            Username = p.Username,
            Timeout = p.TimeoutS ?? 15,
            CommandTimeout = p.CommandTimeoutS ?? 0,
            Pooling = false, // Core pools physical connections (§6)
            ApplicationName = p.Options?.GetValueOrDefault("appName") ?? "Querithm",
            // §13 rule 2: SSL Mode=Require by default; VerifyFull offered with a CA path.
            SslMode = p.TlsMode switch
            {
                TlsMode.Disable => SslMode.Disable,
                TlsMode.Prefer or TlsMode.Optional => SslMode.Prefer,
                TlsMode.VerifyCa => SslMode.VerifyCA,
                TlsMode.VerifyFull => SslMode.VerifyFull,
                _ => SslMode.Require,
            },
        };
        if (!string.IsNullOrEmpty(p.CaPath)) b.RootCertificate = p.CaPath;
        if (p.Auth is AuthKind.Password or AuthKind.Sql or AuthKind.EntraToken) b.Password = secret;
        if (p.Auth is AuthKind.Kerberos && !string.IsNullOrEmpty(p.Options?.GetValueOrDefault("kerberosServiceName")))
            b.KerberosServiceName = p.Options!["kerberosServiceName"];
        return b.ConnectionString;
    }

    /// <summary>One TCP host; several hosts (failover lists) or a Unix socket directory cannot go through one SSH forward.</summary>
    public (string Host, int Port, string? Refusal) TunnelTarget(Profile p)
    {
        var host = (p.Host ?? "localhost").Trim();
        var port = p.Port is > 0 ? p.Port.Value : 5432;
        if (host.Contains(',')) return (host, port, "Several hosts (a failover list) cannot share one SSH tunnel; give one host.");
        if (host.StartsWith('/') || host.StartsWith('@')) return (host, port, "A Unix socket path cannot go through an SSH tunnel; give the server's TCP host.");
        return (host, port, null);
    }

    /// <summary>Npgsql checks the certificate against the host it dials (127.0.0.1 here), so Verify full becomes Verify CA.</summary>
    public (Profile Profile, string? Note) RouteThrough(Profile p, string host, int port)
    {
        var routed = p with { Host = host, Port = port };
        if (p.TlsMode != TlsMode.VerifyFull) return (routed, null);
        return (routed with { TlsMode = TlsMode.VerifyCa },
            "Through the SSH tunnel the certificate's host name cannot be checked (the driver connects to 127.0.0.1), so Verify full runs as Verify CA: the certificate chain is still verified.");
    }

    // Keywords ParseConnectionString maps onto profile fields; anything else the driver knows is reported as not applied.
    static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Port", "Database", "Username", "Password", "SSL Mode", "Root Certificate", "Timeout", "Command Timeout",
        "Application Name", "Kerberos Service Name",
    };

    // libpq URI query parameters and the Npgsql keyword each one becomes.
    static readonly Dictionary<string, string> UriParams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = "Host", ["port"] = "Port", ["dbname"] = "Database", ["user"] = "Username", ["password"] = "Password",
        ["sslmode"] = "SSL Mode", ["sslrootcert"] = "Root Certificate", ["connect_timeout"] = "Timeout",
        ["application_name"] = "Application Name", ["krbsrvname"] = "Kerberos Service Name",
    };

    public ParseConnectionStringResult ParseConnectionString(string connectionString, Profile basis)
    {
        var uriUnknown = new List<string>();
        var text = connectionString.Trim();
        if (text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            text = FromUri(text, uriUnknown);

        var k = ConnectionStringKeywords.Apply(text, () => new NpgsqlConnectionStringBuilder());
        var b = k.Builder;
        var p = basis;
        var options = new Dictionary<string, string>(basis.Options ?? []);
        string? secret = null;

        if (k.Has("Host")) p = p with { Host = NullIfEmpty(b.Host), Port = k.Has("Port") ? b.Port : null };
        else if (k.Has("Port")) p = p with { Port = b.Port };
        if (k.Has("Database")) p = p with { DatabaseDefault = NullIfEmpty(b.Database) };
        if (k.Has("Username")) p = p with { Username = NullIfEmpty(b.Username) };
        if (k.Has("Password"))
        {
            secret = NullIfEmpty(b.Password);
            // An Azure access token also travels as the password, so that choice is kept.
            if (p.Auth is not (AuthKind.Password or AuthKind.EntraToken)) p = p with { Auth = AuthKind.Password };
        }
        else if (k.Has("Kerberos Service Name")) p = p with { Auth = AuthKind.Kerberos };
        if (k.Has("Kerberos Service Name")) SetOption(options, "kerberosServiceName", NullIfEmpty(b.KerberosServiceName));

        // A mode the string does not name keeps the editor's (Require by default, §13 rule 2; M3); a weaker one is reported.
        var notes = new List<string>();
        if (k.Has("SSL Mode"))
        {
            p = p with
            {
                TlsMode = b.SslMode switch
                {
                    SslMode.Disable => TlsMode.Disable,
                    SslMode.Allow or SslMode.Prefer => TlsMode.Prefer,
                    SslMode.VerifyCA => TlsMode.VerifyCa,
                    SslMode.VerifyFull => TlsMode.VerifyFull,
                    _ => TlsMode.Default,
                },
            };
            if (b.SslMode == SslMode.Disable) notes.Add(WeakTls.NotEncrypted("SSL Mode=Disable"));
            else if (b.SslMode is SslMode.Allow or SslMode.Prefer) notes.Add(WeakTls.Downgradable($"SSL Mode={b.SslMode}"));
        }
        if (k.Has("Root Certificate")) p = p with { CaPath = NullIfEmpty(b.RootCertificate) };
        if (k.Has("Timeout")) p = p with { TimeoutS = b.Timeout };
        if (k.Has("Command Timeout")) p = p with { CommandTimeoutS = b.CommandTimeout };
        if (k.Has("Application Name")) SetOption(options, "appName", NullIfEmpty(b.ApplicationName));

        return new ParseConnectionStringResult
        {
            Profile = p with { Options = options.Count > 0 ? options : null },
            Secret = secret,
            Unknown = [.. uriUnknown, .. k.Unknown],
            NotApplied = [.. k.Named.Where(n => !Mapped.Contains(n))],
            Notes = [.. notes],
        };
    }

    // The protocol's RowDescription names each column's table OID and attnum, so origins are exact and free.
    /// <summary>A self-join (one table OID for every alias) or a set operator: the row a key names may not be the one shown.</summary>
    public string? ResultEditRefusal(string batchText, int resultInBatch, SchemaSnapshot? snapshot) => PgResultShape.EditRefusal(batchText);

    public ColumnOrigin?[]? ResultOrigins(DbDataReader reader, IReadOnlyList<DbColumn> columns, string batchText, int resultInBatch, SchemaSnapshot? snapshot)
    {
        if (snapshot == null) return null;
        var byId = snapshot.Objects.Where(o => o.Columns != null).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        var origins = new ColumnOrigin?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] is not Npgsql.Schema.NpgsqlDbColumn { TableOID: > 0 } c || !byId.TryGetValue(c.TableOID, out var obj)) continue;
            var col = obj.Columns!.FirstOrDefault(x => x.Ordinal == c.ColumnAttributeNumber);
            if (col != null) origins[i] = new ColumnOrigin(obj.Id, col.Name);
        }
        return origins;
    }

    /// <summary>postgres://user:password@host:port/database?param=value → Npgsql keywords (single host only).</summary>
    static string FromUri(string text, List<string> unknown)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Not a valid postgres:// URI (multi-host URIs are not supported).");
        var b = new DbConnectionStringBuilder { ["Host"] = uri.Host.Trim('[', ']') };
        if (uri.Port > 0) b["Port"] = uri.Port.ToString(CultureInfo.InvariantCulture);
        if (uri.UserInfo.Length > 0)
        {
            var colon = uri.UserInfo.IndexOf(':');
            b["Username"] = Uri.UnescapeDataString(colon < 0 ? uri.UserInfo : uri.UserInfo[..colon]);
            if (colon >= 0) b["Password"] = Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]);
        }
        var db = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (db.Length > 0) b["Database"] = db;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (!UriParams.TryGetValue(name, out var keyword)) unknown.Add(name);
            // libpq spells modes verify-full / verify-ca; Npgsql's enum has no hyphen.
            else b[keyword] = keyword == "SSL Mode" ? value.Replace("-", "") : value;
        }
        return b.ConnectionString;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static void SetOption(Dictionary<string, string> options, string key, string? value)
    {
        if (value == null) options.Remove(key);
        else options[key] = value;
    }

    public async Task<DbConnection> OpenAsync(Profile profile, string? secret, string? database, FileGrant? file, CancellationToken ct)
    {
        var connectionString = BuildConnectionString(profile, secret, database);
        // The lease keeps the source alive from here until the connection closes, so a ForgetConnections meanwhile cannot
        // dispose it under the open.
        var conn = CreateConnection(profile.Id, connectionString, out var release);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            release();
            throw;
        }
    }

    public Task ChangeDatabaseAsync(DbConnection connection, string database, CancellationToken ct) =>
        throw new NotSupportedException("PostgreSQL connections are bound to one database; Core opens a pool per database.");

    public string? CurrentDatabase(DbConnection connection) => connection.Database;

    public string ServerVersion(DbConnection connection) => $"PostgreSQL {((NpgsqlConnection)connection).PostgreSqlVersion}";

    public Task<int?> GetSpidAsync(DbConnection connection, CancellationToken ct) => Task.FromResult<int?>(((NpgsqlConnection)connection).ProcessID);

    public ITransactionControl Transactions => PostgresTransactions.Instance;

    public IDisposable SubscribeMessages(DbConnection connection, Action<ProviderMessage> onMessage)
    {
        var c = (NpgsqlConnection)connection;
        void Handler(object sender, NpgsqlNoticeEventArgs e)
        {
            var n = e.Notice;
            var sev = n.Severity is "WARNING" ? Severity.Warning : Severity.Info;
            var text = string.IsNullOrEmpty(n.Detail) ? n.MessageText : $"{n.MessageText}\n{n.Detail}";
            var (proc, line) = ParseWhere(n.Where);
            onMessage(new ProviderMessage(sev, text, line, proc, null, null, null));
        }
        c.Notice += Handler;
        return new Unsubscribe(() => c.Notice -= Handler);
    }

    sealed class Unsubscribe(Action a) : IDisposable
    {
        public void Dispose() => a();
    }

    /// <summary>"PL/pgSQL function f(integer) line 5 at RAISE" → (f, 5).</summary>
    static (string? Proc, int? Line) ParseWhere(string? where)
    {
        if (string.IsNullOrEmpty(where)) return (null, null);
        var first = where.Split('\n')[0];
        const string fn = "function ";
        var fi = first.IndexOf(fn, StringComparison.Ordinal);
        var li = first.IndexOf(" line ", StringComparison.Ordinal);
        string? proc = fi >= 0 ? first[(fi + fn.Length)..(li > fi ? li : first.Length)].Trim() : null;
        int? line = null;
        if (li >= 0)
        {
            var rest = first[(li + 6)..];
            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var n)) line = n;
        }
        return (proc, line);
    }

    public IReadOnlyList<ProviderMessage> TranslateException(Exception ex, string? commandText = null)
    {
        if (ex is PostgresException pg)
        {
            int? line = null;
            if (pg.Position > 0 && commandText != null)
            {
                // Npgsql sends each statement separately, so Position is relative to the failing statement.
                var stmt = pg.BatchCommand?.CommandText;
                var start = string.IsNullOrEmpty(stmt) ? 0 : Math.Max(0, commandText.IndexOf(stmt.Trim(), StringComparison.Ordinal));
                var pos = Math.Min(start + pg.Position - 1, commandText.Length);
                line = 1;
                for (var i = 0; i < pos; i++) if (commandText[i] == '\n') line++;
            }
            var (proc, procLine) = ParseWhere(pg.Where);
            var text = pg.MessageText;
            if (!string.IsNullOrEmpty(pg.Detail)) text += "\nDETAIL: " + pg.Detail;
            if (!string.IsNullOrEmpty(pg.Hint)) text += "\nHINT: " + pg.Hint;
            if (!string.IsNullOrEmpty(pg.Where)) text += "\nCONTEXT: " + pg.Where;
            return [new ProviderMessage(Severity.Error, $"{pg.SqlState}: {text}", line ?? procLine, proc)];
        }
        if (ex is NpgsqlException { InnerException: { } inner }) return [new ProviderMessage(Severity.Error, $"{ex.Message} {inner.Message}")];
        return [new ProviderMessage(Severity.Error, ex.Message)];
    }

    public (string? Before, string? After) SessionOptionsSql(ExecuteOptions options) => (null, null);

    public void AddParameter(DbCommand command, QueryParameter p)
    {
        // Values are sent as untyped text so the server resolves the type from context (never string substitution).
        var prm = p.Name.StartsWith('$') ? new NpgsqlParameter() : new NpgsqlParameter(p.Name.TrimStart(':', '@'), null);
        prm.NpgsqlDbType = NpgsqlDbType.Unknown;
        prm.Value = p.IsNull || p.Value == null ? DBNull.Value : p.Value;
        command.Parameters.Add(prm);
    }

    public ColumnHint GetColumnHint(DbColumn column)
    {
        var tn = (column.DataTypeName ?? "").ToLowerInvariant();
        if (tn is "interval" or "time with time zone" or "timetz" or "money") return ColumnHint.ProviderString;
        if (tn is "numeric" && column.NumericPrecision is null or 0) return ColumnHint.ProviderString;
        if (column.DataType == null || column.DataType == typeof(object)) return ColumnHint.ProviderString;
        return ColumnHint.Default;
    }

    const uint NumericOid = 1700, IntervalOid = 1186, TimetzOid = 1266;

    public string? ReadAsString(DbDataReader reader, int ordinal)
    {
        var r = (NpgsqlDataReader)reader;
        // By type OID: the reader's type name carries the modifiers ("numeric(45)", "interval(3)", "time(2) with time zone").
        var oid = r.GetDataTypeOID(ordinal);
        if (oid == IntervalOid)
        {
            var iv = r.GetFieldValue<NpgsqlInterval>(ordinal);
            var parts = new List<string>();
            if (iv.Months != 0) parts.Add(iv.Months % 12 == 0 ? $"{iv.Months / 12} year{(Math.Abs(iv.Months) == 12 ? "" : "s")}" : $"{iv.Months} mon");
            if (iv.Days != 0) parts.Add($"{iv.Days} day{(Math.Abs(iv.Days) == 1 ? "" : "s")}");
            if (iv.Time != 0 || parts.Count == 0) parts.Add(TimeSpan.FromTicks(iv.Time * 10).ToString(@"hh\:mm\:ss\.FFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'));
            return string.Join(" ", parts);
        }
        if (oid == NumericOid)
        {
            // Unconstrained numeric can be NaN, ±Infinity or wider than System.Decimal: render the wire value exactly.
            using var stream = r.GetStream(ordinal);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return PgBinaryText.Value(NumericOid, ms.ToArray());
        }
        var v = r.GetValue(ordinal);
        return v switch
        {
            null or DBNull => null,
            DateTimeOffset dto when oid == TimetzOid => dto.ToString("HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };
    }

    public bool IsPlanResultSet(DbDataReader reader) => false;

    public async Task<Capabilities> GetCapabilitiesAsync(DbConnection connection, CancellationToken ct)
    {
        var c = (NpgsqlConnection)connection;
        return await Task.FromResult(new Capabilities
        {
            Trace = new CapabilityInfo { Available = false, Reason = "Trace (Extended Events) is SQL Server only." },
            Debugger = new CapabilityInfo { Available = false, Reason = "Not checked" },
            EstimatedPlan = new CapabilityInfo { Available = true },
            ActualPlan = new CapabilityInfo { Available = true, Reason = "EXPLAIN ANALYZE runs the query (inside a rolled-back transaction)." },
            CreateOrAlter = false,
            EncryptStrict = false,
            MajorVersion = c.PostgreSqlVersion.Major,
            EngineEdition = "PostgreSQL",
            CompletionTier = "tier1+tier2",
            Product = Product(c),
        }).ConfigureAwait(false);
    }

    public async Task<DatabaseInfo[]> ListDatabasesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT datname, datname IN ('postgres','template0','template1') FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY 2, 1";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseInfo>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(new DatabaseInfo { Name = r.GetString(0), IsSystem = r.GetBoolean(1), State = "ONLINE" });
        return list.ToArray();
    }

    public async Task ValidateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1";
        cmd.CommandTimeout = 5;
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public void Cancel(DbCommand command) => command.Cancel(); // CancelRequest, out of band (§7 step 4)
}
