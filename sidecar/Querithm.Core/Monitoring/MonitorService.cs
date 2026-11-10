using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;

namespace Querithm.Core.Monitoring;

/// <summary>
/// Owns the activity monitors (Phase B): start/stop (cap 8 per sidecar), RPC-boundary validation, and routing. Holds no
/// timing logic of its own: when work runs is each monitor's <see cref="MonitorScheduler"/>'s business.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
    public const int MaxMonitors = 8;
    public const int MinIntervalMs = 1_000, MaxIntervalMs = 30_000, DefaultIntervalMs = 2_000;
    public const int MaxTextIds = 500;

    public static readonly MonitorPane[] DefaultPanes =
        [MonitorPane.Sessions, MonitorPane.Locks, MonitorPane.Waits, MonitorPane.Metrics, MonitorPane.Io, MonitorPane.TopQueries, MonitorPane.Deadlocks];

    readonly SessionManager _sessions;
    readonly INotifier _notifier;
    readonly ILogger _log;
    readonly IMonitorClock _clock;
    readonly ISchemaSource? _schema;
    readonly MonitorExporter _exporter;
    readonly ConcurrentDictionary<string, ActivityMonitor> _monitors = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _killLocks = new();
    readonly Timer _sweep;
    readonly ConcurrentDictionary<string, ConcurrentDictionary<SessionKey, byte>> _terminated = new();
    readonly object _startGate = new();

    public MonitorService(SessionManager sessions, INotifier notifier, ILogger log, string dataDir, ISchemaSource? schema = null, IMonitorClock? clock = null)
    {
        _sessions = sessions;
        _notifier = notifier;
        _log = log;
        _schema = schema;
        _clock = clock ?? SystemMonitorClock.Instance;
        _exporter = new MonitorExporter(Path.Combine(dataDir, "exports"));
        // Staged exports left by an earlier run (off the startup path), then every 5 minutes: a session left open for days
        // with no further export must not keep a deadlock file (query text, logins, hosts) for days.
        _sweep = new Timer(_ => { try { _exporter.Sweep(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } },
            null, TimeSpan.Zero, TimeSpan.FromMinutes(5));
        sessions.ProfileForgotten += id => _ = StopForProfileAsync(id);
    }

    /// <summary>Tests: schedulers run inline on a fake clock.</summary>
    public bool InlineSchedulers { get; init; }
    WaitDictionary? _dictionary;
    /// <summary>Loaded on first use (diagnosis, hover cards), not at startup.</summary>
    public WaitDictionary Dictionary { get => _dictionary ??= WaitDictionary.Default; init => _dictionary = value; }
    public MonitorExporter Exporter => _exporter;

    public event Action<ActivityMonitor>? MonitorStarted;
    public event Action<ActivityMonitor>? MonitorStopped;

    public IEnumerable<ActivityMonitor> All => _monitors.Values;
    /// <summary>
    /// A monitor's recent read of every session answers the own-tab blocking banner for the profile (O13). A paused,
    /// reconnecting, timing-out, filtered or truncated monitor does not, and the banner's shared check runs instead.
    /// </summary>
    public bool AnswersBannerFor(string profileId) => _monitors.Values.Any(m => m.Profile.Id == profileId && !m.Stopped && m.AnswersOwnSessions);

    public MonitorStartResult Start(MonitorStartParams p)
    {
        if (p == null || string.IsNullOrEmpty(p.ProfileId)) throw QuerithmException.Invalid("monitor.start needs a profileId.");
        var interval = p.IntervalMs ?? DefaultIntervalMs;
        if (interval is < MinIntervalMs or > MaxIntervalMs) throw QuerithmException.Invalid($"The interval must be {MinIntervalMs / 1000}–{MaxIntervalMs / 1000} s.");
        var panes = ValidatePanes(p.Panes ?? DefaultPanes);
        if (p.DatabaseFilter is { Length: > 128 }) throw QuerithmException.Invalid("The database filter is too long.");
        var profile = _sessions.GetProfile(p.ProfileId);
        var provider = _sessions.Providers.Get(profile.Engine);
        if (provider.Activity == null) throw new QuerithmException("unsupported", $"The activity monitor is not available for {profile.Engine}.");
        FeatureGuard.Require(provider, Feature.Monitor);
        if (!_sessions.CanOpen(profile)) throw new QuerithmException("needsSecret", "Connect to the server first; the monitor uses the connection's credentials.");
        ActivityMonitor m;
        lock (_startGate)
        {
            // Monitors that stopped themselves (refused, sign-in rejected) and were never stopped by the UI.
            foreach (var (stoppedId, _) in _monitors.Where(kv => kv.Value.Stopped).ToList()) _monitors.TryRemove(stoppedId, out _);
            if (_monitors.Values.Count(x => !x.Stopped) >= MaxMonitors)
                throw new QuerithmException("tooManyMonitors", $"Up to {MaxMonitors} activity monitors can run at once; close one first.");
            m = new ActivityMonitor(Ulid.NewId(), profile, provider, _sessions, _notifier, _log, _clock, _schema,
                _killLocks.GetOrAdd(profile.Id, _ => new SemaphoreSlim(1, 1)), interval, panes, p.DatabaseFilter, InlineSchedulers,
                _terminated.GetOrAdd(profile.Id, _ => new()));
            _monitors[m.Id] = m;
        }
        MonitorStarted?.Invoke(m);
        m.Start();
        return new MonitorStartResult { MonitorId = m.Id, IntervalMs = interval, Panes = panes, Engine = profile.Engine };
    }

    static MonitorPane[] ValidatePanes(MonitorPane[] panes)
    {
        if (panes.Length > 16) throw QuerithmException.Invalid("Too many panes.");
        foreach (var pane in panes)
        {
            if (!Enum.IsDefined(pane)) throw QuerithmException.Invalid("Unknown pane.");
            if (pane is MonitorPane.QueryStore or MonitorPane.Health) throw QuerithmException.Invalid($"The {pane} pane is not available yet.");
        }
        return [.. panes.Distinct()];
    }

    /// <summary>Unknown or stopped monitors are refused (K16).</summary>
    public ActivityMonitor Get(string? monitorId) =>
        monitorId != null && _monitors.TryGetValue(monitorId, out var m) && !m.Stopped ? m : throw QuerithmException.NotFound("Monitor", monitorId ?? "");

    public async Task StopAsync(string monitorId)
    {
        if (!_monitors.TryRemove(monitorId, out var m)) return;
        await m.StopAsync().ConfigureAwait(false);
        MonitorStopped?.Invoke(m);
    }

    /// <summary>Profile disconnected, deleted, forgotten or edited: its monitors stop and say why (L3).</summary>
    public async Task StopForProfileAsync(string profileId, string reason = "The server was disconnected.")
    {
        foreach (var m in _monitors.Values.Where(m => m.Profile.Id == profileId).ToList())
        {
            await StopAsync(m.Id).ConfigureAwait(false);
            _notifier.Notify("monitor.stopped", new MonitorStoppedEvent { MonitorId = m.Id, Reason = reason });
        }
    }

    public void SetPanes(MonitorSetPanesParams p) => Get(p.MonitorId).SetPanes(ValidatePanes(p.Panes ?? throw QuerithmException.Invalid("Panes are required.")));

    public void SetInterval(MonitorSetIntervalParams p)
    {
        if (p.IntervalMs is < MinIntervalMs or > MaxIntervalMs) throw QuerithmException.Invalid($"The interval must be {MinIntervalMs / 1000}–{MaxIntervalMs / 1000} s.");
        Get(p.MonitorId).SetInterval(p.IntervalMs);
    }

    public void SetTopQueries(MonitorTopQueriesParams p)
    {
        if (p.OrderBy is not ("cpu" or "duration" or "reads" or "executions")) throw QuerithmException.Invalid("Order by cpu, duration, reads or executions.");
        Get(p.MonitorId).SetTopQueries(p.OrderBy, p.SinceStart);
    }

    public MonitorTextsResult Texts(MonitorTextsParams p)
    {
        if (p.Ids == null || p.Ids.Length > MaxTextIds) throw QuerithmException.Invalid($"monitor.texts takes at most {MaxTextIds} ids.");
        foreach (var id in p.Ids)
            if (id == null || !TextStore.IsValidId(id)) throw QuerithmException.Invalid("A text id is 32 lowercase hex characters.");
        return Get(p.MonitorId).GetTexts(p.Ids);
    }

    public Task<KillResult> KillAsync(MonitorKillParams p)
    {
        if (p == null) throw QuerithmException.Invalid("monitor.kill needs parameters.");
        // K24: every attempt is audited, also those refused here before reaching the monitor (ids and outcome only).
        if (p.MonitorId == null || !_monitors.TryGetValue(p.MonitorId, out var found) || found.Stopped)
        {
            _log.LogInformation("Monitor kill: {Mode} session {Id}: Refused unknownMonitor at the RPC boundary", p.Mode, p.Target?.Id);
            throw QuerithmException.NotFound("Monitor", p.MonitorId ?? "");
        }
        var m = found;
        if (KillGuard.ValidateShape(p, m.Activity, m.Profile.Engine) is { } invalid)
        {
            _log.LogInformation("Monitor kill: {Mode} session {Id} on profile {Profile}: Refused invalid at the RPC boundary", p.Mode, p.Target?.Id, m.Profile.Id);
            throw QuerithmException.Invalid(invalid);
        }
        // The environment is read now from the store, not from the monitor's start: a profile switched to Prod is guarded.
        var env = (_sessions.TryGetProfile(m.Profile.Id) ?? m.Profile).Environment;
        return m.KillAsync(p, env);
    }

    SessionKey ValidKey(ActivityMonitor m, SessionKey? key)
    {
        // The same id range as a kill (SQL Server spids fit a smallint; providers narrow the id to int).
        var max = MonitorRules.For(m.Profile.Engine).MaxSessionId;
        if (key == null || key.Id < 1 || key.Id > max || key.Instance == null || key.Instance.Length is 0 or > 64 || !m.Activity.IsValidInstance(key.Instance))
            throw QuerithmException.Invalid("Invalid session identity.");
        return key;
    }

    public Task<KillPreview> KillPreviewAsync(MonitorSessionParams p)
    {
        var m = Get(p.MonitorId);
        return m.KillPreviewAsync(ValidKey(m, p.Key));
    }

    public Task<Diagnosis> DiagnoseAsync(MonitorSessionParams p)
    {
        var m = Get(p.MonitorId);
        return m.DiagnoseAsync(ValidKey(m, p.Key), Dictionary);
    }

    public Task<RequestDetail> RequestDetailAsync(MonitorSessionParams p)
    {
        var m = Get(p.MonitorId);
        return m.RequestDetailAsync(ValidKey(m, p.Key));
    }

    public WaitInfo WaitInfo(WaitInfoParams p)
    {
        if (p.WaitType == null || p.WaitType.Length > 128) throw QuerithmException.Invalid("Invalid wait type.");
        return Dictionary.Info(p.Engine, p.WaitType);
    }

    public Task<DeadlocksResult> DeadlocksAsync(DeadlocksParams p)
    {
        var to = p.ToMs ?? Time.NowMs();
        var from = p.FromMs ?? to - 24 * 3_600_000L;
        // from ≥ 0 and from ≤ to keep to - from from overflowing past the 7-day check.
        if (from < 0 || from > to || to - from > 7 * 86_400_000L) throw QuerithmException.Invalid("The deadlock window is at most 7 days.");
        return Get(p.MonitorId).DeadlocksAsync(from, to);
    }

    /// <summary>The original deadlock XML as an SSMS-compatible .xdl under &lt;data dir&gt;/exports; contains query text, so it needs a confirm (X14).</summary>
    public async Task<MonitorFileResult> DeadlockXmlAsync(DeadlockXmlParams p)
    {
        if (!p.ConfirmedText) throw new QuerithmException("notConfirmed", "The deadlock file contains query text; confirm the export first.");
        if (p.EventId == null || !TextStore.IsValidId(p.EventId)) throw QuerithmException.Invalid("Invalid deadlock id.");
        var xml = await Get(p.MonitorId).DeadlockXmlAsync(p.EventId).ConfigureAwait(false)
                  ?? throw new QuerithmException("notFound", "The deadlock is no longer in the server's buffers.");
        return _exporter.WriteText($"deadlock-{p.EventId[..12]}", ".xdl", xml);
    }

    public HistoryResult History(HistoryParams p) => Get(p.MonitorId).GetHistory(p.Seq);

    public void HistorySettings(HistorySettingsParams p)
    {
        if (p.RetentionMinutes is < 1 or > 60) throw QuerithmException.Invalid("History keeps 1–60 minutes.");
        Get(p.MonitorId).SetRetention(p.RetentionMinutes);
    }

    /// <summary>B6 export: JSON under &lt;data dir&gt;/exports; query text, login and host only when the user ticked "include query text" (§13).</summary>
    public MonitorFileResult Export(MonitorExportParams p)
    {
        var m = Get(p.MonitorId);
        var ticks = m.HistoryRange(p.FromSeq, p.ToSeq);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        return _exporter.WriteJson($"monitor-{m.Profile.Name}", w =>
        {
            w.WriteStartObject();
            w.WriteString("format", "querithm-monitor-history");
            w.WriteNumber("version", 1);
            w.WriteString("profile", m.Profile.Name);
            w.WriteString("engine", Store.EnumText.Of(m.Profile.Engine));
            w.WriteNumber("exportedAt", Time.NowMs());
            w.WriteBoolean("includesText", p.IncludeText);
            w.WriteStartArray("ticks");
            foreach (var s in ticks)
            {
                var rows = (s.Sessions ?? []).Select(r => p.IncludeText ? r : r with { Login = null, Host = null, Program = null, TextId = null }).ToArray();
                if (p.IncludeText)
                    foreach (var (id, t) in s.Texts ?? []) texts.TryAdd(id, t);
                var tick = s with
                {
                    Sessions = rows, Texts = null, Capabilities = null,
                    TopQueries = p.IncludeText ? s.TopQueries : s.TopQueries?.Select(q => q with { TextId = null }).ToArray(),
                    Locks = s.Locks, NewDeadlocks = null,
                };
                JsonSerializer.Serialize(w, tick, Json.Monitor.MonitorSnapshot);
            }
            w.WriteEndArray();
            if (p.IncludeText)
            {
                w.WriteStartObject("texts");
                foreach (var (id, t) in texts) w.WriteString(id, t);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });
    }

    /// <summary>Sidecar shutdown: every monitor stops within 2 s (L9).</summary>
    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync().ConfigureAwait(false);
        var all = _monitors.Values.ToList();
        _monitors.Clear();
        await Task.WhenAny(Task.WhenAll(all.Select(m => m.StopAsync())), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }
}
