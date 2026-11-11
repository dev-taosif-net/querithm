using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Sessions;
using Querithm.Core.Store;
using Querithm.Host;
using Querithm.Providers.Oracle;

namespace Querithm.Tests.Integration;

/// <summary>
/// D4 S05 against the Oracle Free fixture (QUERITHM_IT=1): the catalog as the least-privileged user (its own schema plus
/// the schema-only account querithm_own, which granted it a table, a view and a procedure) and as the catalog-role user.
/// Every object kind the provider lists, case-distinct quoted names, a synonym into another schema, markers that move
/// with DDL and not with DML or statistics, GET_DDL and its ALL_SOURCE / ALL_VIEWS fallback, statistics row counts, and
/// a dictionary view the login cannot read (a real ORA-00942: the test renames the view) as a note, not a failure.
/// </summary>
public sealed class OracleMetadataIntegrationTests
{
    const string App = OracleIntegrationTests.AppUser, Mon = OracleIntegrationTests.MonitorUser;
    const string AppSchema = "QUERITHM_APP", Own = "QUERITHM_OWN";

    static Profile ProfileOf(string user) => new()
    {
        Id = "", Name = "ora-meta", Engine = Engine.Oracle, Host = "127.0.0.1", Port = OracleIntegrationTests.Port, Auth = AuthKind.Password, Username = user,
        DatabaseDefault = OracleIntegrationTests.Service, TlsMode = TlsMode.Disable, TimeoutS = 30,
    };

    static string Secret(string user) => FixtureSecrets.Password(user == App ? "oracle" : "oracle-mon");

    static async Task<DbConnection> OpenAsync(string user = App)
    {
        var dir = Directory.CreateTempSubdirectory("qora-meta-").FullName;
        return await new OracleProvider(dir).OpenAsync(ProfileOf(user), Secret(user), null, null, Timeout());
    }

