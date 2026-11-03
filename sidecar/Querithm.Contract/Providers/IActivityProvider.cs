using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>
/// The server refused a connection's read of its own session identity, and the connection itself works. Such a connection
/// is kept for queries; the monitor cannot tell its server session from a foreign one, so kills are refused while it is
/// open (ARCHITECTURE §6 rule 7, §16).
/// </summary>
public sealed class IdentityUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Server activity for the monitor (Phase B). Exposed as <see cref="IDbProvider.Activity"/>; null degrades the whole
/// monitor with a reason. Everything the monitor sends is built from what the server reported: no value is invented.
/// </summary>
public interface IActivityProvider
{
    /// <summary>
    /// The connection's own session key. Reads the connection's own row, which every login can normally see. Throws
    /// <see cref="IdentityUnavailableException"/> when the server refuses that read on a working connection (an explicit
    /// DENY, a compatible server without the view or column, no own row returned); a connection failure throws as usual.
    /// </summary>
    Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct);

    /// <summary>
    /// A token that changes whenever the driver reconnected underneath the same connection object (SqlClient connection
    /// resiliency: ClientConnectionId; PG: the backend pid). No I/O. A change means the identity must be read again.
    /// </summary>
    string ConnectionToken(DbConnection connection);

    /// <summary>Statements that make the monitor connection unable to block, be blocked for long, or hold back cleanup.</summary>
    string MonitorSessionSetupSql { get; }

    /// <summary>
    /// Client-side settings for a monitor or kill control connection, applied right after it opens (no I/O). SQL Server:
    /// server errors must throw — query tabs report user errors as messages, which would make a denied DMV read or a
    /// refused KILL look like an empty, successful result.
    /// </summary>
    void PrepareMonitorConnection(DbConnection connection);

    /// <summary>
    /// True when the server rejected the sign-in itself (wrong or expired password, unknown login): retrying with the same
    /// remembered secret cannot succeed and repeated failures can lock the account. Not for a database that is
    /// unavailable or failing over, which a retry can outlast.
    /// </summary>
    bool CredentialsRejected(Exception ex);

    /// <summary>The monitor category of a wait type as this provider names it in rows and counters.</summary>
    string WaitCategory(string waitType);

    /// <summary>Idle / background waits excluded from the waits pane.</summary>
    bool IsBenignWait(string waitType);

    /// <summary>Validates an opaque <see cref="SessionKey.Instance"/> (≤ 64 chars, this provider's own format) at the RPC boundary.</summary>
    bool IsValidInstance(string instance);

    /// <summary>Validates an opaque <see cref="ActivityRow.StatementStart"/>.</summary>
    bool IsValidStatementStart(string statementStart);

    /// <summary>
    /// Probes roles, views, columns and extensions (never a version string or a platform name) and returns the
    /// per-connection state every pane query uses. Runs at start and after every reconnect.
    /// </summary>
    Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct);

    /// <summary>
    /// Own-tab blocking banner (B1b): sessions blocked by any of <paramref name="ids"/>, per blocker.
    /// Null when this login cannot see other sessions (the reason is <paramref name="unavailable"/>).
    /// </summary>
    Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct);
}

/// <summary>One blocker's victims, for the banner.</summary>
public sealed record BlockedBySummary(long BlockerId, int BlockedCount, long MaxWaitMs);

/// <summary>A pane's rows, or why it has none. Degraded and error panes carry their reason; values are never filled in.</summary>
public sealed record PaneResult<T>(T? Value, PaneState State, string? Reason = null, int? Total = null)
{
    public static PaneResult<T> Ok(T value, string? note = null) => new(value, PaneState.Ok, note);
    public static PaneResult<T> Truncated(T value, int total, string? note = null) => new(value, PaneState.Truncated, note, total);
    public static PaneResult<T> Degraded(string reason) => new(default, PaneState.Degraded, reason);
    public static PaneResult<T> Error(string message) => new(default, PaneState.Error, message);
}

/// <summary>A cumulative wait counter as the server reports it.</summary>
public sealed record WaitCounter(string WaitType, string Category, long WaitMs, long? Tasks, long? SignalMs);

/// <summary>
/// One overview counter. Cumulative counters are turned into rates by the sidecar's delta tracker; point values pass
/// through. A null value means unavailable (<paramref name="Note"/> says why).
/// </summary>
public sealed record MetricCounter(string Key, double? Value, bool Cumulative, string Unit, string? Note = null);

/// <summary>A cumulative I/O counter. Scope "file" (SQL Server) or "cluster" (PG pg_stat_io).</summary>
public sealed record IoCounter(string Scope, string? Database, string? File, string? BackendType, string? Object, string? Context,
    long ReadBytes, long WriteBytes, long? ReadStallMs, long? WriteStallMs)
{
    public string Key => Scope == "file" ? $"f|{Database}|{File}" : $"c|{BackendType}|{Object}|{Context}";
}

