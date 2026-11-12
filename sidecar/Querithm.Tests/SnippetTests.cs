using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Store;

namespace Querithm.Tests;

/// <summary>The snippet library (Phase A3): CRUD in store.db, sidecar-side validation, the read-only built-in set.</summary>
public class SnippetTests
{
    readonly LocalStore _db = LocalStore.InMemory();
    SnippetStore Store => new(_db);

    static Snippet New(string prefix = "mysel", string body = "SELECT ${1:*} FROM ${2:t};", Dialect? dialect = Dialect.Pg, string title = "My select", string[]? tags = null) =>
        new() { Id = "", Title = title, Prefix = prefix, Body = body, Dialect = dialect, Tags = tags ?? ["query"] };

    static QuerithmException Refused(Action a) => Assert.Throws<QuerithmException>(a);

    static string? Field(QuerithmException e) => (e.DataPayload as IDictionary<string, string>)?["field"];

    [Fact]
    public void Save_list_update_and_delete_round_trip()
    {
        var saved = Store.Save(New(tags: [" query ", "QUERY", "", "report"]));
        Assert.Equal(26, saved.Id.Length);
        Assert.True(saved.UpdatedAt > 0);
        Assert.Equal(["query", "report"], saved.Tags); // trimmed, de-duplicated ignoring case, blanks dropped
        var listed = Store.List().Single(s => !s.BuiltIn);
        Assert.Equal(saved.Tags, listed.Tags);
        Assert.Equal(saved with { Tags = listed.Tags }, listed);

        var updated = Store.Save(saved with { Title = "  Renamed ", Body = "SELECT 2;", Dialect = null });
        Assert.Equal((saved.Id, "Renamed", (Dialect?)null), (updated.Id, updated.Title, updated.Dialect));
        Assert.Equal("SELECT 2;", Store.List().Single(s => s.Id == saved.Id).Body);

        Store.Delete(saved.Id);
        Assert.DoesNotContain(Store.List(), s => !s.BuiltIn);
        Assert.Equal("notFound", Refused(() => Store.Delete(saved.Id)).Code);
        Assert.Equal("notFound", Refused(() => Store.Save(saved)).Code); // updating a deleted snippet does not resurrect it
    }

    [Fact]
    public void Built_ins_come_first_cover_both_dialects_and_are_read_only()
    {
        var all = Store.List();
        Assert.All(all.TakeWhile(s => s.BuiltIn), s => Assert.StartsWith(BuiltInSnippets.IdPrefix, s.Id));
        Assert.Contains(all, s => s.BuiltIn && s.Dialect == Dialect.Tsql);
        Assert.Contains(all, s => s.BuiltIn && s.Dialect == Dialect.Pg);
        Assert.Contains(all, s => s.BuiltIn && s.Dialect == Dialect.Oracle);
        var b = all.First(s => s.BuiltIn);
        Assert.Equal("snippetReadOnly", Refused(() => Store.Save(b with { Title = "mine" })).Code);
        Assert.Equal("snippetReadOnly", Refused(() => Store.Delete(b.Id)).Code);
        // Every built-in passes the same validation as a user snippet, and built-in prefixes are unique per dialect.
        Assert.All(BuiltInSnippets.All, s => SnippetStore.Validate(s));
        Assert.Equal(BuiltInSnippets.All.Length, BuiltInSnippets.All.Select(s => (s.Dialect, s.Prefix.ToLowerInvariant())).Distinct().Count());
    }

