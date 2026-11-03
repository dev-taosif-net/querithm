using DuckDB.NET.Data;
using Querithm.Contract;
using Querithm.Providers.DuckDb;

namespace Querithm.Tests.FileEngines;

/// <summary>
/// L20: DuckDB opens wait only for opens of the same file. A file's open (driver open, configuration, audit) is held in
/// the <see cref="DuckDbInstances.BeforeOpen"/> seam to stand in for a slow one; the seam is an AsyncLocal, so other tests'
/// opens never see it.
/// </summary>
public sealed class DuckDbOpenGateTests : IAsyncDisposable
{
    readonly DuckDbHost _h = new();
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    Task<DuckDBConnection> OpenAsync(string? path, FileAccessMode mode = FileAccessMode.ReadWrite, string grantId = "g1", CancellationToken ct = default) =>
        DuckDbInstances.OpenAsync(_h.AppData, path == null ? null : DuckDbHost.Grant(path, mode, grantId), ct);

    static string? Scalar(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Holds the first open of <paramref name="path"/> inside its gate until <c>Release</c>; counts the opens that reach the driver.</summary>
    sealed class Hold
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reached, Inside, MaxInside;

        public Hold(string path) => DuckDbInstances.BeforeOpen.Value = async (dataSource, ct) =>
        {
            if (dataSource != path) return;
            var n = Interlocked.Increment(ref Inside);
            lock (this) MaxInside = Math.Max(MaxInside, n);
            if (Interlocked.Increment(ref Reached) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            Interlocked.Decrement(ref Inside);
        };
    }

    [Fact]
    public async Task A_slow_open_of_one_file_does_not_hold_another_file_or_an_in_memory_database()
    {
        var slow = _h.NewDatabase("slow.duckdb");
        var other = _h.NewDatabase("other.duckdb");
        var hold = new Hold(slow);
        var first = OpenAsync(slow);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            await using (var b = await OpenAsync(other, grantId: "g2").WaitAsync(Bound))
                Assert.Equal("true", Scalar(b, "SELECT current_setting('lock_configuration')::VARCHAR"));
            await using (var m = await OpenAsync(null).WaitAsync(Bound))
                Assert.Equal("false", Scalar(m, "SELECT current_setting('enable_external_access')::VARCHAR"));
            Assert.False(first.IsCompleted);
        }
        finally { hold.Release.TrySetResult(); }
        await using var a = await first.WaitAsync(Bound);
        Assert.Contains(DuckDbInstances.Open(), o => o.DataSource == slow);
    }

    [Fact]
    public async Task Opens_of_one_file_still_wait_for_each_other_and_the_second_inherits_the_locked_instance()
    {
        var path = _h.NewDatabase();
        var hold = new Hold(path);
        var first = OpenAsync(path, grantId: "g1");
        Task<DuckDBConnection>? second = null;
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            second = OpenAsync(path, grantId: "g2");
            await Task.Delay(300);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, hold.Reached); // the second waits at the file's gate, before the driver
        }
        finally { hold.Release.TrySetResult(); }
        await using var a = await first.WaitAsync(Bound);
        await using var b = await second!.WaitAsync(Bound);
        Assert.Equal(2, hold.Reached);
        Assert.Equal(1, hold.MaxInside);
        Assert.Equal("true", Scalar(b, "SELECT current_setting('lock_configuration')::VARCHAR"));
        Assert.Single(DuckDbInstances.Open(), o => o.DataSource == path);
    }

    [Fact]
    public async Task While_the_first_open_is_in_flight_another_mode_is_refused_and_a_cancelled_waiter_leaves_nothing_behind()
    {
        var path = _h.NewDatabase();
        var hold = new Hold(path);
        var first = OpenAsync(path);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(path, FileAccessMode.ReadOnly, "g2").WaitAsync(Bound));
            Assert.Contains("already open with writes allowed", e.Message);
            using var cts = new CancellationTokenSource(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAsync(path, grantId: "g3", ct: cts.Token));
        }
        finally { hold.Release.TrySetResult(); }
        var a = await first.WaitAsync(Bound);
        await a.DisposeAsync();
        Assert.DoesNotContain(DuckDbInstances.Open(), o => o.DataSource == path);
        // Every connection closed: the other mode opens now.
        await using var ro = await OpenAsync(path, FileAccessMode.ReadOnly, "g4").WaitAsync(Bound);
        Assert.Single(DuckDbInstances.Open(), o => o.DataSource == path && o.Mode == FileAccessMode.ReadOnly);
    }

    [Fact]
    public async Task A_case_variant_opened_while_the_first_is_still_opening_is_refused()
    {
        if (!OperatingSystem.IsLinux()) return; // case-insensitive file systems (Windows, macOS): one file, one identity
        var lower = _h.NewDatabase("gate.duckdb");
        var upper = _h.NewDatabase("GATE.duckdb");
        var hold = new Hold(lower);
        var first = OpenAsync(lower);
        try
        {
            await hold.Entered.Task.WaitAsync(Bound);
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(upper, grantId: "g2").WaitAsync(Bound));
            Assert.Contains("letter case", e.Message);
        }
        finally { hold.Release.TrySetResult(); }
        await using var a = await first.WaitAsync(Bound);
    }
}
