using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>
/// Completion audit 2026-11-25 (M8, found worse than suspected): ScriptDom recurses while it parses, so a 16,000-term
/// OR or UNION ALL chain, or 1,000 nested parentheses or CASEs, overflowed a thread-pool thread's stack inside
/// <c>TSql160Parser.Parse</c> and ended the process — the sidecar, from completion, diagnostics, folding, formatting or the
/// classifier every run goes through. Each case here crashed the test host before the fix. Every engine gets the same
/// inputs, so a recursion in another engine's own analysis shows up here too.
/// </summary>
public class DeepStackTests
{
    public static TheoryData<string, string> Inputs()
    {
        var d = new TheoryData<string, string>();
        foreach (var engine in new[] { "tsql", "pg", "mysql", "oracle", "sqlite", "duckdb" })
            foreach (var shape in new[] { "or", "union", "parens", "case" })
                d.Add(engine, shape);
        return d;
    }

    static string Sql(string shape) => shape switch
    {
        "or" => "SELECT a FROM t WHERE " + string.Join(" OR ", Enumerable.Range(0, 20_000).Select(i => $"a = {i}")),
        "union" => string.Join(" UNION ALL ", Enumerable.Range(0, 20_000).Select(i => $"SELECT {i} AS x")),
        "parens" => "SELECT " + new string('(', 2_000) + "1" + new string(')', 2_000) + " AS x FROM t",
        _ => "SELECT " + string.Concat(Enumerable.Repeat("CASE WHEN a = 1 THEN ", 2_000)) + "1" + string.Concat(Enumerable.Repeat(" END", 2_000)) + " AS x FROM t",
    };

    static IDialectService Service(string engine) => engine switch
    {
        "tsql" => new TSqlLanguageService(), "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(),
        "oracle" => new OracleLanguageService(), "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "tsql" => Dialect.Tsql, "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    static readonly SchemaSnapshot Empty = new() { ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh, Objects = [], Fks = [] };

    [Theory, MemberData(nameof(Inputs))]
    public void Deep_and_long_sql_is_analysed_without_ending_the_process(string engine, string shape)
    {
        var svc = Service(engine);
        var sql = Sql(shape);
        var doc = $"deep-{engine}-{shape}";
        Assert.NotNull(svc.Classify(sql));
        Assert.NotNull(svc.Diagnostics(new DiagnosticsParams { DocId = doc, Text = sql, Dialect = DialectOf(engine) }));
        Assert.NotNull(svc.Folding(new FoldingParams { DocId = doc, Text = sql, Dialect = DialectOf(engine) }));
        Assert.NotNull(svc.Format(new FormatParams { Text = sql, Dialect = DialectOf(engine) }));
        var at = sql.Length;
        Assert.NotNull(svc.Complete(new CompleteParams { DocId = doc, Text = sql + " AND ", Offset = at + 5, Dialect = DialectOf(engine), ProfileId = "p", Database = "db", Schema = engine == "oracle" ? "APP" : null },
            new FakeSchema(Empty, "db"), CancellationToken.None));
        Assert.NotNull(svc.Signature(new SignatureParams { DocId = doc, Text = sql + " AND coalesce(", Offset = at + 14, Dialect = DialectOf(engine), ProfileId = "p", Database = "db" },
            new FakeSchema(Empty, "db"), CancellationToken.None));
        svc.Forget(doc);
    }

    [Fact]
    public void Tsql_right_at_both_limits_is_analysed_without_ending_the_process()
    {
        // Review 2026-11-26: the other cases stop at 20,000 terms and 2,000 levels, far below the limits text is parsed
        // up to. A chain just under MaxChainOperators inside parentheses just under MaxNesting: every walk over it
        // (the classifier, diagnostics, folding, completion's containment walk) runs on the deep stack.
        var depth = TSqlParsing.MaxNesting - 10;
        var terms = TSqlParsing.MaxChainOperators - depth - 10;
        var sql = "SELECT a FROM t WHERE " + new string('(', depth) + string.Join(" OR ", Enumerable.Repeat("a = 1", terms)) + new string(')', depth);
        var svc = new TSqlLanguageService();
        Assert.Empty(svc.Classify(sql).ParseErrors);
        Assert.Empty(svc.Diagnostics(new DiagnosticsParams { DocId = "limits", Text = sql, Dialect = Dialect.Tsql }));
        Assert.NotNull(svc.Folding(new FoldingParams { DocId = "limits", Text = sql, Dialect = Dialect.Tsql }));
        Assert.StartsWith("Cannot format:", svc.Format(new FormatParams { Text = sql, Dialect = Dialect.Tsql }).Error); // Formatting.MaxChainOperators
        Assert.NotNull(svc.Complete(new CompleteParams { DocId = "limits", Text = sql + " AND ", Offset = sql.Length + 5, Dialect = Dialect.Tsql, ProfileId = "p", Database = "db" },
            new FakeSchema(Empty, "db"), CancellationToken.None));
        svc.Forget("limits");
    }

    [Fact]
    public void A_long_tsql_chain_still_classifies_its_statements()
    {
        // 20,000 ORs is far below what the deep stack holds: parsed, and the DELETE without WHERE is still found.
        var sql = Sql("or") + ";\nDELETE FROM dbo.Orders;";
        var c = new TSqlLanguageService().Classify(sql);
        Assert.Empty(c.ParseErrors);
        Assert.Contains(c.Destructive, f => f.Rule == "deleteWithoutWhere" || f.Statement.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tsql_nested_past_the_limit_is_not_parsed_and_counts_as_unchecked()
    {
        var n = TSqlParsing.MaxNesting + 1;
        var sql = "SELECT " + new string('(', n) + "1" + new string(')', n) + ";\nDROP TABLE dbo.Orders;";
        var c = new TSqlLanguageService().Classify(sql);
        // Not parsed: one parse error saying why, so QueryGuard asks for the destructive confirmation on production ("unparsed").
        var e = Assert.Single(c.ParseErrors);
        Assert.Contains("too deep", e.Message);
        Assert.Empty(c.Destructive);
    }

    [Fact]
    public void Tsql_with_more_chain_operators_than_the_limit_is_not_parsed()
    {
        var tokens = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true)
            .GetTokenStream(new StringReader("SELECT 1 WHERE " + string.Join(" OR ", Enumerable.Range(0, TSqlParsing.MaxChainOperators + 2).Select(_ => "1=1"))), out _);
        Assert.Contains("too many", TSqlParsing.TooDeep(tokens));
        Assert.Null(TSqlParsing.TooDeep(new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true).GetTokenStream(new StringReader(Sql("or")), out _)));
    }

    static IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> Lex(string s) =>
        new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true).GetTokenStream(new StringReader(s), out _);

