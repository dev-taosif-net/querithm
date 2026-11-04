using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Grid-edit audit: result shapes whose origins could name another row than the one shown (self-joins, set operators,
/// an unqualified name in several schemas), MySQL tables that cannot roll back, and unique indexes that prove nothing.
/// Each case was editable before its fix.
/// </summary>
public class EditAuditTests
{
    public static TheoryData<string> AllEngines => new() { "mssql", "pg", "mysql", "sqlite", "duckdb" };

    static IDataEditDialect Dialect(string engine) => engine switch
    {
        "pg" => new PostgresDataEdit(),
        "mysql" => new MySqlDataEdit(),
        "sqlite" => new SqliteDataEdit(),
        "duckdb" => new DuckDbDataEdit(),
        _ => new SqlServerDataEdit(),
    };

    /// <summary>One table, id + code, no primary key; <paramref name="engine"/> spelled types.</summary>
    static SchemaSnapshot Table(string? noRollbackEngine = null) => new()
    {
        ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh, Fks = [],
        Objects =
        [
            new SnapObject
            {
                Id = 1, Schema = "app", Name = "t", Kind = ObjectKind.Table, NoRollbackEngine = noRollbackEngine,
                Columns =
                [
                    new SnapColumn { Name = "id", Type = "int", Pk = true, Ordinal = 1 },
                    new SnapColumn { Name = "code", Type = "varchar(10)", Ordinal = 2 },
                    new SnapColumn { Name = "v", Type = "int", Nullable = true, Ordinal = 3 },
                ],
            },
        ],
    };

    static (ResultColumn[], ColumnOrigin?[]) Result(params string[] columns) =>
        (columns.Select((c, i) => new ResultColumn { Name = c, TypeName = "x", ArrowType = "utf8", Ordinal = i }).ToArray(),
         columns.Select(c => (ColumnOrigin?)new ColumnOrigin(1, c)).ToArray());

    // ------------------------------------------------------------------------------- MySQL engines without rollback

    [Theory]
    [InlineData("MyISAM")]
    [InlineData("MEMORY")]
    [InlineData("ARCHIVE")]
    [InlineData("Aria")]
    public void A_table_whose_engine_cannot_roll_back_is_read_only(string engine)
    {
        var (cols, origins) = Result("id", "v");
        var e = EditabilityAnalyzer.Analyze(new MySqlDataEdit(), cols, origins, Table(engine), _ => []);
        Assert.False(e.Editable);
        Assert.Equal($"Read-only: app.t is a {engine} table — its changes can't be rolled back", e.Reason);
        Assert.True(EditabilityAnalyzer.Analyze(new MySqlDataEdit(), cols, origins, Table(), _ => []).Editable);
    }

    [Fact]
    public void The_no_rollback_engine_and_the_not_a_key_flag_survive_the_metadata_store()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        store.ApplyRefresh("p", "db", true,
            [new ObjectStamp(1, "app", "t", ObjectKind.Table, 1, null, NoRollbackEngine: "MyISAM"), new ObjectStamp(2, "app", "u", ObjectKind.Table, 1, null)], [],
            new MetadataBundle([new ColumnRow(1, 1, "id", "int", null, null, null, false, false, false, null)], [],
                [new IndexRow(1, "ux_partial", true, false, ["id"], NotAKey: true), new IndexRow(1, "ux_id", true, false, ["id"])], []),
            "8.4");
        var snap = store.Load("p", "db");
        Assert.Equal("MyISAM", snap.Objects.Single(o => o.Id == 1).NoRollbackEngine);
        Assert.Null(snap.Objects.Single(o => o.Id == 2).NoRollbackEngine);
        var idx = store.Details("p", "db", 1).Indexes;
        Assert.True(idx.Single(i => i.Name == "ux_partial").NotAKey);
        Assert.False(idx.Single(i => i.Name == "ux_id").NotAKey);
    }

    // ------------------------------------------------------------------------------- unique indexes that prove nothing

    [Theory, MemberData(nameof(AllEngines))]
    public void A_partial_filtered_expression_or_disabled_unique_index_is_never_the_row_key(string engine)
    {
        var snap = Table() with { Objects = [Table().Objects[0] with { Columns = [.. Table().Objects[0].Columns!.Select(c => c with { Pk = false })] }] };
        var (cols, origins) = Result("code", "v");
        IndexInfo[] NotAKey(long _) => [new IndexInfo { Name = "ux_code_active", IsUnique = true, Columns = ["code"], NotAKey = true }];
        var e = EditabilityAnalyzer.Analyze(Dialect(engine), cols, origins, snap, NotAKey);
        Assert.False(e.Editable);
        Assert.Equal("Read-only: app.t has no primary key or NOT NULL unique index", e.Reason);
        // The same index without the flag is a key, as before.
        IndexInfo[] Plain(long _) => [new IndexInfo { Name = "ux_code", IsUnique = true, Columns = ["code"] }];
        Assert.Equal(["code"], EditabilityAnalyzer.Analyze(Dialect(engine), cols, origins, snap, Plain).KeyColumns);
    }

    // ------------------------------------------------------------------------------- statement shapes (tokens)

    [Theory]
    [InlineData("SELECT a.id, b.v FROM t a JOIN t b ON b.id = a.id + 1", "the same table (t) appears more than once in the query")]
    // public.t and t: the check compares the table part, so it errs on the safe side (as it does for "T" against t).
    [InlineData("SELECT a.id, a.v FROM public.t a, t b WHERE b.id = a.id", "the same table (t) appears more than once in the query")]
    [InlineData("SELECT id, v FROM t WHERE id IN (SELECT id FROM t WHERE v > 1)", "the same table (t) appears more than once in the query")]
    [InlineData("SELECT * FROM (t a JOIN \"T\" b ON true)", "the same table (t) appears more than once in the query")]
    [InlineData("SELECT * FROM (t a JOIN t b ON true)", "the same table (t) appears more than once in the query")]
    [InlineData("WITH x AS (SELECT id FROM t) SELECT t.id, t.v FROM t JOIN x ON x.id = t.id", "the same table (t) appears more than once in the query")]
    [InlineData("SELECT id, v FROM t UNION ALL SELECT id, v FROM u", "the query combines several queries (UNION)")]
    [InlineData("SELECT id FROM t EXCEPT SELECT id FROM u", "the query combines several queries (EXCEPT)")]
    [InlineData("SELECT id FROM t INTERSECT SELECT id FROM u", "the query combines several queries (INTERSECT)")]
    public void Self_joins_and_set_operators_are_refused_from_postgres_tokens(string sql, string reason) =>
        Assert.Equal(reason, PgResultShape.EditRefusal(sql));

    [Theory]
    [InlineData("SELECT id, v FROM t WHERE v > 0 ORDER BY id")]
    [InlineData("SELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.customer_id, lines l WHERE l.order_id = o.id")]
    [InlineData("SELECT id, extract(year FROM placed) AS y, extract(month FROM placed) AS m, substring(code FROM 2) FROM t")]
    [InlineData("SELECT id, 'FROM t JOIN t' AS s FROM t")]
    [InlineData("SELECT * FROM generate_series(1, 3) g, generate_series(1, 3) h")]
    [InlineData("SELECT id, count(*) OVER (PARTITION BY code ORDER BY id) FROM t")]
    public void Plain_queries_keep_their_editability(string sql)
    {
        Assert.Null(PgResultShape.EditRefusal(sql));
        Assert.Null(SqliteResultShape.EditRefusal(sql));
    }

    [Theory]
    [InlineData("SELECT a.id, b.v FROM [t] a JOIN \"t\" b ON b.id = a.id", "the same table (t) appears more than once in the query")]
    [InlineData("SELECT id, v FROM t UNION ALL SELECT id, v FROM t2", "the query combines several queries (UNION)")]
    [InlineData("SELECT * FROM (SELECT id, v FROM t UNION SELECT id, v FROM u)", "the query combines several queries (UNION)")]
    public void Self_joins_and_compound_selects_are_refused_from_sqlite_tokens(string sql, string reason) =>
        Assert.Equal(reason, SqliteResultShape.EditRefusal(sql));

    [Theory]
    [InlineData("SELECT a.id FROM t a JOIN t b ON b.id = a.id", "the same table (t) appears more than once in the query")]
    [InlineData("FROM t UNION ALL FROM t2", "the query combines several queries (UNION)")]
    public void Duckdb_names_the_reason_too(string sql, string reason) =>
        Assert.Equal(reason, new DuckDbProvider().ResultEditRefusal(sql, 0, null));

    [Fact]
    public void Each_engine_provider_reports_its_shape_refusal()
    {
        const string self = "SELECT a.id, b.v FROM t a JOIN t b ON b.id = a.id";
        Assert.NotNull(new PostgresProvider().ResultEditRefusal(self, 0, null));
        Assert.NotNull(new SqliteProvider().ResultEditRefusal(self, 0, null));
        Assert.NotNull(new DuckDbProvider().ResultEditRefusal(self, 0, null));
        // MySQL reports each column's own alias (MySqlDbColumn.TableName) and needs no text check.
        Assert.Null(((IDbProvider)new MySqlProvider()).ResultEditRefusal(self, 0, null));
    }

    // ------------------------------------------------------------------------------- SQL Server (ScriptDom)

    static readonly SchemaSnapshot Sales = FakeSchema.SalesDb();

    [Fact]
    public void Sql_server_self_join_has_no_origins_and_says_why()
    {
        const string sql = "SELECT a.OrderId, b.Status FROM dbo.Orders a JOIN Orders b ON b.OrderId = a.OrderId + 1";
        Assert.Null(Assert.Single(ResultSources.Predict(sql, Sales)!));
        Assert.Equal("the same table (dbo.Orders) appears more than once in the query", ResultSources.Refusal(sql, Sales, 0));
        Assert.Equal("the same table (dbo.Orders) appears more than once in the query", new SqlServerProvider().ResultEditRefusal(sql, 0, Sales));
        Assert.Null(ResultSources.Refusal("SELECT o.OrderId, c.FullName FROM Orders o JOIN Customers c ON c.CustomerId = o.CustomerId", Sales, 0));
    }

    [Fact]
    public void Sql_server_set_operators_say_why()
    {
        Assert.Equal("the query combines several queries (UNION)", ResultSources.Refusal("SELECT OrderId FROM Orders UNION ALL SELECT OrderId FROM Orders", Sales, 0));
        Assert.Equal("the query combines several queries (EXCEPT)", ResultSources.Refusal("SELECT OrderId FROM Orders EXCEPT SELECT CustomerId FROM Customers", Sales, 0));
        // The second result set of the batch is the combined one.
        Assert.Null(ResultSources.Refusal("SELECT OrderId FROM Orders; SELECT 1 INTERSECT SELECT 2", Sales, 0));
        Assert.Equal("the query combines several queries (INTERSECT)", ResultSources.Refusal("SELECT OrderId FROM Orders; SELECT 1 INTERSECT SELECT 2", Sales, 1));
    }

    [Fact]
    public void Sql_server_unqualified_name_in_several_schemas_is_ambiguous_not_dbo()
    {
        var snap = Sales with
        {
            Objects = [.. Sales.Objects, new SnapObject
            {
                Id = 90, Schema = "audit", Name = "Orders", Kind = ObjectKind.Table,
                Columns = [new SnapColumn { Name = "OrderId", Type = "bigint", Pk = true }, new SnapColumn { Name = "Status", Type = "nvarchar(20)", Nullable = true }],
            }],
        };
        const string sql = "SELECT OrderId, Status FROM Orders";
        Assert.Null(Assert.Single(ResultSources.Predict(sql, snap)!));
        Assert.Equal("table Orders exists in more than one schema (ambiguous schema — qualify the table name)", ResultSources.Refusal(sql, snap, 0));
        // Qualified, it is exact again.
        var set = Assert.Single(ResultSources.Predict("SELECT OrderId, Status FROM dbo.Orders", snap)!)!;
        Assert.Equal(new ColumnOrigin(1, "OrderId"), set[0].Origin);
        Assert.Null(ResultSources.Refusal("SELECT OrderId, Status FROM audit.Orders", snap, 0));
    }

    // ------------------------------------------------------------------------------- the execution path

    [Fact]
    public async Task A_shape_refusal_makes_the_recorded_result_read_only_with_its_reason_and_no_origins()
    {
        var provider = new FakeDbProvider
        {
            Handler = (_, _) => Task.FromResult(new FakeResult([FakeResult.Table([("id", typeof(int))], [1])])),
            EditRefusal = (text, _) => text.Contains("refuse") ? "the same table (t) appears more than once in the query" : null,
        };
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var notifier = new CapturingNotifier();
        await using var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), notifier, Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var seenOrigins = new List<ColumnOrigin?[]?>();
        await using var executions = new ExecutionService(sessions, new CapturingFrames(), notifier, new HistoryStore(store), Log.Null)
        {
            Editability = (_, _, origins, _) => { seenOrigins.Add(origins); return new ResultEditInfo { Editable = true }; },
        };
        var s = sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId;

        var refused = executions.Execute(new QueryExecuteParams { SessionId = s, Sql = "SELECT id FROM t /* refuse */" }).ExecutionId!;
        await notifier.WaitDone(refused);
        var edit = executions.Recorded(s, refused, 0)!.Edit!;
        Assert.False(edit.Editable);
        Assert.Equal("Read-only: the same table (t) appears more than once in the query", edit.Reason);
        Assert.Null(executions.Recorded(s, refused, 0)!.Origins);

        var plain = executions.Execute(new QueryExecuteParams { SessionId = s, Sql = "SELECT id FROM t" }).ExecutionId!;
        await notifier.WaitDone(plain);
        Assert.True(executions.Recorded(s, plain, 0)!.Edit!.Editable);
    }

    [Fact]
    public async Task A_shape_check_that_fails_leaves_the_result_read_only()
    {
        var provider = new FakeDbProvider
        {
            Handler = (_, _) => Task.FromResult(new FakeResult([FakeResult.Table([("id", typeof(int))], [1])])),
            EditRefusal = (_, _) => throw new InvalidOperationException("tokenizer bug"),
        };
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u" });
        var notifier = new CapturingNotifier();
        await using var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), notifier, Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        await using var executions = new ExecutionService(sessions, new CapturingFrames(), notifier, new HistoryStore(store), Log.Null)
        {
            Editability = (_, _, _, _) => new ResultEditInfo { Editable = true },
        };
        var s = sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId;
        var id = executions.Execute(new QueryExecuteParams { SessionId = s, Sql = "SELECT id FROM t" }).ExecutionId!;
        var done = await notifier.WaitDone(id);
        Assert.Null(done.Error); // the query itself is unaffected
        Assert.Equal("Read-only: the query's shape could not be checked", executions.Recorded(s, id, 0)!.Edit!.Reason);
    }
}
