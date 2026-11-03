using System.Data;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// M13: a run's server messages reach the webview as coalesced query.messages batches, capped per run with one closing
/// line, in order with its result sets; the notifier's queue is bounded. L13: past 65,535 result sets a run stops
/// streaming more instead of wrapping the u16 result index.
/// </summary>
public class MessageFloodTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    static ServerMessage Msg(int i, string id = "e1") => new() { ExecutionId = id, Message = $"m{i}", Ts = i };

    static List<QueryMessagesEvent> Batches(CapturingNotifier n) =>
        n.All.Where(x => x.Method == "query.messages").Select(x => (QueryMessagesEvent)x.Payload).ToList();

    // ------------------------------------------------------------------ the batcher

    [Fact]
    public void A_flood_goes_out_in_few_batches_in_order()
    {
        var n = new CapturingNotifier();
        using var b = new MessageBatcher(n, "e1");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5_000; i++) b.Add(Msg(i));
        b.Complete();
        sw.Stop();
        var batches = Batches(n);
        // One at once (after a quiet spell), then batches of 500 or per interval: never one notification per message. The
        // bound holds however slow the runner is (8a review): full batches, plus per interval elapsed at most one timer
        // flush and one first-after-a-quiet-spell send, plus the first send and Complete's.
        var intervals = (int)Math.Ceiling(sw.Elapsed / MessageBatcher.Interval);
        Assert.InRange(batches.Count, 2, 5_000 / MessageBatcher.MaxBatch + 2 + 2 * intervals);
        Assert.True(batches.Count < 5_000 / 10, $"{batches.Count} batches for 5,000 messages");
        Assert.Equal(Enumerable.Range(0, 5_000).Select(i => $"m{i}"), batches.SelectMany(x => x.Messages).Select(m => m.Message));
        Assert.DoesNotContain(n.All, x => x.Method == "query.message");
    }

    [Fact]
    public void A_message_after_a_quiet_spell_is_sent_at_once()
    {
        var n = new CapturingNotifier();
        using var b = new MessageBatcher(n, "e1");
        b.Add(Msg(1));
        Assert.Single(Batches(n));
    }

    [Fact]
    public async Task A_waiting_message_goes_out_within_the_interval_without_a_flush()
    {
        var n = new CapturingNotifier();
        using var b = new MessageBatcher(n, "e1");
        b.Add(Msg(1));
        b.Add(Msg(2)); // within the interval: waits for the timer
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Batches(n).SelectMany(x => x.Messages).Count() < 2)
        {
            Assert.True(DateTime.UtcNow < end, "the waiting message was never sent");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Server_messages_past_the_cap_are_counted_and_reported_once_but_own_lines_always_pass()
    {
        var n = new CapturingNotifier();
        using var b = new MessageBatcher(n, "e1");
        for (var i = 0; i < MessageBatcher.MaxMessages + 1_234; i++) b.Add(Msg(i));
        b.Add(new ServerMessage { ExecutionId = "e1", Severity = Severity.Warning, Message = "own line" }, server: false);
        Assert.Equal(1_234, b.Dropped);
        b.Complete();
        var all = Batches(n).SelectMany(x => x.Messages).ToList();
        Assert.Equal(MessageBatcher.MaxMessages + 2, all.Count);
        Assert.Contains(all, m => m.Message == "own line");
        Assert.Equal("1,234 more server messages were not shown: a run keeps the first 20,000.", all[^1].Message);
        Assert.Equal(Severity.Warning, all[^1].Severity);
    }

    [Fact]
    public void After_complete_each_message_is_sent_at_once()
    {
        var n = new CapturingNotifier();
        using var b = new MessageBatcher(n, "e1");
        b.Complete();
        b.Add(Msg(1), server: false);
        b.Add(Msg(2), server: false);
        Assert.Equal(2, Batches(n).Count);
    }

    // ------------------------------------------------------------------ the notifier queue

    [Fact]
    public void The_notifier_queue_drops_detail_past_its_capacity_but_never_a_runs_end()
    {
        // Not attached: nothing drains, as when the shell stopped reading stdout.
        var n = new RpcNotifier(capacity: 100);
        for (var i = 0; i < 1_000; i++) n.Notify("query.messages", new QueryMessagesEvent { ExecutionId = "e", Messages = [] });
        Assert.Equal(100, n.Queued);
        Assert.Equal(900, n.Dropped);
        n.Notify("query.done", new QueryDoneEvent { ExecutionId = "e", SessionId = "s", State = ExecState.Done });
        n.Notify("monitor.snapshot", new object());
        Assert.Equal(101, n.Queued);
        Assert.Equal(901, n.Dropped);
    }

    // ------------------------------------------------------------------ through a run

    sealed class CountingFrames : IFrameSink
    {
        public int Schemas;
        public readonly HashSet<ushort> Indexes = [];

        public ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            lock (Indexes)
            {
                if (kind == FrameKind.Schema) Schemas++;
                Indexes.Add(resultIndex);
            }
            return ValueTask.CompletedTask;
        }
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
            Executions = new ExecutionService(Sessions, frames ?? new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null);
        }

        public Session Open() => Sessions.Get(Sessions.Open(new SessionOpenParams { ProfileId = _profile.Id, Secret = "x" }).SessionId);
        public string Run(Session s, string sql) => Executions.Execute(new QueryExecuteParams { SessionId = s.Id, Sql = sql }).ExecutionId!;
        public Task<QueryDoneEvent> Done(string id) => Notifier.WaitDone(id, (int)Wait.TotalMilliseconds);

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_print_flood_is_batched_capped_and_ordered_before_the_result_set()
    {
        await using var rig = new Rig();
        const int printed = 30_000;
        rig.Provider.Handler = (cmd, _) =>
        {
            if (!cmd.CommandText.Contains("PRINT")) return Task.FromResult(FakeResult.None); // the state reads after the run
            // A WHILE loop of PRINTs, then a SELECT: the driver raises each message as the batch runs.
            for (var i = 0; i < printed; i++) rig.Provider.Emit!(new ProviderMessage(Severity.Info, $"line {i}"));
            return Task.FromResult(FakeResult.Rows(("a", typeof(int))));
        };
        var s = rig.Open();
        var id = rig.Run(s, "WHILE 1 = 1 PRINT 'x'; SELECT 1 AS a");
        var done = await rig.Done(id);
        Assert.Equal(ExecState.Done, done.State);

        var events = rig.Notifier.All.ToList();
        Assert.DoesNotContain(events, e => e.Method == "query.message");
        var batches = events.Where(e => e.Method == "query.messages").Select(e => (QueryMessagesEvent)e.Payload).Where(b => b.ExecutionId == id).ToList();
        Assert.InRange(batches.Count, 2, MessageBatcher.MaxMessages / MessageBatcher.MaxBatch + 10);
        var lines = batches.SelectMany(b => b.Messages).ToList();
        Assert.Equal(MessageBatcher.MaxMessages + 1, lines.Count);
        Assert.Equal("line 0", lines[0].Message);
        Assert.Equal($"line {MessageBatcher.MaxMessages - 1}", lines[MessageBatcher.MaxMessages - 1].Message);
        Assert.StartsWith("10,000 more server messages were not shown", lines[^1].Message);
        // Every message the server sent before the rows reaches the webview before the result set, and all before the end.
        var resultSet = events.FindIndex(e => e.Method == "query.resultSet");
        var lastBeforeRows = events.FindIndex(e => e.Payload is QueryMessagesEvent b && b.Messages.Any(m => m.Message == $"line {MessageBatcher.MaxMessages - 1}"));
        Assert.True(lastBeforeRows < resultSet, $"messages at {lastBeforeRows}, result set at {resultSet}");
        Assert.True(events.FindLastIndex(e => e.Method == "query.messages") < events.FindIndex(e => e.Method == "query.done"));
    }

    [Fact]
    public async Task Errors_from_many_failing_statements_are_batched_too()
    {
        await using var rig = new Rig();
        rig.Provider.Handler = (cmd, _) =>
        {
            if (!cmd.CommandText.Contains("FROM t")) return Task.FromResult(FakeResult.None);
            for (var i = 0; i < 2_000; i++) rig.Provider.Emit!(new ProviderMessage(Severity.Error, $"Invalid object name 't{i}'.", Line: 1));
            return Task.FromResult(FakeResult.None);
        };
        var s = rig.Open();
        var id = rig.Run(s, "SELECT * FROM t");
        var done = await rig.Done(id);
        Assert.Equal(ExecState.Failed, done.State);
        Assert.Equal("Invalid object name 't0'.", done.Error?.Message);
        var batches = Batches(rig.Notifier).Where(b => b.ExecutionId == id).ToList();
        Assert.Equal(2_000, batches.Sum(b => b.Messages.Length));
        Assert.True(batches.Count <= 2_000 / MessageBatcher.MaxBatch + 3, $"{batches.Count} notifications");
    }

    [Fact]
    public async Task Past_65535_result_sets_a_run_stops_streaming_them_with_one_message_and_still_ends()
    {
        var frames = new CountingFrames();
        await using var rig = new Rig(frames);
        var table = FakeResult.Table([("a", typeof(int))]);
        const int sets = ExecutionService.MaxResultSets + 5;
        rig.Provider.Handler = (cmd, _) => Task.FromResult(cmd.CommandText.Contains("SELECT 1 AS a") ? new FakeResult(Enumerable.Repeat(table, sets).ToArray()) : FakeResult.None);
        var s = rig.Open();
        var id = rig.Run(s, "SELECT 1 AS a");
        var done = await rig.Done(id);

        Assert.Equal(ExecState.Done, done.State);
        Assert.Equal(ExecutionService.MaxResultSets, done.ResultSetCount);
        Assert.Equal(ExecutionService.MaxResultSets, frames.Schemas);
        // Before L13 the index wrapped to 0 and the 65,536th set overwrote the first one in the webview.
        Assert.DoesNotContain(ushort.MaxValue, frames.Indexes);
        Assert.Single(rig.Notifier.Of<ServerMessage>(), m => m.ExecutionId == id && m.Message.Contains("more than 65,535 result sets", StringComparison.Ordinal));
        Assert.Equal(ExecutionService.MaxResultSets, rig.Notifier.Of<QueryResultSetEvent>().Count(e => e.ExecutionId == id));
        Assert.Equal(1, s.ExecLock.CurrentCount);
    }
}
