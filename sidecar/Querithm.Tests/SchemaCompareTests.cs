using System.Text.Json;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Compare;
using Querithm.Core.Metadata;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Schema compare without a server: normalisation, classes and tags, renames, script order and guards, per engine.</summary>
public class SchemaCompareTests
{
    public static TheoryData<string> Engines => TestEngines.Names;

    static ISchemaCompareDialect D(string engine) => engine == "mssql" ? new SqlServerSchemaCompare() : new PostgresSchemaCompare();
    static string Dbo(string engine) => engine == "mssql" ? "dbo" : "public";
    static string Int(string engine) => engine == "mssql" ? "int" : "integer";
    static string Big(string engine) => engine == "mssql" ? "bigint" : "bigint";

    static ModelColumn Col(string name, string type, bool nullable = true, string? dflt = null) => new() { Name = name, Type = type, Nullable = nullable, Default = dflt };

    static ModelTable Table(string schema, string name, params ModelColumn[] cols) =>
        new() { Schema = schema, Name = name, Columns = cols, PrimaryKey = new ModelKey { Name = $"pk_{name}", Columns = [cols[0].Name] } };

    static SchemaModel Model(string engine, ModelTable[] tables, ModelModule[]? modules = null, ModelSequence[]? sequences = null, ModelType[]? types = null) => new()
    {
        Engine = engine == "mssql" ? Engine.Mssql : Engine.Pg, MajorVersion = 16, Database = "db", Schemas = [Dbo(engine)], Tables = tables,
        Modules = modules ?? [], Sequences = sequences ?? [], Types = types ?? [],
    };

    static ModelModule View(string engine, string name, string body, bool schemaBound = false, params ModelRef[] refs) => new()
    {
        Schema = Dbo(engine), Name = name, Kind = ModuleKind.View, SchemaBound = schemaBound, References = refs,
        Definition = engine == "mssql"
            ? $"CREATE VIEW dbo.{name}{(schemaBound ? " WITH SCHEMABINDING" : "")} AS {body}"
            : $"CREATE OR REPLACE VIEW public.{name} AS\n {body};",
    };

    static CompareItem Item(Comparison c, CompareObjectKind kind, string name) => c.Items.Single(i => i.Kind == kind && i.Name == name);

    static SchemaMigrationResult Migrate(Comparison c, bool dataLoss = true, PossibleRename[]? renames = null) =>
        new MigrationScriptBuilder(c).Build(c.Items.Select(i => i.Key).ToArray(), renames ?? [], dataLoss, "-- test");

    // ------------------------------------------------------------------ normalisation

    [Fact]
    public void Sql_server_expressions_normalise_on_tokens()
    {
        var d = new SqlServerSchemaCompare();
        Assert.Equal(d.NormalizeExpression("((0))"), d.NormalizeExpression("(0)"));
        Assert.Equal(d.NormalizeExpression("([qty]>(0))"), d.NormalizeExpression("( QTY > (0) )"));
        Assert.Equal(d.NormalizeExpression("(getdate())"), d.NormalizeExpression("GETDATE /* now */ ()"));
        Assert.NotEqual(d.NormalizeExpression("(N'A')"), d.NormalizeExpression("(N'a')"));
        Assert.NotEqual(d.NormalizeExpression("(1)+(2)"), d.NormalizeExpression("(1+2)")); // only parentheses around the whole expression go
    }

    [Fact]
    public void Postgres_expressions_normalise_on_tokens()
    {
        var d = new PostgresSchemaCompare();
        Assert.Equal(d.NormalizeExpression("(qty > 0)"), d.NormalizeExpression("QTY>0"));
        Assert.Equal(d.NormalizeExpression("\"qty\" > 0"), d.NormalizeExpression("qty > 0"));
        Assert.NotEqual(d.NormalizeExpression("\"Qty\" > 0"), d.NormalizeExpression("qty > 0"));
        Assert.NotEqual(d.NormalizeExpression("'A'::text"), d.NormalizeExpression("'a'::text"));
        Assert.Equal(d.NormalizeExpression("1 -- one\n + 2"), d.NormalizeExpression("1+2"));
    }

    [Fact]
    public void Bodies_ignore_header_whitespace_and_comments()
    {
        var ss = new SqlServerSchemaCompare();
        Assert.Equal(ss.NormalizeBody("CREATE PROC dbo.p AS SELECT 1"), ss.NormalizeBody("create or alter procedure dbo.p\nAS\n-- note\nSELECT 1;"));
        Assert.Equal(ss.NormalizeBody("ALTER VIEW v AS SELECT [a] FROM t"), ss.NormalizeBody("CREATE VIEW v AS SELECT a FROM t"));
        Assert.NotEqual(ss.NormalizeBody("CREATE VIEW v AS SELECT 'x'"), ss.NormalizeBody("CREATE VIEW v AS SELECT 'X'"));
        Assert.Equal("VIEW v AS SELECT 1", ss.WithoutHeader("CREATE OR ALTER VIEW v AS SELECT 1"));
        Assert.Equal("VIEW v AS SELECT 1", ss.WithoutHeader("/* c */ CREATE VIEW v AS SELECT 1"));

        var pg = new PostgresSchemaCompare();
        const string a = "CREATE OR REPLACE FUNCTION public.f() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$";
        Assert.Equal(pg.NormalizeBody(a), pg.NormalizeBody("CREATE FUNCTION public.f()\n RETURNS int\n LANGUAGE plpgsql\nAS $fn$\nBEGIN\n  RETURN 1; -- one\nEND\n$fn$;"));
        Assert.NotEqual(pg.NormalizeBody(a), pg.NormalizeBody(a.Replace("RETURN 1", "RETURN 2")));
        Assert.Equal("FUNCTION f() AS 1", pg.WithoutHeader("CREATE OR REPLACE FUNCTION f() AS 1"));
    }

