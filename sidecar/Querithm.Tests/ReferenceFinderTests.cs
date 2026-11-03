using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>Find usages: references found on the T-SQL AST and on PostgreSQL tokens, with their confidence.</summary>
public class ReferenceFinderTests
{
    static RefContext Ctx(string defaultSchema, Dictionary<(string, string), string[]> tables, StringComparer names) => new()
    {
        DefaultSchema = defaultSchema,
        Columns = (s, t) => tables.FirstOrDefault(kv => names.Equals(kv.Key.Item1, s) && names.Equals(kv.Key.Item2, t)).Value,
        SchemasOf = n => tables.Keys.Where(k => names.Equals(k.Item2, n)).Select(k => k.Item1).Distinct().ToArray(),
    };

    static readonly RefContext Ss = Ctx("dbo", new()
    {
        [("dbo", "Orders")] = ["Id", "CustomerId", "OrderDate", "Qty"],
        [("dbo", "Customers")] = ["Id", "Name"],
        [("dbo", "Returns")] = ["Id", "OrderDate"],
        [("sales", "Orders")] = ["Id"],
    }, StringComparer.OrdinalIgnoreCase);

    static readonly RefContext Pg = Ctx("public", new()
    {
        [("public", "orders")] = ["id", "customer_id", "order_date", "qty"],
        [("public", "customers")] = ["id", "name"],
        [("public", "returns")] = ["id", "order_date"],
    }, StringComparer.Ordinal);

    static string[] Texts(string sql, RefScan scan) => scan.Hits.Select(h => sql.Substring(h.Offset, h.Length)).ToArray();

    // ------------------------------------------------------------------ T-SQL

    const string TsqlProc = """
        CREATE PROCEDURE dbo.usp_GetOrders @since date AS
        BEGIN
          -- OrderDate in a comment is not a reference
          SELECT o.OrderDate, c.Name, 'OrderDate' AS label
          FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId
          WHERE o.[OrderDate] > @since;
          SELECT OrderDate FROM Orders;
          SELECT Name FROM dbo.Customers WHERE Id IN (SELECT CustomerId FROM dbo.Orders WHERE OrderDate > @since);
          SELECT r.Id FROM dbo.Returns r JOIN dbo.Orders o2 ON o2.Id = r.Id WHERE OrderDate IS NULL;
          UPDATE dbo.Orders SET OrderDate = @since WHERE Id = 1;
          INSERT INTO dbo.Orders (Id, OrderDate) SELECT Id, OrderDate FROM dbo.Returns;
        END
        """;

    [Fact]
    public void Tsql_columns_resolve_through_aliases_and_scopes()
    {
        var scan = new TSqlReferenceFinder().Find(TsqlProc, new RefTarget(RefKind.Column, "dbo", "Orders", "OrderDate"), Ss);
        Assert.False(scan.ParseFailed);
        var hits = scan.Hits.Select(h => (Text: TsqlProc.Substring(h.Offset, h.Length), h.Confidence, Line: TsqlProc[..h.Offset].Count(c => c == '\n') + 1)).ToList();
        // o.OrderDate, o.[OrderDate], bare in SELECT … FROM Orders, the correlated subquery, UPDATE SET, INSERT column list.
        Assert.Equal(["OrderDate", "[OrderDate]", "OrderDate", "OrderDate", "OrderDate", "OrderDate"], hits.Where(h => h.Confidence == RefConfidence.Exact).Select(h => h.Text));
        // Returns and Orders both have OrderDate in that query: possible, not exact.
        Assert.Single(hits, h => h.Confidence == RefConfidence.Possible && h.Line == 9);
        // INSERT … SELECT OrderDate FROM dbo.Returns is Returns' column, the comment and the string are nothing.
        Assert.DoesNotContain(hits, h => h.Line == 3);
        Assert.Equal(1, hits.Count(h => h.Line == 11));
    }

