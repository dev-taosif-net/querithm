using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Querithm.Tests.FileEngines;

namespace Querithm.Tests.Security;

/// <summary>
/// Regression tests for PT7-1 (the classifier must split identifiers as each engine does: ASCII whitespace only, every
/// character from U+0080 up an identifier character, DuckDB's Unicode-space mapping mirrored), the WHERE safety net, PT7-4
/// (a NUL in the text is refused, never a hang) and PT2-1 (temp_store cannot be set). See PentestProdGuardTests for the PoCs.
/// </summary>
public sealed class LexicalParityTests
{
    static readonly ExecuteOptions ProdOnly = new() { ConfirmedProd = true };

    static IDialectService Lang(string engine) => engine switch
    {
        "sqlite" => new SqliteLanguageService(),
        "duckdb" => new DuckDbLanguageService(),
        "pg" => new PgLanguageService(),
        "mysql" => new MySqlLanguageService(),
        _ => throw new ArgumentException(engine),
    };

    public static TheoryData<string, string> GluedAliases()
    {
        var data = new TheoryData<string, string>();
        // Characters every engine reads as part of a name (DuckDB maps none of these to a space).
        foreach (var engine in new[] { "sqlite", "duckdb", "pg", "mysql" })
            foreach (var c in new[] { "\u20AC", "\u00D7", "\u0085", "\u1680", "\u2028", "\u200C", "\uD83D\uDE00" })
                data.Add(engine, c);
        // Unicode spaces: identifier characters to SQLite, PostgreSQL and MySQL (DuckDB maps them, see below).
        foreach (var engine in new[] { "sqlite", "pg", "mysql" })
            foreach (var c in new[] { "\u00A0", "\u2000", "\u2009", "\u200B", "\u202F", "\u3000", "\uFEFF" })
                data.Add(engine, c);
        return data;
    }

