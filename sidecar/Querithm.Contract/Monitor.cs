namespace Querithm.Contract;

// Server activity monitor (Phase B): one dedicated connection per monitor, one poll loop, JSON snapshots
// (bounded server status, not query rows — §16). Values the server did not report are null, never zero.

/// <summary>One pane of the Activity tab. QueryStore / Health are reserved for the deferred B8 and refused by monitor.start.</summary>
public enum MonitorPane { Sessions, Locks, Waits, Metrics, Io, TopQueries, Deadlocks, QueryStore, Health }

/// <summary>collecting = first sample of a delta pane (no deltas yet); resetDetected = a counter went backwards this tick.</summary>
public enum PaneState { Ok, Collecting, Truncated, Degraded, Error, ResetDetected }

/// <summary>connected | connecting (first open) | reconnecting (back-off, see RetryInMs) | stopped.</summary>
public enum MonitorConnectionState { Connecting, Connected, Reconnecting, Stopped }

public enum KillMode { CancelRequest, TerminateSession }

/// <summary>done = sent to the server; the other values say why nothing (or nothing more) was sent.</summary>
public enum KillOutcome { Done, Refused, Gone, StatementFinished, NoRunningQuery, RollingBack, Failed }

/// <summary>Kill safety preview (B1c): none = no open transaction; unknown = not readable (treated as high for confirmation).</summary>
public enum RollbackRisk { None, Low, High, Unknown }

public sealed record MonitorStartParams
{
    public required string ProfileId { get; init; }
    /// <summary>1,000–30,000 ms; default 2,000.</summary>
    public int? IntervalMs { get; init; }
    /// <summary>Default: every Phase B pane.</summary>
    public MonitorPane[]? Panes { get; init; }
    /// <summary>Filters rows by database name; never changes the monitor connection's database and is not a security boundary.</summary>
    public string? DatabaseFilter { get; init; }
}

public sealed record MonitorRef
{
    public required string MonitorId { get; init; }
}

public sealed record MonitorStartResult
{
    public required string MonitorId { get; init; }
    public required int IntervalMs { get; init; }
    public required MonitorPane[] Panes { get; init; }
    public Engine Engine { get; init; }
}

public sealed record MonitorSetPanesParams
{
    public required string MonitorId { get; init; }
    public required MonitorPane[] Panes { get; init; }
}

/// <summary>Top queries order (cpu | duration | reads | executions) over the last interval or since the monitor started.</summary>
public sealed record MonitorTopQueriesParams
{
    public required string MonitorId { get; init; }
    public required string OrderBy { get; init; }
    public bool SinceStart { get; init; }
}

public sealed record MonitorSetIntervalParams
{
    public required string MonitorId { get; init; }
    public required int IntervalMs { get; init; }
}

/// <summary>A server session, exact to the server's own precision. Id is what users see (spid / pid); Instance is opaque.</summary>
public sealed record SessionKey
{
    public required long Id { get; init; }
    /// <summary>Provider-defined start identity (SQL Server login_time ticks, PG backend_start µs). Empty when the server hides it.</summary>
    public required string Instance { get; init; }
}

