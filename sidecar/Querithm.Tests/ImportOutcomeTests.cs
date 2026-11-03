using System.Data.Common;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;
using Querithm.Tests.Security;

namespace Querithm.Tests;

/// <summary>
/// An import whose own COMMIT is cut short (the connection lost, a cancel) may or may not have written its rows: it
/// reports <c>outcomeUnknown</c>, as grid Apply does, instead of an error a retry would follow with a second copy. Before,
/// it reported the driver's error like any failure. Generic batched-INSERT loader over fake connections, per transaction
/// control (the native loaders run their COMMITs through the same <see cref="BulkTransaction.CommitAsync"/>).
/// </summary>
public sealed class ImportOutcomeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qimport-outcome-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    public static TheoryData<string> Engines => new() { "mssql", "pg" };

    static string CommitSql(string engine) => engine == "mssql" ? "COMMIT TRANSACTION" : "COMMIT";

    /// <summary>SQL Server's / PostgreSQL's import rules with the generic loader (no native bulk path over a fake connection).</summary>
    sealed class GenericImport(IImportDialect inner) : IImportDialect
    {
        public string TypeFor(ImportColumnProfile column) => inner.TypeFor(column);
        public ImportFit Check(ImportColumnProfile column, SnapColumn target) => inner.Check(column, target);
        public bool NullUsesDefault => inner.NullUsesDefault;
        public bool BlankIsNull(string? type) => inner.BlankIsNull(type);
        public string CreateTable(string schema, string table, IReadOnlyList<(string Name, string Type, bool NotNull)> columns, IReadOnlyList<string> primaryKey) => inner.CreateTable(schema, table, columns, primaryKey);
        public string AlterColumnType(string schema, string table, SnapColumn column, string newType) => inner.AlterColumnType(schema, table, column, newType);
        public string AddColumn(string schema, string table, string column, string type) => inner.AddColumn(schema, table, column, type);
        public IBulkLoader? Loader => null;
    }

    sealed class ServerError(string message) : DbException(message);

    async Task<ImportProgressEvent> ImportAsync(string engine, Func<FakeDbCommand, Exception?> onTx, ImportRunOptions? options = null, int rows = 3,
        Func<ITransactionControl, ITransactionControl>? transactions = null)
    {
        await using var h = new TestHostWithFake();
        h.Provider.DataEditImpl = engine == "pg" ? new PostgresDataEdit() : new SqlServerDataEdit();
        h.Provider.ImportImpl = new GenericImport(engine == "pg" ? new PostgresImport() : new SqlServerImport());
        if (engine == "pg") h.Provider.Transactions = new FakePgTransactions();
        if (transactions != null) h.Provider.Transactions = transactions(h.Provider.Transactions);
        h.Provider.Handler = (cmd, _) => onTx(cmd) is { } fail ? throw fail : Task.FromResult(FakeResult.None);
        var file = Path.Combine(_dir, $"{engine}-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(file, "id,name\n" + string.Concat(Enumerable.Range(1, rows).Select(i => $"{i},n{i}\n")));
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "tok", FilePath = file });
        var schema = engine == "pg" ? "public" : "dbo";
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true, [new ObjectStamp(1, schema, "items", ObjectKind.Table, 1, null)], [],
            new MetadataBundle([new ColumnRow(1, 1, "id", engine == "pg" ? "integer" : "int", null, null, null, false, false, false, null),
                                new ColumnRow(1, 2, "name", "varchar", 20, null, null, true, false, false, null)], [], [], []), "16");
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");
        var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = s.SessionId, InspectionId = ins.InspectionId, FileToken = "tok", FilePath = file, Schema = schema, Table = "items", Options = options });
        Assert.NotNull(run.JobId);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        ImportProgressEvent? done;
        while ((done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done)) == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the import did not finish");
            await Task.Delay(20);
        }
        return done;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_cut_short_by_a_lost_connection_reports_the_outcome_unknown(string engine)
    {
        var done = await ImportAsync(engine, cmd =>
        {
            if (cmd.CommandText.Trim() != CommitSql(engine)) return null;
            cmd.Fake.Break();
            return new InvalidOperationException("The connection is broken.");
        });
        Assert.Equal("outcomeUnknown", done.ErrorCode);
        Assert.False(done.Cancelled);
        Assert.Contains("whether its 3 row(s) were written", done.Error);
        Assert.Contains("is unknown", done.Error);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_commit_the_server_refuses_on_a_working_connection_is_an_ordinary_failure(string engine)
    {
        var done = await ImportAsync(engine, cmd => cmd.CommandText.Trim() == CommitSql(engine) ? new ServerError("deferred constraint violated") : null);
        Assert.Null(done.ErrorCode);
        Assert.Contains("deferred constraint violated", done.Error);
        Assert.Equal(0, done.RowsLoaded);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_failure_before_the_commit_says_nothing_about_an_unknown_outcome(string engine)
    {
        var done = await ImportAsync(engine, cmd => cmd.CommandText.Contains("INSERT INTO", StringComparison.Ordinal) ? new ServerError("duplicate key") : null);
        Assert.Null(done.ErrorCode);
        Assert.Contains("duplicate key", done.Error);
    }

    /// <summary>SQLite's / DuckDB's commit: it checks for the transaction first and sends nothing when it is gone.</summary>
    sealed class GoneAtCommit(ITransactionControl inner) : ITransactionControl
    {
        public TransactionTraits Traits => inner.Traits;
        public Task BeginAsync(DbConnection c, CancellationToken ct) => inner.BeginAsync(c, ct);
        public Task<bool> BeginReadOnlyAsync(DbConnection c, CancellationToken ct) => inner.BeginReadOnlyAsync(c, ct);
        public Task CommitAsync(DbConnection c, CancellationToken ct) =>
            throw new NoTransactionOpenException("No transaction is open: it had already ended (rolled back by an interrupted statement, or committed by the script).");
        public Task RollbackAsync(DbConnection c, CancellationToken ct) => inner.RollbackAsync(c, ct);
        public Task SavepointAsync(DbConnection c, string name, CancellationToken ct) => inner.SavepointAsync(c, name, ct);
        public Task RollbackToSavepointAsync(DbConnection c, string name, CancellationToken ct) => inner.RollbackToSavepointAsync(c, name, ct);
        public Task ReleaseSavepointAsync(DbConnection c, string name, CancellationToken ct) => inner.ReleaseSavepointAsync(c, name, ct);
        public Task<TxState> StateAsync(DbConnection c, CancellationToken ct) => inner.StateAsync(c, ct);
    }

    [Fact]
    public async Task A_commit_that_finds_its_transaction_already_rolled_back_is_a_known_outcome_not_an_unknown_one()
    {
        // 8a review: SQLite and DuckDB throw before sending anything when an interrupted statement ended the transaction.
        var done = await ImportAsync("mssql", _ => null, transactions: t => new GoneAtCommit(t));
        Assert.Null(done.ErrorCode);
        Assert.False(done.Cancelled);
        Assert.Contains("nothing was written", done.Error);
        Assert.Equal(0, done.RowsLoaded);
    }

    [Fact]
    public void InCommit_is_set_while_the_commit_runs_and_stays_set_when_it_throws()
    {
        var tx = new BulkTransaction(BulkTxMode.Own);
        Assert.False(tx.InCommit);
        var seen = false;
        tx.CommitAsync(() => { seen = tx.InCommit; return Task.CompletedTask; }).GetAwaiter().GetResult();
        Assert.True(seen);
        Assert.False(tx.InCommit);
        Assert.Throws<InvalidOperationException>(() => tx.CommitAsync(() => throw new InvalidOperationException("lost")).GetAwaiter().GetResult());
        Assert.True(tx.InCommit);
    }
}
