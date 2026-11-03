using System.Reflection;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Providers.MySql;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// M9 (a): MySQL reports an open transaction only through performance_schema, and only where that event is recorded. An
/// uninstrumented connection used to read "none" over a live transaction (no row in the table), so the connection went
/// back to the pool with it. The instrumentation is probed once per connection; without it a savepoint probe decides, and
/// what neither can tell is unknown (the connection stays pinned). The fake server answers each statement.
/// </summary>
public class MySqlTransactionStateTests
{
    static MySqlException Error(MySqlErrorCode code, string message)
    {
        // MySqlConnector raises these itself; its constructors are internal.
        var ctor = typeof(MySqlException).GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters() is { Length: > 0 } ps && ps[0].ParameterType == typeof(MySqlErrorCode)
                        && ps.Skip(1).All(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(Exception)))
            .OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? code : p.ParameterType == typeof(string) ? (object)message : null).ToArray();
        return (MySqlException)ctor.Invoke(args);
    }

    /// <summary>A fake MySQL server: <paramref name="instrumented"/> answers the probe (null = refused), and a transaction is open or not.</summary>
    sealed class Server(bool? instrumented, bool open, int autocommit = 1)
    {
        public readonly List<string> Seen = [];
        public Func<string, Exception?> Fail { get; set; } = _ => null;
        public FakeDbConnection Connection() => new((cmd, _) => Task.FromResult(Answer(cmd.CommandText.Trim())));

        FakeResult Answer(string sql)
        {
            Seen.Add(sql);
            if (Fail(sql) is { } fail) throw fail;
            if (sql == MySqlTransactions.InstrumentedSql.Trim())
                return instrumented is { } on ? FakeResult.Value(on ? 1 : 0) : throw Error(MySqlErrorCode.TableAccessDenied, "SELECT command denied to user for table 'setup_consumers'");
            if (sql == MySqlTransactions.EventSql) return FakeResult.Value(open && instrumented == true ? 1 : 0); // uninstrumented: no row, whatever is open
            if (sql == "SELECT @@autocommit") return FakeResult.Value(autocommit);
            // MySQL keeps a savepoint only inside an open transaction (with autocommit on, SAVEPOINT alone does nothing): the
            // probe's RELEASE then fails (both statements are one command, one round trip).
            if (sql == MySqlTransactions.ProbeSql && !open)
                throw Error(MySqlErrorCode.StoredProcedureDoesNotExist, "SAVEPOINT querithm_tx_probe does not exist");
            return FakeResult.None;
        }
    }

    static Task<TxState> State(FakeDbConnection c) => new MySqlTransactions().StateAsync(c, default);

    [Theory]
    [InlineData(true, TxState.Open)]
    [InlineData(false, TxState.None)]
    public async Task An_instrumented_connection_reads_its_transaction_event(bool open, TxState expected)
    {
        var server = new Server(instrumented: true, open);
        Assert.Equal(expected, await State(server.Connection()));
        Assert.Contains(MySqlTransactions.EventSql, server.Seen);
        Assert.DoesNotContain(server.Seen, s => s.Contains("SAVEPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_instrumentation_an_open_transaction_never_reads_as_none()
    {
        // performance_schema has no row for this thread, so the event table would say "none" over the open transaction.
        var server = new Server(instrumented: false, open: true);
        Assert.Equal(TxState.Open, await State(server.Connection()));
        Assert.DoesNotContain(MySqlTransactions.EventSql, server.Seen);
        Assert.Equal(["SELECT @@autocommit", MySqlTransactions.ProbeSql], server.Seen.Skip(1)); // two round trips
    }

    [Fact]
    public async Task Without_instrumentation_no_transaction_reads_as_none_from_the_savepoint_probe()
    {
        var server = new Server(instrumented: false, open: false);
        Assert.Equal(TxState.None, await State(server.Connection()));
    }

    [Fact]
    public async Task Without_instrumentation_and_with_autocommit_off_the_state_is_unknown()
    {
        var server = new Server(instrumented: false, open: false, autocommit: 0);
        Assert.Equal(TxState.Unknown, await State(server.Connection()));
        Assert.DoesNotContain(server.Seen, s => s.Contains("SAVEPOINT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_performance_schema_falls_back_to_the_savepoint_probe()
    {
        var server = new Server(instrumented: null, open: true);
        Assert.Equal(TxState.Open, await State(server.Connection()));
    }

    [Fact]
    public async Task A_savepoint_the_server_refuses_reads_as_unknown_never_as_none()
    {
        var server = new Server(instrumented: false, open: true)
        {
            Fail = sql => sql == MySqlTransactions.ProbeSql ? Error(MySqlErrorCode.XAERRemoveFail, "The command cannot be executed when global transaction is in the  ACTIVE state") : null,
        };
        Assert.Equal(TxState.Unknown, await State(server.Connection()));
    }

    [Fact]
    public async Task The_instrumentation_is_probed_once_per_connection()
    {
        var server = new Server(instrumented: true, open: false);
        var tx = new MySqlTransactions();
        var c = server.Connection();
        await tx.StateAsync(c, default);
        await tx.StateAsync(c, default);
        await tx.StateAsync(server.Connection(), default); // another connection: probed again
        Assert.Equal(2, server.Seen.Count(s => s == MySqlTransactions.InstrumentedSql.Trim()));
        Assert.Equal(3, server.Seen.Count(s => s == MySqlTransactions.EventSql));
    }
}
