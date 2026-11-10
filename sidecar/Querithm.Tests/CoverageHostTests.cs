using System.Data.Common;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Querithm.Core;
using Querithm.Core.Logging;
using Querithm.Host;
using StreamJsonRpc;

namespace Querithm.Tests;

/// <summary>Host plumbing: the JSON-RPC error contract, ordered notifications, the scrubbing logger, orphan prevention.</summary>
public class HostRpcTests : IAsyncDisposable
{
    sealed class FakeDbException(string message) : DbException(message);

    public sealed class Target
    {
        public Task Throw(string kind) => kind switch
        {
            "app" => throw new QuerithmException("notFound", "Thing 'x' was not found. Password=hunter2", new { id = 7 }),
            "db" => throw new FakeDbException("login failed; pwd=topsecret"),
            "cancel" => throw new OperationCanceledException(),
            "agg" => throw new AggregateException(new QuerithmException("invalid", "bad input")),
            "empty" => throw new InvalidOperationException(""),
            _ => throw new InvalidOperationException("boom Authorization: Bearer abc.def"),
        };
        public string Echo(string s) => s;
    }

    readonly QuerithmJsonRpc _server;
    readonly JsonRpc _client;

    public HostRpcTests()
    {
        var a = new Pipe();
        var b = new Pipe();
        var fmt = () => new SystemTextJsonFormatter { JsonSerializerOptions = Json.Create() };
        _server = new QuerithmJsonRpc(new NewLineDelimitedMessageHandler(b.Writer.AsStream(), a.Reader.AsStream(), fmt()));
        _server.AddLocalRpcTarget(new Target());
        _server.StartListening();
        _client = new JsonRpc(new NewLineDelimitedMessageHandler(a.Writer.AsStream(), b.Reader.AsStream(), fmt()));
        _client.StartListening();
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    async Task<RemoteInvocationException> Fail(string kind) =>
        await Assert.ThrowsAsync<RemoteInvocationException>(() => _client.InvokeAsync("Throw", [kind]));

    [Fact]
    public async Task Application_errors_carry_the_stable_code_and_a_scrubbed_message()
    {
        var ex = await Fail("app");
        Assert.Equal(QuerithmJsonRpc.AppError, ex.ErrorCode);
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.Contains("***", ex.Message);
        Assert.Contains("notFound", ex.ErrorData?.ToString() ?? "");
    }

    [Fact]
    public async Task Aggregate_wrappers_are_unwrapped()
    {
        var ex = await Fail("agg");
        Assert.Equal(QuerithmJsonRpc.AppError, ex.ErrorCode);
        Assert.Equal("bad input", ex.Message);
    }

    [Fact]
    public async Task Database_errors_map_to_their_own_code_and_are_scrubbed()
    {
        var ex = await Fail("db");
        Assert.Equal(QuerithmJsonRpc.DatabaseError, ex.ErrorCode);
        Assert.DoesNotContain("topsecret", ex.Message);
    }

    [Fact]
    public async Task Cancellation_reaches_the_caller_as_cancellation_not_as_an_internal_error()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.InvokeAsync("Throw", ["cancel"]));
    }

    [Fact]
    public async Task Unexpected_errors_are_internal_errors_with_scrubbed_text_and_no_stack()
    {
        var ex = await Fail("other");
        Assert.Equal((int)StreamJsonRpc.Protocol.JsonRpcErrorCode.InternalError, ex.ErrorCode);
        Assert.DoesNotContain("abc.def", ex.Message);
        Assert.DoesNotContain(" at ", ex.Message);
        Assert.Contains("InvalidOperationException", ex.ErrorData?.ToString() ?? "");
    }

    [Fact]
    public async Task An_exception_without_a_message_still_reaches_the_caller_with_one()
    {
        // Regression: an interop TypeLoadException has an empty message, and the UI showed a blank red error.
        var ex = await Fail("empty");
        Assert.Contains("InvalidOperationException", ex.Message);
    }

    [Fact]
    public void Trimmed_host_keeps_builtin_com_marshalling_for_the_native_sql_client()
    {
        // Regression: trimming disables built-in COM marshalling, and SqlClient's native SNI on Windows marshals
        // AuthProviderInfo's object fields with it, so every TLS pre-login failed. CI trims only on Linux (managed SNI).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "sidecar", "Querithm.Host", "Querithm.Host.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(dir!.FullName, "sidecar", "Querithm.Host", "Querithm.Host.csproj"));
        Assert.Contains(project.Descendants("BuiltInComInteropSupport"), e => e.Value.Trim() == "true");
    }

    [Fact]
    public async Task Notifications_arrive_in_the_order_they_were_queued()
    {
        var received = new List<int>();
        var done = new TaskCompletionSource();
        var a = new Pipe();
        var b = new Pipe();
        var fmt = () => new SystemTextJsonFormatter { JsonSerializerOptions = Json.Create() };
        using var server = new JsonRpc(new NewLineDelimitedMessageHandler(b.Writer.AsStream(), a.Reader.AsStream(), fmt()));
        using var client = new JsonRpc(new NewLineDelimitedMessageHandler(a.Writer.AsStream(), b.Reader.AsStream(), fmt()));
        client.AddLocalRpcMethod("tick", new Action<int>(n => { lock (received) { received.Add(n); if (received.Count == 50) done.TrySetResult(); } }));
        server.StartListening();
        client.StartListening();
        var notifier = new RpcNotifier();
        notifier.Attach(server);
        for (var i = 0; i < 50; i++) notifier.Notify("tick", new { n = i });
        await done.Task.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.Equal(Enumerable.Range(0, 50), received);
    }

    [Fact]
    public async Task Notifier_survives_a_disposed_connection()
    {
        var a = new Pipe();
        var b = new Pipe();
        var server = new JsonRpc(new NewLineDelimitedMessageHandler(b.Writer.AsStream(), a.Reader.AsStream(), new SystemTextJsonFormatter()));
        server.StartListening();
        var notifier = new RpcNotifier();
        notifier.Attach(server);
        server.Dispose();
        notifier.Notify("tick", new { n = 1 });
        notifier.Notify("tick", new { n = 2 });
        await Task.Delay(100);   // the pump swallows the failure instead of crashing the process
    }
}