/// <summary>One session (sp_WhoIsActive-level). Every duration is computed on the server clock.</summary>
public sealed record ActivityRow
{
    public required SessionKey Key { get; init; }
    public string? Login { get; init; }
    public string? Host { get; init; }
    public string? Program { get; init; }
    public string? Database { get; init; }
    /// <summary>Server's own words: running / runnable / suspended / sleeping (SQL Server); active / idle / idle in transaction (PG).</summary>
    public required string Status { get; init; }
    public string? Command { get; init; }
    /// <summary>PG backend_type (client backend, autovacuum worker, …); null on SQL Server.</summary>
    public string? BackendType { get; init; }
    /// <summary>SQL Server is_user_process; PG backend_type = 'client backend'. Only user sessions can ever be killed.</summary>
    public bool IsUserSession { get; init; }
    public string? WaitType { get; init; }
    public string? WaitResource { get; init; }
    public long? WaitMs { get; init; }
    /// <summary>Distinct blocking session ids (both engines 0..n); never a negative SQL Server pseudo-id.</summary>
    public required long[] BlockedBy { get; init; }
    /// <summary>SQL Server negative blocker: orphanedDtc (-2) | deferredRecovery (-3) | latchOwnerUnknown (-4).</summary>
    public string? BlockedByNote { get; init; }
    /// <summary>Content hash of the exact text shown (32 hex chars); the text travels once in MonitorSnapshot.Texts.</summary>
    public string? TextId { get; init; }
    /// <summary>Why there is no text: encrypted module, hidden by permission, …</summary>
    public string? TextNote { get; init; }
    /// <summary>Grouping only: SQL Server query_hash / PG query_id. Never used to look up text.</summary>
    public string? QueryHash { get; init; }
    public long? ElapsedMs { get; init; }
    public long? TxElapsedMs { get; init; }
    public long? StateChangeMs { get; init; }
    /// <summary>Opaque exact start of the running statement (PG query_start µs); required to cancel on PG.</summary>
    public string? StatementStart { get; init; }
    public long? CpuMs { get; init; }
    public long? Reads { get; init; }
    public long? Writes { get; init; }
    public long? LogicalReads { get; init; }
    public long? RowCount { get; init; }
    public int? OpenTx { get; init; }
    /// <summary>BACKUP / DBCC / index rebuild progress, as the server reports it.</summary>
    public double? PercentComplete { get; init; }
    /// <summary>null = not Querithm's | pool | dedicated | monitor | control (from the connection registry, at snapshot time).</summary>
    public string? QuerithmKind { get; init; }
    /// <summary>The session currently leasing this pool connection (display only).</summary>
    public string? OwnSessionId { get; init; }
    /// <summary>What a Querithm connection without a session is doing (idle, metadata, trace control, …).</summary>
    public string? QuerithmNote { get; init; }
    /// <summary>Why Key.Instance is empty (PG without pg_read_all_stats). Such a row is never killable.</summary>
    public string? IdentityHidden { get; init; }
    /// <summary>SQL Server: the session is rolling back (a killed request, or one this monitor terminated).</summary>
    public bool RollingBack { get; init; }
    public double? RollbackPercent { get; init; }
    public long? TempdbKb { get; init; }
    public long? GrantedMemoryKb { get; init; }
    public long? RequestedMemoryKb { get; init; }
    public int? Dop { get; init; }
}

/// <summary>Head blockers, cycles and placeholders computed by the sidecar's blocking graph; the tree itself is built from BlockedBy.</summary>
public sealed record BlockingInfo
{
    public required long[] HeadBlockers { get; init; }
    /// <summary>Each cycle's session ids ("deadlock in progress").</summary>
    public required long[][] Cycles { get; init; }
    /// <summary>Blockers that are not rows of this snapshot: SQL Server pseudo-blockers and hidden or truncated sessions.</summary>
    public required BlockingPlaceholder[] Placeholders { get; init; }
    public int BlockedCount { get; init; }
}

public sealed record BlockingPlaceholder
{
    /// <summary>Session id, or the SQL Server pseudo-id (-2, -3, -4).</summary>
    public required long Id { get; init; }
    public required string Reason { get; init; }
    public bool IsSession { get; init; }
}

public sealed record LockRow
{
    public required SessionKey Key { get; init; }
    public required string ResourceType { get; init; }
    public string? Database { get; init; }
    /// <summary>Resolved object name, else "db.object_id".</summary>
    public string? Object { get; init; }
    /// <summary>name from metadata cache | name locked | other database.</summary>
    public string? ObjectNote { get; init; }
    public required string Mode { get; init; }
    /// <summary>granted | waiting</summary>
    public required string Status { get; init; }
    public long Count { get; init; }
}

