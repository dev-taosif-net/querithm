using Querithm.Contract.Providers;
using Querithm.Contract;
using Querithm.Intelligence.TSql;

namespace Querithm.Tests;

/// <summary>Bench metric 6 (§14): diagnostics on a 12k-line procedure, via statement chunks parsed in parallel.</summary>
public class LargeDocumentTests
{
    public static string BigProc(int lines = 12_000)
    {
        var sb = new System.Text.StringBuilder("CREATE OR ALTER PROCEDURE dbo.usp_BenchBig @CustomerId int, @StartDate datetime2 = NULL AS\nBEGIN\n    SET NOCOUNT ON;\n    DECLARE @Counter int = 0, @Total decimal(18,2) = 0;\n");
        var n = 4;
        for (var c = 1; n < lines - 1; c++)
        {
            sb.Append($"    ;WITH c{c} AS (\n        SELECT a.id, a.c1, a.amount FROM dbo.t{c % 600 + 1} a\n        WHERE a.c1 > {c} AND a.created_at >= @StartDate\n    )\n");
            sb.Append($"    SELECT x.id, x.c1, x.amount INTO #tmp_{c} FROM c{c} x WHERE x.amount > 0;\n");
            n += 5;
            if (c % 7 == 0)
            {
                sb.Append($"    MERGE dbo.t{c + 1} AS t USING #tmp_{c} AS s ON t.id = s.id\n    WHEN MATCHED THEN UPDATE SET t.c1 = s.c1\n    WHEN NOT MATCHED THEN INSERT (id, c1) VALUES (s.id, s.c1);\n");
                n += 3;
            }
            sb.Append($"    IF @Counter > {c}\n        UPDATE dbo.t{c % 600 + 1} SET c2 = c2 + 1 WHERE id = @Counter;\n    ELSE\n        SELECT @Total = @Total + ISNULL(SUM(amount), 0) FROM dbo.t1 WHERE c1 = {c};\n");
            sb.Append($"    BEGIN TRAN;\n    SET @Counter = CASE WHEN @Counter > 5 THEN 0 ELSE @Counter + 1 END;\n    COMMIT;\n");
            n += 7;
        }
        sb.Append("END;");
        return sb.ToString();
    }

    /// <summary>The bench's 10,000-line PL/pgSQL function (scripts/bench/gen-schema.mjs bigPlpgsql), metric 27.</summary>
    public static string BigPlpgsql()
    {
        var sb = new System.Text.StringBuilder("""
            CREATE OR REPLACE FUNCTION public.fn_bench_plpgsql(p_customer int, p_since timestamptz DEFAULT NULL)
            RETURNS TABLE (out_id int, out_total numeric, out_label text) LANGUAGE plpgsql AS $body$
            DECLARE
              v_counter int := 0;
              v_total numeric(18,2) := 0;
              v_label text;
              rec record;
            BEGIN
              CREATE TEMP TABLE IF NOT EXISTS tmp_bench_rows (id int, amount numeric(18,2), label text) ON COMMIT DROP;

            """);
        for (var k = 0; sb.ToString().Count(c => c == '\n') < 9994; k++)
            sb.Append($"""
                  FOR rec IN SELECT a.id, a.amount, a.c3 FROM public.t{k % 600 + 1} a WHERE a.c1 > {k} AND a.created_at >= COALESCE(p_since, now() - interval '1 day') LOOP
                    v_counter := v_counter + 1;
                    v_total := v_total + COALESCE(rec.amount, 0);
                    INSERT INTO tmp_bench_rows (id, amount, label) VALUES (rec.id, rec.amount, rec.c3);
                  END LOOP;
                  WITH s{k} AS (SELECT r.id, r.amount FROM tmp_bench_rows r WHERE r.amount > {k % 100})
                  SELECT count(*), max(x.label) INTO v_counter, v_label FROM s{k} JOIN tmp_bench_rows x ON x.id = s{k}.id;
                  -- step {k}

                """);
        sb.Append("  RETURN QUERY SELECT t.id, t.amount, t.label FROM tmp_bench_rows t;\nEND\n$body$;");
        return sb.ToString();
    }

    static readonly Querithm.Contract.Providers.ISchemaSource BenchPg = new FakeSchema(new Querithm.Contract.SchemaSnapshot
    {
        ProfileId = "p1", Database = "postgres", Fks = [],
        Objects = Enumerable.Range(1, 600).Select(i => new SnapObject
        {
            Id = i, Schema = "public", Name = $"t{i}", Kind = ObjectKind.Table,
            Columns = new[] { "id", "c1", "c2", "c3", "c4", "amount", "created_at" }.Select(n => new SnapColumn { Name = n, Type = n == "amount" ? "numeric(18,2)" : "integer" }).ToArray(),
        }).ToArray(),
    }, "postgres");