    static CancellationToken Timeout(int seconds = 120) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    static async Task ExecAsync(DbConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Timeout());
    }

    /// <summary>Drops the object if it is there (ORA-00942 / -04043 / -02289 / -01434 / -12003 when it is not).</summary>
    static Task DropAsync(DbConnection c, string kind, string name) => ExecAsync(c, $"""
        BEGIN EXECUTE IMMEDIATE 'DROP {kind} {name}{(kind == "TABLE" ? " CASCADE CONSTRAINTS PURGE" : "")}';
        EXCEPTION WHEN OTHERS THEN IF SQLCODE NOT IN (-942, -4043, -2289, -1434, -12003, -4080) THEN RAISE; END IF; END;
        """);

    static readonly SemaphoreSlim Once = new(1, 1);
    static bool _made;

    /// <summary>The fixture objects every catalog test reads, made once per run (dropped first: a previous run's are replaced).</summary>
    static async Task SchemaAsync()
    {
        await Once.WaitAsync();
        try
        {
            if (_made) return;
            await using var c = await OpenAsync();
            foreach (var (kind, name) in new[]
                     {
                         ("MATERIALIZED VIEW", "Q_S05_MV"), ("VIEW", "Q_S05_V"), ("TABLE", "\"Q_S05_Sales\""), ("TABLE", "Q_S05_SALES"), ("TABLE", "Q_S05_DEPT"),
                         ("TABLE", "Q_S05_EXT"), ("TABLE", "Q_S05_BIN"), ("SEQUENCE", "Q_S05_SEQ"), ("SYNONYM", "Q_S05_SYN"), ("PROCEDURE", "Q_S05_PROC"),
                         ("FUNCTION", "Q_S05_FN"), ("FUNCTION", "Q_S05_BAD"), ("TYPE", "Q_S05_PT"),
                     })
                await DropAsync(c, kind, name);
            await ExecAsync(c, """
                CREATE TABLE q_s05_dept (
                  id NUMBER(10) PRIMARY KEY,
                  name VARCHAR2(40 CHAR) NOT NULL CONSTRAINT q_s05_dept_name UNIQUE,
                  created DATE DEFAULT SYSDATE,
                  code CHAR(3) CONSTRAINT q_s05_dept_code UNIQUE DEFERRABLE INITIALLY DEFERRED)
                """);
            await ExecAsync(c, """
                CREATE TABLE "Q_S05_Sales" (
                  id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                  amt NUMBER(12,2) CONSTRAINT q_s05_amt_ck CHECK (amt >= 0),
                  dept_id NUMBER(10) CONSTRAINT q_s05_sales_dept REFERENCES q_s05_dept (id) DEFERRABLE INITIALLY DEFERRED,
                  twice NUMBER GENERATED ALWAYS AS (amt * 2) VIRTUAL,
                  secret VARCHAR2(10) INVISIBLE)
                """);
            await ExecAsync(c, "CREATE TABLE q_s05_sales (id NUMBER PRIMARY KEY, raw_bytes RAW(16), f FLOAT(126), ts TIMESTAMP(3))");
            await ExecAsync(c, "CREATE INDEX q_s05_sales_fx ON \"Q_S05_Sales\" (UPPER(secret))");
            await ExecAsync(c, "CREATE TABLE q_s05_bin (id NUMBER)");
            await ExecAsync(c, "DROP TABLE q_s05_bin"); // into the recycle bin (on by default), never listed
            await ExecAsync(c, "CREATE TABLE q_s05_ext (x VARCHAR2(10)) ORGANIZATION EXTERNAL (TYPE ORACLE_LOADER DEFAULT DIRECTORY querithm_ext LOCATION ('none.txt'))");
            await ExecAsync(c, "CREATE VIEW q_s05_v AS SELECT id, name FROM q_s05_dept");
            await ExecAsync(c, "CREATE MATERIALIZED VIEW q_s05_mv AS SELECT id, name FROM q_s05_dept");
            await ExecAsync(c, "CREATE SEQUENCE q_s05_seq START WITH 5 INCREMENT BY 5");
            await ExecAsync(c, $"CREATE SYNONYM q_s05_syn FOR {Own}.shared_t");
            await ExecAsync(c, "CREATE PROCEDURE q_s05_proc (p_id IN NUMBER, p_out OUT VARCHAR2, p_d IN NUMBER DEFAULT 1) AS BEGIN p_out := 'x'; END;");
            await ExecAsync(c, "CREATE FUNCTION q_s05_fn (a NUMBER) RETURN VARCHAR2 AS BEGIN RETURN 'y'; END;");
            await ExecAsync(c, "CREATE FUNCTION q_s05_bad RETURN NUMBER AS BEGIN RETURN nope; END;"); // compiles with errors: INVALID
            await ExecAsync(c, "CREATE TRIGGER q_s05_trg BEFORE INSERT ON q_s05_dept FOR EACH ROW BEGIN :NEW.created := SYSDATE; END;");
            await ExecAsync(c, "CREATE TYPE q_s05_pt AS OBJECT (x NUMBER, y NUMBER)");
            _made = true;
        }
        finally { Once.Release(); }
    }

    static ObjectStamp One(IReadOnlyList<ObjectStamp> stamps, string schema, string name) =>
        Assert.Single(stamps, s => s.Schema == schema && s.Name == name);

    [IntegrationFact]
    public async Task The_snapshot_lists_every_object_kind_the_fixture_made_with_exact_names_and_notes()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        var stamps = await meta.GetStampsAsync(c, Timeout());
        Assert.Empty(meta.Notes(c));

        var dept = One(stamps, AppSchema, "Q_S05_DEPT");
        Assert.Equal(ObjectKind.Table, dept.Kind);
        // "Q_S05_Sales" and Q_S05_SALES are two tables, each with its own id.
        Assert.NotEqual(One(stamps, AppSchema, "Q_S05_Sales").Id, One(stamps, AppSchema, "Q_S05_SALES").Id);
        Assert.Equal(ObjectKind.View, One(stamps, AppSchema, "Q_S05_V").Kind);
        var mv = One(stamps, AppSchema, "Q_S05_MV");
        Assert.Equal((ObjectKind.View, "materialized view"), (mv.Kind, mv.Note)); // its container table is not listed again (Single above)
        Assert.Equal("external table", One(stamps, AppSchema, "Q_S05_EXT").Note);
        Assert.DoesNotContain(stamps, s => s.Name.StartsWith("BIN$", StringComparison.Ordinal) || s.Name == "Q_S05_BIN");
        Assert.Equal(ObjectKind.Sequence, One(stamps, AppSchema, "Q_S05_SEQ").Kind);
        Assert.DoesNotContain(stamps, s => s.Name.StartsWith("ISEQ$$", StringComparison.Ordinal)); // an identity column's own sequence
        Assert.Equal(ObjectKind.Synonym, One(stamps, AppSchema, "Q_S05_SYN").Kind);
        Assert.Equal(ObjectKind.Proc, One(stamps, AppSchema, "Q_S05_PROC").Kind);
        var fn = One(stamps, AppSchema, "Q_S05_FN");
        Assert.Equal((ObjectKind.Func, "VARCHAR2", (string?)null), (fn.Kind, fn.ReturnType, fn.Note));
        Assert.Equal("invalid", One(stamps, AppSchema, "Q_S05_BAD").Note);
        var trg = One(stamps, AppSchema, "Q_S05_TRG");
        Assert.Equal((ObjectKind.Trigger, dept.Id), (trg.Kind, trg.ParentObjectId));
        Assert.Equal(ObjectKind.Type, One(stamps, AppSchema, "Q_S05_PT").Kind);
        // Another schema's objects the user was granted are a namespace of their own; Oracle's own schemas are not listed.
        Assert.Equal(ObjectKind.Table, One(stamps, Own, "SHARED_T").Kind);
        Assert.Equal(ObjectKind.View, One(stamps, Own, "SHARED_V").Kind);
        Assert.Equal(ObjectKind.Proc, One(stamps, Own, "SHARED_P").Kind);
        Assert.DoesNotContain(stamps, s => s.Schema is "SYS" or "SYSTEM" or "XDB" or "MDSYS" or "PUBLIC");
        Assert.Equal(stamps.Count, stamps.Select(s => s.Id).Distinct().Count());

        var details = await meta.GetDetailsAsync(c, stamps.Select(s => s.Id).ToList(), Timeout());
        Assert.Empty(meta.Notes(c));
        var sales = One(stamps, AppSchema, "Q_S05_Sales").Id;
        var cols = details.Columns.Where(x => x.ObjectId == sales).ToDictionary(x => x.Name);
        Assert.True(cols["ID"].Identity);
        Assert.Equal("NUMBER(12,2)", cols["AMT"].TypeName);
        Assert.True(cols["TWICE"].Computed);
        Assert.True(cols["SECRET"].Ordinal >= OracleMetadata.InvisibleOrdinalBase);
        Assert.Equal("VARCHAR2(10 BYTE) invisible", cols["SECRET"].TypeName);
        Assert.DoesNotContain(cols.Keys, k => k.StartsWith("SYS_", StringComparison.Ordinal)); // the function-based index's hidden column
        var deptCols = details.Columns.Where(x => x.ObjectId == dept.Id).ToDictionary(x => x.Name);
        Assert.Equal(("VARCHAR2(40 CHAR)", 40, false), (deptCols["NAME"].TypeName, deptCols["NAME"].MaxLen, deptCols["NAME"].Nullable));
        Assert.Equal("SYSDATE", deptCols["CREATED"].Default);
        var other = details.Columns.Where(x => x.ObjectId == One(stamps, AppSchema, "Q_S05_SALES").Id).ToDictionary(x => x.Name);
        Assert.Equal(("RAW(16)", "FLOAT(126)", "TIMESTAMP(3)"), (other["RAW_BYTES"].TypeName, other["F"].TypeName, other["TS"].TypeName));
        Assert.Contains(details.Columns, x => x.ObjectId == mv.Id && x.Name == "NAME");
        Assert.Contains(details.Columns, x => x.ObjectId == One(stamps, AppSchema, "Q_S05_V").Id && x.Name == "ID");

        var idx = details.Indexes.Where(i => i.ObjectId == dept.Id).ToList();
        Assert.Contains(idx, i => i.Primary && i.Columns.SequenceEqual(["ID"]) && !i.NotAKey);
        Assert.Contains(idx, i => i.Name == "Q_S05_DEPT_NAME" && i.Unique && !i.Primary && !i.NotAKey);
        Assert.Contains(idx, i => i.Name == "Q_S05_DEPT_CODE" && i.NotAKey); // initially deferred: violated until commit
        var fx = Assert.Single(details.Indexes, i => i.Name == "Q_S05_SALES_FX");
        Assert.Equal(["(expression)"], fx.Columns);
        Assert.True(fx.NotAKey);
        var fk = Assert.Single(details.Fks, f => f.Name == "Q_S05_SALES_DEPT");
        Assert.Equal((sales, dept.Id, "DEPT_ID", "ID"), (fk.Parent, fk.Referenced, fk.Columns[0].Parent, fk.Columns[0].Referenced));

        var p = details.Params.Where(x => x.ObjectId == One(stamps, AppSchema, "Q_S05_PROC").Id).OrderBy(x => x.Ordinal).ToList();
        Assert.Equal(["P_ID", "P_OUT", "P_D"], p.Select(x => x.Name));
        Assert.Equal((false, true, false), (p[0].Output, p[1].Output, p[2].Output));
        Assert.Equal((false, true), (p[0].HasDefault, p[2].HasDefault));
        Assert.Equal("NUMBER", p[0].TypeName);
    }

    [IntegrationFact]
    public async Task A_marker_moves_with_ALTER_TABLE_ADD_and_CREATE_INDEX_but_not_with_DML_or_statistics()
    {
        await using var c = await OpenAsync();
        await DropAsync(c, "TABLE", "Q_S05_MARK");
        await ExecAsync(c, "CREATE TABLE q_s05_mark (id NUMBER PRIMARY KEY, v VARCHAR2(20))");
        var meta = new OracleMetadata();
        async Task<long> MarkerAsync() => One(await meta.GetStampsAsync(c, Timeout()), AppSchema, "Q_S05_MARK").Marker;

        var start = await MarkerAsync();
        await ExecAsync(c, "INSERT INTO q_s05_mark VALUES (1, 'a')");
        await ExecAsync(c, "UPDATE q_s05_mark SET v = 'b'");
        await ExecAsync(c, "COMMIT");
        await ExecAsync(c, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'Q_S05_MARK'); END;");
        Assert.Equal(start, await MarkerAsync());

        await ExecAsync(c, "ALTER TABLE q_s05_mark ADD (w NUMBER)");
        var added = await MarkerAsync();
        Assert.NotEqual(start, added);
        await ExecAsync(c, "CREATE INDEX q_s05_mark_v ON q_s05_mark (v)");
        var indexed = await MarkerAsync();
        Assert.NotEqual(added, indexed);
        await ExecAsync(c, "ALTER TABLE q_s05_mark ADD CONSTRAINT q_s05_mark_ck CHECK (w > 0)");
        Assert.NotEqual(indexed, await MarkerAsync());
        await DropAsync(c, "TABLE", "Q_S05_MARK");
    }

    [IntegrationFact]
    public async Task A_PLSQL_marker_moves_with_its_source_and_its_trigger_enable_state()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        async Task<(long Proc, long Trigger, long Table)> MarkersAsync()
        {
            var s = await meta.GetStampsAsync(c, Timeout());
            return (One(s, AppSchema, "Q_S05_FN").Marker, One(s, AppSchema, "Q_S05_TRG").Marker, One(s, AppSchema, "Q_S05_DEPT").Marker);
        }
        var start = await MarkersAsync();
        await ExecAsync(c, "CREATE OR REPLACE FUNCTION q_s05_fn (a NUMBER) RETURN VARCHAR2 AS BEGIN RETURN 'z'; END;");
        await ExecAsync(c, "ALTER TRIGGER q_s05_trg DISABLE");
        var after = await MarkersAsync();
        await ExecAsync(c, "ALTER TRIGGER q_s05_trg ENABLE");
        await ExecAsync(c, "CREATE OR REPLACE FUNCTION q_s05_fn (a NUMBER) RETURN VARCHAR2 AS BEGIN RETURN 'y'; END;");
        Assert.NotEqual(start.Proc, after.Proc);
        Assert.NotEqual(start.Trigger, after.Trigger);
        Assert.NotEqual(start.Table, after.Table); // the table's triggers are part of its marker
        Assert.Equal(start, await MarkersAsync());
    }

    [IntegrationFact]
    public async Task Definitions_come_from_GET_DDL_without_storage_clauses_and_from_the_source_when_GET_DDL_is_refused()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata();
        var table = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S05_DEPT", Kind = ObjectKind.Table }, Timeout());
        Assert.StartsWith("CREATE TABLE \"QUERITHM_APP\".\"Q_S05_DEPT\"", table);
        Assert.DoesNotContain("STORAGE", table);
        Assert.DoesNotContain("TABLESPACE", table);
        Assert.EndsWith(";", table!.TrimEnd());
        var proc = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S05_PROC", Kind = ObjectKind.Proc }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PROCEDURE \"QUERITHM_APP\".\"Q_S05_PROC\"", proc);
        Assert.EndsWith("/", proc!.TrimEnd());
        var mv = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S05_MV", Kind = ObjectKind.View }, Timeout());
        Assert.StartsWith("CREATE MATERIALIZED VIEW \"QUERITHM_APP\".\"Q_S05_MV\"", mv);
        var syn = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = AppSchema, Name = "Q_S05_SYN", Kind = ObjectKind.Synonym }, Timeout());
        Assert.Contains("FOR \"QUERITHM_OWN\".\"SHARED_T\"", syn);

        // Another schema's procedure the user may only execute: GET_DDL answers ORA-31603, ALL_SOURCE has the text. The name
        // is qualified so that running it again replaces that procedure, not one in the user's own schema.
        var shared = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_P", Kind = ObjectKind.Proc }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE QUERITHM_OWN.SHARED_P", shared);
        Assert.EndsWith("/", shared!.TrimEnd());
        var view = await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_V", Kind = ObjectKind.View }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE VIEW QUERITHM_OWN.SHARED_V AS", view);
        Assert.Contains("FROM querithm_own.shared_t", view, StringComparison.OrdinalIgnoreCase);
        // A table has no source to fall back to.
        Assert.Null(await meta.GetDefinitionAsync(c, new ObjectRef { Schema = Own, Name = "SHARED_T", Kind = ObjectKind.Table }, Timeout()));

        // The catalog-role user may call GET_DDL for it.
        await using var m = await OpenAsync(Mon);
        var asMon = await meta.GetDefinitionAsync(m, new ObjectRef { Schema = Own, Name = "SHARED_P", Kind = ObjectKind.Proc }, Timeout());
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PROCEDURE \"QUERITHM_OWN\".\"SHARED_P\"", asMon);
    }

    [IntegrationFact]
    public async Task The_catalog_role_user_sees_its_own_schema_and_what_it_was_granted()
    {
        await using var c = await OpenAsync(Mon);
        var meta = new OracleMetadata();
        var stamps = await meta.GetStampsAsync(c, Timeout());
        Assert.Empty(meta.Notes(c));
        Assert.Equal(ObjectKind.Table, One(stamps, Own, "SHARED_T").Kind);
        Assert.Equal(ObjectKind.Proc, One(stamps, Own, "SHARED_P").Kind);
        Assert.DoesNotContain(stamps, s => s.Schema == AppSchema); // nothing granted: SELECT_CATALOG_ROLE opens DBA_* views, not ALL_*
        Assert.DoesNotContain(stamps, s => s.Schema is "SYS" or "SYSTEM");
        var details = await meta.GetDetailsAsync(c, [One(stamps, Own, "SHARED_T").Id], Timeout());
        Assert.Equal(["ID", "LABEL"], details.Columns.Select(x => x.Name));
        Assert.Contains(details.Indexes, i => i.Primary);
    }

    [IntegrationFact]
    public async Task Row_counts_come_from_statistics_null_before_they_are_gathered()
    {
        await using var c = await OpenAsync();
        await DropAsync(c, "TABLE", "Q_S05_ROWS");
        await ExecAsync(c, "CREATE TABLE q_s05_rows (id NUMBER)");
        await ExecAsync(c, "INSERT INTO q_s05_rows SELECT LEVEL FROM DUAL CONNECT BY LEVEL <= 1234");
        await ExecAsync(c, "COMMIT");
        var meta = new OracleMetadata();
        var id = One(await meta.GetStampsAsync(c, Timeout()), AppSchema, "Q_S05_ROWS").Id;

        var before = Assert.Single(await meta.GetRowCountsAsync(c, [id], Timeout()));
        Assert.Equal((id, (long?)null, true, (long?)null), (before.ObjectId, before.Rows, before.Estimated, before.AnalyzedAt));

        await ExecAsync(c, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'Q_S05_ROWS'); END;");
        var after = Assert.Single(await meta.GetRowCountsAsync(c, [id], Timeout()));
        Assert.Equal((1234L, true), (after.Rows, after.Estimated));
        Assert.InRange(after.AnalyzedAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        await DropAsync(c, "TABLE", "Q_S05_ROWS");
    }

    /// <summary>
    /// Object tables (<c>CREATE TABLE … OF type</c>, <c>OF XMLTYPE</c>) are in ALL_ALL_TABLES / ALL_OBJECT_TABLES, never in
    /// ALL_TABLES: they were left out of the explorer, the row counts and schema compare without a word (regression).
    /// </summary>
    [IntegrationFact]
    public async Task Object_and_XMLType_tables_are_listed_counted_and_named_as_not_compared()
    {
        await using var c = await OpenAsync();
        await DropAsync(c, "TABLE", "Q_FIX_OT");
        await DropAsync(c, "TABLE", "Q_FIX_XT");
        await DropAsync(c, "TYPE", "Q_FIX_OT_T");
        try
        {
            await ExecAsync(c, "CREATE TYPE q_fix_ot_t AS OBJECT (x NUMBER, label VARCHAR2(20))");
            await ExecAsync(c, "CREATE TABLE q_fix_ot OF q_fix_ot_t (CONSTRAINT q_fix_ot_pk PRIMARY KEY (x))");
            await ExecAsync(c, "CREATE TABLE q_fix_xt OF XMLTYPE");
            await ExecAsync(c, "INSERT INTO q_fix_ot VALUES (q_fix_ot_t(1, 'a'))");
            await ExecAsync(c, "INSERT INTO q_fix_ot VALUES (q_fix_ot_t(2, 'b'))");
            await ExecAsync(c, "COMMIT");
            await ExecAsync(c, "BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'Q_FIX_OT'); END;");

            var meta = new OracleMetadata();
            var stamps = await meta.GetStampsAsync(c, Timeout());
            var ot = One(stamps, AppSchema, "Q_FIX_OT");
            Assert.Equal((ObjectKind.Table, "object table"), (ot.Kind, ot.Note));
            var xt = One(stamps, AppSchema, "Q_FIX_XT");
            Assert.Equal((ObjectKind.Table, "XMLType table"), (xt.Kind, xt.Note));

            // The type's attributes are the columns; the system-generated OID / row columns are not shown.
            var details = await meta.GetDetailsAsync(c, [ot.Id], Timeout());
            Assert.Equal(["LABEL", "X"], details.Columns.Where(x => x.ObjectId == ot.Id).Select(x => x.Name).Order());
            Assert.Contains(details.Indexes, i => i.ObjectId == ot.Id && i.Primary && i.Columns.SequenceEqual(["X"]));

            var count = Assert.Single(await meta.GetRowCountsAsync(c, [ot.Id], Timeout()));
            Assert.Equal(2L, count.Rows);

            // Schema compare does not model them (a plain CREATE TABLE would lose the type): listed with the reason.
            var model = await new OracleSchemaCompare().ReadModelAsync(c, Timeout(300));
            Assert.Contains(model.NotCompared, n => n.Contains($"{AppSchema}.Q_FIX_OT: an object table", StringComparison.Ordinal));
            Assert.Contains(model.NotCompared, n => n.Contains($"{AppSchema}.Q_FIX_XT: an XMLType table", StringComparison.Ordinal));
            Assert.DoesNotContain(model.Tables, t => t.Name is "Q_FIX_OT" or "Q_FIX_XT");
        }
        finally
        {
            await DropAsync(c, "TABLE", "Q_FIX_OT");
            await DropAsync(c, "TABLE", "Q_FIX_XT");
            await DropAsync(c, "TYPE", "Q_FIX_OT_T");
        }
    }

    /// <summary>The view name a hardened database would refuse: unknown to the server, so it answers a real ORA-00942.</summary>
    static Func<string, string> Refuse(params string[] views) => v => views.Contains(v) ? v + "_QX" : v;

    [IntegrationFact]
    public async Task A_dictionary_view_the_login_cannot_read_is_a_note_on_that_kind_not_a_failed_refresh()
    {
        await SchemaAsync();
        await using var c = await OpenAsync();
        var meta = new OracleMetadata(Refuse("ALL_TRIGGERS", "ALL_TAB_COLS"));
        var stamps = await meta.GetStampsAsync(c, Timeout());
        // Everything else is still listed; triggers (whose parent is unknown without ALL_TRIGGERS) are not.
        One(stamps, AppSchema, "Q_S05_DEPT");
        One(stamps, AppSchema, "Q_S05_PROC");
        Assert.DoesNotContain(stamps, s => s.Kind == ObjectKind.Trigger);
        var notes = meta.Notes(c);
        Assert.Contains("Triggers are not listed: this login cannot read ALL_TRIGGERS (ORA-00942).", notes);
        Assert.Contains("Columns are not shown, and column changes are not detected: this login cannot read ALL_TAB_COLS (ORA-00942).", notes);
        Assert.Equal(2, notes.Count);

        var details = await meta.GetDetailsAsync(c, [One(stamps, AppSchema, "Q_S05_DEPT").Id], Timeout());
        Assert.Empty(details.Columns);
        Assert.Contains(details.Indexes, i => i.Primary); // keys come from ALL_CONSTRAINTS, which it can read
        Assert.Equal(2, meta.Notes(c).Count); // the details read said the same thing again: not repeated

        // A full read on the same connection starts over: nothing refused, nothing noted.
        await new OracleMetadata().GetStampsAsync(c, Timeout());
        Assert.Empty(meta.Notes(c));
    }

    [IntegrationFact]
    public async Task Without_ALL_OBJECTS_there_is_no_catalog_and_the_refresh_fails()
    {
        await using var c = await OpenAsync();
        var ex = await Assert.ThrowsAsync<OracleException>(() => new OracleMetadata(Refuse("ALL_OBJECTS")).GetStampsAsync(c, Timeout()));
        Assert.Equal(942, ex.Number);
    }

    [IntegrationFact]
    public async Task A_refresh_through_the_sidecar_stores_the_snapshot_with_notes_and_object_notes()
    {
        await SchemaAsync();
        var dir = Directory.CreateTempSubdirectory("qora-meta-host-").FullName;
        var notifier = new CapturingNotifier();
        await using var services = Services.Create(LocalStore.InMemory(), new CapturingFrames(), notifier, NullLoggerFactory.Instance,
            new ProviderRegistry(EngineSetup.Providers(dir), ProviderRegistry.HiddenEngines(EngineSetup.BehindFlag, "oracle")), dir);
        var rpc = new RpcServer(services, () => { });
        var p = await rpc.ProfileSave(ProfileOf(App));
        await rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = Secret(App) });
        var db = OracleIntegrationTests.Service;
        await rpc.MetaRefresh(new RefreshParams { ProfileId = p.Id, Database = db, Full = true });
        await services.Metadata.WaitAsync(p.Id, db);
        var snap = await rpc.MetaSnapshot(new SnapshotParams { ProfileId = p.Id, Database = db });
        Assert.Null(snap.Error);
        Assert.Null(snap.Notes);
        Assert.Equal("invalid", Assert.Single(snap.Objects, o => o.Name == "Q_S05_BAD").Note);
        Assert.Equal("materialized view", Assert.Single(snap.Objects, o => o.Name == "Q_S05_MV").Note);
        var dept = Assert.Single(snap.Objects, o => o.Name == "Q_S05_DEPT");
        Assert.Contains(dept.Columns!, x => x.Name == "ID" && x.Pk);
        Assert.Contains(snap.Fks, f => f.Name == "Q_S05_SALES_DEPT" && f.Referenced == dept.Id);
        var counts = await rpc.MetaRowCounts(new RowCountsParams { SessionId = services.Sessions.All.Single().Id, Database = db, ObjectIds = [dept.Id] });
        Assert.True(Assert.Single(counts).Estimated);
    }
}
