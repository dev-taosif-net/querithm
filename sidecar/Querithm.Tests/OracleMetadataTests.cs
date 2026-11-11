using System.Text.RegularExpressions;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Providers.Oracle;
using static Querithm.Providers.Oracle.OracleMetadata;

namespace Querithm.Tests;

/// <summary>
/// D4 S05 unit tests for <see cref="OracleMetadata"/>: the snapshot assembled from canned dictionary rows (kinds, notes,
/// return types, trigger parents), the shape of the one stamp query and what it leaves out for a view the login cannot
/// read, Oracle type names, keys and indexes, foreign keys, the ALL_SOURCE fallback's qualified name, editable scripts and
/// the padded id binds. The server-side behaviour is <c>Integration/OracleMetadataIntegrationTests</c>.
/// </summary>
public class OracleMetadataTests
{
    static StampRow Row(long id, string type, string name = "X", string? status = "VALID", string? flag = null, string? rtype = null, long? parent = null) =>
        new(id, "APP", name, type, id * 10, rtype, parent, status, flag);

    [Fact]
    public void The_snapshot_is_assembled_from_canned_rows_with_kinds_notes_return_types_and_trigger_parents()
    {
        var stamps = Assemble(
        [
            Row(1, "TABLE", "Sales"), Row(2, "TABLE", "SALES"), Row(3, "VIEW"), Row(4, "MATERIALIZED VIEW", status: "INVALID"),
            Row(5, "TABLE", "EXT", flag: "X"), Row(6, "PROCEDURE", rtype: "NUMBER"), Row(7, "FUNCTION", rtype: "VARCHAR2", status: "INVALID"),
            Row(8, "TRIGGER", parent: 1), Row(9, "TYPE", parent: 3), Row(10, "SEQUENCE"), Row(11, "SYNONYM"), Row(12, "PACKAGE"), Row(13, "INDEX"),
        ]);
        Assert.Equal(12, stamps.Count); // an index and anything else unknown is skipped; packages are listed since D4 S08 (OraclePackageTests)
        var by = stamps.ToDictionary(s => s.Id);
        Assert.Equal(("Sales", "SALES"), (by[1].Name, by[2].Name));
        Assert.Equal(ObjectKind.Table, by[1].Kind);
        Assert.Equal((ObjectKind.View, "materialized view, invalid"), (by[4].Kind, by[4].Note));
        Assert.Equal("external table", by[5].Note);
        Assert.Null(by[6].ReturnType); // only a function returns something
        Assert.Equal((ObjectKind.Func, "VARCHAR2", "invalid"), (by[7].Kind, by[7].ReturnType, by[7].Note));
        Assert.Equal((ObjectKind.Trigger, (long?)1), (by[8].Kind, by[8].ParentObjectId));
        Assert.Null(by[9].ParentObjectId); // a parent belongs to triggers only
        Assert.Equal([ObjectKind.Type, ObjectKind.Sequence, ObjectKind.Synonym], new[] { by[9].Kind, by[10].Kind, by[11].Kind });
        Assert.Equal(30, by[3].Marker);
        Assert.All(stamps, s => Assert.Equal("APP", s.Schema));
    }

    static IEnumerable<string> Views(string sql) =>
        Regex.Matches(sql, @"\bALL_[A-Z_]+\b").Select(m => m.Value);

    [Fact]
    public void The_stamp_query_reads_each_dictionary_view_once_with_statistics_feedback_off()
    {
        var sql = new OracleMetadata().StampSql(new HashSet<Piece>());
        var counts = Views(sql).GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
        // ALL_OBJECTS twice: the schema list (does the schema own anything?) and the objects themselves.
        Assert.Equal(2, counts["ALL_OBJECTS"]);
        // ALL_PROCEDURES is read with the details (package members, D4 S08), never by the stamp query.
        foreach (var (piece, (view, _)) in Pieces) Assert.Equal(piece == Piece.Procedures ? 0 : 1, counts.GetValueOrDefault(view));
        Assert.Equal(1, counts["ALL_USERS"]);
        Assert.DoesNotContain("DBA_", sql);
        Assert.Contains("STANDARD_HASH", sql);
        Assert.Contains(NoFeedback, sql);
        Assert.Contains("oracle_maintained = 'N'", sql);
    }

