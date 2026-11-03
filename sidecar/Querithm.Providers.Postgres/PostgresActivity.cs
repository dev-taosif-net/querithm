using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Postgres;

/// <summary>
/// Activity monitor for PostgreSQL (Phase B). Session identity = (pid, backend_start in µs since 2000-01-01 UTC),
/// bound back as timestamptz (B0 row 1). Catalog shapes are probed by view and column, never by version string.
/// </summary>
public sealed class PostgresActivity(IDataEditDialect quoting) : IActivityProvider
{
    public const int MinVersionNum = 120000;
    static readonly long PgEpochTicks = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    static readonly ConcurrentDictionary<string, bool> WaitStartColumn = new();

    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pid, backend_start FROM pg_stat_activity WHERE pid = pg_backend_pid()";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.IsDBNull(1))
                throw new IdentityUnavailableException("The server did not return this connection's own row in pg_stat_activity.");
            return new SessionKey { Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Instance = FormatMicros(r.GetDateTime(1)) };
        }
        // 42501 insufficient privilege; 42P01 / 42703 / 42883 a compatible server without the view, column or function.
        // A terminated backend (57P01) or a lost connection is none of these and fails the open as before (K9).
        catch (PostgresException ex) when (ex.SqlState is "42501" or "42P01" or "42703" or "42883")
        {
            throw new IdentityUnavailableException($"The server refused this connection's read of its own row in pg_stat_activity ({ex.SqlState}).", ex);
        }
    }

    public string ConnectionToken(DbConnection connection) =>
        connection is NpgsqlConnection n ? n.ProcessID.ToString(CultureInfo.InvariantCulture) : connection.GetHashCode().ToString(CultureInfo.InvariantCulture);

    // Autocommit always: the monitor never opens a transaction, so it never holds back vacuum's xmin (M2).
    public string MonitorSessionSetupSql => "SET statement_timeout = '5s'; SET lock_timeout = '2s'; SET application_name = 'Querithm Monitor';";

    /// <summary>The banner check runs on a pool connection, which has no command timeout of its own.</summary>
    internal const int BannerCommandTimeoutS = 5;

    public void PrepareMonitorConnection(DbConnection connection) { } // Npgsql always throws PostgresException

    // 28P01 invalid password, 28000 invalid authorization (no pg_hba entry, unknown role). Not 57P03 (starting up) or 53300
    // (too many connections), which a retry outlasts.
    public bool CredentialsRejected(Exception ex) => ex is PostgresException { SqlState: "28P01" or "28000" };

    // ---- identity format: µs since 2000-01-01 UTC ----

    public static string FormatMicros(DateTime utc) =>
        ((utc.ToUniversalTime().Ticks - PgEpochTicks) / 10).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseMicros(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(text) || text.Length > 20) return false;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var us)) return false;
        if (us > (DateTime.MaxValue.Ticks - PgEpochTicks) / 10) return false;
        utc = new DateTime(PgEpochTicks + us * 10, DateTimeKind.Utc);
        return true;
    }

    public bool IsValidInstance(string instance) => TryParseMicros(instance, out _);
    public string WaitCategory(string waitType) { var (t, e) = PostgresWaits.Split(waitType); return PostgresWaits.Category(t, e); }
    public bool IsBenignWait(string waitType) { var (t, e) = PostgresWaits.Split(waitType); return waitType != "CPU" && PostgresWaits.IsBenign(t, e); }
    public bool IsValidStatementStart(string statementStart) => TryParseMicros(statementStart, out _);

    internal static NpgsqlParameter Ts(string name, string micros) =>
        TryParseMicros(micros, out var v) ? new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = v } : throw new ArgumentException("Invalid session identity.");

    internal static NpgsqlParameter Pid(SessionKey key) =>
        key.Id is >= 1 and <= int.MaxValue ? new NpgsqlParameter("pid", NpgsqlDbType.Integer) { Value = (int)key.Id } : throw new ArgumentOutOfRangeException(nameof(key), "A PostgreSQL pid is a positive integer.");

    // ---- probe ----

    /// <summary>Raw probe values; <see cref="Capabilities"/> turns them into panes and reasons (pure, unit-tested with fake values).</summary>
    public sealed record Probe(int VersionNum, string Version, bool Superuser, bool ReadAllStats, bool SignalBackend, bool Standby,
        bool TrackIoTiming, int TrackActivityQuerySize, string? ComputeQueryId, bool HasStatIo, bool StatIoOpBytes, bool HasCheckpointer,
        bool HasQueryId, bool HasWaitStart, string? PssSchema, string? PssUnavailable, string[] PssColumns, bool PssInfo,
        string? PwsSchema, string? PwsUnavailable, int? PwsPeriodMs, string? Login, string? Database);

    const string ProbeSql = """
        SELECT current_setting('server_version_num')::int, version(), r.rolsuper,
               pg_has_role(current_user, 'pg_read_all_stats', 'USAGE'), pg_has_role(current_user, 'pg_signal_backend', 'USAGE'),
               pg_is_in_recovery(), current_setting('track_io_timing') = 'on',
               pg_size_bytes(current_setting('track_activity_query_size'))::int, current_setting('compute_query_id', true),
               to_regclass('pg_catalog.pg_stat_io') IS NOT NULL,
               EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('pg_catalog.pg_stat_io') AND attname = 'op_bytes'),
               to_regclass('pg_catalog.pg_stat_checkpointer') IS NOT NULL,
               EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'pg_catalog.pg_stat_activity'::regclass AND attname = 'query_id'),
               EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'pg_catalog.pg_locks'::regclass AND attname = 'waitstart'),
               (SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_stat_statements'),
               (SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pg_wait_sampling'),
               current_setting('pg_wait_sampling.profile_period', true), current_user::text, current_database()
        FROM pg_roles r WHERE r.rolname = current_user
        """;

    public async Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        Probe p;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = ProbeSql;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            bool B(int i) => !r.IsDBNull(i) && r.GetBoolean(i);
            p = new Probe(r.GetInt32(0), r.GetString(1), B(2), B(3), B(4), B(5), B(6), r.GetInt32(7), S(8), B(9), B(10), B(11), B(12), B(13),
                S(14), null, [], false, S(15), null, int.TryParse(S(16), NumberStyles.None, CultureInfo.InvariantCulture, out var pp) ? pp : null, S(17), S(18));
        }
        if (p.PssSchema != null)
        {
            var q = quoting.Quote(p.PssSchema);
            var cols = new List<string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT attname::text FROM pg_attribute WHERE attrelid = to_regclass(@rel) AND attnum > 0 AND NOT attisdropped";
                cmd.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = q + ".pg_stat_statements" });
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false)) cols.Add(r.GetString(0));
            }
            var info = await ExistsAsync(connection, q + ".pg_stat_statements_info", ct).ConfigureAwait(false);
            // LIMIT 1, not LIMIT 0: with LIMIT 0 the executor never calls the function, so "not loaded" (55000) is never raised.
            var unavailable = await TryReadAsync(connection, $"SELECT 1 FROM {q}.pg_stat_statements(false) LIMIT 1", ct).ConfigureAwait(false) switch
            {
                null => null,
                "55000" => "pg_stat_statements is installed but not loaded (add it to shared_preload_libraries and restart).",
                var state => $"pg_stat_statements cannot be read ({state}).",
            };
            p = p with { PssColumns = [.. cols], PssInfo = info, PssUnavailable = unavailable };
        }
        if (p.PwsSchema != null)
        {
            var q = quoting.Quote(p.PwsSchema);
            if (await TryReadAsync(connection, $"SELECT 1 FROM {q}.pg_wait_sampling_profile LIMIT 1", ct).ConfigureAwait(false) is { } state)
                p = p with { PwsUnavailable = state == "55000" || state.StartsWith("XX", StringComparison.Ordinal)
                    ? "pg_wait_sampling is installed but not loaded (shared_preload_libraries)" : $"pg_wait_sampling cannot be read ({state})" };
        }
        var self = await IdentityAsync(connection, ct).ConfigureAwait(false);
        return new PostgresActivitySession(p, Capabilities(p), self, quoting);
    }

    static async Task<bool> ExistsAsync(DbConnection c, string rel, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@rel) IS NOT NULL";
        cmd.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Text) { Value = rel });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
    }

    /// <summary>Runs a probe statement; null when it worked, else the SQLSTATE.</summary>
    static async Task<string?> TryReadAsync(DbConnection c, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (PostgresException ex) { return ex.SqlState; }
    }

    public static bool SeesOthers(Probe p) => p.Superuser || p.ReadAllStats;

    /// <summary>Waits source: server-profile (pg_wait_sampling), tick-sampled (from session rows), or none (own sessions only).</summary>
    public static string? WaitSource(Probe p) =>
        p.PwsSchema != null && p.PwsUnavailable == null ? "server-profile" : SeesOthers(p) ? "tick-sampled" : null;

    public static MonitorCapabilities Capabilities(Probe p)
    {
        var seesOthers = SeesOthers(p);
        var waits = WaitSource(p);
        PaneCapability Pane(MonitorPane pane, bool ok, string? reason) => new() { Pane = pane, Available = ok, Reason = reason };
        var pssReason = p.PssSchema == null
            ? $"The pg_stat_statements extension is not installed in database '{p.Database}' (CREATE EXTENSION pg_stat_statements)."
            : p.PssUnavailable;
        var queryHashReason = !p.HasQueryId ? "Query ids need PostgreSQL 14 or later."
            : p.ComputeQueryId == "off" || (p.ComputeQueryId == "auto" && (p.PssSchema == null || p.PssUnavailable != null))
                ? "Query id unavailable (compute_query_id is off, or auto without pg_stat_statements loaded)." : null;
        return new MonitorCapabilities
        {
            ServerVersion = p.Version.Split(" on ")[0],
            Platform = p.Standby ? "hot standby (read-only replica)" : null,
            AllSessionsVisible = seesOthers,
            SessionsReason = seesOthers ? null : "Other roles' query text, waits and session start time require pg_read_all_stats; their sessions cannot be cancelled or terminated from here.",
            CanTerminate = true,
            CanCancelRequest = true,
            TerminateReason = p.Superuser ? null : p.SignalBackend
                ? "Superuser sessions can only be signalled by a superuser."
                : "Only this role's own sessions can be cancelled or terminated (other roles require pg_signal_backend).",
            CancelReason = null,
            LivePlans = false,
            LivePlanReason = "PostgreSQL cannot show the plan of a running query; EXPLAIN the text in a new tab.",
            QueryHashReason = queryHashReason,
            Panes =
            [
                Pane(MonitorPane.Sessions, true, seesOthers ? null : "Other roles' details are hidden (requires pg_read_all_stats)."),
                Pane(MonitorPane.Locks, true, null),
                Pane(MonitorPane.Waits, waits != null, waits switch
                {
                    "server-profile" => null,
                    "tick-sampled" => (p.PwsUnavailable ?? "pg_wait_sampling is not installed") + ": waits are estimated from one sample per interval (low resolution). Install pg_wait_sampling for in-server 10 ms sampling.",
                    _ => "Only this role's sessions are visible (requires pg_read_all_stats), so server waits cannot be estimated.",
                }),
                Pane(MonitorPane.Metrics, true, seesOthers ? "PostgreSQL does not report CPU from SQL." : "Connection counts cover visible sessions only (requires pg_read_all_stats)."),
                Pane(MonitorPane.Io, p.HasStatIo, p.HasStatIo
                    ? "Cluster-wide by backend type, object and context (pg_stat_io has no database or file); the database filter does not apply." + (p.TrackIoTiming ? "" : " Stall times require track_io_timing.")
                    : "Per-backend I/O statistics (pg_stat_io) need PostgreSQL 16 or later; block reads per database are in the overview."),
                Pane(MonitorPane.TopQueries, pssReason == null, pssReason),
                Pane(MonitorPane.Deadlocks, false, "PostgreSQL keeps no deadlock history; enable log_lock_waits and read the server log. The deadlocks counter is shown in the overview."),
            ],
            Login = p.Login,
            OtherLoginsReason = p.Superuser || p.SignalBackend ? null : "Cancelling or terminating another role's session requires pg_signal_backend.",
        };
    }

    public static string? StartRefusal(Probe p) => p.VersionNum < MinVersionNum ? "The activity monitor needs PostgreSQL 12 or later." : null;

    // ---- own-tab blocking banner ----

    public async Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return ([], null);
        var key = connection.ServerVersion;
        if (!WaitStartColumn.TryGetValue(key, out var hasWaitStart))
        {
            await using var c = connection.CreateCommand();
            c.CommandTimeout = BannerCommandTimeoutS;
            c.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'pg_catalog.pg_locks'::regclass AND attname = 'waitstart')";
            WaitStartColumn[key] = hasWaitStart = await c.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        var since = hasWaitStart ? "coalesce((SELECT min(l.waitstart) FROM pg_locks l WHERE l.pid = a.pid AND NOT l.granted), a.query_start)" : "a.query_start";
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = BannerCommandTimeoutS; // a pool connection has none
        cmd.CommandText = $"""
            SELECT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) OR pg_has_role(current_user, 'pg_read_all_stats', 'USAGE');
            SELECT b.pid::bigint, count(*)::int, max((extract(epoch FROM clock_timestamp() - {since}) * 1000))::bigint
            FROM pg_stat_activity a CROSS JOIN LATERAL unnest(pg_blocking_pids(a.pid)) AS b(pid)
            WHERE a.wait_event_type = 'Lock' AND b.pid = ANY(@ids)
            GROUP BY b.pid
            """;
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ids.Select(i => checked((int)i)).ToArray() });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        if (!r.GetBoolean(0)) return (null, "Seeing other roles' sessions requires pg_read_all_stats.");
        await r.NextResultAsync(ct).ConfigureAwait(false);
        var rows = new List<BlockedBySummary>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) rows.Add(new BlockedBySummary(r.GetInt64(0), r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt64(2)));
        return (rows.ToArray(), null);
    }
}

