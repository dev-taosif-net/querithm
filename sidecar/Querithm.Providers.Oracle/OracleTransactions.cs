using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Oracle transactions (D4 S03, D0b API). Outside an explicit transaction every statement commits on its own (ODP.NET's
/// <see cref="OracleConnection.AutoCommit"/>, on by default). <see cref="BeginAsync"/> turns autocommit off and makes the
/// server allocate the transaction at once (<c>DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE)</c>) — an Oracle transaction
/// otherwise starts only at its first change, so "begun, nothing changed yet" and "ended by a typed COMMIT" would look the
/// same. The state is then the server's own: a transaction id while one is open, none once a typed COMMIT / ROLLBACK, a
/// PL/SQL block's COMMIT or DDL's implicit commit ended it (<see cref="StateAsync"/> turns autocommit back on then). A
/// transaction that already holds changes when BEGIN arrives is adopted, never ended. No PL/SQL <c>BEGIN</c> block is ever
/// used to begin one (it is a block, not a transaction).
/// </summary>
public sealed class OracleTransactions : ITransactionControl
{
    public TransactionTraits Traits { get; } = new()
    {
        AutocommitDefault = true, DdlCommitsImplicitly = true, Savepoints = true, ReleaseSavepoint = false, ReadOnlyTransactions = true,
        FailedStatementAbortsTransaction = false, UserStatements = ["COMMIT [WORK]", "ROLLBACK [WORK]", "SAVEPOINT", "ROLLBACK TO [SAVEPOINT]", "SET TRANSACTION"],
    };

    internal const string IdSql = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL";
    internal const string AllocateSql = "SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID(TRUE) FROM DUAL";

    static OracleConnection Ora(DbConnection c) => c as OracleConnection ?? throw new ArgumentException("Not an Oracle connection.", nameof(c));

    public async Task BeginAsync(DbConnection connection, CancellationToken ct)
    {
        var c = Ora(connection);
        c.AutoCommit = false;
        try { await ScalarAsync(c, AllocateSql, ct).ConfigureAwait(false); }
        catch
        {
            // Nothing begun: back to autocommit (a transaction already holding changes is left as it was, for the state read).
            c.AutoCommit = true;
            throw;
        }
    }

    /// <summary>SET TRANSACTION READ ONLY must be a transaction's first statement: when one already holds changes nothing is started (false).</summary>
    public async Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct)
    {
        var c = Ora(connection);
        if (await ScalarAsync(c, IdSql, ct).ConfigureAwait(false) != null) return false;
        c.AutoCommit = false;
        try
        {
            await RunAsync(c, "SET TRANSACTION READ ONLY", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            c.AutoCommit = true;
            throw;
        }
    }

    /// <summary>
    /// COMMIT by statement (cancellable, unlike the driver's transaction object). Read first: the server accepts a COMMIT with
    /// nothing open, which must never be reported as committed.
    /// </summary>
    public async Task CommitAsync(DbConnection connection, CancellationToken ct)
    {
        var c = Ora(connection);
        if (await ScalarAsync(c, IdSql, ct).ConfigureAwait(false) == null && !ReadOnlyOpen(c))
        {
            c.AutoCommit = true;
            throw new NoTransactionOpenException("No transaction was open on this connection, so nothing was committed.");
        }
        await RunAsync(c, "COMMIT", ct).ConfigureAwait(false);
        c.AutoCommit = true;
    }

    public async Task RollbackAsync(DbConnection connection, CancellationToken ct)
    {
        var c = Ora(connection);
        await RunAsync(c, "ROLLBACK", ct).ConfigureAwait(false);
        c.AutoCommit = true;
    }

    public Task SavepointAsync(DbConnection connection, string name, CancellationToken ct) => RunAsync(Ora(connection), $"SAVEPOINT {Checked(name)}", ct);
    public Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct) => RunAsync(Ora(connection), $"ROLLBACK TO SAVEPOINT {Checked(name)}", ct);

    /// <summary>Oracle has no RELEASE SAVEPOINT: a savepoint ends with its transaction.</summary>
    public Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct)
    {
        Checked(name);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Open while the server holds a transaction id for the session. Without one, an explicit transaction a statement ended
    /// returns the connection to autocommit. A read-only transaction has no id (it allocates no undo): while autocommit is
    /// off with none, the explicit transaction Querithm began is reported open, since nothing on the server says otherwise
    /// and a typed COMMIT in it ends nothing that could be lost.
    /// </summary>
    public async Task<TxState> StateAsync(DbConnection connection, CancellationToken ct)
    {
        var c = Ora(connection);
        if (await ScalarAsync(c, IdSql, ct).ConfigureAwait(false) != null)
        {
            // Changes held with autocommit on happen only through a path that bypassed it (a distributed statement, a
            // driver-side failure): adopted as open, so Core pins the connection and the user ends it.
            return TxState.Open;
        }
        if (ReadOnlyOpen(c)) return TxState.Open;
        c.AutoCommit = true;
        return TxState.None;
    }

    /// <summary>Autocommit off with no transaction id: only a read-only transaction Querithm began (BeginAsync always allocates an id).</summary>
    static bool ReadOnlyOpen(OracleConnection c) => !c.AutoCommit && ReadOnly.TryGetValue(c, out _);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OracleConnection, object> ReadOnly = new();

    static string Checked(string name) =>
        name.Length is > 0 and <= 30 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_') && char.IsAsciiLetter(name[0])
            ? name : throw new ArgumentException("Invalid savepoint name.", nameof(name));

    static async Task RunAsync(OracleConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (sql == "SET TRANSACTION READ ONLY") ReadOnly.AddOrUpdate(c, true);
        else if (sql is "COMMIT" or "ROLLBACK") ReadOnly.Remove(c);
    }

    static async Task<object?> ScalarAsync(OracleConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is DBNull ? null : v;
    }
}