public sealed record WaitDelta
{
    public required string Category { get; init; }
    public required string WaitType { get; init; }
    public long WaitMsDelta { get; init; }
    /// <summary>Waiting tasks (SQL Server) / samples (PG); null where the source does not count them.</summary>
    public long? TasksDelta { get; init; }
    /// <summary>SQL Server signal wait; null on PostgreSQL (no such counter).</summary>
    public long? SignalMsDelta { get; init; }
}

/// <summary>PostgreSQL only: server-profile (pg_wait_sampling, cumulative) or tick-sampled (low-resolution estimate).</summary>
public sealed record WaitCoverage
{
    public required string Source { get; init; }
    public int PeriodMs { get; init; }
    public bool Partial { get; init; }
}

/// <summary>One overview metric. Value null means not available (Reason says why) — never zero.</summary>
public sealed record MetricValue
{
    /// <summary>cpu | batchRequests | compilations | userConnections | ple | tps | cacheHit | activeConnections | idleInTx | tempBytes | deadlocks | checkpoints</summary>
    public required string Key { get; init; }
    public double? Value { get; init; }
    /// <summary>percent | perSec | count | seconds | bytesPerSec | perMin</summary>
    public required string Unit { get; init; }
    /// <summary>Scope or provenance ("SQL Server process", "database CPU, 15 s") or why Value is null.</summary>
    public string? Note { get; init; }
}

public sealed record MetricSample
{
    public required long At { get; init; }
    public required MetricValue[] Values { get; init; }
}

/// <summary>I/O since the previous tick. Scope file (SQL Server, per database file) or cluster (PG pg_stat_io groups).</summary>
public sealed record IoDelta
{
    public required string Scope { get; init; }
    public string? Database { get; init; }
    public string? File { get; init; }
    public string? BackendType { get; init; }
    public string? Object { get; init; }
    public string? Context { get; init; }
    public long ReadBytes { get; init; }
    public long WriteBytes { get; init; }
    /// <summary>Null when the server does not time I/O (PG track_io_timing off).</summary>
    public long? ReadStallMs { get; init; }
    public long? WriteStallMs { get; init; }
}

public sealed record TopQueryRow
{
    public required string QueryHash { get; init; }
    public string? TextId { get; init; }
    public long ExecDelta { get; init; }
    /// <summary>SQL Server worker time; null on PostgreSQL (pg_stat_statements has no CPU time).</summary>
    public long? CpuMsDelta { get; init; }
    public long DurationMsDelta { get; init; }
    /// <summary>SQL Server logical reads (pages); null on PostgreSQL.</summary>
    public long? LogicalReadsDelta { get; init; }
    /// <summary>PostgreSQL shared blocks hit + read; null on SQL Server.</summary>
    public long? SharedBlocksDelta { get; init; }
    public long? RowsDelta { get; init; }
    /// <summary>PostgreSQL block read + write time (requires track_io_timing); null on SQL Server or when not timed.</summary>
    public long? IoTimeMsDelta { get; init; }
    public string? Database { get; init; }
}

public sealed record DeadlockVictim
{
    public required string ProcessId { get; init; }
}

public sealed record DeadlockProcess
{
    public required string Id { get; init; }
    public long? Spid { get; init; }
    public string? Login { get; init; }
    public string? Host { get; init; }
    public string? Program { get; init; }
    public string? Database { get; init; }
    public string? IsolationLevel { get; init; }
    public string? LockMode { get; init; }
    public string? WaitResource { get; init; }
    public long? WaitMs { get; init; }
    /// <summary>4 KB preview of the input buffer / statement.</summary>
    public string? Text { get; init; }
    public bool Victim { get; init; }
}

public sealed record DeadlockResourceOwner
{
    public required string ProcessId { get; init; }
    public string? Mode { get; init; }
    /// <summary>owner | waiter</summary>
    public required string Role { get; init; }
}

public sealed record DeadlockResource
{
    public required string Id { get; init; }
    /// <summary>keylock | pagelock | objectlock | ridlock | exchangeEvent | …</summary>
    public required string Kind { get; init; }
    public string? Database { get; init; }
    public string? Object { get; init; }
    public string? Index { get; init; }
    public required DeadlockResourceOwner[] Owners { get; init; }
}