/// <summary>Per-monitor-connection state: probe results and the pg_stat_statements text cache.</summary>
sealed class PostgresActivitySession(PostgresActivity.Probe probe, MonitorCapabilities caps, SessionKey self, IDataEditDialect quoting) : IActivitySession
{
    /// <summary>Normalised statement text per (userid, dbid, queryid): the query file is read only for keys not seen yet (T8).</summary>
    readonly Dictionary<string, string?> _pssTexts = new();
    readonly LinkedList<string> _pssOrder = new();
    const int PssCacheMax = 5_000;

    /// <summary>A pg_wait_sampling read slower than this, three times running, switches to tick-sampled waits.</summary>
    internal const long ProfileReadLimitMs = 50;
    MonitorCapabilities _caps = caps;
    int _slowProfileReads;
    string? _profileTooCostly;

    public MonitorCapabilities Capabilities => _caps;
    public string? StartRefusal => PostgresActivity.StartRefusal(probe);
    public SessionKey Self => self;
    public bool WaitsFromSessions => PostgresActivity.WaitSource(probe) == "tick-sampled" || _profileTooCostly != null;

    /// <summary>
    /// The profile keeps (pid, event, query) entries for every backend that ever existed when profile_pid = on, so its
    /// read grows with connection churn, not with the sessions now open (B0 row 16). Never reset it from here (X15).
    /// </summary>
    void TrackProfileRead(long ms)
    {
        _slowProfileReads = ms > ProfileReadLimitMs ? _slowProfileReads + 1 : 0;
        if (_slowProfileReads < 3 || _profileTooCostly != null) return;
        _profileTooCostly = $"Reading the pg_wait_sampling profile took {ms} ms (it keeps every past backend when pg_wait_sampling.profile_pid = on); " +
            "waits are sampled from the sessions each tick instead (low resolution) until the monitor reconnects. " +
            "Resetting the profile (an administrator's decision) or setting profile_pid = off makes it cheap again.";
        _caps = _caps with { Panes = [.. _caps.Panes.Select(p => p.Pane == MonitorPane.Waits ? p with { Reason = _profileTooCostly } : p)] };
    }
    public int ProfilePeriodMs => probe.PwsPeriodMs ?? 10;
    public PostgresActivity.Probe Probe => probe;

