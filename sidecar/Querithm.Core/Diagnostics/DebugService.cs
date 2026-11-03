using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Sessions;

namespace Querithm.Core.Diagnostics;

/// <summary>Step debugger sessions (§11). Only PostgreSQL pldebugger implements IDebugProvider in v1.</summary>
public sealed class DebugService(SessionManager sessions, INotifier notifier, ILogger log, Execution.QueryGuard guard) : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, IDebugSession> _sessions = new();

    /// <summary>
    /// The call runs on a target connection of its own, so it is caller SQL like any query: guarded first (§13 rule 7 —
    /// refusals, the first run on Prod, destructive statements in the call text), and blocked until confirmed.
    /// </summary>
    public async Task<DebugAttachResult> AttachAsync(DebugAttachParams p, CancellationToken ct)
    {
        var s = sessions.Get(p.SessionId);
        var dbg = s.Provider.Debugger ?? throw new QuerithmException("unsupported", "A step debugger is only available for PostgreSQL with pldebugger.");
        if (guard.Check(s, s.Provider.Language.Classify(p.CallSql), p.ConfirmedProd, p.ConfirmedDestructive) is { } block)
            return new DebugAttachResult { Blocked = block };
        var id = Ulid.NewId();
        var profile = s.Profile;
        var database = s.Database;
        // Target and control are dedicated connections, never pooled (§11: two connections).
        async Task<System.Data.Common.DbConnection> Open(CancellationToken c) =>
            await sessions.OpenDedicatedAsync(profile, s.Provider, database, c).ConfigureAwait(false);
        var session = await dbg.AttachAsync(Open, p.FunctionOid, p.CallSql, e => notifier.Notify("debug.event", e), id, ct).ConfigureAwait(false);
        try
        {
            foreach (var line in p.Breakpoints ?? [])
                await session.SetBreakpointAsync(p.FunctionOid, line, ct).ConfigureAwait(false);
        }
        catch
        {
            // The webview never learns this id: nothing could detach it, and its target would wait inside the function.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _sessions[id] = session;
        return new DebugAttachResult { DebugId = id };
    }

    IDebugSession Get(string id) => _sessions.TryGetValue(id, out var s) ? s : throw QuerithmException.NotFound("Debug session", id);

    public Task SetBreakpointAsync(DebugBreakpointParams p, CancellationToken ct) => Get(p.DebugId).SetBreakpointAsync(p.FunctionOid, p.Line, ct);
    public Task DropBreakpointAsync(DebugBreakpointParams p, CancellationToken ct) => Get(p.DebugId).DropBreakpointAsync(p.FunctionOid, p.Line, ct);
    public Task StepAsync(DebugStepParams p, CancellationToken ct) => Get(p.DebugId).StepAsync(p.Kind, ct);

    public async Task<DebugSource> SourceAsync(DebugSourceParams p, CancellationToken ct) =>
        new() { Source = await Get(p.DebugId).GetSourceAsync(p.FunctionOid, ct).ConfigureAwait(false) };

    public async Task DetachAsync(DebugRef p)
    {
        if (_sessions.TryRemove(p.DebugId, out var s))
        {
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning("Debug detach failed: {Message}", ex.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList()) await DetachAsync(new DebugRef { DebugId = id }).ConfigureAwait(false);
    }
}
