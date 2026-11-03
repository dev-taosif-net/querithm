using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Querithm.Core;

namespace Querithm.Host;

/// <summary>
/// The data plane (§5): one authenticated local socket (UDS 0600 / current-user named pipe), multiplexed
/// frames. The first line from the client must be the launch token or the connection is dropped (§13 rule 4).
/// No TCP port is ever bound.
/// </summary>
public sealed class DataPlaneServer(string endpoint, string token, ILogger log, TimeSpan? clientWait = null) : IFrameSink, IAsyncDisposable
{
    /// <summary>How long a frame waits for a reader to attach before the write fails with <see cref="DataPlaneUnavailableException"/>.</summary>
    static readonly TimeSpan DefaultClientWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a frame whose first byte is already written may still take once its caller's token fires (§5). The
    /// stream is shared by every run, so a frame is never abandoned half written: the shell would read the next frame's
    /// bytes as the rest of this one. Past this grace the client is dropped instead and the shell reconnects. Shorter than
    /// the cancel escalation's first step (5 s, §7), so a cancelled run still ends within it. Settable for tests.
    /// </summary>
    internal TimeSpan FrameGrace { get; set; } = DefaultFrameGrace;

    internal static readonly TimeSpan DefaultFrameGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The shell's <c>STALE_CONSUMER</c> (src-tauri/src/ipc.rs, scripts/dev-bridge-relay.mjs): how long its one socket
    /// reader may stay paused on a full result queue whose consumer stopped asking before it drops that queue and reads on.
    /// The §5 invariant <c>ExecutionService.DefaultFrameDrainLimit + DefaultFrameGrace &gt; ShellStaleConsumer +
    /// StaleMargin</c> keeps an end-of-run frame from dropping the shared client while the shell can still unpark (a
    /// test reads the Rust and dev-bridge constants and asserts it).
    /// </summary>
    internal static readonly TimeSpan ShellStaleConsumer = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StaleMargin = TimeSpan.FromSeconds(5);

    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    TaskCompletionSource<Stream> _client = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Socket? _listener;
    /// <summary>A write already waited out the client wait with no reader: later writes fail at once until one attaches.</summary>
    volatile bool _missing;
    /// <summary>
    /// When the sidecar dropped the client itself (a frame it could not finish; 0 = not since the last attach): for a
    /// while after, a missing reader is the shell reconnecting, and runs say so instead of asking for a restart.
    /// </summary>
    long _droppedAt;
    static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(30);

    bool Reconnecting => Interlocked.Read(ref _droppedAt) is var at and not 0 && Environment.TickCount64 - at < ReconnectWindow.TotalMilliseconds;

    /// <summary>Raised with false when frames start being refused for want of a reader, true when a reader attaches.</summary>
    public event Action<bool>? StateChanged;

    public string Endpoint => endpoint;

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    async Task AcceptLoopAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                while (!_stop.IsCancellationRequested)
                {
                    // Like the Unix socket's accept loop: the next instance listens while a client is attached (one
                    // instance only made it throw "All pipe instances are busy" and end the loop, so nothing could reconnect).
                    // Every connection still has to present the token, and the pipe stays current-user only.
                    var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 4 * 1024 * 1024);
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    if (await AuthenticateAsync(pipe).ConfigureAwait(false)) await AttachAsync(pipe).ConfigureAwait(false);
                    else await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                if (File.Exists(endpoint)) File.Delete(endpoint);
                var oldMask = Umask(0x3F); // create the socket file as 0600
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { _listener.Bind(new UnixDomainSocketEndPoint(endpoint)); }
                finally { Umask(oldMask); }
                File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _listener.Listen(1);
                while (!_stop.IsCancellationRequested)
                {
                    var s = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                    s.SendBufferSize = 4 * 1024 * 1024;
                    var ns = new NetworkStream(s, ownsSocket: true);
                    if (await AuthenticateAsync(ns).ConfigureAwait(false)) await AttachAsync(ns).ConfigureAwait(false);
                    else await ns.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogError(ex, "Data plane listener failed"); }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")]
    static extern int UmaskNative(int mask);

    static int Umask(int mask) => OperatingSystem.IsWindows() ? 0 : UmaskNative(mask);

