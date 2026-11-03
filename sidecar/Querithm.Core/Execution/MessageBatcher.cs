using Querithm.Contract;

namespace Querithm.Core.Execution;

/// <summary>
/// M13: one execution's messages as <c>query.messages</c> batches instead of one notification each. A message after a
/// quiet spell of <see cref="Interval"/> goes out at once; during a flood (a PRINT / RAISE NOTICE loop, thousands of
/// failing statements) the rest wait at most <see cref="Interval"/>, or until <see cref="MaxBatch"/> have gathered. The
/// run calls <see cref="Flush"/> before each of its other notifications (result set, plan, end), so the webview still sees
/// messages and result sets in the order they happened. At most <see cref="MaxMessages"/> server messages are forwarded
/// (the webview's own limit); the rest are counted and <see cref="Complete"/> reports them in one line. Querithm's own
/// lines (cancel escalation, a lost connection) are never capped: a run has only a few.
/// </summary>
public sealed class MessageBatcher(INotifier notifier, string executionId) : IDisposable
{
    public const int MaxMessages = 20_000;
    public const int MaxBatch = 500;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    readonly object _gate = new();
    readonly List<ServerMessage> _pending = [];
    Timer? _timer;
    bool _armed;
    bool _completed;
    int _forwarded;
    long _dropped;
    long _lastFlush = long.MinValue / 2;

    /// <summary>Server messages not forwarded so far (past <see cref="MaxMessages"/>).</summary>
    public long Dropped { get { lock (_gate) return _dropped; } }

    /// <param name="server">A message from the server (capped); false for Querithm's own lines.</param>
    public void Add(ServerMessage message, bool server = true)
    {
        lock (_gate)
        {
            if (server)
            {
                if (_forwarded >= MaxMessages)
                {
                    _dropped++;
                    return;
                }
                _forwarded++;
            }
            _pending.Add(message);
            var waited = Environment.TickCount64 - _lastFlush;
            if (_completed || _pending.Count >= MaxBatch || (!_armed && waited >= (long)Interval.TotalMilliseconds))
            {
                FlushLocked();
                return;
            }
            if (_armed) return;
            _armed = true;
            _timer ??= new Timer(_ => OnTimer());
            _timer.Change(Math.Max(1, (long)Interval.TotalMilliseconds - waited), Timeout.Infinite);
        }
    }

    /// <summary>Sends what is waiting now (before the run's next notification of another kind).</summary>
    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    /// <summary>
    /// The run ended: what is waiting goes out with the "N more messages" line, before query.done. Later messages (a late
    /// cancel warning) are sent one by one.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (!_completed && _dropped > 0)
                _pending.Add(new ServerMessage
                {
                    ExecutionId = executionId, Severity = Severity.Warning, Ts = Time.NowMs(),
                    Message = $"{_dropped:N0} more server messages were not shown: a run keeps the first {MaxMessages:N0}.",
                });
            _completed = true;
            FlushLocked();
            _timer?.Dispose();
            _timer = null;
            _armed = false;
        }
    }

    void OnTimer()
    {
        lock (_gate)
        {
            _armed = false;
            FlushLocked();
        }
    }

    // Under the gate, so batches leave in order; Notify only enqueues (RpcNotifier's channel), it never blocks.
    void FlushLocked()
    {
        if (_pending.Count == 0) return;
        notifier.Notify("query.messages", new QueryMessagesEvent { ExecutionId = executionId, Messages = [.. _pending] });
        _pending.Clear();
        _lastFlush = Environment.TickCount64;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
