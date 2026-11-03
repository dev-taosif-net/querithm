using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;

namespace Querithm.Core.Diagnostics;

/// <summary>
/// SQL Server Trace (§11): an Extended Events session filtered on the session's SPID, polled every 500 ms.
/// The session's connection is pinned for the trace's lifetime so the SPID filter stays valid.
/// This is a trace, not a debugger; the UI never calls it one.
/// </summary>
public sealed class TraceService(SessionManager sessions, INotifier notifier, ILogger log) : IAsyncDisposable
{
    public const string Prefix = "querithm_trace_";

    sealed class ActiveTrace(string id, Session session, PhysicalConnection control, string name)
    {
        public string Id { get; } = id;
        public Session Session { get; } = session;
        public PhysicalConnection Control { get; } = control;
        public string Name { get; } = name;
        public CancellationTokenSource Stop { get; } = new();
        public Task? Poller { get; set; }
        public long LastSeq { get; set; }
    }

    readonly ConcurrentDictionary<string, ActiveTrace> _traces = new();

    public async Task<TraceRef> StartAsync(TraceStartParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var provider = s.Provider.Trace ?? throw new QuerithmException("unsupported", "Trace is only available for SQL Server.");
        if (_traces.Values.FirstOrDefault(t => t.Session.Id == s.Id) is { } existing) return new TraceRef { TraceId = existing.Id };
        if (s.ActiveExecutionId != null) throw new QuerithmException("busy", "The session is executing a query.");

        // Pin the user's connection so subsequent executions keep the traced SPID.
        if (s.Pinned == null)
        {
            var (conn, _) = await sessions.AcquireAsync(s, null, ct).ConfigureAwait(false);
            sessions.Pin(s, conn);
        }
        s.TracePinned = true;
        var spid = await s.Provider.GetSpidAsync(s.Pinned!.Db, ct).ConfigureAwait(false)
                   ?? throw new QuerithmException("noSpid", "Could not determine the session's SPID.");
        s.Spid = spid;

        var control = await sessions.PoolFor(s).BorrowAsync(s.Database, null, ct).ConfigureAwait(false);
        var check = await provider.CheckAsync(control.Db, ct).ConfigureAwait(false);
        if (!check.Available)
        {
            await control.Pool.ReturnAsync(control).ConfigureAwait(false);
            await UnpinAsync(s).ConfigureAwait(false);
            throw new QuerithmException("permission", check.Reason ?? "Trace is not available.");
        }
        var name = $"{Prefix}{spid}";
        try { await provider.StartAsync(control.Db, name, spid, ct).ConfigureAwait(false); }
        catch
        {
            await control.Pool.ReturnAsync(control).ConfigureAwait(false);
            await UnpinAsync(s).ConfigureAwait(false);
            throw;
        }
        var trace = new ActiveTrace(Ulid.NewId(), s, control, name);
        _traces[trace.Id] = trace;
        trace.Poller = Task.Run(() => PollAsync(trace));
        notifier.Notify("session.stateChanged", s.State());
        return new TraceRef { TraceId = trace.Id };
    }

    async Task PollAsync(ActiveTrace t)
    {
        var provider = t.Session.Provider.Trace!;
        while (!t.Stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, t.Stop.Token).ConfigureAwait(false);
                var events = await provider.PollAsync(t.Control.Db, t.Name, t.LastSeq, t.Stop.Token).ConfigureAwait(false);
                if (events.Count > 0)
                {
                    t.LastSeq = events.Max(e => e.Seq);
                    notifier.Notify("trace.event", new TraceEventsEvent { TraceId = t.Id, SessionId = t.Session.Id, Events = events.ToArray() });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Trace poll failed: {Message}", ex.Message);
                notifier.Notify("trace.event", new TraceEventsEvent { TraceId = t.Id, SessionId = t.Session.Id, Events = [], Error = ex.Message });
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    public async Task StopAsync(TraceRef p)
    {
        if (!_traces.TryRemove(p.TraceId, out var t)) return;
        t.Stop.Cancel();
        try { if (t.Poller != null) await t.Poller.ConfigureAwait(false); } catch { /* stopped */ }
        try
        {
            using var cleanup = Cleanup.Start();
            await t.Session.Provider.Trace!.StopAsync(t.Control.Db, t.Name, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { log.LogWarning("Trace drop failed: {Message}", ex.Message); }
        await t.Control.Pool.ReturnAsync(t.Control).ConfigureAwait(false);
        await UnpinAsync(t.Session).ConfigureAwait(false);
        notifier.Notify("trace.event", new TraceEventsEvent { TraceId = t.Id, SessionId = t.Session.Id, Events = [], Stopped = true });
        notifier.Notify("session.stateChanged", t.Session.State());
    }

    public Task StopForSessionAsync(string sessionId) =>
        Task.WhenAll(_traces.Values.Where(t => t.Session.Id == sessionId).Select(t => StopAsync(new TraceRef { TraceId = t.Id })));

    async Task UnpinAsync(Session s)
    {
        s.TracePinned = false;
        if (s.Pinned is { } pinned && s.TxState != TxState.Open)
        {
            using var cleanup = Cleanup.Start();
            await sessions.RefreshTxStateAsync(s, pinned, cleanup.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Drops any querithm_trace_* sessions this sidecar owns (§11, on shutdown).</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in _traces.Keys.ToList()) await StopAsync(new TraceRef { TraceId = id }).ConfigureAwait(false);
    }
}
