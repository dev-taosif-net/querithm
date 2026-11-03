using Querithm.Core;
using Querithm.Contract;
using Querithm.Core.Execution;

namespace Querithm.Tests.Integration;

public class ResultFailureIntegrationTests
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Errors_preserve_completed_sets_and_result_indices(bool pg)
    {
        await using var h = new TestHost();
        var (_, sid) = pg ? await h.OpenPgAsync() : await h.OpenMssqlAsync();
        // A completed empty result must survive; SQL Server continues at the next GO.
        var sql = pg
            ? "SELECT 1 AS before_error WHERE false; SELECT CAST('bad' AS int); SELECT 3 AS after_error;"
            : "SELECT 1 AS before_error WHERE 1=0; SELECT CONVERT(int, v) FROM (VALUES ('bad')) t(v);\nGO\nSELECT 3 AS after_error;";
        var (done, _, id) = await h.RunAsync(sid, sql);
        Assert.Equal(ExecState.Failed, done.State);
        var frames = h.Frames.For(id);
        var schemas = frames.Where(f => f.Kind == FrameKind.Schema).Select(f => f.ResultIndex).ToArray();
        Assert.Equal(schemas.Length, schemas.Distinct().Count());
        Assert.Equal(schemas.Length, done.ResultSetCount);
        Assert.Contains(frames, f => f.Kind == FrameKind.EndOfResultSet && f.ResultIndex == 0);
        Assert.NotNull(h.Services.Executions.Recorded(sid, id, 0));
        foreach (var ri in schemas.Where(ri => !frames.Any(f => f.Kind == FrameKind.EndOfResultSet && f.ResultIndex == ri)))
            Assert.Null(h.Services.Executions.Recorded(sid, id, ri));
        var (again, _, _) = await h.RunAsync(sid, "SELECT 1");
        Assert.Equal(ExecState.Done, again.State);
    }
}
