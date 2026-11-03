using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Sessions;

/// <summary>
/// Where a pooled connection is in its life (ARCHITECTURE §6 "Physical connection lifecycle"). Exactly one owner at a
/// time: the pool (Idle), one borrower (Probing, Leased — a pinned connection is Leased by its session), or nobody
/// (Discarded, terminal). Transitions are compare-and-swap, so a second Return or Discard of the same lease is a no-op.
/// </summary>
public enum ConnLifecycle { Probing, Leased, Idle, Discarded }

public sealed class PhysicalConnection(DbConnection db, Pool pool)
{
    int _state = (int)ConnLifecycle.Probing;

    public DbConnection Db { get; } = db;
    public Pool Pool { get; } = pool;
    public long LastUsedMs { get; set; } = Time.NowMs();
    public bool IsOpen => Db.State == ConnectionState.Open;
    public ConnLifecycle State => (ConnLifecycle)Volatile.Read(ref _state);

    /// <summary>The session leasing (or pinning) this connection; display only (monitor badges), never used for ownership.</summary>
    public string? LeasedBy { get; set; }

    /// <summary>What a borrower without a session uses it for (metadata, data compare, trace control); display only.</summary>
    public string? Purpose { get; set; }

    internal bool Move(ConnLifecycle from, ConnLifecycle to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

    /// <summary>Any live state → Discarded; true only for the caller that made the move.</summary>
    internal bool MoveToDiscarded()
    {
        while (true)
        {
            var s = Volatile.Read(ref _state);
            if (s == (int)ConnLifecycle.Discarded) return false;
            if (Interlocked.CompareExchange(ref _state, (int)ConnLifecycle.Discarded, s) == s) return true;
        }
    }
}

/// <summary>
/// Physical connections for one profile (or profile+database on PostgreSQL), max 8 (§6).
/// Driver pooling is off; this pool keeps connections open so database context and spid are stable.
/// The lock guards bookkeeping only (idle list, waiters, slot count); no database I/O ever runs under it.
/// </summary>
public sealed class Pool(string key, Profile profile, IDbProvider provider, string? database, Func<string?, CancellationToken, Task<DbConnection>> open, int max, ILogger log,
    Func<DbConnection, bool>? transportAlive = null, Func<PhysicalConnection, CancellationToken, Task>? onReuse = null)
{
    readonly object _gate = new();
    readonly List<PhysicalConnection> _idle = [];
    readonly LinkedList<TaskCompletionSource<Grant>> _waiters = new();
    /// <summary>Slots in use: every connection that is Probing, Leased or Idle, plus opens in flight and slots granted to waiters.</summary>
    int _total;
    bool _closed;

    /// <summary>What a queued borrower receives: a connection handed over by Return, or a freed slot (Conn null) to open into.</summary>
    readonly record struct Grant(PhysicalConnection? Conn);

    public string Key { get; } = key;

    /// <summary>Test seam: runs right after a queued borrower wakes up, before it looks at its token.</summary>
    internal Action? AfterWaiterWoken { get; set; }

    /// <summary>Test seam: makes the source that ends a queued wait after maxQueueWait (a timer outside tests).</summary>
    internal Func<TimeSpan, CancellationTokenSource> QueueLimit { get; set; } = q => new CancellationTokenSource(q);

    /// <summary>Test seam: true while the current thread holds the bookkeeping lock.</summary>
    internal bool LockHeldByCurrentThread => System.Threading.Monitor.IsEntered(_gate);

    public Profile Profile { get; } = profile;
    public IDbProvider Provider { get; } = provider;
    public string? Database { get; } = database;
    public string? ServerVersion { get; private set; }
    public int Total { get { lock (_gate) return _total; } }
    public int IdleCount { get { lock (_gate) return _idle.Count; } }
    public int WaiterCount { get { lock (_gate) return _waiters.Count; } }

    /// <summary>Raised (outside the lock) after a physical connection was opened, before anyone borrows it.</summary>
    public event Action<PhysicalConnection>? Opened;

    /// <summary>
    /// Borrow a connection set to <paramref name="db"/> (SQL Server switches with ChangeDatabase). <paramref name="maxQueueWait"/>
    /// bounds only the wait for a full pool (then "poolBusy" and the waiter is removed); opening a connection keeps its own timeout.
    /// </summary>
    public async Task<PhysicalConnection> BorrowAsync(string? db, Action? onQueued, CancellationToken ct, string? purpose = null, TimeSpan? maxQueueWait = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            PhysicalConnection? conn = null;
            TaskCompletionSource<Grant>? waiter = null;
            var create = false;
            lock (_gate)
            {
                if (_closed) throw Closed();
                if (_idle.Count > 0)
                {
                    var i = db == null ? -1 : _idle.FindIndex(c => string.Equals(Provider.CurrentDatabase(c.Db), db, StringComparison.Ordinal));
                    if (i < 0) i = _idle.Count - 1;
                    conn = _idle[i];
                    _idle.RemoveAt(i);
                    conn.Move(ConnLifecycle.Idle, ConnLifecycle.Probing);
                }
                else if (_total < max)
                {
                    _total++;
                    create = true;
                }
                else
                {
                    waiter = new TaskCompletionSource<Grant>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.AddLast(waiter);
                }
            }

            if (waiter != null)
            {
                onQueued?.Invoke();
                Grant grant;
                using var queueLimit = maxQueueWait is { } q ? QueueLimit(q) : null;
                using var linked = queueLimit != null ? CancellationTokenSource.CreateLinkedTokenSource(ct, queueLimit.Token) : null;
                var wct = linked?.Token ?? ct;
                try
                {
                    await using (wct.Register(() => { lock (_gate) _waiters.Remove(waiter); waiter.TrySetCanceled(wct); }))
                        grant = await waiter.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (queueLimit?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                {
                    throw new QuerithmException("poolBusy", "Every connection of this server is in use.");
                }
                AfterWaiterWoken?.Invoke();
                if (wct.IsCancellationRequested)
                {
                    // Granted, then cancelled before resuming: pass the connection or the slot on, never drop it.
                    if (grant.Conn != null) Return(grant.Conn);
                    else ReleaseSlot();
                    if (!ct.IsCancellationRequested) throw new QuerithmException("poolBusy", "Every connection of this server is in use.");
                    ct.ThrowIfCancellationRequested();
                }
                if (grant.Conn == null) create = true; // a freed slot is now ours: open into it
                else
                {
                    conn = grant.Conn;
                    conn.Move(ConnLifecycle.Leased, ConnLifecycle.Probing);
                }
            }

            if (create)
            {
                try
                {
                    var c = await open(db ?? Database ?? Profile.DatabaseDefault, ct).ConfigureAwait(false);
                    ServerVersion ??= Provider.ServerVersion(c);
                    conn = new PhysicalConnection(c, this);
                }
                catch
                {
                    ReleaseSlot();
                    throw;
                }
                Opened?.Invoke(conn);
            }

            if (await ProbeAsync(conn!, db, create, ct).ConfigureAwait(false) is { } ready)
            {
                ready.Purpose = purpose;
                return ready;
            }
        }
    }

    /// <summary>
    /// The Probing step: transport check, staleness validation (§6 rule 6) and database switch, on a connection no other
    /// borrower can see. Returns it Leased, or null after discarding it (the caller loops for another).
    /// </summary>
    async Task<PhysicalConnection?> ProbeAsync(PhysicalConnection conn, string? db, bool fresh, CancellationToken ct)
    {
        // A connection whose SSH tunnel closed is dead even if the driver has not noticed yet.
        if (!conn.IsOpen || transportAlive?.Invoke(conn.Db) == false)
        {
            await DiscardAsync(conn).ConfigureAwait(false);
            return null;
        }
        try
        {
            if (!fresh)
            {
                try
                {
                    if (Time.NowMs() - conn.LastUsedMs > 30_000) await Provider.ValidateAsync(conn.Db, ct).ConfigureAwait(false);
                    // The driver may have reconnected underneath (SqlClient resiliency): the identity is read again then.
                    if (onReuse != null) await onReuse(conn, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogInformation("Pool {Key}: stale connection dropped ({Message})", Key, ex.Message);
                    await DiscardAsync(conn).ConfigureAwait(false);
                    return null;
                }
            }
            if (db != null && !Provider.DatabaseScopedConnections &&
                !string.Equals(Provider.CurrentDatabase(conn.Db), db, StringComparison.Ordinal))
                await Provider.ChangeDatabaseAsync(conn.Db, db, ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled or failed while probing: an open connection goes back for the next borrower, a broken one is dropped.
            if (conn.Move(ConnLifecycle.Probing, ConnLifecycle.Leased)) Return(conn);
            throw;
        }
        conn.LastUsedMs = Time.NowMs();
        return conn.Move(ConnLifecycle.Probing, ConnLifecycle.Leased) ? conn : null;
    }

    /// <summary>Set when the profile changed or was deleted: returned connections are closed, not reused.</summary>
    public bool Retired { get; private set; }

    /// <summary>Leased → Idle (or straight to the next waiter). Anything else — a second return, a discarded connection — is ignored.</summary>
    public void Return(PhysicalConnection conn)
    {
        if (conn.State != ConnLifecycle.Leased) { log.LogDebug("Pool {Key}: ignored return of a {State} connection", Key, conn.State); return; }
        conn.LeasedBy = null;
        conn.Purpose = null;
        if (!conn.IsOpen || Retired) { _ = DiscardAsync(conn); return; }
        conn.LastUsedMs = Time.NowMs();
        lock (_gate)
        {
            if (conn.State != ConnLifecycle.Leased) return;
            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                if (node.Value.TrySetResult(new Grant(conn))) return; // stays Leased: the waiter owns it now
            }
            conn.Move(ConnLifecycle.Leased, ConnLifecycle.Idle);
            _idle.Add(conn);
        }
    }

    /// <summary>
    /// Leased → Idle after reading the connection's transaction state (Phase D0b): a connection a helper (metadata, compare,
    /// scripting) leaves in an open or unknown transaction — or whose state cannot be read — is discarded, never pooled, so
    /// the next borrower never runs inside someone else's transaction. The read runs outside the lock.
    /// </summary>
    public async Task ReturnAsync(PhysicalConnection conn, CancellationToken ct = default)
    {
        if (conn.State != ConnLifecycle.Leased) { log.LogDebug("Pool {Key}: ignored return of a {State} connection", Key, conn.State); return; }
        if (!conn.IsOpen || Retired) { await DiscardAsync(conn).ConfigureAwait(false); return; }
        TxState state;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            state = await Provider.Transactions.StateAsync(conn.Db, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogInformation("Pool {Key}: transaction state unreadable on return, connection dropped ({Message})", Key, ex.Message);
            state = TxState.Unknown;
        }
        if (state != TxState.None)
        {
            if (state == TxState.Open) log.LogWarning("Pool {Key}: a connection came back inside an open transaction; it was closed (the server rolls back)", Key);
            await DiscardAsync(conn).ConfigureAwait(false);
            return;
        }
        Return(conn);
    }

    /// <summary>Closes the connection and frees its slot, exactly once however often it is called.</summary>
    public async Task DiscardAsync(PhysicalConnection conn)
    {
        if (!conn.MoveToDiscarded()) return;
        lock (_gate) _idle.Remove(conn);
        conn.LeasedBy = null;
        try { await conn.Db.DisposeAsync().ConfigureAwait(false); } catch { /* already broken */ }
        ReleaseSlot();
    }

    /// <summary>Hands a freed slot to the first live waiter (which opens into it), else gives it back to the pool.</summary>
    void ReleaseSlot()
    {
        lock (_gate)
        {
            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                if (node.Value.TrySetResult(new Grant(null))) return; // the slot stays counted: it is the waiter's now
            }
            _total--;
        }
    }

    /// <summary>Closes connections idle longer than <paramref name="idleMs"/>.</summary>
    public async Task SweepAsync(long idleMs)
    {
        List<PhysicalConnection> stale;
        lock (_gate)
        {
            var now = Time.NowMs();
            stale = _idle.Where(c => now - c.LastUsedMs > idleMs).ToList();
            foreach (var c in stale) _idle.Remove(c);
        }
        foreach (var c in stale) await DiscardAsync(c).ConfigureAwait(false);
    }

    /// <summary>Shutdown / retirement: idle connections close now, queued borrowers fail, leased ones close when returned.</summary>
    public async Task CloseAllAsync()
    {
        Retired = true;
        List<PhysicalConnection> all;
        List<TaskCompletionSource<Grant>> waiters;
        lock (_gate)
        {
            _closed = true;
            all = [.. _idle];
            waiters = [.. _waiters];
            _waiters.Clear();
        }
        foreach (var w in waiters) w.TrySetException(Closed());
        foreach (var c in all) await DiscardAsync(c).ConfigureAwait(false);
    }

    static QuerithmException Closed() => new("poolClosed", "The connection settings changed or the app is shutting down; run the query again.");
}
