using System.Diagnostics;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.PgSql.Completion;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Tests;

/// <summary>
/// Code audit 2026-11-25 (M6): a document read cold slower than the tier-2 deadline was never cached — every request read
/// it from the start and was cancelled at its deadline (a 16 MB document needs 2.1–8.8 s, the deadline is 3 s). Here
/// each request has a 100 ms deadline on a document whose cold read takes longer: before, none ever answered; now the
/// document is read once in the background and a later request answers from it.
/// </summary>
[Collection("timing")]
public class LargeDocumentBuildTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static SnapColumn C(string name) => new() { Name = name, Type = "integer" };

    static SchemaSnapshot Shop(string engine)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var schema = engine switch { "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects = [new SnapObject { Id = 1, Schema = schema, Name = N("orders"), Kind = ObjectKind.Table, Columns = [C(N("id")), C(N("status"))] }],
        };
    }

    static IDialectService Service(string engine) => engine switch
    {
        "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(), "oracle" => new OracleLanguageService(),
        "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    [Theory]
    [InlineData("pg")]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void A_document_read_slower_than_the_deadline_is_read_once_and_then_answers(string engine)
    {
        var sb = new StringBuilder(BackgroundBuild.MinChars + 200);
        for (var i = 0; sb.Length < BackgroundBuild.MinChars + 100; i++)
            sb.Append("SELECT o.id, o.status FROM orders o WHERE o.id > ").Append(i).Append(";\n");
        sb.Append("SELECT o.");
        var offset = sb.Length;
        var text = sb.Append(" FROM orders o").ToString();
        var request = new CompleteParams
        {
            DocId = "large-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
            Schema = engine == "oracle" ? "APP" : null,
        };
        var svc = Service(engine);
        var schema = new FakeSchema(Shop(engine), "shop");
        var sw = Stopwatch.StartNew();
        CompletionItem[]? items = null;
        var attempts = 0;
        while (items == null && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            attempts++;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { items = svc.Complete(request, schema, deadline.Token); }
            catch (OperationCanceledException) { Thread.Sleep(50); }
        }
        output.WriteLine($"{engine}: answered after {sw.ElapsedMilliseconds} ms, {attempts} request(s)");
        Assert.NotNull(items);
        Assert.Contains(items, i => i.Label.Equals("status", StringComparison.OrdinalIgnoreCase));
        svc.Forget(request.DocId);
        Assert.False(DocumentCaches.Holds(request.DocId));
    }

    /// <summary>
    /// Second round: the read checked that it was still current, then stored; a forget between the two left the forgotten
    /// document cached. The store is undone now when the read is no longer current after it.
    /// </summary>
    [Fact]
    public void A_forget_that_lands_during_the_store_undoes_it()
    {
        var docId = "race-" + Guid.NewGuid();
        var stored = 0;
        var undone = 0;
        var value = BackgroundBuild.Run("doc:test|" + docId, "text", () => "value",
            store: _ => { stored++; BackgroundBuild.Forget(docId); }, // the forget runs right after the store
            unstore: _ => undone++);
        Assert.Equal("value", value);
        Assert.Equal(1, stored);
        Assert.Equal(1, undone);
        // Without a forget nothing is undone.
        BackgroundBuild.Run("doc:test|" + docId + "b", "text", () => "value", store: _ => stored++, unstore: _ => undone++);
        Assert.Equal(1, undone);
    }

    /// <summary>
    /// Second round: Oracle's schema-switch scan and its document were read in the background on two pool threads, and
    /// each lexed the whole text. The scan's lex is shared with the document build now.
    /// </summary>
    [Fact]
    public void A_long_oracle_document_is_lexed_once()
    {
        var sb = new StringBuilder("ALTER SESSION SET CURRENT_SCHEMA = APP;\n");
        for (var i = 0; sb.Length < BackgroundBuild.MinChars + 100; i++) sb.Append("SELECT o.id FROM orders o WHERE o.id > ").Append(i).Append(";\n");
        sb.Append("SELECT o.");
        var offset = sb.Length;
        var text = sb.Append(" FROM orders o").ToString();
        var request = new CompleteParams { DocId = "lex-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = Dialect.Oracle, ProfileId = "p", Database = "shop", Schema = "APP" };
        var before = OracleCompletion.LargeLexes;
        var items = new OracleLanguageService().Complete(request, new FakeSchema(Shop("oracle"), "shop"), CancellationToken.None);
        Assert.Contains(items, i => i.Label == "STATUS");
        Assert.Equal(1, OracleCompletion.LargeLexes - before);
        new OracleLanguageService().Forget(request.DocId);
    }

    /// <summary>lang.forget ends a background read: it neither runs on nor stores the document after the forget.</summary>
    [Fact]
    public void Forget_ends_a_background_read()
    {
        var text = string.Concat(Enumerable.Repeat("SELECT o.id FROM orders o WHERE o.id > 1;\n", BackgroundBuild.MinChars / 40 + 10)) + "SELECT o.";
        var svc = new DuckDbLanguageService();
        var docId = "large-forget-" + Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try { svc.Complete(new CompleteParams { DocId = docId, Text = text, Offset = text.Length, Dialect = Dialect.DuckDb, ProfileId = "p", Database = "shop" }, new FakeSchema(Shop("duckdb"), "shop"), deadline.Token); }
        catch (OperationCanceledException) { /* expected: the read goes on in the background */ }
        svc.Forget(docId);
        var sw = Stopwatch.StartNew();
        while (BackgroundBuild.Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(20);
        Assert.False(DocumentCaches.Holds(docId));
    }
}