/// <summary>
/// One plan-cache / statement-stats entry with cumulative totals. <paramref name="Entry"/> identifies the entry
/// (deltas are per entry, then grouped by <paramref name="QueryHash"/>); <paramref name="TextKey"/> fetches its text.
/// SQL Server leaves QueryHash and TextKey empty for entries that did not run since the previous read's cutoff: their
/// counters cannot have changed, so they never yield a delta, and a full read does not build two strings per cached statement.
/// </summary>
public sealed record TopQueryCounter(UInt128 Entry, string QueryHash, string? Database, string TextKey,
    long Execs, double? CpuMs, double DurationMs, long? LogicalReads, long? SharedBlocks, long? Rows, double? IoMs = null);

/// <summary>
/// Top-query entry identities: 128 bits instead of the server's key strings, because the tracker keeps one per cached
/// statement (a plan handle as hex is 130 characters; the soak's two SQL Server monitors held ~800 B per entry).
/// </summary>
public static class TopQueryKey
{
    /// <summary>The first 16 bytes of SHA-256 over the entry's identifying bytes (SQL Server: plan handle and statement offsets).</summary>
    public static UInt128 Of(ReadOnlySpan<byte> key)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(key, hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(hash);
    }

    /// <summary>The same over a composite key string (PostgreSQL: userid:dbid:queryid:toplevel).</summary>
    public static UInt128 Of(string key) => Of(System.Runtime.InteropServices.MemoryMarshal.AsBytes(key.AsSpan()));
}

/// <summary>A top query's text (null when encrypted or gone) and the database the server attributes it to.</summary>
public sealed record TopQueryText(string? Text, string? Database);

/// <summary>A top-queries read: full (every entry; missing ones were evicted) or incremental (changed entries only).</summary>
public sealed record TopQueryRead(TopQueryCounter[] Entries, bool Full, string? ResetMarker);

/// <summary>What the main tick asks for. Sessions are read when the Sessions pane is on or waits are tick-sampled.</summary>
public sealed record TickRequest(bool Sessions, bool Waits, bool Metrics, bool Io, int MaxSessions, string? DatabaseFilter = null);

/// <summary>One main tick, collected in a single round trip where the server allows it.</summary>
public sealed record TickResult(
    PaneResult<ActivityRow[]>? Sessions,
    IReadOnlyDictionary<string, string?>? SessionTexts,
    PaneResult<WaitCounter[]>? Waits,
    PaneResult<MetricCounter[]>? Metrics,
    PaneResult<IoCounter[]>? Io,
    string? ResetMarker);

/// <summary>Rollback state of a session this monitor terminated (SQL Server); null where the engine has none.</summary>
public sealed record RollbackStatus(bool InProgress, double? Percent);

/// <summary>Per-connection monitor state returned by <see cref="IActivityProvider.ProbeAsync"/>.</summary>
public interface IActivitySession
{
    MonitorCapabilities Capabilities { get; }

    /// <summary>Set when the monitor must not start (server below the minimum version), with the reason.</summary>
    string? StartRefusal { get; }

    /// <summary>The monitor connection's own session.</summary>
    SessionKey Self { get; }

    /// <summary>PG without an in-server wait profile: waits are derived from the tick's session rows (tier 2).</summary>
    bool WaitsFromSessions { get; }

    /// <summary>
    /// Sessions / waits / metrics / I/O in one batch. <see cref="TickResult.SessionTexts"/> maps each row's provisional
    /// text key to the server's text exactly as returned (the sidecar hashes it into the row's TextId).
    /// </summary>
    Task<TickResult> TickAsync(DbConnection connection, TickRequest request, CancellationToken ct);

    Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct);

    /// <summary>SQL Server: ids not resolved from the metadata cache, looked up under the monitor's LOCK_TIMEOUT.</summary>
    Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection,
        IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct);

    Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Texts of the final top queries, by <see cref="TopQueryCounter.TextKey"/>, read fresh from the server.</summary>
    Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct);

    /// <param name="recentOnly">The periodic poll: what is new since this session's previous poll (SQL Server resumes in the event files).</param>
    Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false);

    /// <summary>The original deadlock report of one event (exported as .xdl); null when no longer in the server's buffers.</summary>
    Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct);

    /// <summary>
    /// Cancels the target's running statement after re-checking identity on the server in the same statement.
    /// Engines that cannot cancel a single request return <see cref="KillOutcome.Refused"/> without sending anything.
    /// </summary>
    Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct);

    /// <summary>Terminates the target session after re-checking identity on the server in the same batch.</summary>
    Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct);

    /// <summary>Rollback progress of a terminated session (identity-guarded); null when the engine reports none.</summary>
    Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct);

    Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct);

    Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct);
}