    [Fact]
    public void Schema_renames_touch_qualified_names_only()
    {
        var ss = new SqlServerSchemaCompare();
        Assert.Equal("SELECT i.id FROM qb.items i JOIN [qb].[other] o ON o.id = i.id WHERE s = 'qa.items' AND x = db.qa.t",
            ss.RenameSchema("SELECT i.id FROM qa.items i JOIN [qa].[other] o ON o.id = i.id WHERE s = 'qa.items' AND x = db.qa.t", "qa", "qb"));
        Assert.Equal("SELECT * FROM [my schema].t", ss.RenameSchema("SELECT * FROM qa.t", "qa", "my schema"));

        var pg = new PostgresSchemaCompare();
        Assert.Equal("SELECT * FROM qb.items, \"qb\".x WHERE y = 'qa.x' AND f = $$ SELECT 1 FROM qb.t $$",
            pg.RenameSchema("SELECT * FROM qa.items, \"qa\".x WHERE y = 'qa.x' AND f = $$ SELECT 1 FROM qa.t $$", "qa", "qb"));
        Assert.Equal("SELECT * FROM \"Q B\".t", pg.RenameSchema("SELECT * FROM qa.t", "qa", "Q B"));
    }

    // ------------------------------------------------------------------ classes and tags

    [Theory, MemberData(nameof(Engines))]
    public void Every_class_and_tag(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var src = Model(engine,
            [
                Table(s, "same", Col("id", i, false)),
                Table(s, "retyped", Col("id", i, false), Col("qty", Big(engine))),
                Table(s, "indexed", Col("id", i, false), Col("a", i)) with { Indexes = [new ModelIndex { Name = "ix_a", Columns = ["a"], Kind = "NONCLUSTERED", Definition = engine == "pg" ? "CREATE INDEX ix_a ON public.indexed USING btree (a)" : null }] },
                Table(s, "checked", Col("id", i, false), Col("a", i)) with { Checks = [new ModelCheck { Name = "ck_a", Expression = engine == "mssql" ? "([a]>(0))" : "CHECK ((a > 0))" }] },
                Table(s, "added", Col("id", i, false)),
            ],
            [
                View(engine, "v_same", "SELECT 1 AS one"),
                View(engine, "v_ws", "SELECT 1 AS one"),
                View(engine, "v_body", "SELECT 2 AS two"),
            ],
            [new ModelSequence { Schema = s, Name = "seq", Type = "bigint", Start = "1", Increment = "1" }]);
        var tgt = Model(engine,
            [
                Table(s, "same", Col("id", i, false)),
                Table(s, "retyped", Col("id", i, false), Col("qty", i)),
                Table(s, "indexed", Col("id", i, false), Col("a", i)),
                Table(s, "checked", Col("id", i, false), Col("a", i)) with { Checks = [new ModelCheck { Name = "ck_a", Expression = engine == "mssql" ? "([a]>(1))" : "CHECK ((a > 1))" }] },
                Table(s, "dropped", Col("id", i, false)),
            ],
            [
                View(engine, "v_same", "SELECT 1 AS one"),
                View(engine, "v_ws", "SELECT   1   AS one -- same"),
                View(engine, "v_body", "SELECT 3 AS three"),
            ],
            [new ModelSequence { Schema = s, Name = "seq", Type = "bigint", Start = "1", Increment = "5" }]);
        var c = SchemaComparer.Compare(src, tgt, d);

        Assert.Equal(CompareClass.Identical, Item(c, CompareObjectKind.Table, "same").Class);
        var retyped = Item(c, CompareObjectKind.Table, "retyped");
        Assert.Equal((CompareClass.Different, DiffTag.Columns), (retyped.Class, retyped.Tags.Single()));
        Assert.True(retyped.DataLoss);
        Assert.Contains(retyped.Changes, x => x.Contains($"{i} → bigint"));
        Assert.Equal([DiffTag.Indexes], Item(c, CompareObjectKind.Table, "indexed").Tags);
        Assert.Equal([DiffTag.Constraints], Item(c, CompareObjectKind.Table, "checked").Tags);
        Assert.Equal(CompareClass.SourceOnly, Item(c, CompareObjectKind.Table, "added").Class);
        var dropped = Item(c, CompareObjectKind.Table, "dropped");
        Assert.Equal(CompareClass.TargetOnly, dropped.Class);
        Assert.True(dropped.DataLoss);
        Assert.Equal(CompareClass.Identical, Item(c, CompareObjectKind.View, "v_same").Class);
        Assert.Equal([DiffTag.WhitespaceOnly], Item(c, CompareObjectKind.View, "v_ws").Tags);
        Assert.Equal([DiffTag.Body], Item(c, CompareObjectKind.View, "v_body").Tags);
        Assert.Equal([DiffTag.Options], Item(c, CompareObjectKind.Sequence, "seq").Tags);
        Assert.Equal(CompareClass.Identical, Item(c, CompareObjectKind.Schema, s).Class);
    }

    [Fact]
    public void Encrypted_modules_are_flagged_not_compared()
    {
        var d = new SqlServerSchemaCompare();
        var enc = new ModelModule { Schema = "dbo", Name = "p", Kind = ModuleKind.Procedure, Definition = null };
        var c = SchemaComparer.Compare(Model("mssql", [], [enc]), Model("mssql", [], [enc]), d);
        Assert.Contains("Encrypted", Item(c, CompareObjectKind.Procedure, "p").Note);
        var one = SchemaComparer.Compare(Model("mssql", [], [enc]), Model("mssql", [], []), d);
        Assert.Contains("not available", Item(one, CompareObjectKind.Procedure, "p").Note);
        Assert.Contains(Migrate(one).Warnings, w => w.Contains("encrypted"));
    }

