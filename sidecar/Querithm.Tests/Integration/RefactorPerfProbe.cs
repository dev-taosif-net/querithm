using System.Diagnostics;
using Querithm.Contract;
using Xunit.Abstractions;

namespace Querithm.Tests.Integration;

/// <summary>Timing probe for §14 metric 24 (QUERITHM_IT=1 and QUERITHM_PERF=1): find usages over the fixture's ~550 modules.</summary>
[Collection("refactor")]
public class RefactorPerfProbe(ITestOutputHelper output)
{
    [IntegrationFact("QUERITHM_PERF")]
    public async Task Find_usages_timings_on_the_fixture()
    {
        await using var h = new TestHost();
        var (p, sid) = await h.OpenMssqlAsync("SalesDB");
        h.Services.Metadata.Refresh(new RefreshParams { ProfileId = p.Id, Database = "SalesDB", Full = true });
        await Task.Delay(100);
        await h.Services.Metadata.WaitAsync(p.Id, "SalesDB");
        var tab = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"SELECT o.*, {i} AS n FROM dbo.t{i % 8 + 1} o WHERE o.id > {i};"));
        var tabs = Enumerable.Range(0, 5).Select(i => new OpenTabText { TabId = $"t{i}", Title = $"T{i}", Text = tab }).ToArray();
        FindUsagesParams Args(int n) => new() { SessionId = sid, Database = "SalesDB", Target = new RefactorTarget { Kind = RefactorKind.Column, Schema = "dbo", Name = $"t{n}", Column = "id" }, Tabs = tabs };
        await h.Rpc.RefactorFindUsages(Args(1)); // fills definition_cache
        var fresh = new List<double>();
        for (var n = 2; n <= 8; n++)
        {
            var sw = Stopwatch.StartNew();
            await h.Rpc.RefactorFindUsages(Args(n));
            fresh.Add(sw.Elapsed.TotalMilliseconds);
        }
        fresh.Sort();
        output.WriteLine($"fresh targets: median {fresh[3]:F1} ms ({string.Join(", ", fresh.Select(x => Math.Round(x)))})");
    }
}
