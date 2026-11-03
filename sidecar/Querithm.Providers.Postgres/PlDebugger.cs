using System.Data.Common;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Postgres;

/// <summary>
/// PL/pgSQL step debugger via the pldebugger extension (pldbgapi, §11). A <i>target</i> connection runs the
/// user's call; a <i>control</i> connection drives it with a global breakpoint on the function.
/// </summary>
public sealed class PlDebugger : IDebugProvider
{
    public async Task<CapabilityInfo> CheckAsync(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pldbgapi'), EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pldbgapi')";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        if (r.GetBoolean(0)) return new CapabilityInfo { Available = true };
        return new CapabilityInfo
        {
            Available = false,
            Reason = r.GetBoolean(1)
                ? "pldebugger is installed on the server but not in this database: run CREATE EXTENSION pldbgapi."
                : "The pldebugger extension is not installed on this server.",
        };
    }

    public async Task<IDebugSession> AttachAsync(Func<CancellationToken, Task<DbConnection>> open, long functionOid, string callSql,
        Action<DebugEvent> onEvent, string debugId, CancellationToken ct)
    {
        var target = (NpgsqlConnection)await open(ct).ConfigureAwait(false);
        var control = (NpgsqlConnection)await open(ct).ConfigureAwait(false);
        var s = new Session(debugId, target, control, functionOid, onEvent);
        try
        {
            await s.StartAsync(callSql, ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            await s.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    sealed class Session(string id, NpgsqlConnection target, NpgsqlConnection control, long functionOid, Action<DebugEvent> onEvent) : IDebugSession
    {
        int _listener;
        Task? _targetRun;
        readonly CancellationTokenSource _targetCts = new();
        readonly SemaphoreSlim _gate = new(1, 1);
        volatile bool _finished;

        public async Task StartAsync(string callSql, CancellationToken ct)
        {
            _listener = Convert.ToInt32(await Scalar("SELECT pldbg_create_listener()", ct).ConfigureAwait(false));
            await Scalar($"SELECT pldbg_set_global_breakpoint({_listener}, {functionOid}::oid, NULL::int, {target.ProcessID})", ct).ConfigureAwait(false);

            // The target blocks inside the function until the control steps it.
            _targetRun = Task.Run(async () =>
            {
                var messages = new List<string>();
                target.Notice += (_, e) => messages.Add(e.Notice.MessageText);
                try
                {
                    await using var cmd = target.CreateCommand();
                    cmd.CommandText = callSql;
                    await using var r = await cmd.ExecuteReaderAsync(_targetCts.Token).ConfigureAwait(false);
                    var rows = new List<string>();
                    do
                        while (await r.ReadAsync(_targetCts.Token).ConfigureAwait(false) && rows.Count < 20)
                            rows.Add(string.Join(", ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL" : r.GetValue(i)?.ToString())));
                    while (await r.NextResultAsync(_targetCts.Token).ConfigureAwait(false));
                    _finished = true;
                    onEvent(new DebugEvent
                    {
                        DebugId = id, Kind = DebugEventKind.Finished,
                        Message = string.Join("\n", messages.Concat(rows.Count > 0 ? ["Result: " + string.Join(" | ", rows)] : [])),
                    });
                }
                catch (Exception ex)
                {
                    _finished = true;
                    onEvent(new DebugEvent { DebugId = id, Kind = ex is OperationCanceledException ? DebugEventKind.Finished : DebugEventKind.Error, Message = ex.Message });
                }
            });

            await Scalar($"SELECT pldbg_wait_for_target({_listener})", ct).ConfigureAwait(false);
            var bp = await BreakpointAsync($"SELECT func::bigint, linenumber FROM pldbg_wait_for_breakpoint({_listener})", ct).ConfigureAwait(false);
            onEvent(new DebugEvent { DebugId = id, Kind = DebugEventKind.Attached, FunctionOid = functionOid });
            await PausedAsync(bp, ct).ConfigureAwait(false);
        }

        async Task<object?> Scalar(string sql, CancellationToken ct)
        {
            await using var cmd = control.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        async Task<(long Func, int Line)?> BreakpointAsync(string sql, CancellationToken ct)
        {
            await using var cmd = control.CreateCommand();
            cmd.CommandText = sql;
            var step = Task.Run(async () =>
            {
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0) ? ((long, int)?)(r.GetInt64(0), r.GetInt32(1)) : null;
            }, ct);
            // If the target finishes while the control waits, there is no further breakpoint.
            var done = await Task.WhenAny(step, _targetRun ?? Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (done == step) return await step.ConfigureAwait(false);
            try { cmd.Cancel(); } catch { /* ignore */ }
            try { await step.ConfigureAwait(false); } catch { /* cancelled */ }
            return null;
        }

        async Task PausedAsync((long Func, int Line)? bp, CancellationToken ct)
        {
            if (bp == null || _finished) return;
            var stack = new List<DebugFrame>();
            await using (var cmd = control.CreateCommand())
            {
                cmd.CommandText = $"SELECT level, targetname, func::bigint, linenumber, args FROM pldbg_get_stack({_listener})";
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    stack.Add(new DebugFrame { Level = r.GetInt32(0), Function = r.GetString(1), FunctionOid = r.GetInt64(2), Line = r.GetInt32(3), Args = r.IsDBNull(4) ? null : r.GetString(4) });
            }
            var vars = new List<DebugVariable>();
            await using (var cmd = control.CreateCommand())
            {
                cmd.CommandText = $"SELECT name, varclass::text, format_type(dtype, NULL), value, value IS NULL FROM pldbg_get_variables({_listener})";
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    vars.Add(new DebugVariable { Name = r.GetString(0), VarClass = r.GetString(1), Type = r.IsDBNull(2) ? null : r.GetString(2), Value = r.IsDBNull(3) ? null : r.GetString(3), IsNull = r.GetBoolean(4) });
            }
            onEvent(new DebugEvent { DebugId = id, Kind = DebugEventKind.Paused, FunctionOid = bp.Value.Func, Line = bp.Value.Line, Stack = stack.ToArray(), Variables = vars.ToArray() });
        }

        public async Task SetBreakpointAsync(long oid, int line, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { await Scalar($"SELECT pldbg_set_breakpoint({_listener}, {oid}::oid, {line})", ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        public async Task DropBreakpointAsync(long oid, int line, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { await Scalar($"SELECT pldbg_drop_breakpoint({_listener}, {oid}::oid, {line})", ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        public async Task StepAsync(DebugStepKind kind, CancellationToken ct)
        {
            if (_finished) return;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                onEvent(new DebugEvent { DebugId = id, Kind = DebugEventKind.Running });
                var fn = kind switch { DebugStepKind.Into => "pldbg_step_into", DebugStepKind.Over => "pldbg_step_over", _ => "pldbg_continue" };
                var bp = await BreakpointAsync($"SELECT func::bigint, linenumber FROM {fn}({_listener})", ct).ConfigureAwait(false);
                await PausedAsync(bp, ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task<string> GetSourceAsync(long oid, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { return (string?)await Scalar($"SELECT pldbg_get_source({_listener}, {oid}::oid)", ct).ConfigureAwait(false) ?? ""; }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_finished && _listener != 0)
                try
                {
                    using var cleanup = Cleanup.Start();
                    await Scalar($"SELECT pldbg_abort_target({_listener})", cleanup.Token).ConfigureAwait(false);
                }
                catch { /* target gone */ }
            _targetCts.Cancel();
            if (_targetRun != null) try { await _targetRun.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* ignore */ }
            await target.DisposeAsync().ConfigureAwait(false);
            await control.DisposeAsync().ConfigureAwait(false);
        }
    }
}