    [Fact]
    public void Tsql_tables_procedures_and_headers()
    {
        var f = new TSqlReferenceFinder();
        var orders = f.Find(TsqlProc, new RefTarget(RefKind.Table, "dbo", "Orders"), Ss);
        Assert.Equal(6, orders.Hits.Count); // every FROM/JOIN/UPDATE/INSERT target, the unqualified one included
        Assert.All(orders.Hits, h => Assert.True(TsqlProc.Substring(h.Offset, h.Length) == "Orders"));
        Assert.Contains(orders.Hits, h => h.Confidence == RefConfidence.Possible); // unqualified Orders: dbo or sales
        Assert.DoesNotContain(f.Find(TsqlProc, new RefTarget(RefKind.Table, "sales", "Orders"), Ss).Hits, h => h.Confidence == RefConfidence.Exact);

        const string calls = "CREATE PROCEDURE dbo.outer_p AS EXEC dbo.usp_GetOrders @since = '2026-01-01'; EXEC usp_GetOrders '2026'; SELECT dbo.fn_total(1), x.y FROM dbo.fn_rows(1) x;";
        var proc = f.Find(calls, new RefTarget(RefKind.Procedure, "dbo", "usp_GetOrders"), Ss);
        Assert.Equal(2, proc.Hits.Count);
        Assert.Equal(["fn_total"], Texts(calls, f.Find(calls, new RefTarget(RefKind.Function, "dbo", "fn_total"), Ss)));
        Assert.Equal(["fn_rows"], Texts(calls, f.Find(calls, new RefTarget(RefKind.Function, "dbo", "fn_rows"), Ss)));
        var header = f.Find(TsqlProc, new RefTarget(RefKind.Procedure, "dbo", "usp_GetOrders"), Ss);
        Assert.True(Assert.Single(header.Hits).Definition);

        const string qualifiers = "SELECT Orders.Qty, dbo.Orders.Id FROM dbo.Orders WITH (NOLOCK); WITH Orders AS (SELECT 1 AS Qty) SELECT Qty FROM Orders;";
        var q = f.Find(qualifiers, new RefTarget(RefKind.Table, "dbo", "Orders"), Ss);
        Assert.Equal(3, q.Hits.Count); // Orders.Qty and dbo.Orders.Id qualifiers and the FROM; the CTE named Orders is not the table
    }

    [Fact]
    public void Tsql_dynamic_sql_is_flagged()
    {
        var f = new TSqlReferenceFinder();
        var t = new RefTarget(RefKind.Table, "dbo", "Orders");
        Assert.True(f.Find("CREATE PROC p AS DECLARE @s nvarchar(max) = N'SELECT * FROM dbo.Orders'; EXEC (@s);", t, Ss).DynamicSql);
        Assert.True(f.Find("CREATE PROC p AS EXEC sp_executesql N'SELECT 1';", t, Ss).DynamicSql);
        Assert.False(f.Find(TsqlProc, t, Ss).DynamicSql);
    }

    [Fact]
    public void Tsql_target_at_the_cursor()
    {
        var f = new TSqlReferenceFinder();
        int At(string word, int nth = 0) { var i = -1; for (var k = 0; k <= nth; k++) i = TsqlProc.IndexOf(word, i + 1, StringComparison.Ordinal); return i + 1; }
        Assert.Equal(new RefTarget(RefKind.Column, "dbo", "Orders", "OrderDate"), f.TargetAt(TsqlProc, At("o.OrderDate") + 3, Ss));
        Assert.Equal(new RefTarget(RefKind.Table, "dbo", "Orders"), f.TargetAt(TsqlProc, At("dbo.Orders o") + 5, Ss));
        Assert.Equal(new RefTarget(RefKind.Column, "dbo", "Customers", "Name"), f.TargetAt(TsqlProc, At("c.Name") + 3, Ss));
        Assert.Equal(new RefTarget(RefKind.Procedure, "dbo", "usp_GetOrders"), f.TargetAt(TsqlProc, At("usp_GetOrders") + 2, Ss));
        Assert.Null(f.TargetAt(TsqlProc, At("'OrderDate'") + 2, Ss)); // inside a string
        Assert.Null(f.TargetAt(TsqlProc, At("SELECT") + 1, Ss)); // a keyword
    }