    [Theory]
    [MemberData(nameof(GluedAliases))]
    public void A_non_ascii_character_glues_the_alias_to_where(string engine, string c)
    {
        var lang = Lang(engine);
        Assert.Contains(lang.Classify($"DELETE FROM t AS a{c}WHERE").Destructive, f => f.Rule == "deleteWithoutWhere");
        Assert.Contains(lang.Classify($"UPDATE t AS u{c}WHERE SET v = 0").Destructive, f => f.Rule == "updateWithoutWhere");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    [InlineData("pg")]
    [InlineData("mysql")]
    public void Ascii_whitespace_still_separates_and_a_real_where_still_counts(string engine)
    {
        var lang = Lang(engine);
        Assert.Empty(lang.Classify("DELETE\tFROM\nt\r\nWHERE\fid = 1").Destructive);
        Assert.Empty(lang.Classify("UPDATE t SET v = 0 WHERE id = 1").Destructive);
        Assert.Empty(lang.Classify("DELETE FROM t WHERE (id) = 1;").Destructive);
        Assert.Empty(lang.Classify("UPDATE caf\u00E9 SET v = 0 WHERE \u00E9t\u00E9 = 1").Destructive);
    }

    /// <summary>The safety net: a WHERE with nothing after it is not a condition, whatever came before it.</summary>
    [Theory]
    [InlineData("sqlite", "DELETE FROM t WHERE")]
    [InlineData("sqlite", "DELETE FROM t WHERE;")]
    [InlineData("sqlite", "UPDATE t SET v = 0 WHERE -- c\n")]
    [InlineData("duckdb", "DELETE FROM t WHERE")]
    [InlineData("duckdb", "UPDATE t SET v = 0 WHERE /* c */;")]
    [InlineData("pg", "DELETE FROM t WHERE")]
    [InlineData("pg", "UPDATE t SET v = 0 WHERE;")]
    [InlineData("mysql", "DELETE FROM t WHERE")]
    [InlineData("mysql", "UPDATE t SET v = 0 WHERE # c")]
    public void A_trailing_where_counts_as_no_where(string engine, string sql) =>
        Assert.NotEmpty(Lang(engine).Classify(sql).Destructive);

    /// <summary>T-SQL is classified by ScriptDom, not by these tokenizers: the same texts are destructive or unparsed there.</summary>
    [Theory]
    [InlineData("DELETE FROM t AS a\u00A0WHERE")]
    [InlineData("DELETE FROM t AS a\u3000WHERE")]
    [InlineData("DELETE FROM t AS a\u20ACWHERE")]
    [InlineData("DELETE t FROM t AS a\u00A0WHERE")]
    [InlineData("UPDATE t SET v = 0 FROM t AS u\u00A0WHERE")]
    [InlineData("DELETE FROM t WHERE")]
    public void Tsql_classifier_is_not_fooled_by_unicode_characters(string sql)
    {
        var c = new TSqlLanguageService().Classify(sql);
        Assert.True(c.Destructive.Length > 0 || c.ParseErrors.Length > 0, "a T-SQL text without a real WHERE must be destructive or unparsed (the guard then blocks it)");
    }

    // ---------------------------------------------------------------- DuckDB's Unicode-space mapping (a port of StripUnicodeSpaces)

    [Theory]
    [InlineData("a\u00A0b", "a b")]
    [InlineData("a\u3000b\u2060c\uFEFFd\u200Be", "a b c d e")]
    [InlineData("'x\u00A0' a\u00A0b", "'x\u00A0' a b")]           // inside quotes: kept
    [InlineData("\"x\u00A0\" a\u00A0b", "\"x\u00A0\" a b")]
    [InlineData("-- x\u00A0\na\u00A0b", "-- x\u00A0\na b")]
    [InlineData("$t$\u00A0$t$ a\u00A0b", "$t$\u00A0$t$ a b")]
    [InlineData("/* ' */ a\u00A0b", "/* ' */ a\u00A0b")]          // it does not know block comments: the quote stops the mapping
    [InlineData("$$$$ a\u00A0b", "$$$$ a\u00A0b")]                // its dollar-quote reading of $$$$ never closes
    [InlineData("a\u00A0", "a\u00A0")]                              // the last two bytes are never examined
    [InlineData("a\u0085b\u2028c", "a\u0085b\u2028c")]             // not in its list
    public void DuckDb_maps_unicode_spaces_as_the_engine_does(string text, string expected) =>
        Assert.Equal(expected, DuckDbTokenizer.MapUnicodeSpaces(text));

    [Fact]
    public void DuckDb_tokens_keep_the_text_as_written()
    {
        var t = DuckDbTokenizer.Tokenize("SELECT\u00A0$x\u00A0;").Where(x => !x.IsTrivia).ToList();
        // DuckDB's mapper reads "$x" + the space as the start of a $tag$, so that space is not mapped: one parameter, as DuckDB reads it.
        Assert.Equal(["SELECT", "$x" + (char)0xA0, ";"], t.Select(x => x.Text));
        Assert.Equal("\u00A0", DuckDbTokenizer.Tokenize("a\u00A0b")[1].Text);
    }

    [Theory]
    [InlineData("DELETE\u00A0FROM t")]                                   // mapped: a DELETE to DuckDB
    [InlineData("DROP\u3000TABLE t")]
    [InlineData("DELETE /* ' */ FROM t AS a\u00A0WHERE RETURNING id")]   // not mapped after the quote: an alias
    [InlineData("SELECT 1 /* ' */; DELETE /* ' */ FROM t AS a\u00A0WHERE RETURNING id")] // the batch alone is what runs
    public void DuckDb_classifier_reads_unicode_spaces_as_duckdb_does(string sql) =>
        Assert.NotEmpty(new DuckDbLanguageService().Classify(sql).Destructive);

    [Fact]
    public void DuckDb_a_mapped_space_before_where_is_still_a_where() =>
        Assert.Empty(new DuckDbLanguageService().Classify("DELETE FROM t\u00A0WHERE\u00A0id = 1").Destructive);

    static async Task<(DuckDbHost H, string Sid)> DuckProd(EnvironmentKind env = EnvironmentKind.Prod)
    {
        var h = new DuckDbHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v VARCHAR); INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, env);
        return (h, sid);
    }

    static long Count(IList<Apache.Arrow.RecordBatch> b) => b[0].Column(0) switch
    {
        Apache.Arrow.Int64Array i => i.GetValue(0)!.Value,
        Apache.Arrow.StringArray t => long.Parse(t.GetString(0)!, System.Globalization.CultureInfo.InvariantCulture),
        var a => throw new InvalidOperationException(a.GetType().Name),
    };

    static async Task<long> DuckCount(DuckDbHost h, string sid) =>
        Count((await h.RunAsync(sid, "SELECT count(*)::BIGINT FROM t", ProdOnly)).Batches);

    /// <summary>Real DuckDB: the texts the classifier calls destructive really delete every row, and are blocked on Prod.</summary>
    [Theory]
    [InlineData("DELETE\u00A0FROM t")]
    [InlineData("DELETE /* ' */ FROM t AS a\u00A0WHERE RETURNING id")]
    public async Task DuckDb_engine_and_classifier_agree(string sql)
    {
        var (h, sid) = await DuckProd();
        await using var _h = h;
        var r = await h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql, Options = ProdOnly });
        Assert.Equal(GuardReason.Destructive, r.Blocked?.Reason);
        Assert.Equal(3, await DuckCount(h, sid));
        var (done, _, _) = await h.RunAsync(sid, sql, ProdOnly with { ConfirmedDestructive = true });
        Assert.Null(done.Error?.Message);
        Assert.Equal(0, await DuckCount(h, sid));
    }

    [Fact]
    public async Task DuckDb_a_mapped_space_runs_without_a_false_destructive_confirm()
    {
        var (h, sid) = await DuckProd();
        await using var _h = h;
        var (done, _, _) = await h.RunAsync(sid, "DELETE FROM t\u00A0WHERE\u00A0id = 1", ProdOnly);
        Assert.Null(done.Error?.Message);
        Assert.Equal(2, await DuckCount(h, sid));
    }

    // ---------------------------------------------------------------- PT7-4: NUL

    [Theory]
    [InlineData(EnvironmentKind.Dev)]
    [InlineData(EnvironmentKind.Prod)]
    public async Task DuckDb_nul_is_refused_and_nothing_runs(EnvironmentKind env)
    {
        var (h, sid) = await DuckProd(env);
        await using var _h = h;
        var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.QueryExecute(new QueryExecuteParams
        {
            SessionId = sid, Sql = "SELECT 1;\nDELETE FROM t\0 WHERE 0", Options = new ExecuteOptions { ConfirmedProd = true, ConfirmedDestructive = true },
        }));
        Assert.Equal("refused", ex.Code);
        Assert.Contains("line 2", ex.Message);
        Assert.Equal(3, await DuckCount(h, sid));
    }

    [Fact]
    public async Task Sqlite_nul_is_refused_at_once_on_a_dev_profile_too()
    {
        await using var h = new SqliteHost();
        var path = h.NewDatabase(setup: "CREATE TABLE t (id INTEGER PRIMARY KEY, v); INSERT INTO t(v) VALUES (1),(2),(3);");
        var (_, sid) = await h.OpenFileAsync(path, FileAccessMode.ReadWrite, EnvironmentKind.Dev);
        foreach (var sql in new[] { "SELECT 1\0 x", "SELECT '\0'", "DELETE FROM t\0 WHERE 0" })
        {
            var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() =>
                h.Rpc.QueryExecute(new QueryExecuteParams { SessionId = sid, Sql = sql }).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("refused", ex.Code);
        }
        // The session still works.
        var (done, batches, _) = await h.RunAsync(sid, "SELECT count(*) FROM t");
        Assert.Null(done.Error);
        Assert.Equal(3, Count(batches));
    }

    [Fact]
    public void The_nul_refusal_names_the_line() =>
        Assert.Contains("line 3", new SqliteLanguageService().Classify("SELECT 1;\n\nSELECT 2\0").Refusal);

    // ---------------------------------------------------------------- SQLite file policy

    [Theory]
    [InlineData("PRAGMA temp_store = FILE")]
    [InlineData("PRAGMA temp.temp_store = 1")]
    [InlineData("PRAGMA temp_store(0)")]
    [InlineData("SELECT \"load_extension\"('evil')")]
    [InlineData("SELECT [load_extension]('evil')")]
    [InlineData("SELECT `LOAD_EXTENSION`('evil', 'init')")]
    public void Sqlite_policy_refusals(string sql) => Assert.NotNull(new SqliteLanguageService().Classify(sql).Refusal);

    [Theory]
    [InlineData("PRAGMA temp_store")]
    [InlineData("SELECT \"load_extension\" FROM t")]
    public void Sqlite_reads_stay_allowed(string sql) => Assert.Null(new SqliteLanguageService().Classify(sql).Refusal);

    [Fact]
    public void Sqlite_the_classifier_and_the_authorizer_share_one_pragma_list() =>
        Assert.Contains("temp_store", SqlitePragmaPolicy.RefusedToSet);
}
