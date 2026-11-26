using System.Diagnostics;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;
using Querithm.Intelligence.TSql;
using Querithm.Intelligence.TSql.Completion;
using Querithm.Tests.Fakes;

namespace Querithm.Tests;

/// <summary>
/// Completion audit 2026-11-25, H1–H3: tier 2's work stays bounded on every engine. Chained CTEs and nested derived
/// tables resolve each level once (before: 6 CTEs of 10 columns took 1.5 s on PostgreSQL's engine, growing about 6× per
/// level); a cancelled request stops; the RPC layer cancels a document's older request and ends one at its deadline; the
/// statement at the cursor is never kept in the T-SQL parse cache.
/// </summary>
public class CompletionBudgetTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<string> Engines => new() { "tsql", "pg", "mysql", "oracle", "sqlite", "duckdb" };

    static readonly SchemaSnapshot Empty = new() { ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh, Objects = [], Fks = [] };

    static IDialectService Service(string engine) => engine switch
    {
        "tsql" => new TSqlLanguageService(),
        "pg" => new PgLanguageService(),
        "mysql" => new MySqlLanguageService(),
        "oracle" => new OracleLanguageService(),
        "sqlite" => new SqliteLanguageService(),
        _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "tsql" => Dialect.Tsql, "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    internal static CompletionItem[] Complete(string engine, string marked, CancellationToken ct)
    {
        var at = marked.IndexOf('|');
        return Service(engine).Complete(new CompleteParams
        {
            DocId = "budget-" + Guid.NewGuid(), Text = marked.Remove(at, 1), Offset = at, Dialect = DialectOf(engine), ProfileId = "p", Database = "db",
            Schema = engine == "oracle" ? "APP" : null,
        }, new FakeSchema(Empty, "db"), ct);
    }

    static string Columns(int n, string? from = null) => string.Join(", ", Enumerable.Range(0, n).Select(i => from == null ? $"1 AS a{i}" : $"a{i}")) + (from != null ? " FROM " + from : "");

    /// <summary>WITH c1 AS (SELECT 1 AS a0, …), c2 AS (SELECT a0, … FROM c1), … SELECT | FROM ck.</summary>
    internal static string CteChain(string engine, int levels, int cols)
    {
        var dual = engine == "oracle" ? " FROM dual" : "";
        var sb = new System.Text.StringBuilder($"WITH c1 AS (SELECT {Columns(cols)}{dual})");
        for (var k = 2; k <= levels; k++) sb.Append($", c{k} AS (SELECT {Columns(cols, $"c{k - 1}")})");
        return sb.Append($" SELECT | FROM c{levels}").ToString();
    }

    /// <summary>SELECT | FROM (SELECT a0, … FROM (SELECT … FROM (SELECT 1 AS a0, …) d3) d2) d1.</summary>
    static string DerivedChain(string engine, int levels, int cols)
    {
        var dual = engine == "oracle" ? " FROM dual" : "";
        var inner = $"(SELECT {Columns(cols)}{dual}) d{levels}";
        for (var k = levels - 1; k >= 1; k--) inner = $"(SELECT {Columns(cols, inner)}) d{k}";
        return $"SELECT | FROM {inner}";
    }

    static void ResolvesWithinBudget(string engine, string marked)
    {
        Complete(engine, marked, CancellationToken.None); // JIT
        var sw = Stopwatch.StartNew();
        var items = Complete(engine, marked, CancellationToken.None);
        sw.Stop();
        var labels = items.Where(i => i.Kind == CompletionKind.Column).Select(i => i.Label).ToList();
        Assert.True(labels.Any(l => l.EndsWith("a9", StringComparison.OrdinalIgnoreCase)), $"{engine}: no a9 in [{string.Join(", ", labels.Take(30))}]");
        // Linear work is about a millisecond here; the exponential path took 1.5 s at this size.
        Assert.True(sw.ElapsedMilliseconds < 250, $"{engine}: {sw.ElapsedMilliseconds} ms");
    }

    [Theory, MemberData(nameof(Engines))]
    public void Chained_ctes_resolve_each_level_once(string engine) => ResolvesWithinBudget(engine, CteChain(engine, 6, 10));

    [Theory, MemberData(nameof(Engines))]
    public void Nested_derived_tables_resolve_each_level_once(string engine) => ResolvesWithinBudget(engine, DerivedChain(engine, 6, 10));

    [Theory, MemberData(nameof(Engines))]
    public void A_cancelled_request_throws_instead_of_answering(string engine)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Complete(engine, CteChain(engine, 3, 4), cts.Token));
        var text = "SELECT coalesce(1, ";
        Assert.ThrowsAny<OperationCanceledException>(() => Service(engine).Signature(new SignatureParams
        {
            DocId = "budget-sig", Text = text, Offset = text.Length, Dialect = DialectOf(engine), ProfileId = "p", Database = "db",
        }, new FakeSchema(Empty, "db"), cts.Token));
    }

    [Fact]
    public void The_tsql_statement_at_the_cursor_is_never_kept_in_the_parse_cache()
    {
        var sql = "SELECT o.OrderId, o.CustomerId FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE o.";
        for (var i = 0; i < 40; i++) Complete("tsql", sql + "Status = " + i + " AND o.|", CancellationToken.None);
        Assert.DoesNotContain(StatementParser.CachedTexts, t => t.Contains(StatementAnalysis.Placeholder, StringComparison.Ordinal));
        Assert.True(StatementParser.CachedChars <= StatementParser.MaxCachedChars);
    }

    static string LongStatement(int terms, string tail) =>
        "SELECT o.OrderId FROM dbo.Orders o WHERE " + string.Join(" OR ", Enumerable.Range(0, terms).Select(i => $"o.Id = {i}")) + " AND o.|" + tail;

    /// <summary>Completion time and one ScriptDom parse of the statement, measured together so a loaded test run scales both.</summary>
    static (double CompleteMs, double ParseMs) TimeAgainstOneParse(string marked)
    {
        Complete("tsql", marked, CancellationToken.None); // JIT
        var one = Stopwatch.StartNew();
        TSqlParsing.ParseText(TSqlParsing.NewParser(), marked.Replace("|", "x"), out _);
        var parseMs = one.Elapsed.TotalMilliseconds;
        var sw = Stopwatch.StartNew();
        Complete("tsql", marked, CancellationToken.None);
        return (sw.Elapsed.TotalMilliseconds, parseMs);
    }

    [Fact]
    public void The_tsql_ladder_never_parses_a_statement_past_its_size_cap()
    {
        // ~290,000 characters, past MaxAstText: no parse per keystroke at all (lexing and the token fallback only).
        var marked = LongStatement(20_000, "");
        Assert.True(marked.Length > StatementAnalysis.MaxAstText);
        var (ms, parseMs) = TimeAgainstOneParse(marked);
        Assert.True(ms < parseMs, $"{ms:F0} ms; one parse {parseMs:F0} ms");
    }

    // ------------------------------------------------------------------------------------------- RPC: newest wins

    /// <summary>Waits for its token on "slow" text (the work a newer keystroke makes useless), answers "fast" at once.</summary>
    internal sealed class BlockingLanguage : IDialectService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        T Work<T>(string text, CancellationToken ct, T answer)
        {
            if (text != "slow") return answer;
            Started.TrySetResult();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("never cancelled");
        }

        public CompletionItem[] Complete(CompleteParams p, ISchemaSource schema, CancellationToken ct) =>
            Work(p.Text, ct, new[] { new CompletionItem { Label = "fast", Kind = CompletionKind.Keyword, SortText = "fast" } });

        public SignatureHelp Signature(SignatureParams p, ISchemaSource schema, CancellationToken ct) => Work(p.Text, ct, new SignatureHelp { Signatures = [] });
        public Classification Classify(string text) => throw new NotSupportedException();
        public Diagnostic[] Diagnostics(DiagnosticsParams p) => [];
        public FormatResult Format(FormatParams p) => throw new NotSupportedException();
        public FoldingRange[] Folding(FoldingParams p) => [];
        public Redaction RedactSecrets(string text) => throw new NotSupportedException();
        public void Forget(string docId) { }
    }

    internal static (LanguageService Service, BlockingLanguage Language) Rpc(TimeSpan? deadline = null)
    {
        var language = new BlockingLanguage();
        var providers = new ProviderRegistry([new FakeDbProvider().As(Engine.Mssql, language)]);
        return (new LanguageService(providers, new FakeSchema(Empty, "db"), deadline), language);
    }

    internal static CompleteParams Req(string doc, string text) => new() { DocId = doc, Text = text, Offset = 0, Dialect = Dialect.Tsql };

    [Fact]
    public async Task A_newer_completion_for_the_document_cancels_the_older_one()
    {
        var (svc, language) = Rpc();
        var older = svc.CompleteAsync(Req("doc", "slow"));
        await language.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = await svc.CompleteAsync(Req("doc", "fast")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(newer, i => i.Label == "fast");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_newer_signature_request_cancels_the_older_one()
    {
        var (svc, language) = Rpc();
        var older = svc.SignatureAsync(new SignatureParams { DocId = "doc", Text = "slow", Offset = 0, Dialect = Dialect.Tsql });
        await language.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.SignatureAsync(new SignatureParams { DocId = "doc", Text = "fast", Offset = 0, Dialect = Dialect.Tsql }).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Another_documents_request_leaves_a_running_one_alone_until_its_deadline()
    {
        var (svc, language) = Rpc(TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();
        var first = svc.CompleteAsync(Req("doc-a", "slow"));
        await language.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await svc.CompleteAsync(Req("doc-b", "fast")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted, "a request for another document cancelled this one");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(sw.ElapsedMilliseconds >= 350, $"ended after {sw.ElapsedMilliseconds} ms, before its deadline");
    }

    [Fact]
    public async Task Lang_signature_validates_its_input_like_lang_complete()
    {
        var (svc, _) = Rpc();
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.SignatureAsync(new SignatureParams { DocId = "", Text = "x", Dialect = Dialect.Tsql }));
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.SignatureAsync(new SignatureParams { DocId = "d", Text = "x", Offset = 5, Dialect = Dialect.Tsql }));
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.SignatureAsync(new SignatureParams { DocId = new string('d', 600), Text = "x", Dialect = Dialect.Tsql }));
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.CompleteAsync(Req("d", "x") with { Database = new string('x', 2000) }));
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.DiagnosticsAsync(new DiagnosticsParams { DocId = new string('d', 600), Text = "x", Dialect = Dialect.Tsql }));
        await Assert.ThrowsAsync<Core.QuerithmException>(() => svc.FoldingAsync(new FoldingParams { DocId = "", Text = "x", Dialect = Dialect.Tsql }));
    }
}

