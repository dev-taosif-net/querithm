using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.DuckDb;
using Querithm.Providers.Sqlite;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// The SQLite and DuckDB native import loaders' commit paths on real temp-dir files (8a review: none were tested): the
/// loader's own transaction commits every row once, batches mode commits each batch and says so, and a transaction that an
/// interrupted statement already ended makes the COMMIT throw <see cref="NoTransactionOpenException"/> — sent nothing, a
/// known outcome — with <see cref="BulkTransaction.InCommit"/> set, which ImportService reports as "nothing was written",
/// never as an unknown outcome.
/// </summary>
public sealed class LoaderCommitFileEngineTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qloader-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    /// <summary>The mapped rows of a load; <paramref name="atEnd"/> runs when the loader asks past the last row.</summary>
    sealed class Rows(IReadOnlyList<string?[]> data, Action? atEnd = null) : IBulkRows
    {
        int _i = -1;
        public List<(long Total, bool Committed)> Loads { get; } = [];

        public bool Read()
        {
            if (++_i < data.Count) return true;
            if (_i == data.Count) atEnd?.Invoke();
            return false;
        }

        public IReadOnlyList<string?> Current => data[_i];
        public long Row => _i + 2;
        public void Reject(string reason) => throw new InvalidOperationException(reason);
        public void Loaded(long totalRows, bool committed) => Loads.Add((totalRows, committed));
    }

    static string?[][] Data(int n) => [.. Enumerable.Range(1, n).Select(i => new string?[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"n{i}" })];

    static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long Count(DbConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ SQLite

    SqliteConnection Sqlite(string setup)
    {
        var c = new SqliteConnection($"Data Source={Path.Combine(_dir, $"{Guid.NewGuid():N}.db")};Pooling=False");
        c.Open();
        Exec(c, setup);
        return c;
    }

    static readonly BulkTarget SqliteTarget = new("main", "t", [new BulkColumn("id", "INTEGER"), new BulkColumn("name", "TEXT")]);

    [Fact]
    public async Task Sqlite_commits_its_own_transaction_with_every_row_once()
    {
        await using var c = Sqlite("CREATE TABLE t (id INTEGER, name TEXT);");
        var tx = new BulkTransaction(BulkTxMode.Own);
        await new SqliteBulkLoader(new SqliteTransactions()).LoadAsync(c, SqliteTarget, new Rows(Data(3)), new ImportRunOptions(), tx, null, CancellationToken.None);
        Assert.False(tx.InCommit);
        Assert.Equal(3, Count(c, "t"));
        Assert.Equal(TxState.None, await new SqliteTransactions().StateAsync(c, CancellationToken.None));
    }

    [Fact]
    public async Task Sqlite_batches_commit_each_batch_and_say_so()
    {
        await using var c = Sqlite("CREATE TABLE t (id INTEGER, name TEXT);");
        var rows = new Rows(Data(5));
        await new SqliteBulkLoader(new SqliteTransactions()).LoadAsync(c, SqliteTarget, rows, new ImportRunOptions(), new BulkTransaction(BulkTxMode.Batches, 2), null, CancellationToken.None);
        Assert.Equal(5, Count(c, "t"));
        Assert.Equal([(2L, true), (4L, true), (5L, true)], rows.Loads.Where(l => l.Committed));
    }

    [Fact]
    public async Task Sqlite_a_transaction_already_rolled_back_at_commit_is_a_known_outcome_and_writes_nothing()
    {
        await using var c = Sqlite("CREATE TABLE t (id INTEGER, name TEXT);");
        var tx = new BulkTransaction(BulkTxMode.Own);
        // What an interrupted write does in SQLite: the whole transaction is rolled back before the loader commits.
        var rows = new Rows(Data(3), () => Exec(c, "ROLLBACK"));
        await Assert.ThrowsAsync<NoTransactionOpenException>(() =>
            new SqliteBulkLoader(new SqliteTransactions()).LoadAsync(c, SqliteTarget, rows, new ImportRunOptions(), tx, null, CancellationToken.None));
        Assert.True(tx.InCommit);
        Assert.Equal(0, Count(c, "t"));
    }

    // ------------------------------------------------------------------ DuckDB

    /// <summary>An in-memory database: the loader reads its catalog under the name the provider registers ("memory" for one it did not open).</summary>
    static DuckDBConnection DuckDb(string setup)
    {
        var c = new DuckDBConnection("Data Source=:memory:");
        c.Open();
        Exec(c, setup);
        return c;
    }

    [Fact]
    public async Task Duckdb_commits_its_own_transaction_with_every_row_once()
    {
        await using var c = DuckDb("CREATE TABLE t (id INTEGER, name VARCHAR);");
        var tx = new BulkTransaction(BulkTxMode.Own);
        await new DuckDbBulkLoader().LoadAsync(c, new BulkTarget("main", "t", [new BulkColumn("id", "INTEGER"), new BulkColumn("name", "VARCHAR")]),
            new Rows(Data(3)), new ImportRunOptions(), tx, null, CancellationToken.None);
        Assert.False(tx.InCommit);
        Assert.Equal(3, Count(c, "t"));
        Assert.Equal(TxState.None, await new DuckDbTransactions().StateAsync(c, CancellationToken.None));
    }

    [Fact]
    public async Task Duckdb_batches_commit_each_batch_and_say_so()
    {
        await using var c = DuckDb("CREATE TABLE t (id INTEGER, name VARCHAR);");
        var rows = new Rows(Data(5));
        await new DuckDbBulkLoader().LoadAsync(c, new BulkTarget("main", "t", [new BulkColumn("id", "INTEGER"), new BulkColumn("name", "VARCHAR")]),
            rows, new ImportRunOptions(), new BulkTransaction(BulkTxMode.Batches, 2), null, CancellationToken.None);
        Assert.Equal(5, Count(c, "t"));
        Assert.Equal([(2L, true), (4L, true), (5L, true)], rows.Loads.Where(l => l.Committed));
    }

    [Fact]
    public async Task Duckdb_a_transaction_already_ended_at_commit_is_a_known_outcome_and_writes_nothing()
    {
        // An INTERVAL column has no appender conversion: the loader takes its INSERT path, one statement per row.
        await using var c = DuckDb("CREATE TABLE t (id INTEGER, span INTERVAL);");
        var tx = new BulkTransaction(BulkTxMode.Own);
        var rows = new Rows([["1", "1 day"], ["2", "2 days"]], () => Exec(c, "ROLLBACK"));
        await Assert.ThrowsAsync<NoTransactionOpenException>(() => new DuckDbBulkLoader().LoadAsync(c,
            new BulkTarget("main", "t", [new BulkColumn("id", "INTEGER"), new BulkColumn("span", "INTERVAL")]), rows, new ImportRunOptions(), tx, null, CancellationToken.None));
        Assert.True(tx.InCommit);
        Assert.Equal(0, Count(c, "t"));
    }
}