    [Fact]
    public void Pg_tier2_on_a_10k_line_plpgsql_function_resolves_records_temp_tables_and_variables_within_budget()
    {
        var text = BigPlpgsql();
        Assert.InRange(text.Count(c => c == '\n') + 1, 10_000, 10_010);
        var pg = new Querithm.Intelligence.PgSql.PgLanguageService();
        CompletionItem[] At(string marker, int nth, string? edited = null)
        {
            var doc = edited ?? text;
            var at = -1;
            for (var i = 0; i <= nth; i++) at = doc.IndexOf(marker, at + 1, StringComparison.Ordinal);
            return pg.Complete(new CompleteParams { DocId = "big-pg", Text = doc, Offset = at + marker.Length, Dialect = Dialect.Pg, ProfileId = "p1", Database = "postgres" }, BenchPg);
        }
        // Record fields from the nearest FOR … IN query, the temp table, CTE and variables deep in the body.
        Assert.Contains(At("COALESCE(rec.", 1100), i => i.Label == "c3");
        Assert.Contains(At("max(x.", 1000), i => i.Label == "label");
        Assert.Contains(At("SELECT r.", 900), i => i.Label == "amount");
        Assert.Contains(At("v_total := ", 1200), i => i.Label == "v_counter");
        Assert.Contains(At("v_total := ", 1200), i => i.Label == "p_since");

        var warm = new List<double>();
        var markers = new[] { "COALESCE(rec.", "max(x.", "v_total := " };
        for (var i = 0; i < 40; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            At(markers[i % 3], 50 + i * 27);
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }
        // After an edit the document is tokenized again (the typing case).
        var cold = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var edited = text.Insert(text.Length / 2, $"  v_counter := {i};\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Contains(At("COALESCE(rec.", 600, edited), x => x.Label == "amount");
            cold.Add(sw.Elapsed.TotalMilliseconds);
        }
        var p95 = warm.Order().ElementAt((int)Math.Ceiling(warm.Count * 0.95) - 1);
        // The 150 ms gate (metric 27) is measured by pnpm bench through lang.complete; this guards an order-of-magnitude
        // regression without failing on a slow CI runner.
        Assert.True(p95 < TestTime.Budget(150 * 3), $"warm p95 {p95:F1} ms; edited {string.Join(", ", cold.Select(c => c.ToString("F1")))} ms");
        Assert.True(cold.Max() < TestTime.Budget(150 * 5), $"edited max {cold.Max():F1} ms");
    }

    [Fact]
    public void Chunked_diagnostics_match_a_full_parse_on_valid_text()
    {
        var text = BigProc();
        Assert.True(text.Length > ChunkedDiagnostics.MinLength);
        Assert.True(ChunkedDiagnostics.Split(text).Count >= 2);
        Assert.Empty(TSqlParsing.Parse(text).Errors);
        Assert.Empty(ChunkedDiagnostics.TryDiagnose(text)!);
    }

    [Fact]
    public void An_error_in_the_middle_is_reported_on_its_line()
    {
        var lines = BigProc().Split('\n');
        var target = lines.Length / 2;
        while (!lines[target].TrimStart().StartsWith("SELECT x.id")) target++;
        lines[target] = lines[target].Replace("FROM c", "FROM FROM c");
        var text = string.Join('\n', lines);
        var full = TSqlParsing.Parse(text).Errors.Select(e => e.Line).ToArray();
        var chunked = ChunkedDiagnostics.TryDiagnose(text)!;
        Assert.Equal(target + 1, Assert.Single(chunked).Line);
        Assert.Contains(target + 1, full);
    }

    [Fact]
    public void After_one_edit_only_one_chunk_is_parsed_again()
    {
        var text = BigProc();
        Assert.Empty(ChunkedDiagnostics.TryDiagnose(text)!); // warm the cache
        var at = text.IndexOf('\n', text.Length / 3) + 1;
        for (var i = 1; i <= 3; i++)
        {
            // Unique edit per iteration so the edited chunk is never already cached.
            var edited = text.Insert(at, $"    SET @Counter = @Counter + {i};\n");
            Assert.Empty(ChunkedDiagnostics.TryDiagnose(edited, out var parsed)!);
            // Checks the work done, not wall time (CI runners vary too much); the 100 ms gate is measured by pnpm bench.
            Assert.Equal(1, parsed);
        }
    }

    // Regression: cut points were offsets from the chunk start, so an insert shifted every later chunk and on 2-core
    // machines one edit re-parsed 3 of 4 chunks. Checked for each core count, since Split sizes chunks by cores.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void An_edit_changes_only_the_chunk_it_is_in(int cores)
    {
        var text = BigProc();
        var before = ChunkedDiagnostics.Split(text, cores).Select(c => ChunkedDiagnostics.Wrap(text, c)).ToHashSet();
        Assert.True(before.Count >= 2);
        foreach (var fraction in new[] { 0.1, 0.33, 0.5, 0.75, 0.95 })
        {
            var at = text.IndexOf('\n', (int)(text.Length * fraction)) + 1;
            var edited = text.Insert(at, "    SET @Counter = @Counter + 42;\n");
            var changed = ChunkedDiagnostics.Split(edited, cores).Count(c => !before.Contains(ChunkedDiagnostics.Wrap(edited, c)));
            Assert.Equal(1, changed);
        }
    }
}
