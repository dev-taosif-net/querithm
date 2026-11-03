using System.Data.Common;

namespace Querithm.Contract.Providers;

/// <summary>
/// How an engine's transactions behave (Phase D0b), as data the tests assert and Core reads instead of branching on the
/// engine. Every engine so far starts in autocommit: each statement outside an explicit transaction commits on its own.
/// </summary>
public sealed record TransactionTraits
{
    /// <summary>Statements outside an explicit transaction commit on their own.</summary>
    public required bool AutocommitDefault { get; init; }
    /// <summary>DDL ends an open transaction (MySQL, Oracle); false where DDL is transactional (PostgreSQL, SQL Server).</summary>
    public required bool DdlCommitsImplicitly { get; init; }
    public required bool Savepoints { get; init; }
    /// <summary>The engine can release a savepoint (SQL Server cannot; the savepoint simply ends with the transaction).</summary>
    public required bool ReleaseSavepoint { get; init; }
    /// <summary>A transaction can be started read-only, so the server refuses every write inside it.</summary>
    public required bool ReadOnlyTransactions { get; init; }
    /// <summary>
    /// A failed statement leaves the transaction unusable until it is rolled back (PostgreSQL 25P02). SQL Server keeps it
    /// usable unless XACT_ABORT is ON, where the server rolls it back itself; the post-execution state read reports either.
    /// </summary>
    public required bool FailedStatementAbortsTransaction { get; init; }
    /// <summary>User-typed statements that begin or end a transaction (BEGIN, START TRANSACTION, COMMIT, …), for the docs and the classifier tests.</summary>
    public required string[] UserStatements { get; init; }
}

/// <summary>
/// Provider-owned transaction control (Phase D0b): the one way Core begins, ends, savepoints and inspects a transaction
/// — for <c>tx.*</c>, grid edits, imports, compare reads and actual plans. A provider may use SQL text or its driver's
/// API; Core serializes the calls on a connection and owns its lifetime, never touching a driver type. Every method
/// works on a transaction that the commands of the same connection see without a <see cref="DbCommand.Transaction"/>
/// (the user's own statements run on a pinned connection without one).
/// </summary>
public interface ITransactionControl
{
    TransactionTraits Traits { get; }

    Task BeginAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Begins a read-only transaction where the engine enforces one (true); false, with nothing started, where it cannot.</summary>
    Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Commits the open transaction. Fails when none is open, so a transaction the server already ended is never reported as committed.</summary>
    Task CommitAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Rolls back whatever transaction is open; nothing when none is.</summary>
    Task RollbackAsync(DbConnection connection, CancellationToken ct);

    Task SavepointAsync(DbConnection connection, string name, CancellationToken ct);
    Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct);
    /// <summary>Releases the savepoint; nothing where the engine has no release (<see cref="TransactionTraits.ReleaseSavepoint"/>).</summary>
    Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct);

    /// <summary>The connection's transaction state as the server reports it; throws when it cannot be read (Core then treats it as unknown).</summary>
    Task<TxState> StateAsync(DbConnection connection, CancellationToken ct);
}

/// <summary>
/// A COMMIT that found no transaction open on the connection and sent nothing (SQLite, DuckDB check first): whatever the
/// transaction held had already ended — an interrupted statement rolls it back — so this call committed nothing. Callers
/// read it as a known outcome, never as a COMMIT cut short (8a review: imports reported "outcome unknown").
/// </summary>
public sealed class NoTransactionOpenException(string message) : InvalidOperationException(message);

/// <summary>Transaction control by SQL statements, for engines whose drivers follow transactions begun in SQL.</summary>
public abstract class SqlTransactionControl : ITransactionControl
{
    public abstract TransactionTraits Traits { get; }
    protected abstract string BeginSql { get; }
    protected abstract string? BeginReadOnlySql { get; }
    protected abstract string CommitSql { get; }
    protected abstract string RollbackSql { get; }
    protected abstract string SavepointSql(string name);
    protected abstract string RollbackToSavepointSql(string name);
    protected abstract string? ReleaseSavepointSql(string name);
    public abstract Task<TxState> StateAsync(DbConnection connection, CancellationToken ct);

    public Task BeginAsync(DbConnection connection, CancellationToken ct) => RunAsync(connection, BeginSql, ct);

    public async Task<bool> BeginReadOnlyAsync(DbConnection connection, CancellationToken ct)
    {
        if (BeginReadOnlySql is not { } sql) return false;
        await RunAsync(connection, sql, ct).ConfigureAwait(false);
        return true;
    }

    public Task CommitAsync(DbConnection connection, CancellationToken ct) => RunAsync(connection, CommitSql, ct);
    public Task RollbackAsync(DbConnection connection, CancellationToken ct) => RunAsync(connection, RollbackSql, ct);
    public Task SavepointAsync(DbConnection connection, string name, CancellationToken ct) => RunAsync(connection, SavepointSql(Checked(name)), ct);
    public Task RollbackToSavepointAsync(DbConnection connection, string name, CancellationToken ct) => RunAsync(connection, RollbackToSavepointSql(Checked(name)), ct);

    public Task ReleaseSavepointAsync(DbConnection connection, string name, CancellationToken ct) =>
        ReleaseSavepointSql(Checked(name)) is { } sql ? RunAsync(connection, sql, ct) : Task.CompletedTask;

    /// <summary>Savepoint names are Querithm's own constants; anything else is refused rather than quoted into SQL.</summary>
    static string Checked(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && char.IsAsciiLetter(name[0])
            ? name : throw new ArgumentException("Invalid savepoint name.", nameof(name));

    protected static async Task RunAsync(DbConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The bound on cleanup that has to run after the caller's token fired — a rollback, a rollback to a savepoint, restoring
/// session options, re-reading the state of the connection a run used. Long enough for a server that is working (a large
/// rollback finishes on the server whether or not the client still waits), short enough that a server that stopped
/// answering never holds a run, a tab or a pooled connection for good. When it fires the driver cancels the command
/// (Npgsql and MySqlConnector break the connection after their cancellation timeout; on SQL Server the run's cancel
/// escalation closes it), and the caller treats the connection as lost.
/// </summary>
public static class Cleanup
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /// <summary>A token source that cancels after <see cref="Limit"/>; dispose it when the cleanup is done.</summary>
    public static CancellationTokenSource Start() => new(Limit);
}