    bool Available(MonitorPane pane) => _caps.Panes.FirstOrDefault(p => p.Pane == pane)?.Available == true;
    string Reason(MonitorPane pane) => _caps.Panes.FirstOrDefault(p => p.Pane == pane)?.Reason ?? "not available";

    string SessionsSql => $"""
        WITH a AS (
          SELECT a.*, CASE WHEN a.wait_event_type = 'Lock' THEN pg_blocking_pids(a.pid) END AS blockers
          FROM pg_stat_activity a WHERE (@db::text IS NULL OR a.datname = @db)
        ), b AS (SELECT DISTINCT unnest(blockers) AS pid FROM a WHERE blockers IS NOT NULL)
        SELECT a.pid, a.backend_start, a.usename::text, a.client_hostname, host(a.client_addr) AS client_addr, a.application_name, a.datname::text,
               a.state, a.backend_type, a.wait_event_type, a.wait_event, a.blockers, left(a.query, 4096) AS query, octet_length(a.query) AS query_bytes,
               {(probe.HasQueryId ? "a.query_id" : "NULL::bigint")} AS query_id,
               CASE WHEN a.state = 'active' THEN (extract(epoch FROM clock_timestamp() - a.query_start) * 1000)::bigint END AS elapsed_ms,
               (extract(epoch FROM clock_timestamp() - a.xact_start) * 1000)::bigint AS tx_ms,
               (extract(epoch FROM clock_timestamp() - a.state_change) * 1000)::bigint AS state_ms,
               CASE WHEN a.state = 'active' THEN a.query_start END AS query_start,
               count(*) OVER ()::int AS total
        FROM a LEFT JOIN b ON b.pid = a.pid
        ORDER BY CASE WHEN cardinality(a.blockers) > 0 THEN 0 WHEN b.pid IS NOT NULL THEN 1 WHEN a.state = 'active' THEN 2 ELSE 3 END, a.pid
        LIMIT @max;
        """;

