using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.DuckDb;

/// <summary>
/// DuckDB transactions (Phase D3, through the D0b API). Autocommit by default; DDL is transactional; no savepoints and no
/// read-only transactions (the read-only open mode is the protection); a failed statement aborts the transaction until
/// ROLLBACK. The state is read from txid_current(): autocommit gives every statement a new transaction, an open
/// transaction keeps its id, and an aborted one refuses the call (still open: it needs a ROLLBACK).
/// </summary>
public sealed class DuckDbTransactions : ITransactionControl
{
    public TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = false, ReleaseSavepoint = false, ReadOnlyTransactions = false,
        FailedStatementAbortsTransaction = true,
        UserStatements = ["BEGIN [TRANSACTION]", "COMMIT", "END", "ROLLBACK", "ABORT"],
    };

    const string NoSavepoints = "DuckDB has no savepoints: a transaction is committed or rolled back as a whole.";

    public Task BeginAsync(DbConnection connection, CancellationToken ct) => Run(connection, "BEGIN TRANSACTION", ct);
    public Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(false);

    public async Task CommitAsync(DbConnection connection, CancellationToken ct)
    {
        if (await StateAsync(connection, ct).ConfigureAwait(false) != TxState.Open)
            throw new NoTransactionOpenException("No transaction is open: it had already ended (committed or rolled back by the script).");
        await Run(connection, "COMMIT", ct).ConfigureAwait(false);
    }

    public async Task RollbackAsync(DbConnection connection, CancellationToken ct)
    {
        if (await StateAsync(connection, ct).ConfigureAwait(false) == TxState.Open) await Run(connection, "ROLLBACK", ct).ConfigureAwait(false);
    }

    public Task SavepointAsync(DbConnection connection, string name, CancellationToken ct) => throw new NotSupportedException(NoSavepoints);
    public Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct) => throw new NotSupportedException(NoSavepoints);
    public Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct) => throw new NotSupportedException(NoSavepoints);

    public async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open) throw new InvalidOperationException("The connection is closed.");
        try
        {
            var a = await Txid(connection, ct).ConfigureAwait(false);
            var b = await Txid(connection, ct).ConfigureAwait(false);
            return a == b ? TxState.Open : TxState.None;
        }
        catch (DbException e) when (e.Message.Contains("transaction is aborted", StringComparison.OrdinalIgnoreCase))
        {
            return TxState.Open;
        }
    }

    static async Task<string?> Txid(DbConnection c, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT txid_current()";
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    static async Task Run(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
