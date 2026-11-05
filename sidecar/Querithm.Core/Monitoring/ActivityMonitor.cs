using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;

namespace Querithm.Core.Monitoring;

/// <summary>
/// One running activity monitor (Phase B): its own dedicated connection (never a tab's session), one
/// <see cref="MonitorScheduler"/>, reconnect with back-off (1 s → 30 s, probe re-run), and the guarded kill paths.
/// Snapshots are JSON notifications with the full session list and each text sent once (§16).
/// </summary>
public sealed class ActivityMonitor : IAsyncDisposable
{
    public const int MaxSessions = 1_000, MaxLocks = 2_000, MaxTopQueries = 100, MaxWaitCategories = 64, MaxDeadlocks = 200;
    public const long ControlConnectTimeoutMs = 5_000, KillLockWaitMs = 5_000, PreemptBoundMs = 1_000;

    readonly SessionManager _sessions;
    readonly INotifier _notifier;
    readonly ILogger _log;
    readonly IMonitorClock _clock;
    readonly ISchemaSource? _schema;
    readonly SemaphoreSlim _killLock;
    readonly bool _inline;
    readonly CancellationTokenSource _stop = new();
    readonly object _state = new();
    readonly TextStore _texts = new();
    readonly DeltaTracker<string> _waits = new();
    readonly DeltaTracker<string> _io = new();
    readonly MetricsBuilder _metrics = new();
    readonly TopQueriesTracker _top = new();
    readonly TickWaitSampler _sampler = new();
    readonly HistoryBuffer _history;
    /// <summary>Sessions terminated on this profile whose rollback may still run (shared by the profile's monitors, K19).</summary>
    readonly ConcurrentDictionary<SessionKey, byte> _terminated;
    /// <summary>Terminated sessions whose rollback progress this monitor showed: it sends their "done" itself.</summary>
    readonly HashSet<SessionKey> _rollbackShown = [];
    /// <summary>The latest sessions read covered every session (no database filter, not truncated) — from the tick itself.</summary>
    bool _lastSessionsFull;
    readonly HashSet<SessionKey> _killing = [];
    readonly HashSet<string> _deadlocksSeen = new(StringComparer.Ordinal);
    readonly Dictionary<MonitorPane, PaneStatus> _status = new();
    readonly Dictionary<MonitorPane, (int Failures, long RetryAt)> _backoff = new();
    readonly Dictionary<(string Db, long Id), string?> _resolvedNames = new();
    readonly List<(string Db, long Id)> _unresolved = [];

    DbConnection? _conn;
    IActivitySession? _session;
    MonitorScheduler? _scheduler;
    TaskCompletionSource _lost;
    Task? _run;
    HashSet<MonitorPane> _panes;
    long _intervalMs;
    long _seq;
    MonitorConnectionState _connection = MonitorConnectionState.Connecting;
    string? _connectionError;
    long? _retryAt;
    bool _capsDue;
    bool _controlOpen;
    string _topOrder = "cpu";
    bool _topSinceStart;
    volatile IReadOnlyDictionary<SessionKey, ActivityRow>? _latest;
    ActivityRow[] _latestRows = [];
    LockRow[]? _pendingLocks;
    TopQueryRow[]? _pendingTop;
    List<DeadlockEvent>? _pendingDeadlocks;

    public ActivityMonitor(string id, Profile profile, IDbProvider provider, SessionManager sessions, INotifier notifier, ILogger log,
        IMonitorClock clock, ISchemaSource? schema, SemaphoreSlim killLock, long intervalMs, IEnumerable<MonitorPane> panes, string? databaseFilter,
        bool inlineScheduler = false, ConcurrentDictionary<SessionKey, byte>? terminated = null)
    {
        _terminated = terminated ?? new();
        Id = id;
        Profile = profile;
        Provider = provider;
        _texts.Redact = text => provider.Language.RedactSecrets(text).Text;
        Activity = provider.Activity!;
        _sessions = sessions;
        _notifier = notifier;
        _log = log;
        _clock = clock;
        _schema = schema;
        _killLock = killLock;
        _intervalMs = intervalMs;
        _panes = [.. panes];
        DatabaseFilter = databaseFilter;
        _inline = inlineScheduler;
        _history = new HistoryBuffer(_texts);
        _lost = NewSignal();
        // Stopping also ends the wait for a lost connection (no WaitAsync: it would resume on the pool even in tests).
        _stop.Token.Register(() => { lock (_state) _lost.TrySetResult(); });
    }

    public string Id { get; }
    public Profile Profile { get; }
    public IDbProvider Provider { get; }
    public IActivityProvider Activity { get; }
    public string? DatabaseFilter { get; private set; }
    public bool Stopped => _stop.IsCancellationRequested;
    public MonitorConnectionState Connection { get { lock (_state) return _connection; } }
    public MonitorScheduler? Scheduler => _scheduler;
    public int ConnectionCount { get { lock (_state) return (_conn != null ? 1 : 0) + (_controlOpen ? 1 : 0); } }
    public IReadOnlyDictionary<SessionKey, ActivityRow>? Latest => _latest;
    public long Seq { get { lock (_state) return _seq; } }
    internal TextStore Texts => _texts;
    /// <summary>The text store's size, read under the state lock (the store is not safe to enumerate while a tick runs).</summary>
    internal (int Count, long Bytes) TextStats() { lock (_state) return (_texts.Count, _texts.TotalBytes); }
    internal HistoryBuffer History => _history;

    /// <summary>Raised after each snapshot (the own-tab blocking banner reads it instead of running its own check).</summary>
    public event Action<ActivityMonitor, IReadOnlyList<ActivityRow>>? Snapshot;

    public void Start() => _run = _inline ? RunAsync() : Task.Run(RunAsync);