public class ScrubbingLoggerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "qlog-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Lines_are_scrubbed_filtered_by_level_and_written_to_the_daily_file()
    {
        string text;
        using (var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Warning))
        {
            var log = provider.CreateLogger("cat");
            Assert.False(log.IsEnabled(LogLevel.Information));
            Assert.True(log.IsEnabled(LogLevel.Error));
            Assert.Null(log.BeginScope("x"));
            log.LogInformation("hidden line");
            log.LogWarning("connect Server=x;Password=hunter2 failed");
            log.LogError(new InvalidOperationException("inner pwd=s3cret"), "boom");
            var file = Directory.GetFiles(_dir, "sidecar-*.log").Single();
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            text = new StreamReader(fs).ReadToEnd();
        }
        Assert.DoesNotContain("hidden line", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("[warn] cat: connect Server=x;Password=*** failed", text);
        Assert.Contains("[erro] cat: boom", text);
    }

    [Fact]
    public void Files_older_than_the_retention_or_past_the_count_are_removed_and_the_new_one_stays()
    {
        Directory.CreateDirectory(_dir);
        var now = DateTime.UtcNow;
        // 3 files of last month, 20 of the last days: the old ones go, then the newest MaxFiles (with today's) stay.
        for (var d = 0; d < 23; d++)
        {
            var f = Path.Combine(_dir, $"sidecar-2026{d:00}01-{100 + d}.log");
            File.WriteAllText(f, "old");
            File.SetLastWriteTimeUtc(f, d < 3 ? now.AddDays(-30) : now.AddHours(-d));
        }
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a log");
        string? current;
        using (var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Information)) current = provider.FilePath;
        var left = Directory.GetFiles(_dir, "sidecar-*.log");
        Assert.Equal(LogFiles.MaxFiles, left.Length);
        Assert.Contains(current, left);
        Assert.All(left, f => Assert.True(now - File.GetLastWriteTimeUtc(f) <= TimeSpan.FromDays(LogFiles.KeepDays), f));
        Assert.True(File.Exists(Path.Combine(_dir, "notes.txt")), "only Querithm's own logs are pruned");
    }

    [Fact]
    public void Each_process_writes_its_own_file_and_a_full_file_rolls_over_to_the_next_part()
    {
        using var provider = new ScrubbingLoggerProvider(_dir, LogLevel.Information, echoToStderr: false, maxFileBytes: 400);
        var first = provider.FilePath!;
        Assert.Equal($"sidecar-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}.log", Path.GetFileName(first));
        var log = provider.CreateLogger("cap");
        for (var i = 0; i < 12; i++) log.LogInformation("line {N} pwd=s3cret padding padding padding", i);
        var parts = Directory.GetFiles(_dir, $"sidecar-*-{Environment.ProcessId}*.log");
        Assert.True(parts.Length >= 2, string.Join(", ", parts));
        Assert.Contains(parts, p => p.EndsWith($"-{Environment.ProcessId}-2.log", StringComparison.Ordinal));
        // A part stops at the first line past the cap: no file grows much beyond it.
        Assert.All(parts, p => Assert.True(new FileInfo(p).Length < 400 + 200, p));
        var text = string.Concat(parts.Select(p => { using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(fs).ReadToEnd(); }));
        Assert.Contains("line 11", text);
        Assert.DoesNotContain("s3cret", text);
    }

    [Fact]
    public void An_unusable_log_directory_falls_back_to_stderr_only()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "qlogfile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            using var provider = new ScrubbingLoggerProvider(Path.Combine(blocker, "sub"), LogLevel.Information);
            provider.CreateLogger("c").LogInformation("still works");
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void A_null_log_directory_logs_to_stderr_only()
    {
        using var provider = new ScrubbingLoggerProvider(null, LogLevel.Debug);
        provider.CreateLogger("c").LogDebug("to stderr");
    }
}

