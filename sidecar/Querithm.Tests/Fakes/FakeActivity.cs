using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Tests.Fakes;

/// <summary>
/// An activity provider over <see cref="FakeDbConnection"/>s: identity = (spid, "1:spid"); every pane answers from
/// settable fields; every call is recorded (and runs a command on the connection, so overlap is detected).
/// </summary>
public sealed class FakeActivity : IActivityProvider
{
    public ConcurrentQueue<string> Calls { get; } = new();
    public Func<long[]>? BlockedBy { get; set; }
    public Func<DbConnection, CancellationToken, Task>? OnIdentity { get; set; }
    public FakeActivitySession? Session { get; private set; }
    public Func<FakeActivitySession>? NewSession { get; set; }
    public (BlockedBySummary[]? Rows, string? Unavailable) BlockedByResult { get; set; } = ([], null);
    public int BlockedByCalls;

    public static string Inst(long spid) => $"1:{spid.ToString(CultureInfo.InvariantCulture)}";

    public async Task<SessionKey> IdentityAsync(DbConnection connection, CancellationToken ct)
    {
        if (OnIdentity != null) await OnIdentity(connection, ct);
        await Run(connection, "identity", ct);
        var spid = ((FakeDbConnection)connection).Spid;
        return new SessionKey { Id = spid, Instance = Inst(spid) };
    }

    public string ConnectionToken(DbConnection connection) => ((FakeDbConnection)connection).Spid.ToString(CultureInfo.InvariantCulture);
    public string MonitorSessionSetupSql => "SET FAKE MONITOR SETTINGS";
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbConnection, object> _prepared = new();
    /// <summary>Was this connection passed to <see cref="PrepareMonitorConnection"/>? (weak: L13 counts leaks)</summary>
    public bool WasPrepared(DbConnection connection) => _prepared.TryGetValue(connection, out _);
    public void PrepareMonitorConnection(DbConnection connection) => _prepared.AddOrUpdate(connection, true);
    /// <summary>The fake's sign-in rejection (a real provider maps its driver's error codes).</summary>
    public sealed class LoginRejectedException() : Exception("Login failed for the fake login.");
    public bool CredentialsRejected(Exception ex) => ex is LoginRejectedException;
    public string WaitCategory(string waitType) => waitType.StartsWith("LCK", StringComparison.Ordinal) ? "Lock" : "Other";
    public bool IsBenignWait(string waitType) => waitType == "SLEEP_TASK";
    public bool IsValidInstance(string instance) => instance.StartsWith("1:", StringComparison.Ordinal) && long.TryParse(instance.AsSpan(2), out _);
    public bool IsValidStatementStart(string statementStart) => long.TryParse(statementStart, out _);

    public Task<IActivitySession> ProbeAsync(DbConnection connection, CancellationToken ct)
    {
        var spid = ((FakeDbConnection)connection).Spid;
        Session = NewSession?.Invoke() ?? new FakeActivitySession();
        Session.Self = new SessionKey { Id = spid, Instance = Inst(spid) };
        Session.Owner = this;
        return Task.FromResult<IActivitySession>(Session);
    }

    public async Task<(BlockedBySummary[]? Rows, string? Unavailable)> BlockedByAsync(DbConnection connection, long[] ids, CancellationToken ct)
    {
        Interlocked.Increment(ref BlockedByCalls);
        await Run(connection, "blockedBy " + string.Join(",", ids), ct);
        return BlockedByResult;
    }