public sealed record DeadlockEvent
{
    /// <summary>Content hash of the deadlock graph (dedupes ring buffer and event file).</summary>
    public required string Id { get; init; }
    public long At { get; init; }
    public required DeadlockVictim[] Victims { get; init; }
    public required DeadlockProcess[] Processes { get; init; }
    public required DeadlockResource[] Resources { get; init; }
    public string? Database { get; init; }
    /// <summary>The XML was cut off or malformed; what could be read is shown.</summary>
    public bool Partial { get; init; }
    /// <summary>ring buffer | event file</summary>
    public string? Source { get; init; }
}

public sealed record PaneStatus
{
    public MonitorPane Pane { get; init; }
    public PaneState State { get; init; }
    /// <summary>Degraded / error reason, or a note on an ok pane (scope, missing timing).</summary>
    public string? Reason { get; init; }
    /// <summary>For truncated panes: rows before the cap.</summary>
    public int? Total { get; init; }
    /// <summary>Error panes: when the pane is tried again (back-off).</summary>
    public long? RetryInMs { get; init; }
}

public sealed record PaneCapability
{
    public MonitorPane Pane { get; init; }
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

/// <summary>What the monitor can see and do, from probing roles, views, columns and extensions (never version strings).</summary>
public sealed record MonitorCapabilities
{
    public required string ServerVersion { get; init; }
    public required PaneCapability[] Panes { get; init; }
    public bool AllSessionsVisible { get; init; }
    /// <summary>Why only own sessions / hidden details are visible.</summary>
    public string? SessionsReason { get; init; }
    public bool CanTerminate { get; init; }
    public string? TerminateReason { get; init; }
    public bool CanCancelRequest { get; init; }
    public string? CancelReason { get; init; }
    public bool LivePlans { get; init; }
    public string? LivePlanReason { get; init; }
    /// <summary>Why QueryHash is unavailable (compute_query_id off, PG &lt; 14).</summary>
    public string? QueryHashReason { get; init; }
    /// <summary>Wording only (Azure SQL Database, Managed Instance); never decides behaviour.</summary>
    public string? Platform { get; init; }
    /// <summary>The monitor connection's login / role.</summary>
    public string? Login { get; init; }
    /// <summary>Why sessions of other logins cannot be cancelled or terminated (PG without pg_signal_backend); null = they can.</summary>
    public string? OtherLoginsReason { get; init; }
}

/// <summary>Rollback progress of a session this monitor terminated (SQL Server).</summary>
public sealed record RollbackProgress
{
    public required SessionKey Key { get; init; }
    public double? Percent { get; init; }
    public bool Done { get; init; }
}

/// <summary>One tick. Every snapshot carries the full Sessions list; a snapshot with an older Seq is ignored by the UI.</summary>
public sealed record MonitorSnapshot
{
    public required string MonitorId { get; init; }
    /// <summary>Sidecar time of the tick (Unix ms).</summary>
    public long At { get; init; }
    public long Seq { get; init; }
    public ActivityRow[]? Sessions { get; init; }
    /// <summary>TextId → 4 KB preview, only ids not already sent while they stay in the monitor's text store.</summary>
    public Dictionary<string, string>? Texts { get; init; }
    public BlockingInfo? Blocking { get; init; }
    public LockRow[]? Locks { get; init; }
    public WaitDelta[]? Waits { get; init; }
    public WaitCoverage? WaitCoverage { get; init; }
    public MetricSample? Metrics { get; init; }
    public IoDelta[]? Io { get; init; }
    public TopQueryRow[]? TopQueries { get; init; }
    /// <summary>Only events not sent before; the full list comes from monitor.deadlocks.</summary>
    public DeadlockEvent[]? NewDeadlocks { get; init; }
    public required PaneStatus[] Status { get; init; }
    public RollbackProgress[]? Rollbacks { get; init; }
    public long LagMs { get; init; }
    /// <summary>Connections this monitor holds right now (monitor + control during a kill).</summary>
    public int ConnectionCount { get; init; }
    public bool Throttled { get; init; }
    public bool Paused { get; init; }
    public MonitorConnectionState Connection { get; init; }
    public string? ConnectionError { get; init; }
    public long? RetryInMs { get; init; }
    /// <summary>Sent after every (re)probe.</summary>
    public MonitorCapabilities? Capabilities { get; init; }
}

public sealed record MonitorStoppedEvent
{
    public required string MonitorId { get; init; }
    public required string Reason { get; init; }
}

public sealed record MonitorTextsParams
{
    public required string MonitorId { get; init; }
    /// <summary>≤ 500 ids of 32 hex chars.</summary>
    public required string[] Ids { get; init; }
}

public sealed record MonitorTextsResult
{
    public required Dictionary<string, string> Texts { get; init; }
    /// <summary>Ids no longer in the store (ended sessions): the UI shows "text no longer available" and never asks again.</summary>
    public required string[] Missing { get; init; }
}

public sealed record MonitorKillParams
{
    public required string MonitorId { get; init; }
    public required SessionKey Target { get; init; }
    public KillMode Mode { get; init; }
    public bool ConfirmedKill { get; init; }
    public bool ConfirmedProd { get; init; }
    public bool ConfirmedLongRollback { get; init; }
    /// <summary>CancelRequest only: the row's StatementStart as the dialog showed it (required on PG).</summary>
    public string? ExpectedStatementStart { get; init; }
}

public sealed record KillResult
{
    public KillOutcome Outcome { get; init; }
    public required string Message { get; init; }
    /// <summary>Stable refusal code: notConfirmed | prod | notInSnapshot | ownMonitor | systemSession | querithmConnection | querithmReconnecting | querithmUnidentified | unsupported | longRollback | rollingBack | inProgress | disconnected | identityHidden | notASession.</summary>
    public string? Code { get; init; }
    public GuardReason? Guard { get; init; }
    /// <summary>monitor | control | fallback — which connection carried the kill.</summary>
    public string? Path { get; init; }
}

public sealed record MonitorSessionParams
{
    public required string MonitorId { get; init; }
    public required SessionKey Key { get; init; }
}

public sealed record KillPreview
{
    public required SessionKey Key { get; init; }
    public RollbackRisk Risk { get; init; }
    public string? Reason { get; init; }
    public long? LogBytesUsed { get; init; }
    public long? LogBytesReserved { get; init; }
    public long? TxElapsedMs { get; init; }
    public int? TxCount { get; init; }
    public bool RollingBack { get; init; }
    /// <summary>The session with this exact identity no longer exists on the server (ended, or its id was reused).</summary>
    public bool SessionEnded { get; init; }
    /// <summary>PG: backend_xid present (the transaction wrote); null when not reported.</summary>
    public bool? HasWrites { get; init; }
    /// <summary>Sessions waiting on this one in the latest snapshot.</summary>
    public int BlockedCount { get; init; }
    public ActivityRow? Row { get; init; }
    public string? Text { get; init; }
}

public sealed record DiagnosisFinding
{
    /// <summary>blocked | idleBlocker | hiddenBlocker | memoryGrant | parallelSkew | scan | planWarning | idleInTx | waitClass | longTx | waitType | onCpu | sessionEnded</summary>
    public required string Rule { get; init; }
    /// <summary>high | medium | low | info</summary>
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string? NextStep { get; init; }
    public required string[] Evidence { get; init; }
    /// <summary>When the cause is a Querithm tab: its session id (Go to tab).</summary>
    public string? OwnSessionId { get; init; }
    public long? BlockerId { get; init; }
}

public sealed record Diagnosis
{
    public required SessionKey Key { get; init; }
    public required DiagnosisFinding[] Findings { get; init; }
    public required string[] Evidence { get; init; }
    public string[]? Notes { get; init; }
}

public sealed record WaitInfoParams
{
    public Engine Engine { get; init; }
    public required string WaitType { get; init; }
}

public sealed record WaitInfo
{
    public required string WaitType { get; init; }
    public bool Known { get; init; }
    public string? Category { get; init; }
    public string? Meaning { get; init; }
    public string[]? Causes { get; init; }
    public string[]? Check { get; init; }
}

public sealed record DeadlocksParams
{
    public required string MonitorId { get; init; }
    /// <summary>Unix ms; default 24 h before ToMs, at most 7 days.</summary>
    public long? FromMs { get; init; }
    public long? ToMs { get; init; }
}

public sealed record DeadlocksResult
{
    public required DeadlockEvent[] Events { get; init; }
    public required PaneStatus Status { get; init; }
    /// <summary>More than 200 events in the window: the newest 200 are listed.</summary>
    public bool Truncated { get; init; }
}

public sealed record DeadlockXmlParams
{
    public required string MonitorId { get; init; }
    public required string EventId { get; init; }
    /// <summary>The user confirmed the file includes query text.</summary>
    public bool ConfirmedText { get; init; }
}

public sealed record MonitorFileResult
{
    /// <summary>Absolute path under &lt;data dir&gt;/exports.</summary>
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long Bytes { get; init; }
}

public sealed record RequestDetail
{
    public required SessionKey Key { get; init; }
    /// <summary>Full statement batch / query (≤ 1 MB).</summary>
    public string? Text { get; init; }
    public bool TextTruncated { get; init; }
    public string? TextNote { get; init; }
    /// <summary>The running statement within Text (character offsets), where the server reports it.</summary>
    public int? StatementStart { get; init; }
    public int? StatementEnd { get; init; }
    public PlanResult? Plan { get; init; }
    /// <summary>live | cached</summary>
    public string? PlanSource { get; init; }
    public string? PlanReason { get; init; }
    public long? TxElapsedMs { get; init; }
    public int? OpenTx { get; init; }
    public string? IsolationLevel { get; init; }
}

public sealed record HistoryParams
{
    public required string MonitorId { get; init; }
    /// <summary>Reconstruct this tick; null = only list the retained ticks.</summary>
    public long? Seq { get; init; }
}

public sealed record HistoryTick
{
    public long Seq { get; init; }
    public long At { get; init; }
    /// <summary>The monitor was disconnected between the previous retained tick and this one.</summary>
    public bool GapBefore { get; init; }
    public int Sessions { get; init; }
    public int Blocked { get; init; }
}

public sealed record HistoryResult
{
    public required HistoryTick[] Ticks { get; init; }
    public MonitorSnapshot? Snapshot { get; init; }
    public int RetentionMinutes { get; init; }
    public long Bytes { get; init; }
    /// <summary>Oldest ticks were dropped because the memory cap (50 MB) was reached.</summary>
    public bool CapHit { get; init; }
}

public sealed record HistorySettingsParams
{
    public required string MonitorId { get; init; }
    /// <summary>1–60.</summary>
    public required int RetentionMinutes { get; init; }
}

public sealed record MonitorExportParams
{
    public required string MonitorId { get; init; }
    public long? FromSeq { get; init; }
    public long? ToSeq { get; init; }
    /// <summary>User data (§13): query text, login and host are left out unless the user ticked "include query text".</summary>
    public bool IncludeText { get; init; }
}

/// <summary>Own-tab blocking banner (B1b): the session's open transaction blocks others.</summary>
public sealed record SessionBlockingEvent
{
    public required string SessionId { get; init; }
    public int BlockedCount { get; init; }
    /// <summary>Longest wait among the blocked sessions (server clock).</summary>
    public long MaxWaitMs { get; init; }
    /// <summary>monitor | check</summary>
    public required string Source { get; init; }
    /// <summary>Set when blocking cannot be seen on this profile (permission), instead of counts.</summary>
    public string? Unavailable { get; init; }
}