    // Review 2026-11-25 (Gemini, B#2): shapes ScriptDom recurses on that the limits did not count. Before, each of these
    // three was parsed (TooDeep returned null), so a deeper copy of it could still end the process.

    [Fact]
    public void Nested_begin_blocks_and_runs_of_not_count_as_nesting()
    {
        var n = TSqlParsing.MaxNesting + 1;
        Assert.Contains("too deep", TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat("BEGIN ", n)) + "SELECT 1 " + string.Concat(Enumerable.Repeat("END ", n)))));
        Assert.Contains("too deep", TSqlParsing.TooDeep(Lex("SELECT 1 WHERE " + string.Concat(Enumerable.Repeat("NOT ", n)) + "1 = 1")));
        // CASE and BEGIN share END: a CASE closed inside a block leaves the block open.
        Assert.Contains("too deep", TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat("BEGIN SELECT CASE WHEN 1=1 THEN 1 END; ", n)) + string.Concat(Enumerable.Repeat("END ", n)))));
    }

    [Fact]
    public void Nested_ifs_count_four_chain_terms_each()
    {
        // 250,000 nested IFs parse on the deep stack; one IF costs about four chain terms of stack.
        Assert.Contains("too many", TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat("IF 1=1 ", 187_501)) + "SELECT 1")));
        Assert.Null(TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat("IF 1=1 ", 1_000)) + "SELECT 1")));
    }

    [Fact]
    public void Statements_that_are_not_blocks_do_not_count_as_nesting()
    {
        // BEGIN TRAN / TRANSACTION / DISTRIBUTED TRAN / DIALOG open no block; END CONVERSATION closes none.
        Assert.Null(TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat(
            "BEGIN TRAN; COMMIT; BEGIN TRANSACTION t; ROLLBACK; BEGIN DISTRIBUTED TRAN; COMMIT; BEGIN DIALOG @h FROM SERVICE a TO SERVICE 'b'; ", 4))), maxNesting: 2, maxChain: 1_000));
        // END CONVERSATION inside a block: the block stays open, so three BEGINs are three levels.
        Assert.Contains("too deep", TSqlParsing.TooDeep(Lex("BEGIN END CONVERSATION @h; BEGIN BEGIN SELECT 1 END END END"), maxNesting: 2, maxChain: 1_000));
    }

    [Fact]
    public void A_script_of_many_short_statements_is_not_refused_for_its_total_size()
    {
        // B#6: 75,001 statements of ten operators each were refused as one 750,010-operator chain (and shown as "syntax
        // errors" on production). A chain ends at its statement's ';'.
        Assert.Null(TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Repeat("SELECT 1+1+1+1+1+1+1+1+1+1+1;", 75_001)))));
        // … but not inside parentheses, where ';' ends nothing, and IFs add up over the batch until GO.
        Assert.Contains("too many", TSqlParsing.TooDeep(Lex("SELECT (1+1+1;1+1+1+1)"), maxNesting: 100, maxChain: 4)); // 5 terms, none reset
        Assert.Contains("too many", TSqlParsing.TooDeep(Lex("IF 1=1 SELECT 1; IF 1=1 SELECT 1;"), maxNesting: 100, maxChain: 7));
        Assert.Null(TSqlParsing.TooDeep(Lex("IF 1=1 SELECT 1;\nGO\nIF 1=1 SELECT 1;"), maxNesting: 100, maxChain: 7));
    }

    [Fact]
    public void A_thread_the_system_refuses_fails_the_call_instead_of_running_on_a_small_stack()
    {
        // B#4: Linux with strict overcommit can refuse the 256 MiB stack. The call fails like any error; running inline on
        // the caller's small stack could overflow it on deep text, which no catch survives.
        var ran = false;
        var ex = Assert.Throws<InvalidOperationException>(() => DeepStack.Run(() => { ran = true; return 1; }, _ => throw new OutOfMemoryException()));
        Assert.False(ran);
        Assert.IsType<OutOfMemoryException>(ex.InnerException);
        Assert.Equal(2, DeepStack.Run(() => 2)); // and the next call starts a thread as before
    }

    [Fact]
    public void Lexer_errors_are_reported_as_before_the_token_parse()
    {
        // ParseText lexes first, then parses the tokens: an unclosed string still reports the lexer's message, as Parse(TextReader) did.
        var parsed = TSqlParsing.ParseUncached("SELECT 'abc");
        Assert.Contains(parsed.Errors, e => e.Message.Contains("closing quotation mark", StringComparison.Ordinal));
    }

    [Fact]
    public void Sql_server_plan_refusal_and_data_compare_survive_a_long_chain()
    {
        var sql = Sql("union");
        Assert.Null(new SqlServerPlans().Refusal(sql, PlanMode.Estimated));
        Assert.Null(new SqlServerDataCompare().QueryRefusal(sql)); // one SELECT statement (a UNION): accepted, parsed without crashing
        Assert.NotNull(new SqlServerDataCompare().DerivedQuery(sql).Body);
    }

    [Fact]
    public void Reference_finder_and_result_sources_survive_a_long_chain()
    {
        var sql = Sql("or");
        var finder = new TSqlReferenceFinder();
        var ctx = new RefContext { DefaultSchema = "dbo", Columns = (_, _) => ["a"], SchemasOf = _ => ["dbo"] };
        var scan = finder.Find(sql, new RefTarget(RefKind.Table, "dbo", "t"), ctx);
        Assert.NotEmpty(scan.Hits);
        finder.TargetAt(sql, 16, ctx);
        finder.LocalOccurrences(sql, 16);
        Assert.NotNull(ResultSources.Trace(sql, Empty, 0));
    }

    [Fact]
    public void Deep_work_runs_on_a_deep_stack_and_nests_inline()
    {
        Assert.False(DeepStack.OnDeepStack);
        Assert.True(DeepStack.Run(() => DeepStack.OnDeepStack && DeepStack.Run(() => DeepStack.OnDeepStack)));
        var ex = Assert.Throws<InvalidOperationException>(() => DeepStack.Run<int>(() => throw new InvalidOperationException("kept")));
        Assert.Equal("kept", ex.Message);
    }
}