    string WaitsSql => $"SELECT event_type, event, sum(count)::bigint FROM {quoting.Quote(probe.PwsSchema!)}.pg_wait_sampling_profile GROUP BY 1, 2;";

    string MetricsSql => $"""
        SELECT sum(xact_commit + xact_rollback)::bigint, sum(blks_hit)::bigint, sum(blks_read)::bigint, sum(temp_bytes)::bigint, sum(deadlocks)::bigint,
               max(stats_reset)::text FROM pg_stat_database WHERE (@db::text IS NULL OR datname = @db);
        SELECT count(*) FILTER (WHERE state = 'active' AND backend_type = 'client backend')::bigint,
               count(*) FILTER (WHERE state IN ('idle in transaction', 'idle in transaction (aborted)'))::bigint FROM pg_stat_activity;
        {(probe.HasCheckpointer
            ? "SELECT (num_timed + num_requested)::bigint, stats_reset::text FROM pg_stat_checkpointer;"
            : "SELECT (checkpoints_timed + checkpoints_req)::bigint, stats_reset::text FROM pg_stat_bgwriter;")}
        """;

    string IoSql => probe.StatIoOpBytes
        ? "SELECT backend_type, object, context, (coalesce(reads, 0) * op_bytes)::bigint, (coalesce(writes, 0) * op_bytes)::bigint, read_time, write_time FROM pg_stat_io WHERE reads IS NOT NULL OR writes IS NOT NULL;"
        : "SELECT backend_type, object, context, coalesce(read_bytes, 0)::bigint, coalesce(write_bytes, 0)::bigint, read_time, write_time FROM pg_stat_io WHERE read_bytes IS NOT NULL OR write_bytes IS NOT NULL;";

