using System.Diagnostics;
using System.Text;
using Querithm.Contract;
using Querithm.Contract.Providers;
using Querithm.Intelligence.DuckDb;
using Querithm.Intelligence.MySql;
using Querithm.Intelligence.Oracle;
using Querithm.Intelligence.PgSql;
using Querithm.Intelligence.Sqlite;

namespace Querithm.Tests;

/// <summary>
/// The superseded-request figure of the completion audit (median 0.23 ms) was measured on a fake engine that waits on the
/// token. This measures the real PostgreSQL-family engines: a request on a ~1 MB document (a cold document cache, the
/// cursor in its last statement) whose token is cancelled a few milliseconds in must end within 50 ms of the cancel
/// (§14 #5d). Before 2026-11-25 the engines checked the token only on entry and in nested column resolution, so such a
/// request ran to its end. Wall-clock: in the timing collection.
/// </summary>
[Collection("timing")]
public class Tier2CancellationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static SnapColumn C(string name) => new() { Name = name, Type = "integer" };

    static SchemaSnapshot Shop(string engine)
    {
        string N(string s) => engine == "oracle" ? s.ToUpperInvariant() : s;
        var schema = engine switch { "pg" => "public", "mysql" => "shop", "oracle" => "APP", _ => "main" };
        return new SchemaSnapshot
        {
            ProfileId = "p", Database = "shop", Status = SnapshotStatus.Fresh, Fks = [],
            Objects =
            [
                new SnapObject { Id = 1, Schema = schema, Name = N("orders"), Kind = ObjectKind.Table, Columns = [C(N("id")), C(N("customer_id")), C(N("status")), C(N("total"))] },
                new SnapObject { Id = 2, Schema = schema, Name = N("customers"), Kind = ObjectKind.Table, Columns = [C(N("customer_id")), C(N("full_name"))] },
            ],
        };
    }

    static IDialectService Service(string engine) => engine switch
    {
        "pg" => new PgLanguageService(), "mysql" => new MySqlLanguageService(), "oracle" => new OracleLanguageService(),
        "sqlite" => new SqliteLanguageService(), _ => new DuckDbLanguageService(),
    };

    static Dialect DialectOf(string engine) => engine switch
    {
        "pg" => Dialect.Pg, "mysql" => Dialect.MySql, "oracle" => Dialect.Oracle, "sqlite" => Dialect.Sqlite, _ => Dialect.DuckDb,
    };

    /// <summary>About <paramref name="chars"/> characters of ordinary statements, then <c>SELECT o.| FROM orders o</c>.</summary>
    static (string Text, int Offset) Document(int chars)
    {
        var sb = new StringBuilder(chars + 64);
        for (var i = 0; sb.Length < chars; i++)
            sb.Append("SELECT o.id, o.status, c.full_name FROM orders o JOIN customers c ON c.customer_id = o.customer_id WHERE o.total > ")
              .Append(i).Append(" AND o.status = 'open';\n");
        sb.Append("SELECT o.");
        var offset = sb.Length;
        sb.Append(" FROM orders o");
        return (sb.ToString(), offset);
    }

    CompleteParams Request(string engine, string text, int offset) => new()
    {
        DocId = "cancel-" + Guid.NewGuid(), Text = text, Offset = offset, Dialect = DialectOf(engine), ProfileId = "p", Database = "shop",
        Schema = engine == "oracle" ? "APP" : null,
    };

    /// <summary>
    /// A temp table the depth limit cuts short is kept per (table, depth), not dropped: recomputing it on every read
    /// would make 12 levels of 20 named columns cost about 20^9 projections (the audit's H1, through temp tables).
    /// </summary>
    [Theory]
    [InlineData("pg")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    [InlineData("oracle")] // code audit 2026-11-25 (L9): Oracle's global temporary tables take the same path
    public void A_wide_temp_table_chain_past_the_depth_limit_stays_cheap(string engine)
    {
        var cols = string.Join(", ", Enumerable.Range(0, 20).Select(i => $"a{i}"));
        var create = engine switch { "mysql" => "CREATE TEMPORARY TABLE", "oracle" => "CREATE GLOBAL TEMPORARY TABLE", _ => "CREATE TEMP TABLE" };
        var sb = new StringBuilder();
        sb.Append($"{create} t1 AS SELECT {string.Join(", ", Enumerable.Range(0, 20).Select(i => $"{i} AS a{i}"))}{(engine == "oracle" ? " FROM dual" : "")};\n");
        for (var k = 2; k <= 12; k++) sb.Append($"{create} t{k} AS SELECT {cols} FROM t{k - 1};\n");
        sb.Append("SELECT ");
        var offset = sb.Length;
        sb.Append(" FROM t12 x, t3 y");
        var svc = Service(engine);
        var schema = new FakeSchema(Shop(engine), "shop");
        svc.Complete(Request(engine, sb.ToString(), offset), schema, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        var items = svc.Complete(Request(engine, sb.ToString(), offset), schema, CancellationToken.None);
        sw.Stop();
        output.WriteLine($"{engine}: 12 temp tables x 20 columns, {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Contains(items, i => i.Label.Equals("y.a0", StringComparison.OrdinalIgnoreCase));
        Assert.True(sw.Elapsed.TotalMilliseconds < 250, $"{engine}: {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Mapping_another_dialects_tokens_stops_in_a_cancelled_request_and_never_outside_one()
    {
        // Review 2026-11-25: MySQL, Oracle and SQLite mapped every token with an unchecked Select after the checked
        // lexing; cancelled at 85 % on a 1 MB document, 3 of 5 MySQL runs finished 19.6–30.8 ms after the cancel (quiet
        // machine) and 92–208 ms in the integration run.
        var items = Enumerable.Range(0, 5_000).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (Tier2Cancellation.Use(cts.Token))
            Assert.Throws<OperationCanceledException>(() => Tier2Cancellation.Map(items, i => i + 1));
        // Outside a request (the guard's classification shares these tokenizers) it never stops.
        Assert.Equal(5_000, Tier2Cancellation.Map(items, i => i + 1)[^1]);
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public void A_cancelled_request_on_a_1_mb_document_ends_within_50_ms(string engine)
    {
        var svc = Service(engine);
        var schema = new FakeSchema(Shop(engine), "shop");
        var (text, offset) = Document(1_000_000);
        // Warm the JIT, then the uncancelled cost on a cold document (a new docId), median of three.
        svc.Complete(Request(engine, text, offset), schema, CancellationToken.None);
        // Garbage collected before each measured run: after the rest of the suite the test host's heap is large, and a
        // collection inside the window measured the host, not the engine (in the full suite MySQL's uncancelled run read
        // 691 ms against 408–551 ms alone, and its stop median 72 ms against under 10 ms alone).
        static void Settle() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        var full = Enumerable.Range(0, 3).Select(_ =>
        {
            Settle();
            var sw = Stopwatch.StartNew();
            svc.Complete(Request(engine, text, offset), schema, CancellationToken.None);
            return sw.Elapsed.TotalMilliseconds;
        }).Order().ElementAt(1);
        Assert.True(full > 50, $"{engine}: the document is too small to show a stop ({full:F1} ms uncancelled)");

        // Cancelled a few milliseconds in (tokenizing), at 40 % and at 70 % of the uncancelled time (splitting, the
        // statement at the cursor, scopes and items): every phase must stop. A request that already ended is not counted.
        foreach (var at in new[] { 3, full * 0.4, full * 0.7 })
        {
            var stops = new List<double>();
            var finished = new List<double>();
            for (var trial = 0; trial < 5; trial++)
            {
                using var cts = new CancellationTokenSource();
                var request = Request(engine, text, offset);
                Settle();
                var gen2Before = GC.CollectionCount(2);
                long cancelledAt = 0, endedAt = 0;
                var threw = false;
                var worker = new Thread(() =>
                {
                    try { svc.Complete(request, schema, cts.Token); }
                    catch (OperationCanceledException) { threw = true; }
                    endedAt = Stopwatch.GetTimestamp();
                });
                var start = Stopwatch.GetTimestamp();
                worker.Start();
                // A spin, not Thread.Sleep: its granularity on Windows is 15.6 ms.
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < at) Thread.SpinWait(50);
                cancelledAt = Stopwatch.GetTimestamp();
                cts.Cancel();
                Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the request never ended");
                if (endedAt < cancelledAt) continue; // finished before the cancel (a fast run): nothing to measure
                var after = Stopwatch.GetElapsedTime(cancelledAt, endedAt).TotalMilliseconds;
                output.WriteLine($"  {engine} trial at {at:F0} ms: ended {after:F1} ms after the cancel, {(threw ? "cancelled" : "finished")}, gen2 collections {GC.CollectionCount(2) - gen2Before}");
                stops.Add(after);
                // A cancel that lands in the last steps (after the final check) lets the answer finish: it still has to
                // end within the bound, which is what a superseded request needs.
                if (!threw) finished.Add(after);
            }
            Assert.True(stops.Count >= 3, $"{engine}: cancelled at {at:F0} ms, only {stops.Count} of 5 runs were still going");
            stops.Sort();
            var median = stops[stops.Count / 2];
            output.WriteLine($"{engine}: 1 MB document, uncancelled {full:F1} ms; cancelled at {at:F0} ms, stopped after median {median:F2} ms, max {stops[^1]:F2} ms"
                + (finished.Count > 0 ? $"; {finished.Count} finished instead, {string.Join(" / ", finished.Select(f => f.ToString("F1")))} ms after the cancel" : ""));
            Assert.True(median <= 50, $"{engine}: cancelled at {at:F0} ms, stopped {median:F1} ms after the cancel (median; uncancelled {full:F1} ms)");
        }
    }
}
