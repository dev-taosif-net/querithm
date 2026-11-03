namespace Querithm.Core;

/// <summary>Sidecar → webview JSON-RPC notifications. Implemented by Querithm.Host.</summary>
public interface INotifier
{
    void Notify(string method, object payload);
}

public static class FrameKind
{
    public const byte Schema = 0x01;
    public const byte Batch = 0x02;
    public const byte EndOfResultSet = 0x03;
    public const byte EndOfExecution = 0x04;
}

/// <summary>
/// Data plane (§5): [u32 length][u8 kind][16-byte executionId][u16 resultIndex][payload], little-endian.
/// Writes await when the reader (Rust) applies backpressure.
/// </summary>
public interface IFrameSink
{
    ValueTask WriteFrameAsync(byte kind, Ulid executionId, ushort resultIndex, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>Data plane frame limits (§5), shared with the shell (<c>src-tauri/src/ipc.rs</c>).</summary>
public static class FrameLimits
{
    /// <summary>What the length field counts besides the payload: kind, execution id, result index.</summary>
    public const int HeaderBody = 1 + 16 + 2;

    /// <summary>
    /// The largest length field the shell accepts (<c>MAX_FRAME_LEN</c> in ipc.rs, the same value): above it the shell
    /// treats the stream as one that lost its framing and reconnects. Batches flush at 10,000 rows or 4 MB, so one goes
    /// past 4 MB only by its last row, whose cells are capped (text 1 M characters, about 3 MB of UTF-8 at most; binary
    /// 64 KB); schema, end and import-preview frames are far smaller. A data-compare batch flushes at 1,000 diff rows or
    /// 4 MB too; its text is not capped, so only one diff row of very long values comes near it. A payload that would still
    /// exceed it fails its run before a byte is written (<see cref="FrameTooLargeException"/>).
    /// </summary>
    public const int MaxLength = 256 * 1024 * 1024;
}

/// <summary>
/// No reader is attached to the data plane: frames have nowhere to go. <paramref name="reconnecting"/>: the sidecar reset
/// the channel itself (a frame it could not finish, <see cref="DataPlaneBrokenException"/>) and the shell is reconnecting,
/// so the way out is a moment's wait, not a restart.
/// </summary>
public sealed class DataPlaneUnavailableException(bool reconnecting = false) : IOException(reconnecting
    ? "The results channel between the Querithm engine and the window is reconnecting (it was reset after a block of rows could not be sent), so rows cannot be shown right now. Run the query again in a moment."
    : "The results channel between the Querithm engine and the window is not connected, so rows cannot be shown. Restart Querithm to reconnect it.");

/// <summary>One frame would be larger than the data plane carries (<see cref="FrameLimits.MaxLength"/>); nothing of it was written.</summary>
public sealed class FrameTooLargeException(long payloadBytes) : IOException(
    $"A block of {payloadBytes / (1024 * 1024):N0} MB of results is larger than the {FrameLimits.MaxLength / (1024 * 1024)} MB the results channel " +
    "carries in one piece (a row with many very long values). Select fewer or shorter columns.");

/// <summary>
/// A frame failed part-way (the shell stopped reading, or its connection broke): the client was dropped so the shell
/// reconnects with clean framing, and this run's results are incomplete.
/// </summary>
public sealed class DataPlaneBrokenException(Exception inner) : IOException(
    "The results channel to the window broke while sending rows, so this run's results are incomplete. Querithm is reconnecting the results channel: run the query again in a moment.", inner);