    [Fact]
    public void A_duplicate_prefix_is_refused_ignoring_case_with_the_field_named()
    {
        Store.Save(New("dup"));
        var e = Refused(() => Store.Save(New("DUP", dialect: Dialect.Pg)));
        Assert.Equal(("snippetPrefixTaken", "prefix"), (e.Code, Field(e)));
        Assert.Contains("My select", e.Message);
        // A built-in of an overlapping dialect owns its prefix; another dialect's built-in does not.
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(New("upsert", dialect: Dialect.Pg))).Code);
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(New("upsert", dialect: null))).Code);
        Assert.Equal("upsert", Store.Save(New("upsert", dialect: Dialect.Tsql)).Prefix);
        // Saving a snippet again under its own prefix is not a clash.
        var own = Store.List().Single(s => s.Prefix == "dup");
        Store.Save(own with { Body = "SELECT 3;" });
    }

    [Fact]
    public void User_prefixes_are_unique_per_dialect_and_a_dialect_free_snippet_overlaps_both()
    {
        Store.Save(New("mytop", dialect: Dialect.Tsql));
        Assert.Equal(Dialect.Pg, Store.Save(New("MyTop", dialect: Dialect.Pg)).Dialect);
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(New("mytop", dialect: null))).Code);
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(New("mytop", dialect: Dialect.Pg))).Code);
        // A dialect-free snippet blocks the prefix for every dialect.
        Store.Save(New("anyd", dialect: null));
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(New("anyd", dialect: Dialect.Tsql))).Code);
        // Moving a snippet onto a dialect that already uses its prefix is a clash too.
        var pg = Store.List().Single(s => s.Prefix == "MyTop");
        Assert.Equal("snippetPrefixTaken", Refused(() => Store.Save(pg with { Dialect = Dialect.Tsql })).Code);
    }

    public static TheoryData<string, Func<Snippet, Snippet>> Invalid => new()
    {
        { "title", s => s with { Title = "   " } },
        { "title", s => s with { Title = new string('t', SnippetLimits.Title + 1) } },
        { "title", s => s with { Title = "two\nlines" } },
        { "prefix", s => s with { Prefix = "" } },
        { "prefix", s => s with { Prefix = new string('p', SnippetLimits.Prefix + 1) } },
        { "prefix", s => s with { Prefix = "has space" } },
        { "prefix", s => s with { Prefix = "semi;colon" } },
        { "prefix", s => s with { Prefix = "ümlaut" } },
        { "body", s => s with { Body = "  \n " } },
        { "body", s => s with { Body = new string('x', SnippetLimits.BodyBytes + 1) } },
        // 2-byte characters: under the limit in chars, over it in UTF-8 bytes.
        { "body", s => s with { Body = new string('é', SnippetLimits.BodyBytes / 2 + 1) } },
        { "body", s => s with { Body = "SELECT 1;\0" } },
        { "tags", s => s with { Tags = Enumerable.Range(0, SnippetLimits.Tags + 1).Select(i => $"t{i}").ToArray() } },
        { "tags", s => s with { Tags = [new string('g', SnippetLimits.Tag + 1)] } },
        { "dialect", s => s with { Dialect = (Dialect)42 } },
    };

    [Theory, MemberData(nameof(Invalid))]
    public void Invalid_snippets_are_refused_by_the_sidecar_and_nothing_is_stored(string field, Func<Snippet, Snippet> spoil)
    {
        var e = Refused(() => Store.Save(spoil(New())));
        Assert.Equal(("invalid", field), (e.Code, Field(e)));
        Assert.DoesNotContain(Store.List(), s => !s.BuiltIn);
    }

    [Fact]
    public void Limits_at_the_boundary_are_accepted()
    {
        var s = Store.Save(New(new string('p', SnippetLimits.Prefix), new string('x', SnippetLimits.BodyBytes), title: new string('t', SnippetLimits.Title),
            tags: Enumerable.Range(0, SnippetLimits.Tags).Select(i => new string('g', SnippetLimits.Tag - 2) + i.ToString("00")).ToArray()));
        Assert.Equal(SnippetLimits.BodyBytes, s.Body.Length);
    }

    [Fact]
    public void The_library_stops_at_its_snippet_limit()
    {
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO snippet (id, title, prefix, body) VALUES ($id, 't', $p, 'b')";
            var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var p = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
            for (var i = 0; i < SnippetLimits.Count; i++)
            {
                id.Value = $"id{i}";
                p.Value = $"p{i}";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Assert.Equal("snippetLimit", Refused(() => Store.Save(New("one_more"))).Code);
        // Editing an existing snippet is still allowed at the limit.
        Store.Save(Store.List().First(s => s.Id == "id0") with { Body = "SELECT 1;" });
    }

    [Fact]
    public async Task Rpc_methods_list_save_and_delete_and_reject_missing_params()
    {
        await using var h = new TestHost();
        var before = await h.Rpc.SnippetList();
        Assert.All(before, s => Assert.True(s.BuiltIn));
        var saved = await h.Rpc.SnippetSave(new SnippetSaveParams { Snippet = New("rpc1") });
        Assert.Contains(await h.Rpc.SnippetList(), s => s.Id == saved.Id && !s.BuiltIn);
        await h.Rpc.SnippetDelete(new SnippetRef { Id = saved.Id });
        Assert.Equal(before.Length, (await h.Rpc.SnippetList()).Length);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => { _ = h.Rpc.SnippetSave(new SnippetSaveParams { Snippet = null! }); }).Code);
        Assert.Equal("invalid", Assert.Throws<QuerithmException>(() => { _ = h.Rpc.SnippetDelete(new SnippetRef { Id = "" }); }).Code);
    }
    /// <summary>A snippet body as Monaco inserts it when every placeholder keeps its default (test helper, not SQL parsing).</summary>
    public static string Expanded(string body, IReadOnlyDictionary<string, string>? values = null) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(body, @"\$\{\d+:([^}]*)\}", m => values?.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value),
            @"\$\d+", "").Replace(@"\$", "$");

    /// <summary>D4 S12: each Oracle built-in splits into the statements it shows, PL/SQL units whole with their END;.</summary>
    [Theory]
    [InlineData("fetchf", "Sql")]
    [InlineData("plblock", "PlSql")]
    [InlineData("forcur", "PlSql")]
    [InlineData("pkg", "PlSql,PlSql")]
    [InlineData("mergeinto", "Sql")]
    public void Oracle_built_ins_split_into_the_statements_they_show(string prefix, string kinds)
    {
        var snippet = BuiltInSnippets.All.Single(b => b.Dialect == Dialect.Oracle && b.Prefix == prefix);
        var text = Expanded(snippet.Body);
        var statements = Querithm.Intelligence.Oracle.OracleSplitter.Split(text);
        Assert.Equal(kinds, string.Join(",", statements.Select(st => st.Kind)));
        foreach (var st in statements.Where(st => st.Kind == Querithm.Intelligence.Oracle.OracleStatementKind.PlSql))
            Assert.EndsWith(";", text[st.Start..st.End].TrimEnd());
        Assert.DoesNotContain("${", text);
    }
}
