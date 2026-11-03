using System.Data.Common;
using System.Globalization;
using System.Text;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.MySql;

/// <summary>
/// Activity monitor for MySQL / MariaDB (Phase D2). Session identity = (connection id, performance_schema THREAD_ID): a
/// connection id is not reused until it wraps, and the thread id makes a reused one tell. MySQL reads sessions, statements,
/// InnoDB lock waits and digests from performance_schema; MariaDB (performance_schema off by default) reads
/// information_schema.PROCESSLIST and INNODB_LOCK_WAITS; without performance_schema the server hides the thread id, so a
/// session key carries the connection id only (Instance empty) and kills are refused with the reason. Cancel and terminate re-check the identity on
/// the server in the same batch: the KILL is prepared from the verified thread row (never from text the webview sent).
/// </summary>
public sealed class MySqlActivity : IActivityProvider
{
    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CONNECTION_ID(), (SELECT THREAD_ID FROM performance_schema.threads WHERE PROCESSLIST_ID = CONNECTION_ID()), @@performance_schema";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            if (!r.IsDBNull(1)) return new SessionKey { Id = id, Instance = Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)! };
            // performance_schema off (MariaDB's default): the server hides the start identity. The connection id still names
            // exactly one live connection, so the monitor tells its own sessions apart; kills, which would need the instance to
            // rule out a reused id, are refused by the capabilities.
            if (Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) == 0) return new SessionKey { Id = id, Instance = "" };
            throw new IdentityUnavailableException("performance_schema does not show this connection's thread, so its identity cannot be verified.");
        }
        // 1142 SELECT denied on performance_schema; 1146 no such table (a server built without it).
        catch (MySqlException ex) when (ex.Number is 1142 or 1146)
        {
            throw new IdentityUnavailableException($"The server refused this connection's read of its own thread in performance_schema ({ex.Number}).", ex);
        }
    }

    public string ConnectionToken(DbConnection connection) =>
        connection is MySqlConnection m ? m.ServerThread.ToString(CultureInfo.InvariantCulture) : connection.GetHashCode().ToString(CultureInfo.InvariantCulture);

    /// <summary>Autocommit; short lock waits; READ COMMITTED so the monitor's information_schema reads hold no snapshot.</summary>
    public string MonitorSessionSetupSql => "SET SESSION lock_wait_timeout = 2; SET SESSION innodb_lock_wait_timeout = 2; SET SESSION transaction_isolation = 'READ-COMMITTED';";

    public void PrepareMonitorConnection(DbConnection connection) { } // MySqlConnector always throws MySqlException

    // 1045 access denied (wrong password, unknown user), 1862 password expired, 1820 must reset the password first.
    public bool CredentialsRejected(Exception ex) => ex is MySqlException { Number: 1045 or 1862 or 1820 };

    public const string Cpu = "CPU", Lock = "Lock", Latch = "Latch", BufferIo = "Buffer IO", LogIo = "Log IO", Network = "Network", Other = "Other";

    /// <summary>performance_schema instrument names (wait/io/file/innodb/…, wait/lock/…, wait/synch/…) and "state:…" from the session list.</summary>
    public string WaitCategory(string waitType)
    {
        var w = waitType.ToLowerInvariant();
        if (w.StartsWith("wait/lock/", StringComparison.Ordinal) || w.StartsWith("state:", StringComparison.Ordinal) && w.Contains("lock", StringComparison.Ordinal)) return Lock;
        if (w.StartsWith("wait/synch/", StringComparison.Ordinal)) return Latch;
        if (w.StartsWith("wait/io/socket/", StringComparison.Ordinal)) return Network;
        if (w.StartsWith("wait/io/file/", StringComparison.Ordinal) && (w.Contains("log", StringComparison.Ordinal) || w.Contains("redo", StringComparison.Ordinal))) return LogIo;
        if (w.StartsWith("wait/io/", StringComparison.Ordinal)) return BufferIo;
        return w == "cpu" ? Cpu : Other;
    }

    /// <summary>A connection waiting for its client (idle, socket reads) is not a server wait.</summary>
    public bool IsBenignWait(string waitType) =>
        waitType == "idle" || waitType.StartsWith("wait/io/socket/sql/client_connection", StringComparison.Ordinal);

    static bool Digits(string s, int max) => s.Length is > 0 && s.Length <= max && s.All(char.IsAsciiDigit);

    /// <summary>A THREAD_ID (decimal), or empty where the server shows none (MariaDB without performance_schema).</summary>
    public bool IsValidInstance(string instance) => instance.Length == 0 || Digits(instance, 20);

    /// <summary>The running statement's performance_schema EVENT_ID.</summary>
    public bool IsValidStatementStart(string statementStart) => Digits(statementStart, 20);

    // ---- probe ----

    /// <summary>Raw probe values; <see cref="Capabilities"/> turns them into panes and reasons (pure, unit-tested).</summary>
    public sealed record Probe(string Version, bool MariaDb, bool PerformanceSchema, bool Process, bool KillOthers, string? Login, bool LockWaitsView);

    public async Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        var maria = MySqlProvider.IsMariaDb(connection);
        Probe p;
        await using (var cmd = connection.CreateCommand())
        {
            // Privileges as granted to the account (roles granted to it count once activated; a role-only grant reads as missing,
            // which only hides the kill buttons for other accounts — the server still decides).
            cmd.CommandText = """
                SELECT VERSION(), @@performance_schema, CURRENT_USER(),
                  EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES p WHERE p.GRANTEE = CONCAT('''', SUBSTRING_INDEX(CURRENT_USER(), '@', 1), '''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''') AND p.PRIVILEGE_TYPE = 'PROCESS'),
                  EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES p WHERE p.GRANTEE = CONCAT('''', SUBSTRING_INDEX(CURRENT_USER(), '@', 1), '''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''') AND p.PRIVILEGE_TYPE IN ('SUPER', 'CONNECTION_ADMIN', 'CONNECTION ADMIN')),
                  EXISTS (SELECT 1 FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'INNODB_LOCK_WAITS')
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            p = new Probe(r.GetString(0), maria, Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) == 1, Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture) == 1,
                Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture) == 1, r.IsDBNull(2) ? null : r.GetString(2), Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture) == 1);
        }
        SessionKey self;
        try { self = await IdentityAsync(connection, ct).ConfigureAwait(false); }
        catch (IdentityUnavailableException)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT CONNECTION_ID()";
            self = new SessionKey { Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture), Instance = "" };
        }
        return new MySqlActivitySession(p, Capabilities(p), self);
    }

    /// <summary>MySQL with performance_schema: its tables. Otherwise (MariaDB, or performance_schema off) PROCESSLIST and the InnoDB views.</summary>
    public static bool UsesPerformanceSchema(Probe p) => !p.MariaDb && p.PerformanceSchema;

    public static MonitorCapabilities Capabilities(Probe p)
    {
        PaneCapability Pane(MonitorPane pane, bool ok, string? reason) => new() { Pane = pane, Available = ok, Reason = reason };
        var ps = p.PerformanceSchema;
        var noPs = p.MariaDb ? "MariaDB's performance_schema is off (performance_schema=ON in the server configuration, then a restart)" : "performance_schema is off on this server";
        var identity = ps ? null : $"{noPs}, so a session's identity cannot be verified before a kill.";
        return new MonitorCapabilities
        {
            ServerVersion = (p.MariaDb ? "MariaDB " : "MySQL ") + p.Version,
            AllSessionsVisible = p.Process,
            SessionsReason = p.Process ? null : "Only this account's sessions are visible (other accounts need the PROCESS privilege).",
            CanTerminate = ps,
            TerminateReason = identity ?? (p.KillOthers ? null : "Only this account's own sessions can be cancelled or terminated (others need CONNECTION_ADMIN or SUPER)."),
            CanCancelRequest = ps,
            CancelReason = identity,
            LivePlans = false,
            LivePlanReason = "The plan of a running statement is not read here (EXPLAIN FOR CONNECTION); EXPLAIN the text in a new tab.",
            QueryHashReason = UsesPerformanceSchema(p) ? null : $"Statement digests need performance_schema{(p.MariaDb ? " (Querithm reads them on MySQL)" : "")}.",
            Panes =
            [
                Pane(MonitorPane.Sessions, true, p.Process ? null : "Other accounts' sessions are hidden (requires the PROCESS privilege)."),
                Pane(MonitorPane.Locks, UsesPerformanceSchema(p), UsesPerformanceSchema(p) ? "InnoDB row and table locks, and metadata locks, from performance_schema." : $"Lock lists need MySQL's performance_schema.data_locks; {(p.MariaDb ? "MariaDB has none" : noPs)}. Blocking chains are still shown in the session list."),
                Pane(MonitorPane.Waits, ps, ps ? "Only the wait instruments enabled in performance_schema.setup_instruments are counted." : $"{noPs}: no wait counters."),
                Pane(MonitorPane.Metrics, true, "MySQL / MariaDB do not report CPU from SQL."),
                Pane(MonitorPane.Io, false, "File I/O per instrument is not read for MySQL / MariaDB yet."),
                Pane(MonitorPane.TopQueries, UsesPerformanceSchema(p), UsesPerformanceSchema(p) ? null : $"Top queries come from performance_schema statement digests: {(p.MariaDb ? "read on MySQL only" : noPs)}."),
                Pane(MonitorPane.Deadlocks, false, "MySQL / MariaDB keep only the latest deadlock (SHOW ENGINE INNODB STATUS); it is not read here. The deadlocks counter is in the overview where the server reports one."),
            ],
            Login = p.Login,
            OtherLoginsReason = p.KillOthers ? null : "Cancelling or terminating another account's session requires CONNECTION_ADMIN or SUPER.",
        };
    }

    /// <summary>Below MySQL 8.0 (no data_locks, no CTE-free window functions) or MariaDB 10.6 the monitor does not start.</summary>
    public static string? StartRefusal(Probe p)
    {
        var parts = p.Version.Split('.', '-');
        int At(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (p.MariaDb) return At(0) > 10 || At(0) == 10 && At(1) >= 6 ? null : "The activity monitor needs MariaDB 10.6 or later.";
        return At(0) >= 8 ? null : "The activity monitor needs MySQL 8.0 or later.";
    }

    // ---- own-tab blocking banner ----

    public async Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return ([], null);
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 5; // a pool connection has none
        var names = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            names.Add("@b" + i.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.Add(new MySqlParameter(names[^1], ids[i]));
        }
        // INNODB_TRX: the waiting transaction and how long it has waited; the blocker from the lock-wait view of the product.
        var waits = MySqlProvider.IsMariaDb(connection)
            ? "SELECT r.trx_mysql_thread_id AS waiter, b.trx_mysql_thread_id AS blocker FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX r ON r.trx_id = w.requesting_trx_id JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id"
            : "SELECT rt.PROCESSLIST_ID AS waiter, bt.PROCESSLIST_ID AS blocker FROM performance_schema.data_lock_waits w JOIN performance_schema.threads rt ON rt.THREAD_ID = w.REQUESTING_THREAD_ID JOIN performance_schema.threads bt ON bt.THREAD_ID = w.BLOCKING_THREAD_ID";
        cmd.CommandText = $"""
            SELECT EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES p WHERE p.GRANTEE = CONCAT('''', SUBSTRING_INDEX(CURRENT_USER(), '@', 1), '''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''') AND p.PRIVILEGE_TYPE = 'PROCESS');
            SELECT x.blocker, COUNT(DISTINCT x.waiter), MAX(TIMESTAMPDIFF(MICROSECOND, t.trx_wait_started, NOW(6)) DIV 1000)
            FROM ({waits}) x LEFT JOIN information_schema.INNODB_TRX t ON t.trx_mysql_thread_id = x.waiter
            WHERE x.blocker IN ({string.Join(", ", names)}) GROUP BY x.blocker
            """;
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            if (Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture) != 1) return (null, "Seeing other accounts' sessions requires the PROCESS privilege.");
            await r.NextResultAsync(ct).ConfigureAwait(false);
            var rows = new List<BlockedBySummary>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new BlockedBySummary(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture)));
            return (rows.ToArray(), null);
        }
        catch (MySqlException ex) when (ex.Number is 1142 or 1146 or 1227)
        {
            return (null, $"The lock-wait views cannot be read by this account ({ex.Number}).");
        }
    }
}

/// <summary>Per-monitor-connection state: the probe and the queries it chose.</summary>
sealed class MySqlActivitySession(MySqlActivity.Probe probe, MonitorCapabilities caps, SessionKey self) : IActivitySession
{
    public MonitorCapabilities Capabilities => caps;
    public string? StartRefusal => MySqlActivity.StartRefusal(probe);
    public SessionKey Self => self;
    public bool WaitsFromSessions => false;

    bool Ps => MySqlActivity.UsesPerformanceSchema(probe);

    /// <summary>MySQL: performance_schema threads with the running top-level statement and the InnoDB transaction.</summary>
    const string PsSessions = """
        SELECT t.PROCESSLIST_ID, t.THREAD_ID, t.PROCESSLIST_USER, t.PROCESSLIST_HOST, t.PROCESSLIST_DB, t.PROCESSLIST_COMMAND, t.PROCESSLIST_STATE,
               t.PROCESSLIST_TIME, LEFT(t.PROCESSLIST_INFO, 4096), CHAR_LENGTH(t.PROCESSLIST_INFO), s.EVENT_ID, s.TIMER_WAIT DIV 1000000000, s.DIGEST, s.ROWS_SENT,
               x.trx_state, TIMESTAMPDIFF(MICROSECOND, x.trx_started, NOW(6)) DIV 1000, TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) DIV 1000, x.trx_rows_modified,
               (SELECT a.ATTR_VALUE FROM performance_schema.session_connect_attrs a WHERE a.PROCESSLIST_ID = t.PROCESSLIST_ID AND a.ATTR_NAME = 'program_name' LIMIT 1),
               COUNT(*) OVER ()
        FROM performance_schema.threads t
        LEFT JOIN performance_schema.events_statements_current s ON s.THREAD_ID = t.THREAD_ID AND s.END_EVENT_ID IS NULL AND s.NESTING_EVENT_LEVEL = 0
        LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = t.PROCESSLIST_ID
        WHERE t.TYPE = 'FOREGROUND' AND t.PROCESSLIST_ID IS NOT NULL AND (@db IS NULL OR t.PROCESSLIST_DB = @db)
        ORDER BY CASE WHEN x.trx_state = 'LOCK WAIT' THEN 0 WHEN t.PROCESSLIST_COMMAND <> 'Sleep' THEN 1 ELSE 2 END, t.PROCESSLIST_ID
        LIMIT @max;
        SELECT rt.PROCESSLIST_ID, bt.PROCESSLIST_ID FROM performance_schema.data_lock_waits w
        JOIN performance_schema.threads rt ON rt.THREAD_ID = w.REQUESTING_THREAD_ID JOIN performance_schema.threads bt ON bt.THREAD_ID = w.BLOCKING_THREAD_ID;
        SELECT DISTINCT wt.PROCESSLIST_ID, gt.PROCESSLIST_ID FROM performance_schema.metadata_locks w
        JOIN performance_schema.metadata_locks g ON g.OBJECT_TYPE = w.OBJECT_TYPE AND g.OBJECT_SCHEMA <=> w.OBJECT_SCHEMA AND g.OBJECT_NAME <=> w.OBJECT_NAME
             AND g.LOCK_STATUS = 'GRANTED' AND g.OWNER_THREAD_ID <> w.OWNER_THREAD_ID
        JOIN performance_schema.threads wt ON wt.THREAD_ID = w.OWNER_THREAD_ID JOIN performance_schema.threads gt ON gt.THREAD_ID = g.OWNER_THREAD_ID
        WHERE w.LOCK_STATUS = 'PENDING' AND wt.PROCESSLIST_ID IS NOT NULL AND gt.PROCESSLIST_ID IS NOT NULL;
        """;

    /// <summary>MariaDB (or performance_schema off): PROCESSLIST, INNODB_TRX and, where it exists, INNODB_LOCK_WAITS.</summary>
    string PlistSessions => $"""
        SELECT p.ID, {(probe.PerformanceSchema ? "(SELECT t.THREAD_ID FROM performance_schema.threads t WHERE t.PROCESSLIST_ID = p.ID)" : "NULL")}, p.USER, p.HOST, p.DB, p.COMMAND, p.STATE,
               p.TIME, LEFT(p.INFO, 4096), CHAR_LENGTH(p.INFO), NULL, {(probe.MariaDb ? "p.TIME_MS" : "p.TIME * 1000")}, NULL, NULL,
               x.trx_state, TIMESTAMPDIFF(MICROSECOND, x.trx_started, NOW(6)) DIV 1000, TIMESTAMPDIFF(MICROSECOND, x.trx_wait_started, NOW(6)) DIV 1000, x.trx_rows_modified,
               NULL, COUNT(*) OVER ()
        FROM information_schema.PROCESSLIST p LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = p.ID
        WHERE p.COMMAND NOT IN ('Daemon', 'Binlog Dump', 'Slave_IO', 'Slave_SQL', 'Slave_worker') AND (@db IS NULL OR p.DB = @db)
        ORDER BY CASE WHEN x.trx_state = 'LOCK WAIT' THEN 0 WHEN p.COMMAND <> 'Sleep' THEN 1 ELSE 2 END, p.ID
        LIMIT @max;
        {(probe.LockWaitsView
            ? "SELECT r.trx_mysql_thread_id, b.trx_mysql_thread_id FROM information_schema.INNODB_LOCK_WAITS w JOIN information_schema.INNODB_TRX r ON r.trx_id = w.requesting_trx_id JOIN information_schema.INNODB_TRX b ON b.trx_id = w.blocking_trx_id;"
            : "SELECT NULL, NULL FROM DUAL WHERE FALSE;")}
        SELECT NULL, NULL FROM DUAL WHERE FALSE;
        """;

    const string WaitsSql = "SELECT EVENT_NAME, SUM_TIMER_WAIT DIV 1000000000, COUNT_STAR FROM performance_schema.events_waits_summary_global_by_event_name WHERE COUNT_STAR > 0;";

    const string MetricsSql = """
        SHOW GLOBAL STATUS WHERE Variable_name IN ('Questions', 'Threads_running', 'Threads_connected', 'Innodb_buffer_pool_read_requests', 'Innodb_buffer_pool_reads', 'Innodb_deadlocks', 'Uptime');
        SELECT COUNT(*) FROM information_schema.INNODB_TRX x JOIN information_schema.PROCESSLIST p ON p.ID = x.trx_mysql_thread_id WHERE p.COMMAND = 'Sleep';
        """;

    public async Task<TickResult> TickAsync(DbConnection connection, TickRequest request, CancellationToken ct)
    {
        PaneResult<ActivityRow[]>? sessions = null;
        Dictionary<string, string?>? texts = null;
        PaneResult<WaitCounter[]>? waits = null;
        PaneResult<MetricCounter[]>? metrics = null;
        string? reset = null;
        if (request.Sessions)
        {
            try { (sessions, texts) = await SessionsAsync(connection, request, ct).ConfigureAwait(false); }
            catch (MySqlException ex) { sessions = PaneResult<ActivityRow[]>.Error(ex.Message); }
        }
        if (request.Waits)
        {
            if (!probe.PerformanceSchema) waits = PaneResult<WaitCounter[]>.Degraded(caps.Panes.First(p => p.Pane == MonitorPane.Waits).Reason ?? "not available");
            else
            {
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = WaitsSql;
                    await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    var list = new List<WaitCounter>();
                    var activity = new MySqlActivity();
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var name = r.GetString(0);
                        list.Add(new WaitCounter(name, activity.WaitCategory(name), Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture), null));
                    }
                    waits = PaneResult<WaitCounter[]>.Ok(list.ToArray());
                }
                catch (MySqlException ex) { waits = PaneResult<WaitCounter[]>.Error(ex.Message); }
            }
        }
        if (request.Metrics)
        {
            try { (metrics, reset) = await MetricsAsync(connection, ct).ConfigureAwait(false); }
            catch (MySqlException ex) { metrics = PaneResult<MetricCounter[]>.Error(ex.Message); }
        }
        var io = request.Io ? PaneResult<IoCounter[]>.Degraded(caps.Panes.First(p => p.Pane == MonitorPane.Io).Reason ?? "not available") : null;
        return new TickResult(sessions, texts, waits, metrics, io, reset);
    }

    async Task<(PaneResult<ActivityRow[]>, Dictionary<string, string?>)> SessionsAsync(DbConnection connection, TickRequest request, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Ps ? PsSessions : PlistSessions;
        cmd.Parameters.Add(new MySqlParameter("@db", (object?)request.DatabaseFilter ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@max", Math.Max(1, request.MaxSessions)));
        var rows = new List<Raw>();
        var total = 0;
        var blockers = new Dictionary<long, HashSet<long>>();
        var mdl = new HashSet<long>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                long? L(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
                string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                rows.Add(new Raw(L(0)!.Value, S(1) ?? "", S(2), S(3), S(4), S(5), S(6), L(7), S(8), L(9), S(10), L(11), S(12), L(13), S(14), L(15), L(16), L(17), S(18)));
                total = (int)(L(19) ?? 0);
            }
            for (var set = 0; set < 2 && await r.NextResultAsync(ct).ConfigureAwait(false); set++)
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                    var waiter = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                    var blocker = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                    (blockers.TryGetValue(waiter, out var b) ? b : blockers[waiter] = []).Add(blocker);
                    if (set == 1) mdl.Add(waiter);
                }
        }
        var texts = new Dictionary<string, string?>();
        var result = rows.Select(x =>
        {
            string? textId = null;
            if (x.Info != null)
            {
                textId = "s" + x.Id.ToString(CultureInfo.InvariantCulture);
                texts[textId] = x.Info;
            }
            var sleeping = x.Command == "Sleep";
            var lockWait = x.TrxState == "LOCK WAIT";
            var waitType = lockWait ? "wait/lock/innodb/row" : !sleeping && x.State is { } st && st.StartsWith("Waiting for", StringComparison.Ordinal) ? "state:" + st : null;
            return new ActivityRow
            {
                Key = new SessionKey { Id = x.Id, Instance = x.Thread },
                Login = x.User, Host = x.Host, Program = x.Program, Database = x.Db,
                Status = sleeping ? (x.TrxState != null ? "idle in transaction" : "sleeping") : "running",
                Command = x.Command, BackendType = "foreground", IsUserSession = x.User is not (null or "system user" or "event_scheduler"),
                WaitType = waitType, WaitResource = waitType == null ? null : x.State, WaitMs = lockWait ? x.WaitMs : waitType != null ? x.StatementMs ?? x.Seconds * 1000 : null,
                BlockedBy = blockers.TryGetValue(x.Id, out var b) ? [.. b.Order()] : [],
                BlockedByNote = mdl.Contains(x.Id) ? "A metadata lock held by another session (any granted lock on the object is shown)." : null,
                TextId = textId, TextNote = x.InfoLength > 4096 ? $"First 4,096 of {x.InfoLength:N0} characters." : null,
                QueryHash = x.Digest, ElapsedMs = sleeping ? null : x.StatementMs ?? x.Seconds * 1000, TxElapsedMs = x.TxMs,
                StateChangeMs = x.Seconds * 1000, StatementStart = sleeping ? null : x.EventId, RowCount = x.RowsSent,
                OpenTx = x.TrxState != null ? 1 : 0,
            };
        }).ToArray();
        var pane = total > result.Length ? PaneResult<ActivityRow[]>.Truncated(result, total) : PaneResult<ActivityRow[]>.Ok(result);
        return (pane, texts);
    }

    sealed record Raw(long Id, string Thread, string? User, string? Host, string? Db, string? Command, string? State, long? Seconds, string? Info, long? InfoLength,
        string? EventId, long? StatementMs, string? Digest, long? RowsSent, string? TrxState, long? TxMs, long? WaitMs, long? RowsModified, string? Program);

    async Task<(PaneResult<MetricCounter[]>, string?)> MetricsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = MetricsSql;
        var status = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        long idleInTx;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (double.TryParse(Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) status[r.GetString(0)] = v;
            await r.NextResultAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            idleInTx = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
        }
        double? V(string name) => status.TryGetValue(name, out var v) ? v : null;
        var requests = V("Innodb_buffer_pool_read_requests");
        var misses = V("Innodb_buffer_pool_reads");
        var list = new List<MetricCounter>
        {
            new("queries", V("Questions"), true, "perSec"),
            // Folded into cacheHit by the sidecar: hits = logical read requests that did not go to disk.
            new("blksHit", requests is { } q && misses is { } m ? q - m : null, true, "count"),
            new("blksRead", misses, true, "count"),
            new("activeConnections", V("Threads_running"), false, "count"),
            new("userConnections", V("Threads_connected"), false, "count"),
            new("idleInTx", idleInTx, false, "count"),
            new("deadlocks", V("Innodb_deadlocks"), true, "count", V("Innodb_deadlocks") == null ? "MySQL has no deadlock counter in its status variables." : null),
            new("cpu", null, false, "percent", "MySQL / MariaDB do not report CPU from SQL."),
        };
        // Counters restart with the server: Uptime is the reset marker.
        var uptime = V("Uptime");
        var marker = uptime is { } u ? DateTimeOffset.UtcNow.AddSeconds(-u).ToUnixTimeSeconds() / 60 : (long?)null;
        return (PaneResult<MetricCounter[]>.Ok(list.ToArray()), marker?.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct)
    {
        if (!Ps) return PaneResult<LockRow[]>.Degraded(caps.Panes.First(p => p.Pane == MonitorPane.Locks).Reason ?? "not available");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.PROCESSLIST_ID, t.THREAD_ID, l.LOCK_TYPE, l.OBJECT_SCHEMA, CONCAT_WS('.', l.OBJECT_NAME, l.INDEX_NAME), l.LOCK_MODE, l.LOCK_STATUS, COUNT(*)
            FROM performance_schema.data_locks l JOIN performance_schema.threads t ON t.THREAD_ID = l.THREAD_ID
            WHERE t.PROCESSLIST_ID IS NOT NULL
            GROUP BY t.PROCESSLIST_ID, t.THREAD_ID, l.LOCK_TYPE, l.OBJECT_SCHEMA, l.OBJECT_NAME, l.INDEX_NAME, l.LOCK_MODE, l.LOCK_STATUS
            UNION ALL
            SELECT t.PROCESSLIST_ID, t.THREAD_ID, CONCAT('METADATA ', m.OBJECT_TYPE), m.OBJECT_SCHEMA, m.OBJECT_NAME, m.LOCK_TYPE, m.LOCK_STATUS, COUNT(*)
            FROM performance_schema.metadata_locks m JOIN performance_schema.threads t ON t.THREAD_ID = m.OWNER_THREAD_ID
            WHERE t.PROCESSLIST_ID IS NOT NULL AND m.OBJECT_TYPE <> 'USER LEVEL LOCK' AND NOT (m.OBJECT_SCHEMA <=> 'performance_schema')
            GROUP BY t.PROCESSLIST_ID, t.THREAD_ID, m.OBJECT_TYPE, m.OBJECT_SCHEMA, m.OBJECT_NAME, m.LOCK_TYPE, m.LOCK_STATUS
            """;
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<LockRow>();
            var total = 0;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                total++;
                if (list.Count >= max) continue;
                list.Add(new LockRow
                {
                    Key = new SessionKey { Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Instance = Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)! },
                    ResourceType = r.GetString(2), Database = r.IsDBNull(3) ? null : r.GetString(3), Object = r.IsDBNull(4) ? null : r.GetString(4),
                    Mode = r.IsDBNull(5) ? "?" : r.GetString(5), Status = r.GetString(6) is "GRANTED" ? "granted" : "waiting", Count = Convert.ToInt64(r.GetValue(7), CultureInfo.InvariantCulture),
                });
            }
            return total > list.Count ? PaneResult<LockRow[]>.Truncated(list.ToArray(), total) : PaneResult<LockRow[]>.Ok(list.ToArray());
        }
        catch (MySqlException ex) { return PaneResult<LockRow[]>.Error(ex.Message); }
    }

    public Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection,
        IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(string Database, long ObjectId), string?>>(new Dictionary<(string, long), string?>());

    public async Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct)
    {
        if (!Ps) return PaneResult<TopQueryRead>.Degraded(caps.Panes.First(p => p.Pane == MonitorPane.TopQueries).Reason ?? "not available");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT SCHEMA_NAME, DIGEST, COUNT_STAR, SUM_TIMER_WAIT / 1000000000, SUM_ROWS_SENT, SUM_ROWS_EXAMINED
            FROM performance_schema.events_statements_summary_by_digest WHERE DIGEST IS NOT NULL
            """;
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<TopQueryCounter>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = r.IsDBNull(0) ? null : r.GetString(0);
                var digest = r.GetString(1);
                var key = (schema ?? "") + "\u0001" + digest;
                list.Add(new TopQueryCounter(TopQueryKey.Of(key), digest, schema, key, Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture), null,
                    Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture), null,
                    Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture)));
            }
            // A full read every time: an entry that is missing was evicted (or the table was truncated).
            return PaneResult<TopQueryRead>.Ok(new TopQueryRead(list.ToArray(), true, null), "Logical reads are rows examined.");
        }
        catch (MySqlException ex) { return PaneResult<TopQueryRead>.Error(ex.Message); }
    }

    public async Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, TopQueryText>();
        foreach (var key in textKeys)
        {
            var cut = key.IndexOf('\u0001');
            if (cut < 0) continue;
            await using var cmd = connection.CreateCommand();
            // The sample of a real execution where the server kept one (MySQL 8.0.3+), else the normalized digest text.
            cmd.CommandText = "SELECT COALESCE(QUERY_SAMPLE_TEXT, DIGEST_TEXT), SCHEMA_NAME FROM performance_schema.events_statements_summary_by_digest WHERE SCHEMA_NAME <=> @s AND DIGEST = @d";
            cmd.Parameters.Add(new MySqlParameter("@s", cut == 0 ? DBNull.Value : key[..cut]));
            cmd.Parameters.Add(new MySqlParameter("@d", key[(cut + 1)..]));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false)) result[key] = new TopQueryText(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
        }
        return result;
    }

    public Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false) =>
        Task.FromResult(PaneResult<DeadlockEvent[]>.Degraded(caps.Panes.First(p => p.Pane == MonitorPane.Deadlocks).Reason ?? "not available"));

    public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) => Task.FromResult<string?>(null);

    static KillResult Refused(string why) => new() { Outcome = KillOutcome.Refused, Code = "unsupported", Message = why };

    /// <summary>
    /// The KILL is prepared on the server from the thread row that still matches the identity (and, for a cancel, the running
    /// statement): nothing from the request is spliced into it, and a session that ended or moved on gets "DO 0".
    /// </summary>
    static string GuardedKill(string kind, bool statement) => $"""
        SET @q_kill = (SELECT CONCAT('KILL {kind} ', t.PROCESSLIST_ID) FROM performance_schema.threads t
          {(statement ? "JOIN performance_schema.events_statements_current s ON s.THREAD_ID = t.THREAD_ID AND s.END_EVENT_ID IS NULL AND s.NESTING_EVENT_LEVEL = 0 AND s.EVENT_ID = @ev" : "")}
          WHERE t.PROCESSLIST_ID = @id AND t.THREAD_ID = @inst LIMIT 1);
        SET @q_run = IFNULL(@q_kill, 'DO 0');
        PREPARE q_kill_stmt FROM @q_run;
        EXECUTE q_kill_stmt;
        DEALLOCATE PREPARE q_kill_stmt;
        SELECT @q_kill IS NOT NULL;
        """;

    static void Bind(DbCommand cmd, SessionKey key, string? statementStart)
    {
        if (key.Id <= 0 || !ulong.TryParse(key.Instance, NumberStyles.None, CultureInfo.InvariantCulture, out var inst)) throw new ArgumentException("Invalid session identity.");
        cmd.Parameters.Add(new MySqlParameter("@id", key.Id));
        cmd.Parameters.Add(new MySqlParameter("@inst", inst));
        if (statementStart != null) cmd.Parameters.Add(new MySqlParameter("@ev", ulong.Parse(statementStart, NumberStyles.None, CultureInfo.InvariantCulture)));
    }

    public async Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct)
    {
        if (!caps.CanCancelRequest) return Refused(caps.CancelReason ?? "Cancelling is not available here.");
        if (statementStart == null || !new MySqlActivity().IsValidStatementStart(statementStart))
            return new KillResult { Outcome = KillOutcome.Refused, Code = "invalid", Message = "Cancelling needs the running statement shown in the dialog." };
        return await KillAsync(connection, key, statementStart, "QUERY", ct).ConfigureAwait(false);
    }

    public async Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        if (!caps.CanTerminate) return Refused(caps.TerminateReason ?? "Terminating is not available here.");
        return await KillAsync(connection, key, null, "CONNECTION", ct).ConfigureAwait(false);
    }

    async Task<KillResult> KillAsync(DbConnection connection, SessionKey key, string? statementStart, string kind, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = GuardedKill(kind, statementStart != null);
        try { Bind(cmd, key, statementStart); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return new KillResult { Outcome = KillOutcome.Refused, Code = "invalid", Message = "The session identity is not valid." };
        }
        try
        {
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1)
                return kind == "QUERY"
                    ? new KillResult { Outcome = KillOutcome.Done, Message = $"Cancel sent to connection {key.Id}; its session stays connected." }
                    : new KillResult { Outcome = KillOutcome.Done, Message = $"Connection {key.Id} was terminated; the server rolls back its open transaction." };
        }
        catch (MySqlException ex) when (ex.Number == 1095) // not owner of thread
        {
            return new KillResult { Outcome = KillOutcome.Refused, Code = "denied", Message = "This account may only cancel or terminate its own sessions (others need CONNECTION_ADMIN or SUPER)." };
        }
        catch (MySqlException ex) when (ex.Number == 1094) // unknown thread: it ended between the check and the KILL
        {
            return new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended; nothing was sent." };
        }
        return await WhyNotAsync(connection, key, ct).ConfigureAwait(false);
    }

    async Task<KillResult> WhyNotAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.THREAD_ID, (SELECT COUNT(*) FROM performance_schema.events_statements_current s WHERE s.THREAD_ID = t.THREAD_ID AND s.END_EVENT_ID IS NULL AND s.NESTING_EVENT_LEVEL = 0)
            FROM performance_schema.threads t WHERE t.PROCESSLIST_ID = @id
            """;
        cmd.Parameters.Add(new MySqlParameter("@id", key.Id));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false) || Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) != key.Instance)
            return new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended or its connection id now belongs to another session; nothing was sent." };
        return Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) > 0
            ? new KillResult { Outcome = KillOutcome.StatementFinished, Code = "statementFinished", Message = "That statement finished; the session is running another one. Nothing was sent." }
            : new KillResult { Outcome = KillOutcome.NoRunningQuery, Code = "noRunningQuery", Message = "The session is not running a statement; nothing was sent." };
    }

    public Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct) => Task.FromResult<RollbackStatus?>(null);

    /// <summary>What a kill would undo: the session's InnoDB transaction (rows it changed) and the sessions waiting on it.</summary>
    public async Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT p.ID, LEFT(p.INFO, 4096), TIMESTAMPDIFF(MICROSECOND, x.trx_started, NOW(6)) DIV 1000, x.trx_rows_modified, x.trx_id IS NOT NULL
            FROM information_schema.PROCESSLIST p LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = p.ID WHERE p.ID = @id;
            {(Ps ? "SELECT THREAD_ID FROM performance_schema.threads WHERE PROCESSLIST_ID = @id;" : "SELECT NULL;")}
            """;
        cmd.Parameters.Add(new MySqlParameter("@id", key.Id));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new KillPreview { Key = key, SessionEnded = true, Risk = RollbackRisk.None };
        var text = r.IsDBNull(1) ? null : r.GetString(1);
        long? txMs = r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
        long? modified = r.IsDBNull(3) ? null : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
        var inTx = Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture) == 1;
        await r.NextResultAsync(ct).ConfigureAwait(false);
        if (Ps && (!await r.ReadAsync(ct).ConfigureAwait(false) || Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) != key.Instance))
            return new KillPreview { Key = key, SessionEnded = true, Risk = RollbackRisk.None };
        var risk = !inTx ? RollbackRisk.None : modified is null ? RollbackRisk.Unknown : modified > 10_000 ? RollbackRisk.High : RollbackRisk.Low;
        return new KillPreview
        {
            Key = key, Risk = risk, TxElapsedMs = txMs, TxCount = inTx ? 1 : 0, HasWrites = inTx ? modified > 0 : false, Text = text,
            Reason = !inTx ? "No open InnoDB transaction." : $"An open transaction that changed {modified:N0} row(s); terminating rolls them back.",
        };
    }

    public async Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT p.INFO, TIMESTAMPDIFF(MICROSECOND, x.trx_started, NOW(6)) DIV 1000, x.trx_id IS NOT NULL, x.trx_isolation_level
            FROM information_schema.PROCESSLIST p LEFT JOIN information_schema.INNODB_TRX x ON x.trx_mysql_thread_id = p.ID WHERE p.ID = @id
            """;
        cmd.Parameters.Add(new MySqlParameter("@id", key.Id));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new RequestDetail { Key = key, TextNote = "The session ended." };
        var text = r.IsDBNull(0) ? null : r.GetString(0);
        var inTx = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) == 1;
        return new RequestDetail
        {
            Key = key, Text = text, TextNote = text == null ? "The session is not running a statement." : null,
            TxElapsedMs = r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), OpenTx = inTx ? 1 : 0,
            IsolationLevel = r.IsDBNull(3) ? null : r.GetString(3), PlanReason = caps.LivePlanReason,
        };
    }
}