    [Fact]
    public void Tsql_local_symbols()
    {
        var f = new TSqlReferenceFinder();
        const string sql = "DECLARE @n int = 1; SELECT @n + 1; WITH recent AS (SELECT o.Id FROM dbo.Orders o WHERE o.Qty > @n) SELECT recent.Id FROM recent;";
        string[] Occ(int offset) => f.LocalOccurrences(sql, offset)!.Select(s => sql.Substring(s.Offset, s.Length)).ToArray();
        Assert.Equal(["@n", "@n", "@n"], Occ(sql.IndexOf("@n", StringComparison.Ordinal) + 1));
        Assert.Equal(["recent", "recent", "recent"], Occ(sql.LastIndexOf("recent", StringComparison.Ordinal) + 1));
        Assert.Equal(["o", "o", "o"], Occ(sql.IndexOf("o.Qty", StringComparison.Ordinal)));
        Assert.Null(f.LocalOccurrences(sql, sql.IndexOf("Orders", StringComparison.Ordinal) + 1));
    }

    [Fact]
    public void Renamed_names_keep_their_quoting()
    {
        var ss = new TSqlReferenceFinder();
        Assert.Equal("PlacedAt", ss.Rename("OrderDate", "PlacedAt"));
        Assert.Equal("[PlacedAt]", ss.Rename("[OrderDate]", "PlacedAt"));
        Assert.Equal("[Placed At]", ss.Rename("OrderDate", "Placed At"));
        Assert.Equal("[a]]b]", ss.Rename("x", "a]b"));
        var pg = new PgReferenceFinder();
        Assert.Equal("placed_at", pg.Rename("order_date", "placed_at"));
        Assert.Equal("\"PlacedAt\"", pg.Rename("order_date", "PlacedAt"));
        Assert.Equal("\"placed_at\"", pg.Rename("\"order_date\"", "placed_at"));
    }

    [Theory]
    [InlineData("user", "[user]")]
    [InlineData("order", "[order]")]
    [InlineData("select", "[select]")]
    [InlineData("SELECT", "[SELECT]")]
    [InlineData("Name", "Name")]
    [InlineData("password", "password")]
    [InlineData("MixedCase", "MixedCase")]
    public void Sql_server_renames_to_a_reserved_word_are_bracketed(string newName, string expected)
    {
        var ss = new TSqlReferenceFinder();
        Assert.Equal(expected, ss.Rename("OrderDate", newName));
        // The rewritten statement parses: a bare reserved word would not.
        var sql = $"SELECT {ss.Rename("OrderDate", newName)} FROM dbo.Orders ORDER BY {ss.Rename("OrderDate", newName)};";
        Querithm.Intelligence.TSql.TSqlParsing.NewParser().Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("user", "\"user\"")]
    [InlineData("order", "\"order\"")]
    [InlineData("select", "\"select\"")]
    [InlineData("between", "\"between\"")]
    [InlineData("MixedCase", "\"MixedCase\"")]
    [InlineData("placed_at", "placed_at")]
    [InlineData("name", "name")]
    public void Postgres_renames_to_a_reserved_word_or_a_mixed_case_name_are_quoted(string newName, string expected)
    {
        var pg = new PgReferenceFinder();
        Assert.Equal(expected, pg.Rename("order_date", newName));
        var sql = $"SELECT {pg.Rename("order_date", newName)} FROM public.orders";
        var tokens = Querithm.Intelligence.PgSql.PgTokenizer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        Assert.Equal(expected.StartsWith('"') ? Querithm.Intelligence.PgSql.PgTokenKind.QuotedIdent : Querithm.Intelligence.PgSql.PgTokenKind.Word, tokens[1].Kind);
    }