    [Fact]
    public void Server_named_constraints_match_by_shape()
    {
        var d = new SqlServerSchemaCompare();
        ModelTable T(string pk, string ck) => Table("dbo", "t", Col("id", "int", false), Col("a", "int")) with
        {
            PrimaryKey = new ModelKey { Name = pk, SystemNamed = true, Columns = ["id"], Kind = "CLUSTERED" },
            Checks = [new ModelCheck { Name = ck, SystemNamed = true, Expression = "([a]>(0))" }],
        };
        var c = SchemaComparer.Compare(Model("mssql", [T("PK__t__3213E83F1A2B", "CK__t__a__5BE2A6F2")]), Model("mssql", [T("PK__t__3213E83F9C8D", "CK__t__a__7D439ABC")]), d);
        Assert.Equal(CompareClass.Identical, Item(c, CompareObjectKind.Table, "t").Class);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Possible_renames_are_offered_only_when_unambiguous(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var one = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", i, false), Col("placed_at", "date"))]),
            Model(engine, [Table(s, "t", Col("id", i, false), Col("order_date", "date"))]), d);
        var r = Assert.Single(one.Renames);
        Assert.Equal(("order_date", "placed_at", "date"), (r.From, r.To, r.Type));

        var two = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", i, false), Col("b", "date"), Col("d", "date"))]),
            Model(engine, [Table(s, "t", Col("id", i, false), Col("a", "date"), Col("c", "date"))]), d);
        Assert.Empty(two.Renames);
        var other = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", i, false), Col("b", "text"))]),
            Model(engine, [Table(s, "t", Col("id", i, false), Col("a", "date"))]), d);
        Assert.Empty(other.Renames);
    }

    // ------------------------------------------------------------------ migration script

    [Theory, MemberData(nameof(Engines))]
    public void Renames_are_never_assumed_and_confirmed_ones_keep_the_data(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var c = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("placed_at", "date"))]),
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("order_date", "date"))]), d);
        var plain = Migrate(c);
        Assert.Contains("DROP COLUMN", plain.Sql);
        Assert.DoesNotContain(engine == "mssql" ? "sp_rename" : "RENAME COLUMN", plain.Sql);

        var renamed = Migrate(c, renames: [c.Renames.Single()]);
        Assert.DoesNotContain("DROP COLUMN", renamed.Sql);
        Assert.Contains(engine == "mssql" ? "EXEC sp_rename N'[dbo].[t].[order_date]', N'placed_at', N'COLUMN';" : "ALTER TABLE \"public\".\"t\" RENAME COLUMN \"order_date\" TO \"placed_at\";", renamed.Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Data_loss_needs_an_explicit_yes(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var c = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", i, false), Col("qty", "bigint"))]),
            Model(engine, [Table(s, "t", Col("id", i, false), Col("qty", i), Col("legacy", i)), Table(s, "old", Col("id", i, false))]), d);
        var safe = Migrate(c, dataLoss: false);
        Assert.Contains(safe.Skipped, x => x.Contains("drop table") && x.Contains("old"));
        Assert.Contains(safe.Skipped, x => x.Contains("legacy"));
        Assert.Contains(safe.Skipped, x => x.Contains("qty"));
        var code = string.Join("\n", safe.Sql.Split('\n').Where(l => !l.StartsWith("--")));
        Assert.DoesNotContain("DROP TABLE", code);
        Assert.DoesNotContain("legacy", code);

        var all = Migrate(c, dataLoss: true);
        Assert.Empty(all.Skipped);
        Assert.Contains("DROP TABLE", all.Sql);
        Assert.Contains("legacy", all.Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Dependents_of_an_altered_column_are_dropped_first_and_recreated_after(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var t = new ModelRef(s, "t");
        var v = new ModelRef(s, "v");
        ModelModule[] Views() =>
        [
            View(engine, "v", engine == "mssql" ? "SELECT id, qty FROM dbo.t" : "SELECT id, qty FROM t", schemaBound: true, t),
            View(engine, "w", "SELECT id FROM v", schemaBound: false, v),
        ];
        var c = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("qty", "bigint"))], Views()),
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("qty", Int(engine)))], Views()), d);
        var sql = Migrate(c).Sql;
        int At(string what) { var k = sql.IndexOf(what, StringComparison.Ordinal); Assert.True(k >= 0, $"{what} missing in\n{sql}"); return k; }
        var q = engine == "mssql" ? "[dbo].[v]" : "\"public\".\"v\"";
        var alter = At(engine == "mssql" ? "ALTER COLUMN [qty] bigint" : "ALTER COLUMN \"qty\" TYPE bigint");
        Assert.True(At($"DROP VIEW {q}") < alter);
        Assert.True(alter < At(engine == "mssql" ? "CREATE OR ALTER VIEW dbo.v" : "CREATE OR REPLACE VIEW public.v "));
        if (engine == "pg")
        {
            // PostgreSQL: views on views block the drop too; dependents go first and come back after.
            Assert.True(At("DROP VIEW \"public\".\"w\"") < At("DROP VIEW \"public\".\"v\""));
            Assert.True(At("CREATE OR REPLACE VIEW public.v ") < At("CREATE OR REPLACE VIEW public.w"));
        }
        else Assert.DoesNotContain("DROP VIEW [dbo].[w]", sql); // not schema-bound: SQL Server lets it be
    }

    [Fact]
    public void Sql_server_warns_about_modules_a_dropped_table_breaks()
    {
        var d = new SqlServerSchemaCompare();
        var v = View("mssql", "v", "SELECT id FROM dbo.old", false, new ModelRef("dbo", "old"));
        var c = SchemaComparer.Compare(Model("mssql", [], [v]), Model("mssql", [Table("dbo", "old", Col("id", "int", false))], [v]), d);
        var r = Migrate(c);
        Assert.Contains(r.Warnings, w => w.StartsWith("dbo.v reads a table that is dropped"));
        Assert.DoesNotContain("DROP VIEW", r.Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Computed_columns_on_an_altered_column_are_dropped_and_re_added(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var total = new ModelColumn { Name = "total", Type = engine == "mssql" ? "" : "bigint", Computed = "(qty * 2)", Persisted = engine == "pg", Uses = ["qty"] };
        var c = SchemaComparer.Compare(
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("qty", "bigint"), total)]),
            Model(engine, [Table(s, "t", Col("id", Int(engine), false), Col("qty", Int(engine)), total)]), d);
        var sql = Migrate(c).Sql;
        var drop = sql.IndexOf(engine == "mssql" ? "DROP COLUMN [total]" : "DROP COLUMN \"total\"", StringComparison.Ordinal);
        var alter = sql.IndexOf("ALTER COLUMN", StringComparison.Ordinal);
        var add = sql.IndexOf(engine == "mssql" ? "ADD [total] AS" : "ADD COLUMN \"total\"", StringComparison.Ordinal);
        Assert.True(drop >= 0 && drop < alter && alter < add, sql);
    }

    [Fact]
    public void Sql_server_indexes_checks_and_fks_on_a_retyped_column_are_rebuilt_around_it()
    {
        var d = new SqlServerSchemaCompare();
        ModelTable Parent(string type) => Table("dbo", "p", Col("id", type, false));
        ModelTable Child(string type) => Table("dbo", "c", Col("id", "int", false), Col("pid", type, false)) with
        {
            Indexes = [new ModelIndex { Name = "ix_pid", Columns = ["pid"], Kind = "NONCLUSTERED" }],
            Checks = [new ModelCheck { Name = "ck_pid", Expression = "([pid]>(0))", Columns = ["pid"] }],
            Fks = [new ModelFk { Name = "fk_c_p", Columns = ["pid"], RefSchema = "dbo", RefTable = "p", RefColumns = ["id"] }],
        };
        var c = SchemaComparer.Compare(Model("mssql", [Parent("bigint"), Child("bigint")]), Model("mssql", [Parent("int"), Child("int")]), d);
        var sql = Migrate(c).Sql;
        int At(string what, int from = 0) { var k = sql.IndexOf(what, from, StringComparison.Ordinal); Assert.True(k >= 0, $"{what} missing in\n{sql}"); return k; }
        var alterParent = At("ALTER TABLE [dbo].[p] ALTER COLUMN [id] bigint");
        Assert.True(At("DROP CONSTRAINT [fk_c_p]") < At("DROP CONSTRAINT [pk_p]"));
        Assert.True(At("DROP CONSTRAINT [pk_p]") < alterParent);
        Assert.True(At("DROP INDEX [ix_pid]") < At("ALTER TABLE [dbo].[c] ALTER COLUMN [pid] bigint"));
        Assert.True(At("DROP CONSTRAINT [ck_pid]") < At("ALTER TABLE [dbo].[c] ALTER COLUMN [pid] bigint"));
        Assert.True(alterParent < At("ADD CONSTRAINT [pk_p] PRIMARY KEY"));
        Assert.True(At("ADD CONSTRAINT [pk_p] PRIMARY KEY") < At("ADD CONSTRAINT [fk_c_p] FOREIGN KEY"));
        Assert.True(At("CREATE NONCLUSTERED INDEX [ix_pid]") > At("ALTER TABLE [dbo].[c] ALTER COLUMN [pid] bigint"));
    }

    [Fact]
    public void Sql_server_script_guards_every_batch()
    {
        var d = new SqlServerSchemaCompare();
        var sql = d.Wrap([new MigrationStep(MigrationPhase.Tables, "CREATE TABLE a (id int);", "a"), new MigrationStep(MigrationPhase.Tables, "CREATE TABLE b (id int);", "b"),
            new MigrationStep(MigrationPhase.Modules, "CREATE VIEW v AS SELECT 1 AS x;", "v")], "-- head");
        Assert.StartsWith("-- head\n\nSET XACT_ABORT ON;\nGO\nBEGIN TRANSACTION;\nGO\n", sql);
        Assert.Equal(3, sql.Split(SqlServerSchemaCompare.Guard).Length - 1);
        Assert.EndsWith($"CREATE VIEW v AS SELECT 1 AS x;\nGO\n{SqlServerSchemaCompare.Guard}\nGO\nCOMMIT TRANSACTION;\nGO\nSET NOEXEC OFF;\nGO\n", sql);
    }

    [Fact]
    public void Postgres_enum_values_are_added_before_the_transaction()
    {
        var d = new PostgresSchemaCompare();
        ModelType E(params string[] v) => new() { Schema = "public", Name = "status", Kind = "enum", Values = v };
        var c = SchemaComparer.Compare(Model("pg", [], types: [E("new", "paid", "shipped", "done")]), Model("pg", [], types: [E("new", "shipped")]), d);
        var sql = Migrate(c).Sql;
        var begin = sql.IndexOf("BEGIN;", StringComparison.Ordinal);
        Assert.True(sql.IndexOf("ADD VALUE IF NOT EXISTS 'paid' AFTER 'new';", StringComparison.Ordinal) is var a && a >= 0 && a < begin, sql);
        Assert.True(sql.IndexOf("ADD VALUE IF NOT EXISTS 'done' AFTER 'shipped';", StringComparison.Ordinal) is var b && b >= 0 && b < begin, sql);

        var reordered = SchemaComparer.Compare(Model("pg", [], types: [E("shipped", "new")]), Model("pg", [], types: [E("new", "shipped")]), d);
        Assert.NotNull(Item(reordered, CompareObjectKind.Type, "status").Note);
        Assert.Contains(Migrate(reordered).Warnings, w => w.Contains("cannot be scripted"));
    }

    [Fact]
    public void Postgres_replace_rules()
    {
        var d = new PostgresSchemaCompare();
        var f = new ModelModule { Schema = "public", Name = "f", Kind = ModuleKind.Function, Signature = "", Returns = "integer", Definition = "CREATE OR REPLACE FUNCTION public.f() ..." };
        Assert.False(d.NeedsDropBeforeCreate(f, f, 16));
        Assert.True(d.NeedsDropBeforeCreate(f, f with { Returns = "bigint" }, 16));
        var v = new ModelModule { Schema = "public", Name = "v", Kind = ModuleKind.View, Columns = ["id integer", "qty integer"], Definition = "x" };
        Assert.False(d.NeedsDropBeforeCreate(v, v with { Columns = ["id integer", "qty integer", "extra text"] }, 16));
        Assert.True(d.NeedsDropBeforeCreate(v, v with { Columns = ["id integer"] }, 16));
        Assert.True(d.NeedsDropBeforeCreate(v, v with { Columns = ["id integer", "qty bigint"] }, 16));
        Assert.True(d.NeedsDropBeforeCreate(v with { Kind = ModuleKind.MaterializedView }, v with { Kind = ModuleKind.MaterializedView }, 16));
        var trg = new ModelModule { Schema = "public", Name = "t_x", Table = "x", Kind = ModuleKind.Trigger, Definition = "CREATE TRIGGER t_x BEFORE INSERT ON public.x FOR EACH ROW EXECUTE FUNCTION f();" };
        Assert.True(d.NeedsDropBeforeCreate(trg, trg, 13));
        Assert.False(d.NeedsDropBeforeCreate(trg, trg, 14));
        Assert.StartsWith("CREATE OR REPLACE TRIGGER t_x", d.CreateModule(trg, 14));
        Assert.StartsWith("CREATE TRIGGER t_x", d.CreateModule(trg, 13));
        Assert.Equal("DROP TRIGGER \"t_x\" ON \"public\".\"x\";", d.DropModule(trg));
    }

    [Fact]
    public void Sql_server_modules_get_create_or_alter_and_their_schema()
    {
        var d = new SqlServerSchemaCompare();
        var p = new ModelModule { Schema = "sales", Name = "p", Kind = ModuleKind.Procedure, Definition = "CREATE PROC p AS SELECT 1" };
        Assert.Equal("CREATE OR ALTER PROC [sales].p AS SELECT 1", d.CreateModule(p, 16));
        Assert.Equal("CREATE PROC [sales].p AS SELECT 1", d.CreateModule(p, 13));
        Assert.True(d.NeedsDropBeforeCreate(p, p, 13));
        Assert.False(d.NeedsDropBeforeCreate(p, p, 16));
        Assert.Equal("CREATE OR ALTER VIEW dbo.v AS SELECT 1", d.CreateModule(p with { Schema = "dbo", Kind = ModuleKind.View, Definition = "CREATE OR ALTER VIEW dbo.v AS SELECT 1" }, 16));
    }

    [Theory, MemberData(nameof(Engines))]
    public void New_modules_are_created_after_what_they_reference(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var v1 = View(engine, "v1", "SELECT 1 AS one");
        var v2 = View(engine, "v2", "SELECT one FROM v1", false, new ModelRef(s, "v1"));
        var c = SchemaComparer.Compare(Model(engine, [], [v2, v1]), Model(engine, []), d);
        var sql = Migrate(c).Sql;
        Assert.True(sql.IndexOf($"{s}.v1", StringComparison.Ordinal) < sql.IndexOf($"{s}.v2", StringComparison.Ordinal), sql);

        var a = View(engine, "a", "SELECT 1 AS x", false, new ModelRef(s, "b"));
        var b = View(engine, "b", "SELECT 1 AS x", false, new ModelRef(s, "a"));
        Assert.Contains(Migrate(SchemaComparer.Compare(Model(engine, [], [a, b]), Model(engine, []), d)).Warnings, w => w.StartsWith("Circular"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Hostile_names_are_quoted(string engine)
    {
        var d = D(engine);
        var t = new ModelTable { Schema = "s]\"x", Name = "t]\"; DROP TABLE x; --", Columns = [Col("c]\"", "int")] };
        var sql = d.CreateTable(t);
        if (engine == "mssql")
        {
            Assert.Contains("[s]]\"x].[t]]\"; DROP TABLE x; --]", sql);
            Assert.Contains("[c]]\"]", sql);
        }
        else
        {
            Assert.Contains("\"s]\"\"x\".\"t]\"\"; DROP TABLE x; --\"", sql);
            Assert.Contains("\"c]\"\"\"", sql);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void Nothing_to_do_is_said_plainly(string engine)
    {
        var m = Model(engine, [Table(Dbo(engine), "t", Col("id", Int(engine), false))]);
        var r = Migrate(SchemaComparer.Compare(m, m, D(engine)));
        Assert.Equal(0, r.Statements);
        Assert.Contains("Nothing to change", r.Sql);
    }

    // ------------------------------------------------------------------ files, service, cache

    [Fact]
    public void Model_files_round_trip()
    {
        var m = Model("pg", [Table("public", "t", Col("id", "integer", false), Col("st", "status", dflt: "'new'::status"))],
            [View("pg", "v", "SELECT 1")], [new ModelSequence { Schema = "public", Name = "s", Type = "bigint", Start = "1", Increment = "1", Cycle = true }],
            [new ModelType { Schema = "public", Name = "status", Kind = "enum", Values = ["new", "done"] }]);
        var json = JsonSerializer.Serialize(m, Json.Context.SchemaModel);
        Assert.Contains("\"engine\":\"pg\"", json);
        var back = JsonSerializer.Deserialize(json, Json.Context.SchemaModel)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, Json.Context.SchemaModel));
        Assert.Equal(SchemaModel.CurrentFormat, back.Format);
    }

    [Fact]
    public async Task An_engine_without_schema_compare_says_so()
    {
        var store = LocalStore.InMemory();
        var profiles = new ProfileStore(store);
        var profile = profiles.Save(new Profile { Id = "", Name = "fake", Engine = Engine.Pg, Host = "h" });
        var provider = new MetadataRefreshTests.FakeProvider(new MetadataRefreshTests.SlowMetadata());
        var sessions = new SessionManager(profiles, new ProviderRegistry([provider]), new CapturingNotifier(), Log.Null);
        sessions.RememberSecret(profile.Id, "x");
        var metaStore = new MetadataStore(store);
        var metadata = new MetadataService(sessions, metaStore, new SchemaCache(metaStore), new CapturingNotifier(), Log.Null);
        var objects = new ObjectService(sessions, metadata);
        var service = new SchemaCompareService(sessions, objects, new ProviderRegistry([provider]), new DefinitionFetcher(sessions, objects, metadata, Log.Null), Log.Null);
        var sid = sessions.Open(new SessionOpenParams { ProfileId = profile.Id, Database = "db" }).SessionId;
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => service.RunAsync(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = sid }, Target = new CompareSide { SessionId = sid },
        }, CancellationToken.None));
        Assert.Equal("compareUnsupported", ex.Code);
        Assert.Contains("not available", ex.Message);
    }

    [Fact]
    public void Definition_cache_records_missing_and_encrypted_modules()
    {
        var store = new MetadataStore(LocalStore.InMemory());
        store.ApplyRefresh("p", "db", true,
            [new ObjectStamp(1, "dbo", "t", ObjectKind.Table, 1, null), new ObjectStamp(2, "dbo", "v", ObjectKind.View, 1, null),
             new ObjectStamp(3, "dbo", "p", ObjectKind.Proc, 1, null), new ObjectStamp(4, "dbo", "f", ObjectKind.Func, 1, null)],
            [], new MetadataBundle([], [], [], []), "16");
        Assert.Equal([2L, 3, 4], store.MissingDefinitionIds("p", "db").Order());
        store.CacheDefinitions("p", "db", [(2, "CREATE VIEW dbo.v AS SELECT 1"), (3, null)]);
        Assert.Equal([4L], store.MissingDefinitionIds("p", "db"));
        var defs = store.Definitions("p", "db");
        Assert.Equal("CREATE VIEW dbo.v AS SELECT 1", defs[2]);
        Assert.True(defs.ContainsKey(3) && defs[3] == null); // encrypted: recorded, not fetched again
        // A refresh that changes the view drops its cached text.
        store.ApplyRefresh("p", "db", false, [new ObjectStamp(2, "dbo", "v", ObjectKind.View, 2, null)], [], new MetadataBundle([], [], [], []), "16");
        Assert.Equal([2L, 4], store.MissingDefinitionIds("p", "db").Order());
    }

    [Theory, MemberData(nameof(Engines))]
    public void Foreign_keys_are_created_after_both_tables_and_dropped_before_dropped_tables(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var parent = Table(s, "parent", Col("id", i, false));
        var child = Table(s, "child", Col("id", i, false), Col("parent_id", i, false)) with
        {
            Fks = [new ModelFk { Name = "fk_child_parent", Columns = ["parent_id"], RefSchema = s, RefTable = "parent", RefColumns = ["id"] }],
        };

        // Creating both tables from scratch: both CREATE TABLE statements precede ADD CONSTRAINT fk_child_parent.
        var createCmp = SchemaComparer.Compare(Model(engine, [child, parent]), Model(engine, []), d);
        var createSql = Migrate(createCmp).Sql;
        var createParent = createSql.IndexOf(engine == "mssql" ? "CREATE TABLE [dbo].[parent]" : "CREATE TABLE \"public\".\"parent\"", StringComparison.Ordinal);
        var createChild = createSql.IndexOf(engine == "mssql" ? "CREATE TABLE [dbo].[child]" : "CREATE TABLE \"public\".\"child\"", StringComparison.Ordinal);
        var addFk = createSql.IndexOf("FOREIGN KEY", StringComparison.Ordinal);
        Assert.True(createParent >= 0 && createChild >= 0 && addFk > createParent && addFk > createChild, createSql);

        // Dropping parent while child stays: DROP CONSTRAINT fk_child_parent precedes DROP TABLE parent and warns.
        var childWithoutFk = Table(s, "child", Col("id", i, false), Col("parent_id", i, false));
        var dropCmp = SchemaComparer.Compare(Model(engine, [childWithoutFk]), Model(engine, [parent, child]), d);
        var dropRes = Migrate(dropCmp, dataLoss: true);
        var dropFk = dropRes.Sql.IndexOf(engine == "mssql" ? "DROP CONSTRAINT [fk_child_parent]" : "DROP CONSTRAINT \"fk_child_parent\"", StringComparison.Ordinal);
        var dropParent = dropRes.Sql.IndexOf(engine == "mssql" ? "DROP TABLE [dbo].[parent]" : "DROP TABLE \"public\".\"parent\"", StringComparison.Ordinal);
        Assert.True(dropFk >= 0 && dropParent > dropFk, dropRes.Sql);
    }

    [Fact]
    public void Postgres_overloaded_functions_compare_by_signature_independently()
    {
        var d = new PostgresSchemaCompare();
        var fInt = new ModelModule
        {
            Schema = "public", Name = "calc", Kind = ModuleKind.Function, Signature = "x integer", Returns = "integer",
            Definition = "CREATE OR REPLACE FUNCTION public.calc(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x + 1 $$;",
        };
        var fText = new ModelModule
        {
            Schema = "public", Name = "calc", Kind = ModuleKind.Function, Signature = "x text", Returns = "text",
            Definition = "CREATE OR REPLACE FUNCTION public.calc(x text) RETURNS text LANGUAGE sql AS $$ SELECT x $$;",
        };
        var fIntChanged = fInt with { Definition = "CREATE OR REPLACE FUNCTION public.calc(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x + 2 $$;" };

        var cmp = SchemaComparer.Compare(Model("pg", [], [fIntChanged, fText]), Model("pg", [], [fInt, fText]), d);
        var items = cmp.Items.Where(x => x.Kind == CompareObjectKind.Function).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(CompareClass.Different, items.Single(x => x.Detail == "x integer").Class);
        Assert.Equal(CompareClass.Identical, items.Single(x => x.Detail == "x text").Class);
    }

    // ------------------------------------------------------------------ review regressions (PARITY-IMPLEMENTATION-REVIEW)

    static int At(string sql, string what)
    {
        var k = sql.IndexOf(what, StringComparison.Ordinal);
        Assert.True(k >= 0, $"{what} missing in\n{sql}");
        return k;
    }

    [Theory, MemberData(nameof(Engines))]
    public void Dropping_linked_tables_together_drops_the_key_between_them_first(string engine)
    {
        // Orders sorts after OrderLines ('s' > 'L'), so the tables are dropped parent first: the key must already be gone.
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var parent = Table(s, "Orders", Col("id", i, false));
        var child = Table(s, "OrderLines", Col("id", i, false), Col("order_id", i, false)) with
        {
            Fks = [new ModelFk { Name = "fk_lines_orders", Columns = ["order_id"], RefSchema = s, RefTable = "Orders", RefColumns = ["id"] }],
        };
        var self = Table(s, "Tree", Col("id", i, false), Col("parent_id", i)) with
        {
            Fks = [new ModelFk { Name = "fk_tree_tree", Columns = ["parent_id"], RefSchema = s, RefTable = "Tree", RefColumns = ["id"] }],
        };
        var r = Migrate(SchemaComparer.Compare(Model(engine, []), Model(engine, [parent, child, self]), d), dataLoss: true);
        var q = engine == "mssql" ? (Func<string, string>)(n => $"[{n}]") : n => $"\"{n}\"";
        var dropFk = At(r.Sql, $"DROP CONSTRAINT {q("fk_lines_orders")}");
        Assert.True(dropFk < At(r.Sql, $"DROP TABLE {q(s)}.{q("Orders")}"), r.Sql);
        Assert.True(dropFk < At(r.Sql, $"DROP TABLE {q(s)}.{q("OrderLines")}"), r.Sql);
        Assert.DoesNotContain("fk_tree_tree", r.Sql); // a key to its own table goes with it
        Assert.DoesNotContain(r.Warnings, w => w.Contains("fk_lines_orders"));
    }

    [Fact]
    public void Sql_server_column_rename_runs_after_what_blocks_sp_rename()
    {
        var d = new SqlServerSchemaCompare();
        ModelTable T(string col, string? dfltName = "df_t_col") => Table("dbo", "t",
            Col("id", "int", false),
            Col(col, "date") with { Default = "(getdate())", DefaultName = dfltName },
            new ModelColumn { Name = "yr", Type = "", Computed = $"(datepart(year,[{col}]))", Uses = [col] },
            Col("status", "int")) with
        {
            Checks = [new ModelCheck { Name = "ck_dt", Expression = $"([{col}]>'2000-01-01')", Columns = [col] }],
            Indexes = [new ModelIndex { Name = "ix_status", Columns = ["status"], Filter = $"([{col}] IS NOT NULL)" }],
        };
        var v = new ModelModule
        {
            Schema = "dbo", Name = "v", Kind = ModuleKind.View, SchemaBound = true, References = [new ModelRef("dbo", "t")],
            Definition = "CREATE VIEW dbo.v WITH SCHEMABINDING AS SELECT id FROM dbo.t",
        };
        var c = SchemaComparer.Compare(Model("mssql", [T("order_date")], [v]), Model("mssql", [T("placed_at")], [v]), d);
        var sql = Migrate(c, renames: [c.Renames.Single()]).Sql;
        var rename = At(sql, "EXEC sp_rename");
        Assert.True(At(sql, "DROP CONSTRAINT [ck_dt]") < rename, sql);
        Assert.True(At(sql, "DROP COLUMN [yr]") < rename, sql);
        Assert.True(At(sql, "DROP INDEX [ix_status]") < rename, sql);
        Assert.True(At(sql, "DROP VIEW [dbo].[v]") < rename, sql);
        Assert.True(rename < At(sql, "ADD [yr] AS (datepart(year,[order_date]))"), sql);
        Assert.True(rename < At(sql, "ADD CONSTRAINT [ck_dt]"), sql);
        Assert.True(rename < At(sql, "NONCLUSTERED INDEX [ix_status]"), sql);
        Assert.True(rename < At(sql, "CREATE OR ALTER VIEW dbo.v"), sql);
    }

    [Fact]
    public void Postgres_column_rename_leaves_dependents_and_runs_before_steps_that_use_the_new_name()
    {
        var d = new PostgresSchemaCompare();
        var v = View("pg", "v", "SELECT id FROM t", false, new ModelRef("public", "t"));
        ModelTable T(string col, string dflt) => Table("public", "t", Col("id", "integer", false), Col(col, "date", true, dflt)) with
        {
            Indexes = [new ModelIndex { Name = "ix_t", Columns = ["id"], Filter = $"({col} IS NOT NULL)", Definition = $"CREATE INDEX ix_t ON public.t USING btree (id) WHERE ({col} IS NOT NULL)" }],
        };
        var c = SchemaComparer.Compare(Model("pg", [T("order_date", "now()")], [v]), Model("pg", [T("placed_at", "CURRENT_DATE")], [v]), d);
        var sql = Migrate(c, renames: [c.Renames.Single()]).Sql;
        var rename = At(sql, "RENAME COLUMN \"placed_at\" TO \"order_date\"");
        Assert.True(rename < At(sql, "ALTER COLUMN \"order_date\" DROP DEFAULT"), sql);
        Assert.True(rename < At(sql, "ALTER COLUMN \"order_date\" SET DEFAULT now()"), sql);
        Assert.DoesNotContain("DROP VIEW", sql); // PostgreSQL follows the rename itself
    }

    [Fact]
    public void Sql_server_matches_references_and_modules_whatever_their_case()
    {
        var d = new SqlServerSchemaCompare();
        // The view says dbo.ORDERS (sys.sql_expression_dependencies keeps the casing as written); the table is dbo.Orders.
        var bound = new ModelModule
        {
            Schema = "dbo", Name = "v_bound", Kind = ModuleKind.View, SchemaBound = true, References = [new ModelRef("DBO", "ORDERS")],
            Definition = "CREATE VIEW dbo.v_bound WITH SCHEMABINDING AS SELECT id, qty FROM DBO.ORDERS",
        };
        var c = SchemaComparer.Compare(
            Model("mssql", [Table("dbo", "Orders", Col("id", "int", false), Col("qty", "bigint"))], [bound]),
            Model("mssql", [Table("dbo", "Orders", Col("id", "int", false), Col("qty", "int"))], [bound]), d);
        var sql = Migrate(c).Sql;
        var alter = At(sql, "ALTER COLUMN [qty] bigint");
        Assert.True(At(sql, "DROP VIEW [dbo].[v_bound]") < alter, sql);
        Assert.True(alter < At(sql, "CREATE OR ALTER VIEW dbo.v_bound"), sql);

        // New modules: b_inner is referenced as DBO.B_INNER and must come before a_outer despite the name order.
        var inner = View("mssql", "b_inner", "SELECT 1 AS one");
        var outer = View("mssql", "a_outer", "SELECT one FROM DBO.B_INNER", false, new ModelRef("DBO", "B_INNER"));
        var created = Migrate(SchemaComparer.Compare(Model("mssql", [], [outer, inner]), Model("mssql", []), d));
        Assert.True(At(created.Sql, "dbo.b_inner") < At(created.Sql, "dbo.a_outer"), created.Sql);
        Assert.DoesNotContain(created.Warnings, w => w.StartsWith("Circular"));

        // A module whose name differs only in case between the sides is one module: altered, not created.
        var src = View("mssql", "MyView", "SELECT 2 AS two");
        var tgt = View("mssql", "myview", "SELECT 1 AS one");
        var altered = Migrate(SchemaComparer.Compare(Model("mssql", [], [src]), Model("mssql", [], [tgt]), d)).Sql;
        Assert.Contains("-- alter view dbo.MyView", altered);
        Assert.DoesNotContain("DROP VIEW", altered);
    }

    [Fact]
    public void Postgres_names_stay_case_sensitive()
    {
        var d = new PostgresSchemaCompare();
        // "ORDERS" is another table on PostgreSQL: the view on it is not touched by a change to "Orders".
        var other = View("pg", "v_other", "SELECT id FROM \"ORDERS\"", false, new ModelRef("public", "ORDERS"));
        var c = SchemaComparer.Compare(
            Model("pg", [Table("public", "Orders", Col("id", "integer", false), Col("qty", "bigint"))], [other]),
            Model("pg", [Table("public", "Orders", Col("id", "integer", false), Col("qty", "integer"))], [other]), d);
        Assert.DoesNotContain("v_other", Migrate(c).Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Filtered_indexes_on_a_retyped_or_dropped_column_are_handled(string engine)
    {
        var d = D(engine);
        var s = Dbo(engine);
        var i = Int(engine);
        var filter = engine == "mssql" ? "([status] IS NOT NULL)" : "(status IS NOT NULL)";
        var ix = new ModelIndex
        {
            Name = "ix_t_id", Columns = ["id"], Filter = filter,
            Definition = engine == "pg" ? "CREATE INDEX ix_t_id ON public.t USING btree (id) WHERE (status IS NOT NULL)" : null,
        };
        ModelTable T(string statusType) => Table(s, "t", Col("id", i, false), Col("status", statusType)) with { Indexes = [ix] };
        var sql = Migrate(SchemaComparer.Compare(Model(engine, [T("bigint")]), Model(engine, [T(i)]), d)).Sql;
        if (engine == "mssql")
        {
            var alter = At(sql, "ALTER COLUMN [status] bigint");
            Assert.True(At(sql, "DROP INDEX [ix_t_id]") < alter, sql);
            Assert.True(alter < At(sql, "NONCLUSTERED INDEX [ix_t_id]"), sql);
        }
        else Assert.DoesNotContain("DROP INDEX", sql); // PostgreSQL rebuilds the index itself

        // The column is dropped: SQL Server needs the index out of the way first; neither engine re-creates it.
        var dropped = Migrate(SchemaComparer.Compare(Model(engine, [Table(s, "t", Col("id", i, false))]), Model(engine, [T(i)]), d), dataLoss: true).Sql;
        var dropCol = At(dropped, engine == "mssql" ? "DROP COLUMN [status]" : "DROP COLUMN \"status\"");
        if (engine == "mssql") Assert.True(At(dropped, "DROP INDEX [ix_t_id]") < dropCol, dropped);
        Assert.DoesNotContain("NONCLUSTERED INDEX [ix_t_id]", dropped);
        Assert.DoesNotContain("CREATE INDEX ix_t_id", dropped);
    }

    [Theory]
    [InlineData("ALTER PROCEDURE dbo.p AS SELECT 1", 16, "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("/* saved */ alter view dbo.v AS SELECT 1", 16, "/* saved */ CREATE OR ALTER view dbo.v AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.p AS SELECT 1", 13, "CREATE PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT 1", 13, "CREATE VIEW dbo.v AS SELECT 1")]
    [InlineData("CREATE  OR  ALTER VIEW dbo.v AS SELECT 1", 16, "CREATE  OR  ALTER VIEW dbo.v AS SELECT 1")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 'ALTER'", 13, "CREATE VIEW dbo.v AS SELECT 'ALTER'")]
    public void Sql_server_module_header_creates_the_module_whatever_it_was_saved_as(string def, int major, string expected)
    {
        var d = new SqlServerSchemaCompare();
        var kind = def.Contains("PROC", StringComparison.OrdinalIgnoreCase) ? ModuleKind.Procedure : ModuleKind.View;
        Assert.Equal(expected, d.CreateModule(new ModelModule { Schema = "dbo", Name = kind == ModuleKind.View ? "v" : "p", Kind = kind, Definition = def }, major));
    }

    [Fact]
    public void Postgres_module_definitions_are_not_rewritten()
    {
        // PostgreSQL definitions come from pg_get_*def as CREATE OR REPLACE already; nothing to turn into a CREATE.
        var d = new PostgresSchemaCompare();
        const string def = "CREATE OR REPLACE VIEW public.v AS\n SELECT 1;";
        Assert.Equal(def, d.CreateModule(new ModelModule { Schema = "public", Name = "v", Kind = ModuleKind.View, Definition = def }, 16));
    }
}
