using System.Data.Common;
using System.Globalization;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.SqlServer;

/// <summary>
/// SQL Server transactions by T-SQL (Phase D0b): SqlClient follows a transaction begun in SQL, so the user's statements on
/// the pinned connection need no SqlTransaction. Autocommit by default; DDL is transactional; savepoints (SAVE TRANSACTION)
/// have no release; no read-only transactions. Nested BEGIN TRAN only raises @@TRANCOUNT: any count above 0 is open.
/// With XACT_ABORT ON an error or a cancel rolls the whole transaction back on the server, which the state read reports.
/// </summary>
public sealed class SqlServerTransactions : SqlTransactionControl
{
    public override TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = true, ReleaseSavepoint = false, ReadOnlyTransactions = false,
        FailedStatementAbortsTransaction = false,
        UserStatements = ["BEGIN TRAN[SACTION]", "BEGIN DISTRIBUTED TRAN[SACTION]", "COMMIT [TRAN[SACTION] | WORK]", "ROLLBACK [TRAN[SACTION] | WORK]", "SAVE TRAN[SACTION]"],
    };

    protected override string BeginSql => "BEGIN TRANSACTION";
    protected override string? BeginReadOnlySql => null;
    /// <summary>Strict: error 3902 when the server already ended the transaction, so that is never reported as a commit.</summary>
    protected override string CommitSql => "COMMIT TRANSACTION";
    protected override string RollbackSql => "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION";
    protected override string SavepointSql(string name) => $"SAVE TRANSACTION {name}";
    protected override string RollbackToSavepointSql(string name) => $"ROLLBACK TRANSACTION {name}";
    protected override string? ReleaseSavepointSql(string name) => null;

    public override async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT @@TRANCOUNT";
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return n > 0 ? TxState.Open : TxState.None;
    }
}
