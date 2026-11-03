using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Intelligence.PgSql.Completion;
using Querithm.Intelligence.TSql;
using Querithm.Providers.DuckDb;
using Querithm.Providers.MySql;
using Querithm.Providers.Postgres;
using Querithm.Providers.Sqlite;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>M12: per-document language caches are bounded, and lang.forget drops a document from every engine's.</summary>
public class DocumentCacheTests
{
    [Fact]
    public void Lru_drops_the_least_recently_used_entry_first()
    {
        var c = new LruCache<string, int>(3);
        c.Set("a", 1);
        c.Set("b", 2);
        c.Set("c", 3);
        Assert.True(c.TryGet("a", out _)); // a is now the most recent
        c.Set("d", 4);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out var a));
        Assert.Equal(1, a);
        Assert.Equal(3, c.Count);
        c.Set("a", 9); // replacing keeps one entry
        Assert.Equal(3, c.Count);
        Assert.True(c.Remove("a"));
        Assert.False(c.Remove("a"));
        Assert.Equal(1, c.RemoveWhere((k, _) => k.EndsWith('c')));
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void TSql_parse_cache_never_holds_more_than_its_bound()
    {
        // Every tab or notebook cell ever opened used to keep its full AST (M12).
        var run = Guid.NewGuid().ToString("N");
        for (var i = 0; i < TSqlParsing.MaxDocuments + 40; i++)
        {
            TSqlParsing.Parse($"SELECT {i} AS n -- {run}", $"{run}-{i}");
            Assert.True(TSqlParsing.CachedDocuments <= TSqlParsing.MaxDocuments, $"{TSqlParsing.CachedDocuments} documents cached");
            Assert.True(TSqlParsing.CachedTexts <= TSqlParsing.MaxTexts, $"{TSqlParsing.CachedTexts} texts cached");
        }
    }

    [Fact]
    public void TSql_dynamic_sql_literals_are_classified_without_taking_a_cache_slot()
    {
        Assert.True(TSqlParsing.MaxTexts >= TSqlParsing.MaxDocuments);
        var run = Guid.NewGuid().ToString("N");
        // A script with many EXEC('…') literals: each is parsed and classified, none is cached.
        var literals = Enumerable.Range(0, TSqlParsing.MaxTexts + 4).Select(i => $"DELETE FROM t{i}_{run}").ToList();
        var script = string.Join("\n", literals.Select(l => $"EXEC('{l}');"));
        var c = new TSqlLanguageService().Classify(script);
        Assert.Equal(literals.Count, c.Destructive.Count(f => f.Rule == "deleteWithoutWhere"));
        Assert.All(literals, l => Assert.False(TSqlParsing.IsCachedText(l)));
    }

    [Fact]
    public void TSql_forget_drops_the_documents_parse_and_lexed_model()
    {
        var id = "forget-" + Guid.NewGuid().ToString("N");
        var text = $"SELECT 1 AS {id.Replace('-', '_')}";
        var first = TSqlParsing.Parse(text, id);
        Assert.Same(first, TSqlParsing.Parse(text, id));
        Intelligence.TSql.Completion.DocModel.Get(text, id);
        Assert.True(TSqlParsing.IsCached(id) && Intelligence.TSql.Completion.DocModel.IsCached(id));
        new TSqlLanguageService().Forget(id);
        Assert.False(TSqlParsing.IsCached(id));
        Assert.False(Intelligence.TSql.Completion.DocModel.IsCached(id));
    }

    static TestHost AllEngines() => new(new SqlServerProvider(), new PostgresProvider(), new MySqlProvider(), new SqliteProvider(), new DuckDbProvider());

    [Fact]
    public async Task Lang_forget_clears_every_engines_document_caches()
    {
        await using var host = AllEngines();
        var id = "doc-" + Guid.NewGuid().ToString("N");
        // Fill each engine's per-document caches the way the editor does: completion and folding with the tab's docId.
        await host.Rpc.LangComplete(new CompleteParams { DocId = id, Text = "SELECT * FROM t WHERE ", Offset = 22, Dialect = Dialect.Tsql });
        await host.Rpc.LangFolding(new FoldingParams { DocId = id, Text = "SELECT 1\nGO\nSELECT 2", Dialect = Dialect.Tsql });
        Assert.True(Intelligence.TSql.Completion.DocModel.IsCached(id));
        foreach (var d in new[] { Dialect.Pg, Dialect.MySql, Dialect.Sqlite, Dialect.DuckDb })
            await host.Rpc.LangComplete(new CompleteParams { DocId = id, Text = "USE x; SELECT * FROM t WHERE ", Offset = 29, Dialect = d });
        Assert.True(DocumentCaches.Holds(id));

        await host.Rpc.LangForget(new LangForgetParams { DocId = id });

        Assert.False(Intelligence.TSql.Completion.DocModel.IsCached(id));
        Assert.False(DocumentCaches.Holds(id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public async Task Lang_forget_refuses_an_empty_or_oversized_doc_id(int length)
    {
        await using var host = AllEngines();
        await Assert.ThrowsAsync<QuerithmException>(() => host.Rpc.LangForget(new LangForgetParams { DocId = new string('x', length) }));
    }

    [Fact]
    public async Task Lang_forget_of_an_unknown_document_is_harmless()
    {
        await using var host = AllEngines();
        await host.Rpc.LangForget(new LangForgetParams { DocId = "never-seen" });
    }
}
