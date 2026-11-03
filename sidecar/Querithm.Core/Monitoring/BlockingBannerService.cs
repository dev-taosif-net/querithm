using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Monitoring;

/// <summary>
/// Own-tab blocking banner (B1b): does a tab's open transaction block other sessions? With a monitor running for the
/// profile its snapshot answers. Otherwise one shared check per profile every 10 s, only while some tab of that profile
/// has an open transaction and is not executing, on a connection borrowed from Core's pool with a 1 s bounded wait (pool
/// full → the cycle is skipped and its waiter removed). Never runs on a tab session.
/// </summary>
public sealed class BlockingBannerService : IAsyncDisposable
{
    public const long CheckEveryMs = 10_000, ShowAfterMs = 5_000, BorrowWaitMs = 1_000, CheckTimeoutMs = 5_000, BorrowBoundMs = 45_000;

    readonly SessionManager _sessions;
    readonly INotifier _notifier;
    readonly ILogger _log;
    readonly Func<string, bool> _monitorAnswers;
    readonly Dictionary<string, (int Count, long MaxWait)> _shown = new();
    readonly HashSet<string> _unavailableTold = [];
    /// <summary>Profiles whose sign-in the server rejected, with the credential epoch it was rejected under.</summary>
    readonly Dictionary<string, long> _rejected = new();
    readonly object _gate = new();
    readonly Timer? _timer;
    int _running;