public class DataPlaneServerTests : IAsyncDisposable
{
    readonly string _sock = Path.Combine(Path.GetTempPath(), $"qdp-{Guid.NewGuid():N}.sock");
    readonly DataPlaneServer _server;

    public DataPlaneServerTests()
    {
        _server = new DataPlaneServer(_sock, "launch-token-1", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        if (OperatingSystem.IsWindows()) return;   // the Windows endpoint is a named pipe; the framing is shared
        _server.Start();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    async Task<System.Net.Sockets.NetworkStream> Connect(string token)
    {
        for (var i = 0; i < 100 && !File.Exists(_sock); i++) await Task.Delay(20);
        // The socket file appears at bind(), a moment before listen(): retry a refused connect.
        System.Net.Sockets.Socket s;
        for (var attempt = 0; ; attempt++)
        {
            s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            try { await s.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(_sock)); break; }
            catch (System.Net.Sockets.SocketException) when (attempt < 100) { s.Dispose(); await Task.Delay(20); }
        }
        var ns = new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
        await ns.WriteAsync(System.Text.Encoding.UTF8.GetBytes(token + "\n"));
        return ns;
    }

    static async Task<byte[]> ReadExactly(Stream s, int n)
    {
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf).AsTask().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        return buf;
    }

    [Fact]
    public async Task Frames_carry_length_kind_execution_id_and_result_index()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var client = await Connect("launch-token-1");
        var id = Ulid.New();
        await _server.WriteFrameAsync(FrameKind.Batch, id, 7, new byte[] { 1, 2, 3, 4, 5 }, CancellationToken.None);
        await _server.WriteFrameAsync(FrameKind.EndOfExecution, id, 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        var h1 = await ReadExactly(client, 23);
        Assert.Equal(1u + 16 + 2 + 5, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h1));
        Assert.Equal(FrameKind.Batch, h1[4]);
        Assert.Equal(id.Bytes.ToArray(), h1[5..21]);
        Assert.Equal(7, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(h1.AsSpan(21)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await ReadExactly(client, 5));
        var h2 = await ReadExactly(client, 23);
        Assert.Equal(1u + 16 + 2, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(h2));
        Assert.Equal(FrameKind.EndOfExecution, h2[4]);
    }

