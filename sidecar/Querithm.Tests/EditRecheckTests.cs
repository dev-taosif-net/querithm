using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Oracle;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// The edit option that "often did not appear": the verdict was decided once, from whatever metadata was cached when the
/// query ran, so a table created or altered since the last refresh stayed read-only until the user refreshed and re-ran.
/// Now a read-only verdict that out-of-date metadata may explain is marked <see cref="ResultEditInfo.Rechecking"/>, the
/// metadata is read again after the run, and <c>query.resultEdit</c> carries the final verdict — whatever happens to that read.
/// </summary>
public class EditRecheckTests
{
    public static TheoryData<string> AllEngines => new() { "mssql", "pg", "mysql", "oracle", "sqlite", "duckdb" };

    static IDataEditDialect Dialect(string engine) => engine switch
    {
        "pg" => new PostgresDataEdit(),
        "mysql" => new MySqlDataEdit(),
        "oracle" => new OracleDataEdit(),
        "sqlite" => new SqliteDataEdit(),
        "duckdb" => new DuckDbDataEdit(),
        _ => new SqlServerDataEdit(),
    };

    const string Missing = "app.t is not in this database's cached metadata";

    /// <summary>The table the queries read: id (primary key) and v, in a type each engine's dialect matches exactly.</summary>
    static SnapObject TableT(string engine)
    {
        var type = engine == "oracle" ? "NUMBER(10)" : "int";
        return new SnapObject
        {
            Id = 1, Schema = "app", Name = "t", Kind = ObjectKind.Table,
            Columns = [new SnapColumn { Name = "id", Type = type, Pk = true, Ordinal = 1 }, new SnapColumn { Name = "v", Type = type, Nullable = true, Ordinal = 2 }],
        };
    }

    static SchemaSnapshot Snap(params SnapObject[] objects) => new() { ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh, Fks = [], Objects = objects };

    /// <summary>Another table, so the snapshot is not empty: the stale one is the table, not the whole database.</summary>
    static SnapObject Other => new() { Id = 9, Schema = "app", Name = "other", Kind = ObjectKind.Table, Columns = [new SnapColumn { Name = "id", Type = "int", Pk = true, Ordinal = 1 }] };

    static ResultColumn[] Columns(params string[] names) => names.Select((c, i) => new ResultColumn { Name = c, TypeName = "int", ArrowType = "int32", Ordinal = i }).ToArray();

    // ------------------------------------------------------------------------------- the analyzer: what is worth a second look