    async Task<bool> AuthenticateAsync(Stream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buf = new byte[256];
        var len = 0;
        try
        {
            while (len < buf.Length)
            {
                var n = await s.ReadAsync(buf.AsMemory(len, 1), cts.Token).ConfigureAwait(false);
                if (n == 0) return false;
                if (buf[len] == (byte)'\n') break;
                len++;
            }
        }
        catch (OperationCanceledException) { return false; }
        var expected = Encoding.UTF8.GetBytes(token);
        var ok = len == expected.Length && CryptographicOperations.FixedTimeEquals(buf.AsSpan(0, len), expected);
        if (!ok) log.LogWarning("Data plane: rejected a connection with a bad token");
        return ok;
    }

    /// <summary>
    /// A new client replaces the old one between frames: the swap takes the write lock, so a frame being written
    /// finishes on the old stream before it is disposed, and later frames go to the new one.
    /// </summary>
    internal async Task AttachAsync(Stream s)
    {
        log.LogInformation("Data plane client connected");
        Stream? old = null;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client.Task.IsCompleted)
            {
                old = _client.Task.Result;
                _client = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _client.TrySetResult(s);
            _missing = false;
            Interlocked.Exchange(ref _droppedAt, 0);
        }
        finally { _writeLock.Release(); }
        StateChanged?.Invoke(true);
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // The shell refuses a longer frame as a stream that lost its framing: fail this run before writing anything.
        if (payload.Length > FrameLimits.MaxLength - FrameLimits.HeaderBody) throw new FrameTooLargeException(payload.Length);
        var header = new byte[4 + FrameLimits.HeaderBody];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(FrameLimits.HeaderBody + payload.Length));
        header[4] = kind;
        executionId.Bytes.CopyTo(header, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(21), resultIndex);
        Stream stream;
        TaskCompletionSource<Stream> client;
        while (true)
        {
            // Wait for a client outside the lock (AttachAsync needs it), then make sure it was not replaced meanwhile.
            // Never wait forever for a reader: a run parked here could not end, so its Cancel did nothing and its tab
            // stayed busy (the end-of-execution write runs without the run's token).
            client = _client;
            if (_missing && !client.Task.IsCompleted) throw new DataPlaneUnavailableException(Reconnecting);
            try { stream = await client.Task.WaitAsync(clientWait ?? DefaultClientWait, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                if (!_missing)
                {
                    _missing = true;
                    log.LogWarning("Data plane: no client attached; frames are refused until one connects");
                    StateChanged?.Invoke(false);
                }
                throw new DataPlaneUnavailableException(Reconnecting);
            }
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            if (ReferenceEquals(client, _client)) break;
            _writeLock.Release();
        }
        var started = false;
        try
        {
            // The caller's token decides only whether the frame starts. Once its first byte may be out, the frame completes
            // or the client goes: a cancel gives it FrameGrace, then the writes below stop and the client is dropped.
            ct.ThrowIfCancellationRequested();
            using var grace = new CancellationTokenSource();
            using var onCancel = ct.Register(() => grace.CancelAfter(FrameGrace));
            started = true;
            // Backpressure: when Rust pauses reading, these writes await (§5).
            await stream.WriteAsync(header, grace.Token).ConfigureAwait(false);
            if (!payload.IsEmpty) await stream.WriteAsync(payload, grace.Token).ConfigureAwait(false);
            await stream.FlushAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (started)
        {
            DropClient(client, stream, ex);
            // A cancelled run ends as cancelled; anything else fails it with the reason (its results are incomplete).
            if (ct.IsCancellationRequested) throw new OperationCanceledException("The frame was not completed after cancel.", ex, ct);
            throw new DataPlaneBrokenException(ex);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Under the write lock, after a frame failed part-way: its bytes may be on the wire, so nothing more may follow on
    /// this connection. The client is disposed (the shell sees the pipe close and reconnects, §5) and later frames wait
    /// for the next one, as before the first connect.
    /// </summary>
    void DropClient(TaskCompletionSource<Stream> client, Stream stream, Exception why)
    {
        if (ReferenceEquals(client, _client))
        {
            _client = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _droppedAt, Math.Max(1, Environment.TickCount64));
        }
        log.LogWarning("Data plane: a frame could not be completed ({Reason}); the client was dropped so the shell reconnects", why.Message);
        try { stream.Dispose(); }
        catch (Exception ex) { log.LogDebug("Data plane: disposing the dropped client failed: {Message}", ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Dispose();
        if (_client.Task.IsCompletedSuccessfully) await _client.Task.Result.DisposeAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            try { File.Delete(endpoint); } catch { /* ignore */ }
    }
}
