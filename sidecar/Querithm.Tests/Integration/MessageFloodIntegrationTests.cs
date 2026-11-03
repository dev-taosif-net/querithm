using Querithm.Contract;
using Querithm.Core.Execution;

namespace Querithm.Tests.Integration;

/// <summary>
/// M13 against the Docker fixture (QUERITHM_IT=1): each engine that sends server messages floods them, and the run's
/// messages arrive coalesced (query.messages), capped at 20,000 with one closing line. SQLite and DuckDB send none.
/// </summary>
public sealed class MessageFloodIntegrationTests
{
    const int Flood = 25_000;

    static List<QueryMessagesEvent> Batches(CapturingNotifier n, string id) =>
        n.All.Where(x => x.Method == "query.messages").Select(x => (QueryMessagesEvent)x.Payload).Where(b => b.ExecutionId == id).ToList();

    static void AssertCapped(CapturingNotifier n, string id)
    {
        var batches = Batches(n, id);
        var lines = batches.SelectMany(b => b.Messages).ToList();
        Assert.Equal(MessageBatcher.MaxMessages + 1, lines.Count);
        Assert.StartsWith($"{Flood - MessageBatcher.MaxMessages:N0} more server messages were not shown", lines[^1].Message);
        Assert.InRange(batches.Count, 1, MessageBatcher.MaxMessages / MessageBatcher.MaxBatch + 50);
        Assert.DoesNotContain(n.All, x => x.Method == "query.message");
    }

    [IntegrationFact]
    public async Task SqlServer_print_flood_is_batched_and_capped()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var (done, _, id) = await h.RunAsync(sid, $"DECLARE @i int = 0; WHILE @i < {Flood} BEGIN PRINT CONCAT('line ', @i); SET @i += 1; END; SELECT 1 AS a;");
        Assert.Equal(ExecState.Done, done.State);
        AssertCapped(h.Notifier, id);
    }

    [IntegrationFact]
    public async Task SqlServer_raiserror_nowait_messages_arrive_while_the_batch_runs()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenMssqlAsync();
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = "RAISERROR('started', 0, 1) WITH NOWAIT; WAITFOR DELAY '00:00:03'; SELECT 1 AS a;" });
        var id = r.ExecutionId!;
        // The progress line is shown during the wait (a quiet spell sends it at once), not when the batch ends.
        var end = DateTime.UtcNow.AddSeconds(2);
        while (!h.Notifier.Of<ServerMessage>().Any(m => m.ExecutionId == id && m.Message == "started"))
        {
            Assert.True(DateTime.UtcNow < end, "RAISERROR WITH NOWAIT did not arrive during the wait");
            await Task.Delay(20);
        }
        Assert.DoesNotContain(h.Notifier.All, x => x.Payload is QueryDoneEvent d && d.ExecutionId == id);
        await h.Notifier.WaitDone(id);
    }

    [IntegrationFact]
    public async Task Postgres_raise_notice_flood_is_batched_and_capped()
    {
        await using var h = new TestHost();
        var (_, sid) = await h.OpenPgAsync();
        var (done, _, id) = await h.RunAsync(sid, $"DO $$ BEGIN FOR i IN 1..{Flood} LOOP RAISE NOTICE 'line %', i; END LOOP; END $$;");
        Assert.Equal(ExecState.Done, done.State);
        AssertCapped(h.Notifier, id);
    }

    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task MySql_warnings_of_many_statements_are_batched(string product)
    {
        var (h, _, sid) = await MySqlIntegrationTests.OpenAsync(product);
        await using var _ = h;
        const int statements = 50;
        // Statements without a result set: nothing else of the run goes out between their warnings, so they coalesce.
        var (done, _, id) = await MySqlIntegrationTests.Run(h, sid, string.Concat(Enumerable.Repeat("DO CAST('abc' AS SIGNED);\n", statements)));
        Assert.Null(done.Error);
        var batches = Batches(h.Notifier, id);
        Assert.Equal(statements, batches.Sum(b => b.Messages.Count(m => m.Severity == Severity.Warning)));
        Assert.True(batches.Count < statements / 2, $"{batches.Count} notifications for {statements} warnings");
    }

    /// <summary>
    /// SELECTs with a warning each: every result set flushes the warnings before it (M13 keeps the order), so the run sends
    /// one batch per result set — the integration run of 2026-11-03 expected fewer, which would put warnings after the
    /// result set of a later statement.
    /// </summary>
    [IntegrationTheory]
    [MemberData(nameof(MySqlIntegrationTests.Products), MemberType = typeof(MySqlIntegrationTests))]
    public async Task MySql_warnings_between_result_sets_keep_their_order(string product)
    {
        var (h, _, sid) = await MySqlIntegrationTests.OpenAsync(product);
        await using var _ = h;
        const int statements = 20;
        var (done, _, id) = await MySqlIntegrationTests.Run(h, sid, string.Concat(Enumerable.Repeat("SELECT CAST('abc' AS SIGNED) AS n;\n", statements)));
        Assert.Null(done.Error);
        var events = h.Notifier.All.Where(x => x.Payload is QueryMessagesEvent m ? m.ExecutionId == id : x.Payload is QueryResultSetEvent r && r.ExecutionId == id).ToList();
        var warnings = 0;
        var resultSets = 0;
        foreach (var e in events)
        {
            if (e.Payload is QueryMessagesEvent m) warnings += m.Messages.Count(x => x.Severity == Severity.Warning);
            else Assert.Equal(resultSets++, warnings); // result set k follows the warnings of the k statements before it
        }
        Assert.Equal(statements, resultSets);
        Assert.Equal(statements, warnings);
        Assert.True(Batches(h.Notifier, id).Count <= resultSets + 1);
    }
}