    [Fact]
    public void A_view_the_login_cannot_read_is_left_out_with_what_depends_on_it()
    {
        var meta = new OracleMetadata();
        var noTriggers = meta.StampSql(new HashSet<Piece> { Piece.Triggers });
        Assert.DoesNotContain("ALL_TRIGGERS", noTriggers);
        Assert.Contains("WHEN 'TRIGGER' THEN NULL", noTriggers); // a trigger's table is unknown: triggers are not listed
        Assert.Contains("TO_CHAR(o.last_ddl_time", noTriggers); // the weaker marker part
        var noSynonyms = meta.StampSql(new HashSet<Piece> { Piece.Synonyms });
        Assert.DoesNotContain("ALL_SYNONYMS", noSynonyms);
        Assert.Contains("WHEN 'SYNONYM' THEN CASE WHEN o.owner <> 'PUBLIC' THEN 1 END", noSynonyms); // private synonyms still listed
        var noTables = meta.StampSql(new HashSet<Piece> { Piece.Tables });
        Assert.DoesNotContain("ALL_TABLES", noTables);
        Assert.Contains("WHEN 'TABLE' THEN NULL", noTables);
        var none = meta.StampSql(Pieces.Keys.ToHashSet());
        Assert.Equal(["ALL_USERS", "ALL_OBJECTS", "ALL_OBJECTS"], Views(none));
    }

    [Fact]
    public void A_renamed_view_reaches_every_reference()
    {
        var sql = new OracleMetadata(v => v == "ALL_TAB_COLS" ? "ALL_TAB_COLS_QX" : v).StampSql(new HashSet<Piece>());
        Assert.Contains("ALL_TAB_COLS_QX", sql);
        Assert.DoesNotMatch(@"\bALL_TAB_COLS\b", sql);
    }

    [Fact]
    public void A_denied_view_note_says_what_is_missing_why_and_the_server_code()
    {
        var ex = OracleErrorFactory.Create(942, "ORA-00942: table or view does not exist");
        Assert.True(IsDenied(ex));
        Assert.True(IsDenied(OracleErrorFactory.Create(1031, "ORA-01031: insufficient privileges")));
        Assert.False(IsDenied(OracleErrorFactory.Create(904, "ORA-00904: invalid identifier")));
        Assert.False(IsDenied(new InvalidOperationException()));
        Assert.Equal("Triggers are not listed: this login cannot read ALL_TRIGGERS (ORA-00942).", DeniedNote(Pieces[Piece.Triggers].Effect, "ALL_TRIGGERS", ex));
        Assert.True(NoGetDdl(OracleErrorFactory.Create(31603, "ORA-31603: object not found")));
    }