/// <summary>
/// The wall-clock gates of <see cref="CompletionBudgetTests"/>, run alone (<see cref="TimingCollection"/>): in the full
/// parallel suite the T-SQL p95 of the chained CTEs read 51.5 ms against 12 ms run alone (2026-11-25, this machine).
/// </summary>
[Collection("timing")]
public class CompletionBudgetTimingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static CompletionItem[] Complete(string engine, string marked, CancellationToken ct) => CompletionBudgetTests.Complete(engine, marked, ct);
    static string CteChain(string engine, int levels, int cols) => CompletionBudgetTests.CteChain(engine, levels, cols);
    static (LanguageService Service, CompletionBudgetTests.BlockingLanguage Language) Rpc() => CompletionBudgetTests.Rpc();
    static CompleteParams Req(string doc, string text) => CompletionBudgetTests.Req(doc, text);

    /// <summary>§14 #5c: tier 2 through 8 chained CTEs of 30 columns, in process, every engine (20 runs, p95).</summary>
    [Fact]
    [Trait("Category", "Measure")]
    public void Chained_ctes_eight_by_thirty_per_engine()
    {
        foreach (var engine in new[] { "tsql", "pg", "mysql", "oracle", "sqlite", "duckdb" })
        {
            var sql = CteChain(engine, 8, 30);
            Complete(engine, sql, CancellationToken.None); // JIT
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var sw = Stopwatch.StartNew();
                Complete(engine, sql, CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            output.WriteLine($"{engine}: 8 CTEs x 30 columns, median {times[10]:F2} ms, p95 {times[19]:F2} ms");
            Assert.True(times[19] < 50, $"{engine}: p95 {times[19]:F1} ms");
        }
    }

    /// <summary>
    /// Phase 0 exit criterion (audit 2026-11-25): a superseded request stops within 50 ms of the newer one arriving — its
    /// answer ends as cancelled and its engine work sees the token. Median of 10, so one slow scheduling on a busy machine
    /// does not decide it.
    /// </summary>
    [Fact]
    public async Task A_superseded_request_stops_within_50_ms()
    {
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var (svc, language) = Rpc();
            var older = svc.CompleteAsync(Req("doc", "slow"));
            await language.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var sw = Stopwatch.StartNew();
            var newer = svc.CompleteAsync(Req("doc", "fast"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older.WaitAsync(TimeSpan.FromSeconds(10)));
            times.Add(sw.Elapsed.TotalMilliseconds);
            await newer.WaitAsync(TimeSpan.FromSeconds(10));
        }
        times.Sort();
        output.WriteLine($"superseded request stopped after median {times[5]:F2} ms, max {times[^1]:F2} ms");
        Assert.True(times[5] <= 50, $"median {times[5]:F1} ms");
    }
}