    [Fact]
    public async Task A_connection_with_the_wrong_token_is_dropped_and_never_receives_frames()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var bad = await Connect("not-the-token");
        var buf = new byte[1];
        var n = await bad.ReadAsync(buf).AsTask().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, n);   // closed by the server
        await using var good = await Connect("launch-token-1");
        await _server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(FrameKind.EndOfExecution, (await ReadExactly(good, 23))[4]);
    }

    [Fact]
    public async Task The_socket_is_owner_only_and_removed_on_dispose()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var client = await Connect("launch-token-1");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_sock));
        Assert.Equal(_sock, _server.Endpoint);
        await _server.DisposeAsync();
        Assert.False(File.Exists(_sock));
    }

    [Fact]
    public async Task A_new_authenticated_client_replaces_the_old_one()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var first = await Connect("launch-token-1");
        await _server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await ReadExactly(first, 23);
        await using var second = await Connect("launch-token-1");
        await Task.Delay(500);   // the attach happens asynchronously after the token line is read
        await _server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 9, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        var got = await ReadExactly(second, 23);
        Assert.Equal(9, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(got.AsSpan(21)));
    }

    /// <summary>A client stream whose writes wait until released; records whether it was disposed mid-write.</summary>
    sealed class GatedStream : MemoryStream
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, DisposedMidWrite;
        int _writing;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writing);
            Entered.TrySetResult();
            await Gate.Task.WaitAsync(ct);
            ObjectDisposedException.ThrowIf(Disposed, this);
            await base.WriteAsync(buffer, ct);
            Interlocked.Decrement(ref _writing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposedMidWrite |= Volatile.Read(ref _writing) > 0;
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_reconnect_lets_the_frame_in_flight_finish_on_the_old_client_and_sends_the_next_to_the_new_one()
    {
        // Platform-neutral: attaches streams directly, as the accept loop does after the token check.
        await using var server = new DataPlaneServer("unused", "t", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var old = new GatedStream();
        await server.AttachAsync(old);
        var inFlight = server.WriteFrameAsync(FrameKind.Batch, Ulid.New(), 1, new byte[] { 1, 2, 3 }, CancellationToken.None).AsTask();
        await old.Entered.Task.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));

        var fresh = new MemoryStream();
        var attach = server.AttachAsync(fresh);
        await Task.Delay(50);
        Assert.False(attach.IsCompleted);   // waits for the frame being written
        old.Gate.SetResult();
        await inFlight.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        await attach.WaitAsync(TestTime.Of(TimeSpan.FromSeconds(10)));
        Assert.True(old.Disposed);
        Assert.False(old.DisposedMidWrite);
        Assert.Equal(23 + 3, old.Length);

        await server.WriteFrameAsync(FrameKind.EndOfExecution, Ulid.New(), 2, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(23, fresh.Length);
    }
}

/// <summary>Runs the real sidecar executable the way the Rust shell does: env-configured, JSON-RPC on stdio.</summary>
public class SidecarProcessTests
{
    // The host's own build output, not the copy next to the tests: coverage instrumentation of a second process would clobber the test run's hit files.
    static string SidecarDll
    {
        get
        {
            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var cfg = tfm.Parent!;
            var sidecar = Path.Combine(cfg.Parent!.Parent!.Parent!.FullName, "Querithm.Host", "bin", cfg.Name, tfm.Name, "querithm-sidecar.dll");
            return sidecar;
        }
    }

