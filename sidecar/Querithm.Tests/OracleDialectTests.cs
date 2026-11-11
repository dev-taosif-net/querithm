using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core.Execution;
using Querithm.Intelligence.Oracle;
using Querithm.Providers.Oracle;

namespace Querithm.Tests;

/// <summary>
/// D4 S10 unit rules for Oracle grid edits and imports: type parsing, the grid's text as typed parameters, generated SQL
/// (key predicates, DECODE conflict checks, the RETURNING block, the UNION ALL relation, preview literals), the ROWID
/// fallback key, result origins from tokens, and the import types, checks and DDL.
/// </summary>
public sealed class OracleDialectTests
{
    static readonly OracleDataEdit D = new();
    static readonly OracleImport I = new();

    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    [Theory]
    [InlineData("NUMBER(12,2)", "Number", "NUMBER", 12, 2, null)]
    [InlineData("NUMBER(10)", "Number", "NUMBER", 10, 0, null)]
    [InlineData("NUMBER", "Number", "NUMBER", null, null, null)]
    [InlineData("INTEGER", "Number", "INTEGER", null, 0, null)]
    [InlineData("FLOAT(126)", "Number", "FLOAT", 126, null, null)]
    [InlineData("VARCHAR2(40 CHAR)", "VarChar", "VARCHAR2", null, null, 40)]
    [InlineData("VARCHAR2(10) invisible", "VarChar", "VARCHAR2", null, null, 10)]
    [InlineData("CHAR(3)", "Char", "CHAR", null, null, 3)]
    [InlineData("TIMESTAMP(3)", "Timestamp", "TIMESTAMP", null, 3, null)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", "TimestampTz", "TIMESTAMP WITH TIME ZONE", null, 6, null)]
    [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE", "TimestampLtz", "TIMESTAMP WITH LOCAL TIME ZONE", null, 9, null)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", "IntervalDs", "INTERVAL DAY TO SECOND", 2, 6, null)]
    [InlineData("RAW(16)", "Raw", "RAW", null, null, 16)]
    [InlineData("LONG RAW", "LongRaw", "LONG RAW", null, null, null)]
    [InlineData("MDSYS.SDO_GEOMETRY", "Object", "MDSYS.SDO_GEOMETRY", null, null, null)]
    [InlineData("ROWID", "RowId", "ROWID", null, null, null)]
    public void Catalog_types_are_taken_apart(string type, string kind, string name, int? precision, int? scale, int? length)
    {
        var t = OracleColumnType.Parse(type);
        Assert.Equal((kind, name, precision, scale, length), (t.Kind.ToString(), t.Name, t.Precision, t.Scale, t.Length));
    }

    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("+007", "7")]
    [InlineData("-0.50", "-0.5")]
    [InlineData(".25", "0.25")]
    [InlineData("1e3", "1000")]
    [InlineData("1.25E-2", "0.0125")]
    [InlineData("-0", "0")]
    [InlineData("12345678901234567890123456789012345678", "12345678901234567890123456789012345678")]
    public void Numbers_become_plain_decimal_text_with_the_exponent_applied(string text, string plain) => Assert.Equal(plain, OracleDataEdit.PlainNumber(text));

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.2.3")]
    [InlineData("e5")]
    [InlineData("1e")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    public void Text_that_is_not_a_number_is_refused(string text) => Assert.Null(OracleDataEdit.PlainNumber(text));

    [Fact]
    public void Grid_text_becomes_typed_parameters_whatever_the_culture()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal((OracleDbType.Decimal, (object)OracleDecimal.Parse("12345678.1234")), Param("NUMBER(12,4)", "12345678.1234"));
            Assert.Equal(OracleDecimal.One, (OracleDecimal)Param("NUMBER(1)", "true").Value);
            var date = (OracleDate)Param("DATE", "2026-01-02 03:04:05").Value;
            Assert.Equal((2026, 1, 2, 3, 4, 5), (date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second));
            var ts = (OracleTimeStamp)Param("TIMESTAMP(9)", "2026-01-02T03:04:05.123456789").Value;
            Assert.Equal(123456789, ts.Nanosecond);
            var bc = (OracleDate)Param("DATE", "-4712-01-01").Value;
            Assert.Equal(-4712, bc.Year);
            var tz = (OracleTimeStampTZ)Param("TIMESTAMP(6) WITH TIME ZONE", "2026-01-02 03:04:05.5 +02:00").Value;
            Assert.Equal(("+02:00", 500_000_000), (tz.TimeZone, tz.Nanosecond));
            var raw = Param("RAW(16)", "0x0a0b");
            Assert.Equal(OracleDbType.Raw, raw.Type);
            Assert.Equal(new byte[] { 0x0A, 0x0B }, (byte[])raw.Value);
            Assert.Equal((OracleDbType.Char, (object)"ab   "), Param("CHAR(5)", "ab   "));
            Assert.Equal((OracleDbType.NVarchar2, (object)"ü"), Param("NVARCHAR2(1)", "ü"));
            Assert.Equal((OracleDbType.BinaryDouble, (object)0.1), Param("BINARY_DOUBLE", "0.1"));
            Assert.Equal((OracleDbType.Boolean, (object)false), Param("BOOLEAN", "false"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    static (OracleDbType Type, object Value) Param(string type, string text) => OracleDataEdit.ToParameter(OracleColumnType.Parse(type), text);

    [Theory]
    [InlineData("NUMBER(5,2)", "1234.5", "NUMBER(5,2) holds 3")]
    [InlineData("NUMBER(3)", "1000", "NUMBER(3) holds 3")]
    [InlineData("NUMBER", "1e200", "beyond")]
    [InlineData("DATE", "2026-01-02 03:04:05.5", "fraction")]
    [InlineData("DATE", "02/01/2026", "not a date")]
    [InlineData("DATE", "2026-02-30", "not a date")]
    [InlineData("TIMESTAMP(6)", "2026-01-02 03:04:05.1234567890", "not a timestamp")]
    [InlineData("RAW(2)", "0x010203", "RAW(2) holds 2")]
    [InlineData("RAW(16)", "0x0A…", "shortened")]
    [InlineData("RAW(16)", "0A0B", "0x")]
    [InlineData("VARCHAR2(3 CHAR)", "abcd", "holds 3")]
    [InlineData("VARCHAR2(3 BYTE)", "üü", "4 bytes")]
    [InlineData("BOOLEAN", "yes", "boolean")]
    [InlineData("BINARY_FLOAT", "abc", "not a number")]
    public void Values_the_column_cannot_take_are_refused_before_anything_is_sent(string type, string text, string says) =>
        Assert.Contains(says, Assert.Throws<FormatException>(() => Param(type, text)).Message);

    [Fact]
    public void A_char_value_keeps_its_padding_and_fits_when_only_the_padding_is_past_the_length() =>
        Assert.Equal("ab   ", Param("CHAR(2)", "ab   ").Value);

    [Fact]
    public void Lob_long_object_and_text_only_columns_are_read_only_with_the_reason()
    {
        Assert.Contains("read in part", D.LockReason(new SnapColumn { Name = "C", Type = "CLOB" }));
        Assert.Contains("read in part", D.LockReason(new SnapColumn { Name = "B", Type = "BLOB" }));
        Assert.Contains("LONG values", D.LockReason(new SnapColumn { Name = "L", Type = "LONG" }));
        Assert.Contains("LONG RAW values", D.LockReason(new SnapColumn { Name = "L", Type = "LONG RAW" }));
        Assert.Contains("BFILENAME", D.LockReason(new SnapColumn { Name = "F", Type = "BFILE" }));
        Assert.Contains("object or collection", D.LockReason(new SnapColumn { Name = "G", Type = "MDSYS.SDO_GEOMETRY" }));
        Assert.Contains("XMLTYPE", D.LockReason(new SnapColumn { Name = "X", Type = "XMLTYPE" }));
        Assert.Contains("ROWID", D.LockReason(new SnapColumn { Name = "ROWID", Type = "ROWID" }));
        Assert.Null(D.LockReason(new SnapColumn { Name = "N", Type = "NUMBER(10)" }));
        Assert.Null(D.LockReason(new SnapColumn { Name = "R", Type = "UROWID" })); // a column of row ids is data
        Assert.False(D.Comparable("BINARY_DOUBLE"));
        Assert.False(D.Comparable("CLOB"));
        Assert.False(D.Comparable("RAW(100)"));
        Assert.True(D.Comparable("RAW(16)"));
        Assert.True(D.ExactKey("ROWID"));
        Assert.True(D.ExactKey("TIMESTAMP(6) WITH TIME ZONE"));
    }

    [Fact]
    public void Text_forms_read_on_the_server_never_depend_on_nls_settings()
    {
        Assert.Equal("TO_CHAR(\"A\", 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')", D.ValueText("\"A\"", "NUMBER(12,2)"));
        Assert.Equal("LTRIM(TO_CHAR(\"D\", 'SYYYY-MM-DD HH24:MI:SS'))", D.ValueText("\"D\"", "DATE")); // signed year: BC keeps its era
        Assert.Equal("LTRIM(TO_CHAR(\"T\", 'SYYYY-MM-DD HH24:MI:SS.FF3'))", D.ValueText("\"T\"", "TIMESTAMP(3)"));
        Assert.Equal("LTRIM(TO_CHAR(\"T\", 'SYYYY-MM-DD HH24:MI:SS.FF6 TZR'))", D.ValueText("\"T\"", "TIMESTAMP(6) WITH TIME ZONE"));
        Assert.Equal("'0x' || RAWTOHEX(\"R\")", D.ValueText("\"R\"", "RAW(16)"));
        Assert.Equal("ROWIDTOCHAR(\"ROWID\")", D.ValueText("\"ROWID\"", "ROWID"));
        Assert.Equal("'(LONG value not shown)'", D.ValueText("\"L\"", "LONG"));
        Assert.Equal("DECODE(\"A\", :p0, 1, 0) = 1", D.NullSafeEquals("\"A\"", ":p0"));
    }

    [Fact]
    public void An_inserted_row_returns_its_key_through_its_rowid_in_an_implicit_result()
    {
        var sql = D.InsertReturningKey("\"S\".\"T\"", [("NAME", ":p0")], [("ID", "NUMBER(10)", true)]);
        Assert.Contains("INSERT INTO \"S\".\"T\" (\"NAME\") VALUES (:p0) RETURNING ROWID INTO q$rid;", sql);
        Assert.Contains("OPEN q$key FOR SELECT TO_CHAR(\"ID\", 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''') FROM \"S\".\"T\" WHERE ROWID = q$rid;", sql);
        Assert.Contains("DBMS_SQL.RETURN_RESULT(q$key);", sql);
        // A ROWID key reads back as text; no column given → the first real key column takes its DEFAULT.
        Assert.Contains("SELECT ROWIDTOCHAR(ROWID) FROM", D.InsertReturningKey("\"S\".\"T\"", [("A", ":p0")], [("ROWID", "ROWID", false)]));
        Assert.Contains("(\"ID\") VALUES (DEFAULT)", D.InsertReturningKey("\"S\".\"T\"", [], [("ID", "NUMBER", true)]));
        Assert.Throws<InvalidOperationException>(() => D.InsertReturningKey("\"S\".\"T\"", [], [("ROWID", "ROWID", false)]));
    }

    [Fact]
    public void The_key_relation_is_a_union_of_selects_from_dual_without_as()
    {
        var sql = D.ValuesRelation([[ "0", ":p0" ], [ "1", ":p1" ]], ["qi", "k0"], "v");
        Assert.Equal("(SELECT 0 AS qi, :p0 AS k0 FROM DUAL UNION ALL SELECT 1, :p1 FROM DUAL) v", sql);
    }

    [Fact]
    public void Preview_literals_are_typed_and_quote_only_the_quote()
    {
        Assert.Equal("1.5", D.PreviewLiteral("NUMBER(12,2)", V("1.50")));
        Assert.Equal("TO_DATE('2026-01-02 03:04:05', 'SYYYY-MM-DD HH24:MI:SS')", D.PreviewLiteral("DATE", V("2026-01-02T03:04:05")));
        Assert.Equal("TO_DATE('-0100-03-04 05:06:07', 'SYYYY-MM-DD HH24:MI:SS')", D.PreviewLiteral("DATE", V("-0100-03-04 05:06:07"))); // 100 BC stays BC
        Assert.Equal("TIMESTAMP '2026-01-02 03:04:05.5'", D.PreviewLiteral("TIMESTAMP(6)", V("2026-01-02 03:04:05.5")));
        Assert.Equal("HEXTORAW('0A0B')", D.PreviewLiteral("RAW(4)", V("0x0a0b")));
        Assert.Equal("'it''s \\n'", D.PreviewLiteral("VARCHAR2(20)", V("it's \\n")));
        Assert.Equal("N'ü'", D.PreviewLiteral("NVARCHAR2(5)", V("ü")));
        Assert.Equal("NULL", D.PreviewLiteral("DATE", V(null)));
        Assert.Equal("DEFAULT", D.PreviewLiteral("DATE", new GridValue { UseDefault = true }));
        Assert.Equal("'not a date'", D.PreviewLiteral("DATE", V("not a date")));
        Assert.Contains("ORA_ROWSCN", D.ConcurrencyNote(null));
    }

    // ------------------------------------------------------------------ editability + generated statements

    static SchemaSnapshot Snap(string? rowAddress = "heap") => new()
    {
        ProfileId = "p", Database = "FREEPDB1", Status = SnapshotStatus.Fresh, Fks = [],
        Objects =
        [
            new SnapObject
            {
                Id = 1, Schema = "APP", Name = "ORDERS", Kind = ObjectKind.Table, RowAddress = "heap",
                Columns =
                [
                    new SnapColumn { Name = "ID", Type = "NUMBER(10)", Pk = true, Ordinal = 1 },
                    new SnapColumn { Name = "NAME", Type = "VARCHAR2(40 CHAR)", Ordinal = 2 },
                    new SnapColumn { Name = "NOTE", Type = "CLOB", Nullable = true, Ordinal = 3 },
                    new SnapColumn { Name = "TWICE", Type = "NUMBER", Computed = true, Nullable = true, Ordinal = 4 },
                    new SnapColumn { Name = "SECRET", Type = "VARCHAR2(10) invisible", Nullable = true, Ordinal = 100_005 },
                ],
            },
            new SnapObject
            {
                Id = 2, Schema = "APP", Name = "LOG", Kind = ObjectKind.Table, RowAddress = rowAddress,
                Columns = [new SnapColumn { Name = "MSG", Type = "VARCHAR2(100)", Nullable = true, Ordinal = 1 }, new SnapColumn { Name = "N", Type = "NUMBER", Ordinal = 2 }],
            },
            new SnapObject { Id = 3, Schema = "APP", Name = "ORDERS_SYN", Kind = ObjectKind.Synonym, TargetObjectId = 1 },
            new SnapObject { Id = 4, Schema = "PUBLIC", Name = "PUB_LOG", Kind = ObjectKind.Synonym, TargetObjectId = 2 },
            new SnapObject { Id = 5, Schema = "APP", Name = "FAR", Kind = ObjectKind.Synonym, Note = OracleResultSources.RemoteNote },
            new SnapObject { Id = 6, Schema = "OTHER", Name = "ORDERS", Kind = ObjectKind.Table, RowAddress = "heap", Columns = [new SnapColumn { Name = "X", Type = "NUMBER", Ordinal = 1 }] },
            new SnapObject { Id = 7, Schema = "APP", Name = "V_ORDERS", Kind = ObjectKind.View, Columns = [new SnapColumn { Name = "ID", Type = "NUMBER(10)", Ordinal = 1 }] },
        ],
    };

    static ResultColumn[] Cols(params string[] names) => names.Select((n, i) => new ResultColumn { Name = n, TypeName = "x", ArrowType = "utf8", Ordinal = i }).ToArray();

    static ColumnOrigin?[]? Origins(string sql, params string[] names) => OracleResultSources.Predict(sql, Snap(), "APP", names);

    static void Same(ColumnOrigin?[] expected, ColumnOrigin?[]? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.AsEnumerable(), actual.AsEnumerable());
    }

    [Fact]
    public void Origins_come_from_the_tokens_and_the_current_schema()
    {
        Same([new ColumnOrigin(1, "ID"), new ColumnOrigin(1, "NAME"), new ColumnOrigin(1, "NOTE"), new ColumnOrigin(1, "TWICE")],
            Origins("SELECT * FROM orders WHERE id > 1 ORDER BY id FETCH FIRST 10 ROWS ONLY", "ID", "NAME", "NOTE", "TWICE")); // not the invisible column
        Same([new ColumnOrigin(1, "NAME"), null, new ColumnOrigin(1, "ROWID")],
            Origins("SELECT o.name AS n, UPPER(o.name), o.ROWID FROM app.orders o WHERE o.id IN (SELECT 1 FROM dual)", "N", "UPPER(O.NAME)", "ROWID"));
        Same([new ColumnOrigin(1, "ID")], Origins("select \"ID\" from orders_syn", "ID")); // a synonym is followed
        Same([new ColumnOrigin(2, "MSG")], Origins("SELECT msg FROM pub_log", "MSG")); // a PUBLIC synonym when the schema has none
        Same([new ColumnOrigin(6, "X")], Origins("SELECT x FROM other.orders", "X"));
        Same([new ColumnOrigin(1, "ID"), new ColumnOrigin(1, "NAME"), new ColumnOrigin(1, "NOTE"), new ColumnOrigin(1, "TWICE"), new ColumnOrigin(1, "ROWID")],
            Origins("SELECT t.*, t.ROWID FROM orders t FOR UPDATE", "ID", "NAME", "NOTE", "TWICE", "ROWID"));
        // A name resolves in the current schema: unknown → nothing, another schema → its own table.
        Assert.Null(OracleResultSources.Predict("SELECT id FROM orders", Snap(), null, ["ID"]));
        Same([new ColumnOrigin(6, "X")], OracleResultSources.Predict("SELECT x FROM orders", Snap(), "OTHER", ["X"]));
    }

    [Theory]
    [InlineData("SELECT DISTINCT id FROM orders")]
    [InlineData("SELECT o.id FROM orders o JOIN log l ON 1 = 1")]
    [InlineData("SELECT id FROM orders, log")]
    [InlineData("SELECT id FROM orders GROUP BY id")]
    [InlineData("SELECT id FROM orders CONNECT BY PRIOR id = id")]
    [InlineData("SELECT id FROM orders AS OF TIMESTAMP SYSDATE - 1")]
    [InlineData("SELECT id FROM orders PARTITION (p1)")]
    [InlineData("SELECT id FROM orders@far")]
    [InlineData("SELECT id FROM orders UNION ALL SELECT id FROM orders")]
    [InlineData("SELECT id FROM orders MINUS SELECT 1 FROM dual")]
    [InlineData("SELECT id FROM TABLE(f())")]
    [InlineData("SELECT * FROM (SELECT id FROM orders)")]
    [InlineData("WITH x AS (SELECT id FROM orders) SELECT id FROM x")]
    [InlineData("SELECT id FROM nowhere")]
    [InlineData("SELECT id FROM orders; SELECT 1 FROM dual")]
    [InlineData("BEGIN NULL; END;")]
    public void Anything_but_one_plain_table_predicts_nothing(string sql) => Assert.Null(Origins(sql, "ID"));

    [Theory]
    [InlineData("SELECT id FROM orders@far", "database link")]
    [InlineData("SELECT id FROM far", "APP.FAR is a synonym for a remote object")]
    [InlineData("SELECT id FROM orders MINUS SELECT id FROM log", "MINUS")]
    [InlineData("SELECT a.id FROM orders a JOIN orders b ON b.id = a.id", "more than once")]
    [InlineData("SELECT id FROM orders UNION SELECT 1 FROM dual", "UNION")]
    public void A_shape_whose_origins_cannot_be_trusted_is_refused_with_the_reason(string sql, string says) =>
        Assert.Contains(says, OracleResultSources.EditRefusal(sql, Snap()));

    [Fact]
    public void A_plain_query_is_not_refused() => Assert.Null(OracleResultSources.EditRefusal("SELECT id, name FROM orders WHERE name = 'a@b'", Snap()));

    static ResultEditInfo Analyze(string sql, string? rowAddress = "heap", params string[] names) =>
        EditabilityAnalyzer.Analyze(D, Cols(names), OracleResultSources.Predict(sql, Snap(rowAddress), "APP", names), Snap(rowAddress), _ => []);

    [Fact]
    public void A_primary_key_table_is_editable_and_lob_computed_and_rowid_columns_are_locked()
    {
        var e = Analyze("SELECT id, name, note, twice, ROWID FROM orders", "heap", "ID", "NAME", "NOTE", "TWICE", "ROWID");
        Assert.True(e.Editable, e.Reason);
        Assert.Equal(["ID"], e.KeyColumns);
        Assert.Contains("read in part", e.Columns.Single(c => c.Column == "NOTE").LockReason);
        Assert.Equal("computed column", e.Columns.Single(c => c.Column == "TWICE").LockReason);
        Assert.Contains("ROWID", e.Columns.Single(c => c.Column == "ROWID").LockReason);
        Assert.Null(e.InsertBlockedReason); // the ROWID is never required for an insert
    }

    [Fact]
    public void Without_a_key_the_rowid_stands_in_only_for_a_heap_table()
    {
        var heap = Analyze("SELECT ROWID, msg, n FROM log", "heap", "ROWID", "MSG", "N");
        Assert.True(heap.Editable, heap.Reason);
        Assert.Equal(["ROWID"], heap.KeyColumns);
        Assert.Equal([0], heap.KeyOrdinals);
        Assert.Null(heap.InsertBlockedReason);

        var noRowid = Analyze("SELECT msg, n FROM log", "heap", "MSG", "N");
        Assert.Equal("Read-only: APP.LOG has no primary key or NOT NULL unique index", noRowid.Reason);
        Assert.Contains("index-organized", Analyze("SELECT ROWID, msg FROM log", "iot", "ROWID", "MSG").Reason);
        Assert.Contains("row movement is enabled", Analyze("SELECT ROWID, msg FROM log", "moves", "ROWID", "MSG").Reason);
        Assert.Contains("refresh the explorer", Analyze("SELECT ROWID, msg FROM log", null, "ROWID", "MSG").Reason);
        Assert.StartsWith("Read-only: APP.LOG has no primary key or NOT NULL unique index, and its ROWID cannot stand in", Analyze("SELECT ROWID, msg FROM log", "moves", "ROWID", "MSG").Reason);
        // A primary key not in the result: the ROWID of a heap table stands in for it.
        var pkMissing = Analyze("SELECT name, ROWID FROM orders", "heap", "NAME", "ROWID");
        Assert.Equal(["ROWID"], pkMissing.KeyColumns);
        Assert.Equal("Read-only: primary key column ID is not in the SELECT list", Analyze("SELECT name FROM orders", "heap", "NAME").Reason);
    }

    [Fact]
    public void A_view_and_another_engines_dialect_never_get_a_rowid_key()
    {
        Assert.StartsWith("Read-only: APP.V_ORDERS is a view", Analyze("SELECT id FROM v_orders", "heap", "ID").Reason);
        var pg = EditabilityAnalyzer.Analyze(new Querithm.Providers.Postgres.PostgresDataEdit(), Cols("ROWID", "MSG"), [new ColumnOrigin(2, "ROWID"), new ColumnOrigin(2, "MSG")], Snap(), _ => []);
        Assert.False(pg.Editable);
    }

    [Fact]
    public void Updates_deletes_and_inserts_bind_every_value_and_compare_originals_with_decode()
    {
        var edit = Analyze("SELECT ROWID, msg, n FROM log", "heap", "ROWID", "MSG", "N");
        var p = new GridPatchParams
        {
            SessionId = "s", ExecutionId = "e",
            Updates = [new GridRowUpdate { RowIndex = 0, Key = [V("AAAVbOAAAAAABBuAAA")], Changes = new() { ["MSG"] = V("new") }, Original = new() { ["MSG"] = V("") } }],
            Deletes = [new GridRowDelete { RowIndex = 1, Key = [V("AAAVbOAAAAAABBuAAB")], Original = new() { ["MSG"] = V(null), ["N"] = V("2") } }],
            Inserts = [new GridRowInsert { TempIndex = 0, Values = new() { ["MSG"] = V("x"), ["N"] = V("3") } }],
        };
        var statements = PatchBuilder.Build(D, edit, p, force: false);
        Assert.Equal("DELETE FROM \"APP\".\"LOG\" WHERE \"ROWID\" = :p0 AND DECODE(\"MSG\", :p1, 1, 0) = 1 AND DECODE(\"N\", :p2, 1, 0) = 1", statements[0].Sql);
        Assert.Equal("UPDATE \"APP\".\"LOG\" SET \"MSG\" = :p0 WHERE \"ROWID\" = :p1 AND DECODE(\"MSG\", :p2, 1, 0) = 1", statements[1].Sql);
        Assert.Contains("INSERT INTO \"APP\".\"LOG\" (\"MSG\", \"N\") VALUES (:p0, :p1) RETURNING ROWID INTO q$rid;", statements[2].Sql);
        Assert.Contains("SELECT ROWIDTOCHAR(ROWID) FROM \"APP\".\"LOG\"", statements[2].Sql);
        // Every value is a typed, named parameter; '' and NULL bind alike (Oracle stores '' as NULL).
        using var cmd = new OracleCommand();
        for (var i = 0; i < statements[1].Values.Count; i++) D.AddValue(cmd, i, statements[1].Values[i].Type, statements[1].Values[i].Value);
        Assert.True(cmd.BindByName);
        Assert.Equal(new[] { "p0", "p1", "p2" }, cmd.Parameters.Cast<OracleParameter>().Select(x => x.ParameterName));
        Assert.Equal(OracleDbType.Varchar2, cmd.Parameters[2].OracleDbType);
        var preview = PatchBuilder.Preview(D, statements, null);
        Assert.Contains("WHERE \"ROWID\" = 'AAAVbOAAAAAABBuAAB' AND DECODE(\"MSG\", NULL, 1, 0) = 1 AND DECODE(\"N\", 2, 1, 0) = 1;", preview);
    }

    [Fact]
    public void A_null_binds_with_its_columns_type_so_a_union_of_key_rows_stays_one_type()
    {
        using var cmd = new OracleCommand();
        D.AddValue(cmd, 0, "NUMBER(10)", V(null));
        D.AddValue(cmd, 1, "DATE", V(null));
        Assert.Equal((OracleDbType.Decimal, OracleDbType.Date), (cmd.Parameters[0].OracleDbType, cmd.Parameters[1].OracleDbType));
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
    }

    // ------------------------------------------------------------------ session schema, catalog facts

    [Theory]
    [InlineData("SELECT 1 FROM dual", false)]
    [InlineData("UPDATE t SET a = 1", false)]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = other", true)]
    [InlineData("alter session set nls_date_format = 'YYYY'", true)]
    [InlineData("CALL p()", true)]
    [InlineData("BEGIN EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = x'; END;", true)]
    [InlineData("DECLARE n NUMBER; BEGIN NULL; END;", true)]
    [InlineData("SELECT 'ALTER SESSION' FROM dual", false)]
    public void A_batch_that_may_change_the_current_schema_makes_it_unknown(string sql, bool changes) => Assert.Equal(changes, OracleSessionSchema.MayChange(sql));

    [Fact]
    public void Tables_carry_their_row_address_and_a_synonym_over_a_link_says_so()
    {
        var stamps = OracleMetadata.Assemble(
        [
            new OracleMetadata.StampRow(1, "APP", "T", "TABLE", 10, null, null, "VALID", null, RowAddress: "moves"),
            new OracleMetadata.StampRow(2, "APP", "S", "SYNONYM", 11, null, null, "VALID", "R"),
            new OracleMetadata.StampRow(3, "APP", "V", "VIEW", 12, null, null, "VALID", null, RowAddress: "heap"),
        ]);
        Assert.Equal("moves", stamps.Single(s => s.Id == 1).RowAddress);
        Assert.Equal(OracleResultSources.RemoteNote, stamps.Single(s => s.Id == 2).Note);
        Assert.Null(stamps.Single(s => s.Id == 3).RowAddress);
        var sql = new OracleMetadata().StampSql(new HashSet<OracleMetadata.Piece>());
        Assert.Contains("t.row_movement = 'ENABLED' THEN 'moves'", sql);
        Assert.Contains("y.db_link IS NOT NULL THEN 'R'", sql);
    }

    // ------------------------------------------------------------------ import

    static ImportColumnProfile Col(ImportKind kind, int precision = 0, int scale = 0, int maxLength = 0, string? min = null, string? max = null) =>
        new() { Name = "c", Kind = kind, Precision = precision, Scale = scale, MaxLength = maxLength, Min = min, Max = max };

    [Fact]
    public void Import_types_new_tables_from_the_file()
    {
        Assert.Equal("NUMBER(1)", I.TypeFor(Col(ImportKind.Bool)));
        Assert.Equal("NUMBER(10)", I.TypeFor(Col(ImportKind.Int32)));
        Assert.Equal("NUMBER(19)", I.TypeFor(Col(ImportKind.Int64)));
        Assert.Equal("NUMBER(12,2)", I.TypeFor(Col(ImportKind.Decimal, 12, 2)));
        Assert.Equal("NUMBER", I.TypeFor(Col(ImportKind.Decimal, 50, 2)));
        Assert.Equal("DATE", I.TypeFor(Col(ImportKind.DateTime)));
        Assert.Equal("TIMESTAMP(3)", I.TypeFor(Col(ImportKind.DateTime, 3)));
        Assert.Equal("TIMESTAMP(0) WITH TIME ZONE", I.TypeFor(Col(ImportKind.DateTimeOffset)));
        Assert.Equal("VARCHAR2(255 CHAR)", I.TypeFor(Col(ImportKind.Text, maxLength: 12)));
        Assert.Equal("VARCHAR2(1000 CHAR)", I.TypeFor(Col(ImportKind.Text, maxLength: 900)));
        Assert.Equal("CLOB", I.TypeFor(Col(ImportKind.Text, maxLength: 5000)));
        var ddl = I.CreateTable("APP", "New T", [("ID", "NUMBER(10)", false), ("BODY", "CLOB", false), ("K2", "CLOB", false)], ["ID", "K2"]);
        Assert.Equal("CREATE TABLE \"APP\".\"New T\" (\n    \"ID\" NUMBER(10) NOT NULL,\n    \"BODY\" CLOB,\n    \"K2\" VARCHAR2(1000 CHAR) NOT NULL,\n    PRIMARY KEY (\"ID\", \"K2\")\n);", ddl);
        Assert.Equal("ALTER TABLE \"APP\".\"T\" MODIFY (\"C\" NUMBER(14,2));", I.AlterColumnType("APP", "T", new SnapColumn { Name = "C", Type = "NUMBER(10,2)" }, "NUMBER(14,2)"));
        Assert.StartsWith("-- Oracle cannot MODIFY a column to CLOB", I.AlterColumnType("APP", "T", new SnapColumn { Name = "C", Type = "VARCHAR2(10)" }, "CLOB"));
        Assert.Equal("ALTER TABLE \"APP\".\"T\" ADD (\"C\" DATE);", I.AddColumn("APP", "T", "C", "DATE"));
    }

    [Fact]
    public void Import_checks_number_date_and_timestamp_fit()
    {
        Assert.Equal(ImportVerdictKind.Fits, I.Check(Col(ImportKind.Int32, min: "-5", max: "999"), new SnapColumn { Name = "c", Type = "NUMBER(3)" }).Kind);
        var widen = I.Check(Col(ImportKind.Int32, min: "1", max: "12345"), new SnapColumn { Name = "c", Type = "NUMBER(3)" });
        Assert.Equal((ImportVerdictKind.Widen, "NUMBER(5,0)"), (widen.Kind, widen.WidenTo));
        Assert.Equal(ImportVerdictKind.Fits, I.Check(Col(ImportKind.Decimal, 7, 3), new SnapColumn { Name = "c", Type = "NUMBER" }).Kind);
        Assert.Contains("rounds them to 2", I.Check(Col(ImportKind.Decimal, 5, 4), new SnapColumn { Name = "c", Type = "NUMBER(5,2)" }).Message);
        Assert.Equal(ImportVerdictKind.Mismatch, I.Check(Col(ImportKind.Text, maxLength: 3), new SnapColumn { Name = "c", Type = "NUMBER(5)" }).Kind);
        Assert.Equal(ImportVerdictKind.Fits, I.Check(Col(ImportKind.Bool), new SnapColumn { Name = "c", Type = "NUMBER(1)" }).Kind);
        Assert.Equal(ImportVerdictKind.Fits, I.Check(Col(ImportKind.DateTime), new SnapColumn { Name = "c", Type = "DATE" }).Kind);
        var fraction = I.Check(Col(ImportKind.DateTime, 3), new SnapColumn { Name = "c", Type = "DATE" });
        Assert.Equal((ImportVerdictKind.Widen, "TIMESTAMP(3)"), (fraction.Kind, fraction.WidenTo));
        Assert.Contains("whole seconds", I.Check(Col(ImportKind.DateTime, 6), new SnapColumn { Name = "c", Type = "TIMESTAMP(0)" }).Message);
        Assert.Equal(ImportVerdictKind.Fits, I.Check(Col(ImportKind.DateTimeOffset), new SnapColumn { Name = "c", Type = "TIMESTAMP(6) WITH TIME ZONE" }).Kind);
        Assert.Equal(ImportVerdictKind.Mismatch, I.Check(Col(ImportKind.DateTimeOffset), new SnapColumn { Name = "c", Type = "DATE" }).Kind);
        var longer = I.Check(Col(ImportKind.Text, maxLength: 80), new SnapColumn { Name = "c", Type = "VARCHAR2(40 CHAR)" });
        Assert.Equal((ImportVerdictKind.Widen, "VARCHAR2(80 CHAR)"), (longer.Kind, longer.WidenTo));
        Assert.Equal(ImportVerdictKind.Mismatch, I.Check(Col(ImportKind.Text, maxLength: 5000), new SnapColumn { Name = "c", Type = "VARCHAR2(40)" }).Kind);
        Assert.Equal(ImportVerdictKind.Mismatch, I.Check(Col(ImportKind.Text, maxLength: 5), new SnapColumn { Name = "c", Type = "BLOB" }).Kind);
        Assert.False(I.BlankIsNull("VARCHAR2(10)"));
        Assert.False(I.BlankIsNull("CHAR(1)"));
        Assert.True(I.BlankIsNull("NUMBER"));
        Assert.True(I.BlankIsNull("DATE"));
        Assert.False(I.NullUsesDefault);
        Assert.IsType<OracleArrayLoader>(I.Loader);
    }
}
