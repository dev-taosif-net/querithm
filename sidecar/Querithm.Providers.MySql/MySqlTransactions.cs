using System.Data.Common;
using System.Runtime.CompilerServices;
using MySqlConnector;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.MySql;

/// <summary>
/// MySQL / MariaDB transactions (Phase D2, D0b API). Autocommit by default; DDL commits implicitly (and so ends an open
/// transaction); savepoints with RELEASE; read-only transactions. The state: MariaDB's <c>@@in_transaction</c>; MySQL has
/// no such variable, so its own transaction event in performance_schema (ACTIVE while one is open) — but only where that
/// event is recorded at all, which is probed once per connection (M9): performance_schema on, the transaction instrument
/// and the events_transactions_current consumers enabled, and this thread instrumented. Without it, an empty table would
/// read as "none" over a live transaction, so the state comes from a savepoint probe instead: with autocommit on, MySQL
/// keeps a savepoint only inside an open transaction (outside one SAVEPOINT does nothing and RELEASE fails with 1305).
/// With autocommit off, or when nothing can be read, the state is unknown and Core pins (or discards) the connection
/// instead of pooling it.
/// </summary>
public sealed class MySqlTransactions : SqlTransactionControl
{
    public override TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = true, Savepoints = true, ReleaseSavepoint = true, ReadOnlyTransactions = true,
        FailedStatementAbortsTransaction = false,
        UserStatements = ["START TRANSACTION", "BEGIN [WORK]", "COMMIT [WORK]", "ROLLBACK [WORK]", "SAVEPOINT", "RELEASE SAVEPOINT", "ROLLBACK TO [SAVEPOINT]", "SET autocommit"],
    };

    protected override string BeginSql => "START TRANSACTION";
    protected override string? BeginReadOnlySql => "START TRANSACTION READ ONLY";
    protected override string CommitSql => "COMMIT";
    protected override string RollbackSql => "ROLLBACK";
    protected override string SavepointSql(string name) => $"SAVEPOINT {name}";
    protected override string RollbackToSavepointSql(string name) => $"ROLLBACK TO SAVEPOINT {name}";
    protected override string? ReleaseSavepointSql(string name) => $"RELEASE SAVEPOINT {name}";

    /// <summary>Whether performance_schema records this connection's transactions, probed once per connection.</summary>
    readonly ConditionalWeakTable<DbConnection, StrongBox<bool>> _instrumented = new();

    internal const string InstrumentedSql = """
        SELECT @@performance_schema = 1
           AND (SELECT COUNT(*) FROM performance_schema.setup_consumers
                WHERE NAME IN ('global_instrumentation', 'thread_instrumentation', 'events_transactions_current') AND ENABLED = 'YES') = 3
           AND EXISTS(SELECT 1 FROM performance_schema.setup_instruments WHERE NAME = 'transaction' AND ENABLED = 'YES')
           AND EXISTS(SELECT 1 FROM performance_schema.threads WHERE THREAD_ID = PS_CURRENT_THREAD_ID() AND INSTRUMENTED = 'YES')
        """;
    internal const string EventSql = "SELECT IF(EXISTS(SELECT 1 FROM performance_schema.events_transactions_current WHERE THREAD_ID = PS_CURRENT_THREAD_ID() AND STATE = 'ACTIVE'), 1, 0)";
    const string Probe = "querithm_tx_probe";
    internal const string ProbeSql = $"SAVEPOINT {Probe}; RELEASE SAVEPOINT {Probe}";

    public override async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        try
        {
            if (MySqlProvider.IsMariaDb(connection)) return await ScalarAsync(connection, "SELECT @@in_transaction", ct).ConfigureAwait(false) != 0 ? TxState.Open : TxState.None;
            if (await InstrumentedAsync(connection, ct).ConfigureAwait(false))
                return await ScalarAsync(connection, EventSql, ct).ConfigureAwait(false) != 0 ? TxState.Open : TxState.None;
            // With autocommit off every statement opens a transaction, and the probe below cannot tell an empty one from work
            // (and would open one itself): unknown, without the probe.
            if (await ScalarAsync(connection, "SELECT @@autocommit", ct).ConfigureAwait(false) == 0) return TxState.Unknown;
            // The probe's two statements go in one round trip (8a review: two round trips per read on an uninstrumented
            // server, was three); RELEASE fails with 1305 when no transaction kept the savepoint.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = ProbeSql;
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.NextResultAsync(ct).ConfigureAwait(false)) { }
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.StoredProcedureDoesNotExist) { return TxState.None; }
            return TxState.Open;
        }
        catch (MySqlException) { return TxState.Unknown; } // not readable (privileges, an XA transaction refusing the savepoint)
    }

    async Task<bool> InstrumentedAsync(DbConnection connection, CancellationToken ct)
    {
        if (_instrumented.TryGetValue(connection, out var known)) return known.Value;
        bool on;
        try { on = await ScalarAsync(connection, InstrumentedSql, ct).ConfigureAwait(false) != 0; }
        catch (MySqlException) { on = false; } // performance_schema not readable for this account
        _instrumented.AddOrUpdate(connection, new StrongBox<bool>(on));
        return on;
    }

    static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0 : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
    }
}
