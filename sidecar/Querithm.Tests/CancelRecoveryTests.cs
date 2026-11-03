using System.Data;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Contract.Providers;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// Cancel always ends a run (§7 step 4, §14 #15b), whatever its dependencies do: a data plane nobody reads, a server that
/// never acknowledges the driver's cancel, a run queued or still connecting, a grid / import write. Each case failed
/// (hung) before its fix: the tab spun forever, Cancel did nothing and the session stayed locked.
/// </summary>
public class CancelRecoveryTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);
    const string Stuck = "WAITFOR DELAY '01:00:00'";

    /// <summary>A data plane with no reader: every write waits until its token is cancelled.</summary>
    sealed class StalledFrames : IFrameSink
    {
        public readonly TaskCompletionSource FirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            FirstWrite.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>The data plane server's answer when no reader attached in time.</summary>
    sealed class UnavailableFrames : IFrameSink
    {
        public ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            ValueTask.FromException(new DataPlaneUnavailableException());
    }

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; } = new();
        public SessionManager Sessions { get; }
        public CapturingNotifier Notifier { get; } = new();
        public ExecutionService Executions { get; }
        readonly Profile _profile;

        public Rig(IFrameSink? frames = null)
        {
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            _profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(_profile.Id, "x");
            Executions = new ExecutionService(Sessions, frames ?? new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null)
            {
                FrameDrainLimit = Grace, CancelGrace = Grace, StuckLimit = Grace,
            };
        }

        /// <summary>Set when a run reaches the stuck server call (see <see cref="StuckOn"/>).</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Commands containing <paramref name="text"/> run <paramref name="stuck"/>; everything else returns one row.</summary>
        public void StuckOn(string text, Func<FakeDbCommand, CancellationToken, Task<FakeResult>> stuck) =>
            Provider.Handler = (cmd, ct) =>
            {
                if (!cmd.CommandText.Contains(text)) return Task.FromResult(FakeResult.Rows(("a", typeof(int))));
                Entered.TrySetResult();
                return stuck(cmd, ct);
            };

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);

        public string Run(Session s, string sql) => Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = sql }).ExecutionId!;

        public Task<QueryDoneEvent> Done(string id) => Notifier.WaitDone(id, (int)Wait.TotalMilliseconds);

        public IEnumerable<ServerMessage> Messages(string id) => Notifier.Of<ServerMessage>().Where(m => m.ExecutionId == id);

        public async Task Until(Func<bool> condition)
        {
            var end = DateTime.UtcNow + Wait;
            while (!condition())
            {
                if (DateTime.UtcNow > end) throw new TimeoutException("condition not met");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    /// <summary>A server that never acknowledges the cancel: the call ends only when its connection is closed under it.</summary>
    static async Task<FakeResult> UntilClosed(FakeDbCommand cmd)
    {
        while (cmd.Fake.State == ConnectionState.Open) await Task.Delay(10);
        throw new InvalidOperationException("The connection was closed.");
    }

    // ------------------------------------------------------------------ data plane

    [Fact]
    public async Task Cancel_ends_a_run_whose_frames_nobody_reads_and_frees_the_session()
    {
        var frames = new StalledFrames();
        await using var rig = new Rig(frames);
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.Rows(("a", typeof(int))));
        var s = rig.Open();

        var run = rig.Run(s, "SELECT 1 AS a");
        await frames.FirstWrite.Task.WaitAsync(Wait); // parked on the schema frame
        Assert.True(rig.Executions.Cancel(run).Cancelled);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
        Assert.Null(s.ActiveExecutionId);
        Assert.Equal(1, s.ExecLock.CurrentCount); // the next run on this tab is not queued behind it
    }

    [Fact]
    public async Task A_cancelled_runs_end_frame_waits_the_cancel_grace_not_the_long_drain_limit()
    {
        var frames = new StalledFrames();
        await using var rig = new Rig(frames);
        // The drain limit outlasts the shell's paused reader (35 s, §5); a cancelled run must still end within 10 s (§14 #15b).
        rig.Executions.FrameDrainLimit = TimeSpan.FromMinutes(5);
        rig.Provider.Handler = (_, _) => Task.FromResult(FakeResult.Rows(("a", typeof(int))));
        var s = rig.Open();

        var run = rig.Run(s, "SELECT 1 AS a");
        await frames.FirstWrite.Task.WaitAsync(Wait);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Executions.Cancel(run);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
        Assert.True(sw.Elapsed < Wait / 2, $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task With_no_data_plane_reader_the_run_fails_once_with_the_reason_and_runs_no_later_batch()
    {
        await using var rig = new Rig(new UnavailableFrames());
        var ran = new List<string>();
        rig.Provider.Handler = (cmd, _) =>
        {
            lock (ran) ran.Add(cmd.CommandText.Trim());
            return Task.FromResult(FakeResult.Rows(("a", typeof(int))));
        };
        var s = rig.Open();

        var run = rig.Run(s, "SELECT 1 AS a\nGO\nSELECT 2 AS a");
        var done = await rig.Done(run);

        Assert.Equal(ExecState.Failed, done.State);
        Assert.Contains("results channel", done.Error!.Message);
        Assert.Single(rig.Messages(run), m => m.Severity == Severity.Error);
        Assert.DoesNotContain(ran, t => t.Contains("SELECT 2"));
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }

    [Fact]
    public async Task The_data_plane_refuses_a_frame_when_no_client_attaches_in_time_then_at_once_until_one_does()
    {
        await using var server = new DataPlaneServer("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromMilliseconds(200));
        var states = new List<bool>();
        server.StateChanged += states.Add;
        await Assert.ThrowsAsync<DataPlaneUnavailableException>(() =>
            server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask().WaitAsync(Wait));

        // Already known to be missing: no second wait, no second notification.
        var again = server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.True(again.IsFaulted);
        await Assert.ThrowsAsync<DataPlaneUnavailableException>(() => again.AsTask());
        Assert.Equal([false], states);

        var client = new MemoryStream();
        await server.AttachAsync(client);
        Assert.Equal([false, true], states);
        await server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask().WaitAsync(Wait);
        Assert.Equal(23, client.Length);
    }

    [Fact]
    public async Task A_frame_written_before_the_client_attaches_waits_for_it()
    {
        await using var server = new DataPlaneServer("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromSeconds(10));
        var write = server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(write.IsCompleted);
        var client = new MemoryStream();
        await server.AttachAsync(client);
        await write.WaitAsync(Wait);
        Assert.Equal(23, client.Length);
    }

    // ------------------------------------------------------------------ a frame cut off mid-write (H2)

    /// <summary>
    /// A shell that took <c>allow</c> bytes and then stopped reading (its queue for that run is full): each later write
    /// waits until its token fires or <see cref="Drain"/> lets everything through. Records every byte it received.
    /// </summary>
    sealed class ChokedStream(int allow) : Stream
    {
        readonly MemoryStream _got = new();
        readonly TaskCompletionSource _drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _allow = allow;
        public readonly TaskCompletionSource Choked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Disposed;

        public byte[] Bytes { get { lock (_got) return _got.ToArray(); } }
        public void Drain() => _drain.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            while (!buffer.IsEmpty)
            {
                var n = _drain.Task.IsCompleted ? buffer.Length : Math.Min(buffer.Length, _allow);
                lock (_got) _got.Write(buffer.Span[..n]);
                _allow -= n;
                buffer = buffer[n..];
                if (buffer.IsEmpty) break;
                Choked.TrySetResult();
                await _drain.Task.WaitAsync(ct);
                ObjectDisposedException.ThrowIf(Disposed, this);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>The whole frames at the start of <paramref name="bytes"/> (kind, execution id) and the bytes left after them.</summary>
    static (List<(byte Kind, string Id)> Frames, int Left) Parse(byte[] bytes)
    {
        var frames = new List<(byte, string)>();
        var at = 0;
        while (bytes.Length - at >= 4)
        {
            var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
            if (bytes.Length - at - 4 < len) break;
            frames.Add((bytes[at + 4], new Ulid(bytes.AsSpan(at + 5, 16).ToArray()).ToString()));
            at += 4 + len;
        }
        return (frames, bytes.Length - at);
    }

    static DataPlaneServer Server(TimeSpan grace) =>
        new("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Wait) { FrameGrace = grace };

    [Fact]
    public async Task A_frame_cancelled_mid_write_drops_the_client_instead_of_leaving_it_torn_before_the_next_frame()
    {
        await using var server = Server(Grace);
        var choked = new ChokedStream(allow: 23 + 1000);
        await server.AttachAsync(choked);
        using var cts = new CancellationTokenSource();
        var write = server.WriteFrameAsync(FrameKind.Batch, Ulid.New(), 0, new byte[1_000_000], cts.Token).AsTask();
        await choked.Choked.Task.WaitAsync(Wait);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Wait));
        Assert.True(sw.Elapsed < Wait / 2, $"took {sw.Elapsed}"); // the grace, not the reader, bounds it
        Assert.True(choked.Disposed); // the connection holding part of a frame is gone: the shell reconnects
        var torn = choked.Bytes.Length;
        Assert.Equal(23 + 1000, torn);

        // The next frame waits for the reconnect and arrives whole on the new connection; nothing follows the torn one.
        using var bound = new CancellationTokenSource(Wait);
        var id = Ulid.New();
        var next = server.WriteFrameAsync(FrameKind.EndOfExecution, id, 0, ReadOnlyMemory<byte>.Empty, bound.Token).AsTask();
        var fresh = new MemoryStream();
        await server.AttachAsync(fresh);
        await next.WaitAsync(Wait);
        var (frames, rest) = Parse(fresh.ToArray());
        Assert.Equal([(FrameKind.EndOfExecution, id.ToString())], frames);
        Assert.Equal(0, rest);
        Assert.Equal(torn, choked.Bytes.Length);
    }

    [Fact]
    public async Task A_frame_cancelled_mid_write_still_completes_when_the_reader_catches_up_within_the_grace()
    {
        await using var server = Server(Wait);
        var choked = new ChokedStream(allow: 23 + 1000);
        await server.AttachAsync(choked);
        using var cts = new CancellationTokenSource();
        var id = Ulid.New();
        var write = server.WriteFrameAsync(FrameKind.Batch, id, 0, new byte[100_000], cts.Token).AsTask();
        await choked.Choked.Task.WaitAsync(Wait);

        cts.Cancel();
        await Task.Delay(100);
        Assert.False(write.IsCompleted); // a cancel never cuts a frame that has started
        choked.Drain();
        await write.WaitAsync(Wait);
        Assert.False(choked.Disposed);

        await server.WriteFrameAsync(FrameKind.EndOfExecution, id, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask().WaitAsync(Wait);
        var (frames, rest) = Parse(choked.Bytes);
        Assert.Equal([(FrameKind.Batch, id.ToString()), (FrameKind.EndOfExecution, id.ToString())], frames);
        Assert.Equal(0, rest);
    }

    [Fact]
    public async Task A_frame_the_shell_would_refuse_as_too_long_fails_its_run_before_a_byte_is_written()
    {
        await using var server = Server(Grace);
        var client = new MemoryStream();
        await server.AttachAsync(client);
        var big = new PhantomPayload().Of(FrameLimits.MaxLength - FrameLimits.HeaderBody + 1);
        var ex = await Assert.ThrowsAsync<FrameTooLargeException>(() => server.WriteFrameAsync(FrameKind.Batch, Ulid.New(), 0, big, CancellationToken.None).AsTask());
        Assert.Contains("Select fewer or shorter columns", ex.Message);
        Assert.Equal(0, client.Length);
        // The connection is untouched: the next frame goes out as usual.
        await server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(23, client.Length);
    }

    /// <summary>A payload with a length and no bytes behind it (256 MB is not allocated just to be refused).</summary>
    sealed class PhantomPayload : System.Buffers.MemoryManager<byte>
    {
        public Memory<byte> Of(int length) => CreateMemory(length);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("the refused payload was read");
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new InvalidOperationException("the refused payload was read");
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Fact]
    public async Task Cancel_under_backpressure_ends_the_run_promptly_and_another_tab_s_results_arrive_whole()
    {
        // The frame's grace is shorter than the cancel escalation's first step, as in the app (3 s and 5 s).
        await using var server = Server(Grace / 3);
        await using var rig = new Rig(server);
        // About 6 MB of text: the first batch (4 MB) is far more than the stalled shell takes.
        var wide = Enumerable.Range(0, 6_000).Select(i => new object?[] { new string('x', 1_000) }).ToArray();
        rig.Provider.Handler = (cmd, _) => Task.FromResult(cmd.CommandText.Contains("big")
            ? new FakeResult([FakeResult.Table([("t", typeof(string))], wide)])
            : new FakeResult([FakeResult.Table([("a", typeof(int))], new object?[] { 1 })]));
        var choked = new ChokedStream(allow: 100_000);
        await server.AttachAsync(choked);
        var s1 = rig.Open();
        var s2 = rig.Open();

        var big = rig.Run(s1, "SELECT big");
        await choked.Choked.Task.WaitAsync(Wait); // parked mid-batch: the shell stopped reading
        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Executions.Cancel(big);
        Assert.Equal(ExecState.Cancelled, (await rig.Done(big)).State);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal(1, s1.ExecLock.CurrentCount);

        // The torn connection was dropped, not reused: only whole frames, then the cut one, and nothing after it.
        Assert.True(choked.Disposed);
        var cut = choked.Bytes;
        var (old, rest) = Parse(cut);
        Assert.Equal([(FrameKind.Schema, big)], old);
        Assert.True(rest > 0);

        // The shell reconnects; the other tab's run streams on the new connection, every frame whole and in order.
        var fresh = new MemoryStream();
        await server.AttachAsync(fresh);
        var other = rig.Run(s2, "SELECT 1 AS a");
        Assert.Equal(ExecState.Done, (await rig.Done(other)).State);
        var (frames, left) = Parse(fresh.ToArray());
        Assert.Equal(0, left);
        Assert.Equal([FrameKind.Schema, FrameKind.Batch, FrameKind.EndOfResultSet, FrameKind.EndOfExecution], frames.Where(f => f.Id == other).Select(f => f.Kind));
        Assert.Equal(cut.Length, choked.Bytes.Length); // nothing more reached the old connection
    }

    // ------------------------------------------------------------------ server ignores the cancel

    [Fact]
    public async Task A_server_that_ignores_cancel_gets_its_connection_closed_and_the_tab_is_free_again()
    {
        await using var rig = new Rig();
        rig.StuckOn("WAITFOR", (cmd, _) => UntilClosed(cmd));
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
        Assert.Contains(rig.Messages(run), m => m.Severity == Severity.Warning && m.Message.Contains("closed this tab's connection"));
        Assert.NotEqual(ConnectionState.Open, rig.Provider.Opened.Single().State);
        Assert.Equal(1, s.ExecLock.CurrentCount);

        // The next run gets a fresh connection and works.
        var next = rig.Run(s, "SELECT 1 AS a");
        Assert.Equal(ExecState.Done, (await rig.Done(next)).State);
        Assert.Equal(2, rig.Provider.Opened.Count);
    }

    // ------------------------------------------------------------------ cancel racing completion (M10)

    [Fact]
    public async Task A_cancel_that_lands_after_the_last_statement_completed_reports_the_run_as_done()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var id = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The statement completed (its rows were read); the cancel arrives while the run reads the batch's warnings.
        rig.Provider.AfterBatch = async ct =>
        {
            rig.Executions.Cancel(await id.Task);
            ct.ThrowIfCancellationRequested();
        };
        var run = rig.Run(s, "UPDATE t SET a = 1");
        id.SetResult(run);

        var done = await rig.Done(run);
        Assert.Equal(ExecState.Done, done.State);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task A_cancel_mid_statement_whose_driver_then_returns_normally_reports_the_run_as_cancelled()
    {
        await using var rig = new Rig();
        // MySQL's KILL QUERY makes SELECT SLEEP(n) return 1 with no error: the driver call ends normally after the cancel.
        rig.StuckOn("SLEEP", async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return FakeResult.Rows(("s", typeof(int)));
        });
        var s = rig.Open();

        var run = rig.Run(s, "SELECT SLEEP(30)");
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        var done = await rig.Done(run);
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.Null(done.Error);
        Assert.Equal(1, s.ExecLock.CurrentCount);
        Assert.Equal(ExecState.Done, (await rig.Done(rig.Run(s, "SELECT 1 AS a"))).State);
    }

    [Fact]
    public async Task A_late_cancel_after_a_failed_batch_reports_the_failure_with_its_first_error()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var id = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = (cmd, _) => cmd.CommandText.Contains("boom") ? throw new InvalidOperationException("boom failed") : Task.FromResult(FakeResult.Rows(("b", typeof(int))));
        // The first batch fails, the last one completes, then the cancel arrives: the run failed, and says why.
        rig.Provider.AfterBatch = async ct =>
        {
            rig.Executions.Cancel(await id.Task);
            ct.ThrowIfCancellationRequested();
        };
        var run = rig.Run(s, "SELECT boom\nGO\nSELECT 2 AS b");
        id.SetResult(run);

        var done = await rig.Done(run);
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Equal("boom failed", done.Error?.Message);
    }

    [Fact]
    public async Task A_cancel_between_batches_still_stops_the_run()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var id = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = 0;
        rig.Provider.AfterBatch = async ct =>
        {
            if (Interlocked.Increment(ref batches) == 1) rig.Executions.Cancel(await id.Task);
            ct.ThrowIfCancellationRequested();
        };
        var run = rig.Run(s, "SELECT 1 AS a\nGO\nSELECT 2 AS b");
        id.SetResult(run);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task Closing_the_connection_of_a_run_inside_an_open_transaction_says_the_transaction_is_gone()
    {
        await using var rig = new Rig();
        rig.StuckOn("WAITFOR", (cmd, _) => UntilClosed(cmd));
        var s = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Done(rig.Run(s, "BEGIN TRAN"))).State);
        Assert.NotNull(s.Pinned);

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        await rig.Done(run);
        Assert.Contains(rig.Messages(run), m => m.Message.Contains("any open transaction was rolled back"));
        Assert.Null(s.Pinned);
        Assert.Equal(TxState.None, s.TxState);
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }

    [Fact]
    public async Task A_run_that_stops_on_cancel_keeps_its_connection_and_the_escalation_never_closes_it_later()
    {
        await using var rig = new Rig();
        rig.StuckOn("WAITFOR", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeResult.None; });
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);
        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);

        await Task.Delay(Grace * 3); // past both escalation steps
        var conn = rig.Provider.Opened.Single();
        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.DoesNotContain(rig.Messages(run), m => m.Message.Contains("closed this tab's connection") || m.Message.Contains("has not stopped"));
        Assert.Equal(ExecState.Done, (await rig.Done(rig.Run(s, "SELECT 1 AS a"))).State);
        Assert.Single(rig.Provider.Opened); // reused, not replaced
    }

    [Fact]
    public async Task A_run_that_even_closing_its_connection_cannot_stop_is_reported_with_the_way_out()
    {
        await using var rig = new Rig();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.StuckOn("WAITFOR", async (_, _) => { await release.Task; throw new InvalidOperationException("gone"); });
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        await rig.Until(() => rig.Messages(run).Any(m => m.Severity == Severity.Error && m.Message.Contains("Restart the engine")));
        release.SetResult();
        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
    }

    [Fact]
    public async Task A_cancelled_run_still_finishing_its_cleanup_is_told_so_and_not_sent_to_restart_the_engine()
    {
        await using var rig = new Rig();
        rig.Executions.StuckLimit = TimeSpan.FromSeconds(30);
        var cancelled = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.Contains("WAITFOR")) { rig.Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            // The state read after the cancelled statement: slow on a busy server, bounded by Cleanup.Limit in the run.
            if (cmd.CommandText == "SELECT @@TRANCOUNT" && Volatile.Read(ref cancelled) == 1) await release.Task;
            return FakeResult.Rows(("a", typeof(int)));
        };
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        Volatile.Write(ref cancelled, 1);
        rig.Executions.Cancel(run);

        await rig.Until(() => rig.Messages(run).Any(m => m.Message.Contains("still finishing up")));
        Assert.DoesNotContain(rig.Messages(run), m => m.Message.Contains("estart the engine"));
        release.SetResult();
        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
    }

    [Fact]
    public async Task A_run_still_in_its_driver_call_is_told_it_may_end_by_itself_before_it_is_reported_stuck()
    {
        await using var rig = new Rig();
        rig.Provider.CloseAbortsCommand = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.StuckOn("WAITFOR", async (_, _) => { await release.Task; throw new InvalidOperationException("gone"); });
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        await rig.Until(() => rig.Messages(run).Any(m => m.Severity == Severity.Warning && m.Message.Contains("may still end by itself")));
        await rig.Until(() => rig.Messages(run).Any(m => m.Severity == Severity.Error && m.Message.Contains("Restart the engine")));
        var order = rig.Messages(run).Select(m => m.Message).ToList();
        Assert.True(order.FindIndex(m => m.Contains("may still end by itself")) < order.FindIndex(m => m.Contains("Restart the engine")));
        release.SetResult();
        Assert.Equal(ExecState.Cancelled, (await rig.Done(run)).State);
    }

    [Fact]
    public async Task On_an_engine_where_closing_mid_command_is_unsafe_the_connection_is_kept_and_the_stuck_run_is_reported()
    {
        await using var rig = new Rig();
        rig.Provider.CloseAbortsCommand = false; // SQLite / DuckDB: in-process native engines
        rig.StuckOn("WAITFOR", (cmd, _) => UntilClosed(cmd));
        var s = rig.Open();

        var run = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        await rig.Until(() => rig.Messages(run).Any(m => m.Severity == Severity.Error && m.Message.Contains("Restart the engine")));
        var conn = rig.Provider.Opened.Single();
        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.DoesNotContain(rig.Messages(run), m => m.Message.Contains("closed this tab's connection"));
        conn.Close(); // what a restart does
        await rig.Done(run);
    }

    // ------------------------------------------------------------------ queued, connecting, tool runs

    [Fact]
    public async Task Cancelling_a_run_queued_behind_a_stuck_one_ends_it_at_once()
    {
        await using var rig = new Rig();
        rig.StuckOn("WAITFOR", (cmd, _) => UntilClosed(cmd));
        var s = rig.Open();
        var first = rig.Run(s, Stuck);
        await rig.Entered.Task.WaitAsync(Wait);

        var queued = rig.Run(s, "SELECT 1 AS a");
        await rig.Until(() => rig.Notifier.Of<QueryQueuedEvent>().Any(q => q.ExecutionId == queued));
        rig.Executions.Cancel(queued);
        var done = await rig.Done(queued).WaitAsync(Grace); // well before any escalation
        Assert.Equal(ExecState.Cancelled, done.State);
        Assert.Equal(first, s.ActiveExecutionId); // the stuck run is untouched

        rig.Executions.Cancel(first);
        await rig.Done(first);
    }

    [Fact]
    public async Task Cancel_while_the_connection_is_still_opening_ends_the_run()
    {
        await using var rig = new Rig();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.OnOpen = async ct => { opening.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var s = rig.Open();

        var run = rig.Run(s, "SELECT 1 AS a");
        await opening.Task.WaitAsync(Wait);
        rig.Executions.Cancel(run);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(run).WaitAsync(Grace)).State);
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }

    [Fact]
    public async Task A_grid_or_import_write_stuck_on_its_connection_is_ended_by_cancel()
    {
        await using var rig = new Rig();
        var s = rig.Open();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var write = rig.Executions.RunToolAsync<int>(s, "-- write", async ctx =>
        {
            entered.TrySetResult();
            while (ctx.Connection.Db.State == ConnectionState.Open) await Task.Delay(10); // ignores ctx.Ct, like an unacknowledged cancel
            throw new InvalidOperationException("The connection was closed.");
        }, n => (n, null), started.SetResult);

        var id = await started.Task.WaitAsync(Wait);
        await entered.Task.WaitAsync(Wait);
        rig.Executions.Cancel(id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => write.WaitAsync(Wait));
        Assert.Equal(ExecState.Cancelled, (await rig.Done(id)).State);
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }

    // ------------------------------------------------------------------ plans and interactive RPCs (M5)

    /// <summary>A plan provider whose EXPLAIN is one command on the run's connection (what every real one does).</summary>
    sealed class CommandPlans : Contract.Providers.IPlanProvider
    {
        public async Task<PlanResult> GetPlanAsync(System.Data.Common.DbConnection c, string sql, PlanMode mode, CancellationToken ct)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN " + sql;
            await cmd.ExecuteNonQueryAsync(ct);
            return new PlanResult { Plan = new PlanTree { Statements = [] }, Raw = "[]", Format = "test" };
        }

        public PlanTree Parse(string raw) => new() { Statements = [] };
    }

    [Fact]
    public async Task Cancel_ends_an_actual_plan_on_a_server_that_never_acknowledges_the_cancel()
    {
        await using var rig = new Rig();
        rig.Provider.PlansImpl = new CommandPlans();
        rig.StuckOn("EXPLAIN", (cmd, _) => UntilClosed(cmd));
        var s = rig.Open();

        var id = rig.Executions.Plan(new PlanParams { SessionId = s.Id, Sql = "DELETE FROM t WHERE id > 0", Mode = PlanMode.Actual, ConfirmedRun = true }).ExecutionId!;
        await rig.Entered.Task.WaitAsync(Wait);
        Assert.Equal(id, s.ActiveExecutionId); // a run the tab's Cancel reaches
        Assert.True(rig.Executions.Cancel(id).Cancelled);

        Assert.Equal(ExecState.Cancelled, (await rig.Done(id)).State);
        Assert.Equal(1, s.ExecLock.CurrentCount);
        Assert.Null(s.ActiveExecutionId);
        // The escalation closed the connection the plan's transaction was on: it is discarded, never pooled again.
        var conn = Assert.Single(rig.Provider.Opened);
        Assert.NotEqual(ConnectionState.Open, conn.State);
        Assert.Contains("BEGIN TRANSACTION", conn.Log);
    }

    [Fact]
    public async Task A_cancel_that_lands_during_the_actual_plans_begin_still_rolls_its_transaction_back()
    {
        await using var rig = new Rig();
        rig.Provider.PlansImpl = new CommandPlans();
        string? id = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The server ran BEGIN; the cancel arrives before its reply, so the driver reports the command cancelled.
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText == "BEGIN TRANSACTION")
            {
                cmd.Fake.TxDepth++;
                await ready.Task;
                rig.Executions.Cancel(id!);
                throw new OperationCanceledException(ct);
            }
            return FakeResult.None;
        };
        var s = rig.Open();

        id = rig.Executions.Plan(new PlanParams { SessionId = s.Id, Sql = "DELETE FROM t WHERE id > 0", Mode = PlanMode.Actual, ConfirmedRun = true }).ExecutionId!;
        ready.SetResult();

        Assert.Equal(ExecState.Cancelled, (await rig.Done(id)).State);
        var conn = Assert.Single(rig.Provider.Opened);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", conn.Log);
        Assert.DoesNotContain(conn.Log, l => l.StartsWith("EXPLAIN", StringComparison.Ordinal));
        Assert.Equal(0, conn.TxDepth);
        // Never pinned as the tab's "open transaction": the plan's own BEGIN was undone.
        Assert.Equal((TxState.None, (PhysicalConnection?)null), (s.TxState, s.Pinned));
    }

    [Fact]
    public async Task A_cancel_that_lands_during_the_actual_plans_savepoint_leaves_the_users_transaction_open()
    {
        await using var rig = new Rig();
        rig.Provider.PlansImpl = new CommandPlans();
        var s = rig.Open();
        Assert.Equal(ExecState.Done, (await rig.Done(rig.Run(s, "BEGIN TRAN"))).State);
        string? id = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Provider.Handler = async (cmd, ct) =>
        {
            if (cmd.CommandText.StartsWith("SAVE TRANSACTION", StringComparison.Ordinal))
            {
                await ready.Task;
                rig.Executions.Cancel(id!);
                throw new OperationCanceledException(ct);
            }
            return FakeResult.None;
        };

        id = rig.Executions.Plan(new PlanParams { SessionId = s.Id, Sql = "DELETE FROM t WHERE id > 0", Mode = PlanMode.Actual, ConfirmedRun = true }).ExecutionId!;
        ready.SetResult();

        Assert.Equal(ExecState.Cancelled, (await rig.Done(id)).State);
        var conn = (FakeDbConnection)s.Pinned!.Db;
        // No rollback to a savepoint that may not exist, and never the user's whole transaction.
        Assert.DoesNotContain(conn.Log, l => l.Contains("ROLLBACK", StringComparison.Ordinal));
        Assert.Equal(TxState.Open, s.TxState);
    }

    /// <summary>A catalog that reads definitions with one command (the RPC path every engine's metadata takes).</summary>
    sealed class CommandCatalog : Contract.Providers.IMetadataProvider
    {
        public async Task<string?> GetDefinitionAsync(System.Data.Common.DbConnection c, ObjectRef obj, CancellationToken ct)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT definition FROM catalog";
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }

        public Task<IReadOnlyList<ObjectStamp>> GetStampsAsync(System.Data.Common.DbConnection c, CancellationToken ct) => throw new NotSupportedException();
        public Task<MetadataBundle> GetDetailsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(long Id, string? Definition)>> GetDefinitionsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dependency[]> GetDependenciesAsync(System.Data.Common.DbConnection c, ObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
        public string ToEditableScript(string definition, Capabilities caps) => definition;
        public Task<RowCount[]> GetRowCountsAsync(System.Data.Common.DbConnection c, IReadOnlyList<long> objectIds, CancellationToken ct) => throw new NotSupportedException();
    }

    static async Task<(TestHostWithFake Host, string SessionId)> CatalogHost(Func<FakeDbCommand, CancellationToken, Task<FakeResult>> definitionRead, bool closeAborts)
    {
        var h = new TestHostWithFake(TimeSpan.FromMilliseconds(300));
        h.Services.Objects.StopGrace = TimeSpan.FromMilliseconds(100);
        h.Provider.CloseAbortsCommand = closeAborts;
        h.Provider.MetadataImpl = new CommandCatalog();
        h.Provider.Handler = (cmd, ct) => cmd.CommandText.Contains("definition") ? definitionRead(cmd, ct) : Task.FromResult(FakeResult.Rows(("a", typeof(int))));
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "srv", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        return (h, s.SessionId);
    }

    static Task<DefinitionResult> ReadDefinition(TestHostWithFake h, string sid) =>
        h.Rpc.MetaDefinition(new DefinitionParams { SessionId = sid, Object = new ObjectRef { Schema = "dbo", Name = "v", Kind = ObjectKind.View } });

    [Fact]
    public async Task A_catalog_read_on_a_server_that_never_answers_ends_at_its_deadline_and_frees_the_connection()
    {
        var (h, sid) = await CatalogHost((cmd, _) => UntilClosed(cmd), closeAborts: true); // SQL Server: the cancel is never acknowledged
        await using var _ = h;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<QuerithmException>(() => ReadDefinition(h, sid)).WaitAsync(Wait);

        Assert.Equal("timeout", ex.Code);
        Assert.Contains("did not finish within", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        // The connection under the stuck read was closed, so the pool slot is not held for good.
        Assert.Contains(h.Provider.Opened, c => c.State != ConnectionState.Open);
    }

    [Fact]
    public async Task A_catalog_read_ends_at_its_deadline_where_the_driver_honours_the_token()
    {
        var (h, sid) = await CatalogHost(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return FakeResult.None; }, closeAborts: false);
        await using var _ = h;
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => ReadDefinition(h, sid)).WaitAsync(Wait);
        Assert.Equal("timeout", ex.Code);
        Assert.All(h.Provider.Opened, c => Assert.Equal(ConnectionState.Open, c.State)); // nothing closed: the cancel ended it
    }
}
