using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Oracle's activity provider. Every connection reads its own session identity (§6 rule 7, D4 S03): the SID from
/// <c>SYS_CONTEXT('USERENV','SID')</c>, which needs no grant, and SERIAL# and LOGON_TIME from V$SESSION, which tell a reused
/// SID apart. Without that grant the connection is registered unidentified (§16): it serves queries, and kills are refused
/// while it is open. The activity monitor (D4 S12, owner decision O4 default) reads V$SESSION, V$LOCK, V$SQL / V$SQLSTATS,
/// V$SYSTEM_EVENT, V$SYSSTAT and V$TRANSACTION where the account may; each pane it cannot read says which grant it needs,
/// and without V$SESSION the monitor stops with that reason. Cancel (<c>ALTER SYSTEM CANCEL SQL</c>) and terminate (<c>ALTER
/// SYSTEM KILL SESSION … IMMEDIATE</c>) need ALTER SYSTEM and re-check the identity on the server in the same PL/SQL block:
/// the statement is built there from the V$SESSION row that still matches, never from text the webview sent.
/// </summary>
public sealed class OracleActivity : IActivityProvider
{
    internal const string SidSql = "SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'SID')) FROM DUAL";
    internal const string SessionSql = "SELECT SERIAL#, TO_CHAR(LOGON_TIME, 'YYYYMMDDHH24MISS') FROM V$SESSION WHERE SID = :sid";

