using System.Data.Common;
using Npgsql;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Postgres;

/// <summary>
/// PostgreSQL transactions by SQL (Phase D0b): Npgsql follows BEGIN / COMMIT / ROLLBACK issued as statements, so no
/// NpgsqlTransaction is needed. Autocommit by default; DDL is transactional; savepoints with RELEASE; read-only
/// transactions; a failed statement aborts the block (25P02) until it is rolled back.
/// </summary>
public sealed class PostgresTransactions : SqlTransactionControl
{
    public static PostgresTransactions Instance { get; } = new();

    public override TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = true, ReleaseSavepoint = true, ReadOnlyTransactions = true,
        FailedStatementAbortsTransaction = true,
        UserStatements = ["BEGIN", "START TRANSACTION", "COMMIT", "END", "ROLLBACK", "ABORT", "SAVEPOINT", "RELEASE", "ROLLBACK TO", "PREPARE TRANSACTION"],
    };

    protected override string BeginSql => "BEGIN";
    protected override string? BeginReadOnlySql => "BEGIN TRANSACTION READ ONLY";
    protected override string CommitSql => "COMMIT";
    protected override string RollbackSql => "ROLLBACK";
    protected override string SavepointSql(string name) => $"SAVEPOINT {name}";
    protected override string RollbackToSavepointSql(string name) => $"ROLLBACK TO SAVEPOINT {name}";
    protected override string? ReleaseSavepointSql(string name) => $"RELEASE SAVEPOINT {name}";

    /// <summary>SAVEPOINT succeeds only inside a transaction block (25P01 otherwise); an aborted block reports 25P02.</summary>
    public override async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SAVEPOINT querithm_tx_probe";
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "25P01")
        {
            return TxState.None;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
        {
            return TxState.Open;
        }
        cmd.CommandText = "RELEASE SAVEPOINT querithm_tx_probe";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return TxState.Open;
    }
}
