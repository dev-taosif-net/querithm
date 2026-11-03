using System.IO.Compression;
using System.Text;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Import;

namespace Querithm.Tests;

/// <summary>
/// L8: crafted import files are refused at inspection with the limit they cross, instead of filling memory: before, an
/// unterminated quote buffered the rest of the file into one field, NDJSON with a new key per line made one profiler per
/// key and replayed every earlier row for it, and an .xlsx part was decompressed whatever its size. Files are generated
/// here in a temp directory.
/// </summary>
public sealed class ImportLimitsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("qimport-limits-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    static Inspection Inspect(string path) => FileInspector.Inspect("i", path, null, (_, _, _) => { }, CancellationToken.None);

    static string Refusal(string path) => Assert.Throws<ImportLimitException>(() => Inspect(path)).Message;

    /// <summary>
    /// The value and record limits scaled down for this test's flow (1 Mi / 4 Mi characters): the shipped 64 Mi / 128 Mi
    /// would need hundreds of MB per test. The checks are the same code; ImportLimitsSizingTests covers the shipped values.
    /// </summary>
    static IDisposable Scaled() => ImportLimits.Scale(1 << 20, 4 << 20);

    const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>A one-sheet workbook; <paramref name="sheet"/> writes the sheet part (its rows go inside sheetData).</summary>
    string Xlsx(string name, Action<Stream> sheet, int extraEntries = 0)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string entry, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(xml);
        }
        Part("xl/workbook.xml", $"""<workbook xmlns="{Ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="S" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>""");
        using (var s = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open()) sheet(s);
        for (var i = 0; i < extraEntries; i++) zip.CreateEntry($"customXml/item{i}.xml");
        return path;
    }

    static void Rows(Stream s, string rows)
    {
        using var w = new StreamWriter(s);
        w.Write($"""<worksheet xmlns="{Ns}"><sheetData>{rows}</sheetData></worksheet>""");
    }

    [Fact]
    public void An_xlsx_part_that_expands_like_a_zip_bomb_is_refused_before_it_is_read()
    {
        // 32 MB of whitespace inside the sheet compresses to a few KB: a ratio no real sheet has.
        var path = Xlsx("bomb.xlsx", s =>
        {
            using var w = new StreamWriter(s);
            w.Write($"""<worksheet xmlns="{Ns}"><sheetData><row r="1"><c r="A1"><v>1</v></c></row>""");
            var blanks = new string(' ', 1 << 20);
            for (var i = 0; i < 32; i++) w.Write(blanks);
            w.Write("</sheetData></worksheet>");
        });
        Assert.InRange(new FileInfo(path).Length, 1, 1 << 20);
        var message = Refusal(path);
        Assert.Contains("decompression bomb", message);
        Assert.Contains("xl/worksheets/sheet1.xml", message);
    }

    [Fact]
    public void An_archive_with_more_entries_than_a_workbook_has_is_refused()
    {
        var path = Xlsx("entries.xlsx", s => Rows(s, """<row r="1"><c r="A1"><v>1</v></c></row>"""), extraEntries: ImportLimits.MaxZipEntries);
        Assert.Contains($"at most {ImportLimits.MaxZipEntries:N0}", Refusal(path));
    }

    [Fact]
    public void An_xlsx_cell_longer_than_the_value_limit_is_refused_and_one_at_the_limit_is_read()
    {
        using var _ = Scaled();
        var path = Xlsx("long.xlsx", s => Rows(s, $"""<row r="1"><c r="A1" t="inlineStr"><is><t>{new string('x', ImportLimits.FieldChars + 1)}</t></is></c></row>"""));
        Assert.Contains($"more than {ImportLimits.FieldChars:N0} characters", Refusal(path));
        var ok = Xlsx("limit.xlsx", s => Rows(s, $"""<row r="1"><c r="A1"><v>id</v></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>{new string('y', ImportLimits.FieldChars)}</t></is></c></row>"""));
        Assert.Equal(ImportLimits.FieldChars, Inspect(ok).Columns[0].MaxLength);
    }

    [Fact]
    public void An_xlsx_value_past_the_column_limit_is_refused_but_empty_formatted_cells_out_to_xfd_are_not()
    {
        var wide = Xlsx("wide.xlsx", s => Rows(s, """<row r="1"><c r="A1"><v>1</v></c><c r="FFF1"><v>2</v></c></row>"""));
        Assert.Contains($"at most {ImportLimits.MaxColumns:N0} columns", Refusal(wide));
        var styled = Xlsx("styled.xlsx", s => Rows(s, """<row r="1"><c r="A1" t="inlineStr"><is><t>id</t></is></c><c r="XFD1" s="1"/></row><row r="2"><c r="A2"><v>7</v></c><c r="XFD2" s="1"/></row>"""));
        var ins = Inspect(styled);
        Assert.Equal(["id"], ins.Plan.Columns);
        Assert.Equal(1, ins.RowCount);
    }

    [Fact]
    public void A_csv_quote_that_never_closes_is_refused_at_the_field_limit_naming_the_line_it_opened_on()
    {
        using var _ = Scaled();
        var path = Write("unterminated.csv", "id,name\n1,ok\n2,\"" + new string('x', ImportLimits.FieldChars + 10) + "\n3,more\n");
        var message = Refusal(path);
        Assert.Contains("Line 3", message);
        Assert.Contains("closing quote missing", message);
    }

    [Fact]
    public void A_csv_record_with_more_fields_than_the_column_limit_is_refused()
    {
        var path = Write("fields.csv", "a,b\n" + string.Join(",", Enumerable.Range(0, ImportLimits.MaxColumns + 1)) + "\n");
        Assert.Contains($"more than {ImportLimits.MaxColumns:N0} fields", Refusal(path));
    }

    [Fact]
    public void Ndjson_with_5000_distinct_keys_is_refused_at_the_column_limit()
    {
        var path = Write("keys.ndjson", string.Concat(Enumerable.Range(0, 5_000).Select(i => $"{{\"k{i}\": {i}}}\n")));
        var message = Refusal(path);
        Assert.Contains($"Row {ImportLimits.MaxColumns + 1:N0}", message);
        Assert.Contains($"at most {ImportLimits.MaxColumns:N0} columns", message);
    }

    [Fact]
    public void Ndjson_keys_first_seen_late_count_the_earlier_rows_as_nulls_without_replaying_them()
    {
        var path = Write("late.ndjson", string.Concat(Enumerable.Range(0, 3_000).Select(i => $"{{\"k{i}\": {i}}}\r\n")));
        var ins = Inspect(path);
        Assert.Equal(3_000, ins.Columns.Length);
        Assert.Equal(3_000, ins.RowCount);
        var last = ins.Columns[^1];
        Assert.Equal(2_999L, last.NullCount);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, last.FirstNullRows);
        Assert.Equal(2_999L, ins.Columns[0].NullCount);
        Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, ins.Columns[0].FirstNullRows);
    }

    [Fact]
    public void An_ndjson_line_without_an_end_is_refused_at_the_record_limit_and_cr_only_lines_still_read()
    {
        using var _ = Scaled();
        var path = Path.Combine(_dir, "line.ndjson");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            w.Write("{\"a\": \"");
            var chunk = new string('z', 1 << 20);
            for (var i = 0; i <= ImportLimits.RecordChars >> 20; i++) w.Write(chunk);
        }
        Assert.Contains("Line 1 of the NDJSON file is longer than", Refusal(path));
        var cr = Inspect(Write("cr.ndjson", "{\"a\": 1}\r{\"a\": 2}\r\n{\"a\": 3}"));
        Assert.Equal(3, cr.RowCount);
    }

    [Fact]
    public void A_json_value_longer_than_the_value_limit_is_refused()
    {
        using var _ = Scaled();
        var path = Write("value.json", "[{\"a\": \"" + new string('q', ImportLimits.FieldChars + 1) + "\"}]");
        Assert.Contains("the value of a is longer than", Refusal(path));
    }

    [Fact]
    public async Task The_refusal_reaches_the_wizard_as_an_import_file_error_with_the_reason()
    {
        await using var h = new TestHost();
        var path = Write("rpc.ndjson", string.Concat(Enumerable.Range(0, 5_000).Select(i => $"{{\"k{i}\": {i}}}\n")));
        var e = await Assert.ThrowsAsync<QuerithmException>(() => h.Rpc.ImportInspect(new ImportInspectParams { FileToken = "t", FilePath = path }));
        Assert.Equal("importFile", e.Code);
        Assert.Contains($"at most {ImportLimits.MaxColumns:N0} columns", e.Message);
    }

    // ------------------------------------------------------------------ 8a review: the shipped limits

    [Fact]
    public void The_shipped_limits_accept_values_past_the_grids_text_limit_and_size_the_record_for_one_at_the_limit()
    {
        Assert.True(ImportLimits.MaxFieldChars > Core.Execution.ResultSetEncoder.TextLimitChars);
        Assert.True(ImportLimits.MaxRecordChars >= 2 * ImportLimits.MaxFieldChars);
        Assert.Equal(ImportLimits.MaxFieldChars, ImportLimits.FieldChars); // no test scale outside a Scaled() flow
        Assert.InRange(ImportLimits.MaxSharedStringsBytes, 128L << 20, 512L << 20);
    }

    [Fact]
    public void A_csv_value_longer_than_the_grid_shows_is_imported_whole_and_previewed_as_the_grid_shows_it()
    {
        // A 1 MB document as base64 is 1.4 M characters: refused before the 8a review (the limit was the grid's 1 Mi).
        var big = new string('b', Core.Execution.ResultSetEncoder.TextLimitChars * 2);
        var ins = Inspect(Write("doc.csv", $"id,doc\n1,{big}\n2,small\n"));
        Assert.Equal(2, ins.RowCount);
        Assert.Equal(big.Length, ins.Columns[1].MaxLength);
        Assert.Equal(Core.Execution.ResultSetEncoder.TextLimitChars, ins.Preview[0][1]!.Length);
        Assert.Equal("small", ins.Preview[1][1]);
    }
}