    [Theory, MemberData(nameof(AllEngines))]
    public void A_missing_or_empty_snapshot_is_worth_reading_the_metadata_again(string engine)
    {
        foreach (var snapshot in new[] { null, Snap() with { Status = SnapshotStatus.Empty } })
        {
            var e = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), null, snapshot, _ => []);
            Assert.False(e.Editable);
            Assert.True(e.Rechecking);
            Assert.Equal("Read-only: no cached metadata for this database yet (open it in the explorer to load it)", e.Reason);
        }
        // Read, and it lists no tables: said so, not "no metadata yet … open it in the explorer" (which the recheck then
        // called "checked against freshly read metadata" about a database it had just read). A new table may still come.
        var empty = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), null, Snap(), _ => []);
        Assert.Equal((false, true, "Read-only: this database's cached metadata lists no tables"), (empty.Editable, empty.Rechecking, empty.Reason));
    }

    [Theory, MemberData(nameof(AllEngines))]
    public void Untraced_columns_say_the_providers_reason_and_are_rechecked_only_when_it_calls_them_stale(string engine)
    {
        var stale = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), null, Snap(Other), _ => [], Missing, stale: true);
        Assert.Equal((false, true, "Read-only: " + Missing), (stale.Editable, stale.Rechecking, stale.Reason));

        // The same for a result whose columns are named but not one traces to a table (SELECT id, v FROM new_table).
        var none = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), [null, null], Snap(Other), _ => [], Missing, stale: true);
        Assert.Equal((false, true, "Read-only: " + Missing), (none.Editable, none.Rechecking, none.Reason));

        // A reason the query's shape explains is final: reading the metadata again would change nothing.
        const string shape = "this result comes after EXEC in the same batch";
        var final = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), null, Snap(Other), _ => [], shape, stale: false);
        Assert.Equal((false, false, "Read-only: " + shape), (final.Editable, final.Rechecking, final.Reason));

        // No reason from the provider: the old wording, and final.
        var unknown = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), null, Snap(Other), _ => []);
        Assert.Equal((false, false, "Read-only: column origins unknown for this batch"), (unknown.Editable, unknown.Rechecking, unknown.Reason));
        var computed = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), [null, null], Snap(Other), _ => []);
        Assert.Equal((false, false, "Read-only: the result has no table columns"), (computed.Editable, computed.Rechecking, computed.Reason));
    }

    [Theory, MemberData(nameof(AllEngines))]
    public void A_table_missing_from_the_snapshot_or_without_columns_is_rechecked_and_a_keyless_one_is_final(string engine)
    {
        ColumnOrigin?[] origins = [new ColumnOrigin(1, "id"), new ColumnOrigin(1, "v")];
        var gone = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), origins, Snap(Other), _ => []);
        Assert.Equal((false, true), (gone.Editable, gone.Rechecking));

        var bare = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), origins, Snap(TableT(engine) with { Columns = null }), _ => []);
        Assert.Equal((false, true, "Read-only: column metadata for app.t is not loaded yet"), (bare.Editable, bare.Rechecking, bare.Reason));

        var keyless = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), origins, Snap(TableT(engine) with { Columns = [.. TableT(engine).Columns!.Select(c => c with { Pk = false })] }), _ => []);
        Assert.Equal((false, false), (keyless.Editable, keyless.Rechecking));

        // A join is the query's own doing.
        var join = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "x"), [new ColumnOrigin(1, "id"), new ColumnOrigin(9, "id")], Snap(TableT(engine), Other), _ => []);
        Assert.Equal((false, false), (join.Editable, join.Rechecking));

        var ok = EditabilityAnalyzer.Analyze(Dialect(engine), Columns("id", "v"), origins, Snap(TableT(engine)), _ => []);
        Assert.True(ok.Editable, ok.Reason);
        Assert.False(ok.Rechecking);
    }

    // ------------------------------------------------------------------------------- the execution path

    sealed class Schema : ISchemaSource
    {
        public volatile SchemaSnapshot? Current;
        public SchemaSnapshot? Get(string? profileId, string? database) => Current;
        public IReadOnlyList<string> Databases(string? profileId) => [];
    }

    sealed class Rig : IAsyncDisposable
    {
        public FakeDbProvider Provider { get; }
        public CapturingNotifier Notifier { get; } = new();
        public Schema Schema { get; } = new() { Current = Snap(Other) };
        public SessionManager Sessions { get; }
        public ExecutionService Executions { get; }
        public string SessionId { get; }
        /// <summary>Calls of the metadata read; what it does is <see cref="OnRefresh"/> (default: the table appears).</summary>
        public string Engine { get; }
        public int Refreshes;
        public Func<CancellationToken, Task<string?>> OnRefresh;

        public Rig(string engine, bool withRefresh = true, params object?[][] rows)
        {
            Engine = engine;
            Provider = new FakeDbProvider
            {
                Handler = (_, _) => Task.FromResult(new FakeResult([FakeResult.Table([("id", typeof(int)), ("v", typeof(int))], rows)])),
                // The fake engine traces t only when the snapshot holds it, and calls anything else stale — as the real
                // providers do for a table the server read but the cache does not list.
                Origins = (_, _, snap) => snap?.Objects.Any(o => o.Id == 1 && o.Columns != null) == true
                    ? new TracedOrigins([new ColumnOrigin(1, "id"), new ColumnOrigin(1, "v")])
                    : new TracedOrigins(null, Missing, Stale: true),
            };
            OnRefresh = _ => { Schema.Current = Snap(Other, TableT(engine)); return Task.FromResult<string?>(null); };
            var store = LocalStore.InMemory();
            var profiles = new ProfileStore(store);
            var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Contract.Engine.Mssql, Host = "h", Auth = AuthKind.Sql, Username = "u", DatabaseDefault = "db" });
            Sessions = new SessionManager(profiles, new ProviderRegistry([Provider]), Notifier, Log.Null);
            Sessions.RememberSecret(profile.Id, "x");
            Executions = new ExecutionService(Sessions, new CapturingFrames(), Notifier, new HistoryStore(store), Log.Null, Schema)
            {
                Editability = (_, cols, o, snap) => EditabilityAnalyzer.Analyze(Dialect(engine), cols, o.Columns, snap, _ => [], o.Why, o.Stale),
            };
            if (withRefresh) Executions.RefreshMetadata = async (_, _, _, ct) => { Interlocked.Increment(ref Refreshes); return await OnRefresh(ct); };
            SessionId = Sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Secret = "x" }).SessionId;
        }

        public async Task<string> RunAsync(string sql = "SELECT id, v FROM app.t")
        {
            var id = Executions.Execute(new QueryExecuteParams { SessionId = SessionId, Sql = sql }).ExecutionId!;
            Assert.Null((await Notifier.WaitDone(id)).Error);
            return id;
        }

        public ResultEditInfo First(string execution, int result = 0) =>
            Notifier.Of<QueryResultSetEvent>().Single(e => e.ExecutionId == execution && e.ResultIndex == result).Edit!;

        public async Task<ResultEditInfo> FinalAsync(string execution, int result = 0)
        {
            var deadline = DateTime.UtcNow + TestTime.Of(TimeSpan.FromSeconds(15));
            while (DateTime.UtcNow < deadline)
            {
                if (Notifier.Of<QueryResultEditEvent>().FirstOrDefault(e => e.ExecutionId == execution && e.ResultIndex == result) is { } e) return e.Edit;
                await Task.Delay(10);
            }
            throw new Xunit.Sdk.XunitException("no query.resultEdit arrived: the webview would show \"checking\" for ever");
        }

        public ResultEditInfo Recorded(string execution, int result = 0) => Executions.Recorded(SessionId, execution, result)!.Edit!;

        public async ValueTask DisposeAsync()
        {
            await Executions.DisposeAsync();
            await Sessions.DisposeAsync();
        }
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_table_created_after_the_snapshot_was_read_becomes_editable_without_running_again(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10], [2, 20]]);
        var id = await rig.RunAsync();

        // First verdict: read-only, but marked as being checked — never a silent, final "read-only".
        var first = rig.First(id);
        Assert.Equal((false, true, "Read-only: " + Missing), (first.Editable, first.Rechecking, first.Reason));

        var final = await rig.FinalAsync(id);
        Assert.True(final.Editable, final.Reason);
        Assert.False(final.Rechecking);
        Assert.Equal(("app", "t"), (final.Schema, final.Table));
        Assert.Equal(["id"], final.KeyColumns);
        Assert.Equal(1, rig.Refreshes);

        // What Apply resolves against is the new verdict, with the origins traced the second time.
        var rec = rig.Executions.Recorded(rig.SessionId, id, 0)!;
        Assert.True(rec.Edit!.Editable);
        Assert.Equal([new ColumnOrigin(1, "id"), new ColumnOrigin(1, "v")], rec.Origins);
        Assert.Equal("db", rec.Database);
        Assert.Single(rig.Notifier.Of<QueryResultEditEvent>(), e => e.ExecutionId == id);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task An_empty_result_of_a_new_table_becomes_editable_too(string engine)
    {
        // "When the table returns no rows the edit option does not appear": an empty table is usually a new one, which the
        // cached metadata does not hold yet. The verdict never depended on rows; it depended on that cache.
        await using var rig = new Rig(engine);
        var id = await rig.RunAsync();
        Assert.True(rig.First(id).Rechecking);
        var final = await rig.FinalAsync(id);
        Assert.True(final.Editable, final.Reason);
        Assert.Null(final.InsertBlockedReason); // the first row can be added
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task An_empty_result_of_a_known_table_is_editable_at_once(string engine)
    {
        await using var rig = new Rig(engine);
        rig.Schema.Current = Snap(Other, TableT(engine));
        var id = await rig.RunAsync();
        var first = rig.First(id);
        Assert.True(first.Editable, first.Reason);
        Assert.False(first.Rechecking);
        Assert.Equal(0, rig.Refreshes);
        Assert.Empty(rig.Notifier.Of<QueryResultEditEvent>());
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_verdict_that_stays_read_only_against_fresh_metadata_is_final_and_is_not_checked_on_every_run(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.OnRefresh = _ => Task.FromResult<string?>(null); // the refresh succeeds, the table is still not listed (a temporary table)
        var id = await rig.RunAsync();
        var final = await rig.FinalAsync(id);
        Assert.Equal((false, false, "Read-only: " + Missing + ExecutionService.CheckedFresh), (final.Editable, final.Rechecking, final.Reason));
        Assert.Equal(final, rig.Recorded(id));

        // The same reason on the same database moments later: no second metadata read, and the answer is final at once.
        var again = await rig.RunAsync();
        var first = rig.First(again);
        Assert.Equal((false, false, "Read-only: " + Missing + ExecutionService.CheckedFresh), (first.Editable, first.Rechecking, first.Reason));
        Assert.Equal(1, rig.Refreshes);
        Assert.DoesNotContain(rig.Notifier.Of<QueryResultEditEvent>(), e => e.ExecutionId == again);

        // Once that window has passed, it is checked again (the table may exist in the catalog by now).
        rig.Executions.RecheckSettle = TimeSpan.Zero;
        rig.OnRefresh = _ => { rig.Schema.Current = Snap(Other, TableT(engine)); return Task.FromResult<string?>(null); };
        var later = await rig.RunAsync();
        Assert.True(rig.First(later).Rechecking);
        Assert.True((await rig.FinalAsync(later)).Editable);
        Assert.Equal(2, rig.Refreshes);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_metadata_read_that_fails_leaves_the_result_read_only_with_the_reason_and_is_tried_again_next_run(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.OnRefresh = _ => Task.FromResult<string?>("the server was not found");
        var id = await rig.RunAsync();
        var final = await rig.FinalAsync(id);
        Assert.Equal((false, false), (final.Editable, final.Rechecking));
        Assert.Equal("Read-only: " + Missing + " — reading the metadata again failed: the server was not found", final.Reason);
        Assert.False(rig.Recorded(id).Rechecking);

        // A failed read settles nothing: the next run tries again, and succeeds.
        rig.OnRefresh = _ => { rig.Schema.Current = Snap(Other, TableT(engine)); return Task.FromResult<string?>(null); };
        var again = await rig.RunAsync();
        Assert.True(rig.First(again).Rechecking);
        Assert.True((await rig.FinalAsync(again)).Editable);
        Assert.Equal(2, rig.Refreshes);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_metadata_read_that_throws_still_answers(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.OnRefresh = _ => throw new InvalidOperationException("connection reset; Password=hunter2");
        var id = await rig.RunAsync();
        var final = await rig.FinalAsync(id);
        Assert.Equal((false, false), (final.Editable, final.Rechecking));
        Assert.StartsWith("Read-only: " + Missing + " — reading the metadata again failed: connection reset", final.Reason);
        Assert.DoesNotContain("hunter2", final.Reason); // §13: a driver message is scrubbed before it reaches the webview
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_metadata_read_that_never_ends_is_given_up_within_its_limit(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.Executions.RecheckLimit = TimeSpan.FromMilliseconds(150);
        rig.OnRefresh = async ct => { await Task.Delay(Timeout.Infinite, ct); return null; }; // a server that never answers
        var id = await rig.RunAsync();
        var final = await rig.FinalAsync(id);
        Assert.Equal((false, false), (final.Editable, final.Rechecking));
        Assert.Equal("Read-only: " + Missing + " — reading the metadata again failed: it did not finish within 0 s", final.Reason);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_read_only_result_the_query_itself_explains_is_never_rechecked(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.Provider.Origins = (_, _, _) => new TracedOrigins(null, "this result comes after EXEC in the same batch");
        var id = await rig.RunAsync();
        var first = rig.First(id);
        Assert.Equal((false, false, "Read-only: this result comes after EXEC in the same batch"), (first.Editable, first.Rechecking, first.Reason));
        await Task.Delay(100);
        Assert.Equal(0, rig.Refreshes);
        Assert.Empty(rig.Notifier.Of<QueryResultEditEvent>());
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_shape_refusal_is_never_rechecked(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.Provider.EditRefusal = (_, _) => "the same table (t) appears more than once in the query";
        var id = await rig.RunAsync();
        var first = rig.First(id);
        Assert.Equal((false, false), (first.Editable, first.Rechecking));
        await Task.Delay(100);
        Assert.Equal(0, rig.Refreshes);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task Without_a_way_to_read_the_metadata_again_the_first_verdict_is_final(string engine)
    {
        await using var rig = new Rig(engine, withRefresh: false, rows: [[1, 10]]);
        var id = await rig.RunAsync();
        var first = rig.First(id);
        Assert.Equal((false, false, "Read-only: " + Missing), (first.Editable, first.Rechecking, first.Reason));
        Assert.False(rig.Recorded(id).Rechecking);
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task Every_result_set_of_a_run_is_answered_by_one_metadata_read(string engine)
    {
        await using var rig = new Rig(engine);
        var table = FakeResult.Table([("id", typeof(int)), ("v", typeof(int))], [1, 10]);
        rig.Provider.Handler = (_, _) => Task.FromResult(new FakeResult([table, table, table]));
        var id = await rig.RunAsync("SELECT id, v FROM app.t; SELECT id, v FROM app.t; SELECT id, v FROM app.t");
        for (var i = 0; i < 3; i++)
        {
            Assert.True(rig.First(id, i).Rechecking);
            Assert.True((await rig.FinalAsync(id, i)).Editable);
        }
        Assert.Equal(1, rig.Refreshes);
        Assert.Equal(3, rig.Notifier.Of<QueryResultEditEvent>().Count(e => e.ExecutionId == id));
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task The_final_verdict_arrives_after_the_run_ended(string engine)
    {
        // The metadata is read after the run let go of its connection: a session-owned engine reads it on that connection.
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        var id = await rig.RunAsync();
        await rig.FinalAsync(id);
        var order = rig.Notifier.All.Select(x => x.Payload).ToList();
        var done = order.FindIndex(p => p is QueryDoneEvent d && d.ExecutionId == id);
        var edit = order.FindIndex(p => p is QueryResultEditEvent e && e.ExecutionId == id);
        Assert.True(done >= 0 && edit > done, $"query.done at {done}, query.resultEdit at {edit}");
    }

    [Theory, MemberData(nameof(AllEngines))]
    public async Task A_check_that_throws_says_so_instead_of_leaving_no_verdict(string engine)
    {
        await using var rig = new Rig(engine, rows: [[1, 10]]);
        rig.Executions.Editability = (_, _, _, _) => throw new InvalidOperationException("analyzer bug");
        var id = await rig.RunAsync();
        var first = rig.First(id);
        Assert.Equal((false, false), (first.Editable, first.Rechecking));
        Assert.Equal("Read-only: Querithm could not check whether these rows can be edited — run the query again", first.Reason);
    }
}