    public const string NoSessionGrant =
        "This account cannot read V$SESSION (it needs SELECT on V_$SESSION, or SELECT_CATALOG_ROLE), so the connection's server session cannot be told apart from a later one with the same SID.";

    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        long sid;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SidSql;
            sid = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        await using var q = (OracleCommand)connection.CreateCommand();
        q.CommandText = SessionSql;
        q.BindByName = true;
        q.Parameters.Add(new OracleParameter("sid", OracleDbType.Int64) { Value = sid });
        try
        {
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new IdentityUnavailableException("V$SESSION does not show this connection's own session.");
            var serial = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            return new SessionKey { Id = sid, Instance = $"{serial.ToString(CultureInfo.InvariantCulture)}:{r.GetString(1)}" };
        }
        // ORA-00942 table or view does not exist (no grant); ORA-01031 insufficient privileges.
        catch (OracleException ex) when (ex.Number is 942 or 1031)
        {
            throw new IdentityUnavailableException(NoSessionGrant, ex);
        }
    }

    static readonly ConditionalWeakTable<DbConnection, string> Tokens = new();

    /// <summary>
    /// ODP.NET does not reconnect underneath a connection object (Application Continuity is off in the connection string), so
    /// the token is the object's own: a reopened connection is a new object.
    /// </summary>
    public string ConnectionToken(DbConnection connection) => Tokens.GetValue(connection, _ => Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Oracle readers take no locks and are never blocked by writers, so there is nothing to lower; the module name makes the
    /// monitor's own session recognisable in V$SESSION (and in other tools).
    /// </summary>
    public string MonitorSessionSetupSql => "BEGIN DBMS_APPLICATION_INFO.SET_MODULE('Querithm Monitor', NULL); END;";

    public void PrepareMonitorConnection(DbConnection connection) { } // ODP.NET always throws OracleException

    /// <summary>ORA-01017 wrong user or password, 28000 account locked, 28001 password expired, 01005 empty password.</summary>
    public bool CredentialsRejected(Exception ex) => ex is OracleException o && OracleErrors.Code(o) is 1017 or 28000 or 28001 or 1005;

    public const string Cpu = "CPU", Lock = "Lock", Latch = "Latch", BufferIo = "Buffer IO", LogIo = "Log IO", Network = "Network", Other = "Other";

    /// <summary>The monitor category of an Oracle wait event, by its name (the event's WAIT_CLASS refines it where the server reports one).</summary>
    public string WaitCategory(string waitType) => CategoryOf(waitType, null);

    /// <summary>V$SYSTEM_EVENT / V$SESSION wait classes and event names → the monitor's shared categories.</summary>
    public static string CategoryOf(string evt, string? waitClass)
    {
        if (evt.StartsWith("enq: TX", StringComparison.Ordinal) || evt.StartsWith("enq: TM", StringComparison.Ordinal) || evt.StartsWith("enq: UL", StringComparison.Ordinal)
            || evt.StartsWith("enq: ", StringComparison.Ordinal) && waitClass is null or "Application")
            return Lock;
        if (evt.StartsWith("latch", StringComparison.Ordinal) || evt.StartsWith("library cache", StringComparison.Ordinal) || evt.StartsWith("cursor:", StringComparison.Ordinal)
            || evt.StartsWith("row cache", StringComparison.Ordinal) || evt.StartsWith("buffer busy", StringComparison.Ordinal) || waitClass == "Concurrency")
            return Latch;
        if (evt.StartsWith("log file", StringComparison.Ordinal) || evt.StartsWith("log buffer", StringComparison.Ordinal) || waitClass == "Commit") return LogIo;
        if (evt.StartsWith("SQL*Net", StringComparison.Ordinal) || waitClass == "Network") return Network;
        if (evt.StartsWith("db file", StringComparison.Ordinal) || evt.StartsWith("direct path", StringComparison.Ordinal) || evt == "read by other session"
            || waitClass is "User I/O" or "System I/O")
            return BufferIo;
        if (evt.StartsWith("resmgr:cpu", StringComparison.Ordinal) || waitClass == "Scheduler") return Cpu;
        return Other;
    }

    /// <summary>
    /// Waits in Oracle's Idle class that a session list can show: a session waiting for its client, a background timer, a
    /// sleeping PL/SQL block. The waits pane leaves the whole Idle class out on the server.
    /// </summary>
    public bool IsBenignWait(string waitType) =>
        waitType.StartsWith("SQL*Net message from", StringComparison.Ordinal) || waitType.StartsWith("SQL*Net break/reset", StringComparison.Ordinal)
        || waitType is "rdbms ipc message" or "pmon timer" or "smon timer" or "jobq slave wait" or "PL/SQL lock timer" or "pipe get" or "class slave wait"
        || waitType.StartsWith("Streams AQ:", StringComparison.Ordinal) || waitType.StartsWith("Space Manager:", StringComparison.Ordinal);

    /// <summary>"SERIAL#:YYYYMMDDHH24MISS".</summary>
    public bool IsValidInstance(string instance)
    {
        var colon = instance.IndexOf(':');
        return colon is > 0 and <= 10 && instance.Length == colon + 15 && instance.AsSpan(0, colon).ContainsOnlyDigits() && instance.AsSpan(colon + 1).ContainsOnlyDigits();
    }

    /// <summary>"SQL_ID:SQL_EXEC_ID" of the running statement: a 13-character SQL_ID (digits and lower-case letters) and its execution id.</summary>
    public bool IsValidStatementStart(string statementStart)
    {
        if (statementStart.Length is < 15 or > 34 || statementStart[13] != ':') return false;
        foreach (var c in statementStart.AsSpan(0, 13)) if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'z')) return false;
        return statementStart.AsSpan(14).ContainsOnlyDigits();
    }

    // ---- probe ----

    /// <summary>Raw probe values; <see cref="Capabilities"/> turns them into panes and reasons (pure, unit-tested).</summary>
    public sealed record Probe(string Version, string? Login, bool Sessions, bool Locks, bool SqlText, bool SqlStats, bool SystemEvents, bool SysStat,
        bool Transactions, bool SysMetric, bool Instance, bool AlterSystem, long InstanceNumber, long? BlockSize);

    /// <summary>
    /// One PL/SQL block: each view tried with a query that returns no row (a view the account may not read fails at parse with
    /// ORA-00942 / ORA-01031), ALTER SYSTEM from SESSION_PRIVS (granted directly or through an enabled role), and the undo block
    /// size. The view names are constants of this block; nothing in it comes from outside.
    /// </summary>
    internal const string ProbeSql = """
        DECLARE
          v_alter NUMBER;
          v_blk NUMBER;
          FUNCTION can(v VARCHAR2) RETURN NUMBER IS
            n NUMBER;
          BEGIN
            EXECUTE IMMEDIATE 'SELECT COUNT(*) FROM ' || v || ' WHERE ROWNUM < 1' INTO n;
            RETURN 1;
          EXCEPTION WHEN OTHERS THEN
            IF SQLCODE IN (-942, -1031) THEN RETURN 0; END IF;
            RAISE;
          END;
        BEGIN
          :p_sess := can('V$SESSION');
          :p_lock := can('V$LOCK');
          :p_sql := can('V$SQL');
          :p_stats := can('V$SQLSTATS');
          :p_event := can('V$SYSTEM_EVENT');
          :p_sysstat := can('V$SYSSTAT');
          :p_tx := can('V$TRANSACTION');
          :p_metric := can('V$SYSMETRIC');
          :p_inst := can('V$INSTANCE');
          SELECT COUNT(*) INTO v_alter FROM SESSION_PRIVS WHERE PRIVILEGE = 'ALTER SYSTEM';
          :p_alter := v_alter;
          :p_login := SYS_CONTEXT('USERENV', 'SESSION_USER');
          :p_instno := TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE'));
          v_blk := NULL;
          IF can('V$PARAMETER') = 1 THEN
            EXECUTE IMMEDIATE 'SELECT TO_NUMBER(VALUE) FROM V$PARAMETER WHERE NAME = ''db_block_size''' INTO v_blk;
          END IF;
          :p_blk := v_blk;
        END;
        """;

    static readonly string[] ProbeFlags = ["p_sess", "p_lock", "p_sql", "p_stats", "p_event", "p_sysstat", "p_tx", "p_metric", "p_inst", "p_alter"];

    public async Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        Probe p;
        await using (var cmd = (OracleCommand)connection.CreateCommand())
        {
            cmd.CommandText = ProbeSql;
            cmd.BindByName = true;
            foreach (var name in ProbeFlags) cmd.Parameters.Add(new OracleParameter(name, OracleDbType.Int32, ParameterDirection.Output));
            cmd.Parameters.Add(new OracleParameter("p_login", OracleDbType.Varchar2, 256, null, ParameterDirection.Output));
            cmd.Parameters.Add(new OracleParameter("p_instno", OracleDbType.Int64, ParameterDirection.Output));
            cmd.Parameters.Add(new OracleParameter("p_blk", OracleDbType.Int64, ParameterDirection.Output));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            bool F(string name) => Num(cmd.Parameters[name]) == 1;
            var product = OracleProvider.ProductOf(connection);
            p = new Probe($"{product.Name} {product.Version}", Str(cmd.Parameters["p_login"]), F("p_sess"), F("p_lock"), F("p_sql"), F("p_stats"), F("p_event"),
                F("p_sysstat"), F("p_tx"), F("p_metric"), F("p_inst"), Num(cmd.Parameters["p_alter"]) > 0, Num(cmd.Parameters["p_instno"]) ?? 1, Num(cmd.Parameters["p_blk"]));
        }
        SessionKey self;
        if (p.Sessions) self = await IdentityAsync(connection, ct).ConfigureAwait(false);
        else
        {
            // The monitor refuses to start (StartRefusal); its own SID still names it in the log and the stop reason.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SidSql;
            self = new SessionKey { Id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture), Instance = "" };
        }
        return new OracleActivitySession(p, Capabilities(p), self);
    }

    internal static long? Num(OracleParameter p) => p.Value switch
    {
        OracleDecimal { IsNull: true } => null,
        OracleDecimal d => (long)d.Value,
        null or DBNull => null,
        var v => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    static string? Str(OracleParameter p) => p.Value switch
    {
        OracleString { IsNull: true } => null,
        OracleString s => s.Value,
        null or DBNull => null,
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    static string Grant(string view) => $"SELECT on {view.Replace("V$", "V_$", StringComparison.Ordinal)} (or SELECT_CATALOG_ROLE)";

    public const string NoAlterSystem = "Cancelling or terminating a session needs the ALTER SYSTEM privilege, which this account does not have.";

    public static MonitorCapabilities Capabilities(Probe p)
    {
        PaneCapability Pane(MonitorPane pane, bool ok, string? reason) => new() { Pane = pane, Available = ok, Reason = reason };
        return new MonitorCapabilities
        {
            ServerVersion = p.Version,
            // V$SESSION lists every session of the container the connection reached (a pluggable database shows its own).
            AllSessionsVisible = p.Sessions,
            SessionsReason = p.Sessions ? null : $"Sessions need {Grant("V$SESSION")}.",
            CanTerminate = p.Sessions && p.AlterSystem,
            TerminateReason = p.AlterSystem ? null : NoAlterSystem,
            CanCancelRequest = p.Sessions && p.AlterSystem,
            CancelReason = p.AlterSystem ? null : NoAlterSystem,
            LivePlans = false,
            LivePlanReason = "The plan of a running statement is not read here: open its text in a new tab and ask for its estimated plan there.",
            QueryHashReason = null,
            Panes =
            [
                Pane(MonitorPane.Sessions, p.Sessions, p.Sessions
                    ? p.SqlText ? null : $"Statement text needs {Grant("V$SQL")}."
                    : $"Sessions need {Grant("V$SESSION")}."),
                Pane(MonitorPane.Locks, p.Sessions && p.Locks, p.Locks ? "TM (table) and TX (transaction) locks of user sessions, from V$LOCK." : $"Locks need {Grant("V$LOCK")}. Blocking chains are still shown in the session list."),
                Pane(MonitorPane.Waits, p.SystemEvents, p.SystemEvents ? "Wait events since the instance started (V$SYSTEM_EVENT), the Idle class left out." : $"Wait events need {Grant("V$SYSTEM_EVENT")}."),
                Pane(MonitorPane.Metrics, p.Sessions, p.SysStat ? (p.SysMetric ? null : $"CPU needs {Grant("V$SYSMETRIC")}.") : $"Rates need {Grant("V$SYSSTAT")}; only session counts are shown."),
                Pane(MonitorPane.Io, false, "File I/O per data file is not read for Oracle yet."),
                Pane(MonitorPane.TopQueries, p.SqlStats, p.SqlStats ? "Cursors in the shared pool (V$SQLSTATS): a statement aged out of it is no longer counted." : $"Top queries need {Grant("V$SQLSTATS")}."),
                Pane(MonitorPane.Deadlocks, false, "Oracle writes deadlocks (ORA-00060) to the alert log and a trace file, which Querithm does not read."),
            ],
            Login = p.Login,
            OtherLoginsReason = null, // ALTER SYSTEM reaches every session of the container
        };
    }

    /// <summary>Without V$SESSION nothing can be shown and the monitor could not recognise its own session (K6): it stops with the grant it needs.</summary>
    public static string? StartRefusal(Probe p) => p.Sessions
        ? null
        : $"The activity monitor reads V$SESSION, which this account cannot read: grant it {Grant("V$SESSION")}.";

    // ---- own-tab blocking banner ----

    /// <summary>V$SESSION.BLOCKING_SESSION of the sessions waiting on any of <paramref name="ids"/> (SIDs).</summary>
    public async Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return ([], null);
        await using var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandTimeout = 5; // a pool connection has none
        cmd.BindByName = true;
        var names = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            names.Add(":b" + i.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.Add(new OracleParameter("b" + i.ToString(CultureInfo.InvariantCulture), OracleDbType.Int64) { Value = ids[i] });
        }
        cmd.CommandText = $"""
            SELECT s.BLOCKING_SESSION, COUNT(*), MAX(CASE WHEN s.STATE = 'WAITING' THEN s.WAIT_TIME_MICRO END) FROM V$SESSION s
            WHERE s.BLOCKING_SESSION_STATUS = 'VALID' AND s.BLOCKING_SESSION IN ({string.Join(", ", names)}) GROUP BY s.BLOCKING_SESSION
            """;
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<BlockedBySummary>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(new BlockedBySummary(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) / 1000));
            return (rows.ToArray(), null);
        }
        catch (OracleException ex) when (ex.Number is 942 or 1031)
        {
            return (null, "Seeing other sessions needs SELECT on V_$SESSION (or SELECT_CATALOG_ROLE) for this account.");
        }
    }

    /// <summary>V$SESSION.STATUS in the monitor's words; an INACTIVE session with a transaction is "idle in transaction".</summary>
    public static string StatusOf(string? status, bool inTransaction) => status switch
    {
        "ACTIVE" => "active",
        "INACTIVE" => inTransaction ? "idle in transaction" : "inactive",
        "KILLED" => "killed",
        null => "unknown",
        _ => status.ToLowerInvariant(),
    };

    /// <summary>V$LOCK LMODE / REQUEST.</summary>
    public static string LockMode(long mode) => mode switch
    {
        0 => "none",
        1 => "null (N)",
        2 => "row share (SS)",
        3 => "row exclusive (SX)",
        4 => "share (S)",
        5 => "share row exclusive (SSX)",
        6 => "exclusive (X)",
        _ => mode.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>V$SESSION.COMMAND, the codes a monitored session runs (the rest by number).</summary>
    public static string? CommandOf(long? command) => command switch
    {
        null or 0 => null,
        1 => "CREATE TABLE",
        2 => "INSERT",
        3 => "SELECT",
        6 => "UPDATE",
        7 => "DELETE",
        9 => "CREATE INDEX",
        12 => "DROP TABLE",
        15 => "ALTER TABLE",
        26 => "LOCK TABLE",
        44 => "COMMIT",
        45 => "ROLLBACK",
        47 => "PL/SQL EXECUTE",
        85 => "TRUNCATE TABLE",
        170 => "CALL METHOD",
        189 => "MERGE",
        _ => "command " + command.Value.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Per-monitor-connection state: the probe and the queries it chose.</summary>
sealed class OracleActivitySession(OracleActivity.Probe probe, MonitorCapabilities caps, SessionKey self) : IActivitySession
{
    public MonitorCapabilities Capabilities => caps;
    public string? StartRefusal => OracleActivity.StartRefusal(probe);
    public SessionKey Self => self;
    public bool WaitsFromSessions => false;

    const int DetailChars = 1_000_000;
    internal const string IdleText = "The last statement this session ran (it is not running one now).";

    string Reason(MonitorPane pane) => caps.Panes.First(x => x.Pane == pane).Reason ?? "not available";

    static OracleCommand Command(DbConnection connection, string sql)
    {
        var cmd = (OracleCommand)connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        return cmd;
    }

    /// <summary>
    /// User sessions with their wait, blocker, transaction and statement. Text: the running statement, or for an idle session
    /// the last one it ran (V$SQL by SQL_ID and child number; the first 1,000 characters). Blocked sessions and their
    /// blockers sort first so the cap never cuts a chain.
    /// </summary>
    string SessionsSql => $"""
        SELECT s.SID, s.SERIAL#, TO_CHAR(s.LOGON_TIME, 'YYYYMMDDHH24MISS'), s.USERNAME, s.MACHINE, s.PROGRAM, s.SCHEMANAME, s.STATUS, s.COMMAND,
               s.STATE, s.EVENT, s.WAIT_CLASS, s.WAIT_TIME_MICRO, s.BLOCKING_SESSION, s.BLOCKING_SESSION_STATUS, s.BLOCKING_INSTANCE,
               s.SQL_ID, s.SQL_EXEC_ID, s.LAST_CALL_ET, CASE WHEN s.TADDR IS NULL THEN 0 ELSE 1 END, s.MODULE,
               {(probe.Transactions ? "ROUND((SYSDATE - t.START_DATE) * 86400000), t.USED_UREC" : "NULL, NULL")},
               {(probe.SqlText
                   ? "(SELECT q.SQL_TEXT FROM V$SQL q WHERE q.SQL_ID = NVL(s.SQL_ID, s.PREV_SQL_ID) AND q.CHILD_NUMBER = NVL(s.SQL_CHILD_NUMBER, s.PREV_CHILD_NUMBER) AND ROWNUM = 1), NULL"
                   : "NULL, NULL")},
               CASE WHEN s.STATE = 'WAITING' AND s.EVENT LIKE 'enq: T%' AND s.ROW_WAIT_OBJ# > 0
                    THEN (SELECT o.OWNER || '.' || o.OBJECT_NAME FROM ALL_OBJECTS o WHERE o.OBJECT_ID = s.ROW_WAIT_OBJ#) END,
               COUNT(*) OVER ()
        FROM V$SESSION s
        {(probe.Transactions ? "LEFT JOIN V$TRANSACTION t ON t.ADDR = s.TADDR" : "")}
        WHERE s.TYPE = 'USER' AND (:db IS NULL OR s.SCHEMANAME = :db)
        ORDER BY CASE WHEN s.BLOCKING_SESSION_STATUS = 'VALID' OR s.SID IN (SELECT b.BLOCKING_SESSION FROM V$SESSION b WHERE b.BLOCKING_SESSION_STATUS = 'VALID') THEN 0
                      WHEN s.STATUS = 'ACTIVE' THEN 1 ELSE 2 END, s.SID
        FETCH FIRST :max ROWS ONLY
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
            catch (OracleException ex) { sessions = PaneResult<ActivityRow[]>.Error(ex.Message); }
        }
        if (request.Waits)
        {
            if (!probe.SystemEvents) waits = PaneResult<WaitCounter[]>.Degraded(Reason(MonitorPane.Waits));
            else
            {
                try { waits = await WaitsAsync(connection, ct).ConfigureAwait(false); }
                catch (OracleException ex) { waits = PaneResult<WaitCounter[]>.Error(ex.Message); }
            }
        }
        if (request.Metrics)
        {
            try { (metrics, reset) = await MetricsAsync(connection, ct).ConfigureAwait(false); }
            catch (OracleException ex) { metrics = PaneResult<MetricCounter[]>.Error(ex.Message); }
        }
        var io = request.Io ? PaneResult<IoCounter[]>.Degraded(Reason(MonitorPane.Io)) : null;
        return new TickResult(sessions, texts, waits, metrics, io, reset);
    }

    async Task<(PaneResult<ActivityRow[]>, Dictionary<string, string?>)> SessionsAsync(DbConnection connection, TickRequest request, CancellationToken ct)
    {
        await using var cmd = Command(connection, SessionsSql);
        cmd.Parameters.Add(new OracleParameter("db", OracleDbType.Varchar2) { Value = (object?)request.DatabaseFilter ?? DBNull.Value });
        cmd.Parameters.Add(new OracleParameter("max", OracleDbType.Int32) { Value = Math.Max(1, request.MaxSessions) });
        var rows = new List<ActivityRow>();
        var texts = new Dictionary<string, string?>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                long? L(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
                string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                var sid = L(0)!.Value;
                var status = S(7);
                var inTx = L(19) == 1;
                var active = status == "ACTIVE";
                var waiting = S(9) == "WAITING";
                var evt = S(10);
                var waitClass = S(11);
                var sqlId = S(16);
                var execId = L(17);
                string? textId = null, textNote = null;
                if (S(23) is { } text)
                {
                    textId = "s" + sid.ToString(CultureInfo.InvariantCulture);
                    texts[textId] = text;
                    // SQL_TEXT holds the first 1,000 characters; the whole statement is in the session's detail (one LOB read per
                    // row for the length cost the tick about half its time in the D4 S12 bench profile).
                    var cut = text.Length >= OracleActivitySessionLimits.TextChars ? $"The first {OracleActivitySessionLimits.TextChars:N0} characters; open the session for the whole statement." : null;
                    textNote = active ? cut : cut == null ? IdleText : $"{IdleText} {cut}";
                }
                else if ((sqlId ?? "") != "" && !probe.SqlText) textNote = Reason(MonitorPane.Sessions);
                // A wait the session is in right now, unless it only waits for its client (an idle session).
                var waitType = waiting && evt != null && !(waitClass == "Idle" && !active) ? evt : null;
                var blocked = S(14) == "VALID" && L(13) is { } blocker ? blocker : (long?)null;
                var blockerInstance = L(15);
                total = (int)(L(26) ?? 0);
                rows.Add(new ActivityRow
                {
                    Key = new SessionKey { Id = sid, Instance = $"{L(1)!.Value.ToString(CultureInfo.InvariantCulture)}:{S(2)}" },
                    Login = S(3), Host = S(4), Program = S(5) ?? S(20), Database = S(6),
                    Status = OracleActivity.StatusOf(status, inTx),
                    Command = OracleActivity.CommandOf(L(8)), BackendType = "user", IsUserSession = true,
                    WaitType = waitType, WaitResource = waitType == null ? null : S(25), WaitMs = waitType == null ? null : L(12) / 1000,
                    BlockedBy = blocked is { } b ? [b] : [],
                    BlockedByNote = blocked != null && blockerInstance is { } bi && bi != probe.InstanceNumber
                        ? $"The blocker runs on instance {bi.ToString(CultureInfo.InvariantCulture)} (Oracle RAC); only this instance's sessions are listed." : null,
                    TextId = textId, TextNote = textNote,
                    QueryHash = active ? sqlId : null,
                    ElapsedMs = active ? L(18) * 1000 : null, StateChangeMs = L(18) * 1000,
                    TxElapsedMs = inTx ? L(21) : null,
                    StatementStart = active && !string.IsNullOrEmpty(sqlId) && execId is { } e ? $"{sqlId}:{e.ToString(CultureInfo.InvariantCulture)}" : null,
                    OpenTx = inTx ? 1 : 0,
                    // ALTER SYSTEM KILL SESSION marks the session KILLED while the server rolls it back and cleans up.
                    RollingBack = status == "KILLED",
                });
            }
        }
        var pane = total > rows.Count ? PaneResult<ActivityRow[]>.Truncated(rows.ToArray(), total) : PaneResult<ActivityRow[]>.Ok(rows.ToArray());
        return (pane, texts);
    }

    async Task<PaneResult<WaitCounter[]>> WaitsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = Command(connection, "SELECT EVENT, WAIT_CLASS, ROUND(TIME_WAITED_MICRO / 1000), TOTAL_WAITS FROM V$SYSTEM_EVENT WHERE WAIT_CLASS <> 'Idle' AND TOTAL_WAITS > 0");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<WaitCounter>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = r.GetString(0);
            list.Add(new WaitCounter(name, OracleActivity.CategoryOf(name, r.IsDBNull(1) ? null : r.GetString(1)),
                Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture), null));
        }
        return PaneResult<WaitCounter[]>.Ok(list.ToArray());
    }

    /// <summary>Statistics names read from V$SYSSTAT (cumulative since the instance started).</summary>
    static readonly string[] Stats = ["user commits", "user rollbacks", "execute count", "session logical reads", "physical reads", "parse count (hard)"];

    string MetricsSql
    {
        get
        {
            // One pass over V$SYSSTAT and one over V$SESSION (a scalar subquery per statistic read each view again: the metrics
            // read was a third of the tick in the D4 S12 bench profile).
            var stats = probe.SysStat
                ? $"SELECT {string.Join(", ", Stats.Select((n, i) => $"MAX(CASE WHEN NAME = '{n}' THEN VALUE END) s{i}"))} FROM V$SYSSTAT WHERE NAME IN ({string.Join(", ", Stats.Select(n => $"'{n}'"))})"
                : $"SELECT {string.Join(", ", Stats.Select((_, i) => $"NULL s{i}"))} FROM DUAL";
            return $"""
                SELECT st.*, se.*,
                  {(probe.SysMetric ? "(SELECT MAX(VALUE) KEEP (DENSE_RANK LAST ORDER BY END_TIME) FROM V$SYSMETRIC WHERE METRIC_NAME = 'Host CPU Utilization (%)' AND GROUP_ID = 2)" : "NULL")},
                  {(probe.Instance ? "(SELECT TO_CHAR(STARTUP_TIME, 'YYYYMMDDHH24MISS') FROM V$INSTANCE)" : "NULL")}
                FROM ({stats}) st,
                     (SELECT COUNT(*) n_all, COUNT(CASE WHEN STATUS = 'ACTIVE' THEN 1 END) n_active, COUNT(CASE WHEN STATUS = 'INACTIVE' AND TADDR IS NOT NULL THEN 1 END) n_idle_tx
                      FROM V$SESSION WHERE TYPE = 'USER') se
                """;
        }
    }

    async Task<(PaneResult<MetricCounter[]>, string?)> MetricsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = Command(connection, MetricsSql);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        double? D(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        var (commits, rollbacks, executes, logical, physical, hard) = (D(0), D(1), D(2), D(3), D(4), D(5));
        var noStats = probe.SysStat ? null : caps.Panes.First(x => x.Pane == MonitorPane.Metrics).Reason;
        var cpu = D(9);
        var list = new List<MetricCounter>
        {
            new("tps", commits is { } c && rollbacks is { } rb ? c + rb : null, true, "perSec", noStats),
            new("queries", executes, true, "perSec", noStats),
            new("compilations", hard, true, "perSec", noStats ?? "hard parses"),
            // Folded into cacheHit by the sidecar: hits = logical reads that did not go to disk.
            new("blksHit", logical is { } l && physical is { } ph ? l - ph : null, true, "count"),
            new("blksRead", physical, true, "count"),
            new("userConnections", D(6), false, "count"),
            new("activeConnections", D(7), false, "count"),
            new("idleInTx", D(8), false, "count"),
            new("deadlocks", null, false, "count", "Oracle reports deadlocks (ORA-00060) in the alert log, which SQL cannot read."),
            new("cpu", cpu, false, "percent", probe.SysMetric ? cpu == null ? "n/a (no 60 s sample yet)" : "host CPU, 60 s" : caps.Panes.First(x => x.Pane == MonitorPane.Metrics).Reason),
        };
        // Counters restart with the instance: its startup time is the reset marker.
        return (PaneResult<MetricCounter[]>.Ok(list.ToArray()), r.IsDBNull(10) ? null : r.GetString(10));
    }

    public async Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct)
    {
        if (!probe.Locks) return PaneResult<LockRow[]>.Degraded(Reason(MonitorPane.Locks));
        // TM: ID1 is the table's object id. TX: ID1 / ID2 name the undo slot; a waiting TX names the row's object in V$SESSION.
        await using var cmd = Command(connection, """
            SELECT x.SID, x.SERIAL#, x.LOGON, x.TYPE, x.LMODE, x.REQUEST, x.OBJ,
                   (SELECT o.OWNER FROM ALL_OBJECTS o WHERE o.OBJECT_ID = x.OBJ), (SELECT o.OBJECT_NAME FROM ALL_OBJECTS o WHERE o.OBJECT_ID = x.OBJ)
            FROM (SELECT l.SID, s.SERIAL#, TO_CHAR(s.LOGON_TIME, 'YYYYMMDDHH24MISS') LOGON, l.TYPE, l.LMODE, l.REQUEST,
                         CASE WHEN l.TYPE = 'TM' THEN l.ID1 WHEN l.TYPE = 'TX' AND l.REQUEST > 0 AND s.ROW_WAIT_OBJ# > 0 THEN s.ROW_WAIT_OBJ# END OBJ
                  FROM V$LOCK l JOIN V$SESSION s ON s.SID = l.SID
                  WHERE s.TYPE = 'USER' AND l.TYPE IN ('TM', 'TX', 'UL')) x
            ORDER BY x.REQUEST DESC, x.SID
            """);
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<LockRow>();
            var total = 0;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                total++;
                if (list.Count >= max) continue;
                long L(int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
                var type = r.GetString(3);
                var request = L(5);
                long? obj = r.IsDBNull(6) ? null : L(6);
                var owner = r.IsDBNull(7) ? null : r.GetString(7);
                var name = r.IsDBNull(8) ? null : r.GetString(8);
                list.Add(new LockRow
                {
                    Key = new SessionKey { Id = L(0), Instance = $"{L(1).ToString(CultureInfo.InvariantCulture)}:{r.GetString(2)}" },
                    ResourceType = type switch { "TM" => "TM (table)", "TX" => "TX (transaction)", "UL" => "UL (user lock)", _ => type },
                    Database = owner,
                    Object = name != null ? $"{owner}.{name}" : obj is { } id ? $"object {id.ToString(CultureInfo.InvariantCulture)}" : null,
                    ObjectNote = name == null && obj != null ? "not visible to this account (ALL_OBJECTS)" : type == "TX" && request > 0 ? "the row this session waits for" : null,
                    Mode = OracleActivity.LockMode(request > 0 ? request : L(4)),
                    Status = request > 0 ? "waiting" : "granted",
                    Count = 1,
                });
            }
            return total > list.Count ? PaneResult<LockRow[]>.Truncated(list.ToArray(), total) : PaneResult<LockRow[]>.Ok(list.ToArray());
        }
        catch (OracleException ex) { return PaneResult<LockRow[]>.Error(ex.Message); }
    }

    public Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection,
        IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(string Database, long ObjectId), string?>>(new Dictionary<(string, long), string?>());

    /// <summary>V$SQLSTATS, one entry per SQL_ID and plan; grouped by SQL_ID. A full read every time (an entry gone was aged out).</summary>
    public async Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct)
    {
        if (!probe.SqlStats) return PaneResult<TopQueryRead>.Degraded(Reason(MonitorPane.TopQueries));
        await using var cmd = Command(connection, """
            SELECT SQL_ID, PLAN_HASH_VALUE, EXECUTIONS, CPU_TIME / 1000, ELAPSED_TIME / 1000, BUFFER_GETS, ROWS_PROCESSED, USER_IO_WAIT_TIME / 1000
            FROM V$SQLSTATS WHERE EXECUTIONS > 0
            """);
        cmd.FetchSize = 1024 * 1024;
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<TopQueryCounter>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var sqlId = r.GetString(0);
                var key = sqlId + ":" + Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture);
                double Dbl(int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
                long Lng(int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
                list.Add(new TopQueryCounter(TopQueryKey.Of(key), sqlId, null, sqlId, Lng(2), Dbl(3), Dbl(4), Lng(5), null, Lng(6), Dbl(7)));
            }
            return PaneResult<TopQueryRead>.Ok(new TopQueryRead(list.ToArray(), true, null), "Logical reads are buffer gets; I/O is user I/O wait time.");
        }
        catch (OracleException ex) { return PaneResult<TopQueryRead>.Error(ex.Message); }
    }

    public async Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct)
    {
        var result = new Dictionary<string, TopQueryText>();
        var valid = textKeys.Where(k => k.Length == 13 && k.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z')).Distinct().ToList();
        foreach (var chunk in valid.Chunk(100))
        {
            await using var cmd = (OracleCommand)connection.CreateCommand();
            cmd.BindByName = true;
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                names.Add(":k" + i.ToString(CultureInfo.InvariantCulture));
                cmd.Parameters.Add(new OracleParameter("k" + i.ToString(CultureInfo.InvariantCulture), OracleDbType.Varchar2) { Value = chunk[i] });
            }
            cmd.CommandText = $"SELECT SQL_ID, SQL_FULLTEXT FROM V$SQLSTATS WHERE SQL_ID IN ({string.Join(", ", names)})";
            await using var r = (OracleDataReader)await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = r.GetString(0);
                if (result.ContainsKey(id)) continue;
                result[id] = new TopQueryText(r.IsDBNull(1) ? null : ReadClob(r, 1, OracleActivitySessionLimits.TopTextChars).Text, null);
            }
        }
        return result;
    }

    /// <summary>At most <paramref name="max"/> characters of a CLOB column, and whether it was longer.</summary>
    static (string Text, bool Truncated) ReadClob(OracleDataReader r, int ordinal, int max)
    {
        using var clob = r.GetOracleClob(ordinal);
        var buffer = new char[(int)Math.Min(max, Math.Max(0, clob.Length / 2 + 1))];
        var sb = new StringBuilder();
        int n;
        while (sb.Length < max && (n = clob.Read(buffer, 0, Math.Min(buffer.Length, max - sb.Length))) > 0) sb.Append(buffer, 0, n);
        return (sb.ToString(), clob.Length / 2 > sb.Length);
    }

    public Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false) =>
        Task.FromResult(PaneResult<DeadlockEvent[]>.Degraded(Reason(MonitorPane.Deadlocks)));

    public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) => Task.FromResult<string?>(null);

    static KillResult Refused(string code, string why) => new() { Outcome = KillOutcome.Refused, Code = code, Message = why };

    /// <summary>The session that still has this exact identity (SID, SERIAL#, LOGON_TIME), a user session.</summary>
    const string Identity = "s.SID = :sid AND s.SERIAL# = :serial AND s.LOGON_TIME = TO_DATE(:logon, 'YYYYMMDDHH24MISS') AND s.TYPE = 'USER'";

    /// <summary>
    /// Terminate: the KILL statement is put together on the server from the numbers of the V$SESSION row that still matches the
    /// identity; nothing from the request is spliced into it, and a session that ended or whose SID was reused finds no row.
    /// </summary>
    internal const string KillSql = $"""
        DECLARE
          v_sid NUMBER;
          v_serial NUMBER;
        BEGIN
          SELECT s.SID, s.SERIAL# INTO v_sid, v_serial FROM V$SESSION s WHERE {Identity};
          EXECUTE IMMEDIATE 'ALTER SYSTEM KILL SESSION ''' || TO_CHAR(v_sid, 'TM9') || ',' || TO_CHAR(v_serial, 'TM9') || ''' IMMEDIATE';
          :p_done := 1;
        EXCEPTION WHEN NO_DATA_FOUND THEN :p_done := 0;
        END;
        """;

    /// <summary>
    /// Cancel: only while the same execution of the same statement runs (SQL_ID and SQL_EXEC_ID as the dialog showed them);
    /// ALTER SYSTEM CANCEL SQL names the session and the SQL_ID the server just reported.
    /// </summary>
    internal const string CancelSql = $"""
        DECLARE
          v_sid NUMBER;
          v_serial NUMBER;
          v_sql VARCHAR2(13);
        BEGIN
          SELECT s.SID, s.SERIAL#, s.SQL_ID INTO v_sid, v_serial, v_sql FROM V$SESSION s
          WHERE {Identity} AND s.STATUS = 'ACTIVE' AND s.SQL_ID = :sqlid AND s.SQL_EXEC_ID = :execid;
          EXECUTE IMMEDIATE 'ALTER SYSTEM CANCEL SQL ''' || TO_CHAR(v_sid, 'TM9') || ', ' || TO_CHAR(v_serial, 'TM9') || ', ' || v_sql || '''';
          :p_done := 1;
        EXCEPTION WHEN NO_DATA_FOUND THEN :p_done := 0;
        END;
        """;

    /// <summary>The identity's binds; false (nothing bound) when it is not "SERIAL#:YYYYMMDDHH24MISS".</summary>
    internal static bool BindIdentity(OracleCommand cmd, SessionKey key)
    {
        var colon = key.Instance.IndexOf(':');
        if (key.Id <= 0 || !new OracleActivity().IsValidInstance(key.Instance)
            || !long.TryParse(key.Instance.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var serial)
            || !DateTime.TryParseExact(key.Instance[(colon + 1)..], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        cmd.Parameters.Add(new OracleParameter("sid", OracleDbType.Int64) { Value = key.Id });
        cmd.Parameters.Add(new OracleParameter("serial", OracleDbType.Int64) { Value = serial });
        cmd.Parameters.Add(new OracleParameter("logon", OracleDbType.Varchar2) { Value = key.Instance[(colon + 1)..] });
        return true;
    }

    public async Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct)
    {
        if (!caps.CanCancelRequest) return Refused("unsupported", caps.CancelReason ?? "Cancelling is not available here.");
        if (statementStart == null || !new OracleActivity().IsValidStatementStart(statementStart))
            return Refused("invalid", "Cancelling needs the running statement shown in the dialog.");
        await using var cmd = Command(connection, CancelSql);
        if (!BindIdentity(cmd, key)) return Refused("invalid", "The session identity is not valid.");
        cmd.Parameters.Add(new OracleParameter("sqlid", OracleDbType.Varchar2) { Value = statementStart[..13] });
        cmd.Parameters.Add(new OracleParameter("execid", OracleDbType.Decimal) { Value = decimal.Parse(statementStart[14..], NumberStyles.None, CultureInfo.InvariantCulture) });
        return await RunAsync(connection, cmd, key, cancel: true, ct).ConfigureAwait(false);
    }

    public async Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        if (!caps.CanTerminate) return Refused("unsupported", caps.TerminateReason ?? "Terminating is not available here.");
        await using var cmd = Command(connection, KillSql);
        if (!BindIdentity(cmd, key)) return Refused("invalid", "The session identity is not valid.");
        return await RunAsync(connection, cmd, key, cancel: false, ct).ConfigureAwait(false);
    }

    async Task<KillResult> RunAsync(DbConnection connection, OracleCommand cmd, SessionKey key, bool cancel, CancellationToken ct)
    {
        var done = new OracleParameter("p_done", OracleDbType.Int32, ParameterDirection.Output);
        cmd.Parameters.Add(done);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (OracleActivity.Num(done) == 1)
                return cancel
                    ? new KillResult { Outcome = KillOutcome.Done, Message = $"Cancel sent to session {key.Id}; it stays connected and its statement ends with ORA-01013." }
                    : new KillResult { Outcome = KillOutcome.Done, Message = $"Session {key.Id} was terminated; the server rolls back its open transaction." };
        }
        catch (OracleException ex) when (OracleErrors.Code(ex) == 1031)
        {
            return Refused("denied", OracleActivity.NoAlterSystem);
        }
        catch (OracleException ex) when (OracleErrors.Code(ex) == 30) // the session ended between the check and the statement
        {
            return new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended; nothing was sent." };
        }
        catch (OracleException ex) when (!cancel && OracleErrors.Code(ex) == 31)
        {
            // ORA-00031: marked for kill — the server ends it as soon as the session can be interrupted.
            return new KillResult { Outcome = KillOutcome.Done, Message = $"Session {key.Id} is marked for kill; the server ends it and rolls back its transaction as soon as it can." };
        }
        return await WhyNotAsync(connection, key, ct).ConfigureAwait(false);
    }

    async Task<KillResult> WhyNotAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = Command(connection, $"SELECT s.STATUS, s.SQL_ID FROM V$SESSION s WHERE {Identity}");
        if (!BindIdentity(cmd, key)) return Refused("invalid", "The session identity is not valid.");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended or its SID now belongs to another session; nothing was sent." };
        return !r.IsDBNull(0) && r.GetString(0) == "ACTIVE" && !r.IsDBNull(1)
            ? new KillResult { Outcome = KillOutcome.StatementFinished, Code = "statementFinished", Message = "That statement finished; the session is running another one. Nothing was sent." }
            : new KillResult { Outcome = KillOutcome.NoRunningQuery, Code = "noRunningQuery", Message = "The session is not running a statement; nothing was sent." };
    }

    /// <summary>A killed session shows STATUS = 'KILLED' in its own row (<see cref="ActivityRow.RollingBack"/>); no progress figure.</summary>
    public Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct) => Task.FromResult<RollbackStatus?>(null);

    /// <summary>What a terminate would undo: the session's transaction (undo records and blocks, V$TRANSACTION).</summary>
    public async Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = Command(connection, $"""
            SELECT s.STATUS, CASE WHEN s.TADDR IS NULL THEN 0 ELSE 1 END,
                   {(probe.Transactions ? "ROUND((SYSDATE - t.START_DATE) * 86400000), t.USED_UREC, t.USED_UBLK" : "NULL, NULL, NULL")}
            FROM V$SESSION s {(probe.Transactions ? "LEFT JOIN V$TRANSACTION t ON t.ADDR = s.TADDR" : "")}
            WHERE {Identity}
            """);
        if (!BindIdentity(cmd, key)) return new KillPreview { Key = key, SessionEnded = true, Risk = RollbackRisk.None };
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new KillPreview { Key = key, SessionEnded = true, Risk = RollbackRisk.None };
        long? L(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
        var killed = !r.IsDBNull(0) && r.GetString(0) == "KILLED";
        var inTx = L(1) == 1;
        var (txMs, records, blocks) = (L(2), L(3), L(4));
        if (!inTx) return new KillPreview { Key = key, Risk = RollbackRisk.None, RollingBack = killed, TxCount = 0, HasWrites = false, Reason = "No open transaction." };
        if (!probe.Transactions)
            return new KillPreview { Key = key, Risk = RollbackRisk.Unknown, RollingBack = killed, TxCount = 1, Reason = "the transaction's undo needs SELECT on V_$TRANSACTION" };
        long? bytes = blocks is { } b && probe.BlockSize is { } size ? b * size : null;
        return new KillPreview
        {
            Key = key, RollingBack = killed, TxCount = 1, TxElapsedMs = txMs, HasWrites = records > 0, LogBytesUsed = bytes,
            Risk = bytes == null && records > 0 ? RollbackRisk.Unknown : RollbackRisk.Low,
            Reason = records > 0
                ? $"An open transaction with {records:N0} undo record(s){(bytes is { } u ? $" ({u / 1024:N0} KB of undo)" : "")}; terminating rolls them back."
                : "An open transaction that has not changed anything yet.",
        };
    }

    public async Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = Command(connection, $"""
            SELECT s.STATUS, CASE WHEN s.TADDR IS NULL THEN 0 ELSE 1 END, {(probe.Transactions ? "ROUND((SYSDATE - t.START_DATE) * 86400000)" : "NULL")},
                   {(probe.SqlText ? "(SELECT q.SQL_FULLTEXT FROM V$SQL q WHERE q.SQL_ID = NVL(s.SQL_ID, s.PREV_SQL_ID) AND q.CHILD_NUMBER = NVL(s.SQL_CHILD_NUMBER, s.PREV_CHILD_NUMBER) AND ROWNUM = 1)" : "NULL")}
            FROM V$SESSION s {(probe.Transactions ? "LEFT JOIN V$TRANSACTION t ON t.ADDR = s.TADDR" : "")}
            WHERE {Identity}
            """);
        if (!BindIdentity(cmd, key)) return new RequestDetail { Key = key, TextNote = "The session identity is not valid." };
        await using var r = (OracleDataReader)await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new RequestDetail { Key = key, TextNote = "The session ended." };
        var active = !r.IsDBNull(0) && r.GetString(0) == "ACTIVE";
        var inTx = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) == 1;
        var (text, truncated) = r.IsDBNull(3) ? (null, false) : ReadClob(r, 3, DetailChars);
        return new RequestDetail
        {
            Key = key, Text = text, TextTruncated = truncated,
            TextNote = !probe.SqlText ? Reason(MonitorPane.Sessions) : text == null ? "The session has not run a statement the server still holds." : active ? null : IdleText,
            TxElapsedMs = inTx && !r.IsDBNull(2) ? Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture) : null,
            OpenTx = inTx ? 1 : 0, PlanReason = caps.LivePlanReason,
        };
    }
}

static class OracleActivitySessionLimits
{
    /// <summary>V$SQL.SQL_TEXT, a VARCHAR2(1000): the session list's preview.</summary>
    public const int TextChars = 1000;
    public const int TopTextChars = 65_536;
}

static class SpanDigits
{
    public static bool ContainsOnlyDigits(this ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (!char.IsAsciiDigit(c)) return false;
        return s.Length > 0;
    }
}
