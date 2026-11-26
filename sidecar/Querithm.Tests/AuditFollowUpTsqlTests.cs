using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Intelligence.TSql;
using Querithm.Intelligence.TSql.Completion;
using Querithm.Providers.SqlServer;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Code audit follow-up 2026-11-26, SQL Server: nested comments that overflowed the lexer's stack (the process ended),
/// the formatter's quadratic chains, the operator count that refused long scripts written without ';', completion that
/// depended on how busy the machine was, compound assignments, CROSS JOIN repairs, and SHOWPLAN left on by a cancel.
/// </summary>
public class AuditFollowUpTsqlTests
{
    static string Nested(int depth) => "SELECT 1 " + string.Concat(Enumerable.Repeat("/*", depth)) + string.Concat(Enumerable.Repeat("*/", depth)) + "\nSELECT 2";

    // ------------------------------------------------------------------ nested comments (crashed the test host before)

    [Theory]
    [InlineData("SELECT 1", 0)]
    [InlineData("/* a */ SELECT 1", 1)]
    [InlineData("/* a /* b /* c */ */ */", 3)]
    [InlineData("/* a */ /* b /* c */ */", 2)]
    [InlineData("SELECT '/* not a comment' /* one */", 1)]
    [InlineData("SELECT [a /* name */] /* one */", 1)]
    [InlineData("SELECT \"a /* name \"\" */\" /* one */", 1)]
    [InlineData("SELECT 'it''s /*' /* one */", 1)]
    [InlineData("-- /* /* line comment\nSELECT 1 /* one */", 1)]
    [InlineData("SELECT 'unclosed /* /* /*", 0)]
    [InlineData("--x\r/* a /* b */ */", 2)] // ScriptDom ends a line comment at a lone CR (second review 2026-11-26)
    [InlineData("/* unclosed /* /*", 3)]
    public void Comment_nesting_is_counted_the_way_the_lexer_reads_it(string sql, int depth) => Assert.Equal(depth, TSqlParsing.CommentNesting(sql));