    internal async Task Run(DbConnection c, string what, CancellationToken ct)
    {
        Calls.Enqueue(what);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "FAKE " + what;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class FakeActivitySession : IActivitySession
{
    internal FakeActivity Owner = null!;
    public MonitorCapabilities Capabilities { get; set; } = new()
    {
        ServerVersion = "Fake 1", AllSessionsVisible = true, CanTerminate = true, CanCancelRequest = false, CancelReason = "fake engine cannot cancel",
        Panes = Enum.GetValues<MonitorPane>().Select(p => new PaneCapability { Pane = p, Available = true }).ToArray(),
    };
    public string? StartRefusal { get; set; }
    public SessionKey Self { get; set; } = null!;
    public bool WaitsFromSessions { get; set; }

    /// <summary>Rows returned by the next ticks (the monitor's own row is added).</summary>
    public List<ActivityRow> Rows { get; } = [];
    public Func<TickRequest, CancellationToken, Task>? OnTick { get; set; }
    public Func<SessionKey, CancellationToken, Task<KillResult>>? OnTerminate { get; set; }
    public KillPreview? Preview { get; set; }
    /// <summary>Runs inside the kill preview (something that happens while a kill waits on the server).</summary>
    public Func<SessionKey, CancellationToken, Task>? OnPreview { get; set; }
    public int Ticks;
    public int Terminates;
    /// <summary>A pane-level failure, as a provider reports it after isolating the failing query.</summary>
    public string? WaitsError { get; set; }
    /// <summary>Rows the locks job returns.</summary>
    public LockRow[] LockRows { get; set; } = [];
    /// <summary>Runs inside the locks job (e.g. a query that never returns).</summary>
    public Func<CancellationToken, Task>? OnLocks { get; set; }
    /// <summary>Events the deadlock poll returns.</summary>
    public List<DeadlockEvent> DeadlockEvents { get; } = [];

    public async Task<TickResult> TickAsync(DbConnection connection, TickRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Ticks);
        await Owner.Run(connection, "tick", ct);
        if (OnTick != null) await OnTick(request, ct);
        var rows = Rows.Append(new ActivityRow { Key = Self, Status = "running", BlockedBy = [], IsUserSession = true }).ToArray();
        return new TickResult(
            request.Sessions ? PaneResult<ActivityRow[]>.Ok(rows) : null, new Dictionary<string, string?>(),
            request.Waits ? WaitsError != null ? PaneResult<WaitCounter[]>.Error(WaitsError) : PaneResult<WaitCounter[]>.Ok([new WaitCounter("LCK_M_X", "Lock", 100L * Ticks, Ticks, 0)]) : null,
            request.Metrics ? PaneResult<MetricCounter[]>.Ok([new MetricCounter("batchRequests", 10.0 * Ticks, true, "perSec")]) : null,
            request.Io ? PaneResult<IoCounter[]>.Ok([new IoCounter("file", "db", "f", null, null, null, 1000L * Ticks, 500L * Ticks, Ticks, Ticks)]) : null,
            "start-1");
    }

    public async Task<PaneResult<LockRow[]>> LocksAsync(DbConnection connection, int max, CancellationToken ct)
    {
        await Owner.Run(connection, "locks", ct);
        if (OnLocks != null) await OnLocks(ct);
        return PaneResult<LockRow[]>.Ok(LockRows);
    }

    public Task<IReadOnlyDictionary<(string Database, long ObjectId), string?>> ResolveObjectNamesAsync(DbConnection connection, IReadOnlyList<(string Database, long ObjectId)> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<(string, long), string?>>(new Dictionary<(string, long), string?>());

    public async Task<PaneResult<TopQueryRead>> TopQueriesAsync(DbConnection connection, CancellationToken ct)
    {
        await Owner.Run(connection, "top", ct);
        return PaneResult<TopQueryRead>.Ok(new TopQueryRead([], true, null));
    }

    public Task<IReadOnlyDictionary<string, TopQueryText>> TopQueryTextsAsync(DbConnection connection, IReadOnlyList<string> textKeys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, TopQueryText>>(new Dictionary<string, TopQueryText>());

    public List<bool> DeadlockReads { get; } = [];

    public async Task<PaneResult<DeadlockEvent[]>> DeadlocksAsync(DbConnection connection, long fromMs, long toMs, int max, CancellationToken ct, bool recentOnly = false)
    {
        lock (DeadlockReads) DeadlockReads.Add(recentOnly);
        await Owner.Run(connection, "deadlocks", ct);
        var events = DeadlockEvents.Where(e => e.At >= fromMs && e.At <= toMs).OrderByDescending(e => e.At).ToList();
        return events.Count > max ? PaneResult<DeadlockEvent[]>.Truncated([.. events.Take(max)], events.Count) : PaneResult<DeadlockEvent[]>.Ok([.. events]);
    }

    public Task<string?> DeadlockXmlAsync(DbConnection connection, string eventId, long fromMs, long toMs, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<KillResult> CancelRequestAsync(DbConnection connection, SessionKey key, string? statementStart, CancellationToken ct) =>
        Task.FromResult(new KillResult { Outcome = KillOutcome.Refused, Code = "unsupported", Message = "fake" });

    public async Task<KillResult> TerminateSessionAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        Interlocked.Increment(ref Terminates);
        await Owner.Run(connection, $"terminate {key.Id}", ct);
        return OnTerminate != null ? await OnTerminate(key, ct) : new KillResult { Outcome = KillOutcome.Done, Message = "terminated" };
    }

    public Task<RollbackStatus?> RollbackStatusAsync(DbConnection connection, SessionKey key, CancellationToken ct) => Task.FromResult<RollbackStatus?>(null);

    public async Task<KillPreview> KillPreviewAsync(DbConnection connection, SessionKey key, CancellationToken ct)
    {
        await Owner.Run(connection, $"preview {key.Id}", ct);
        if (OnPreview != null) await OnPreview(key, ct);
        return Preview ?? new KillPreview { Key = key, TxCount = 0 };
    }

    public Task<RequestDetail> RequestDetailAsync(DbConnection connection, SessionKey key, CancellationToken ct) =>
        Task.FromResult(new RequestDetail { Key = key, Text = "SELECT 1" });
}
