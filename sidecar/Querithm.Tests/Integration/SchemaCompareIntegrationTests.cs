using Apache.Arrow;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests.Integration;

/// <summary>
/// Schema compare on both fixture engines: a compare of two databases, the migration run on the target, and
/// a second compare that finds nothing left; the SQL Server NOEXEC guard and the PostgreSQL transaction; files.
/// </summary>
[Collection("schemacompare")]
public class SchemaCompareIntegrationTests
{
    const string Src = "querithm_cmp_src";
    const string Tgt = "querithm_cmp_tgt";

    public static TheoryData<string> Engines => TestEngines.Names;

    static async Task Ok(TestHost h, string sid, string sql)
    {
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)) + done.Error?.Message);
    }

    static async Task<long> Scalar(TestHost h, string sid, string sql)
    {
        var (done, b, id) = await h.RunAsync(sid, sql);
        Assert.True(done.State == ExecState.Done, string.Join("\n", h.Messages(id)));
        return b[0].Column(0) switch
        {
            Int32Array a => a.GetValue(0) ?? -1,
            Int64Array a => a.GetValue(0) ?? -1,
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>Fresh source and target databases; returns sessions on each.</summary>
    static async Task<(string Source, string Target)> DatabasesAsync(TestHost h, string engine)
    {
        if (engine == "mssql")
        {
            var (_, master) = await h.OpenMssqlAsync("master");
            foreach (var db in new[] { Src, Tgt })
                await Ok(h, master, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {db}; END; CREATE DATABASE {db};");
            return ((await h.OpenMssqlAsync(Src)).SessionId, (await h.OpenMssqlAsync(Tgt)).SessionId);
        }
        var (_, postgres) = await h.OpenPgAsync("postgres");
        foreach (var db in new[] { Src, Tgt })
        {
            await Ok(h, postgres, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            await Ok(h, postgres, $"CREATE DATABASE {db}");
        }
        return ((await h.OpenPgAsync(Src)).SessionId, (await h.OpenPgAsync(Tgt)).SessionId);
    }

    const string MssqlSource = """
        CREATE SCHEMA sales;
        GO
        CREATE SEQUENCE dbo.order_no AS bigint START WITH 1000 INCREMENT BY 1;
        CREATE TYPE dbo.Phone FROM nvarchar(20) NULL;
        GO
        CREATE TABLE dbo.customers (
          id int IDENTITY(1,1) NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
          name nvarchar(100) NOT NULL,
          email nvarchar(200) NULL CONSTRAINT uq_customers_email UNIQUE,
          phone dbo.Phone NULL,
          created datetime2 NOT NULL CONSTRAINT df_customers_created DEFAULT (sysutcdatetime()));
        CREATE TABLE dbo.orders (
          id int NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
          customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES dbo.customers (id),
          qty bigint NOT NULL CONSTRAINT ck_orders_qty CHECK (qty > 0),
          total AS (qty * 2),
          placed_at datetime2 NULL,
          status nvarchar(20) NOT NULL CONSTRAINT df_orders_status DEFAULT (N'new'));
        CREATE INDEX ix_orders_customer ON dbo.orders (customer_id) INCLUDE (qty);
        CREATE TABLE sales.regions (code char(2) NOT NULL CONSTRAINT pk_regions PRIMARY KEY, name nvarchar(50) NOT NULL);
        GO
        CREATE VIEW dbo.v_orders WITH SCHEMABINDING AS SELECT o.id, o.qty, c.name FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id;
        GO
        CREATE VIEW dbo.v_big AS SELECT id FROM dbo.v_orders WHERE qty > 10;
        GO
        CREATE FUNCTION dbo.f_double(@x int) RETURNS int AS BEGIN RETURN @x * 2; END;
        GO
        CREATE PROCEDURE dbo.p_orders AS SELECT id, qty, name FROM dbo.v_orders;
        GO
        """;

    const string MssqlTarget = """
        CREATE SEQUENCE dbo.order_no AS bigint START WITH 1000 INCREMENT BY 5;
        GO
        CREATE TABLE dbo.customers (
          id int IDENTITY(1,1) NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
          name nvarchar(50) NOT NULL,
          email nvarchar(200) NULL,
          created datetime2 NOT NULL CONSTRAINT df_customers_created DEFAULT (getdate()),
          legacy int NULL);
        CREATE TABLE dbo.orders (
          id int NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
          customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES dbo.customers (id),
          qty int NOT NULL CONSTRAINT ck_orders_qty CHECK (qty > 0),
          total AS (qty * 2),
          order_date datetime2 NULL,
          status nvarchar(20) NOT NULL CONSTRAINT df_orders_status DEFAULT (N'new'));
        CREATE INDEX ix_orders_customer ON dbo.orders (customer_id);
        CREATE TABLE dbo.old_stuff (id int NOT NULL PRIMARY KEY);
        INSERT dbo.customers (name, email, legacy) VALUES (N'Ada', N'ada@example.com', 1);
        INSERT dbo.orders (id, customer_id, qty, order_date) VALUES (1, 1, 3, '2026-10-21T10:00:00');
        GO
        CREATE VIEW dbo.v_orders WITH SCHEMABINDING AS SELECT o.id, o.qty, c.name FROM dbo.orders o JOIN dbo.customers c ON c.id = o.customer_id;
        GO
        CREATE VIEW dbo.v_big AS SELECT id FROM dbo.v_orders WHERE qty > 10;
        GO
        CREATE FUNCTION dbo.f_double(@x int) RETURNS int AS BEGIN   RETURN @x*2;   END;
        GO
        CREATE PROCEDURE dbo.p_orders AS SELECT id FROM dbo.v_orders;
        GO
        CREATE PROCEDURE dbo.p_old AS SELECT 1;
        GO
        """;

    const string PgSource = """
        CREATE SCHEMA sales;
        CREATE TYPE status AS ENUM ('new', 'paid', 'shipped');
        CREATE SEQUENCE order_no AS bigint START WITH 1000 INCREMENT BY 1;
        CREATE TABLE customers (id int GENERATED ALWAYS AS IDENTITY CONSTRAINT pk_customers PRIMARY KEY, name varchar(100) NOT NULL,
          email text CONSTRAINT uq_customers_email UNIQUE, created timestamptz NOT NULL DEFAULT now());
        CREATE TABLE orders (id int CONSTRAINT pk_orders PRIMARY KEY, customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES customers (id),
          qty bigint NOT NULL CONSTRAINT ck_orders_qty CHECK (qty > 0), total bigint GENERATED ALWAYS AS (qty * 2) STORED, placed_at timestamptz,
          st status NOT NULL DEFAULT 'new');
        CREATE INDEX ix_orders_customer ON orders (customer_id) INCLUDE (qty);
        CREATE TABLE sales.regions (code char(2) CONSTRAINT pk_regions PRIMARY KEY, name text NOT NULL);
        CREATE VIEW v_orders AS SELECT o.id, o.qty, c.name FROM orders o JOIN customers c ON c.id = o.customer_id;
        CREATE VIEW v_big AS SELECT id FROM v_orders WHERE qty > 10;
        CREATE FUNCTION f_double(x int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN x * 2; END $$;
        CREATE FUNCTION f_count() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM orders $$;
        CREATE FUNCTION trg_orders() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.qty := greatest(NEW.qty, 1); RETURN NEW; END $$;
        CREATE TRIGGER t_orders BEFORE INSERT ON orders FOR EACH ROW EXECUTE FUNCTION trg_orders();
        """;

    const string PgTarget = """
        CREATE TYPE status AS ENUM ('new', 'shipped');
        CREATE SEQUENCE order_no AS bigint START WITH 1000 INCREMENT BY 5;
        CREATE TABLE customers (id int GENERATED ALWAYS AS IDENTITY CONSTRAINT pk_customers PRIMARY KEY, name varchar(50) NOT NULL,
          email text, created timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP, legacy int);
        CREATE TABLE orders (id int CONSTRAINT pk_orders PRIMARY KEY, customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES customers (id),
          qty int NOT NULL CONSTRAINT ck_orders_qty CHECK (qty > 0), total bigint GENERATED ALWAYS AS (qty * 2) STORED, order_date timestamptz,
          st status NOT NULL DEFAULT 'new');
        CREATE INDEX ix_orders_customer ON orders (customer_id);
        CREATE TABLE old_stuff (id int PRIMARY KEY);
        INSERT INTO customers (name, email, legacy) VALUES ('Ada', 'ada@example.com', 1);
        INSERT INTO orders (id, customer_id, qty, order_date) VALUES (1, 1, 3, '2026-10-21 10:00:00+00');
        CREATE VIEW v_orders AS SELECT o.id, o.qty, c.name FROM orders o JOIN customers c ON c.id = o.customer_id;
        CREATE VIEW v_big AS SELECT id FROM v_orders WHERE qty > 10;
        CREATE FUNCTION f_double(x int) RETURNS int LANGUAGE plpgsql AS $$ BEGIN   RETURN x*2;   END $$;
        CREATE FUNCTION f_count() RETURNS int LANGUAGE sql AS $$ SELECT count(*)::int FROM orders $$;
        CREATE FUNCTION f_old() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
        """;

    static CompareItem Item(SchemaCompareResult r, CompareObjectKind kind, string name) =>
        r.Items.Single(i => i.Kind == kind && i.Name == name);

    static string Describe(SchemaCompareResult r) =>
        string.Join("\n", r.Items.Where(i => i.Class != CompareClass.Identical).Select(i => $"{i.Key} {i.Class} [{string.Join(",", i.Tags)}] {string.Join("; ", i.Changes)} {i.Note}"));

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Compare_migrate_and_compare_again_finds_nothing(string engine)
    {
        await using var h = new TestHost();
        var (src, tgt) = await DatabasesAsync(h, engine);
        var ms = engine == "mssql";
        await Ok(h, src, ms ? MssqlSource : PgSource);
        await Ok(h, tgt, ms ? MssqlTarget : PgTarget);
        var dbo = ms ? "dbo" : "public";

        var r = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = src, Database = Src }, Target = new CompareSide { SessionId = tgt, Database = Tgt },
        });
        var all = Describe(r);
        Assert.Null(r.MigrationRefusal);
        Assert.True(r.TransactionalDdl);
        Assert.Equal(CompareClass.SourceOnly, Item(r, CompareObjectKind.Schema, "sales").Class);
        Assert.Equal(CompareClass.Identical, Item(r, CompareObjectKind.Schema, dbo).Class);
        Assert.Equal(CompareClass.Different, Item(r, CompareObjectKind.Sequence, "order_no").Class);
        var customers = Item(r, CompareObjectKind.Table, "customers");
        Assert.Equal(CompareClass.Different, customers.Class);
        Assert.Contains(DiffTag.Columns, customers.Tags);
        Assert.Contains(DiffTag.Constraints, customers.Tags);
        Assert.True(customers.DataLoss, all);
        var orders = Item(r, CompareObjectKind.Table, "orders");
        Assert.Contains(DiffTag.Indexes, orders.Tags);
        Assert.Equal(CompareClass.TargetOnly, Item(r, CompareObjectKind.Table, "old_stuff").Class);
        Assert.Equal(CompareClass.SourceOnly, Item(r, CompareObjectKind.Table, "regions").Class);
        Assert.Equal(CompareClass.Identical, Item(r, CompareObjectKind.View, "v_orders").Class);
        Assert.Equal(CompareClass.Identical, Item(r, CompareObjectKind.View, "v_big").Class);
        var dbl = Item(r, CompareObjectKind.Function, "f_double");
        Assert.Equal([DiffTag.WhitespaceOnly], dbl.Tags);
        var rename = Assert.Single(r.PossibleRenames);
        Assert.Equal(("order_date", "placed_at"), (rename.From, rename.To));
        if (ms)
        {
            Assert.Equal(CompareClass.SourceOnly, Item(r, CompareObjectKind.Type, "Phone").Class);
            Assert.Equal([DiffTag.Body], Item(r, CompareObjectKind.Procedure, "p_orders").Tags);
            Assert.Equal(CompareClass.TargetOnly, Item(r, CompareObjectKind.Procedure, "p_old").Class);
        }
        else
        {
            Assert.Equal(CompareClass.Different, Item(r, CompareObjectKind.Type, "status").Class);
            Assert.Equal([DiffTag.Body], Item(r, CompareObjectKind.Function, "f_count").Tags);
            Assert.Equal(CompareClass.TargetOnly, Item(r, CompareObjectKind.Function, "f_old").Class);
            Assert.Equal(CompareClass.SourceOnly, Item(r, CompareObjectKind.Trigger, "t_orders").Class);
        }

        // The diff view scripts both sides from the same generator.
        var sql = await h.Rpc.SchemaCompareObjectSql(new CompareObjectParams { CompareId = r.CompareId, Key = customers.Key });
        Assert.Contains("customers", sql.Source);
        Assert.Contains("legacy", sql.Target);
        Assert.DoesNotContain("legacy", sql.Source);

        // Without data loss: drops are listed, not scripted.
        var keys = r.Items.Where(i => i.Class != CompareClass.Identical).Select(i => i.Key).ToArray();
        var safe = await h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = r.CompareId, Keys = keys });
        Assert.Contains(safe.Skipped, s => s.Contains("old_stuff"));
        Assert.Contains(safe.Skipped, s => s.Contains("legacy"));
        Assert.DoesNotContain("old_stuff", safe.Sql.Split('\n').Where(l => !l.StartsWith("--")).Aggregate("", (a, b) => a + b + "\n"));

        var mig = await h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = r.CompareId, Keys = keys, Renames = [rename], IncludeDataLoss = true });
        Assert.Empty(mig.Skipped);
        if (ms) Assert.Contains(SqlServerSchemaCompare.Guard, mig.Sql);
        else Assert.True(mig.Sql.IndexOf("ADD VALUE", StringComparison.Ordinal) < mig.Sql.IndexOf("BEGIN;", StringComparison.Ordinal), mig.Sql);
        await Ok(h, tgt, mig.Sql);

        // The renamed column kept its data.
        Assert.Equal(1, await Scalar(h, tgt, ms
            ? "SELECT COUNT(*) FROM dbo.orders WHERE placed_at = '2026-10-21T10:00:00'"
            : "SELECT count(*)::int FROM orders WHERE placed_at = '2026-10-21 10:00:00+00'"));

        var again = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = src, Database = Src }, Target = new CompareSide { SessionId = tgt, Database = Tgt },
        });
        Assert.True(again.Items.All(i => i.Class == CompareClass.Identical), Describe(again) + "\n\n" + mig.Sql);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_failing_statement_rolls_back_the_whole_migration(string engine)
    {
        await using var h = new TestHost();
        var (_, tgt) = await DatabasesAsync(h, engine);
        var ms = engine == "mssql";
        ISchemaCompareDialect d = ms ? new SqlServerSchemaCompare() : new PostgresSchemaCompare();
        var exists = ms ? "SELECT COUNT(*) FROM sys.tables WHERE name IN ('qa','qb','qc')" : "SELECT count(*)::int FROM pg_class WHERE relname IN ('qa','qb','qc')";

        // A compile error in the third batch: on SQL Server the guard after it rolls back and skips the rest (NOEXEC).
        var broken = ms ? "CREATE PROCEDURE dbo.broken AS SELECT FROM;" : "CREATE FUNCTION broken() RETURNS int LANGUAGE sql AS $$ SELECT nope $$;";
        MigrationStep S(string sql) => new(MigrationPhase.Tables, sql, sql);
        var script = d.Wrap([S("CREATE TABLE qa (id int);"), S("CREATE TABLE qb (id int);"), S(broken), S("CREATE TABLE qc (id int);")], "-- test");
        await h.RunAsync(tgt, script);
        if (!ms) await h.RunAsync(tgt, "ROLLBACK"); // PostgreSQL stops at the error inside the open transaction
        Assert.Equal(0, await Scalar(h, tgt, exists));

        // A runtime error: XACT_ABORT rolls back, and the guard sees @@TRANCOUNT = 0.
        var fails = ms ? "SELECT 1 / 0;" : "SELECT 1 / 0;";
        script = d.Wrap([S("CREATE TABLE qa (id int);"), S(fails), S("CREATE TABLE qb (id int);")], "-- test");
        await h.RunAsync(tgt, script);
        if (!ms) await h.RunAsync(tgt, "ROLLBACK");
        Assert.Equal(0, await Scalar(h, tgt, exists));
        // NOEXEC is off again: the session works normally.
        Assert.Equal(0, await Scalar(h, tgt, exists));
        await Ok(h, tgt, "CREATE TABLE qc (id int);");
        Assert.Equal(1, await Scalar(h, tgt, exists));
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task Two_schemas_of_one_database(string engine)
    {
        await using var h = new TestHost();
        var (src, _) = await DatabasesAsync(h, engine);
        var ms = engine == "mssql";
        await Ok(h, src, ms
            ? """
              CREATE SCHEMA qa;
              GO
              CREATE SCHEMA qb;
              GO
              CREATE TABLE qa.items (id int NOT NULL PRIMARY KEY, label nvarchar(40) NOT NULL);
              CREATE TABLE qb.items (id int NOT NULL PRIMARY KEY);
              GO
              CREATE VIEW qa.v_items AS SELECT i.id, i.label FROM qa.items i;
              GO
              """
            : """
              CREATE SCHEMA qa; CREATE SCHEMA qb;
              CREATE TABLE qa.items (id int PRIMARY KEY, label text NOT NULL);
              CREATE TABLE qb.items (id int PRIMARY KEY);
              CREATE VIEW qa.v_items AS SELECT i.id, i.label FROM qa.items i;
              """);
        var r = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = src, Database = Src, Schema = "qa" }, Target = new CompareSide { SessionId = src, Database = Src, Schema = "qb" },
        });
        Assert.Equal(CompareClass.Different, Item(r, CompareObjectKind.Table, "items").Class);
        Assert.Equal("qb", Item(r, CompareObjectKind.Table, "items").Schema);
        Assert.Equal(CompareClass.SourceOnly, Item(r, CompareObjectKind.View, "v_items").Class);
        var mig = await h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = r.CompareId, Keys = r.Items.Select(i => i.Key).ToArray() });
        // Everything lands in the target schema, references included.
        Assert.DoesNotContain(ms ? "[qa]" : "\"qa\"", mig.Sql);
        Assert.DoesNotContain("qa.", mig.Sql);
        await Ok(h, src, mig.Sql);
        var again = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = src, Database = Src, Schema = "qa" }, Target = new CompareSide { SessionId = src, Database = Src, Schema = "qb" },
        });
        Assert.True(again.Items.All(i => i.Class == CompareClass.Identical), Describe(again) + "\n\n" + mig.Sql);
    }

    [IntegrationTheory, MemberData(nameof(Engines))]
    public async Task A_saved_model_file_compares_but_cannot_be_migrated_into(string engine)
    {
        await using var h = new TestHost();
        var (src, tgt) = await DatabasesAsync(h, engine);
        var ms = engine == "mssql";
        await Ok(h, src, ms ? MssqlSource : PgSource);
        await Ok(h, tgt, ms ? MssqlTarget : PgTarget);
        var file = await h.Rpc.SchemaCompareExportModel(new SchemaExportParams { SessionId = src, Database = Src });
        Assert.EndsWith(".qschema.json", file.FileName);
        Assert.StartsWith(Src, file.FileName);
        var path = Path.Combine(h.DataDir, file.FileName);
        await File.WriteAllTextAsync(path, file.Json);

        var fromFile = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { FileToken = "t", FilePath = path }, Target = new CompareSide { SessionId = tgt, Database = Tgt },
        });
        Assert.False(fromFile.Source.Live);
        Assert.Null(fromFile.MigrationRefusal);
        Assert.Equal(CompareClass.TargetOnly, Item(fromFile, CompareObjectKind.Table, "old_stuff").Class);

        var intoFile = await h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = tgt, Database = Tgt }, Target = new CompareSide { FileToken = "t", FilePath = path },
        });
        Assert.NotNull(intoFile.MigrationRefusal);
        var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() =>
            h.Rpc.SchemaCompareMigrationSql(new SchemaMigrationParams { CompareId = intoFile.CompareId, Keys = [] }));
        Assert.Equal("compareTarget", ex.Code);

        await File.WriteAllTextAsync(path, "{\"not\": \"a model\"");
        var bad = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { FileToken = "t", FilePath = path }, Target = new CompareSide { SessionId = tgt, Database = Tgt },
        }));
        Assert.Equal("compareFile", bad.Code);
    }

    [IntegrationFact]
    public async Task Sides_on_different_engines_are_refused_with_a_reason()
    {
        await using var h = new TestHost();
        var (_, ms) = await h.OpenMssqlAsync("master");
        var (_, pg) = await h.OpenPgAsync("postgres");
        var ex = await Assert.ThrowsAsync<Core.QuerithmException>(() => h.Rpc.SchemaCompareRun(new SchemaCompareParams
        {
            Source = new CompareSide { SessionId = ms }, Target = new CompareSide { SessionId = pg },
        }));
        Assert.Equal("compareEngines", ex.Code);
        Assert.Contains("same engine", ex.Message);
    }
}
