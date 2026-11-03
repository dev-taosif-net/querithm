using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// Activity monitor for SQL Server (Phase B). Session identity = (session_id, login_time as SqlDateTime ticks);
/// login_time is bound back as SqlDbType.DateTime so the comparison is exact to the server's 1/300 s (B0 row 1).
/// </summary>
public sealed class SqlServerActivity(IPlanProvider plans, IDataEditDialect quoting) : IActivityProvider
{
    public const int MinMajorVersion = 13; // SQL Server 2016

    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT session_id, login_time FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            // Query-tab connections turn errors below severity 17 into messages, so a denied read can also arrive as no row.
            if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.IsDBNull(1))
                throw new IdentityUnavailableException("The server did not return this connection's own row in sys.dm_exec_sessions.");
            return new SessionKey { Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Instance = FormatInstance(r.GetDateTime(1)) };
        }
        // 229/230 permission denied on the object or a column, 208/207 no such object or column, 300/297 server permission.
        // A failed connection (severity 20+) is none of these and fails the open as before (K9).
        catch (SqlException ex) when (ex.Number is 229 or 230 or 208 or 207 or 300 or 297)
        {
            throw new IdentityUnavailableException($"The server refused this connection's read of its own row in sys.dm_exec_sessions (error {ex.Number}).", ex);
        }
    }

    public string ConnectionToken(DbConnection connection) =>
        connection is SqlConnection sql ? sql.ClientConnectionId.ToString("N") : connection.GetHashCode().ToString(CultureInfo.InvariantCulture);

    public string MonitorSessionSetupSql => "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW;";

    // Query-tab connections turn errors below severity 17 into info messages (message streaming); the monitor needs them as
    // exceptions: permission 300/297, lock timeout 1222 and KILL 6102/6104/6106/6107 would otherwise read as success.
    /// <summary>The banner check runs on a pool connection, which has no command timeout of its own.</summary>
    internal const int BannerCommandTimeoutS = 5;

    public void PrepareMonitorConnection(DbConnection connection)
    {
        if (connection is SqlConnection sql) sql.FireInfoMessageEventOnUserErrors = false;
    }

    // 18456 login failed, 18488 password must be changed, 18487 password expired, 18486 account locked out, 18470 login
    // disabled, 18452 untrusted domain. Not 4060 (database unavailable: a restore or an availability-group failover, which a
    // retry outlasts).
    public bool CredentialsRejected(Exception ex) => ex is SqlException { Number: 18456 or 18488 or 18487 or 18486 or 18470 or 18452 };

    // ---- identity format: "<DayTicks>:<TimeTicks>" of SqlDateTime ----

    public static string FormatInstance(DateTime loginTime)
    {
        var s = new SqlDateTime(loginTime);
        return string.Create(CultureInfo.InvariantCulture, $"{s.DayTicks}:{s.TimeTicks}");
    }

    public static bool TryParseInstance(string? text, out SqlDateTime value)
    {
        value = SqlDateTime.Null;
        if (string.IsNullOrEmpty(text) || text.Length > 64) return false;
        var i = text.IndexOf(':');
        if (i <= 0 || i != text.LastIndexOf(':')) return false;
        if (!int.TryParse(text.AsSpan(0, i), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day)) return false;
        if (!int.TryParse(text.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var time)) return false;
        if (time >= 300 * 86_400) return false;
        try { value = new SqlDateTime(day, time); return true; }
        catch (SqlTypeException) { return false; }
    }

    public bool IsValidInstance(string instance) => TryParseInstance(instance, out _);
    public string WaitCategory(string waitType) => SqlServerWaits.Category(waitType);
    public bool IsBenignWait(string waitType) => SqlServerWaits.Benign.Contains(waitType);

    /// <summary>SQL Server cannot cancel a single request from another connection, so no statement start is ever accepted.</summary>
    public bool IsValidStatementStart(string statementStart) => false;

    internal static SqlParameter StartParam(SessionKey key) =>
        TryParseInstance(key.Instance, out var v) ? new SqlParameter("@start", SqlDbType.DateTime) { Value = v } : throw new ArgumentException("Invalid session identity.");

    /// <summary>KILL is not parameterisable: the spid is a validated integer, formatted invariantly (§16); the rest is parameters.</summary>
    internal static string SpidLiteral(long id) =>
        id is >= 1 and <= 32767 ? id.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(id), "A SQL Server session id is 1–32767.");

    // ---- probe ----

    /// <summary>Raw probe values; <see cref="Capabilities"/> turns them into panes and reasons (pure, unit-tested with fake values).</summary>
    public sealed record Probe(int EngineEdition, int MajorVersion, string ProductVersion, bool ViewServerState, bool ViewServerPerformanceState,
        bool AlterAnyConnection, bool ViewDatabaseState, bool KillDatabaseConnection, bool LivePlanDmv, bool LightweightProfiling, string? Database);

    const string ProbeSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int),
               CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'), 0),
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE'), 0),
               ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY CONNECTION'), 0),
               ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE'), 0),
               ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'KILL DATABASE CONNECTION'), 0),
               (SELECT COUNT(*) FROM sys.all_objects WHERE name = N'dm_exec_query_statistics_xml' AND schema_id = SCHEMA_ID(N'sys')),
               (SELECT COUNT(*) FROM sys.database_scoped_configurations WHERE name = N'LIGHTWEIGHT_QUERY_PROFILING' AND CONVERT(nvarchar(20), value) IN (N'1', N'ON')),
               DB_NAME();
        """;

    public async Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        Probe p;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = ProbeSql;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("The capability probe returned no row.");
            int I(int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
            p = new Probe(I(0), I(1), r.IsDBNull(2) ? "" : r.GetString(2), I(3) == 1, I(4) == 1, I(5) == 1, I(6) == 1, I(7) == 1, I(8) > 0, I(9) > 0,
                r.IsDBNull(10) ? null : r.GetString(10));
        }
        var self = await IdentityAsync(connection, ct).ConfigureAwait(false);
        return new SqlServerActivitySession(p, Capabilities(p), self, plans, quoting);
    }

    public static bool IsAzureSqlDatabase(Probe p) => p.EngineEdition == 5;

    /// <summary>
    /// Decides panes and actions from probed permissions and DMVs. EngineEdition 5 (Azure SQL Database) scopes DMVs to the
    /// connected database; the platform name is only used to word reasons.
    /// </summary>
    public static MonitorCapabilities Capabilities(Probe p)
    {
        var azure = IsAzureSqlDatabase(p);
        var state = azure ? p.ViewDatabaseState : p.ViewServerState || p.ViewServerPerformanceState;
        var need = azure ? "requires VIEW DATABASE STATE" : "requires VIEW SERVER STATE (2022+: VIEW SERVER PERFORMANCE STATE)";
        PaneCapability Pane(MonitorPane pane, bool ok, string? reason) => new() { Pane = pane, Available = ok, Reason = ok ? null : reason };
        var canKill = azure ? p.KillDatabaseConnection : p.AlterAnyConnection;
        return new MonitorCapabilities
        {
            ServerVersion = $"SQL Server {p.ProductVersion}",
            Platform = p.EngineEdition switch { 5 => "Azure SQL Database", 8 => "Azure SQL Managed Instance", _ => null },
            AllSessionsVisible = state,
            SessionsReason = state ? null : $"Only this login's own sessions are visible ({need}).",
            CanTerminate = canKill,
            TerminateReason = canKill ? null : azure ? "Terminating sessions requires KILL DATABASE CONNECTION on Azure SQL Database." : "Terminating sessions requires ALTER ANY CONNECTION.",
            CanCancelRequest = false,
            CancelReason = "SQL Server can only terminate the whole session; cancel your own queries from their tab.",
            LivePlans = p.LivePlanDmv && state,
            LivePlanReason = !state ? need : !p.LivePlanDmv ? "Live plans need sys.dm_exec_query_statistics_xml (SQL Server 2016 SP1+)." :
                p.LightweightProfiling ? null : "Lightweight query profiling is off; cached plans are shown.",
            Panes =
            [
                Pane(MonitorPane.Sessions, true, null),
                Pane(MonitorPane.Locks, state, need),
                Pane(MonitorPane.Waits, state && !azure, azure ? "Server-wide waits are not available on Azure SQL Database." : need),
                Pane(MonitorPane.Metrics, state, need),
                Pane(MonitorPane.Io, state, need),
                Pane(MonitorPane.TopQueries, state, need),
                Pane(MonitorPane.Deadlocks, state && !azure,
                    azure ? "Azure SQL Database does not expose the system_health session (sys.fn_xe_telemetry_blob_target_read_file is not available)." : need),
            ],
        };
    }

    public static string? StartRefusal(Probe p) =>
        !IsAzureSqlDatabase(p) && p.MajorVersion is > 0 and < MinMajorVersion ? "The activity monitor needs SQL Server 2016 or later." : null;

    // ---- own-tab blocking banner ----

    public async Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return ([], null);
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = BannerCommandTimeoutS; // a pool connection has none; a token cancel alone sends only an attention
        var values = new StringBuilder();
        for (var i = 0; i < ids.Length; i++)
        {
            if (i > 0) values.Append(',');
            values.Append(CultureInfo.InvariantCulture, $"(@b{i})");
            cmd.Parameters.Add(new SqlParameter($"@b{i}", SqlDbType.Int) { Value = checked((int)ids[i]) });
        }
        cmd.CommandText = $"""
            IF ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'), 0) = 0 AND ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE'), 0) = 0
                SELECT CAST(NULL AS int), 0, CAST(0 AS bigint), 0;
            ELSE
                SELECT r.blocking_session_id, COUNT(*), MAX(CAST(r.wait_time AS bigint)), 1
                FROM sys.dm_exec_requests r JOIN (VALUES {values}) v(id) ON v.id = r.blocking_session_id
                GROUP BY r.blocking_session_id;
            """;
        var rows = new List<BlockedBySummary>();
        // A pool connection reports user errors as messages (query tabs); this check needs them thrown (a silent error
        // would read as "blocking nobody" and clear the banner).
        var sql = connection as SqlConnection;
        var asMessages = sql?.FireInfoMessageEventOnUserErrors ?? false;
        if (sql != null) sql.FireInfoMessageEventOnUserErrors = false;
        try
        {
            await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rd.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rd.GetInt32(3) == 0) return (null, "Seeing other sessions requires VIEW SERVER STATE.");
                rows.Add(new BlockedBySummary(Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture), rd.GetInt32(1), rd.GetInt64(2)));
            }
        }
        finally { if (sql != null) sql.FireInfoMessageEventOnUserErrors = asMessages; }
        return (rows.ToArray(), null);
    }
}

/// <summary>Per-monitor-connection state: probe results and the top-queries cutoff.</summary>
sealed class SqlServerActivitySession(SqlServerActivity.Probe probe, MonitorCapabilities caps, SessionKey self, IPlanProvider plans, IDataEditDialect quoting) : IActivitySession
{
    int _topRuns;
    /// <summary>The deadlock poll's position in the system_health files (per monitor connection).</summary>
    readonly SqlServerDeadlocks.FileCursor _deadlockCursor = new();
    long _planCacheAtFullRead;
    DateTime? _cutoff;

    public MonitorCapabilities Capabilities => caps;
    public string? StartRefusal => SqlServerActivity.StartRefusal(probe);
    public SessionKey Self => self;
    public bool WaitsFromSessions => false;
    bool Full => caps.AllSessionsVisible;

    // ---- main tick: sessions, blockers, waits, metrics, I/O in one batch ----

    /// <summary>
    /// 1 while any transaction is being rolled back. sys.dm_tran_session_transactions then blocks (MSQL_XACT_MGR_MUTEX) until
    /// the rollback ends — a client cancel is not processed meanwhile — so every read of it is guarded by @rb = 0 (B0 row 9).
    /// </summary>
    const string RollbackRunningSql = "DECLARE @rb bit = CASE WHEN EXISTS (SELECT 1 FROM sys.dm_tran_active_transactions WHERE transaction_state = 7) THEN 1 ELSE 0 END;\n";

    internal const string TxAgesHidden = "Transaction ages are not shown while SQL Server rolls back a terminated session: it blocks reading which transaction belongs to which session until the rollback ends.";

    const string SessionsFull = RollbackRunningSql + """
        SELECT TOP (@max)
          s.session_id, s.login_time, s.login_name, s.host_name, s.program_name,
          DB_NAME(COALESCE(r.database_id, s.database_id)) AS database_name,
          COALESCE(r.status, s.status) AS status, r.command, s.is_user_process,
          COALESCE(w.wait_type, r.wait_type) AS wait_type,
          COALESCE(w.resource_description, r.wait_resource) AS wait_resource,
          COALESCE(w.wait_duration_ms, r.wait_time) AS wait_ms,
          CASE WHEN t.encrypted = 1 THEN NULL
               WHEN r.session_id IS NULL OR r.statement_start_offset IS NULL OR r.statement_start_offset / 2 >= LEN(t.text) THEN LEFT(t.text, 4096)
               ELSE LEFT(SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                    (CASE WHEN r.statement_end_offset IN (-1, 0) THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1), 4096)
          END AS text,
          CAST(ISNULL(t.encrypted, 0) AS int) AS encrypted,
          CONVERT(varchar(18), r.query_hash, 1) AS query_hash,
          DATEDIFF_BIG(millisecond, r.start_time, SYSDATETIME()) AS elapsed_ms,
          DATEDIFF_BIG(millisecond, tx.begin_time, SYSDATETIME()) AS tx_ms,
          DATEDIFF_BIG(millisecond, s.last_request_end_time, SYSDATETIME()) AS state_ms,
          CAST(COALESCE(r.cpu_time, s.cpu_time) AS bigint) AS cpu_ms,
          CAST(COALESCE(r.reads, s.reads) AS bigint) AS reads, CAST(COALESCE(r.writes, s.writes) AS bigint) AS writes,
          CAST(COALESCE(r.logical_reads, s.logical_reads) AS bigint) AS logical_reads, CAST(COALESCE(r.row_count, s.row_count) AS bigint) AS row_count,
          s.open_transaction_count, CAST(NULLIF(r.percent_complete, 0) AS float) AS percent_complete,
          CASE WHEN r.status = N'rollback' OR r.command = N'KILLED/ROLLBACK' THEN 1 ELSE 0 END AS rolling_back,
          CAST(CASE WHEN su.pages + ISNULL(tu.pages, 0) > 0 THEN (su.pages + ISNULL(tu.pages, 0)) * 8 END AS bigint) AS tempdb_kb,
          CAST(mg.granted_memory_kb AS bigint) AS granted_kb, CAST(mg.requested_memory_kb AS bigint) AS requested_kb, CAST(mg.dop AS int) AS dop,
          CAST(@rb AS int) AS rollback_running, COUNT(*) OVER () AS total
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
        OUTER APPLY (SELECT TOP (1) wt.wait_type, wt.wait_duration_ms, wt.resource_description FROM sys.dm_os_waiting_tasks wt
                     WHERE wt.session_id = s.session_id ORDER BY wt.wait_duration_ms DESC) w
        OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS begin_time FROM sys.dm_tran_session_transactions st
                     JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id WHERE @rb = 0 AND st.session_id = s.session_id) tx
        OUTER APPLY (SELECT SUM(u.user_objects_alloc_page_count + u.internal_objects_alloc_page_count - u.user_objects_dealloc_page_count - u.internal_objects_dealloc_page_count) AS pages
                     FROM sys.dm_db_session_space_usage u WHERE u.session_id = s.session_id) su
        OUTER APPLY (SELECT SUM(u.user_objects_alloc_page_count + u.internal_objects_alloc_page_count - u.user_objects_dealloc_page_count - u.internal_objects_dealloc_page_count) AS pages
                     FROM sys.dm_db_task_space_usage u WHERE u.session_id = s.session_id) tu
        OUTER APPLY (SELECT TOP (1) g.granted_memory_kb, g.requested_memory_kb, g.dop FROM sys.dm_exec_query_memory_grants g WHERE g.session_id = s.session_id) mg
        WHERE (@db IS NULL OR DB_NAME(COALESCE(r.database_id, s.database_id)) = @db)
        ORDER BY CASE WHEN ISNULL(r.blocking_session_id, 0) <> 0 OR w.wait_type LIKE N'LCK[_]M[_]%' THEN 0
                      WHEN EXISTS (SELECT 1 FROM sys.dm_os_waiting_tasks b WHERE b.blocking_session_id = s.session_id AND b.session_id <> s.session_id) THEN 1
                      WHEN r.session_id IS NOT NULL THEN 2 ELSE 3 END, s.session_id;
        SELECT DISTINCT wt.session_id, wt.blocking_session_id FROM sys.dm_os_waiting_tasks wt WHERE wt.blocking_session_id IS NOT NULL
        UNION
        SELECT r.session_id, r.blocking_session_id FROM sys.dm_exec_requests r WHERE r.blocking_session_id <> 0;
        """;

    // Without VIEW SERVER (PERFORMANCE) STATE the session DMVs show the login's own sessions and the rest of the DMVs fail,
    // dm_exec_sql_text and dm_exec_connections included, even for the login's own session (B0 row 4): no text then.
    internal const string OwnTextNote = "query text requires VIEW SERVER STATE";

    const string SessionsOwn = """
        SELECT TOP (@max)
          s.session_id, s.login_time, s.login_name, s.host_name, s.program_name,
          DB_NAME(COALESCE(r.database_id, s.database_id)) AS database_name,
          COALESCE(r.status, s.status) AS status, r.command, s.is_user_process,
          r.wait_type, r.wait_resource, r.wait_time AS wait_ms,
          CAST(NULL AS nvarchar(max)) AS text, 0 AS encrypted,
          CONVERT(varchar(18), r.query_hash, 1) AS query_hash,
          DATEDIFF_BIG(millisecond, r.start_time, SYSDATETIME()) AS elapsed_ms, CAST(NULL AS bigint) AS tx_ms,
          DATEDIFF_BIG(millisecond, s.last_request_end_time, SYSDATETIME()) AS state_ms,
          CAST(COALESCE(r.cpu_time, s.cpu_time) AS bigint) AS cpu_ms,
          CAST(COALESCE(r.reads, s.reads) AS bigint) AS reads, CAST(COALESCE(r.writes, s.writes) AS bigint) AS writes,
          CAST(COALESCE(r.logical_reads, s.logical_reads) AS bigint) AS logical_reads, CAST(COALESCE(r.row_count, s.row_count) AS bigint) AS row_count,
          s.open_transaction_count, CAST(NULLIF(r.percent_complete, 0) AS float) AS percent_complete,
          CASE WHEN r.status = N'rollback' THEN 1 ELSE 0 END AS rolling_back,
          CAST(NULL AS bigint) AS tempdb_kb, CAST(NULL AS bigint) AS granted_kb, CAST(NULL AS bigint) AS requested_kb, CAST(NULL AS int) AS dop,
          CAST(0 AS int) AS rollback_running, COUNT(*) OVER () AS total
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
        WHERE (@db IS NULL OR DB_NAME(COALESCE(r.database_id, s.database_id)) = @db)
        ORDER BY s.session_id;
        SELECT r.session_id, r.blocking_session_id FROM sys.dm_exec_requests r WHERE r.blocking_session_id <> 0;
        """;

    const string WaitsSql = """
        SELECT wait_type, CAST(waiting_tasks_count AS bigint), CAST(wait_time_ms AS bigint), CAST(signal_wait_time_ms AS bigint)
        FROM sys.dm_os_wait_stats WHERE wait_time_ms > 0 OR waiting_tasks_count > 0;
        """;

    const string MetricsSql = """
        SELECT RTRIM(counter_name), CAST(cntr_value AS bigint), cntr_type FROM sys.dm_os_performance_counters
        WHERE (RTRIM(object_name) LIKE N'%:SQL Statistics' AND counter_name IN (N'Batch Requests/sec', N'SQL Compilations/sec'))
           OR (RTRIM(object_name) LIKE N'%:General Statistics' AND counter_name = N'User Connections')
           OR (RTRIM(object_name) LIKE N'%:Buffer Manager' AND counter_name = N'Page life expectancy');
        SELECT TOP (1) record FROM sys.dm_os_ring_buffers
        WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%' ORDER BY [timestamp] DESC;
        SELECT CONVERT(varchar(33), sqlserver_start_time, 126) FROM sys.dm_os_sys_info;
        """;

    const string AzureCpuSql = "SELECT TOP (1) CAST(avg_cpu_percent AS float) FROM sys.dm_db_resource_stats ORDER BY end_time DESC;";

    const string IoSql = """
        SELECT DB_NAME(f.database_id), COALESCE(mf.name, CONCAT(N'file ', f.file_id)), CAST(f.num_of_bytes_read AS bigint), CAST(f.num_of_bytes_written AS bigint),
               CAST(f.io_stall_read_ms AS bigint), CAST(f.io_stall_write_ms AS bigint)
        FROM sys.dm_io_virtual_file_stats(NULL, NULL) f
        LEFT JOIN sys.master_files mf ON mf.database_id = f.database_id AND mf.file_id = f.file_id;
        """;

    public async Task<TickResult> TickAsync(DbConnection connection, TickRequest req, CancellationToken ct)
    {
        var waitsOk = req.Waits && Available(MonitorPane.Waits);
        var metricsOk = req.Metrics && Available(MonitorPane.Metrics);
        var ioOk = req.Io && Available(MonitorPane.Io);
        try
        {
            return await BatchAsync(connection, req, req.Sessions, waitsOk, metricsOk, ioOk, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && connection.State == ConnectionState.Open)
        {
            // One pane failed (permission revoked mid-run, a DMV timing out): run each alone so the others still answer.
            PaneResult<ActivityRow[]>? sessions = null; IReadOnlyDictionary<string, string?>? texts = null;
            PaneResult<WaitCounter[]>? waits = null; PaneResult<MetricCounter[]>? metrics = null; PaneResult<IoCounter[]>? io = null; string? marker = null;
            async Task One(bool want, Func<TickResult, bool> take, bool s, bool w, bool m, bool i, MonitorPane pane)
            {
                if (!want) return;
                try { take(await BatchAsync(connection, req, s, w, m, i, ct).ConfigureAwait(false)); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    var msg = Describe(e);
                    switch (pane)
                    {
                        case MonitorPane.Sessions: sessions = PaneResult<ActivityRow[]>.Error(msg); break;
                        case MonitorPane.Waits: waits = PaneResult<WaitCounter[]>.Error(msg); break;
                        case MonitorPane.Metrics: metrics = PaneResult<MetricCounter[]>.Error(msg); break;
                        default: io = PaneResult<IoCounter[]>.Error(msg); break;
                    }
                }
            }
            await One(req.Sessions, t => { sessions = t.Sessions; texts = t.SessionTexts; return true; }, true, false, false, false, MonitorPane.Sessions).ConfigureAwait(false);
            await One(waitsOk, t => { waits = t.Waits; return true; }, false, true, false, false, MonitorPane.Waits).ConfigureAwait(false);
            await One(metricsOk, t => { metrics = t.Metrics; marker = t.ResetMarker; return true; }, false, false, true, false, MonitorPane.Metrics).ConfigureAwait(false);
            await One(ioOk, t => { io = t.Io; return true; }, false, false, false, true, MonitorPane.Io).ConfigureAwait(false);
            return new TickResult(sessions, texts, Fill(waits, req.Waits, MonitorPane.Waits), Fill(metrics, req.Metrics, MonitorPane.Metrics), Fill(io, req.Io, MonitorPane.Io), marker);
        }
    }

    PaneResult<T>? Fill<T>(PaneResult<T>? r, bool wanted, MonitorPane pane) =>
        r ?? (wanted && !Available(pane) ? PaneResult<T>.Degraded(Reason(pane)) : null);

    bool Available(MonitorPane pane) => caps.Panes.FirstOrDefault(p => p.Pane == pane)?.Available == true;
    string Reason(MonitorPane pane) => caps.Panes.FirstOrDefault(p => p.Pane == pane)?.Reason ?? "not available";

    static string Describe(Exception e) => e is SqlException sql ? sql.Number switch
    {
        300 or 297 => "Permission denied (VIEW SERVER STATE was revoked?).",
        1222 => "The server's lock timeout was reached.",
        -2 => "The query timed out (the server is busy).",
        _ => sql.Message,
    } : e.Message;

    async Task<TickResult> BatchAsync(DbConnection connection, TickRequest req, bool sessions, bool waits, bool metrics, bool io, CancellationToken ct)
    {
        var azure = SqlServerActivity.IsAzureSqlDatabase(probe);
        var sql = new StringBuilder();
        if (sessions) sql.Append(Full ? SessionsFull : SessionsOwn);
        if (waits) sql.Append(WaitsSql);
        if (metrics) sql.Append(azure ? AzureCpuSql : MetricsSql);
        if (io) sql.Append(IoSql);
        if (sql.Length == 0) return new TickResult(null, null, null, null, null, null);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        // Bounded by the scheduler's per-job timeout; the connection's command timeout is the driver backstop.
        if (sessions)
        {
            cmd.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = req.MaxSessions + 1 });
            cmd.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 128) { Value = (object?)req.DatabaseFilter ?? DBNull.Value });
        }
        PaneResult<ActivityRow[]>? sessionsResult = null;
        Dictionary<string, string?>? texts = null;
        PaneResult<WaitCounter[]>? waitsResult = null;
        PaneResult<MetricCounter[]>? metricsResult = null;
        PaneResult<IoCounter[]>? ioResult = null;
        string? marker = null;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (sessions)
        {
            (sessionsResult, texts) = await ReadSessionsAsync(r, req.MaxSessions, ct).ConfigureAwait(false);
            if (!Full && sessionsResult.Value is { } own)
                sessionsResult = sessionsResult with { Value = own.Select(x => x with { TextNote = OwnTextNote }).ToArray() };
            await r.NextResultAsync(ct).ConfigureAwait(false);
            var blockers = new Dictionary<long, List<long>>();
            var notes = new Dictionary<long, string>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var sid = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var b = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                if (b == sid || b == 0) continue; // self-blocking (parallel exchange) is not blocking
                if (b < 0) { notes[sid] = BlockerNote(b); continue; }
                if (!blockers.TryGetValue(sid, out var list)) blockers[sid] = list = [];
                if (!list.Contains(b)) list.Add(b);
            }
            if (sessionsResult.Value is { } rows)
                sessionsResult = sessionsResult with
                {
                    Value = rows.Select(row => row with
                    {
                        BlockedBy = blockers.TryGetValue(row.Key.Id, out var bl) ? [.. bl.Order()] : [],
                        BlockedByNote = notes.GetValueOrDefault(row.Key.Id),
                    }).ToArray(),
                };
            if (waits || metrics || io) await r.NextResultAsync(ct).ConfigureAwait(false);
        }
        if (waits)
        {
            var list = new List<WaitCounter>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var type = r.GetString(0);
                if (SqlServerWaits.Benign.Contains(type)) continue;
                list.Add(new WaitCounter(type, SqlServerWaits.Category(type), r.GetInt64(2), r.GetInt64(1), r.GetInt64(3)));
            }
            // dm_os_wait_stats counts a wait when it completes; a wait still in progress is in the sessions pane (B0).
            waitsResult = PaneResult<WaitCounter[]>.Ok(list.ToArray(), "Waits completed since the previous tick; waits still in progress are shown in the sessions pane.");
            if (metrics || io) await r.NextResultAsync(ct).ConfigureAwait(false);
        }
        if (metrics)
        {
            (metricsResult, marker) = azure ? await ReadAzureCpuAsync(r, ct).ConfigureAwait(false) : await ReadMetricsAsync(r, ct).ConfigureAwait(false);
            if (io) await r.NextResultAsync(ct).ConfigureAwait(false);
        }
        if (io)
        {
            var list = new List<IoCounter>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new IoCounter("file", r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), null, null, null,
                    r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
            ioResult = PaneResult<IoCounter[]>.Ok(list.ToArray());
        }
        return new TickResult(sessionsResult, texts, waitsResult ?? Fill<WaitCounter[]>(null, req.Waits, MonitorPane.Waits),
            metricsResult ?? Fill<MetricCounter[]>(null, req.Metrics, MonitorPane.Metrics), ioResult ?? Fill<IoCounter[]>(null, req.Io, MonitorPane.Io), marker);
    }

    internal static string BlockerNote(long id) => id switch
    {
        -2 => "orphanedDtc",
        -3 => "deferredRecovery",
        -4 => "latchOwnerUnknown",
        _ => "unknown",
    };

    internal static async Task<(PaneResult<ActivityRow[]>, Dictionary<string, string?>)> ReadSessionsAsync(DbDataReader r, int max, CancellationToken ct)
    {
        var rows = new List<ActivityRow>();
        var texts = new Dictionary<string, string?>();
        var seen = new HashSet<long>();
        int total = 0, dups = 0;
        var txHidden = false;
        int O(string name) => r.GetOrdinal(name);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            total = Convert.ToInt32(r.GetValue(O("total")), CultureInfo.InvariantCulture);
            var id = Convert.ToInt64(r.GetValue(O("session_id")), CultureInfo.InvariantCulture);
            // A MARS session has one dm_exec_requests row per active request: the first (highest priority) row is its row (S8).
            if (!seen.Add(id)) { dups++; continue; }
            txHidden |= Convert.ToInt32(r.GetValue(O("rollback_running")), CultureInfo.InvariantCulture) == 1;
            if (rows.Count >= max) continue;
            long? L(string n) { var i = O(n); return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
            string? S(string n) { var i = O(n); return r.IsDBNull(i) ? null : r.GetString(i); }
            var key = new SessionKey { Id = id, Instance = SqlServerActivity.FormatInstance(r.GetDateTime(O("login_time"))) };
            var text = S("text");
            var encrypted = Convert.ToInt32(r.GetValue(O("encrypted")), CultureInfo.InvariantCulture) == 1;
            var textKey = $"s{id}";
            if (text != null) texts[textKey] = text;
            var status = S("status") ?? "unknown";
            var isRequest = S("command") != null;
            rows.Add(new ActivityRow
            {
                Key = key,
                Login = S("login_name"), Host = NullIfEmpty(S("host_name")), Program = NullIfEmpty(S("program_name")), Database = S("database_name"),
                Status = status, Command = S("command"),
                IsUserSession = Convert.ToBoolean(r.GetValue(O("is_user_process")), CultureInfo.InvariantCulture),
                WaitType = S("wait_type"), WaitResource = NullIfEmpty(S("wait_resource")), WaitMs = L("wait_ms"),
                BlockedBy = [],
                TextId = text != null ? textKey : null,
                TextNote = encrypted ? "text unavailable (encrypted)" : null,
                QueryHash = S("query_hash") is { } h && h != "0x0000000000000000" ? h : null,
                ElapsedMs = isRequest ? L("elapsed_ms") : null,
                TxElapsedMs = L("tx_ms"),
                StateChangeMs = isRequest ? null : L("state_ms"),
                CpuMs = L("cpu_ms"), Reads = L("reads"), Writes = L("writes"), LogicalReads = L("logical_reads"), RowCount = L("row_count"),
                OpenTx = (int?)L("open_transaction_count"),
                PercentComplete = r.IsDBNull(O("percent_complete")) ? null : r.GetDouble(O("percent_complete")),
                RollingBack = Convert.ToInt32(r.GetValue(O("rolling_back")), CultureInfo.InvariantCulture) == 1,
                TempdbKb = L("tempdb_kb"), GrantedMemoryKb = L("granted_kb"), RequestedMemoryKb = L("requested_kb"), Dop = (int?)L("dop"),
            });
        }
        total -= dups;
        var note = txHidden ? TxAgesHidden : null;
        return (total > max ? PaneResult<ActivityRow[]>.Truncated(rows.ToArray(), total, note) : PaneResult<ActivityRow[]>.Ok(rows.ToArray(), note), texts);
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static async Task<(PaneResult<MetricCounter[]>, string?)> ReadMetricsAsync(DbDataReader r, CancellationToken ct)
    {
        var list = new List<MetricCounter>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            var value = r.GetInt64(1);
            // 272696576 = cumulative per-second counter (delta over elapsed time); 65792 = point value (B0 row 5).
            var cumulative = Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture) == 272696576;
            var key = name switch
            {
                "Batch Requests/sec" => "batchRequests",
                "SQL Compilations/sec" => "compilations",
                "User Connections" => "userConnections",
                "Page life expectancy" => "ple",
                _ => null,
            };
            if (key != null) list.Add(new MetricCounter(key, value, cumulative, cumulative ? "perSec" : key == "ple" ? "seconds" : "count"));
        }
        await r.NextResultAsync(ct).ConfigureAwait(false);
        string? record = null;
        if (await r.ReadAsync(ct).ConfigureAwait(false)) record = r.IsDBNull(0) ? null : r.GetString(0);
        list.Add(CpuFromRingBuffer(record));
        await r.NextResultAsync(ct).ConfigureAwait(false);
        string? marker = null;
        if (await r.ReadAsync(ct).ConfigureAwait(false)) marker = r.IsDBNull(0) ? null : r.GetString(0);
        return (PaneResult<MetricCounter[]>.Ok(list.ToArray()), marker);
    }

    static async Task<(PaneResult<MetricCounter[]>, string?)> ReadAzureCpuAsync(DbDataReader r, CancellationToken ct)
    {
        double? cpu = null;
        if (await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) cpu = r.GetDouble(0);
        return (PaneResult<MetricCounter[]>.Ok([new MetricCounter("cpu", cpu, false, "percent", cpu == null ? "n/a (no resource stats yet)" : "database CPU, 15 s")]), null);
    }

    /// <summary>
    /// SQL Server process CPU % from the undocumented scheduler-monitor ring buffer, parsed defensively. Only
    /// ProcessUtilization is used: SystemIdle is 0 on Linux (B0 row 5), so "other processes" is never derived.
    /// </summary>
    internal static MetricCounter CpuFromRingBuffer(string? record)
    {
        if (string.IsNullOrEmpty(record)) return new MetricCounter("cpu", null, false, "percent", "n/a (no scheduler sample yet)");
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65_536 };
            using var x = System.Xml.XmlReader.Create(new StringReader(record), settings);
            while (x.Read())
                if (x.NodeType == System.Xml.XmlNodeType.Element && x.LocalName == "ProcessUtilization"
                    && int.TryParse(x.ReadElementContentAsString(), NumberStyles.None, CultureInfo.InvariantCulture, out var pct) && pct is >= 0 and <= 100)
                    return new MetricCounter("cpu", pct, false, "percent", "SQL Server process");
        }
        catch (System.Xml.XmlException) { /* unexpected shape */ }
        return new MetricCounter("cpu", null, false, "percent", "n/a (ring buffer format)");
    }

    // ---- locks ----

    public async Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct)
    {
        if (!Available(MonitorPane.Locks)) return PaneResult<LockRow[]>.Degraded(Reason(MonitorPane.Locks));
        // No OBJECT_NAME(): it takes metadata locks and can hang behind a schema-modification lock (plan B2). Names come
        // from the metadata cache or the lazy resolver under LOCK_TIMEOUT.
        const string sql = """
            SELECT TOP (@max) l.request_session_id, s.login_time, l.resource_type, DB_NAME(l.resource_database_id) AS db,
                   CAST(CASE WHEN l.resource_type = N'OBJECT' THEN l.resource_associated_entity_id END AS bigint) AS object_id,
                   CAST(CASE WHEN l.resource_type IN (N'KEY', N'PAGE', N'RID', N'HOBT') THEN l.resource_associated_entity_id END AS bigint) AS hobt_id,
                   l.request_mode, l.request_status, COUNT_BIG(*) AS n, COUNT(*) OVER () AS total
            FROM sys.dm_tran_locks l
            LEFT JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
            WHERE l.resource_type <> N'DATABASE' OR l.request_status <> N'GRANT'
            GROUP BY l.request_session_id, s.login_time, l.resource_type, l.resource_database_id,
                     CASE WHEN l.resource_type = N'OBJECT' THEN l.resource_associated_entity_id END,
                     CASE WHEN l.resource_type IN (N'KEY', N'PAGE', N'RID', N'HOBT') THEN l.resource_associated_entity_id END,
                     l.request_mode, l.request_status
            ORDER BY CASE WHEN l.request_status = N'GRANT' THEN 1 ELSE 0 END, l.request_session_id;
            """;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
        var rows = new List<LockRow>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            total = r.GetInt32(9);
            var db = r.IsDBNull(3) ? null : r.GetString(3);
            string? obj = null;
            if (!r.IsDBNull(4)) obj = $"{db}.{r.GetInt64(4).ToString(CultureInfo.InvariantCulture)}";
            else if (!r.IsDBNull(5)) obj = $"{db}.hobt:{r.GetInt64(5).ToString(CultureInfo.InvariantCulture)}";
            var spid = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            rows.Add(new LockRow
            {
                Key = new SessionKey { Id = spid, Instance = r.IsDBNull(1) ? "" : SqlServerActivity.FormatInstance(r.GetDateTime(1)) },
                ResourceType = r.GetString(2), Database = db, Object = obj,
                Mode = r.GetString(6), Status = r.GetString(7) == "GRANT" ? "granted" : "waiting", Count = r.GetInt64(8),
            });
        }
        return total > rows.Count ? PaneResult<LockRow[]>.Truncated(rows.ToArray(), total) : PaneResult<LockRow[]>.Ok(rows.ToArray());
    }

    public async Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection,
        IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct)
    {
        // Runs under the monitor's LOCK_TIMEOUT (2 s): a name behind a schema lock stays an id ("name locked").
        var result = new Dictionary<(string, long), string?>();
        foreach (var group in ids.GroupBy(i => i.Database).Take(20))
        {
            await using var cmd = connection.CreateCommand();
            var db = quoting.Quote(group.Key); // a server-returned name: quoted by the engine's quoting service (S13)
            var vals = new StringBuilder();
            var n = 0;
            foreach (var (_, oid) in group.Take(200))
            {
                if (n > 0) vals.Append(',');
                vals.Append(CultureInfo.InvariantCulture, $"(@o{n})");
                cmd.Parameters.Add(new SqlParameter($"@o{n}", SqlDbType.BigInt) { Value = oid });
                n++;
            }
            cmd.CommandText = $"""
                SELECT v.id, s.name + N'.' + o.name FROM (VALUES {vals}) v(id)
                LEFT JOIN {db}.sys.partitions p ON p.hobt_id = v.id
                JOIN {db}.sys.objects o ON o.object_id = COALESCE(p.object_id, v.id)
                JOIN {db}.sys.schemas s ON s.schema_id = o.schema_id;
                """;
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) result[(group.Key, r.GetInt64(0))] = r.GetString(1);
            }
            catch (SqlException ex) when (ex.Number is 1222 or 916 or 4060 or 229)
            {
                foreach (var (_, oid) in group) result.TryAdd((group.Key, oid), null);
            }
        }
        return result;
    }

    // ---- top queries ----

    public async Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct)
    {
        if (!Available(MonitorPane.TopQueries)) return PaneResult<TopQueryRead>.Degraded(Reason(MonitorPane.TopQueries));
        // Full on start and every 30th run (catches evictions); otherwise entries with last_execution_time >= the previous
        // cutoff, where cutoff = MIN(now, earliest start of anything still running), all in server time (plan B4).
        var full = _cutoff == null || _topRuns % 30 == 0;
        _topRuns++;
        await using var cmd = connection.CreateCommand();
        // The entry key is a digest of plan handle + statement offsets, computed here (no hex strings per cached statement;
        // HASHBYTES on the server made the full read 1.7× slower — EntryKey). Query hash and text key come only for
        // entries that ran since the cutoff, the only ones whose counters can have changed. A full read keeps the
        // cutoff: its entries that ran since then still yield deltas.
        cmd.CommandText = """
            SELECT CASE WHEN MIN(r.start_time) < GETDATE() THEN MIN(r.start_time) ELSE GETDATE() END FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE r.session_id <> @@SPID AND s.is_user_process = 1;
            SELECT qs.plan_handle,
                   CASE WHEN qs.last_execution_time >= @cutoff THEN 1 ELSE 0 END, qs.statement_start_offset, qs.statement_end_offset,
                   CASE WHEN qs.last_execution_time >= @cutoff THEN CONVERT(varchar(130), qs.sql_handle, 1) END,
                   CASE WHEN qs.last_execution_time >= @cutoff THEN CONVERT(varchar(18), qs.query_hash, 1) END,
                   CAST(qs.execution_count AS bigint), CAST(qs.total_worker_time AS bigint),
                   CAST(qs.total_elapsed_time AS bigint), CAST(qs.total_logical_reads AS bigint), CAST(qs.total_rows AS bigint)
            FROM sys.dm_exec_query_stats qs
            WHERE @full = 1 OR qs.last_execution_time >= @cutoff;
            SELECT CAST(SUM(entries_count) AS bigint) FROM sys.dm_os_memory_cache_counters WHERE type IN (N'CACHESTORE_SQLCP', N'CACHESTORE_OBJCP');
            """;
        cmd.Parameters.Add(new SqlParameter("@cutoff", SqlDbType.DateTime) { Value = _cutoff is { } c ? c : DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@full", SqlDbType.Bit) { Value = full });
        DateTime? next = null;
        long? cache = null;
        var list = new List<TopQueryCounter>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) next = r.GetDateTime(0);
            await r.NextResultAsync(ct).ConfigureAwait(false);
            var handle = new byte[64];
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var length = (int)r.GetBytes(0, 0, handle, 0, handle.Length);
                var ran = r.GetInt32(1) == 1;
                int s = r.GetInt32(2), e = r.GetInt32(3);
                list.Add(new TopQueryCounter(EntryKey(handle.AsSpan(0, length), s, e), ran ? (r.IsDBNull(5) ? "0x" : r.GetString(5)) : "", null,
                    ran ? $"{r.GetString(4)}:{s}:{e}" : "",
                    r.GetInt64(6), r.GetInt64(7) / 1000.0, r.GetInt64(8) / 1000.0, r.GetInt64(9), null, r.IsDBNull(10) ? null : r.GetInt64(10)));
            }
            if (await r.NextResultAsync(ct).ConfigureAwait(false) && await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) cache = r.GetInt64(0);
        }
        _cutoff = next;
        if (full) _planCacheAtFullRead = cache ?? 0;
        else if (cache is { } now && _planCacheAtFullRead >= 10 && now < _planCacheAtFullRead / 10)
        {
            // The plan cache was emptied (an administrator's flush, memory pressure): read it all now so
            // the tracker sees the evictions and reports the reset (T1), instead of at the next 30th run.
            _topRuns = 0;
            return await TopQueriesAsync(connection, ct).ConfigureAwait(false);
        }
        return PaneResult<TopQueryRead>.Ok(new TopQueryRead(list.ToArray(), full, null));
    }

    public async Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, TopQueryText>();
        if (textKeys.Count == 0) return result;
        await using var cmd = connection.CreateCommand();
        var vals = new StringBuilder();
        var n = 0;
        var keys = new List<string>();
        foreach (var k in textKeys.Take(100))
        {
            var parts = k.Split(':');
            if (parts.Length != 3 || !TryHex(parts[0], out var handle)
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s)
                || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var e)) continue;
            if (n > 0) vals.Append(',');
            vals.Append(CultureInfo.InvariantCulture, $"(@k{n}, @h{n}, @s{n}, @e{n})");
            cmd.Parameters.Add(new SqlParameter($"@k{n}", SqlDbType.Int) { Value = n });
            cmd.Parameters.Add(new SqlParameter($"@h{n}", SqlDbType.VarBinary, 64) { Value = handle });
            cmd.Parameters.Add(new SqlParameter($"@s{n}", SqlDbType.Int) { Value = s });
            cmd.Parameters.Add(new SqlParameter($"@e{n}", SqlDbType.Int) { Value = e });
            keys.Add(k);
            n++;
        }
        if (n == 0) return result;
        cmd.CommandText = $"""
            SELECT v.k, CASE WHEN t.encrypted = 1 THEN NULL
                   WHEN v.s / 2 >= LEN(t.text) THEN LEFT(t.text, 4096)
                   ELSE LEFT(SUBSTRING(t.text, v.s / 2 + 1, (CASE WHEN v.e IN (-1, 0) THEN DATALENGTH(t.text) ELSE v.e END - v.s) / 2 + 1), 4096) END,
                   DB_NAME(t.dbid)
            FROM (VALUES {vals}) v(k, h, s, e) CROSS APPLY sys.dm_exec_sql_text(v.h) t;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result[keys[r.GetInt32(0)]] = new TopQueryText(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
        }
        return result;
    }

    static bool TryHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length > 130 || text.Length % 2 != 0) return false;
        try { bytes = Convert.FromHexString(text.AsSpan(2)); return true; }
        catch (FormatException) { return false; }
    }

    // ---- deadlocks ----

    public Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false) =>
        Available(MonitorPane.Deadlocks)
            ? SqlServerDeadlocks.ReadAsync(connection, fromMs, toMs, max, ct, recentOnly ? _deadlockCursor : null)
            : Task.FromResult(PaneResult<DeadlockEvent[]>.Degraded(Reason(MonitorPane.Deadlocks)));

    public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) =>
        Available(MonitorPane.Deadlocks) ? SqlServerDeadlocks.XmlAsync(connection, eventId, fromMs, toMs, ct) : Task.FromResult<string?>(null);

    // ---- kill ----

    public Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct) =>
        Task.FromResult(new KillResult { Outcome = KillOutcome.Refused, Code = "unsupported", Message = caps.CancelReason! });

    public async Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = TerminateSql(key.Id);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id });
        cmd.Parameters.Add(SqlServerActivity.StartParam(key));
        try
        {
            var gone = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return gone is string
                ? new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended or its ID was reused; nothing was sent." }
                : new KillResult { Outcome = KillOutcome.Done, Message = $"Session {key.Id} was terminated; the server rolls back its open work." };
        }
        catch (SqlException ex)
        {
            return new KillResult
            {
                Outcome = ex.Number == 6106 ? KillOutcome.Gone : KillOutcome.Failed,
                Code = ex.Number switch { 6102 => "permission", 6106 => "gone", 6107 => "systemSession", 6104 => "ownMonitor", _ => "serverError" },
                Message = ex.Number switch
                {
                    6102 => caps.TerminateReason ?? "The login is not allowed to use KILL.",
                    6106 => "The session already ended.",
                    6107 => "Only user sessions can be terminated.",
                    6104 => "The monitor cannot terminate its own session.",
                    _ => ex.Message,
                },
            };
        }
    }

    /// <summary>The guarded KILL batch (plan B1c); the spid literal is a validated integer, everything else a parameter.</summary>
    /// <summary>
    /// A plan-cache statement's identity: SHA-256 of plan handle (≤ 64 bytes) and statement offsets, cut to 128 bits. In
    /// the sidecar because HASHBYTES in the query cost the server more than the hashing costs here (full read over 28,144
    /// entries: 366 ms with it, 159 ms with the raw handle, 210 ms for the hex strings before).
    /// </summary>
    internal static UInt128 EntryKey(ReadOnlySpan<byte> planHandle, int start, int end)
    {
        Span<byte> key = stackalloc byte[planHandle.Length + 8];
        planHandle.CopyTo(key);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key[planHandle.Length..], start);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key[(planHandle.Length + 4)..], end);
        return TopQueryKey.Of(key);
    }

    internal static string TerminateSql(long id) =>
        $"IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id = @id AND login_time = @start AND is_user_process = 1) KILL {SqlServerActivity.SpidLiteral(id)}; ELSE SELECT 'gone';";

    internal static string StatusOnlySql(long id) =>
        $"IF EXISTS (SELECT 1 FROM sys.dm_exec_sessions WHERE session_id = @id AND login_time = @start) KILL {SqlServerActivity.SpidLiteral(id)} WITH STATUSONLY; ELSE SELECT 'gone';";

    public async Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        // A killed running request has a row with status 'rollback' and percent_complete; a killed sleeping session has
        // none, and STATUSONLY's info message 6109 reports progress (B0 row 2).
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CAST(r.percent_complete AS float) FROM sys.dm_exec_sessions s JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                WHERE s.session_id = @id AND s.login_time = @start AND (r.status = N'rollback' OR r.command = N'KILLED/ROLLBACK');
                SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE session_id = @id AND login_time = @start;
                """;
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id });
            cmd.Parameters.Add(SqlServerActivity.StartParam(key));
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false)) return new RollbackStatus(true, r.IsDBNull(0) ? null : r.GetDouble(0));
            await r.NextResultAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            if (r.GetInt32(0) == 0) return new RollbackStatus(false, 100);
        }
        if (connection is not SqlConnection sql) return new RollbackStatus(true, null);
        double? pct = null;
        var inProgress = false;
        void OnInfo(object _, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError err in e.Errors)
                if (err.Number == 6109) { inProgress = true; pct = PercentIn(err.Message); }
        }
        sql.InfoMessage += OnInfo;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = StatusOnlySql(key.Id);
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id });
            cmd.Parameters.Add(SqlServerActivity.StartParam(key));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 6120 or 6106) { return new RollbackStatus(false, null); }
        finally { sql.InfoMessage -= OnInfo; }
        return new RollbackStatus(inProgress, pct);
    }

    /// <summary>The first "N%" in a 6109 message (server output, not SQL); null when the wording has none.</summary>
    internal static double? PercentIn(string message)
    {
        var p = message.IndexOf('%');
        if (p <= 0) return null;
        var start = p;
        while (start > 0 && char.IsAsciiDigit(message[start - 1])) start--;
        return start < p && int.TryParse(message.AsSpan(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= 100 ? v : null;
    }

    // ---- kill preview ----

    public async Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        if (!Full) return new KillPreview { Key = key, Risk = RollbackRisk.Unknown, Reason = "Reading transaction state requires VIEW SERVER STATE." };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            IF EXISTS (SELECT 1 FROM sys.dm_tran_active_transactions WHERE transaction_state = 7)
                SELECT CAST(NULL AS bigint), CAST(NULL AS bigint), CAST(NULL AS bigint), -1,
                       MAX(CASE WHEN r.status = N'rollback' OR r.command = N'KILLED/ROLLBACK' THEN 1 ELSE 0 END)
                FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                WHERE s.session_id = @id AND s.login_time = @start
                GROUP BY s.session_id;
            ELSE
            SELECT CAST(SUM(dt.database_transaction_log_bytes_used) AS bigint), CAST(SUM(dt.database_transaction_log_bytes_reserved) AS bigint),
                   DATEDIFF_BIG(millisecond, MIN(at.transaction_begin_time), SYSDATETIME()), COUNT(DISTINCT st.transaction_id),
                   MAX(CASE WHEN r.status = N'rollback' OR r.command = N'KILLED/ROLLBACK' THEN 1 ELSE 0 END)
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_tran_session_transactions st ON st.session_id = s.session_id
            LEFT JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id
            LEFT JOIN sys.dm_tran_database_transactions dt ON dt.transaction_id = st.transaction_id
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            WHERE s.session_id = @id AND s.login_time = @start
            GROUP BY s.session_id;
            """;
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id });
        cmd.Parameters.Add(SqlServerActivity.StartParam(key));
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new KillPreview { Key = key, SessionEnded = true, Reason = "The session ended." };
            var txCount = r.GetInt32(3);
            if (txCount < 0)
                return new KillPreview
                {
                    Key = key, Risk = RollbackRisk.Unknown, RollingBack = r.GetInt32(4) == 1,
                    Reason = "SQL Server is rolling back a terminated session and blocks reading other sessions' transactions until it ends",
                };
            return new KillPreview
            {
                Key = key,
                LogBytesUsed = r.IsDBNull(0) ? null : r.GetInt64(0),
                LogBytesReserved = r.IsDBNull(1) ? null : r.GetInt64(1),
                TxElapsedMs = r.IsDBNull(2) ? null : r.GetInt64(2),
                TxCount = txCount,
                RollingBack = r.GetInt32(4) == 1,
            };
        }
        catch (SqlException ex) when (ex.Number is 300 or 297)
        {
            return new KillPreview { Key = key, Risk = RollbackRisk.Unknown, Reason = "Reading transaction state requires VIEW SERVER STATE." };
        }
    }

    // ---- request detail (B5) ----

    public async Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        if (!Full) return new RequestDetail { Key = key, TextNote = OwnTextNote, PlanReason = "The plan needs VIEW SERVER STATE." };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = RollbackRunningSql + """
            SELECT LEFT(t.text, 1048576), CAST(ISNULL(t.encrypted, 0) AS int), r.statement_start_offset, r.statement_end_offset,
                   CONVERT(varchar(130), r.plan_handle, 1), s.open_transaction_count, s.transaction_isolation_level,
                   (SELECT DATEDIFF_BIG(millisecond, MIN(at.transaction_begin_time), SYSDATETIME()) FROM sys.dm_tran_session_transactions st
                    JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id WHERE @rb = 0 AND st.session_id = s.session_id),
                   CASE WHEN DATALENGTH(t.text) > 2097152 THEN 1 ELSE 0 END
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
            OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
            WHERE s.session_id = @id AND s.login_time = @start;
            """;
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id });
        cmd.Parameters.Add(SqlServerActivity.StartParam(key));
        string? text = null, planHandle = null;
        int? start = null, end = null, openTx = null, iso = null;
        long? txMs = null;
        bool encrypted, truncated;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return new RequestDetail { Key = key, TextNote = "The session ended.", PlanReason = "The session ended." };
            text = r.IsDBNull(0) ? null : r.GetString(0);
            encrypted = r.GetInt32(1) == 1;
            if (!r.IsDBNull(2) && text != null)
            {
                start = Math.Min(r.GetInt32(2) / 2, text.Length);
                var e = r.IsDBNull(3) ? -1 : r.GetInt32(3);
                end = e is -1 or 0 ? text.Length : Math.Min(e / 2 + 1, text.Length);
            }
            planHandle = r.IsDBNull(4) ? null : r.GetString(4);
            openTx = r.IsDBNull(5) ? null : r.GetInt32(5);
            iso = r.IsDBNull(6) ? null : Convert.ToInt32(r.GetValue(6), CultureInfo.InvariantCulture);
            txMs = r.IsDBNull(7) ? null : r.GetInt64(7);
            truncated = r.GetInt32(8) == 1;
        }
        // Plan: live (lightweight profiling) → cached → reason.
        string? raw = null, source = null, reason = null;
        if (planHandle == null) reason = "The session is not running a request.";
        else if (!Full) reason = "Plans require VIEW SERVER STATE.";
        else
        {
            if (caps.LivePlans && caps.LivePlanReason == null)
            {
                raw = await ScalarStringAsync(connection, "SELECT CAST(query_plan AS nvarchar(max)) FROM sys.dm_exec_query_statistics_xml(@id);",
                    [new SqlParameter("@id", SqlDbType.Int) { Value = (int)key.Id }], ct).ConfigureAwait(false);
                if (raw != null) source = "live";
            }
            if (raw == null && TryHex(planHandle, out var ph))
            {
                raw = await ScalarStringAsync(connection, "SELECT CAST(query_plan AS nvarchar(max)) FROM sys.dm_exec_query_plan(@ph);",
                    [new SqlParameter("@ph", SqlDbType.VarBinary, 64) { Value = ph }], ct).ConfigureAwait(false);
                if (raw != null) source = "cached";
            }
            if (raw == null) reason = "The server has no plan for this request (not cached, or too large to return).";
        }
        PlanResult? plan = null;
        if (raw != null)
        {
            try { plan = new PlanResult { Plan = plans.Parse(raw), Raw = raw, Format = "xml" }; }
            catch (Exception ex) { reason = $"The plan could not be read: {ex.Message}"; source = null; }
        }
        return new RequestDetail
        {
            Key = key, Text = text, TextTruncated = truncated, TextNote = encrypted ? "text unavailable (encrypted)" : text == null ? "no text" : null,
            StatementStart = start, StatementEnd = end, Plan = plan, PlanSource = plan != null ? source : null, PlanReason = plan == null ? reason : null,
            TxElapsedMs = txMs, OpenTx = openTx,
            IsolationLevel = iso switch { 1 => "read uncommitted", 2 => "read committed", 3 => "repeatable read", 4 => "serializable", 5 => "snapshot", _ => null },
        };
    }

    static async Task<string?> ScalarStringAsync(DbConnection connection, string sql, SqlParameter[] ps, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(ps);
        try { return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string; }
        catch (SqlException) { return null; }
    }
}