    /// <summary>Tests (inline): resume on the signalling thread so a fake clock drives the monitor deterministically.</summary>
    TaskCompletionSource NewSignal() => new(_inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------------------------------------------------------- connection lifecycle

    async Task RunAsync()
    {
        var backoff = 1_000L;
        while (!_stop.IsCancellationRequested)
        {
            string? error = null;
            // Open, identity, session settings and probe have no command timeout of their own: a server that accepts the
            // login and then stalls would hold the monitor in "connecting" forever, with every kill refused.
            using var bound = _clock.After(ConnectBoundMs);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, bound.Token);
            try
            {
                await ConnectAsync(connect.Token).ConfigureAwait(false);
                Task lost;
                lock (_state) lost = _lost.Task;
                await lost.ConfigureAwait(false);
                if (_stop.IsCancellationRequested) break;
                error = "The monitor connection was lost.";
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            {
                error = $"Connecting to the server and reading its capabilities took longer than {ConnectBoundMs / 1000} s.";
                _log.LogInformation("Monitor {Id}: connection failed ({Message})", Id, error);
            }
            catch (MonitorRefusedException r)
            {
                _log.LogInformation("Monitor {Id} refused to start: {Reason}", Id, r.Message);
                await StopWithReasonAsync(r.Message).ConfigureAwait(false);
                break;
            }
            catch (IdentityUnavailableException ex)
            {
                // The server refuses the monitor's read of its own session (§6 rule 7): the monitor could not recognise itself
                // (K6) and reads the same views for every pane, so a retry every 30 s cannot help.
                _log.LogInformation("Monitor {Id} refused to start: {Reason}", Id, ex.Message);
                await StopWithReasonAsync($"{ex.Message} The monitor needs to read its own session to tell it apart from the sessions it shows, so it stopped.").ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is QuerithmException { Code: "sshAuth" or "sshHostKeyChanged" or "sshHostKeyUnknown" } || Activity.CredentialsRejected(ex))
            {
                // Retrying every ≤ 30 s with the same remembered secret cannot succeed and can lock the account; a changed
                // or unknown SSH host key needs the user's decision.
                _log.LogInformation("Monitor {Id}: the server rejected the sign-in; stopped", Id);
                await StopWithReasonAsync($"The server rejected the monitor's sign-in ({Describe(ex)}). The monitor stopped instead of retrying, so the account is not locked out; start it again once the connection's credentials are fixed.").ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                _log.LogInformation("Monitor {Id}: connection failed ({Message})", Id, error);
            }
            await TeardownAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested) break;
            if (_tickedSinceConnect) { backoff = 1_000; _tickedSinceConnect = false; } // the connection worked: start the back-off again
            lock (_state)
            {
                _connection = MonitorConnectionState.Reconnecting;
                _connectionError = error;
                _retryAt = _clock.NowMs + backoff;
                _history.MarkGap();
            }
            ResetTrackers();
            Publish(statusOnly: true);
            try { await _clock.WaitUntilAsync(_clock.NowMs + backoff, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 30_000);
        }
    }

    /// <summary>
    /// Twice the profile's connect timeout (an SSH tunnel's open and the login each get the full timeout) plus 15 s for
    /// identity, session settings and probe: never shorter than a healthy slow open (the O14 lesson).
    /// </summary>
    long ConnectBoundMs => (2 * (Profile.TimeoutS ?? 15) + 15) * 1000L;

    async Task StopWithReasonAsync(string reason)
    {
        await TeardownAsync().ConfigureAwait(false);
        lock (_state) { _connection = MonitorConnectionState.Stopped; _connectionError = reason; }
        Publish(statusOnly: true);
        _notifier.Notify("monitor.stopped", new MonitorStoppedEvent { MonitorId = Id, Reason = reason });
        _stop.Cancel();
    }

    string Describe(Exception ex) =>
        ex is QuerithmException q ? q.Message : Provider.TranslateException(ex).FirstOrDefault()?.Message ?? ex.Message;

    sealed class MonitorRefusedException(string message) : Exception(message);

    /// <summary>
    /// The driver's own command timeout on monitor and control connections, above every job timeout (the longest is 10 s).
    /// Jobs are cancelled by the scheduler; a cancel the server never acknowledges (SqlClient sends only an attention) would
    /// otherwise hold the job, and with it stop, reconnect and kill preemption, for as long as the server stalls. On a
    /// driver timeout SqlClient and Npgsql break the connection after their attention / cancellation wait.
    /// </summary>
    public const int CommandBackstopS = 15;

    /// <summary>The monitor's connection profile: its own application name, no transparent driver reconnects, a command-timeout backstop.</summary>
    Profile MonitorProfile(Profile profile) =>
        profile with
        {
            CommandTimeoutS = CommandBackstopS,
            Options = new Dictionary<string, string>(profile.Options ?? []) { ["appName"] = "Querithm Monitor", ["connectRetryCount"] = "0" },
        };

    string? MonitorDatabase => Profile.DatabaseDefault ?? Provider.Traits.DefaultDatabase;

    async Task ConnectAsync(CancellationToken ct)
    {
        lock (_state)
        {
            if (_connection != MonitorConnectionState.Reconnecting) _connection = MonitorConnectionState.Connecting;
        }
        var conn = await _sessions.OpenDedicatedAsync(Profile, Provider, MonitorDatabase, ct, ConnectionRegistry.Kinds.Monitor, MonitorProfile).ConfigureAwait(false);
        Activity.PrepareMonitorConnection(conn);
        try
        {
            // Applied on every (re)open (M1).
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = Activity.MonitorSessionSetupSql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            var session = await Activity.ProbeAsync(conn, ct).ConfigureAwait(false);
            if (session.StartRefusal is { } refusal)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new MonitorRefusedException(refusal);
            }
            var scheduler = new MonitorScheduler(_clock, TickAsync, _intervalMs, _inline);
            scheduler.AddPeriodic("locks", 5 * _intervalMs, 5_000, LocksAsync, throttleRank: 2);
            scheduler.AddPeriodic("topQueries", 5 * _intervalMs, 5_000, TopQueriesAsync, throttleRank: 1);
            scheduler.AddPeriodic("deadlocks", 30_000, 10_000, DeadlockPollAsync, throttleRank: 0);
            scheduler.AddPeriodic("lockNames", 5 * _intervalMs, 5_000, ResolveNamesAsync, throttleRank: 3);
            lock (_state)
            {
                // StopAsync cancels first and then completes _lost under this lock: a stop that came during the probe must
                // not be answered by installing a fresh signal and starting a scheduler nobody stops.
                ct.ThrowIfCancellationRequested();
                if (_paused) scheduler.Pause(); // a pause made while reconnecting holds for the new scheduler (L11)
                _conn = conn;
                _session = session;
                _scheduler = scheduler;
                _lost = NewSignal();
                _connection = MonitorConnectionState.Connected;
                _connectionError = null;
                _retryAt = null;
                _capsDue = true;
                ApplyPanes();
            }
            scheduler.Start();
        }
        catch
        {
            if (_conn != conn) await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    async Task TeardownAsync()
    {
        MonitorScheduler? scheduler;
        DbConnection? conn;
        lock (_state)
        {
            scheduler = _scheduler;
            conn = _conn;
            _scheduler = null;
            _conn = null;
            _session = null;
        }
        if (scheduler != null) await scheduler.StopAsync().ConfigureAwait(false);
        if (conn != null)
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* broken */ }
    }

    void ResetTrackers()
    {
        lock (_state)
        {
            _waits.Reset();
            _io.Reset();
            _metrics.Reset();
            _top.Reset();
            _sampler.Reset();
            _backoff.Clear();
        }
    }

    void Lost(Exception ex)
    {
        _log.LogInformation("Monitor {Id}: connection lost ({Message})", Id, ex.Message);
        TaskCompletionSource lost;
        lock (_state) lost = _lost;
        lost.TrySetResult();
    }

    bool ConnectionBroken => _conn is not { State: ConnectionState.Open };

    // ---------------------------------------------------------------- settings

    void ApplyPanes()
    {
        if (_scheduler == null) return;
        foreach (var job in _scheduler.Jobs)
        {
            job.Enabled = job.Name switch
            {
                "locks" or "lockNames" => _panes.Contains(MonitorPane.Locks),
                "topQueries" => _panes.Contains(MonitorPane.TopQueries),
                "deadlocks" => _panes.Contains(MonitorPane.Deadlocks),
                _ => job.Enabled,
            };
            if (job.Name is "locks" or "topQueries" or "lockNames") job.BasePeriodMs = 5 * _intervalMs;
        }
    }

    public void SetPanes(IEnumerable<MonitorPane> panes)
    {
        lock (_state)
        {
            _panes = [.. panes];
            foreach (var p in _status.Keys.Where(p => !_panes.Contains(p)).ToList()) _status.Remove(p);
            ApplyPanes();
        }
    }

    public void SetInterval(long ms)
    {
        MonitorScheduler? s;
        lock (_state)
        {
            _intervalMs = ms;
            ApplyPanes();
            s = _scheduler;
        }
        s?.SetInterval(ms);
    }

    public void SetTopQueries(string orderBy, bool sinceStart)
    {
        lock (_state)
        {
            _topOrder = orderBy;
            _topSinceStart = sinceStart;
        }
    }

    public void SetRetention(int minutes)
    {
        lock (_state) _history.RetentionMinutes = minutes;
    }

    bool _paused;
    public void Pause()
    {
        lock (_state) _paused = true;
        ApplyPause();
        Publish(statusOnly: true);
    }

    public void Resume()
    {
        lock (_state) _paused = false;
        ApplyPause();
    }

    readonly object _pauseGate = new();

    /// <summary>
    /// Applies the latest pause state to the current scheduler. Serialised, and each call reads the state afresh, so a
    /// Pause and a Resume that cross end with the scheduler matching _paused. Lock order: _pauseGate → _state → scheduler.
    /// </summary>
    void ApplyPause()
    {
        lock (_pauseGate)
        {
            MonitorScheduler? s;
            bool paused;
            lock (_state) { s = _scheduler; paused = _paused; }
            if (paused) s?.Pause();
            else s?.Resume();
        }
    }

    bool Due(MonitorPane pane)
    {
        if (!_panes.Contains(pane)) return false;
        return !_backoff.TryGetValue(pane, out var b) || _clock.NowMs >= b.RetryAt;
    }

    void Fail(MonitorPane pane, string message)
    {
        var (failures, _) = _backoff.GetValueOrDefault(pane);
        failures++;
        var wait = Math.Min(_intervalMs * (1L << Math.Min(failures - 1, 4)), 30_000);
        _backoff[pane] = (failures, _clock.NowMs + wait);
        _status[pane] = new PaneStatus { Pane = pane, State = PaneState.Error, Reason = message, RetryInMs = wait };
    }

    void Succeed(MonitorPane pane, PaneState state, string? reason, int? total = null)
    {
        _backoff.Remove(pane);
        _status[pane] = new PaneStatus { Pane = pane, State = state, Reason = reason, Total = total };
    }

    void Apply<T>(MonitorPane pane, PaneResult<T>? r)
    {
        if (r == null) return;
        if (r.State == PaneState.Error) Fail(pane, r.Reason ?? "error");
        else Succeed(pane, r.State, r.Reason, r.Total);
    }

    // ---------------------------------------------------------------- main tick

    async Task TickAsync(CancellationToken ct)
    {
        IActivitySession session;
        DbConnection conn;
        TickRequest req;
        lock (_state)
        {
            if (_session == null || _conn == null) return;
            session = _session;
            conn = _conn;
            var waits = Due(MonitorPane.Waits);
            req = new TickRequest(Due(MonitorPane.Sessions) || (waits && session.WaitsFromSessions), waits, Due(MonitorPane.Metrics), Due(MonitorPane.Io),
                MaxSessions, DatabaseFilter);
        }
        TickResult result;
        try { result = await session.TickAsync(conn, req, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            if (ConnectionBroken) { Lost(ex); return; }
            FailTick(req, ex is OperationCanceledException ? "The monitor query timed out (5 s); the server may be busy." : Describe(ex));
            return;
        }
        try { await AfterTickAsync(session, conn, req, result, ct).ConfigureAwait(false); }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            // The scheduler swallows job exceptions: without this the panes would keep the previous tick's data as "ok".
            _log.LogWarning("Monitor {Id}: processing a tick failed ({Type})", Id, ex.GetType().Name);
            FailTick(req, "The monitor could not process the server's response this tick; retrying.");
            return;
        }
        _tickedSinceConnect = true;
    }

    /// <summary>Set by a tick that completed; a reconnect resets the back-off only after one (a server that accepts and then drops the monitor backs off).</summary>
    volatile bool _tickedSinceConnect;

    void FailTick(TickRequest req, string message)
    {
        lock (_state)
        {
            foreach (var pane in new[] { MonitorPane.Sessions, MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.Io })
                if (pane == MonitorPane.Sessions ? req.Sessions : pane == MonitorPane.Waits ? req.Waits : pane == MonitorPane.Metrics ? req.Metrics : req.Io)
                    Fail(pane, message);
        }
        Publish(statusOnly: true);
    }

    async Task AfterTickAsync(IActivitySession session, DbConnection conn, TickRequest req, TickResult result, CancellationToken ct)
    {
        var at = _clock.NowMs;
        var wall = Time.NowMs();
        ActivityRow[]? rows = null;
        Dictionary<SessionKey, RollbackStatus?> rollback = [];
        // Rollback progress of sessions this monitor terminated that are still there (SQL Server, K19).
        SessionKey[] terminated;
        terminated = [.. _terminated.Keys];
        lock (_state) terminated = [.. terminated.Union(_rollbackShown)];
        if (terminated.Length > 0 && result.Sessions?.Value is { } present)
        {
            var keys = present.Select(r => r.Key).ToHashSet();
            foreach (var k in terminated)
            {
                if (!keys.Contains(k)) continue;
                // Progress unknown rather than the whole tick lost: a timed-out read still publishes the rows already read.
                try { rollback[k] = await session.RollbackStatusAsync(conn, k, ct).ConfigureAwait(false); }
                catch (Exception) when (!_stop.IsCancellationRequested) { rollback[k] = null; if (ct.IsCancellationRequested) break; }
            }
        }
        List<RollbackProgress>? progress = null;
        lock (_state)
        {
            if (result.Sessions is { } sr)
            {
                if (sr.Value is { } raw)
                {
                    rows = new ActivityRow[raw.Length];
                    for (var i = 0; i < raw.Length; i++)
                    {
                        var r = raw[i];
                        string? textId = null;
                        if (r.TextId != null && result.SessionTexts?.GetValueOrDefault(r.TextId) is { } text)
                            textId = _texts.InternForSession(r.Key, text);
                        var hit = _sessions.Registry.Find(r.Key);
                        var rolling = r.RollingBack;
                        double? pct = r.RollingBack ? r.PercentComplete : null;
                        if (rollback.TryGetValue(r.Key, out var st) && st is { InProgress: true }) { rolling = true; pct = st.Percent ?? pct; }
                        rows[i] = r with
                        {
                            TextId = textId,
                            QuerithmKind = hit?.Kind,
                            OwnSessionId = hit?.LeasedBy,
                            QuerithmNote = hit == null ? null : hit.LeasedBy != null ? null : hit.Idle ? "idle" : hit.Purpose,
                            RollingBack = rolling,
                            RollbackPercent = rolling ? pct : null,
                        };
                    }
                    var live = rows.Select(r => r.Key).ToHashSet();
                    // Absent from a read of every session = the rollback is over. A filtered or truncated read proves nothing
                    // (the set is shared with the profile's other monitors).
                    var everySession = DatabaseFilter == null && sr.State != PaneState.Truncated;
                    _lastSessionsFull = everySession;
                    foreach (var k in terminated)
                    {
                        if (live.Contains(k))
                        {
                            (progress ??= []).Add(new RollbackProgress { Key = k, Percent = rows.First(r => r.Key == k).RollbackPercent });
                            _rollbackShown.Add(k);
                        }
                        // Gone from a read of every session, or already pruned by another of the profile's monitors: this
                        // monitor still ends the progress it showed (the UI keeps the last one it got).
                        else if (everySession || !_terminated.ContainsKey(k))
                        {
                            (progress ??= []).Add(new RollbackProgress { Key = k, Percent = 100, Done = true });
                            _terminated.TryRemove(k, out _);
                            _rollbackShown.Remove(k);
                        }
                    }
                    _latestRows = rows;
                    _latest = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
                }
                if (req.Sessions && _panes.Contains(MonitorPane.Sessions)) Apply(MonitorPane.Sessions, sr);
                else if (sr.State == PaneState.Error && session.WaitsFromSessions) Fail(MonitorPane.Waits, sr.Reason ?? "error");
            }
        }

        WaitDelta[]? waits = null;
        WaitCoverage? coverage = null;
        MetricSample? metrics = null;
        IoDelta[]? io = null;
        lock (_state)
        {
            var marker = result.ResetMarker;
            if (req.Waits)
            {
                if (session.WaitsFromSessions)
                {
                    if (rows != null)
                    {
                        waits = _sampler.Sample(rows, at, _intervalMs, Activity.WaitCategory, Activity.IsBenignWait);
                        var truncated = result.Sessions?.State == PaneState.Truncated;
                        coverage = new WaitCoverage { Source = "tick-sampled", PeriodMs = (int)_intervalMs, Partial = truncated };
                        Succeed(MonitorPane.Waits, waits == null ? PaneState.Collecting : PaneState.Ok, Reason(session, MonitorPane.Waits));
                        waits = Cap(waits);
                    }
                }
                else if (result.Waits is { } wr)
                {
                    if (wr.Value is { } counters)
                    {
                        var d = _waits.Update(counters.SelectMany(c => new[] { (c.WaitType + "|ms", (double)c.WaitMs), (c.WaitType + "|n", (double)(c.Tasks ?? 0)), (c.WaitType + "|sig", (double)(c.SignalMs ?? 0)) }), at, marker);
                        if (d.HasDeltas)
                            waits = Cap(counters.Select(c => new WaitDelta
                            {
                                Category = c.Category, WaitType = c.WaitType,
                                WaitMsDelta = (long)d.Deltas.GetValueOrDefault(c.WaitType + "|ms"),
                                TasksDelta = c.Tasks == null ? null : (long)d.Deltas.GetValueOrDefault(c.WaitType + "|n"),
                                SignalMsDelta = c.SignalMs == null ? null : (long)d.Deltas.GetValueOrDefault(c.WaitType + "|sig"),
                            }).Where(w => w.WaitMsDelta > 0 || w.TasksDelta > 0).OrderByDescending(w => w.WaitMsDelta).ToArray());
                        if (MonitorRules.For(Provider.Engine).WaitsSampledPerTick && session is { } s2)
                            coverage = new WaitCoverage { Source = "server-profile", PeriodMs = 10, Partial = false };
                        Succeed(MonitorPane.Waits, d.First ? PaneState.Collecting : d.Reset ? PaneState.ResetDetected : PaneState.Ok, Reason(session, MonitorPane.Waits));
                    }
                    else Apply(MonitorPane.Waits, wr);
                }
            }
            if (req.Metrics && result.Metrics is { } mr)
            {
                if (mr.Value is { } counters)
                {
                    var (sample, state) = _metrics.Build(counters, at, wall, marker);
                    metrics = sample;
                    Succeed(MonitorPane.Metrics, state, mr.Reason ?? Reason(session, MonitorPane.Metrics));
                }
                else Apply(MonitorPane.Metrics, mr);
            }
            if (req.Io && result.Io is { } ir)
            {
                if (ir.Value is { } counters)
                {
                    var d = _io.Update(counters.SelectMany(c => new[] { (c.Key + "|r", (double)c.ReadBytes), (c.Key + "|w", (double)c.WriteBytes),
                        (c.Key + "|rs", (double)(c.ReadStallMs ?? 0)), (c.Key + "|ws", (double)(c.WriteStallMs ?? 0)) }), at, marker);
                    if (d.HasDeltas)
                        io = counters.Select(c => new IoDelta
                        {
                            Scope = c.Scope, Database = c.Database, File = c.File, BackendType = c.BackendType, Object = c.Object, Context = c.Context,
                            ReadBytes = (long)d.Deltas.GetValueOrDefault(c.Key + "|r"), WriteBytes = (long)d.Deltas.GetValueOrDefault(c.Key + "|w"),
                            ReadStallMs = c.ReadStallMs == null ? null : (long)d.Deltas.GetValueOrDefault(c.Key + "|rs"),
                            WriteStallMs = c.WriteStallMs == null ? null : (long)d.Deltas.GetValueOrDefault(c.Key + "|ws"),
                        }).OrderByDescending(x => x.ReadBytes + x.WriteBytes).ToArray();
                    Succeed(MonitorPane.Io, d.First ? PaneState.Collecting : d.Reset ? PaneState.ResetDetected : PaneState.Ok, ir.Reason);
                }
                else Apply(MonitorPane.Io, ir);
            }
        }
        Publish(statusOnly: false, rows, waits, coverage, metrics, io, progress);
    }

    static WaitDelta[]? Cap(WaitDelta[]? w) => w is { Length: > MaxWaitCategories } ? w[..MaxWaitCategories] : w;

    static string? Reason(IActivitySession s, MonitorPane pane) => s.Capabilities.Panes.FirstOrDefault(p => p.Pane == pane)?.Reason;

    // ---------------------------------------------------------------- secondary jobs

    async Task LocksAsync(CancellationToken ct)
    {
        var (session, conn) = Current();
        if (session == null || conn == null) return;
        PaneResult<LockRow[]> r;
        try { r = await session.LocksAsync(conn, MaxLocks, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            if (ConnectionBroken) { Lost(ex); return; }
            lock (_state) Fail(MonitorPane.Locks, JobFailure(ex));
            return;
        }
        // Outside the state lock: the cache is read from the local store, and 2,000 lock rows must not hold up ticks,
        // publishes or a kill while it is.
        var named = r.Value is { } read ? NamesFromCache(read) : null;
        lock (_state)
        {
            Apply(MonitorPane.Locks, r);
            if (named != null) _pendingLocks = named.Select(ResolveLockName).ToArray();
        }
    }

    const string CacheNameNote = "name from metadata cache";

    static bool TryObjectId(LockRow l, out string db, out long oid)
    {
        db = l.Database ?? "";
        oid = 0;
        if (l.Object is not { } obj || l.Database == null) return false;
        var dot = obj.LastIndexOf('.');
        return dot >= 0 && long.TryParse(obj.AsSpan(dot + 1), out oid);
    }

    /// <summary>Lock object names from the metadata cache (§8): one database list and one snapshot per database per job.</summary>
    LockRow[] NamesFromCache(LockRow[] rows)
    {
        if (_schema == null) return rows;
        // The ids each cached database has locks on; names are built only for those.
        HashSet<string>? cachedDbs = null;
        var wanted = new Dictionary<string, Dictionary<long, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in rows)
        {
            if (!TryObjectId(l, out var db, out var oid)) continue;
            cachedDbs ??= _schema.Databases(Profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!cachedDbs.Contains(db)) continue;
            if (!wanted.TryGetValue(db, out var ids)) wanted[db] = ids = [];
            ids.TryAdd(oid, null);
        }
        foreach (var (db, ids) in wanted)
            if (_schema.Get(Profile.Id, db) is { } snap)
                foreach (var o in snap.Objects)
                    if (ids.TryGetValue(o.Id, out var known) && known == null) ids[o.Id] = $"{o.Schema}.{o.Name}";
        var result = new LockRow[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var l = result[i] = rows[i];
            if (TryObjectId(l, out var db, out var oid) && wanted.TryGetValue(db, out var ids) && ids.GetValueOrDefault(oid) is { } name)
                result[i] = l with { Object = name, ObjectNote = CacheNameNote };
        }
        return result;
    }

    /// <summary>Ids the metadata cache did not name: SQL Server ids are resolved lazily (B2). Under the state lock.</summary>
    LockRow ResolveLockName(LockRow l)
    {
        if (l.ObjectNote == CacheNameNote || !TryObjectId(l, out var db, out var oid)) return l;
        if (_resolvedNames.TryGetValue((db, oid), out var name))
            return name != null ? l with { Object = name } : l with { ObjectNote = "name locked" };
        if (MonitorRules.For(Provider.Engine).ResolvesLockObjectNames && _unresolved.Count < 200 && !_unresolved.Contains((db, oid))) _unresolved.Add((db, oid));
        return l;
    }

    async Task ResolveNamesAsync(CancellationToken ct)
    {
        var (session, conn) = Current();
        List<(string, long)> ids;
        lock (_state)
        {
            ids = [.. _unresolved];
            _unresolved.Clear();
        }
        if (session == null || conn == null || ids.Count == 0) return;
        try
        {
            var names = await session.ResolveObjectNamesAsync(conn, ids, ct).ConfigureAwait(false);
            lock (_state)
                foreach (var id in ids)
                {
                    _resolvedNames[id] = names.GetValueOrDefault(id);
                    if (_resolvedNames.Count > 5_000) _resolvedNames.Clear();
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ConnectionBroken) Lost(ex);
        }
    }

    async Task TopQueriesAsync(CancellationToken ct)
    {
        var (session, conn) = Current();
        if (session == null || conn == null) return;
        try
        {
            var r = await session.TopQueriesAsync(conn, ct).ConfigureAwait(false);
            if (r.Value is not { } read)
            {
                lock (_state) Apply(MonitorPane.TopQueries, r);
                return;
            }
            PaneState state;
            IReadOnlyList<TopQueriesTracker.Totals> top;
            string? note;
            lock (_state)
            {
                (state, top) = _top.Update(read, _topOrder, _topSinceStart, MaxTopQueries);
                note = _top.Note; // over the tracking cap, or since-start groups dropped
            }
            // Text only for the final top 100, read fresh (never cached under sql_handle — an ALTER keeps the handle).
            var keys = top.Select(t => t.Sample!.TextKey).Distinct().ToList();
            var texts = keys.Count > 0 ? await session.TopQueryTextsAsync(conn, keys, ct).ConfigureAwait(false) : new Dictionary<string, TopQueryText>();
            lock (_state)
            {
                var rows = top.Select(t =>
                {
                    var tx = texts.GetValueOrDefault(t.Sample!.TextKey);
                    return new TopQueryRow
                    {
                        QueryHash = t.QueryHash,
                        TextId = tx?.Text is { } s ? _texts.Intern(s) : null,
                        ExecDelta = t.Execs,
                        CpuMsDelta = t.CpuMs is { } c ? (long)Math.Round(c) : null,
                        DurationMsDelta = (long)Math.Round(t.DurationMs),
                        LogicalReadsDelta = t.LogicalReads,
                        SharedBlocksDelta = t.SharedBlocks,
                        RowsDelta = t.Rows,
                        IoTimeMsDelta = t.IoMs is { } io ? (long)Math.Round(io) : null,
                        Database = t.Database ?? tx?.Database,
                    };
                }).ToArray();
                _pendingTop = rows;
                Succeed(MonitorPane.TopQueries, state, note ?? r.Reason ?? Reason(session, MonitorPane.TopQueries));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            if (ConnectionBroken) { Lost(ex); return; }
            lock (_state) Fail(MonitorPane.TopQueries, JobFailure(ex));
        }
    }

    async Task DeadlockPollAsync(CancellationToken ct)
    {
        var (session, conn) = Current();
        if (session == null || conn == null) return;
        try
        {
            var now = Time.NowMs();
            // The poll reads what is new since its previous run (SQL Server resumes in the event files): re-reading the files
            // costs in proportion to their size every 30 s, and the ring buffer alone misses the newest when full (B0 row 15).
            var r = await session.DeadlocksAsync(conn, now - 24 * 3_600_000L, now, MaxDeadlocks, ct, recentOnly: true).ConfigureAwait(false);
            lock (_state)
            {
                Apply(MonitorPane.Deadlocks, r);
                if (r.Value is not { } events) return;
                // Each event is sent once (X16). The first poll also sends what it primed from (SQL Server: the last 24 h of the
                // file being written); the webview treats that first batch as history and never toasts it.
                foreach (var e in events.OrderBy(e => e.At))
                    if (_deadlocksSeen.Add(e.Id)) (_pendingDeadlocks ??= []).Add(e);
                if (_deadlocksSeen.Count > 10_000) _deadlocksSeen.Clear();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            if (ConnectionBroken) { Lost(ex); return; }
            lock (_state) Fail(MonitorPane.Deadlocks, JobFailure(ex));
        }
    }

    /// <summary>
    /// A secondary job that failed or was cancelled while the monitor runs: its pane says so instead of showing the last
    /// result as current (L7). Cancelled = its time limit, or pre-empted for a kill.
    /// </summary>
    string JobFailure(Exception ex) => ex is OperationCanceledException
        ? "The query did not finish within its time limit (the server may be busy) or was cancelled for a kill; retrying."
        : Describe(ex);

    (IActivitySession? Session, DbConnection? Conn) Current()
    {
        lock (_state) return (_session, _conn);
    }

    // ---------------------------------------------------------------- snapshot

    void Publish(bool statusOnly, ActivityRow[]? rows = null, WaitDelta[]? waits = null, WaitCoverage? coverage = null, MetricSample? metrics = null,
        IoDelta[]? io = null, List<RollbackProgress>? progress = null)
    {
        // Sequence number and notification in one order: a status-only publish (pause, a failed tick) must not overtake a
        // full snapshot, which the UI would then drop as older and lose its locks, top queries and new deadlocks.
        lock (_publishGate) PublishLocked(statusOnly, rows, waits, coverage, metrics, io, progress);
    }

    readonly object _publishGate = new();
    TopQueryRow[]? _latestTop;
    long? _fullSessionsAtMs;

    /// <summary>
    /// The latest sessions read covers every session (no database filter, not truncated) and is recent: its rows can answer
    /// the own-tab blocking banner. Not while paused, reconnecting or timing out — no fresh read arrives then.
    /// </summary>
    public bool AnswersOwnSessions
    {
        get { lock (_state) return DatabaseFilter == null && _fullSessionsAtMs is { } t && _clock.NowMs - t <= 2 * _intervalMs + 5_000; }
    }

    void PublishLocked(bool statusOnly, ActivityRow[]? rows, WaitDelta[]? waits, WaitCoverage? coverage, MetricSample? metrics,
        IoDelta[]? io, List<RollbackProgress>? progress)
    {
        MonitorSnapshot snapshot;
        ActivityRow[] latest;
        var full = false;
        lock (_state)
        {
            var paneStatus = new List<PaneStatus>();
            var disconnected = _connection != MonitorConnectionState.Connected;
            foreach (var pane in _panes.OrderBy(p => p))
            {
                if (disconnected)
                    paneStatus.Add(new PaneStatus { Pane = pane, State = PaneState.Error, Reason = _connectionError ?? "connection lost, retrying",
                        RetryInMs = _retryAt is { } ra ? Math.Max(0, ra - _clock.NowMs) : null });
                else if (_status.TryGetValue(pane, out var st)) paneStatus.Add(st);
                else if (_session?.Capabilities.Panes.FirstOrDefault(c => c.Pane == pane) is { Available: false } cap)
                    paneStatus.Add(new PaneStatus { Pane = pane, State = PaneState.Degraded, Reason = cap.Reason });
                else paneStatus.Add(new PaneStatus { Pane = pane, State = PaneState.Collecting });
            }
            Dictionary<string, string>? texts = null;
            var locks = _pendingLocks;
            var top = _pendingTop;
            var deadlocks = _pendingDeadlocks;
            if (!statusOnly)
            {
                _pendingLocks = null;
                _pendingTop = null;
                _pendingDeadlocks = null;
                // Live texts: the latest rows' and the top queries on screen (kept until the next top-queries run, M10); the
                // text store sends only ids it has not sent yet.
                if (top != null) _latestTop = top;
                var live = (rows ?? _latestRows).Select(r => r.TextId).OfType<string>();
                if (_latestTop != null) live = live.Concat(_latestTop.Select(t => t.TextId).OfType<string>());
                texts = _texts.Commit(live, (rows ?? _latestRows).Select(r => r.Key).ToHashSet());
            }
            latest = rows ?? _latestRows;
            var sessionsTotal = _status.GetValueOrDefault(MonitorPane.Sessions)?.Total;
            if (!statusOnly && rows != null)
            {
                full = _lastSessionsFull;
                _fullSessionsAtMs = full ? _clock.NowMs : null;
            }
            snapshot = new MonitorSnapshot
            {
                MonitorId = Id,
                At = Time.NowMs(),
                Seq = ++_seq,
                Sessions = statusOnly ? null : rows,
                Texts = texts,
                Blocking = statusOnly || rows == null ? null : BlockingGraph.Build(rows, sessionsTotal != null ? "beyond the 1,000-session cap" : null),
                Locks = statusOnly ? null : locks,
                Waits = waits,
                WaitCoverage = coverage,
                Metrics = metrics,
                Io = io,
                TopQueries = statusOnly ? null : top,
                NewDeadlocks = statusOnly ? null : deadlocks?.ToArray(),
                Status = [.. paneStatus],
                Rollbacks = progress?.ToArray(),
                LagMs = _scheduler?.LastLagMs ?? 0,
                ConnectionCount = (_conn != null ? 1 : 0) + (_controlOpen ? 1 : 0),
                Throttled = _scheduler?.Throttled ?? false,
                Paused = _paused,
                Connection = _connection,
                ConnectionError = _connectionError,
                RetryInMs = _retryAt is { } r2 ? Math.Max(0, r2 - _clock.NowMs) : null,
                Capabilities = _capsDue ? _session?.Capabilities : null,
            };
            if (snapshot.Capabilities != null) _capsDue = false;
            if (!statusOnly && rows != null) _history.Add(snapshot, _clock.NowMs);
        }
        _notifier.Notify("monitor.snapshot", snapshot);
        // Only a read of every session answers the banner: a filtered or truncated one would clear banners of tabs it cannot see.
        if (full) Snapshot?.Invoke(this, latest);
    }

    // ---------------------------------------------------------------- texts / history

    public MonitorTextsResult GetTexts(IReadOnlyList<string> ids)
    {
        lock (_state)
        {
            var (found, missing) = _texts.Get(ids);
            return new MonitorTextsResult { Texts = found, Missing = missing };
        }
    }

    public HistoryResult GetHistory(long? seq)
    {
        lock (_state)
            return new HistoryResult
            {
                Ticks = _history.Ticks(), Snapshot = seq is { } s ? _history.At(s) : null,
                RetentionMinutes = _history.RetentionMinutes, Bytes = _history.Bytes, CapHit = _history.CapHit,
            };
    }

    public List<MonitorSnapshot> HistoryRange(long? from, long? to)
    {
        lock (_state) return _history.Range(from, to).ToList();
    }

    // ---------------------------------------------------------------- interactive jobs

    public Task<T> InteractiveAsync<T>(string kind, Func<IActivitySession, DbConnection, CancellationToken, Task<T>> job, long timeoutMs = 5_000)
    {
        MonitorScheduler? s;
        lock (_state) s = _connection == MonitorConnectionState.Connected ? _scheduler : null;
        if (s == null) throw new QuerithmException("disconnected", "The monitor is disconnected; try again once it reconnects.");
        return s.RunInteractiveAsync(kind, async ct =>
        {
            var (session, conn) = Current();
            if (session == null || conn == null) throw new QuerithmException("disconnected", "The monitor is disconnected.");
            try { return await job(session, conn, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not QuerithmException && ConnectionBroken)
            {
                Lost(ex);
                throw new QuerithmException("disconnected", "The monitor connection was lost.");
            }
        }, timeoutMs);
    }

    public async Task<KillPreview> KillPreviewAsync(SessionKey key)
    {
        var p = await InteractiveAsync("killPreview", (s, c, ct) => s.KillPreviewAsync(c, key, ct)).ConfigureAwait(false);
        return Enrich(p);
    }

    KillPreview Enrich(KillPreview p)
    {
        lock (_state)
        {
            var row = _latest?.GetValueOrDefault(p.Key);
            return p with
            {
                Risk = KillRisk.Classify(p),
                BlockedCount = BlockingGraph.VictimCount(_latestRows, p.Key.Id),
                Row = row,
                Text = row?.TextId is { } t ? _texts.TryGet(t) : null,
                RollingBack = p.RollingBack || _terminated.ContainsKey(p.Key) || row?.RollingBack == true,
            };
        }
    }

    public Task<RequestDetail> RequestDetailAsync(SessionKey key) =>
        InteractiveAsync("requestDetail", (s, c, ct) => s.RequestDetailAsync(c, key, ct));

    public Task<DeadlocksResult> DeadlocksAsync(long fromMs, long toMs) =>
        InteractiveAsync("deadlocks", async (s, c, ct) =>
        {
            var r = await s.DeadlocksAsync(c, fromMs, toMs, MaxDeadlocks, ct).ConfigureAwait(false);
            return new DeadlocksResult
            {
                Events = r.Value ?? [],
                Status = new PaneStatus { Pane = MonitorPane.Deadlocks, State = r.State, Reason = r.Reason, Total = r.Total },
                Truncated = r.State == PaneState.Truncated,
            };
        }, 10_000);

    public Task<string?> DeadlockXmlAsync(string eventId) =>
        InteractiveAsync("deadlockXml", (s, c, ct) => s.DeadlockXmlAsync(c, eventId, Time.NowMs() - 7 * 86_400_000L, Time.NowMs(), ct), 10_000);

    public async Task<Diagnosis> DiagnoseAsync(SessionKey key, WaitDictionary dictionary)
    {
        ActivityRow? row;
        lock (_state) row = _latest?.GetValueOrDefault(key);
        if (row == null) return Diagnoser.Run(key, null, [], null, null, null, Provider.Engine, dictionary);
        KillPreview? preview = null;
        RequestDetail? detail = null;
        try
        {
            (preview, detail) = await InteractiveAsync("diagnose", async (s, c, ct) =>
                (await s.KillPreviewAsync(c, key, ct).ConfigureAwait(false), await s.RequestDetailAsync(c, key, ct).ConfigureAwait(false)), 10_000).ConfigureAwait(false);
        }
        catch (QuerithmException) { /* rules that need fresh data are skipped and noted */ }
        ActivityRow[] rows;
        WaitDelta[]? waits;
        lock (_state)
        {
            rows = _latestRows;
            waits = null;
        }
        return Diagnoser.Run(key, row, rows, preview == null ? null : Enrich(preview), detail, waits, Provider.Engine, dictionary);
    }

    // ---------------------------------------------------------------- kill

    /// <summary>
    /// Guarded kill (B1c). Steps 1–6 without I/O; then on the idle monitor connection, else on a dedicated control
    /// connection (never a pool connection, 5 s connect timeout), else — when no control connection can be opened — the
    /// running read-only job is cancelled and the kill runs on the monitor connection. Kills on a profile are serialised.
    /// </summary>
    public async Task<KillResult> KillAsync(MonitorKillParams p, EnvironmentKind env)
    {
        KillResult result;
        lock (_state)
        {
            if (_stop.IsCancellationRequested)
                return Audit(p, new KillResult { Outcome = KillOutcome.Refused, Code = "monitorStopped", Message = "The monitor was stopped; nothing was sent." });
            if (!_killing.Add(p.Target))
                return Audit(p, new KillResult { Outcome = KillOutcome.Refused, Code = "inProgress", Message = "A kill of this session is already in progress." });
        }
        try { result = await KillCoreAsync(p, env).ConfigureAwait(false); }
        finally
        {
            TaskCompletionSource? done = null;
            lock (_state)
            {
                _killing.Remove(p.Target);
                if (_killing.Count == 0) { done = _killsDone; _killsDone = null; }
            }
            done?.TrySetResult();
        }
        return Audit(p, result);
    }

    /// <summary>Completed when the last running kill ends: stop waits for it before closing the connections it may use (C14).</summary>
    TaskCompletionSource? _killsDone;

    /// <summary>Stop waits this long for a running kill (its own limits: 5 s control open, 10 s statement, 15 s driver backstop).</summary>
    const int KillStopWaitMs = 16_000;

    KillResult Audit(MonitorKillParams p, KillResult r)
    {
        // K24: no query text, login or host — only the ids and the outcome.
        _log.LogInformation("Monitor kill: {Mode} session {Id} on profile {Profile}: {Outcome} {Code} via {Path}", p.Mode, p.Target.Id, Profile.Id, r.Outcome, r.Code ?? "-", r.Path ?? "-");
        return r;
    }

    async Task<KillResult> KillCoreAsync(MonitorKillParams p, EnvironmentKind env)
    {
        IActivitySession? session;
        MonitorScheduler? scheduler;
        bool connected;
        HashSet<SessionKey> rolling;
        lock (_state)
        {
            session = _session;
            scheduler = _scheduler;
            connected = _connection == MonitorConnectionState.Connected && session != null && scheduler != null;
            rolling = [.. _terminated.Keys];
        }
        var refusal = KillGuard.Check(new KillGuard.Context(p, env, connected, _latest, session?.Self, _sessions.Registry, session?.Capabilities, rolling));
        if (refusal != null) return refusal;
        // A Querithm connection reconnected mid-run and is not identified again yet: its new session would pass as foreign (K7).
        if (_sessions.Registry.HasReconnected())
            return new KillResult { Outcome = KillOutcome.Refused, Code = "querithmReconnecting", Message = ReconnectingMessage };
        if (UnidentifiedRefusal() is { } unidentified) return unidentified;

        if (!await _killLock.WaitAsync(TimeSpan.FromMilliseconds(KillLockWaitMs)).ConfigureAwait(false))
            return new KillResult { Outcome = KillOutcome.Refused, Code = "inProgress", Message = "Another kill is in progress on this server; try again." };
        try
        {
            // Preempting path 1: the monitor connection, when no job runs on it right now.
            var lease = scheduler!.TryClaimIdle();
            if (lease != null)
            {
                using (lease) return await RunKillAsync(session!, Current().Conn!, p, "monitor").ConfigureAwait(false);
            }
            // Path 2: a dedicated control connection outside the pool cap (same tunnel, same secrets).
            DbConnection? control = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                cts.CancelAfter(TimeSpan.FromMilliseconds(ControlConnectTimeoutMs));
                lock (_state) _controlOpen = true;
                control = await _sessions.OpenDedicatedAsync(Profile, Provider, MonitorDatabase, cts.Token, ConnectionRegistry.Kinds.Control, MonitorProfile).ConfigureAwait(false);
                Activity.PrepareMonitorConnection(control);
            }
            catch (Exception ex)
            {
                lock (_state) _controlOpen = false;
                _log.LogInformation("Monitor {Id}: control connection failed ({Message}); preempting the monitor connection", Id, ex.Message);
            }
            if (control != null)
            {
                try { return await RunKillAsync(session!, control, p, "control").ConfigureAwait(false); }
                finally
                {
                    try { await control.DisposeAsync().ConfigureAwait(false); } catch { /* closed */ }
                    lock (_state) _controlOpen = false;
                }
            }
            // Path 3 (bounded): cancel the running read-only monitor job, then use the monitor connection.
            var preempted = await scheduler.PreemptAsync(PreemptBoundMs).ConfigureAwait(false);
            if (preempted == null) return new KillResult { Outcome = KillOutcome.Failed, Code = "busy", Message = "The monitor is busy and no extra connection could be opened; try again.", Path = "fallback" };
            using (preempted) return await RunKillAsync(session!, Current().Conn!, p, "fallback").ConfigureAwait(false);
        }
        finally { _killLock.Release(); }
    }

    /// <summary>
    /// KillGuard step 5 again, right before the statement: the kill may have waited for the kill lock, a control connection
    /// and the server-side preview, and a Querithm connection registered (or reconnected) meanwhile is still refused (K8, K9).
    /// </summary>
    /// <summary>Kills are refused on every server while one of Querithm's connections is unidentified: it could be the target.</summary>
    const string ReconnectingMessage = "One of Querithm's own connections was reconnected by its driver and is not identified again yet, so it could be this session; nothing was sent. "
        + "Try again in a moment — if this persists, run a statement in the tab that reconnected or close it.";

    KillResult? LateQuerithmRefusal(SessionKey target)
    {
        if (_sessions.Registry.Find(target) is { } hit)
            return new KillResult { Outcome = KillOutcome.Refused, Code = "querithmConnection",
                Message = hit.Kind is ConnectionRegistry.Kinds.Monitor ? "This is the monitor's own connection." : KillGuard.QuerithmConnectionMessage };
        if (_sessions.Registry.HasReconnected())
            return new KillResult { Outcome = KillOutcome.Refused, Code = "querithmReconnecting", Message = ReconnectingMessage };
        return UnidentifiedRefusal();
    }

    /// <summary>
    /// One of Querithm's open connections could not read its own identity (the server refused it, §6 rule 7): its server
    /// session looks foreign, so every kill is refused until it closes — on every server, as for a reconnect (two profiles
    /// can point at the same server).
    /// </summary>
    KillResult? UnidentifiedRefusal() => _sessions.Registry.UnidentifiedReason() is { } why
        ? new KillResult
        {
            Outcome = KillOutcome.Refused, Code = "querithmUnidentified",
            Message = $"One of Querithm's own connections could not read its own session identity ({why}), so this session could be one of "
                + "Querithm's; nothing was sent. Kills work again once no Querithm connection to that server is open: close its tabs (idle "
                + "connections close after 5 minutes unused).",
        }
        : null;

    async Task<KillResult> RunKillAsync(IActivitySession session, DbConnection conn, MonitorKillParams p, string path)
    {
        // Linked to stop: stop waits for this kill before it closes the connection the kill runs on.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        // Stopped while this kill waited for the kill lock or a connection: nothing has been sent yet.
        if (_stop.IsCancellationRequested)
            return new KillResult { Outcome = KillOutcome.Refused, Code = "monitorStopped", Message = "The monitor was stopped; nothing was sent.", Path = path };
        try
        {
            if (LateQuerithmRefusal(p.Target) is { } early) return early with { Path = path };
            if (p.Mode == KillMode.TerminateSession)
            {
                // Step 7: rollback risk recomputed on the server now, never taken from the client.
                var preview = Enrich(await session.KillPreviewAsync(conn, p.Target, cts.Token).ConfigureAwait(false));
                // Ended between the snapshot and now (K5): nothing to kill, and not a reason to ask for a rollback confirm.
                if (preview.SessionEnded)
                    return new KillResult { Outcome = KillOutcome.Gone, Code = "gone", Message = "The session already ended; nothing was sent.", Path = path };
                if (preview.RollingBack)
                    return new KillResult { Outcome = KillOutcome.RollingBack, Code = "rollingBack", Message = "The session is already rolling back.", Path = path };
                if (KillRisk.NeedsLongRollbackConfirm(preview.Risk) && !p.ConfirmedLongRollback)
                    return new KillResult
                    {
                        Outcome = KillOutcome.Refused, Code = "longRollback", Path = path,
                        Message = preview.Risk == RollbackRisk.Unknown
                            ? $"The rollback cost cannot be read ({preview.Reason}); confirm that the rollback may take long."
                            : "Rolling back this transaction may take a long time and hold locks; confirm that first.",
                    };
                if (LateQuerithmRefusal(p.Target) is { } late) return late with { Path = path };
                var r = await session.TerminateSessionAsync(conn, p.Target, cts.Token).ConfigureAwait(false);
                if (r.Outcome == KillOutcome.Done && MonitorRules.For(Provider.Engine).RemembersTerminated) _terminated.TryAdd(p.Target, 0);
                return r with { Path = path };
            }
            return (await session.CancelRequestAsync(conn, p.Target, p.ExpectedStatementStart, cts.Token).ConfigureAwait(false)) with { Path = path };
        }
        catch (Exception ex) when (ex is not QuerithmException)
        {
            // A failed kill never falls back to closing one of our own connections; the failure is reported as is.
            if (conn == _conn && ConnectionBroken) Lost(ex);
            // Cut off after the statement may have reached the server: say so, never "nothing happened".
            if (ex is OperationCanceledException && cts.IsCancellationRequested)
                return new KillResult
                {
                    Outcome = KillOutcome.Failed, Code = _stop.IsCancellationRequested ? "monitorStopped" : "timeout", Path = path,
                    Message = (_stop.IsCancellationRequested ? "The monitor was stopped while the kill ran" : "The server did not answer the kill within 10 s")
                        + "; it may still take effect — check the session in the list before trying again.",
                };
            return new KillResult { Outcome = KillOutcome.Failed, Code = "serverError", Message = Describe(ex), Path = path };
        }
    }

    // ---------------------------------------------------------------- stop

    public async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        Task? kills;
        lock (_state)
        {
            // No more ticks or snapshots while stop waits for a running kill below.
            _scheduler?.Pause();
            _lost.TrySetResult();
            kills = _killing.Count > 0 ? (_killsDone ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : null;
        }
        // A running kill is cancelled by the stop and ends within its own limits; the connections it uses are closed after it.
        if (kills != null)
            try { await kills.WaitAsync(TimeSpan.FromMilliseconds(KillStopWaitMs)).ConfigureAwait(false); } catch (TimeoutException) { /* closed anyway */ }
        if (_run != null)
            try { await _run.ConfigureAwait(false); } catch { /* stopped */ }
        await TeardownAsync().ConfigureAwait(false);
        lock (_state)
        {
            _history.Clear();
            _connection = MonitorConnectionState.Stopped;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