    [Theory]
    [InlineData("VARCHAR2", null, 40, 40, "C", null, null, "VARCHAR2(40 CHAR)")]
    [InlineData("VARCHAR2", null, 10, 10, "B", null, null, "VARCHAR2(10 BYTE)")]
    [InlineData("CHAR", null, 3, 3, "B", null, null, "CHAR(3 BYTE)")]
    [InlineData("NVARCHAR2", null, 40, 20, "C", null, null, "NVARCHAR2(20)")]
    [InlineData("NUMBER", null, 22, null, null, 12, 2, "NUMBER(12,2)")]
    [InlineData("NUMBER", null, 22, null, null, 10, 0, "NUMBER(10)")]
    [InlineData("NUMBER", null, 22, null, null, null, null, "NUMBER")]
    [InlineData("NUMBER", null, 22, null, null, null, 0, "INTEGER")]
    [InlineData("NUMBER", null, 22, null, null, 5, -2, "NUMBER(5,-2)")]
    [InlineData("FLOAT", null, 22, null, null, 126, null, "FLOAT(126)")]
    [InlineData("RAW", null, 16, null, null, null, null, "RAW(16)")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", null, 13, null, null, null, 6, "TIMESTAMP(6) WITH TIME ZONE")]
    [InlineData("DATE", null, 7, null, null, null, null, "DATE")]
    [InlineData("CLOB", null, 4000, null, null, null, null, "CLOB")]
    [InlineData("Q_PT", "APP", 1, null, null, null, null, "APP.Q_PT")]
    [InlineData("XMLTYPE", "SYS", 2000, null, null, null, null, "XMLTYPE")]
    [InlineData(null, null, null, null, null, null, null, "UNKNOWN")]
    public void Column_types_read_as_Oracle_writes_them(string? dataType, string? owner, int? len, int? charLen, string? charUsed, int? precision, int? scale, string expected) =>
        Assert.Equal(expected, TypeName(dataType, owner, null, len, charLen, charUsed, precision, scale));

    [Fact]
    public void An_argument_of_a_user_type_reads_as_owner_dot_name() =>
        Assert.Equal("APP.Q_PT", TypeName("OBJECT", "APP", "Q_PT", null, null, null, null, null));

    [Fact]
    public void Columns_keep_identity_virtual_default_and_invisible_columns_sort_after_the_visible_ones()
    {
        var id = Column(new CatalogColumn(1, 1, 1, "ID", "NUMBER", null, 22, null, null, null, null, false, true, false, false, null));
        Assert.Equal((1, "NUMBER", true, false, false), (id.Ordinal, id.TypeName, id.Identity, id.Computed, id.Nullable));
        var name = Column(new CatalogColumn(1, 2, 2, "NAME", "VARCHAR2", null, 160, 40, "C", null, null, true, false, false, false, "'none'  \n"));
        Assert.Equal(("VARCHAR2(40 CHAR)", 40, "'none'"), (name.TypeName, name.MaxLen, name.Default)); // characters, not bytes; Oracle's trailing blanks dropped
        var twice = Column(new CatalogColumn(1, 3, 3, "TWICE", "NUMBER", null, 22, null, null, null, null, true, false, true, false, "\"AMT\"*2"));
        Assert.True(twice.Computed);
        var secret = Column(new CatalogColumn(1, null, 4, "SECRET", "VARCHAR2", null, 10, 10, "B", null, null, true, false, false, true, null));
        Assert.Equal((InvisibleOrdinalBase + 4, "VARCHAR2(10 BYTE) invisible"), (secret.Ordinal, secret.TypeName));
        Assert.Null(Column(new CatalogColumn(1, 5, 5, "D", "DATE", null, 7, null, null, null, null, true, false, false, false, "   ")).Default);
    }

    [Fact]
    public void Keys_come_from_constraints_and_an_index_backing_one_is_not_listed_twice()
    {
        KeyRow Key(string name, bool primary, string col, string status = "ENABLED", string deferred = "IMMEDIATE", string validated = "VALIDATED") =>
            new(1, name, primary, status, deferred, validated, "APP", name, col);
        IndexColumnRow Ix(string name, string col, bool unique = true, string type = "NORMAL", string status = "VALID", bool expr = false) =>
            new(1, "APP", name, unique, type, status, col, expr);
        var list = AssembleIndexes(
            [Key("PK_T", true, "A"), Key("PK_T", true, "B"), Key("UQ_LATE", false, "C", deferred: "DEFERRED"), Key("UQ_OFF", false, "D", status: "DISABLED"),
             Key("UQ_NOVAL", false, "E", validated: "NOT VALIDATED")],
            [Ix("PK_T", "A"), Ix("PK_T", "B"), Ix("IX_PLAIN", "C", unique: false), Ix("UX_FX", "SYS_NC00005$", type: "FUNCTION-BASED NORMAL", expr: true),
             Ix("UX_BROKEN", "D", status: "UNUSABLE"), Ix("UX_GOOD", "E")]).ToDictionary(i => i.Name);
        Assert.Equal(8, list.Count); // PK_T once, though an index of that name backs it
        Assert.Equal((true, true, false), (list["PK_T"].Primary, list["PK_T"].Unique, list["PK_T"].NotAKey));
        Assert.Equal(["A", "B"], list["PK_T"].Columns);
        Assert.True(list["UQ_LATE"].NotAKey); // initially deferred: violated until commit
        Assert.True(list["UQ_OFF"].NotAKey);
        Assert.True(list["UQ_NOVAL"].NotAKey); // NOVALIDATE: rows already there may break it
        Assert.True(list["IX_PLAIN"].NotAKey); // not unique
        Assert.Equal(["(expression)"], list["UX_FX"].Columns);
        Assert.True(list["UX_FX"].NotAKey);
        Assert.True(list["UX_BROKEN"].NotAKey);
        Assert.False(list["UX_GOOD"].NotAKey);
    }

    [Fact]
    public void Foreign_keys_group_their_column_pairs_in_order()
    {
        var fks = AssembleFks([new FkColumnRow(1, "FK_A", "X", "ID1", 9), new FkColumnRow(1, "FK_A", "Y", "ID2", 9), new FkColumnRow(1, "FK_B", "Z", "ID", 8)]);
        Assert.Equal(2, fks.Count);
        var a = fks.Single(f => f.Name == "FK_A");
        Assert.Equal((1L, 9L), (a.Parent, a.Referenced));
        Assert.Equal(["X:ID1", "Y:ID2"], a.Columns.Select(c => $"{c.Parent}:{c.Referenced}"));
    }

    [Theory]
    [InlineData("PROCEDURE              shared_p (p_id IN NUMBER) AS BEGIN NULL; END;", "PROCEDURE OWN.SHARED_P (p_id IN NUMBER) AS BEGIN NULL; END;")]
    [InlineData("function f return number is begin return 1; end;", "function OWN.SHARED_P return number is begin return 1; end;")]
    [InlineData("PROCEDURE own.p AS BEGIN NULL; END;", "PROCEDURE OWN.SHARED_P AS BEGIN NULL; END;")]
    [InlineData("PROCEDURE \"own\".\"p q\" AS BEGIN NULL; END;", "PROCEDURE OWN.SHARED_P AS BEGIN NULL; END;")]
    [InlineData("TYPE BODY pt AS MEMBER FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; END;", "TYPE BODY OWN.SHARED_P AS MEMBER FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; END;")]
    [InlineData("EDITIONABLE TRIGGER trg BEFORE INSERT ON t BEGIN NULL; END;", "EDITIONABLE TRIGGER OWN.SHARED_P BEFORE INSERT ON t BEGIN NULL; END;")]
    [InlineData("-- not a unit\nSELECT 1 FROM dual", "-- not a unit\nSELECT 1 FROM dual")]
    public void The_source_fallback_names_the_unit_with_its_owner(string unit, string expected) =>
        Assert.Equal(expected, Qualify(unit, "OWN.SHARED_P"));

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1 x FROM dual", "CREATE OR REPLACE VIEW v AS SELECT 1 x FROM dual")]
    [InlineData("CREATE FORCE EDITIONABLE VIEW v AS SELECT 1 x FROM dual", "CREATE OR REPLACE FORCE EDITIONABLE VIEW v AS SELECT 1 x FROM dual")]
    [InlineData("CREATE PROCEDURE p AS BEGIN NULL; END;\n/", "CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;\n/")]
    [InlineData("CREATE OR REPLACE EDITIONABLE PROCEDURE p AS BEGIN NULL; END;", "CREATE OR REPLACE EDITIONABLE PROCEDURE p AS BEGIN NULL; END;")]
    [InlineData("CREATE TABLE t (id NUMBER);", "CREATE TABLE t (id NUMBER);")]
    [InlineData("CREATE SEQUENCE s;", "CREATE SEQUENCE s;")]
    [InlineData("  -- c\nCREATE TRIGGER trg BEFORE INSERT ON t BEGIN NULL; END;", "  -- c\nCREATE OR REPLACE TRIGGER trg BEFORE INSERT ON t BEGIN NULL; END;")]
    public void An_editable_script_replaces_what_can_be_replaced(string definition, string expected) =>
        Assert.Equal(expected, new OracleMetadata().ToEditableScript(definition, null!));

    [Theory]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 64)]
    [InlineData(200, 256)]
    [InlineData(500, 500)]
    public void Ids_are_bound_padded_to_a_few_list_sizes(int count, int size)
    {
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray();
        var (list, bind) = IdBinds(ids);
        Assert.Equal(size, list.Split(", ").Length);
        Assert.StartsWith(":i0, :i1", size > 1 ? list : ":i0, :i1");
        using var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand();
        bind(cmd);
        Assert.Equal(size, cmd.Parameters.Count);
        Assert.Equal((long)count, cmd.Parameters[count - 1].Value);
        if (size > count) Assert.Equal(-1L, cmd.Parameters[size - 1].Value); // no object has id -1
        Assert.All(list.Split(", "), item => Assert.Matches(@"^:i[0-9]+$", item)); // never the ids themselves in the text
    }

    [Fact]
    public void Each_kind_maps_to_its_DBMS_METADATA_type()
    {
        Assert.Equal(["TABLE", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER", "TYPE", "SEQUENCE", "SYNONYM"],
            new[] { ObjectKind.Table, ObjectKind.View, ObjectKind.Proc, ObjectKind.Func, ObjectKind.Trigger, ObjectKind.Type, ObjectKind.Sequence, ObjectKind.Synonym }
                .Select(k => MetadataType(k)));
    }

    [Fact]
    public void The_provider_reads_its_catalog_and_still_refuses_dependencies_with_the_reason()
    {
        var provider = new OracleProvider();
        Assert.IsType<OracleMetadata>(provider.Metadata);
        var ex = Assert.Throws<NotSupportedException>(() => provider.Metadata.GetDependenciesAsync(null!, new ObjectRef { Schema = "A", Name = "B" }, default).GetAwaiter().GetResult());
        Assert.Equal(DependenciesNotYet, ex.Message);
    }
}