    public BlockingBannerService(SessionManager sessions, INotifier notifier, ILogger log, Func<string, bool> monitorAnswers, bool timer = true)
    {
        _sessions = sessions;
        _notifier = notifier;
        _log = log;
        _monitorAnswers = monitorAnswers;
        if (timer) _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMilliseconds(CheckEveryMs), TimeSpan.FromMilliseconds(CheckEveryMs));
    }

    /// <summary>The bound on one check query (tests shorten it).</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromMilliseconds(CheckTimeoutMs);

    /// <summary>Checks run (for tests and the bench: one per profile per cycle).</summary>
    public int Checks { get; private set; }

    /// <summary>The last check failure (retried silently next cycle; kept for diagnostics).</summary>
    public string? LastError { get; private set; }

    async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try { await RunCycleAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug("Blocking check failed: {Message}", ex.Message); }
        finally { Volatile.Write(ref _running, 0); }
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var candidates = _sessions.All
            .Where(s => !s.Closed && s.TxState == TxState.Open && s.Pinned != null && s.Provider.Activity != null)
            .GroupBy(s => s.Profile.Id);
        foreach (var group in candidates)
        {
            if (_monitorAnswers(group.Key)) continue; // the monitor's snapshots answer (O13)
            // A rejected sign-in is not retried every 10 s with the same secret (account lockout): only after the profile's
            // connections were rebuilt (new secret, edit, reconnect).
            lock (_gate)
            {
                if (_rejected.TryGetValue(group.Key, out var epoch))
                {
                    if (epoch == _sessions.CredentialEpoch(group.Key)) continue;
                    _rejected.Remove(group.Key);
                }
            }
            // Tabs that are executing are left out of this cycle (O6). Pinned is read once: a tab can unpin meanwhile.
            var idle = group.Select(s => (Session: s, Pinned: s.ActiveExecutionId == null ? s.Pinned : null)).Where(x => x.Pinned != null).ToList();
            var ids = idle.Select(x => (x.Session, Key: _sessions.Registry.KeyOf(x.Pinned!.Db))).Where(x => x.Key != null).ToList();
            // A tab whose server would not return its own identity (§6 rule 7) cannot be found among the blockers: say so once.
            foreach (var (s, pinned) in idle)
                if (_sessions.Registry.IsUnidentified(pinned!.Db) && _unavailableTold.Add(s.Id))
                    _notifier.Notify("session.blocking", new SessionBlockingEvent { SessionId = s.Id, Source = "check",
                        Unavailable = "This connection's server did not return its own session identity, so Querithm cannot tell whether it blocks other sessions." });
            if (ids.Count == 0) continue;
            var pool = _sessions.PoolFor(idle[0].Session);
            PhysicalConnection conn;
            // Read before the open: a failure counts against the credentials it was made with, not a newer secret.
            var epochAtOpen = _sessions.CredentialEpoch(group.Key);
            // The queue wait is 1 s (O14); opening a connection (SSH, login, identity) gets a generous bound of its own,
            // so a server that accepts and then stalls cannot hold this cycle — and every profile's banner — for ever.
            using (var borrow = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                borrow.CancelAfter(TimeSpan.FromMilliseconds(BorrowBoundMs));
                try { conn = await pool.BorrowAsync(null, null, borrow.Token, "blocking check", TimeSpan.FromMilliseconds(BorrowWaitMs)).ConfigureAwait(false); }
                catch (QuerithmException q) when (q.Code == "poolBusy") { continue; } // pool full: skip, the waiter is removed (O14)
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { LastError = $"Opening a connection for the blocking check took longer than {BorrowBoundMs / 1000} s."; continue; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LastError = ex.Message;
                    _log.LogDebug("Blocking check could not borrow: {Message}", ex.Message);
                    if (ex is QuerithmException { Code: "sshAuth" or "sshHostKeyChanged" or "sshHostKeyUnknown" } || idle[0].Session.Provider.Activity!.CredentialsRejected(ex))
                    {
                        lock (_gate) _rejected[group.Key] = epochAtOpen;
                        foreach (var (s, _) in ids)
                            _notifier.Notify("session.blocking", new SessionBlockingEvent { SessionId = s.Id, Source = "check",
                                Unavailable = "The server rejected the sign-in, so the blocking check is paused until this connection is set up again (to avoid locking the account)." });
                    }
                    continue;
                }
            }
            Checks++;
            (Contract.Providers.BlockedBySummary[]? Rows, string? Unavailable) result;
            // The query is bounded; the borrow and open are not (only their queue wait is, O14). A check that hangs would
            // otherwise hold a pool slot and stop the banner on every profile.
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(CheckTimeout);
            var discard = false;
            try { result = await idle[0].Session.Provider.Activity!.BlockedByAsync(conn.Db, ids.Select(x => x.Key!.Id).ToArray(), bound.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Silent: retried next cycle, no banner flicker, no orphan connection (O11). A cancelled command leaves
                // the connection in an unknown state: it is closed, not returned.
                discard = ex is OperationCanceledException || bound.IsCancellationRequested; // a driver may report a cancel as its own error
                LastError = discard && !ct.IsCancellationRequested ? $"The blocking check did not finish within {CheckTimeout.TotalSeconds:0.#} s." : ex.Message;
                _log.LogDebug("Blocking check failed: {Message}", LastError);
                if (ct.IsCancellationRequested) throw;
                continue;
            }
            finally
            {
                if (discard) await pool.DiscardAsync(conn).ConfigureAwait(false);
                else await pool.ReturnAsync(conn).ConfigureAwait(false);
            }
            if (result.Unavailable is { } why)
            {
                foreach (var (s, _) in ids)
                    if (_unavailableTold.Add(s.Id))
                        _notifier.Notify("session.blocking", new SessionBlockingEvent { SessionId = s.Id, Source = "check", Unavailable = why });
                continue;
            }
            foreach (var (s, key) in ids)
            {
                var hit = result.Rows!.FirstOrDefault(r => r.BlockerId == key!.Id);
                Report(s.Id, hit?.BlockedCount ?? 0, hit?.MaxWaitMs ?? 0, "check");
            }
        }
        // Sessions that no longer have an open transaction: clear their banner.
        lock (_gate)
            foreach (var id in _shown.Keys.ToList())
                if (_sessions.All.FirstOrDefault(s => s.Id == id) is not { Closed: false, TxState: TxState.Open })
                    Clear(id, "check");
    }

    /// <summary>A monitor's snapshot for the profile: each own tab's victims and their longest wait (server clock).</summary>
    public void OnSnapshot(ActivityMonitor monitor, IReadOnlyList<ActivityRow> rows)
    {
        foreach (var own in rows.Where(r => r.OwnSessionId != null))
        {
            var victims = rows.Where(r => r.BlockedBy.Contains(own.Key.Id)).ToList();
            Report(own.OwnSessionId!, BlockingGraph.VictimCount(rows, own.Key.Id), victims.Count == 0 ? 0 : victims.Max(v => v.WaitMs ?? 0), "monitor");
        }
        lock (_gate)
            foreach (var id in _shown.Keys.ToList())
                if (!rows.Any(r => r.OwnSessionId == id) && _sessions.All.FirstOrDefault(s => s.Id == id)?.Profile.Id == monitor.Profile.Id)
                    Clear(id, "monitor");
    }

    void Report(string sessionId, int count, long maxWait, string source)
    {
        lock (_gate)
        {
            var show = count > 0 && maxWait >= ShowAfterMs;
            if (show)
            {
                _shown[sessionId] = (count, maxWait);
                _notifier.Notify("session.blocking", new SessionBlockingEvent { SessionId = sessionId, BlockedCount = count, MaxWaitMs = maxWait, Source = source });
            }
            else Clear(sessionId, source);
        }
    }

    void Clear(string sessionId, string source)
    {
        if (_shown.Remove(sessionId))
            _notifier.Notify("session.blocking", new SessionBlockingEvent { SessionId = sessionId, BlockedCount = 0, MaxWaitMs = 0, Source = source });
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer != null) await _timer.DisposeAsync().ConfigureAwait(false);
    }
}