    // ------------------------------------------------------------------ PostgreSQL

    const string PgSqlFunction = """
        CREATE OR REPLACE FUNCTION public.recent_orders(since date)
         RETURNS SETOF orders LANGUAGE sql
        AS $function$
          -- order_date in a comment
          SELECT o.* FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.order_date > since AND 'order_date' <> '';
          SELECT order_date FROM public.orders;
          SELECT r.id FROM returns r JOIN orders o2 ON o2.id = r.id WHERE order_date IS NULL;
          INSERT INTO orders (id, order_date) SELECT id, order_date FROM returns;
        $function$
        """;

    const string PgPlpgsql = """
        CREATE OR REPLACE FUNCTION public.touch(p_id integer)
         RETURNS void LANGUAGE plpgsql
        AS $$
        DECLARE
          order_date date := now();
        BEGIN
          UPDATE orders SET order_date = order_date WHERE id = p_id;
          UPDATE orders o SET qty = 1 WHERE o.order_date < order_date;
          EXECUTE format('DELETE FROM %I', 'orders');
          PERFORM nextval('public.order_no');
        END $$
        """;

    [Fact]
    public void Postgres_columns_in_sql_and_plpgsql_bodies()
    {
        var f = new PgReferenceFinder();
        var t = new RefTarget(RefKind.Column, "public", "orders", "order_date");
        var sql = f.Find(PgSqlFunction, t, Pg);
        var hits = sql.Hits.Select(h => (Text: PgSqlFunction.Substring(h.Offset, h.Length), h.Confidence, Line: PgSqlFunction[..h.Offset].Count(c => c == '\n') + 1)).ToList();
        Assert.Contains(hits, h => h.Line == 5 && h.Confidence == RefConfidence.Exact); // o.order_date
        Assert.Contains(hits, h => h.Line == 6 && h.Confidence == RefConfidence.Exact); // only orders in scope
        Assert.Contains(hits, h => h.Line == 7 && h.Confidence == RefConfidence.Possible); // returns has it too
        Assert.Contains(hits, h => h.Line == 8 && h.Confidence == RefConfidence.Exact); // INSERT column list
        Assert.Equal(1, hits.Count(h => h.Line == 8));
        Assert.DoesNotContain(hits, h => h.Line == 4);
        Assert.Equal(1, hits.Count(h => h.Line == 5)); // the string literal is not a reference
        Assert.False(sql.DynamicSql);

        var pl = f.Find(PgPlpgsql, t, Pg);
        Assert.True(pl.DynamicSql);
        var plHits = pl.Hits.Select(h => (h.Confidence, Line: PgPlpgsql[..h.Offset].Count(c => c == '\n') + 1)).ToList();
        // Unqualified names in PL/pgSQL may be the variable: possible; o.order_date is exact.
        Assert.Contains(plHits, h => h.Line == 8 && h.Confidence == RefConfidence.Exact);
        Assert.All(plHits.Where(h => h.Line == 7), h => Assert.Equal(RefConfidence.Possible, h.Confidence));
    }

