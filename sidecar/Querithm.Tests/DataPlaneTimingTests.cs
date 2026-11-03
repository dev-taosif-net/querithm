using System.Text.RegularExpressions;
using Querithm.Core;
using Querithm.Core.Execution;
using Querithm.Host;

namespace Querithm.Tests;

/// <summary>
/// §5 timing invariant (8a review): the sidecar never drops the data-plane client every tab shares while the shell's one
/// socket reader may still unpark by itself. The shell pauses that reader on a full result queue and drops the queue once
/// its consumer has been silent for STALE_CONSUMER; an end-of-run frame stuck behind it waits FrameDrainLimit, then
/// FrameGrace, before the sidecar drops the client — so that sum must outlast STALE_CONSUMER with a margin. (It was 10 s +
/// 3 s against 30 s: a parked reader made every tab's frames fail.) The constants live in three languages; this test reads
/// the Rust and dev-bridge sources, so a change to either side that breaks the relation fails here.
/// </summary>
public class DataPlaneTimingTests
{
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(here)!); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src-tauri", "tauri.conf.json"))) return d.FullName;
        throw new InvalidOperationException("repository root not found above " + here);
    }

    static TimeSpan RustStaleConsumer()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src-tauri", "src", "ipc.rs"));
        var m = Regex.Match(source, @"const STALE_CONSUMER: Duration = Duration::from_(secs|millis)\((\d+)\);");
        Assert.True(m.Success, "STALE_CONSUMER not found in src-tauri/src/ipc.rs");
        var n = long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[1].Value == "secs" ? TimeSpan.FromSeconds(n) : TimeSpan.FromMilliseconds(n);
    }

    static TimeSpan BridgeStaleConsumer()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "dev-bridge-relay.mjs"));
        var m = Regex.Match(source, @"export const STALE_CONSUMER_MS = ([\d_]+);");
        Assert.True(m.Success, "STALE_CONSUMER_MS not found in scripts/dev-bridge-relay.mjs");
        return TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_sidecar_mirrors_the_shells_stale_consumer_bound()
    {
        Assert.Equal(RustStaleConsumer(), DataPlaneServer.ShellStaleConsumer);
        Assert.Equal(RustStaleConsumer(), BridgeStaleConsumer());
    }

    [Fact]
    public void An_end_of_run_frame_outlasts_a_paused_shell_reader_before_the_shared_client_is_dropped()
    {
        var giveUp = ExecutionService.DefaultFrameDrainLimit + DataPlaneServer.DefaultFrameGrace;
        Assert.True(giveUp > RustStaleConsumer() + DataPlaneServer.StaleMargin,
            $"FrameDrainLimit + FrameGrace = {giveUp.TotalSeconds:0} s must exceed STALE_CONSUMER ({RustStaleConsumer().TotalSeconds:0} s) + {DataPlaneServer.StaleMargin.TotalSeconds:0} s");
    }

    [Fact]
    public void A_cancelled_frame_still_ends_inside_the_cancel_escalations_first_step()
    {
        // The user's Cancel is the one case allowed to drop the client before the shell unparks: the run must end promptly
        // (§7 step 4), and the shell reconnects at once ("reconnecting the results channel").
        Assert.True(DataPlaneServer.DefaultFrameGrace < ExecutionService.DefaultCancelGrace);
    }

    /// <summary>The shell's connection, broken: every write fails part-way.</summary>
    sealed class BrokenStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Broken pipe");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException(new IOException("Broken pipe"));
    }

    [Fact]
    public async Task After_the_sidecar_drops_the_client_runs_are_told_the_channel_is_reconnecting_not_to_restart()
    {
        await using var server = new DataPlaneServer("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromMilliseconds(200));
        await server.AttachAsync(new BrokenStream());
        var id = Ulid.New();

        var broke = await Assert.ThrowsAsync<DataPlaneBrokenException>(() => server.WriteFrameAsync(FrameKind.Batch, id, 0, new byte[16], CancellationToken.None).AsTask());
        Assert.Contains("reconnecting the results channel", broke.Message);
        Assert.DoesNotContain("Restart", broke.Message);

        // The shell has not reconnected yet: the next frame finds no reader and says why.
        var missing = await Assert.ThrowsAsync<DataPlaneUnavailableException>(() => server.WriteFrameAsync(FrameKind.EndOfExecution, id, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask());
        Assert.Contains("reconnecting", missing.Message);
        Assert.DoesNotContain("Restart", missing.Message);

        // Once a reader attaches, frames flow again.
        var fresh = new MemoryStream();
        await server.AttachAsync(fresh);
        await server.WriteFrameAsync(FrameKind.EndOfExecution, id, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.True(fresh.Length > 0);
    }

    [Fact]
    public async Task A_shell_that_never_connected_still_asks_for_a_restart()
    {
        await using var server = new DataPlaneServer("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.FromMilliseconds(100));
        var ex = await Assert.ThrowsAsync<DataPlaneUnavailableException>(() => server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None).AsTask());
        Assert.Contains("Restart Querithm", ex.Message);
    }
}
