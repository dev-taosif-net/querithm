using System.IO.Compression;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Core;
using Querithm.Core.Import;
using Querithm.Providers.Postgres;
using Querithm.Providers.SqlServer;

namespace Querithm.Tests;

/// <summary>Import wizard: reading files, inferring types over the whole file, and per-engine target checks.</summary>
public sealed class ImportTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qimport-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    string Write(string name, string text, Encoding? enc = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, enc ?? new UTF8Encoding(false));
        return path;
    }

    static Inspection Inspect(string path, ImportReadOptions? o = null) => FileInspector.Inspect("i", path, o, (_, _, _) => { }, CancellationToken.None);

    // ------------------------------------------------------------------------------------------ delimited text

    [Fact]
    public void Csv_reader_handles_quotes_embedded_newlines_and_line_ends()
    {
        var r = new DelimitedReader(new StringReader("a,\"b,1\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",,x\rlast,\"\",z"), ',');
        Assert.Equal(["a", "b,1", "say \"hi\""], r.ReadRecord()!);
        Assert.Equal(1, r.Line);
        Assert.Equal(["multi\nline", "", "x"], r.ReadRecord()!);
        Assert.Equal(2, r.Line);
        Assert.Equal(["last", "", "z"], r.ReadRecord()!);
        Assert.Equal(4, r.Line);
        Assert.Null(r.ReadRecord());
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6\n", ',')]
    [InlineData("a;b;c\n1,5;2;3\n4;5;6\n", ';')]
    [InlineData("a\tb\n1\t\"x,y,z\"\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    public void Delimiter_is_detected_outside_quotes(string sample, char expected) => Assert.Equal(expected, DelimitedReader.DetectDelimiter(sample));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"x\"", "\"say \"\"x\"\"\"")]
    [InlineData(" lead", "\" lead\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Csv_escape(string value, string expected) => Assert.Equal(expected, DelimitedReader.Escape(value, ','));

    // ------------------------------------------------------------------------------------------ type inference

    [Theory]
    [InlineData("42", ImportKind.Int32)]
    [InlineData("-2147483649", ImportKind.Int64)]
    [InlineData("99999999999999999999", ImportKind.Decimal)]
    [InlineData("007", ImportKind.Text)] // leading zeros: an identifier, not a number
    [InlineData("0", ImportKind.Int32)]
    [InlineData("0.5", ImportKind.Decimal)]
    [InlineData("1e10", ImportKind.Double)]
    [InlineData("true", ImportKind.Bool)]
    [InlineData("1", ImportKind.Int32)] // 1/0 are numbers, not booleans
    [InlineData("2026-10-21", ImportKind.Date)]
    [InlineData("2026-02-30", ImportKind.Text)]
    [InlineData("2026-10-21 13:05:06.1234567", ImportKind.DateTime)]
    [InlineData("2026-10-21T13:05", ImportKind.DateTime)]
    [InlineData("2026-10-21 13:05:06+05:30", ImportKind.DateTimeOffset)]
    [InlineData("2026-10-21T13:05:06Z", ImportKind.DateTimeOffset)]
    [InlineData("13:05:06.25", ImportKind.Time)]
    [InlineData("25:00", ImportKind.Text)]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", ImportKind.Uuid)]
    [InlineData("21/10/2026", ImportKind.Text)] // culture-specific forms are never guessed
    [InlineData("1,5", ImportKind.Text)]
    public void Values_are_classified_exactly(string value, ImportKind kind) => Assert.Equal(kind, ValueClassifier.Classify(value).Kind);

    [Theory]
    [InlineData(ImportKind.Int32, ImportKind.Int64, ImportKind.Int64)]
    [InlineData(ImportKind.Int64, ImportKind.Decimal, ImportKind.Decimal)]
    [InlineData(ImportKind.Decimal, ImportKind.Double, ImportKind.Double)]
    [InlineData(ImportKind.Date, ImportKind.DateTime, ImportKind.DateTime)]
    [InlineData(ImportKind.DateTime, ImportKind.DateTimeOffset, ImportKind.Text)]
    [InlineData(ImportKind.Bool, ImportKind.Int32, ImportKind.Text)]
    [InlineData(ImportKind.Empty, ImportKind.Uuid, ImportKind.Uuid)]
    public void Kinds_join_on_the_lattice(ImportKind a, ImportKind b, ImportKind expected)
    {
        Assert.Equal(expected, ColumnProfiler.Join(a, b));
        Assert.Equal(expected, ColumnProfiler.Join(b, a));
    }

    [Fact]
    public void Profiles_track_precision_nulls_length_range_and_candidate_keys()
    {
        var p = new ColumnProfiler("amount", 0);
        p.Add("1.5", 1);
        p.Add("1234.125", 2);
        p.Add(null, 3);
        p.Add("7", 4);
        var b = p.Build();
        Assert.Equal((ImportKind.Decimal, 7, 3), (b.Kind, b.Precision, b.Scale));
        Assert.Equal(1L, b.NullCount);
        Assert.Equal([3L], b.FirstNullRows);
        Assert.Equal(8, b.MaxLength);
        Assert.False(b.CandidateKey);
        var id = new ColumnProfiler("id", 0);
        for (var i = 1; i <= 5000; i++) id.Add(i.ToString(), i);
        var ib = id.Build();
        Assert.Equal((ImportKind.Int32, "1", "5000"), (ib.Kind, ib.Min, ib.Max));
        Assert.True(ib.CandidateKey);
        Assert.InRange(ib.DistinctEstimate, 4800, 5200);
    }

    [Fact]
    public void The_whole_file_is_profiled_so_a_late_long_value_is_found()
    {
        var sb = new StringBuilder("id,code,note\n");
        for (var i = 1; i <= 50_000; i++) sb.Append(i).Append(',').Append(i == 48_392 ? "X-LONG-CODE-THAT-NO-SAMPLE-WOULD-SEE" : "AB12").Append(",n\n");
        var ins = Inspect(Write("late.csv", sb.ToString()));
        Assert.Equal(50_000, ins.RowCount);
        var code = Assert.Single(ins.Columns, c => c.Name == "code");
        Assert.Equal(36, code.MaxLength);
        Assert.Equal(ImportKind.Int32, ins.Columns[0].Kind);
        Assert.Equal(FileInspector.PreviewRows, ins.Preview.Count);
    }

    // ------------------------------------------------------------------------------------------ file detection

    [Fact]
    public void Header_detection_and_column_names()
    {
        Assert.True(FileInspector.LooksLikeHeader(["id", "name"], ["1", "x"]));
        Assert.False(FileInspector.LooksLikeHeader(["1", "name"], ["2", "x"]));
        Assert.False(FileInspector.LooksLikeHeader(["id", ""], null));
        Assert.False(FileInspector.LooksLikeHeader(["a", "A"], null));
        Assert.Equal(["id", "Column2", "ID_2", "Column4"], RowSourceFactory.NameColumns(["id", " ", "ID", null], 4));
        var noHeader = Inspect(Write("data.csv", "1,alpha\n2,beta\n"));
        Assert.False(noHeader.Plan.HasHeader);
        Assert.Equal(["Column1", "Column2"], noHeader.Plan.Columns);
        Assert.Equal(2, noHeader.RowCount);
    }

    [Fact]
    public void Encodings_are_detected_from_bom_and_content()
    {
        Assert.IsType<UTF8Encoding>(FileInspector.DetectEncoding([0xEF, 0xBB, 0xBF, (byte)'a'], null));
        Assert.Equal("utf-16le", FileInspector.EncodingName(FileInspector.DetectEncoding([0xFF, 0xFE, (byte)'a', 0], null)));
        Assert.Equal("utf-16be", FileInspector.EncodingName(FileInspector.DetectEncoding([0xFE, 0xFF, 0, (byte)'a'], null)));
        Assert.Equal("latin1", FileInspector.EncodingName(FileInspector.DetectEncoding("café ok"u8.ToArray().Select(b => b).Concat(new byte[] { 0xE9, (byte)'x' }).ToArray(), null)));
        // A multi-byte character cut by the sample end is still UTF-8.
        var cut = Encoding.UTF8.GetBytes("naïve €")[..^1];
        Assert.Equal("utf-8", FileInspector.EncodingName(FileInspector.DetectEncoding(cut, null)));
        var latin = Inspect(Write("latin.csv", "name\ncafé\n", Encoding.Latin1));
        Assert.Equal("latin1", FileInspector.EncodingName(latin.Plan.Encoding));
        Assert.Equal("café", latin.Preview[0][0]);
        var utf16 = Inspect(Write("u16.csv", "name;city\nZoë;Köln\n", new UnicodeEncoding(false, true)));
        Assert.Equal(("Zoë", "Köln", ';'), (utf16.Preview[0][0], utf16.Preview[0][1], utf16.Plan.Delimiter));
    }

    [Fact]
    public void Null_values_padding_and_extra_fields()
    {
        var ins = Inspect(Write("nulls.csv", "a,b,c\n1,NULL,x\n2,,\n3\n4,5,6,7,8\n"));
        Assert.Equal(new string?[] { "3", null, null }, ins.Preview[2].AsEnumerable());
        Assert.Null(ins.Preview[0][1]);
        Assert.Equal(3, ins.Columns.Single(c => c.Name == "b").NullCount);
        Assert.Contains(ins.Notes, n => n.Contains("more fields"));
        var keep = Inspect(Write("keep.csv", "a\nNULL\n"), new ImportReadOptions { NullValues = [""] });
        Assert.Equal("NULL", keep.Preview[0][0]);
    }

    [Fact]
    public void Json_arrays_and_ndjson_collect_every_key()
    {
        var arr = Inspect(Write("a.json", """[{"id":1,"name":"x","tags":["a","b"]},{"id":2,"extra":true, "name":null}]"""));
        Assert.Equal(ImportFormat.Json, arr.Plan.Format);
        Assert.Equal(["id", "name", "tags", "extra"], arr.Plan.Columns);
        Assert.Equal("[\"a\",\"b\"]", arr.Preview[0][2]);
        Assert.Equal(new string?[] { "2", null, null, "true" }, arr.Preview[1].AsEnumerable());
        var extra = arr.Columns.Single(c => c.Name == "extra");
        Assert.Equal((ImportKind.Bool, 1L), (extra.Kind, extra.NullCount)); // row 1 had no "extra"
        var nd = Inspect(Write("b.ndjson", "{\"id\": 1}\n\n{\"id\": 2, \"at\": \"2026-10-21\"}\n"));
        Assert.Equal(ImportFormat.Ndjson, nd.Plan.Format);
        Assert.Equal(2, nd.RowCount);
        Assert.Equal(ImportKind.Date, nd.Columns.Single(c => c.Name == "at").Kind);
        // A .json file holding one object per line is read as NDJSON.
        Assert.Equal(ImportFormat.Ndjson, Inspect(Write("c.json", "{\"a\":1}\n{\"a\":2}\n")).Plan.Format);
    }

    /// <summary>A small workbook written by hand: shared and inline strings, numbers, booleans, a date style, a formula, a gap.</summary>
    string Xlsx()
    {
        var path = Path.Combine(_dir, "book.xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(xml);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Part("xl/workbook.xml", $"""<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Orders" sheetId="1" r:id="rId1"/><sheet name="Other" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="worksheets/sheet2.xml"/></Relationships>""");
        Part("xl/sharedStrings.xml", $"""<sst xmlns="{ns}"><si><t>id</t></si><si><t>name</t></si><si><t>when</t></si><si><t>ok</t></si><si><r><t>Ac</t></r><r><t>me</t></r></si></sst>""");
        Part("xl/styles.xml", $"""<styleSheet xmlns="{ns}"><numFmts><numFmt numFmtId="164" formatCode="yyyy\-mm\-dd"/></numFmts><cellXfs><xf numFmtId="0"/><xf numFmtId="164"/><xf numFmtId="22"/></cellXfs></styleSheet>""");
        Part("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="{ns}"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c></row>
            <row r="2"><c r="A2"><v>1</v></c><c r="B2" t="s"><v>4</v></c><c r="C2" s="1"><v>46316</v></c><c r="D2" t="b"><v>1</v></c></row>
            <row r="3"><c r="A3"><f>A2+1</f><v>2</v></c><c r="B3" t="inlineStr"><is><t>Bolt</t></is></c><c r="C3" s="2"><v>46316.5</v></c></row>
            <row r="4"/>
            </sheetData></worksheet>
            """);
        Part("xl/worksheets/sheet2.xml", $"""<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="B1" t="str"><v>only</v></c></row></sheetData></worksheet>""");
        return path;
    }

    [Fact]
    public void Xlsx_shared_inline_dates_booleans_formulas_and_sheets()
    {
        var ins = Inspect(Xlsx());
        Assert.Equal(ImportFormat.Xlsx, ins.Plan.Format);
        Assert.Equal(["Orders", "Other"], ins.Sheets);
        Assert.Equal(["id", "name", "when", "ok"], ins.Plan.Columns);
        Assert.Equal(2, ins.RowCount); // the empty row 4 is skipped
        Assert.Equal(new string?[] { "1", "Acme", "2026-10-21", "true" }, ins.Preview[0].AsEnumerable());
        Assert.Equal(new string?[] { "2", "Bolt", "2026-10-21 12:00:00", null }, ins.Preview[1].AsEnumerable());
        Assert.Contains(ins.Notes, n => n.Contains("formulas"));
        Assert.Equal(ImportKind.DateTime, ins.Columns[2].Kind);
        var other = Inspect(ins.FilePath, new ImportReadOptions { Sheet = "Other", HasHeader = false });
        Assert.Equal(new string?[] { null, "only" }, other.Preview[0].AsEnumerable());
    }

    [Theory]
    [InlineData("yyyy-mm-dd", true)]
    [InlineData("[$-409]mmm d, yyyy", true)]
    [InlineData("0.00", false)]
    [InlineData("\"day\" 0", false)]
    [InlineData("[Red]0.00", false)]
    public void Xlsx_date_formats(string code, bool date) => Assert.Equal(date, XlsxReader.IsDateFormat(code));

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("XFD1", 16383)]
    public void Xlsx_column_letters(string reference, int index) => Assert.Equal(index, XlsxReader.ColumnIndex(reference));

    // ------------------------------------------------------------------------------------------ engine checks

    static readonly IImportDialect Mssql = new SqlServerImport();
    static readonly IImportDialect Pg = new PostgresImport();

    static ImportColumnProfile P(ImportKind kind, int maxLen = 5, int precision = 0, int scale = 0, string? min = null, string? max = null, long nulls = 0) =>
        new() { Name = "c", Kind = kind, MaxLength = maxLen, Precision = precision, Scale = scale, Min = min, Max = max, NullCount = nulls };

    static SnapColumn T(string type, bool nullable = true) => new() { Name = "c", Type = type, Nullable = nullable };

    [Theory]
    [InlineData("mssql", ImportKind.Int32, "int")]
    [InlineData("mssql", ImportKind.Decimal, "decimal(7,3)")]
    [InlineData("mssql", ImportKind.DateTime, "datetime2(3)")]
    [InlineData("mssql", ImportKind.Uuid, "uniqueidentifier")]
    [InlineData("mssql", ImportKind.Text, "nvarchar(50)")]
    [InlineData("pg", ImportKind.Int64, "bigint")]
    [InlineData("pg", ImportKind.Decimal, "numeric(7,3)")]
    [InlineData("pg", ImportKind.DateTimeOffset, "timestamptz")]
    [InlineData("pg", ImportKind.Bool, "boolean")]
    [InlineData("pg", ImportKind.Text, "text")]
    public void New_table_types(string engine, ImportKind kind, string expected) =>
        Assert.Equal(expected, (engine == "pg" ? Pg : Mssql).TypeFor(P(kind, maxLen: 32, precision: kind == ImportKind.Decimal ? 7 : 3, scale: 3)));

    [Fact]
    public void Sql_server_text_sizes_round_up()
    {
        Assert.Equal("nvarchar(255)", Mssql.TypeFor(P(ImportKind.Text, maxLen: 120)));
        Assert.Equal("nvarchar(max)", Mssql.TypeFor(P(ImportKind.Text, maxLen: 5000)));
    }

    [Theory]
    [InlineData("mssql", "varchar(20)", ImportVerdictKind.Widen, "varchar(50)")]
    [InlineData("mssql", "nvarchar(max)", ImportVerdictKind.Fits, null)]
    [InlineData("pg", "character varying(20)", ImportVerdictKind.Widen, "varchar(32)")]
    [InlineData("pg", "text", ImportVerdictKind.Fits, null)]
    public void Long_text_needs_widening(string engine, string target, ImportVerdictKind kind, string? widen)
    {
        var fit = (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.Text, maxLen: 32), T(target));
        Assert.Equal((kind, widen), (fit.Kind, fit.WidenTo));
    }

    [Theory]
    [InlineData("mssql", "int", "bigint")]
    [InlineData("mssql", "smallint", "bigint")]
    [InlineData("pg", "integer", "bigint")]
    public void Out_of_range_numbers_widen(string engine, string target, string widen)
    {
        var fit = (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.Int64, min: "1", max: "9000000000"), T(target));
        Assert.Equal((ImportVerdictKind.Widen, widen), (fit.Kind, fit.WidenTo));
        Assert.Equal(ImportVerdictKind.Fits, (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.Int32, min: "-5", max: "5"), T(target)).Kind);
    }

    [Theory]
    [InlineData("mssql", "decimal(5,2)", "decimal(8,3)")]
    [InlineData("pg", "numeric(5,2)", "numeric(8,3)")]
    public void Decimals_widen_precision_and_scale(string engine, string target, string widen)
    {
        var fit = (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.Decimal, precision: 8, scale: 3), T(target));
        Assert.Equal((ImportVerdictKind.Widen, widen), (fit.Kind, fit.WidenTo));
        Assert.Equal(ImportVerdictKind.Fits, (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.Decimal, precision: 4, scale: 2), T(target)).Kind);
    }

    [Theory]
    [InlineData("mssql", "int", ImportKind.Text)]
    [InlineData("mssql", "uniqueidentifier", ImportKind.Int32)]
    [InlineData("mssql", "datetime2", ImportKind.DateTimeOffset)]
    [InlineData("pg", "integer", ImportKind.Date)]
    [InlineData("pg", "uuid", ImportKind.Text)]
    [InlineData("pg", "boolean", ImportKind.Decimal)]
    public void Incompatible_values_are_mismatches(string engine, string target, ImportKind kind) =>
        Assert.Equal(ImportVerdictKind.Mismatch, (engine == "pg" ? Pg : Mssql).Check(P(kind), T(target)).Kind);

    [Theory]
    [InlineData("mssql", "date", "datetime2(0)")]
    [InlineData("pg", "date", "timestamp")]
    public void A_time_of_day_into_a_date_column_widens(string engine, string target, string widen)
    {
        var fit = (engine == "pg" ? Pg : Mssql).Check(P(ImportKind.DateTime), T(target));
        Assert.Equal((ImportVerdictKind.Widen, widen), (fit.Kind, fit.WidenTo));
    }

    [Fact]
    public void Booleans_accept_zero_one_columns_and_unknown_types_pass_to_the_server()
    {
        Assert.Equal(ImportVerdictKind.Fits, Mssql.Check(P(ImportKind.Int32, min: "0", max: "1"), T("bit")).Kind);
        Assert.Equal(ImportVerdictKind.Mismatch, Mssql.Check(P(ImportKind.Int32, min: "0", max: "2"), T("bit")).Kind);
        Assert.Equal(ImportVerdictKind.Fits, Pg.Check(P(ImportKind.Int32, min: "0", max: "1"), T("boolean")).Kind);
        Assert.Contains("server converts", Mssql.Check(P(ImportKind.Text), T("geography")).Message);
        Assert.Contains("array", Pg.Check(P(ImportKind.Text), T("integer[]")).Message);
        Assert.Equal(ImportVerdictKind.Fits, Pg.Check(P(ImportKind.Empty), T("integer")).Kind);
    }

    [Fact]
    public void Ddl_quotes_hostile_names_per_engine()
    {
        var cols = new[] { ("i]d\"", "int", true), ("v;al", "text", false) };
        Assert.Equal("CREATE TABLE [s]]x].[t;x] (\n    [i]]d\"] int NOT NULL,\n    [v;al] text NULL,\n    CONSTRAINT [PK_t;x] PRIMARY KEY ([i]]d\"])\n);",
            Mssql.CreateTable("s]x", "t;x", cols, ["i]d\""]));
        Assert.Equal("CREATE TABLE \"s]x\".\"t;x\" (\n    \"i]d\"\"\" int NOT NULL,\n    \"v;al\" text\n);", Pg.CreateTable("s]x", "t;x", cols, []));
        Assert.Equal("ALTER TABLE [dbo].[t] ALTER COLUMN [c] nvarchar(50) NOT NULL;", Mssql.AlterColumnType("dbo", "t", T("nvarchar(20)", nullable: false), "nvarchar(50)"));
        Assert.Equal("ALTER TABLE \"public\".\"t\" ALTER COLUMN \"c\" TYPE varchar(50);", Pg.AlterColumnType("public", "t", T("varchar(20)"), "varchar(50)"));
        Assert.Equal("ALTER TABLE \"public\".\"t\" ADD COLUMN \"n\" text;", Pg.AddColumn("public", "t", "n", "text"));
    }

    [Theory]
    [InlineData("integer", "12", null)]
    [InlineData("integer", "12.5", "not a valid integer")]
    [InlineData("smallint", "40000", "not a valid smallint")]
    [InlineData("numeric(10,2)", "NaN", null)]
    [InlineData("boolean", "yes", null)]
    [InlineData("boolean", "maybe", "not a boolean")]
    [InlineData("timestamptz", "2026-10-21 10:00+02:00", null)]
    [InlineData("date", "21.10.2026", "not a date")]
    [InlineData("uuid", "nope", "not a UUID")]
    [InlineData("character varying(3)", "abcd", "column holds 3")]
    [InlineData("public.mood", "happy", null)]
    [InlineData("integer[]", "{1,2}", null)]
    public void Postgres_rows_are_checked_before_copy(string type, string value, string? reason)
    {
        var why = PostgresImport.Validate(type, value);
        if (reason == null) Assert.Null(why);
        else Assert.Contains(reason, why);
    }

    [Fact]
    public void Auto_mapping_matches_by_name_then_ignoring_separators()
    {
        var file = new[] { P(ImportKind.Int32) with { Name = "Order ID" }, P(ImportKind.Text) with { Name = "name" }, P(ImportKind.Text) with { Name = "extra" } };
        var target = new[] { T("int") with { Name = "order_id" }, T("text") with { Name = "Name" } };
        var map = ImportService.AutoMap(file, target, null);
        Assert.Equal(["order_id", "Name", null], map.Select(m => m.TargetColumn));
    }

    // ------------------------------------------------------------------------------------------ security

    [Fact]
    public async Task Paths_not_injected_by_the_shell_are_refused()
    {
        await using var h = new TestHost();
        var none = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t" }));
        Assert.Equal("fileToken", none.Code);
        var relative = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t", FilePath = "data.csv" }));
        Assert.Equal("fileToken", relative.Code);
        var dir = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t", FilePath = _dir }));
        Assert.Equal("importFile", dir.Code);
        var ok = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t", FilePath = Write("ok.csv", "a\n1\n") });
        Assert.Equal(1, ok.RowCount);
        Assert.NotEmpty(ok.PreviewExecutionId);
    }

    [Fact]
    public async Task A_run_must_use_the_inspected_file()
    {
        await using var h = new TestHost();
        var a = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t1", FilePath = Write("a.csv", "x\n1\n") });
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Pg, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x" });
        var ex = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = s.SessionId, InspectionId = a.InspectionId, FileToken = "t2", FilePath = Write("b.csv", "x\n2\n"), Schema = "public", Table = "t",
        }));
        Assert.Equal("fileToken", ex.Code);
    }

    [Fact]
    public async Task Check_target_reports_new_table_ddl_widening_missing_columns_and_required_unmapped()
    {
        await using var h = new TestHost();
        var path = Write("orders.csv", "order_id,customer_name,notes,extra_col\n1,Alice,ok,x\n2,,long_value_exceeding_ten_chars,y\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t1", FilePath = path });
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = Engine.Mssql, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });

        // 1. New table plan with a nullable column selected as PK flags NullIntoNotNull.
        var newPlan = await h.Rpc.ImportCheckTarget(new ImportTargetParams
        {
            SessionId = s.SessionId, InspectionId = ins.InspectionId, Schema = "dbo", Table = "NewOrders", NewTable = true, PrimaryKey = ["customer_name"],
        });
        Assert.False(newPlan.TableExists);
        Assert.Contains("CREATE TABLE [dbo].[NewOrders]", newPlan.CreateTableSql);
        Assert.Contains(newPlan.Verdicts, v => v.FileColumn == "customer_name" && v.Kind == ImportVerdictKind.NullIntoNotNull);

        // 2. Existing table in snapshot: checks widening, null-into-not-null, missing target column, and required unmapped target column.
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [new ObjectStamp(1, "dbo", "Orders", ObjectKind.Table, 1, null)], [],
            new MetadataBundle(
            [
                new ColumnRow(1, 1, "order_id", "int", null, null, null, false, true, false, null),
                new ColumnRow(1, 2, "customer_name", "nvarchar", 50, null, null, false, false, false, null),
                new ColumnRow(1, 3, "notes", "varchar", 10, null, null, true, false, false, null),
                new ColumnRow(1, 4, "required_code", "varchar", 10, null, null, false, false, false, null),
            ], [], [], []), "16");
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");

        var existingPlan = await h.Rpc.ImportCheckTarget(new ImportTargetParams
        {
            SessionId = s.SessionId, InspectionId = ins.InspectionId, Schema = "dbo", Table = "Orders", NewTable = false,
        });
        Assert.True(existingPlan.TableExists);
        Assert.Contains(existingPlan.Verdicts, v => v.TargetColumn == "notes" && v.Kind == ImportVerdictKind.Widen && v.SuggestedSql!.Contains("ALTER COLUMN [notes]"));
        Assert.Contains(existingPlan.Verdicts, v => v.TargetColumn == "customer_name" && v.Kind == ImportVerdictKind.NullIntoNotNull);
        Assert.Contains(existingPlan.Verdicts, v => v.FileColumn == "extra_col" && v.Kind == ImportVerdictKind.MissingInTarget && v.SuggestedSql!.Contains("ADD [extra_col]"));
        Assert.Contains(existingPlan.Verdicts, v => v.TargetColumn == "required_code" && v.Kind == ImportVerdictKind.RequiredUnmapped);
    }

    [Fact]
    public async Task Prod_import_blocks_until_confirmed_and_keeps_inspection_alive()
    {
        await using var h = new TestHost();
        var file = Write("prod.csv", "id,name\n1,Alice\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "tok", FilePath = file });
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "prod", Engine = Engine.Pg, Host = "h", Environment = EnvironmentKind.Prod });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [new ObjectStamp(1, "public", "items", ObjectKind.Table, 1, null)], [],
            new MetadataBundle([new ColumnRow(1, 1, "id", "integer", null, null, null, false, false, false, null), new ColumnRow(1, 2, "name", "text", null, null, null, true, false, false, null)], [], [], []), "16");
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");

        var blocked = await h.Rpc.ImportRun(new ImportRunParams
        {
            SessionId = s.SessionId, InspectionId = ins.InspectionId, FileToken = "tok", FilePath = file, Schema = "public", Table = "items", ConfirmedProd = false,
        });
        Assert.Null(blocked.JobId);
        Assert.Equal(GuardReason.ProdWrite, blocked.Blocked!.Reason);

        // The inspection must still be present after the prod guard block so the user's confirmed retry can use it.
        var checkAgain = await h.Rpc.ImportCheckTarget(new ImportTargetParams
        {
            SessionId = s.SessionId, InspectionId = ins.InspectionId, Schema = "public", Table = "items", NewTable = false,
        });
        Assert.True(checkAgain.TableExists);
    }

    // An import that ends before its load starts (cancelled while queued behind the session, or no connection) must
    // still send its final import.progress: the wizard waits for it, and without it stays on "Cancel import" forever.
    [Theory]
    [InlineData(Engine.Mssql, "dbo", "int")]
    [InlineData(Engine.Pg, "public", "integer")]
    public async Task Import_cancelled_before_the_load_starts_still_reports_done(Engine engine, string schema, string type)
    {
        await using var h = new TestHost();
        var file = Write("queued.csv", "id\n1\n");
        var ins = await h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "tok", FilePath = file });
        var p = await h.Rpc.ProfileSave(new Profile { Id = "", Name = "dev", Engine = engine, Host = "h" });
        var s = await h.Rpc.SessionOpen(new SessionOpenParams { ProfileId = p.Id, Secret = "x", Database = "db" });
        h.Services.Metadata.Store.ApplyRefresh(p.Id, "db", true,
            [new ObjectStamp(1, schema, "items", ObjectKind.Table, 1, null)], [],
            new MetadataBundle([new ColumnRow(1, 1, "id", type, null, null, null, false, false, false, null)], [], [], []), "16");
        h.Services.Metadata.Cache.Invalidate(p.Id, "db");

        var session = h.Services.Sessions.Get(s.SessionId);
        await session.ExecLock.WaitAsync(); // another execution holds the session: the import queues
        try
        {
            var run = await h.Rpc.ImportRun(new ImportRunParams { SessionId = s.SessionId, InspectionId = ins.InspectionId, FileToken = "tok", FilePath = file, Schema = schema, Table = "items" });
            Assert.NotNull(run.JobId);
            Assert.True((await h.Rpc.ImportCancel(new ImportJobRef { JobId = run.JobId! })).Cancelled);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            ImportProgressEvent? done = null;
            while (done == null && DateTime.UtcNow < deadline)
            {
                done = h.Notifier.Of<ImportProgressEvent>().FirstOrDefault(e => e.JobId == run.JobId && e.Done);
                if (done == null) await Task.Delay(20);
            }
            Assert.NotNull(done);
            Assert.True(done!.Cancelled);
            Assert.Equal(0, done.RowsLoaded);
            Assert.Equal("The import was cancelled.", done.Error);
        }
        finally { session.ExecLock.Release(); }
    }

    // ------------------------------------------------------------------------------------------ review regressions

    [Fact]
    public void Whitespace_only_cells_count_as_nulls_unless_the_column_is_text()
    {
        var qty = new ColumnProfiler("qty", 0);
        qty.Add("1", 1);
        qty.Add("   ", 2);
        qty.Add("2", 3);
        var b = qty.Build();
        Assert.Equal(ImportKind.Int32, b.Kind);
        Assert.Equal(1L, b.NullCount); // so a NOT NULL target is flagged before the load
        Assert.Equal([2L], b.FirstNullRows);
        Assert.False(b.CandidateKey);

        var note = new ColumnProfiler("note", 1);
        note.Add("a", 1);
        note.Add("  ", 2);
        var nb = note.Build();
        Assert.Equal(ImportKind.Text, nb.Kind);
        Assert.Equal(0L, nb.NullCount); // text keeps its blanks
    }

    [Theory]
    [InlineData("mssql", "int", true)]
    [InlineData("mssql", "date", true)]
    [InlineData("mssql", "decimal(10,2)", true)]
    [InlineData("mssql", "uniqueidentifier", true)]
    [InlineData("mssql", "nvarchar(20)", false)]
    [InlineData("mssql", "char(3)", false)]
    [InlineData("mssql", "nvarchar(max)", false)]
    [InlineData("pg", "integer", true)]
    [InlineData("pg", "timestamp with time zone", true)]
    [InlineData("pg", "uuid", true)]
    [InlineData("pg", "numeric(10,2)", true)]
    [InlineData("pg", "text", false)]
    [InlineData("pg", "character varying(10)", false)]
    [InlineData("pg", "character(3)", false)]
    [InlineData("pg", null, false)]
    public void Blank_cells_load_as_null_into_non_character_columns(string engine, string? type, bool blankIsNull)
    {
        IImportDialect d = engine == "mssql" ? new SqlServerImport() : new PostgresImport();
        Assert.Equal(blankIsNull, d.BlankIsNull(type));
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("pg")]
    public void Loaded_rows_send_blank_cells_as_null_where_the_column_type_needs_it(string engine)
    {
        IImportDialect d = engine == "mssql" ? new SqlServerImport() : new PostgresImport();
        string[] types = engine == "mssql" ? ["int", "int", "nvarchar(10)"] : ["integer", "integer", "text"];
        var path = Write("blanks.csv", "id,qty,note\n1,   ,  \n");
        var ins = Inspect(path);
        using var src = RowSourceFactory.Open(path, ins.Plan);
        var rows = new MappedRows(src, [0, 1, 2], types.Select(d.BlankIsNull).ToArray(), 0, Path.Combine(_dir, "rej.csv"), ins.Plan.Columns, (_, _) => { });
        Assert.True(rows.Read());
        Assert.Equal(["1", null, "  "], rows.Current);
        if (engine == "pg") Assert.Null(PostgresImport.Validate(types[0], rows.Current[0]!)); // nothing left for the row check to refuse
        Assert.False(rows.Read());
    }

    [Fact]
    public void Sql_server_inserts_in_an_open_transaction_turn_identity_insert_on_only_for_kept_identity_values()
    {
        var withId = new BulkTarget("dbo", "t]x", [new BulkColumn("id", "int", Identity: true), new BulkColumn("name", "nvarchar(20)")]);
        var noId = new BulkTarget("dbo", "t", [new BulkColumn("name", "nvarchar(20)")]);
        Assert.Equal(("SET IDENTITY_INSERT [dbo].[t]]x] ON;", "SET IDENTITY_INSERT [dbo].[t]]x] OFF;"),
            SqlServerBulkLoader.IdentityInsert(withId, new ImportRunOptions { KeepIdentity = true }));
        Assert.Null(SqlServerBulkLoader.IdentityInsert(withId, new ImportRunOptions { KeepIdentity = false }));
        Assert.Null(SqlServerBulkLoader.IdentityInsert(noId, new ImportRunOptions { KeepIdentity = true }));
    }
}