    [Fact]
    public void Postgres_tables_functions_sequences_and_headers()
    {
        var f = new PgReferenceFinder();
        // FROM, FROM public., JOIN and INSERT INTO; RETURNS SETOF orders is the row type (it follows a rename by itself).
        Assert.Equal(4, f.Find(PgSqlFunction, new RefTarget(RefKind.Table, "public", "orders"), Pg).Hits.Count(h => !h.Definition));
        var header = f.Find(PgPlpgsql, new RefTarget(RefKind.Function, "public", "touch"), Pg);
        Assert.True(Assert.Single(header.Hits).Definition);
        const string calls = "SELECT public.touch(1), touch(2), x.touch FROM t x;";
        Assert.Equal(2, f.Find(calls, new RefTarget(RefKind.Function, "public", "touch"), Pg).Hits.Count);
        var seq = f.Find(PgPlpgsql, new RefTarget(RefKind.Sequence, "public", "order_no"), Pg);
        Assert.Equal("order_no", PgPlpgsql.Substring(Assert.Single(seq.Hits).Offset, seq.Hits[0].Length));
        Assert.True(f.ServerTracked("CREATE OR REPLACE VIEW v AS SELECT 1", ObjectKind.View));
        Assert.True(f.ServerTracked("CREATE FUNCTION f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT 1; END", ObjectKind.Func));
        Assert.False(f.ServerTracked(PgSqlFunction, ObjectKind.Func));
    }

    [Fact]
    public void Postgres_target_at_the_cursor_and_local_symbols()
    {
        var f = new PgReferenceFinder();
        var at = PgSqlFunction.IndexOf("o.order_date", StringComparison.Ordinal) + 3;
        Assert.Equal(new RefTarget(RefKind.Column, "public", "orders", "order_date"), f.TargetAt(PgSqlFunction, at, Pg));
        Assert.Equal(new RefTarget(RefKind.Table, "public", "customers"), f.TargetAt(PgSqlFunction, PgSqlFunction.IndexOf("customers c", StringComparison.Ordinal) + 2, Pg));
        Assert.Equal(new RefTarget(RefKind.Function, "public", "recent_orders"), f.TargetAt(PgSqlFunction, PgSqlFunction.IndexOf("recent_orders", StringComparison.Ordinal) + 2, Pg));
        Assert.Null(f.TargetAt(PgSqlFunction, PgSqlFunction.IndexOf("'order_date'", StringComparison.Ordinal) + 2, Pg));

        const string sql = "WITH recent AS (SELECT o.id FROM orders o WHERE o.qty > 1) SELECT recent.id FROM recent";
        string[] Occ(string text, int offset) => f.LocalOccurrences(text, offset)!.Select(s => text.Substring(s.Offset, s.Length)).ToArray();
        Assert.Equal(["recent", "recent", "recent"], Occ(sql, sql.LastIndexOf("recent", StringComparison.Ordinal) + 1));
        Assert.Equal(["o", "o", "o"], Occ(sql, sql.IndexOf("o.qty", StringComparison.Ordinal)));
        Assert.Equal(["p_id", "p_id"], Occ(PgPlpgsql, PgPlpgsql.IndexOf("p_id;", StringComparison.Ordinal) + 1));
    }

    [Fact]
    public void The_prefilter_matches_whole_words_only()
    {
        Assert.True(RefText.ContainsWord("WHERE id = @Id", "id"));
        Assert.False(RefText.ContainsWord("WHERE CustomerId = @Id", "id")); // part of a longer name, and a variable
        Assert.True(RefText.ContainsWord("SELECT [Id] FROM t", "id"));
        Assert.True(RefText.ContainsWord("x.\"order_date\"", "order_date"));
        Assert.False(RefText.ContainsWord("order_dates", "order_date"));
        Assert.True(RefText.ContainsWord("abc id", "id", 3, 3));
        Assert.False(RefText.ContainsWord("abc id", "id", 0, 4));
    }

    [Fact]
    public void Large_texts_are_searched_chunk_by_chunk_with_the_same_hits()
    {
        // Past ChunkedDiagnostics.MinLength the finder parses only the statement chunks that name the table.
        var sb = new System.Text.StringBuilder("CREATE PROCEDURE dbo.big AS\nBEGIN\n");
        for (var i = 0; i < 4000; i++)
            sb.Append(i % 50 == 0 ? $"    UPDATE dbo.Orders SET OrderDate = GETDATE() WHERE Id = {i};\n" : $"    UPDATE dbo.Customers SET Name = N'x{i}' WHERE Id = {i};\n");
        sb.Append("END\n");
        var text = sb.ToString();
        Assert.True(text.Length >= ChunkedDiagnostics.MinLength);
        var scan = new TSqlReferenceFinder().Find(text, new RefTarget(RefKind.Column, "dbo", "Orders", "OrderDate"), Ss);
        Assert.Equal(80, scan.Hits.Count);
        Assert.All(scan.Hits, h => Assert.Equal("OrderDate", text.Substring(h.Offset, h.Length)));
        Assert.All(scan.Hits, h => Assert.Equal(RefConfidence.Exact, h.Confidence));
    }