    static System.Diagnostics.Process Start(Dictionary<string, string?> env)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{SidecarDll}\"")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var (k, v) in env) { if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v; }
        return System.Diagnostics.Process.Start(psi)!;
    }

    [Fact]
    public async Task Refuses_to_start_without_the_launch_token()
    {
        Assert.True(File.Exists(SidecarDll), "the host project builds with the tests: " + SidecarDll);
        using var p = Start(new() { ["QUERITHM_TOKEN"] = null, ["QUERITHM_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "qsc-" + Guid.NewGuid().ToString("N")) });
        var err = await p.StandardError.ReadToEndAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
        await p.WaitForExitAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("QUERITHM_TOKEN is required", err);
    }

    [Fact]
    public async Task Answers_ping_logs_to_its_data_dir_and_exits_cleanly_on_shutdown()
    {
        Assert.True(File.Exists(SidecarDll), "the host project builds with the tests: " + SidecarDll);
        var dir = Path.Combine(Path.GetTempPath(), "qsc-" + Guid.NewGuid().ToString("N"));
        var sock = Path.Combine(Path.GetTempPath(), $"qsc-{Guid.NewGuid():N}.sock");
        try
        {
            using var p = Start(new()
            {
                ["QUERITHM_TOKEN"] = "tok-123", ["QUERITHM_DATA_DIR"] = dir, ["QUERITHM_SOCKET"] = sock, ["QUERITHM_LOG"] = "debug",
                ["QUERITHM_PARENT_PID"] = Environment.ProcessId.ToString(),
            });
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"system.ping\"}");
            await p.StandardInput.FlushAsync();
            var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(90)));
            Assert.NotNull(line);
            using (var doc = System.Text.Json.JsonDocument.Parse(line!))
            {
                var result = doc.RootElement.GetProperty("result");
                Assert.Equal(p.Id, result.GetProperty("pid").GetInt32());
                Assert.False(string.IsNullOrEmpty(result.GetProperty("version").GetString()));
            }
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"system.shutdown\"}");
            await p.StandardInput.FlushAsync();
            await p.WaitForExitAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("Querithm sidecar", string.Concat(Directory.GetFiles(Path.Combine(dir, "logs"), "sidecar-*.log").Select(File.ReadAllText)));
            Assert.False(File.Exists(sock));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task A_damaged_store_is_set_aside_the_engine_starts_and_says_so_once()
    {
        Assert.True(File.Exists(SidecarDll), "the host project builds with the tests: " + SidecarDll);
        var dir = Path.Combine(Path.GetTempPath(), "qsc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "store.db"), "garbage, not a database " + new string('z', 500));
        var sock = Path.Combine(Path.GetTempPath(), $"qsc-{Guid.NewGuid():N}.sock");
        try
        {
            using var p = Start(new()
            {
                ["QUERITHM_TOKEN"] = "tok-123", ["QUERITHM_DATA_DIR"] = dir, ["QUERITHM_SOCKET"] = sock, ["QUERITHM_LOG_STDERR"] = "0",
                ["QUERITHM_PARENT_PID"] = Environment.ProcessId.ToString(),
            });
            async Task<System.Text.Json.JsonElement> Call(int id, string method, string @params = "")
            {
                await p.StandardInput.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"{@params}}}");
                await p.StandardInput.FlushAsync();
                var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(90)));
                Assert.NotNull(line);
                using var doc = System.Text.Json.JsonDocument.Parse(line!);
                return doc.RootElement.GetProperty("result").Clone();
            }
            var notices = await Call(1, "system.notices");
            var notice = Assert.Single(notices.EnumerateArray());
            Assert.Equal("storeReset", notice.GetProperty("code").GetString());
            var file = notice.GetProperty("file").GetString()!;
            Assert.StartsWith("store.db.corrupt-", file);
            Assert.Contains("Passwords in the system keyring are untouched", notice.GetProperty("message").GetString());
            Assert.StartsWith("garbage, not a database", File.ReadAllText(Path.Combine(dir, file)));
            Assert.Empty((await Call(2, "system.notices")).EnumerateArray());
            Assert.Equal(0, (await Call(3, "profile.list")).GetArrayLength());
            var tail = await Call(4, "system.logTail", ",\"params\":{\"lines\":50}");
            Assert.Contains(tail.GetProperty("lines").EnumerateArray(), l => l.GetString()!.Contains("set aside as " + file, StringComparison.Ordinal));
            await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"system.shutdown\"}");
            await p.StandardInput.FlushAsync();
            await p.WaitForExitAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
            Assert.Equal(0, p.ExitCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Exits_with_code_3_when_the_launcher_is_not_its_parent()
    {
        // Orphan prevention (§4): the shell passes its pid; a mismatch means it already died. Run out of process because
        // ParentWatch sets PR_SET_PDEATHSIG, which must never be applied to the test host itself.
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;   // Windows relies on the Job Object plus a process wait
        Assert.True(File.Exists(SidecarDll), SidecarDll);
        var dir = Path.Combine(Path.GetTempPath(), "qsc-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var p = Start(new()
            {
                ["QUERITHM_TOKEN"] = "tok-123", ["QUERITHM_DATA_DIR"] = dir, ["QUERITHM_PARENT_PID"] = "1999999999",
                ["QUERITHM_SOCKET"] = Path.Combine(Path.GetTempPath(), $"qsc-{Guid.NewGuid():N}.sock"),
            });
            await p.WaitForExitAsync().WaitAsync(TestTime.Of(TimeSpan.FromSeconds(60)));
            Assert.Equal(3, p.ExitCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class PeerOwnershipEdgeTests
{
    [Fact]
    public void A_connection_the_os_does_not_know_is_denied_where_the_os_can_tell()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var nobody = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1);
        var nowhere = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 2);
        Assert.False(Querithm.Core.Ssh.PeerOwnership.IsOwnConnection(nobody, nowhere));
    }

    [Theory]
    [InlineData("127.0.0.1", 22u, true)]
    [InlineData("::1", 2222u, true)]
    [InlineData("not an address", 22u, false)]
    [InlineData("10.0.0.1", 70000u, false)]
    public void Originator_text_parses_into_an_endpoint(string host, uint port, bool ok)
    {
        var ep = Querithm.Core.Ssh.PeerOwnership.Endpoint(host, port);
        Assert.Equal(ok, ep != null);
        if (ok) Assert.Equal((int)port, ep!.Port);
    }
}