    public async Task<TickResult> TickAsync(DbConnection connection, TickRequest req, CancellationToken ct)
    {
        var sessions = req.Sessions || (req.Waits && WaitsFromSessions);
        var waitsProfile = req.Waits && Available(MonitorPane.Waits) && !WaitsFromSessions;
        var io = req.Io && Available(MonitorPane.Io);
        try
        {
            var main = await BatchAsync(connection, req, sessions, false, req.Metrics, io, ct).ConfigureAwait(false);
            if (!waitsProfile) return main;
            // The profile read alone, timed: its cost grows with past backends (TrackProfileRead).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PaneResult<WaitCounter[]>? waits;
            try { waits = (await BatchAsync(connection, req, false, true, false, false, ct).ConfigureAwait(false)).Waits; }
            catch (Exception e) when (e is not OperationCanceledException && connection.State == ConnectionState.Open) { waits = PaneResult<WaitCounter[]>.Error(Describe(e)); }
            TrackProfileRead(sw.ElapsedMilliseconds);
            return main with { Waits = waits };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && connection.State == ConnectionState.Open)
        {
            // Isolate the failing pane; the others still answer this tick.
            async Task<(TickResult? R, string? Error)> One(bool s, bool w, bool m, bool i)
            {
                try { return (await BatchAsync(connection, req, s, w, m, i, ct).ConfigureAwait(false), null); }
                catch (Exception e) when (e is not OperationCanceledException) { return (null, Describe(e)); }
            }
            var (s1, se) = sessions ? await One(true, false, false, false).ConfigureAwait(false) : (null, null);
            var (w1, we) = waitsProfile ? await One(false, true, false, false).ConfigureAwait(false) : (null, null);
            var (m1, me) = req.Metrics ? await One(false, false, true, false).ConfigureAwait(false) : (null, null);
            var (i1, ie) = io ? await One(false, false, false, true).ConfigureAwait(false) : (null, null);
            return new TickResult(
                se != null ? PaneResult<ActivityRow[]>.Error(se) : s1?.Sessions, s1?.SessionTexts,
                we != null ? PaneResult<WaitCounter[]>.Error(we) : w1?.Waits ?? WaitsDegraded(req),
                me != null ? PaneResult<MetricCounter[]>.Error(me) : m1?.Metrics,
                ie != null ? PaneResult<IoCounter[]>.Error(ie) : i1?.Io ?? IoDegraded(req),
                m1?.ResetMarker);
        }
    }

    PaneResult<WaitCounter[]>? WaitsDegraded(TickRequest req) =>
        req.Waits && !Available(MonitorPane.Waits) ? PaneResult<WaitCounter[]>.Degraded(Reason(MonitorPane.Waits)) : null;

    PaneResult<IoCounter[]>? IoDegraded(TickRequest req) =>
        req.Io && !Available(MonitorPane.Io) ? PaneResult<IoCounter[]>.Degraded(Reason(MonitorPane.Io)) : null;

    static string Describe(Exception e) => e is PostgresException pg ? pg.SqlState switch
    {
        "57014" => "The query was cancelled by statement_timeout (the server is busy).",
        "55P03" => "The server's lock timeout was reached.",
        "42501" => "Permission denied (a role was revoked?).",
        _ => pg.MessageText,
    } : e.Message;

