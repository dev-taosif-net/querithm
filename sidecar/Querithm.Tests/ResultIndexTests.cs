using System.Data.Common;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Grid-edit audit: SQL Server runs the next GO batch after an error (StopOnBatchError = false). The result index a
/// failing batch had advanced lived in the batch's locals and was lost, so the next batch reused it: two result sets
/// shared one recorded result (and the UI's edits), and a grid edit could target the wrong table.
/// </summary>
public class ResultIndexTests
{
    sealed class ServerError(string message) : DbException(message);

    [Fact]
    public async Task A_batch_that_fails_after_streaming_a_set_does_not_give_its_index_to_the_next_batch()
    {
        var provider = new FakeDbProvider();
        provider.Handler = (cmd, _) => Task.FromResult(cmd.CommandText.Contains("first_t")
            ? new FakeResult([FakeResult.Table([("first_col", typeof(int))], [1])]) { FailAfterTables = new ServerError("Divide by zero error encountered.") }
            : new FakeResult([FakeResult.Table([("second_col", typeof(int))], [2])]));
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var notifier = new CapturingNotifier();
        await using var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), notifier, Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        await using var executions = new ExecutionService(sessions, new CapturingFrames(), notifier, new HistoryStore(store), Log.Null)
        {
            // Each set's edit decision names its first column, so the recorded result shows which set owns an index.
            Editability = (_, cols, _, _) => new ResultEditInfo { Editable = false, Reason = cols[0].Name },
        };
        Assert.False(provider.StopOnBatchError);
        var s = sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId;

        var id = executions.Execute(new QueryExecuteParams { SessionId = s, Sql = "SELECT first_col FROM first_t; SELECT 1/0\nGO\nSELECT second_col FROM second_t" }).ExecutionId!;
        var done = await notifier.WaitDone(id);

        Assert.NotNull(done.Error);
        var sets = notifier.Of<QueryResultSetEvent>().Where(e => e.ExecutionId == id).ToList();
        Assert.Equal([0, 1], sets.Select(e => e.ResultIndex));
        Assert.Equal(["first_col", "second_col"], sets.Select(e => e.Schema[0].Name));
        Assert.Equal("first_col", executions.Recorded(s, id, 0)?.Edit?.Reason);
        Assert.Equal("second_col", executions.Recorded(s, id, 1)?.Edit?.Reason);
        Assert.Equal(2, done.ResultSetCount);
    }
}
