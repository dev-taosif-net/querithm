using System.Diagnostics;
using Querithm.Contract;
using Xunit.Abstractions;
using static Querithm.Tests.Integration.OracleScriptIntegrationTests;

namespace Querithm.Tests.Integration;

/// <summary>
/// Timing probe for §14 metric 22.oracle (QUERITHM_IT=1 and QUERITHM_PERF=1): the bench's 100k × 10 data compare against a
/// copy with 1,000 changed rows, run in-process so the wall time splits into this process's CPU time (the sidecar's work:
/// fetch, decode, merge) and the rest (server and network). The tables are left in place for a V$SQL read afterwards.
/// </summary>
public class OracleComparePerfProbe(ITestOutputHelper output)
{
    [IntegrationFact("QUERITHM_PERF")]
    public async Task Data_compare_100k_split_into_sidecar_cpu_and_the_rest()
    {
        await using (var c = await OpenAsync())
        {
            foreach (var t in new[] { "q_bench_dc_a", "q_bench_dc_b" }) await DropAsync(c, "TABLE", t);
            await RunScriptAsync(c, """
                CREATE TABLE q_bench_dc_a (id NUMBER(10) PRIMARY KEY, name VARCHAR2(40), qty NUMBER(10), price NUMBER(10,2), ok NUMBER(1), born DATE, at TIMESTAMP(6), code VARCHAR2(12), note VARCHAR2(60), ratio BINARY_DOUBLE);
                INSERT /*+ APPEND */ INTO q_bench_dc_a SELECT level, 'name ' || level, MOD(level, 100), MOD(level, 1000) + 0.25, MOD(level, 2), DATE '2026-01-01' + MOD(level, 365),
                  TIMESTAMP '2026-01-01 00:00:00' + NUMTODSINTERVAL(level, 'MINUTE'), 'C' || level, 'note ' || level, MOD(level, 10) / 10 FROM dual CONNECT BY level <= 100000;
                COMMIT;
                CREATE TABLE q_bench_dc_b AS SELECT * FROM q_bench_dc_a;
                ALTER TABLE q_bench_dc_b ADD PRIMARY KEY (id);
                UPDATE q_bench_dc_b SET name = name || '!' WHERE MOD(id, 100) = 0;
                COMMIT;
                """);
        }
        await using var h = await OracleS11Host.OpenAsync();
        DataCompareSide Side(string t) => new() { SessionId = h.Sid, Database = OracleS11Host.Db, Schema = "QUERITHM_APP", Table = t };
        var me = Process.GetCurrentProcess();
        for (var i = 0; i < 6; i++)
        {
            var cpu0 = me.TotalProcessorTime;
            var gc0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            var st = await h.Rpc.DataCompareStart(new DataCompareParams { Source = Side("Q_BENCH_DC_A"), Target = Side("Q_BENCH_DC_B") });
            DataCompareProgressEvent? done = null;
            while (done == null)
            {
                done = h.Notifier.Of<DataCompareProgressEvent>().FirstOrDefault(e => e.CompareId == st.CompareId && e.Done);
                if (done == null) await Task.Delay(5);
            }
            var wall = sw.Elapsed.TotalMilliseconds;
            me.Refresh();
            var cpu = (me.TotalProcessorTime - cpu0).TotalMilliseconds;
            Assert.Null(done.Error);
            Assert.Equal(1000, done.Different);
            output.WriteLine($"run {i}{(i == 0 ? " (warm-up)" : "")}: wall {wall:F0} ms, process CPU {cpu:F0} ms ({cpu / wall:P0} of wall), gen0 GCs {GC.CollectionCount(0) - gc0}");
            await h.Rpc.QueryClose(new ExecutionRef { ExecutionId = st.ExecutionId }).ConfigureAwait(false);
        }
    }
}