    [Fact]
    public async Task Refactor_service_finds_usages_and_previews_table_rename_from_cached_definitions_and_open_tabs()
    {
        await using var h = new TestHost();
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Mssql, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [
                new ObjectStamp(1, "dbo", "Orders", ObjectKind.Table, 1, null),
                new ObjectStamp(2, "dbo", "Customers", ObjectKind.Table, 1, null),
                new ObjectStamp(3, "dbo", "v_OrderSummary", ObjectKind.View, 1, null),
                new ObjectStamp(4, "dbo", "usp_TouchOrders", ObjectKind.Proc, 1, null),
                new ObjectStamp(5, "dbo", "usp_Encrypted", ObjectKind.Proc, 1, null),
            ], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, "Id", "int", null, null, null, false, true, false, null),
                new ColumnRow(1, 2, "OrderDate", "date", null, null, null, true, false, false, null),
                new ColumnRow(2, 1, "Id", "int", null, null, null, false, true, false, null),
            ], [], [], []), "16");
        h.Services.Metadata.Store.CacheDefinitions(p.Id, "db",
        [
            (3, "CREATE VIEW dbo.v_OrderSummary AS SELECT o.Id, o.OrderDate FROM dbo.Orders o;"),
            (4, "CREATE PROCEDURE dbo.usp_TouchOrders AS BEGIN UPDATE dbo.Orders SET OrderDate = GETDATE(); EXEC (N'SELECT * FROM dbo.Orders'); END"),
            (5, null), // encrypted
        ]);
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");

        var target = new RefactorTarget { Kind = RefactorKind.Table, Schema = "dbo", Name = "Orders" };
        var usages = await h.Rpc.RefactorFindUsages(new FindUsagesParams
        {
            SessionId = s.SessionId, Database = "db", Target = target,
            Tabs = [new OpenTabText { TabId = "t1", Title = "Query1.sql", Text = "SELECT * FROM dbo.Orders;" }],
        });
        Assert.Contains(usages.Hits, x => x.Object?.Name == "v_OrderSummary" && x.Confidence == UsageConfidence.Exact);
        Assert.Contains(usages.Hits, x => x.Object?.Name == "usp_TouchOrders" && x.Confidence == UsageConfidence.Exact);
        Assert.Contains(usages.Hits, x => x.TabId == "t1");
        Assert.Contains(usages.Unchecked, u => u.Object.Name == "usp_Encrypted" && u.Reason.Contains("Encrypted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(usages.Unchecked, u => u.Object.Name == "usp_TouchOrders" && u.Reason.Contains("run time"));

        // Renaming to the exact same name is refused.
        var sameName = await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = s.SessionId, Database = "db", Target = target, NewName = "Orders",
        }));
        Assert.Equal("invalid", sameName.Code);

        // Previewing a valid table rename rewrites both modules and the open tab.
        var preview = await h.Rpc.RefactorPreviewRename(new RenameParams
        {
            SessionId = s.SessionId, Database = "db", Target = target, NewName = "PurchaseOrders",
            Tabs = [new OpenTabText { TabId = "t1", Title = "Query1.sql", Text = "SELECT * FROM dbo.Orders;" }],
        });
        Assert.Contains("EXEC sp_rename N'[dbo].[Orders]', N'PurchaseOrders';", preview.Sql);
        Assert.Equal(2, preview.Objects.Length);
        Assert.All(preview.Objects, o => Assert.Contains("PurchaseOrders", o.After));
        Assert.Equal("PurchaseOrders", Assert.Single(Assert.Single(preview.Tabs).Edits).Text);
    }

    public static TheoryData<string> Engines => TestEngines.Names;

    [Theory, MemberData(nameof(Engines))]
    public async Task Rename_refuses_a_name_that_is_taken_in_the_engines_name_space(string engine)
    {
        await using var h = new TestHost();
        var pg = engine == "pg";
        var s = pg ? "public" : "dbo";
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = pg ? Engine.Pg : Engine.Mssql, Host = "h" });
        var sid = (await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" })).SessionId;
        string I(string t) => pg ? "integer" : t;
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [
                new ObjectStamp(1, s, "Orders", ObjectKind.Table, 1, null),
                new ObjectStamp(2, s, "Customers", ObjectKind.Table, 1, null),
                new ObjectStamp(3, s, "calc", ObjectKind.Func, 1, "int"),
                new ObjectStamp(4, s, "calc_text", ObjectKind.Func, 1, "int"),
                new ObjectStamp(5, s, "calc_int", ObjectKind.Func, 1, "int"),
                new ObjectStamp(6, s, "Money", ObjectKind.Type, 1, null),
            ], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, "Id", I("int"), null, null, null, false, true, false, null),
                new ColumnRow(1, 2, "OrderDate", "date", null, null, null, true, false, false, null),
                new ColumnRow(2, 1, "Id", I("int"), null, null, null, false, true, false, null),
            ],
            [
                new ParamRow(3, 1, "x", I("int"), null, false, false),
                new ParamRow(4, 1, "x", "text", null, false, false),
                new ParamRow(5, 1, "x", I("int"), null, false, false),
            ], [], []), "16");
        string Fn(string name, string arg) => pg
            ? $"CREATE OR REPLACE FUNCTION public.{name}(x {arg}) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$"
            : $"CREATE FUNCTION dbo.{name}(@x {arg}) RETURNS int AS BEGIN RETURN 1; END";
        h.Services.Metadata.Store.CacheDefinitions(p.Id, "db", [(3, Fn("calc", I("int"))), (4, Fn("calc_text", "text")), (5, Fn("calc_int", I("int")))]);
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");
        Task<RenamePreviewResult> Rename(RefactorTarget t, string to) => h.Rpc.RefactorPreviewRename(new RenameParams { SessionId = sid, Database = "db", Target = t, NewName = to });
        async Task Refused(RefactorTarget t, string to) => Assert.Equal("refactorConflict", (await Assert.ThrowsAsync<Querithm.Core.QuerithmException>(() => Rename(t, to))).Code);

        var orders = new RefactorTarget { Kind = RefactorKind.Table, Schema = s, Name = "Orders" };
        var orderDate = new RefactorTarget { Kind = RefactorKind.Column, Schema = s, Name = "Orders", Column = "OrderDate" };
        var calc = new RefactorTarget { Kind = RefactorKind.Function, Schema = s, Name = "calc" };

        await Refused(orders, pg ? "Customers" : "CUSTOMERS"); // SQL Server compares names without case
        await Refused(orderDate, "Id");
        await Refused(calc, "calc_int"); // same arguments
        if (pg)
        {
            Assert.Contains("ALTER FUNCTION", (await Rename(calc, "calc_text")).Sql); // an overload with other arguments may share the name
            await Refused(orders, "Money"); // a table brings a type of its name
        }
        else
        {
            await Refused(calc, "calc_text"); // one name per schema for objects
            Assert.Contains("sp_rename", (await Rename(orders, "Money")).Sql); // types live in sys.types
        }
        Assert.Contains(pg ? "RENAME TO" : "sp_rename", (await Rename(orders, "ORDERS")).Sql); // a change of case is no clash
    }
}