    [Fact]
    public async Task Five_thousand_nested_comments_parse_on_a_pool_thread()
    {
        // 5,000 nested comments overflowed a 1 MB stack in ScriptDom's lexer (measured 2026-11-26) and ended the process.
        var text = Nested(5_000);
        var parsed = await Task.Run(() => TSqlParsing.ParseUncached(text));
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, parsed.Script!.Batches[0].Statements.Count);
        var redacted = await Task.Run(() => new TSqlLanguageService().RedactSecrets(text));
        Assert.Equal(text, redacted.Text);
    }

    [Fact]
    public async Task Comments_nested_past_the_limit_are_refused_not_lexed()
    {
        var text = Nested(TSqlParsing.MaxNesting + 1);
        var parsed = await Task.Run(() => TSqlParsing.ParseUncached(text));
        Assert.Contains("nests comments", Assert.Single(parsed.Errors).Message);
        // It still runs as one batch (the server reads its comments), and the classifier calls it unchecked.
        Assert.Single(parsed.Batches);
        var classification = await Task.Run(() => new TSqlLanguageService().Classify(text));
        Assert.NotEmpty(classification.ParseErrors);
        // History keeps nothing it could not check for passwords.
        var redacted = await Task.Run(() => new TSqlLanguageService().RedactSecrets("ALTER LOGIN x WITH PASSWORD = 'hunter2' " + text));
        Assert.DoesNotContain("hunter2", redacted.Text);
        // Server-returned module text too deep to lex comes back unchanged.
        var definition = "CREATE PROCEDURE dbo.p AS " + text;
        Assert.Equal(definition, await Task.Run(() => new SqlServerMetadata().ToEditableScript(definition, Caps)));
    }

    static readonly Capabilities Caps = new() { CreateOrAlter = true, CompletionTier = "", Trace = new(), Debugger = new(), EstimatedPlan = new(), ActualPlan = new() };

    // ------------------------------------------------------------------ format

    [Fact]
    public void Format_refuses_a_chain_the_generator_would_take_minutes_over()
    {
        var chain = "SELECT a FROM t WHERE " + string.Join(" OR ", Enumerable.Range(0, Formatting.MaxChainOperators + 10).Select(i => $"a = {i}")) + ";";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new TSqlLanguageService().Format(new FormatParams { Text = chain });
        Assert.StartsWith("Cannot format:", r.Error);
        Assert.Contains("to format", r.Error);
        Assert.Equal(chain, r.Text);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"{sw.Elapsed.TotalSeconds:F1} s"); // 80,000 terms took 9 s in the generator alone
        Assert.Null(new TSqlLanguageService().Format(new FormatParams { Text = "select a from t where a = 1 or a = 2;" }).Error);
    }

    [Fact]
    public void Format_still_formats_a_batch_of_many_short_ifs()
    {
        // Second review 2026-11-26: format counted each IF as four chain terms, so a 6,000-line seed script was refused.
        var seed = string.Concat(Enumerable.Range(0, 6_000).Select(i => $"IF NOT EXISTS (SELECT 1 FROM t WHERE id = {i}) INSERT INTO t VALUES ({i});\n"));
        Assert.Null(new TSqlLanguageService().Format(new FormatParams { Text = seed }).Error);
    }

    [Fact]
    public void A_triggers_update_tests_stay_one_chain()
    {
        static IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> Lex(string s) => TSqlParsing.Lex(TSqlParsing.NewParser(), s, out _)!;
        var cond = "IF " + string.Join(" OR ", Enumerable.Range(0, 20).Select(i => $"UPDATE(c{i})")) + " SELECT 1";
        Assert.NotNull(TSqlParsing.TooDeep(Lex(cond), 100, 10)); // each UPDATE( reset the count before
        Assert.Null(TSqlParsing.TooDeep(Lex(string.Concat(Enumerable.Range(0, 20).Select(i => $"UPDATE t SET a = -{i}\n"))), 100, 10));
    }

    [Fact]
    public void Schema_compare_gives_text_too_deep_to_lex_back_unchanged()
    {
        // Second review 2026-11-26: with no tokens every rewrite rebuilt it as "", so the module was dropped and created empty.
        var d = new SqlServerSchemaCompare();
        var body = "CREATE VIEW s1.v AS SELECT 1 AS x " + string.Concat(Enumerable.Repeat("/*", TSqlParsing.MaxNesting + 1)) + string.Concat(Enumerable.Repeat("*/", TSqlParsing.MaxNesting + 1));
        Assert.Equal(body, d.RenameSchema(body, "s1", "s2"));
        Assert.Equal(body, d.RenameIdentifier(body, "x", "y"));
        Assert.Equal(body, d.WithoutHeader(body));
        Assert.NotEqual(d.NormalizeBody(body), d.NormalizeBody(body.Replace("1 AS x", "2 AS x")));
    }

    // ------------------------------------------------------------------ operator count

    [Fact]
    public void The_operator_count_restarts_at_each_statement_verb_but_not_across_a_union()
    {
        static IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> Lex(string s) => TSqlParsing.Lex(TSqlParsing.NewParser(), s, out _)!;
        // 20 INSERTs without ';', one operator each: refused under a limit of 10 before (one count for the whole batch).
        var inserts = string.Concat(Enumerable.Range(0, 20).Select(i => $"INSERT INTO t VALUES (-{i})\n"));
        Assert.Null(TSqlParsing.TooDeep(Lex(inserts), 100, 10));
        var sets = string.Concat(Enumerable.Range(0, 20).Select(i => $"SET @a = @a + {i}\nEXEC dbo.p {i} * 2\n"));
        Assert.Null(TSqlParsing.TooDeep(Lex(sets), 100, 10));
        // A UNION chain is one tree: it still counts as one.
        var union = string.Join(" UNION ALL ", Enumerable.Range(0, 20).Select(i => $"SELECT {i}"));
        Assert.NotNull(TSqlParsing.TooDeep(Lex(union), 100, 10));
        // So does a long condition after a verb.
        Assert.NotNull(TSqlParsing.TooDeep(Lex("DELETE FROM t WHERE " + string.Join(" OR ", Enumerable.Range(0, 20).Select(i => $"a = {i}"))), 100, 10));
    }

    // ------------------------------------------------------------------ completion

    static readonly FakeSchema Sales = new(FakeSchema.SalesDb(), "SalesDB");

    static string[] Labels(string sql)
    {
        var at = sql.IndexOf('|');
        return TSqlCompletion.Complete(new CompleteParams
        {
            DocId = "followup-" + Guid.NewGuid(), Text = sql.Remove(at, 1), Offset = at, Dialect = Dialect.Tsql, ProfileId = "p1", Database = "SalesDB",
        }, Sales, CancellationToken.None).Select(i => i.Label).ToArray();
    }

    [Fact]
    public void The_token_fallback_finds_the_from_item_an_update_alias_stands_for()
    {
        // A statement too long for the repair ladder (MaxAstText) answers from tokens: `UPDATE o` was taken as an unknown
        // table "o" with no columns, and the FROM item that defines o was then skipped as a duplicate.
        var padding = "/*" + new string('x', StatementAnalysis.MaxAstText) + "*/";
        var items = Labels($"UPDATE o SET {padding} Status = 1, | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
        Assert.True(items.Contains("TotalAmount") || items.Contains("o.TotalAmount"), string.Join(", ", items.Take(40)));
    }

    [Theory]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("&=")]
    public void A_compound_assignment_completes_its_value_not_another_target(string op)
    {
        var items = Labels($"UPDATE o SET TotalAmount {op} | FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
        Assert.Contains("c.FullName", items);
    }

    [Fact]
    public void A_cross_join_after_an_open_case_keeps_its_sources()
    {
        var items = Labels("SELECT o.OrderId, CASE WHEN o.| FROM dbo.Orders o CROSS JOIN dbo.Customers c WHERE");
        Assert.Contains("TotalAmount", items);
    }

    // ------------------------------------------------------------------ round 3 (2026-11-26)

    [Theory]
    [InlineData("DECLARE @h int; EXEC sp_prepexec @h OUTPUT, NULL, N'DROP TABLE dbo.Orders';", "drop")]
    [InlineData("DECLARE @h int; EXEC sp_prepare @h OUTPUT, NULL, N'DELETE FROM dbo.Orders';", "deleteWithoutWhere")]
    [InlineData("DECLARE @c int; EXEC sp_cursoropen @c OUTPUT, N'TRUNCATE TABLE dbo.Orders';", "truncate")]
    [InlineData("EXEC sp_execute 1;", "dynamicSql")]
    [InlineData("EXEC xp_cmdshell 'sqlcmd -Q \"DROP TABLE dbo.Orders\"';", "dynamicSql")]
    [InlineData("EXEC sp_execute_external_script @language = N'Python', @script = N'x';", "dynamicSql")]
    [InlineData("SELECT * FROM OPENQUERY(srv, 'DELETE FROM t');", "dynamicSql")]
    [InlineData("RESTORE DATABASE Sales FROM DISK = 'x.bak' WITH REPLACE;", "restore")]
    public void Procedures_and_statements_that_run_or_replace_unseen_text_are_findings(string sql, string rule)
    {
        // Each gave no finding before: on production only the first-run confirm was asked, and the text ran.
        var c = new TSqlLanguageService().Classify(sql);
        Assert.Contains(c.Destructive, f => f.Rule == rule);
    }

    [Fact]
    public void Still_a_harmless_prepared_statement_and_a_plain_restore_verify_stay_quiet()
    {
        Assert.Empty(new TSqlLanguageService().Classify("DECLARE @h int; EXEC sp_prepexec @h OUTPUT, NULL, N'SELECT 1';").Destructive);
        Assert.Empty(new TSqlLanguageService().Classify("RESTORE VERIFYONLY FROM DISK = 'x.bak';").Destructive);
    }

    [Theory]
    [InlineData("EXEC sp_addlogin bob, Summer2026", "Summer2026")]
    [InlineData("EXEC sp_addlogin 'bob', [Tr0ub4dor&3]", "Tr0ub4dor&3")]
    [InlineData("EXEC sp_password NULL, \"N3w!Pass\", 'sa'", "N3w!Pass")]
    [InlineData("EXEC sp_password @new = Hunter2Pass, @loginame = 'sa'", "Hunter2Pass")]
    [InlineData("SET QUOTED_IDENTIFIER OFF; CREATE LOGIN x WITH PASSWORD = \"S3cret!\"", "S3cret!")]
    public void A_secret_written_as_a_name_is_masked_in_history(string sql, string secret)
    {
        var r = new TSqlLanguageService().RedactSecrets(sql);
        Assert.DoesNotContain(secret, r.Text);
    }

    [Fact]
    public void Still_the_login_name_and_the_next_statement_are_kept()
    {
        var r = new TSqlLanguageService().RedactSecrets("EXEC sp_addlogin bob, Summer2026\nSELECT a, b, c, d, e FROM t");
        Assert.Contains("bob", r.Text);
        Assert.Contains("SELECT a, b, c, d, e FROM t", r.Text);
    }

    [Fact]
    public void Format_refuses_statements_nested_too_deep_for_its_output()
    {
        var n = Formatting.MaxStatementDepth + 1;
        var nested = string.Concat(Enumerable.Repeat("IF 1 = 1 ", n)) + "SELECT 1;";
        var r = new TSqlLanguageService().Format(new FormatParams { Text = nested });
        Assert.Contains("nests statements", r.Error);
        Assert.Null(new TSqlLanguageService().Format(new FormatParams { Text = string.Concat(Enumerable.Repeat("IF 1 = 1 ", 50)) + "SELECT 1;" }).Error);
    }

    [Fact]
    public void Apply_and_pivot_chains_count_like_joins()
    {
        static IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> Lex(string s) => TSqlParsing.Lex(TSqlParsing.NewParser(), s, out _)!;
        var apply = "SELECT 1 FROM t " + string.Concat(Enumerable.Range(0, 20).Select(i => $"CROSS APPLY dbo.f() a{i} "));
        Assert.NotNull(TSqlParsing.TooDeep(Lex(apply), 100, 10));
        var outer = "SELECT 1 FROM t " + string.Concat(Enumerable.Range(0, 20).Select(i => $"OUTER APPLY dbo.f() a{i} "));
        Assert.NotNull(TSqlParsing.TooDeep(Lex(outer), 100, 10));
    }

    // ------------------------------------------------------------------ Gemini review 2026-11-26

    [Fact]
    public void Sp_executesql_classifies_its_first_argument_not_a_parameter_named_stmt()
    {
        var c = new TSqlLanguageService().Classify("EXEC sp_executesql N'DROP TABLE dbo.Orders', N'@stmt nvarchar(20)', @stmt = N'SELECT 1';");
        Assert.Contains(c.Destructive, f => f.Rule == "drop");
        // The named form still works, and a name given twice is unknown text.
        Assert.Contains(new TSqlLanguageService().Classify("EXEC sp_executesql @stmt = N'DROP TABLE dbo.Orders';").Destructive, f => f.Rule == "drop");
        Assert.Contains(new TSqlLanguageService().Classify("EXEC sp_prepexec @handle = @h OUTPUT, @params = NULL, @stmt = N'SELECT 1', @stmt = N'DROP TABLE t';").Destructive, f => f.Rule == "dynamicSql");
    }

    [Theory]
    [InlineData("EXEC sp_addlogin @loginame = 'bob', @passwd = Summer2026", "Summer2026")]
    [InlineData("EXEC sp_addlogin @loginame = 'bob', @passwd = [Tr0ub4dor&3]", "Tr0ub4dor&3")]
    [InlineData("EXEC sp_addlinkedsrvlogin 'srv', 'false', NULL, 'u', @rmtpassword = Hunter2Pass", "Hunter2Pass")]
    [InlineData("SET QUOTED_IDENTIFIER OFF\nDECLARE @password nvarchar(100) = \"S3cret!\"", "S3cret!")]
    [InlineData("SET QUOTED_IDENTIFIER OFF\nSELECT ENCRYPTBYPASSPHRASE(\"S3cret!\", 'x')", "S3cret!")]
    public void More_secrets_written_as_names_are_masked(string sql, string secret)
    {
        var r = new TSqlLanguageService().RedactSecrets(sql);
        Assert.DoesNotContain(secret, r.Text);
        Assert.Contains(secret, r.Secrets); // an error quoting it is masked too
    }

    [Fact]
    public void The_plan_refuses_text_too_deep_to_lex()
    {
        var n = TSqlParsing.MaxNesting + 1;
        var sql = string.Concat(Enumerable.Repeat("/*", n)) + string.Concat(Enumerable.Repeat("*/", n)) + " SET SHOWPLAN_XML OFF; DROP TABLE t;";
        Assert.NotNull(new SqlServerPlans().Refusal(sql, PlanMode.Estimated));
    }

    [Fact]
    public void Format_bounds_nested_subqueries_and_its_indent()
    {
        var n = Formatting.MaxStatementDepth + 1;
        var deep = "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", n)) + "1" + new string(')', n) + ";";
        Assert.StartsWith("Cannot format:", new TSqlLanguageService().Format(new FormatParams { Text = deep }).Error);
        Assert.Null(new TSqlLanguageService().Format(new FormatParams { Text = "select 1;", Options = new FormatOptions { KeywordCase = KeywordCase.Upper, IndentSize = -1 } }).Error);
        Assert.Null(new TSqlLanguageService().Format(new FormatParams { Text = "select 1;", Options = new FormatOptions { KeywordCase = KeywordCase.Upper, IndentSize = 10_000_000 } }).Error);
    }

    [Fact]
    public void A_foreign_key_action_after_alter_table_stays_one_statement()
    {
        foreach (var action in new[] { "CASCADE", "NO ACTION", "RESTRICT", "SET NULL" })
        {
            var doc = DocModel.Get($"ALTER TABLE dbo.x ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES dbo.Orders (OrderId) ON DELETE {action} ON UPDATE {action}", "fk-" + Guid.NewGuid());
            Assert.Single(doc.Segs, s => s.Kind == SegKind.Statement);
        }
    }

    [Fact]
    public void A_run_of_prefix_plus_and_minus_counts_as_nesting()
    {
        static IList<Microsoft.SqlServer.TransactSql.ScriptDom.TSqlParserToken> Lex(string s) => TSqlParsing.Lex(TSqlParsing.NewParser(), s, out _)!;
        Assert.Contains("too deep", TSqlParsing.TooDeep(Lex("SELECT " + string.Concat(Enumerable.Repeat("+ - ", 6)) + "1"), 10, 1_000));
        Assert.Null(TSqlParsing.TooDeep(Lex("SELECT " + string.Join(" - ", Enumerable.Range(0, 50)) + ";"), 10, 1_000)); // binary minus: no nesting
    }

    // ------------------------------------------------------------------ deferred documentation

    [Fact]
    public void A_documentation_builder_that_fails_leaves_only_its_item_undocumented()
    {
        var bad = DeferredDocs.Defer(new CompletionItem { Label = "bad", Kind = CompletionKind.Table }, () => throw new InvalidOperationException("boom"));
        var good = DeferredDocs.Defer(new CompletionItem { Label = "good", Kind = CompletionKind.Table }, () => "doc");
        var items = CompletionFilter.Apply([bad, good], "", 0);
        Assert.Null(items.Single(i => i.Label == "bad").Documentation);
        Assert.Equal("doc", items.Single(i => i.Label == "good").Documentation);
    }

    // ------------------------------------------------------------------ SHOWPLAN cleanup

    [Fact]
    public async Task A_cancel_that_lands_after_showplan_went_on_still_turns_it_off()
    {
        var applied = false;
        var conn = new FakeDbConnection((cmd, _) =>
        {
            if (cmd.CommandText == "SET SHOWPLAN_XML ON")
            {
                applied = true; // the server applied it; the client gave up waiting for the answer
                throw new OperationCanceledException();
            }
            return Task.FromResult(FakeResult.None);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqlServerPlans().GetPlanAsync(conn, "SELECT 1", PlanMode.Estimated, CancellationToken.None));
        Assert.True(applied);
        Assert.Contains("SET SHOWPLAN_XML OFF", conn.Log);
    }

    [Fact]
    public async Task The_plans_own_error_is_reported_when_turning_showplan_off_also_fails()
    {
        var conn = new FakeDbConnection((cmd, _) => cmd.CommandText switch
        {
            "SET SHOWPLAN_XML ON" => Task.FromResult(FakeResult.None),
            "SET SHOWPLAN_XML OFF" => throw new InvalidOperationException("off failed"),
            _ => throw new InvalidOperationException("the plan failed"),
        });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new SqlServerPlans().GetPlanAsync(conn, "SELECT 1", PlanMode.Estimated, CancellationToken.None));
        Assert.Equal("the plan failed", e.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State); // never handed on with SHOWPLAN on
    }
}
