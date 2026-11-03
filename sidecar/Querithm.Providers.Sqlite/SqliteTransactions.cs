using System.Data.Common;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Sqlite;

/// <summary>
/// SQLite transactions (Phase D1, through the D0b API). Autocommit by default; DDL is transactional; savepoints with
/// RELEASE; no read-only transactions (the read-only open mode is the protection); a failed statement keeps the
/// transaction. The state is the engine's own flag (sqlite3_get_autocommit), so it is exact and costs no SQL. An
/// interrupted (cancelled) DML statement inside an explicit transaction rolls the whole transaction back — the
/// post-execution state read reports it.
/// </summary>
public sealed class SqliteTransactions : ITransactionControl
{
    public TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = false, Savepoints = true, ReleaseSavepoint = true, ReadOnlyTransactions = false,
        FailedStatementAbortsTransaction = false,
        UserStatements = ["BEGIN [DEFERRED|IMMEDIATE|EXCLUSIVE] [TRANSACTION]", "COMMIT", "END", "ROLLBACK", "SAVEPOINT", "RELEASE", "ROLLBACK TO"],
    };

    static SqliteConnection S(DbConnection c) => (SqliteConnection)c;

    public Task BeginAsync(DbConnection connection, CancellationToken ct) => Run(connection, "BEGIN", ct);
    public Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct) => Task.FromResult(false);

    public Task CommitAsync(DbConnection connection, CancellationToken ct) =>
        SqliteHardening.InTransaction(S(connection))
            ? Run(connection, "COMMIT", ct)
            : throw new NoTransactionOpenException("No transaction is open: it had already ended (rolled back by an interrupted statement, or committed by the script).");

    public Task RollbackAsync(DbConnection connection, CancellationToken ct) =>
        SqliteHardening.InTransaction(S(connection)) ? Run(connection, "ROLLBACK", ct) : Task.CompletedTask;

    public Task SavepointAsync(DbConnection connection, string name, CancellationToken ct) => Run(connection, $"SAVEPOINT {Checked(name)}", ct);
    public Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct) => Run(connection, $"ROLLBACK TO SAVEPOINT {Checked(name)}", ct);
    public Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct) => Run(connection, $"RELEASE SAVEPOINT {Checked(name)}", ct);

    public Task<TxState> StateAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult(connection.State != System.Data.ConnectionState.Open
            ? throw new InvalidOperationException("The connection is closed.")
            : SqliteHardening.InTransaction(S(connection)) ? TxState.Open : TxState.None);

    static string Checked(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? name : throw new ArgumentException("Invalid savepoint name.", nameof(name));

    static async Task Run(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