    async Task<TickResult> BatchAsync(DbConnection connection, TickRequest req, bool sessions, bool waits, bool metrics, bool io, CancellationToken ct)
    {
        var sql = new StringBuilder();
        if (sessions) sql.Append(SessionsSql);
        if (waits) sql.Append(WaitsSql);
        if (metrics) sql.Append(MetricsSql);
        if (io) sql.Append(IoSql);
        if (sql.Length == 0) return new TickResult(null, null, WaitsDegraded(req), null, IoDegraded(req), null);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql.ToString();
        // The scheduler bounds the job, statement_timeout each statement; the connection's command timeout is the driver backstop.
        if (sessions || metrics) cmd.Parameters.Add(new NpgsqlParameter("db", NpgsqlDbType.Text) { Value = (object?)req.DatabaseFilter ?? DBNull.Value });
        if (sessions) cmd.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = req.MaxSessions + 1 });
        PaneResult<ActivityRow[]>? sessionsResult = null;
        Dictionary<string, string?>? texts = null;
        PaneResult<WaitCounter[]>? waitsResult = null;
        PaneResult<MetricCounter[]>? metricsResult = null;
        PaneResult<IoCounter[]>? ioResult = null;
        string? marker = null;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var first = true;
        async Task Next() { if (!first) await r.NextResultAsync(ct).ConfigureAwait(false); first = false; }
        if (sessions)
        {
            await Next().ConfigureAwait(false);
            (sessionsResult, texts) = await ReadSessionsAsync(r, req.MaxSessions, probe.TrackActivityQuerySize, ct).ConfigureAwait(false);
        }
        if (waits)
        {
            await Next().ConfigureAwait(false);
            var list = new List<WaitCounter>();
            var period = ProfilePeriodMs;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var type = r.IsDBNull(0) ? null : r.GetString(0);
                var ev = r.IsDBNull(1) ? null : r.GetString(1);
                if (PostgresWaits.IsBenign(type, ev)) continue;
                var samples = r.GetInt64(2);
                list.Add(new WaitCounter(PostgresWaits.Name(type, ev), PostgresWaits.Category(type, ev), samples * period, samples, null));
            }
            waitsResult = PaneResult<WaitCounter[]>.Ok(list.ToArray());
        }
        if (metrics)
        {
            await Next().ConfigureAwait(false);
            var m = new List<MetricCounter>();
            string? resetA = null, resetB = null;
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
                m.Add(new MetricCounter("tps", D(0), true, "perSec"));
                m.Add(new MetricCounter("blksHit", D(1), true, "count"));
                m.Add(new MetricCounter("blksRead", D(2), true, "count"));
                m.Add(new MetricCounter("tempBytes", D(3), true, "bytesPerSec"));
                m.Add(new MetricCounter("deadlocks", D(4), true, "count"));
                resetA = r.IsDBNull(5) ? null : r.GetString(5);
            }
            await r.NextResultAsync(ct).ConfigureAwait(false);
            var note = PostgresActivity.SeesOthers(probe) ? null : "visible sessions only (requires pg_read_all_stats)";
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                m.Add(new MetricCounter("activeConnections", r.GetInt64(0), false, "count", note));
                m.Add(new MetricCounter("idleInTx", r.GetInt64(1), false, "count", note));
            }
            await r.NextResultAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                m.Add(new MetricCounter("checkpoints", r.IsDBNull(0) ? null : r.GetInt64(0), true, "perMin"));
                resetB = r.IsDBNull(1) ? null : r.GetString(1);
            }
            m.Add(new MetricCounter("cpu", null, false, "percent", "PostgreSQL does not report CPU from SQL"));
            metricsResult = PaneResult<MetricCounter[]>.Ok(m.ToArray());
            marker = $"{resetA}|{resetB}";
        }
        if (io)
        {
            await Next().ConfigureAwait(false);
            var list = new List<IoCounter>();
            var timing = probe.TrackIoTiming;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                // read_time / write_time are 0, not NULL, when track_io_timing is off: never shown as a measured zero.
                long? T(int i) => !timing || r.IsDBNull(i) ? null : (long)Math.Round(r.GetDouble(i));
                list.Add(new IoCounter("cluster", null, null, r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), T(5), T(6)));
            }
            ioResult = PaneResult<IoCounter[]>.Ok(list.ToArray(), Reason(MonitorPane.Io));
        }
        return new TickResult(sessionsResult, texts, waitsResult ?? WaitsDegraded(req), metricsResult, ioResult ?? IoDegraded(req), marker);
    }

    internal static async Task<(PaneResult<ActivityRow[]>, Dictionary<string, string?>)> ReadSessionsAsync(DbDataReader r, int max, int trackActivityQuerySize, CancellationToken ct)
    {
        var rows = new List<ActivityRow>();
        var texts = new Dictionary<string, string?>();
        var total = 0;
        int O(string n) => r.GetOrdinal(n);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            total = r.GetInt32(O("total"));
            if (rows.Count >= max) continue;
            string? S(string n) { var i = O(n); return r.IsDBNull(i) ? null : r.GetString(i); }
            long? L(string n) { var i = O(n); return r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
            var pid = Convert.ToInt64(r.GetValue(O("pid")), CultureInfo.InvariantCulture);
            var bsOrd = O("backend_start");
            var hidden = r.IsDBNull(bsOrd);
            var key = new SessionKey { Id = pid, Instance = hidden ? "" : PostgresActivity.FormatMicros(r.GetDateTime(bsOrd)) };
            var query = S("query");
            string? note = null;
            if (query == "<insufficient privilege>") { query = null; note = "hidden (requires pg_read_all_stats)"; }
            else if (query != null && L("query_bytes") is { } qb && qb >= trackActivityQuerySize - 1)
                note = $"truncated by server setting (track_activity_query_size = {trackActivityQuerySize} bytes)";
            var textKey = $"p{pid}";
            if (!string.IsNullOrEmpty(query)) texts[textKey] = query;
            var state = S("state");
            var backendType = S("backend_type");
            var blockersOrd = O("blockers");
            long[] blockedBy = r.IsDBNull(blockersOrd) ? [] : ((int[])r.GetValue(blockersOrd)).Where(b => b != pid).Select(b => (long)b).Distinct().Order().ToArray();
            var qsOrd = O("query_start");
            var waitType = S("wait_event_type");
            rows.Add(new ActivityRow
            {
                Key = key,
                Login = S("usename"),
                Host = S("client_hostname") ?? S("client_addr"),
                Program = string.IsNullOrEmpty(S("application_name")) ? null : S("application_name"),
                Database = S("datname"),
                Status = state ?? (hidden ? "hidden" : backendType ?? "unknown"),
                Command = null,
                BackendType = backendType,
                IsUserSession = backendType == "client backend",
                WaitType = waitType == null ? null : PostgresWaits.Name(waitType, S("wait_event")),
                BlockedBy = blockedBy,
                TextId = string.IsNullOrEmpty(query) ? null : textKey,
                TextNote = note,
                QueryHash = L("query_id") is { } q and not 0 ? q.ToString(CultureInfo.InvariantCulture) : null,
                ElapsedMs = L("elapsed_ms"),
                TxElapsedMs = L("tx_ms"),
                StateChangeMs = L("state_ms"),
                StatementStart = r.IsDBNull(qsOrd) ? null : PostgresActivity.FormatMicros(r.GetDateTime(qsOrd)),
                OpenTx = hidden ? null : L("tx_ms") != null ? 1 : 0,
                IdentityHidden = hidden ? "requires pg_read_all_stats" : null,
            });
        }
        return (total > max ? PaneResult<ActivityRow[]>.Truncated(rows.ToArray(), total) : PaneResult<ActivityRow[]>.Ok(rows.ToArray()), texts);
    }

    // ---- locks (B2): pg_class is consulted only for the connected database and shared catalogs (LK1, LK2) ----

    public async Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct)
    {
        const string sql = """
            WITH me AS (SELECT oid FROM pg_database WHERE datname = current_database())
            SELECT l.pid, a.backend_start, l.locktype, d.datname::text,
                   CASE WHEN l.relation IS NOT NULL AND (l.database = (SELECT oid FROM me) OR l.database = 0) THEN n.nspname || '.' || c.relname END AS relname,
                   l.relation::bigint, l.mode, l.granted, count(*)::bigint, count(*) OVER ()::int, (l.database = 0) AS shared
            FROM pg_locks l
            LEFT JOIN pg_stat_activity a ON a.pid = l.pid
            LEFT JOIN pg_database d ON d.oid = l.database
            LEFT JOIN pg_class c ON c.oid = l.relation AND (l.database = (SELECT oid FROM me) OR l.database = 0)
            LEFT JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE l.pid IS NOT NULL AND l.locktype <> 'virtualxid'
            GROUP BY l.pid, a.backend_start, l.locktype, d.datname, l.database, n.nspname, c.relname, l.relation, l.mode, l.granted
            ORDER BY l.granted, l.pid
            LIMIT @max;
            """;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = max });
        var rows = new List<LockRow>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            total = r.GetInt32(9);
            var db = r.IsDBNull(3) ? null : r.GetString(3);
            string? obj = null, note = null;
            if (!r.IsDBNull(4)) obj = r.GetString(4);
            else if (!r.IsDBNull(5))
            {
                obj = $"{db ?? "shared"}.{r.GetInt64(5).ToString(CultureInfo.InvariantCulture)}";
                note = "other database";
            }
            rows.Add(new LockRow
            {
                Key = new SessionKey { Id = r.GetInt32(0), Instance = r.IsDBNull(1) ? "" : PostgresActivity.FormatMicros(r.GetDateTime(1)) },
                ResourceType = r.GetString(2), Database = db, Object = obj, ObjectNote = note,
                Mode = r.GetString(6), Status = r.GetBoolean(7) ? "granted" : "waiting", Count = r.GetInt64(8),
            });
        }
        return total > rows.Count ? PaneResult<LockRow[]>.Truncated(rows.ToArray(), total) : PaneResult<LockRow[]>.Ok(rows.ToArray());
    }

    public Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection,
        IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct) =>
        // Never opens a connection to another database (connection budget); names come from the metadata cache only.
        Task.FromResult<IReadOnlyDictionary<(string, long), string?>>(new Dictionary<(string, long), string?>());

    // ---- top queries (B4): pg_stat_statements(showtext := false) every run ----

    string Col(params string[] names) => names.FirstOrDefault(n => probe.PssColumns.Contains(n)) ?? "NULL";

    public async Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct)
    {
        if (!Available(MonitorPane.TopQueries)) return PaneResult<TopQueryRead>.Degraded(Reason(MonitorPane.TopQueries));
        var q = quoting.Quote(probe.PssSchema!);
        var exec = Col("total_exec_time", "total_time");
        var readTime = Col("shared_blk_read_time", "blk_read_time");
        var writeTime = Col("shared_blk_write_time", "blk_write_time");
        var toplevel = probe.PssColumns.Contains("toplevel") ? "s.toplevel::text" : "'t'";
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.userid::text || ':' || s.dbid::text || ':' || s.queryid::text || ':' || {toplevel}, s.queryid::text, d.datname::text,
                   s.calls::bigint, {exec}::float8, s.rows::bigint, (s.shared_blks_hit + s.shared_blks_read)::bigint, ({readTime} + {writeTime})::float8,
                   s.userid::text || ':' || s.dbid::text || ':' || s.queryid::text
            FROM {q}.pg_stat_statements(false) s LEFT JOIN pg_database d ON d.oid = s.dbid
            WHERE s.queryid IS NOT NULL;
            {(probe.PssInfo ? $"SELECT stats_reset::text FROM {q}.pg_stat_statements_info;" : "SELECT NULL::text;")}
            """;
        var list = new List<TopQueryCounter>();
        string? marker = null;
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var timing = probe.TrackIoTiming;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new TopQueryCounter(TopQueryKey.Of(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(8),
                    r.GetInt64(3), null, r.GetDouble(4), null, r.GetInt64(6), r.GetInt64(5), timing && !r.IsDBNull(7) ? r.GetDouble(7) : null));
            await r.NextResultAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0)) marker = r.GetString(0);
        }
        return PaneResult<TopQueryRead>.Ok(new TopQueryRead(list.ToArray(), true, marker));
    }

    public async Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct)
    {
        var missing = textKeys.Where(k => !_pssTexts.ContainsKey(k)).Distinct().ToArray();
        if (missing.Length > 0 && probe.PssSchema != null)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT s.userid::text || ':' || s.dbid::text || ':' || s.queryid::text, left(s.query, 4096)
                FROM {quoting.Quote(probe.PssSchema)}.pg_stat_statements(true) s
                WHERE (s.userid::text || ':' || s.dbid::text || ':' || s.queryid::text) = ANY(@keys);
                """;
            cmd.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = missing });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var k = r.GetString(0);
                var text = r.IsDBNull(1) ? null : r.GetString(1);
                if (text == "<insufficient privilege>") text = null;
                if (_pssTexts.TryAdd(k, text)) _pssOrder.AddLast(k);
            }
            foreach (var k in missing) if (_pssTexts.TryAdd(k, null)) _pssOrder.AddLast(k);
            while (_pssOrder.Count > PssCacheMax) { _pssTexts.Remove(_pssOrder.First!.Value); _pssOrder.RemoveFirst(); }
        }
        return textKeys.Distinct().ToDictionary(k => k, k => new TopQueryText(_pssTexts.GetValueOrDefault(k), null));
    }

    /// <summary>For tests: how many statement texts the session holds.</summary>
    internal int CachedTexts => _pssTexts.Count;

    public Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false) =>
        Task.FromResult(PaneResult<DeadlockEvent[]>.Degraded(Reason(MonitorPane.Deadlocks)));

    public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) => Task.FromResult<string?>(null);

    // ---- cancel / terminate: identity re-checked in the same statement as the signal (B1c) ----

    internal const string CancelSql = "SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE pid = @pid AND backend_start = @start AND backend_type = 'client backend' AND state = 'active' AND query_start = @qs";
    internal const string TerminateSql = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid = @pid AND backend_start = @start AND backend_type = 'client backend'";

    public async Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct)
    {
        if (statementStart == null || !PostgresActivity.TryParseMicros(statementStart, out _))
            return new KillResult { Outcome = KillOutcome.Refused, Code = "invalid", Message = "Cancelling needs the statement start shown in the dialog." };
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = CancelSql;
        cmd.Parameters.Add(PostgresActivity.Pid(key));
        cmd.Parameters.Add(PostgresActivity.Ts("start", key.Instance));
        cmd.Parameters.Add(PostgresActivity.Ts("qs", statementStart));
        try
        {
            var sent = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (sent is true) return new KillResult { Outcome = KillOutcome.Done, Message = $"Cancel sent to backend {key.Id}; its session stays connected." };
            if (sent is false) return new KillResult { Outcome = KillOutcome.Failed, Code = "notSignalled", Message = "The server did not signal the backend." };
            return await WhyNotAsync(connection, key, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            return Denied(ex);
        }
    }

    public async Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = TerminateSql;
        cmd.Parameters.Add(PostgresActivity.Pid(key));
        cmd.Parameters.Add(PostgresActivity.Ts("start", key.Instance));
        try
        {
            var sent = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return sent switch
            {
                true => new KillResult { Outcome = KillOutcome.Done, Message = $"Backend {key.Id} was terminated; the server rolls back its open work." },
                false => new KillResult { Outcome = KillOutcome.Failed, Code = "notSignalled", Message = "The server did not signal the backend." },
                _ => new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended or its pid was reused; nothing was sent." },
            };
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            return Denied(ex);
        }
    }

    KillResult Denied(PostgresException ex) => new()
    {
        Outcome = KillOutcome.Failed, Code = "permission",
        Message = $"The server refused: {ex.MessageText}. " + (_caps.OtherLoginsReason ?? _caps.TerminateReason ?? ""),
    };

    async Task<KillResult> WhyNotAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        // Nothing was signalled; say why (a read only).
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT state FROM pg_stat_activity WHERE pid = @pid AND backend_start = @start";
        cmd.Parameters.Add(PostgresActivity.Pid(key));
        cmd.Parameters.Add(PostgresActivity.Ts("start", key.Instance));
        var state = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return state switch
        {
            null or DBNull => new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session ended or its pid was reused; nothing was sent." },
            "active" => new KillResult { Outcome = KillOutcome.StatementFinished, Code = "statementFinished", Message = "The statement already finished (a newer one is running); nothing was sent." },
            _ => new KillResult { Outcome = KillOutcome.NoRunningQuery, Code = "noRunningQuery", Message = "The session is not running a query; nothing was sent." },
        };
    }

    public Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct) => Task.FromResult<RollbackStatus?>(null);

    public async Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (extract(epoch FROM clock_timestamp() - xact_start) * 1000)::bigint, backend_xid IS NOT NULL, xact_start IS NOT NULL
            FROM pg_stat_activity WHERE pid = @pid AND backend_start = @start
            """;
        cmd.Parameters.Add(PostgresActivity.Pid(key));
        cmd.Parameters.Add(PostgresActivity.Ts("start", key.Instance));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new KillPreview { Key = key, SessionEnded = true, Reason = "The session ended." };
        var inTx = r.GetBoolean(2);
        return new KillPreview
        {
            Key = key,
            TxElapsedMs = r.IsDBNull(0) ? null : r.GetInt64(0),
            TxCount = inTx ? 1 : 0,
            HasWrites = inTx ? r.GetBoolean(1) : null,
        };
    }

    public async Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT query, octet_length(query), state, (extract(epoch FROM clock_timestamp() - xact_start) * 1000)::bigint
            FROM pg_stat_activity WHERE pid = @pid AND backend_start = @start
            """;
        cmd.Parameters.Add(PostgresActivity.Pid(key));
        cmd.Parameters.Add(PostgresActivity.Ts("start", key.Instance));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        const string planReason = "PostgreSQL has no plan for a running query; EXPLAIN this text in a new tab (it is never run automatically).";
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) return new RequestDetail { Key = key, TextNote = "The session ended.", PlanReason = planReason };
        var text = r.IsDBNull(0) ? null : r.GetString(0);
        var hidden = text == "<insufficient privilege>";
        var truncated = !hidden && !r.IsDBNull(1) && r.GetInt32(1) >= probe.TrackActivityQuerySize - 1;
        return new RequestDetail
        {
            Key = key,
            Text = hidden ? null : text,
            TextTruncated = truncated,
            TextNote = hidden ? "hidden (requires pg_read_all_stats)" : truncated ? $"truncated by server setting (track_activity_query_size = {probe.TrackActivityQuerySize} bytes)" : null,
            PlanReason = planReason,
            TxElapsedMs = r.IsDBNull(3) ? null : r.GetInt64(3),
            OpenTx = r.IsDBNull(3) ? 0 : 1,
        };
    }
}
