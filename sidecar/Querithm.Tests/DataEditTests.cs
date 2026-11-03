using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Grid editing: editability decisions, generated DML and value conversion, for every engine.</summary>
public class DataEditTests
{
    static readonly IDataEditDialect Mssql = new SqlServerDataEdit();
    static readonly IDataEditDialect Pg = new PostgresDataEdit();

    public static TheoryData<string> Engines => TestEngines.Names;

    static IDataEditDialect D(string engine) => engine == "pg" ? Pg : Mssql;

    // Orders (1): PK OrderId (identity), CustomerId, Status, Amount float, Notes text, Computed; Log (2): no keys;
    // Codes (3): no PK but a NOT NULL unique index on Code; v_Orders (4): view; Weird (5): hostile names; Ver (6): rowversion.
    static SchemaSnapshot Snapshot(string engine)
    {
        var pg = engine == "pg";
        string T(string mssql, string pgType) => pg ? pgType : mssql;
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh,
            Objects =
            [
                new SnapObject
                {
                    Id = 1, Schema = pg ? "public" : "dbo", Name = "Orders", Kind = ObjectKind.Table,
                    Columns =
                    [
                        new SnapColumn { Name = "OrderId", Type = T("int", "integer"), Pk = true, Identity = true, Ordinal = 1 },
                        new SnapColumn { Name = "CustomerId", Type = T("int", "integer"), Ordinal = 2 },
                        new SnapColumn { Name = "Status", Type = T("nvarchar(20)", "character varying(20)"), Nullable = true, Ordinal = 3 },
                        new SnapColumn { Name = "Amount", Type = T("float", "double precision"), Nullable = true, Ordinal = 4 },
                        new SnapColumn { Name = "Notes", Type = T("ntext", "xml"), Nullable = true, Ordinal = 5 },
                        new SnapColumn { Name = "Total", Type = T("int", "integer"), Computed = true, Nullable = true, Ordinal = 6 },
                        new SnapColumn { Name = "Placed", Type = T("datetime2", "timestamp without time zone"), Nullable = true, Ordinal = 7 },
                    ],
                },
                new SnapObject
                {
                    Id = 2, Schema = "dbo", Name = "Log", Kind = ObjectKind.Table,
                    Columns = [new SnapColumn { Name = "Msg", Type = T("nvarchar(max)", "text"), Ordinal = 1, Nullable = true }],
                },
                new SnapObject
                {
                    Id = 3, Schema = "dbo", Name = "Codes", Kind = ObjectKind.Table,
                    Columns =
                    [
                        new SnapColumn { Name = "Code", Type = T("varchar(10)", "text"), Ordinal = 1 },
                        new SnapColumn { Name = "Label", Type = T("nvarchar(50)", "text"), Ordinal = 2 },
                    ],
                },
                new SnapObject { Id = 4, Schema = "dbo", Name = "v_Orders", Kind = ObjectKind.View, Columns = [new SnapColumn { Name = "OrderId", Type = "int", Ordinal = 1 }] },
                new SnapObject
                {
                    Id = 5, Schema = "we]ird\"s", Name = "t;]\"x", Kind = ObjectKind.Table,
                    Columns =
                    [
                        new SnapColumn { Name = "i]d\"", Type = T("int", "integer"), Pk = true, Ordinal = 1 },
                        new SnapColumn { Name = "v;al", Type = T("nvarchar(10)", "text"), Nullable = true, Ordinal = 2 },
                    ],
                },
                new SnapObject
                {
                    Id = 6, Schema = "dbo", Name = "Ver", Kind = ObjectKind.Table,
                    Columns =
                    [
                        new SnapColumn { Name = "Id", Type = T("int", "integer"), Pk = true, Ordinal = 1 },
                        new SnapColumn { Name = "Name", Type = T("nvarchar(20)", "text"), Ordinal = 2 },
                        new SnapColumn { Name = "RowVer", Type = T("timestamp", "integer"), Ordinal = 3 },
                        new SnapColumn { Name = "Shape", Type = T("geography", "point"), Nullable = true, Ordinal = 4 },
                    ],
                },
                new SnapObject
                {
                    Id = 7, Schema = "dbo", Name = "Measure", Kind = ObjectKind.Table,
                    Columns = [new SnapColumn { Name = "Reading", Type = T("float", "double precision"), Pk = true, Ordinal = 1 }],
                },
            ],
            Fks = [],
        };
    }

    static IndexInfo[] Indexes(long id) => id == 3 ? [new IndexInfo { Name = "UX_Codes", IsUnique = true, Columns = ["Code"] }] : [];

    /// <summary>Result columns 0..n-1 reading (objectId, column) pairs; null = expression.</summary>
    static (ResultColumn[] Columns, ColumnOrigin?[] Origins) Result(params (long Id, string Column)?[] cols)
    {
        var columns = cols.Select((c, i) => new ResultColumn { Name = c?.Column ?? $"expr{i}", TypeName = "x", ArrowType = "utf8", Ordinal = i }).ToArray();
        return (columns, cols.Select(c => c is { } x ? new ColumnOrigin(x.Id, x.Column) : null).ToArray());
    }

    static ResultEditInfo Analyze(string engine, params (long, string)?[] cols)
    {
        var (columns, origins) = Result(cols);
        return EditabilityAnalyzer.Analyze(D(engine), columns, origins, Snapshot(engine), Indexes);
    }

    // ------------------------------------------------------------------------------------------ editability

    [Theory, MemberData(nameof(Engines))]
    public void Single_table_with_its_primary_key_is_editable(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "Status"), (1, "CustomerId"));
        Assert.True(e.Editable, e.Reason);
        Assert.Equal(["OrderId"], e.KeyColumns);
        Assert.Equal([0], e.KeyOrdinals);
        Assert.Equal("Orders", e.Table);
        Assert.Equal(engine == "pg" ? "xmin" : null, e.TokenKind);
        Assert.NotNull(e.ConcurrencyNote);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Read_only_reasons_name_the_problem(string engine)
    {
        Assert.Equal("Read-only: query reads more than one table (dbo.Codes, " + (engine == "pg" ? "public" : "dbo") + ".Orders)",
            Analyze(engine, (1, "OrderId"), (3, "Code")).Reason);
        Assert.Equal("Read-only: primary key column OrderId is not in the SELECT list", Analyze(engine, (1, "Status")).Reason);
        Assert.Equal("Read-only: dbo.v_Orders is a view", Analyze(engine, (4, "OrderId")).Reason);
        Assert.Equal("Read-only: dbo.Log has no primary key or NOT NULL unique index", Analyze(engine, (2, "Msg")).Reason);
        Assert.Equal("Read-only: the result has no table columns", Analyze(engine, [null]).Reason);
        Assert.StartsWith("Read-only: key column Reading is", Analyze(engine, (7, "Reading")).Reason);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Unknown_origins_or_metadata_are_read_only(string engine)
    {
        var (cols, _) = Result((1, "OrderId"));
        Assert.Equal("Read-only: column origins unknown for this batch", EditabilityAnalyzer.Analyze(D(engine), cols, null, Snapshot(engine), Indexes).Reason);
        Assert.StartsWith("Read-only: no cached metadata", EditabilityAnalyzer.Analyze(D(engine), cols, [new ColumnOrigin(1, "OrderId")], null, Indexes).Reason);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_not_null_unique_index_is_the_key_when_there_is_no_primary_key(string engine)
    {
        var e = Analyze(engine, (3, "Label"), (3, "Code"));
        Assert.True(e.Editable, e.Reason);
        Assert.Equal(["Code"], e.KeyColumns);
        Assert.Equal([1], e.KeyOrdinals);
        Assert.StartsWith("Read-only: dbo.Codes has no primary key, and the columns of unique index UX_Codes", Analyze(engine, (3, "Label")).Reason);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Columns_are_locked_with_a_reason(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "Total"), null, (1, "Status"), (1, "Status"));
        Assert.True(e.Editable);
        Assert.Equal("identity values are generated by the server", e.Columns[0].LockReason);
        Assert.True(e.Columns[0].Key);
        Assert.Equal("computed column", e.Columns[1].LockReason);
        Assert.Equal("computed in the query, not a table column", e.Columns[2].LockReason);
        Assert.False(e.Columns[3].Locked);
        Assert.Equal("Status appears earlier in the result; edit it there", e.Columns[4].LockReason);
    }

    [Fact]
    public void Sql_server_rowversion_is_the_token_and_udts_are_locked()
    {
        var e = Analyze("mssql", (6, "Id"), (6, "Name"), (6, "RowVer"), (6, "Shape"));
        Assert.Equal(("rowversion", "RowVer"), (e.TokenKind, e.TokenColumn));
        Assert.Equal("rowversion columns are set by the server", e.Columns[2].LockReason);
        Assert.Equal("geography values cannot be edited in the grid", e.Columns[3].LockReason);
        Assert.Contains("rowversion", e.ConcurrencyNote);
        Assert.Null(Analyze("mssql", (1, "OrderId")).TokenKind);
    }

    [Fact]
    public void Postgres_always_uses_xmin()
    {
        var e = Analyze("pg", (6, "Id"), (6, "Name"));
        Assert.Equal(("xmin", "xmin"), (e.TokenKind, e.TokenColumn));
        Assert.DoesNotContain(e.Columns, c => c.Locked);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Comparable_columns_exclude_float_and_lobs(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "Status"), (1, "Amount"), (1, "Notes"), (1, "Placed"));
        Assert.True(e.Columns[1].Comparable);
        Assert.False(e.Columns[2].Comparable); // float
        Assert.False(e.Columns[3].Comparable); // ntext / xml
        // datetime2 (scale 7) loses its 7th digit in the grid; timestamp keeps microseconds.
        Assert.Equal(engine == "pg", e.Columns[4].Comparable);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Inserting_needs_every_required_column(string engine)
    {
        Assert.Equal("Inserting needs NOT NULL column CustomerId, which is not in the SELECT list", Analyze(engine, (1, "OrderId"), (1, "Status")).InsertBlockedReason);
        Assert.Null(Analyze(engine, (1, "OrderId"), (1, "CustomerId")).InsertBlockedReason);
    }

    // ------------------------------------------------------------------------------------------ statements

    static GridValue V(string? text) => text == null ? new GridValue { IsNull = true } : new GridValue { Text = text };

    static GridPatchParams Patch(GridRowUpdate[]? updates = null, GridRowInsert[]? inserts = null, GridRowDelete[]? deletes = null) =>
        new() { SessionId = "s", ExecutionId = "e", Updates = updates ?? [], Inserts = inserts ?? [], Deletes = deletes ?? [] };

    static GridRowUpdate Update(int row, string key, (string, string?) change, string? token = null, string? original = "old") => new()
    {
        RowIndex = row, Key = [V(key)], Token = token,
        Changes = new() { [change.Item1] = V(change.Item2) },
        Original = original == null ? [] : new() { [change.Item1] = V(original) },
    };

    [Fact]
    public void Sql_server_update_without_rowversion_compares_the_original_value()
    {
        var e = Analyze("mssql", (1, "OrderId"), (1, "Status"));
        var s = Assert.Single(PatchBuilder.Build(Mssql, e, Patch([Update(4, "17", ("Status", "shipped"))]), force: false));
        Assert.Equal(PatchStatementKind.Update, s.Kind);
        Assert.Equal(4, s.Row);
        Assert.Equal("UPDATE [dbo].[Orders] SET [Status] = @p0 WHERE [OrderId] = @p1 AND ([Status] = @p2 OR ([Status] IS NULL AND @p2 IS NULL))", s.Sql);
        Assert.Equal(["shipped", "17", "old"], s.Values.Select(v => v.Value.Text));
        Assert.Equal(["nvarchar(20)", "int", "nvarchar(20)"], s.Values.Select(v => v.Type));
    }

    [Fact]
    public void Sql_server_update_with_rowversion_matches_the_token()
    {
        var e = Analyze("mssql", (6, "Id"), (6, "Name"));
        var s = Assert.Single(PatchBuilder.Build(Mssql, e, Patch([Update(0, "1", ("Name", "x"), token: "0x00000000000007D1")]), false));
        Assert.Equal("UPDATE [dbo].[Ver] SET [Name] = @p0 WHERE [Id] = @p1 AND [RowVer] = @p2", s.Sql);
        Assert.Equal(("binary(8)", "0x00000000000007D1"), (s.Values[2].Type, s.Values[2].Value.Text));
    }

    [Fact]
    public void Postgres_update_casts_values_and_matches_xmin()
    {
        var e = Analyze("pg", (1, "OrderId"), (1, "Status"));
        var s = Assert.Single(PatchBuilder.Build(Pg, e, Patch([Update(0, "17", ("Status", "shipped"), token: "812")]), false));
        Assert.Equal("""UPDATE "public"."Orders" SET "Status" = $1::character varying(20) WHERE "OrderId" = $2::integer AND xmin = $3::xid""", s.Sql);
        Assert.Null(s.Values[2].Type);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Float_columns_are_not_part_of_the_concurrency_check(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "Amount"), (1, "Status"));
        var p = Patch([Update(0, "1", ("Amount", "2.5"), token: engine == "pg" ? "9" : null, original: "1.5")]);
        var s = Assert.Single(PatchBuilder.Build(D(engine), e, p, false));
        Assert.DoesNotContain(engine == "pg" ? "IS NOT DISTINCT FROM" : "[Amount] IS NULL", s.Sql);
        if (engine == "mssql")
        {
            var service = PatchBuilder.Preview(Mssql, [s], null);
            Assert.Equal("UPDATE [dbo].[Orders] SET [Amount] = 2.5 WHERE [OrderId] = 1;\n", service);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void Delete_and_insert_statements(string engine)
    {
        var d = D(engine);
        var e = Analyze(engine, (1, "OrderId"), (1, "CustomerId"), (1, "Status"));
        var p = Patch(
            inserts: [new GridRowInsert { TempIndex = 0, Values = new() { ["CustomerId"] = V("5"), ["Status"] = new GridValue { UseDefault = true } } }],
            deletes: [new GridRowDelete { RowIndex = 3, Key = [V("9")], Token = engine == "pg" ? "77" : null, Original = new() { ["Status"] = V("new"), ["CustomerId"] = V("5") } }]);
        var list = PatchBuilder.Build(d, e, p, false);
        Assert.Equal([PatchStatementKind.Delete, PatchStatementKind.Insert], list.Select(x => x.Kind));
        if (engine == "pg")
        {
            Assert.Equal("""DELETE FROM "public"."Orders" WHERE "OrderId" = $1::integer AND xmin = $2::xid""", list[0].Sql);
            Assert.Equal("""INSERT INTO "public"."Orders" ("CustomerId") VALUES ($1::integer) RETURNING ("OrderId")::text""", list[1].Sql);
        }
        else
        {
            Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [OrderId] = @p0 AND ([CustomerId] = @p1 OR ([CustomerId] IS NULL AND @p1 IS NULL)) AND ([Status] = @p2 OR ([Status] IS NULL AND @p2 IS NULL))", list[0].Sql);
            Assert.Equal("DECLARE @q_key TABLE (k0 int);\nINSERT INTO [dbo].[Orders] ([CustomerId]) OUTPUT INSERTED.[OrderId] INTO @q_key VALUES (@p0);\nSELECT CAST(k0 AS nvarchar(max)) FROM @q_key;", list[1].Sql);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void Hostile_identifiers_are_quoted(string engine)
    {
        var e = Analyze(engine, (5, "i]d\""), (5, "v;al"));
        var s = Assert.Single(PatchBuilder.Build(D(engine), e, Patch([Update(0, "1", ("v;al", "x"), token: "1")]), force: false));
        Assert.Equal(engine == "pg"
                ? """"UPDATE "we]ird""s"."t;]""x" SET "v;al" = $1::text WHERE "i]d""" = $2::integer AND xmin = $3::xid""""
                : """"UPDATE [we]]ird"s].[t;]]"x] SET [v;al] = @p0 WHERE [i]]d"] = @p1 AND ([v;al] = @p2 OR ([v;al] IS NULL AND @p2 IS NULL))"""",
            s.Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Patches_naming_unknown_or_locked_columns_are_refused(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "Status"), (1, "Total"));
        var d = D(engine);
        string Refused(GridPatchParams p) => Assert.Throws<QuerithmException>(() => PatchBuilder.Build(d, e, p, force: true)).Message;
        Assert.StartsWith("Unknown column Password", Refused(Patch([Update(0, "1", ("Password", "x"))])));
        Assert.StartsWith("Total cannot be written: computed column", Refused(Patch([Update(0, "1", ("Total", "3"))])));
        Assert.StartsWith("OrderId cannot be written: identity", Refused(Patch([Update(0, "1", ("OrderId", "3"))])));
        Assert.Equal("A row key needs 1 value(s), got 2.", Refused(Patch([new GridRowUpdate { Key = [V("1"), V("2")], Changes = new() { ["Status"] = V("a") }, Original = [] }])));
        Assert.StartsWith("Inserting needs NOT NULL column CustomerId", Refused(Patch(inserts: [new GridRowInsert { Values = new() { ["Status"] = V("a") } }])));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Not_null_columns_refuse_null(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "CustomerId"));
        var ex = Assert.Throws<QuerithmException>(() => PatchBuilder.Build(D(engine), e, Patch([Update(0, "1", ("CustomerId", null), token: "1", original: "2")]), false));
        Assert.Equal("CustomerId does not allow NULL.", ex.Message);
    }

    [Fact]
    public void Token_is_required_when_the_table_has_one()
    {
        var e = Analyze("pg", (1, "OrderId"), (1, "Status"));
        Assert.Throws<QuerithmException>(() => PatchBuilder.Build(Pg, e, Patch([Update(0, "1", ("Status", "x"), token: null)]), false));
        // Overwrite (after a conflict) matches on the key only.
        var s = Assert.Single(PatchBuilder.Build(Pg, e, Patch([Update(0, "1", ("Status", "x"), token: null)]), force: true));
        Assert.EndsWith("""WHERE "OrderId" = $2::integer""", s.Sql);
    }

    [Fact]
    public void Original_values_are_required_without_a_token()
    {
        var e = Analyze("mssql", (1, "OrderId"), (1, "Status"));
        var ex = Assert.Throws<QuerithmException>(() => PatchBuilder.Build(Mssql, e, Patch([Update(0, "1", ("Status", "x"), original: null)]), false));
        Assert.Contains("original value of Status", ex.Message);
    }

    [Fact]
    public void Preview_inlines_literals_without_confusing_p1_and_p10()
    {
        var e = Analyze("mssql", (5, "i]d\""), (5, "v;al"));
        var stmt = new PatchStatement(PatchStatementKind.Update, 0, "SELECT @p1, @p10, @p0",
            Enumerable.Range(0, 11).Select(i => new PatchValue("int", V(i.ToString()))).ToList());
        Assert.Equal("SELECT 1, 10, 0;\n", PatchBuilder.Preview(Mssql, [stmt], null));
        var pgStmt = new PatchStatement(PatchStatementKind.Update, 0, "SELECT $1::text, $10::text", Enumerable.Range(0, 10).Select(i => new PatchValue("text", V($"it's {i}"))).ToList());
        Assert.Equal("SELECT 'it''s 0'::text, 'it''s 9'::text;\n", PatchBuilder.Preview(Pg, [pgStmt], null));
        Assert.True(e.Editable);
    }

    [Theory]
    [InlineData("nvarchar(20)", "N'it''s'", "it's")]
    [InlineData("varchar(10)", "'x'", "x")]
    [InlineData("int", "42", "42")]
    [InlineData("int", "'4 2'", "4 2")]
    [InlineData("varbinary(16)", "0xABCD", "0xABCD")]
    public void Sql_server_preview_literals(string type, string expected, string text) =>
        Assert.Equal(expected, Mssql.PreviewLiteral(type, V(text)));

    // ------------------------------------------------------------------------------------------ values

    [Theory]
    [InlineData("int", "42", 42)]
    [InlineData("bigint", "-9000000000", -9000000000L)]
    [InlineData("bit", "1", true)]
    [InlineData("bit", "false", false)]
    [InlineData("nvarchar(10)", " padded ", " padded ")]
    public void Sql_server_text_converts_to_typed_values(string type, string text, object expected) =>
        Assert.Equal(expected, SqlServerDataEdit.Convert(SqlServerDataEdit.TypeSpec.Parse(type), text));

    [Fact]
    public void Sql_server_temporal_decimal_guid_and_binary_values()
    {
        object C(string type, string text) => SqlServerDataEdit.Convert(SqlServerDataEdit.TypeSpec.Parse(type), text);
        Assert.Equal(new DateTime(2026, 10, 21, 13, 5, 6, 123), C("datetime2(3)", "2026-10-21 13:05:06.123"));
        Assert.Equal(new DateTime(2026, 10, 21), C("date", "2026-10-21"));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(330)), C("datetimeoffset", "2026-01-02 03:04:05 +05:30"));
        Assert.Equal(new TimeSpan(0, 10, 0, 0, 250), C("time(3)", "10:00:00.25"));
        Assert.Equal("12345678901234567890.12", C("decimal(38,2)", "12345678901234567890.12").ToString());
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), C("uniqueidentifier", "6F9619FF-8B86-D011-B42D-00C04FC964FF"));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, C("varbinary(10)", "0xABCD"));
    }

    [Theory]
    [InlineData("int", "abc", "'abc' is not a valid int value.")]
    [InlineData("tinyint", "300", "'300' is not a valid tinyint value.")]
    [InlineData("date", "21/10/2026", "'21/10/2026' is not a valid date value.")]
    [InlineData("varbinary(max)", "0xABCD…", "'0xABCD…' is not a valid varbinary value.")]
    public void Sql_server_bad_values_fail_with_a_readable_message(string type, string text, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => SqlServerDataEdit.Convert(SqlServerDataEdit.TypeSpec.Parse(type), text)).Message);

    [Fact]
    public void Sql_server_parameters_carry_the_column_type()
    {
        using var cmd = new Microsoft.Data.SqlClient.SqlCommand();
        Mssql.AddValue(cmd, 0, "decimal(10,3)", V("1.5"));
        Mssql.AddValue(cmd, 1, "varchar(max)", V("x"));
        Mssql.AddValue(cmd, 2, "nchar(4)", V(null));
        var p = cmd.Parameters;
        Assert.Equal((System.Data.SqlDbType.Decimal, (byte)10, (byte)3), (p[0].SqlDbType, p[0].Precision, p[0].Scale));
        Assert.Equal((System.Data.SqlDbType.VarChar, -1), (p[1].SqlDbType, p[1].Size));
        Assert.Equal((System.Data.SqlDbType.NChar, 4, (object)DBNull.Value), (p[2].SqlDbType, p[2].Size, p[2].Value));
        Assert.Equal("@p2", p[2].ParameterName);
    }

    [Fact]
    public void Postgres_values_travel_as_text_and_bytea_switches_prefix()
    {
        using var cmd = new Npgsql.NpgsqlCommand();
        Pg.AddValue(cmd, 0, "bytea", V("0xDEAD"));
        Pg.AddValue(cmd, 1, "integer[]", V("{1,2}"));
        Pg.AddValue(cmd, 2, "text", V(null));
        Assert.Equal("\\xDEAD", cmd.Parameters[0].Value);
        Assert.Equal("{1,2}", cmd.Parameters[1].Value);
        Assert.Equal(DBNull.Value, cmd.Parameters[2].Value);
        Assert.Throws<FormatException>(() => Pg.AddValue(cmd, 3, "bytea", V("0xDE…")));
        Assert.Equal("$4::numeric(10,2)", Pg.TypedPlaceholder(3, "numeric(10,2)"));
        Assert.Equal("$4", Pg.TypedPlaceholder(3, "int; DROP TABLE x"));
    }

    [Fact]
    public void Postgres_comparability_by_base_type()
    {
        Assert.True(Pg.Comparable("character varying(20)"));
        Assert.True(Pg.Comparable("integer[]"));
        Assert.False(Pg.Comparable("json"));
        Assert.True(Pg.Comparable("jsonb"));
        Assert.False(Pg.Comparable("double precision"));
        Assert.False(Pg.ExactKey("real"));
        Assert.True(Pg.ExactKey("uuid"));
    }

    [Theory]
    [InlineData("datetime2", false)]
    [InlineData("datetime2(6)", true)]
    [InlineData("time", false)]
    [InlineData("datetimeoffset(3)", true)]
    [InlineData("varbinary(16)", true)]
    [InlineData("varbinary(max)", false)]
    [InlineData("float", false)]
    [InlineData("uniqueidentifier", true)]
    public void Sql_server_exact_keys(string type, bool exact) => Assert.Equal(exact, Mssql.ExactKey(type));

    [Theory, MemberData(nameof(Engines))]
    public void Composite_primary_keys_bind_all_key_columns_in_order(string engine)
    {
        var pg = engine == "pg";
        var snap = new SchemaSnapshot
        {
            ProfileId = "p", Database = "db", Status = SnapshotStatus.Fresh,
            Objects =
            [
                new SnapObject
                {
                    Id = 10, Schema = pg ? "public" : "dbo", Name = "OrderLines", Kind = ObjectKind.Table,
                    Columns =
                    [
                        new SnapColumn { Name = "OrderId", Type = pg ? "integer" : "int", Pk = true, Ordinal = 1 },
                        new SnapColumn { Name = "LineNo", Type = pg ? "smallint" : "smallint", Pk = true, Ordinal = 2 },
                        new SnapColumn { Name = "Qty", Type = pg ? "integer" : "int", Nullable = false, Ordinal = 3 },
                    ],
                },
            ],
            Fks = [],
        };
        var (cols, origins) = Result((10, "Qty"), (10, "LineNo"), (10, "OrderId"));
        var e = EditabilityAnalyzer.Analyze(D(engine), cols, origins, snap, _ => []);
        Assert.True(e.Editable, e.Reason);
        Assert.Equal(["OrderId", "LineNo"], e.KeyColumns);
        Assert.Equal([2, 1], e.KeyOrdinals);

        var p = Patch(
            updates: [new GridRowUpdate { RowIndex = 0, Key = [V("100"), V("2")], Token = pg ? "42" : null, Changes = new() { ["Qty"] = V("5") }, Original = new() { ["Qty"] = V("3") } }],
            deletes: [new GridRowDelete { RowIndex = 1, Key = [V("100"), V("3")], Token = pg ? "43" : null, Original = new() { ["Qty"] = V("1") } }]);
        var stmts = PatchBuilder.Build(D(engine), e, p, force: false);
        Assert.Equal(2, stmts.Count);
        if (pg)
        {
            Assert.Contains("""WHERE "OrderId" = $1::integer AND "LineNo" = $2::smallint AND xmin = $3::xid""", stmts[0].Sql);
            Assert.Contains("""WHERE "OrderId" = $2::integer AND "LineNo" = $3::smallint AND xmin = $4::xid""", stmts[1].Sql);
        }
        else
        {
            Assert.Contains("WHERE [OrderId] = @p0 AND [LineNo] = @p1 AND ([Qty] = @p2", stmts[0].Sql);
            Assert.Contains("WHERE [OrderId] = @p1 AND [LineNo] = @p2 AND ([Qty] = @p3", stmts[1].Sql);
        }

        // Force-overwrite omits optimistic concurrency checks on both engines.
        var forced = PatchBuilder.Build(D(engine), e, p, force: true);
        Assert.DoesNotContain(pg ? "xmin" : "[Qty] IS NULL", forced[0].Sql);
        Assert.DoesNotContain(pg ? "xmin" : "[Qty] IS NULL", forced[1].Sql);
    }

    [Theory, MemberData(nameof(Engines))]
    public void Insert_with_all_defaults_emits_default_values_clause(string engine)
    {
        var e = Analyze(engine, (1, "OrderId"), (1, "CustomerId"), (1, "Status"));
        var p = Patch(inserts:
        [
            new GridRowInsert
            {
                TempIndex = 0,
                Values = new()
                {
                    ["CustomerId"] = new GridValue { UseDefault = true },
                    ["Status"] = new GridValue { UseDefault = true },
                },
            },
        ]);
        var s = Assert.Single(PatchBuilder.Build(D(engine), e, p, force: false));
        Assert.Contains("DEFAULT VALUES", s.Sql);
        Assert.Empty(s.Values);
    }
}
