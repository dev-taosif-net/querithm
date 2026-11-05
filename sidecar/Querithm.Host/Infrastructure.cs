using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core;
using Querithm.Core.Logging;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Querithm.Host;

/// <summary>
/// Sends notifications in call order through a single consumer (StreamJsonRpc writes are not FIFO under contention). M13:
/// at most <see cref="Capacity"/> wait for stdout. Past that the shell has stopped reading, and the kinds a later one
/// replaces or that only add detail (message batches, progress, monitor snapshots, trace events) are dropped and counted;
/// lifecycle notifications (a run's start, result sets and end, state changes) are always queued, so no run is left
/// waiting for its end. Notify never blocks: it is called from driver callbacks.
/// </summary>
public sealed class RpcNotifier(int capacity = RpcNotifier.DefaultCapacity) : INotifier
{
    public const int DefaultCapacity = 10_000;

    static readonly HashSet<string> Droppable = new(StringComparer.Ordinal)
    {
        "query.messages", "query.message", "import.progress", "dataCompare.progress", "monitor.snapshot", "trace.event",
    };

    readonly Channel<(string Method, object Payload)> _queue = Channel.CreateUnbounded<(string, object)>(new UnboundedChannelOptions { SingleReader = true });
    JsonRpc? _rpc;
    int _queued;
    long _dropped;

    public int Capacity { get; } = capacity;
    /// <summary>Notifications waiting for stdout (tests).</summary>
    internal int Queued => Volatile.Read(ref _queued);
    /// <summary>Notifications dropped since the queue was last below its capacity (tests).</summary>
    internal long Dropped => Interlocked.Read(ref _dropped);

    public void Attach(JsonRpc rpc)
    {
        _rpc = rpc;
        _ = Task.Run(PumpAsync);
    }

    public void Notify(string method, object payload)
    {
        if (Volatile.Read(ref _queued) >= Capacity && Droppable.Contains(method))
        {
            if (Interlocked.Increment(ref _dropped) == 1)
                Console.Error.WriteLine($"[warn] notifications back up ({Capacity:N0} waiting): dropping {method} and the like until stdout drains");
            return;
        }
        Interlocked.Increment(ref _queued);
        if (!_queue.Writer.TryWrite((method, payload))) Interlocked.Decrement(ref _queued);
    }

    async Task PumpAsync()
    {
        await foreach (var (method, payload) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Interlocked.Decrement(ref _queued) < Capacity / 2 && Interlocked.Read(ref _dropped) > 0)
                Console.Error.WriteLine($"[warn] notifications drained; {Interlocked.Exchange(ref _dropped, 0):N0} were dropped");
            try { await _rpc!.NotifyWithParameterObjectAsync(method, payload).ConfigureAwait(false); }
            catch (Exception) when (_rpc!.IsDisposed) { return; }
            catch (Exception ex) { Console.Error.WriteLine($"[warn] notify {method} failed: {LogScrubber.Scrub(ex.Message)}"); }
        }
    }
}

/// <summary>
/// Logs to a file per process and day under logs/ and to stderr (relayed by Rust); every line passes the scrubber (§13
/// rule 1). M21: a file that reaches <see cref="LogFiles.MaxFileBytes"/> rolls over to the next part, and the retention
/// rule (<see cref="LogFiles.Prune"/>) runs at start and at every roll. The release shell sets <c>QUERITHM_LOG_STDERR=0</c>:
/// lines then go to the file only, so its own log keeps just what the sidecar printed outside the logger.
/// </summary>
public sealed class ScrubbingLoggerProvider : ILoggerProvider
{
    readonly LogLevel _min;
    readonly object _gate = new();
    readonly string? _dir;
    readonly bool _echo;
    readonly long _cap;
    readonly DateTime _day = DateTime.UtcNow;
    int _part = 1;
    StreamWriter? _file;

    /// <summary>The file being written (null: stderr only).</summary>
    public string? FilePath { get; private set; }

    public ScrubbingLoggerProvider(string? logDir, LogLevel min, bool echoToStderr = true, long maxFileBytes = LogFiles.MaxFileBytes)
    {
        _min = min;
        _echo = echoToStderr;
        _cap = maxFileBytes;
        if (logDir != null)
        {
            try
            {
                // L4: logs/ 0700 and each log 0600 on Unix (scrubbed, but still the user's activity).
                Querithm.Core.Store.PrivateFiles.Folder(logDir);
                _dir = logDir;
                Open();
            }
            catch { _file = null; }
        }
    }

    /// <summary>Opens this process's current part, skipping parts already full (a reused pid on the same day).</summary>
    void Open()
    {
        string path;
        while (true)
        {
            path = LogFiles.SidecarPath(_dir!, _day, Environment.ProcessId, _part);
            if (!File.Exists(path) || new FileInfo(path).Length < _cap) break;
            _part++;
        }
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Querithm.Core.Store.PrivateFiles.FileMode;
        _file = new StreamWriter(new FileStream(path, options)) { AutoFlush = true };
        Querithm.Core.Store.PrivateFiles.NarrowIfExists(path);
        FilePath = path;
        LogFiles.Prune(_dir!, "sidecar-", DateTime.UtcNow, keep: path);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var line = $"{DateTime.UtcNow:O} [{level.ToString().ToLowerInvariant()[..4]}] {category}: {LogScrubber.Scrub(message)}";
        if (ex != null) line += Environment.NewLine + LogScrubber.Scrub(ex.ToString());
        lock (_gate)
        {
            if (_echo || _file == null) Console.Error.WriteLine(line);
            if (_file == null) return;
            try
            {
                _file.WriteLine(line);
                if (_file.BaseStream.Length >= _cap)
                {
                    _file.Dispose();
                    _file = null;
                    _part++;
                    Open();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A full disk or a removed folder: stderr keeps the lines.
                try { _file?.Dispose(); } catch (IOException) { }
                _file = null;
                FilePath = null;
                Console.Error.WriteLine(line);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) _file?.Dispose();
    }

    sealed class Logger(ScrubbingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner._min;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

/// <summary>JSON-RPC error mapping: stable codes, scrubbed messages, no stack traces on the wire.</summary>
public sealed class QuerithmJsonRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
{
    public const int AppError = -32001;
    public const int DatabaseError = -32002;

    /// <summary>The control plane's formatter: <see cref="Json.CreateRpc"/>, member names bound exactly (M1).</summary>
    public static SystemTextJsonFormatter Formatter() => new GuardedJsonFormatter { JsonSerializerOptions = Json.CreateRpc() };

    /// <summary>Methods only the bench and smoke harnesses call (§14): registered only when the harness asks for them.</summary>
    public const string BenchPrefix = "bench.";

    /// <summary>
    /// Every <see cref="IQuerithmRpc"/> method under its contract name; a call's params object is its one DTO. The
    /// <c>bench.*</c> methods (a synthetic million-row stream) exist only with <paramref name="bench"/>: the harnesses
    /// launch the engine with <c>QUERITHM_BENCH=1</c>; a normal run answers them "method not found" (L10).
    /// </summary>
    public void AddMethods(RpcServer server, bool bench = false)
    {
        var map = typeof(RpcServer).GetInterfaceMap(typeof(IQuerithmRpc));
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var name = map.InterfaceMethods[i].GetCustomAttribute<RpcMethodAttribute>()!.Name;
            if (!bench && name.StartsWith(BenchPrefix, StringComparison.Ordinal)) continue;
            AddLocalRpcMethod(map.TargetMethods[i], server, new JsonRpcMethodAttribute(name) { UseSingleObjectParameterDeserialization = true });
        }
        // PT8-5: what GuardedJsonFormatter turns a malformed message into; it answers the caller's id with the error.
        AddLocalRpcMethod(GuardedJsonFormatter.MalformedMethod, new Func<int, string, Task>((code, message) => throw new MalformedRequestException(code, message)));
    }

    protected override JsonRpcError.ErrorDetail CreateErrorDetails(JsonRpcRequest request, Exception exception)
    {
        var ex = exception;
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } ti }) ex = ti;
        var detail = ex switch
        {
            MalformedRequestException m => new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)m.Code,
                Message = m.Message,
                Data = new Dictionary<string, object?> { ["code"] = m.Code == GuardedJsonFormatter.ParseError ? "parseError" : "invalidRequest" },
            },
            QuerithmException q => new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)AppError,
                Message = LogScrubber.Scrub(q.Message),
                Data = new Dictionary<string, object?> { ["code"] = q.Code, ["data"] = q.DataPayload },
            },
            System.Data.Common.DbException db => new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)DatabaseError,
                Message = LogScrubber.Scrub(db.Message),
                Data = new Dictionary<string, object?> { ["code"] = "database" },
            },
            OperationCanceledException => new JsonRpcError.ErrorDetail
            {
                Code = (JsonRpcErrorCode)AppError,
                Message = "The operation was cancelled.",
                Data = new Dictionary<string, object?> { ["code"] = "cancelled" },
            },
            _ => new JsonRpcError.ErrorDetail
            {
                Code = JsonRpcErrorCode.InternalError,
                Message = LogScrubber.Scrub(ex.Message),
                Data = new Dictionary<string, object?> { ["code"] = "internal", ["type"] = ex.GetType().Name },
            },
        };
        detail.Message = Describe(ex, detail.Message);
        return detail;
    }

    /// <summary>Never an empty error: some runtime failures (e.g. an interop TypeLoadException) carry no message at all.</summary>
    public static string Describe(Exception ex, string? message) =>
        string.IsNullOrWhiteSpace(message) ? $"{ex.GetType().Name}: the driver reported no details." : message;
}

/// <summary>A JSON-RPC message the control plane could not take, answered with <see cref="Code"/> (-32700 or -32600).</summary>
public sealed class MalformedRequestException(int code, string message)
    : Exception(message)
{
    public int Code { get; } = code == GuardedJsonFormatter.ParseError ? code : GuardedJsonFormatter.InvalidRequest;
}

/// <summary>
/// PT8-5: the control plane's formatter. StreamJsonRpc closes the connection on any message it cannot deserialize (params
/// nested past System.Text.Json's 64 levels, while the shell's serde_json allows 128; params that are a string, number or
/// boolean), and the sidecar exits when it closes, ending every session and open transaction. Each message is first
/// scanned without a depth limit for its shape: one that is too large, nests more than <see cref="MaxDepth"/> levels, has
/// params that are not an object or array, or that the stock formatter cannot read becomes a call of
/// <see cref="MalformedMethod"/> under the caller's id, which answers it with -32700 / -32600 and keeps the connection open.
/// </summary>
public sealed class GuardedJsonFormatter : SystemTextJsonFormatter, IJsonRpcMessageTextFormatter
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    /// <summary>Containers one message may nest: System.Text.Json's own limit, which reads every request's params.</summary>
    public const int MaxDepth = 64;
    /// <summary>
    /// The largest message parsed. The newline-delimited handler has no limit of its own, so the line is already buffered;
    /// this bounds the parse and the DTO that would be built from it. Scripts and results never come close.
    /// </summary>
    public const long MaxMessageBytes = 64L * 1024 * 1024;
    /// <summary>The internal method a malformed message is answered through ("rpc." names are reserved by JSON-RPC).</summary>
    public const string MalformedMethod = "rpc.malformed";
    const int MaxIdBytes = 256;

    /// <summary>The cap this formatter applies (tests lower it).</summary>
    internal long MaxBytes { get; init; } = MaxMessageBytes;

    JsonRpcMessage IJsonRpcMessageFormatter.Deserialize(System.Buffers.ReadOnlySequence<byte> contentBuffer) => Guard(contentBuffer, Encoding);

    JsonRpcMessage IJsonRpcMessageTextFormatter.Deserialize(System.Buffers.ReadOnlySequence<byte> contentBuffer, System.Text.Encoding encoding) => Guard(contentBuffer, encoding);

    JsonRpcMessage Guard(System.Buffers.ReadOnlySequence<byte> buffer, System.Text.Encoding encoding)
    {
        var (problem, code, id) = Inspect(buffer);
        if (problem == null)
        {
            try { return base.Deserialize(buffer, encoding); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                (problem, code) = ($"The request could not be read ({ex.GetType().Name}).", InvalidRequest);
            }
        }
        Console.Error.WriteLine($"[warn] malformed JSON-RPC message answered with {code}: {problem}");
        return base.Deserialize(new System.Buffers.ReadOnlySequence<byte>(Substitute(id, code, problem)), System.Text.Encoding.UTF8);
    }

    /// <summary>The message's shape, read token by token (no recursion, no depth limit): the problem, if any, and the raw id.</summary>
    (string? Problem, int Code, byte[]? Id) Inspect(System.Buffers.ReadOnlySequence<byte> buffer)
    {
        byte[]? id = null;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(buffer, new System.Text.Json.JsonReaderOptions { MaxDepth = int.MaxValue });
            if (!reader.Read()) return ("The message is empty.", ParseError, null);
            if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject) return ("A request must be a JSON object.", InvalidRequest, null);
            var depth = 1;
            System.Text.Json.JsonTokenType? paramsKind = null;
            string? pending = null;
            while (reader.Read())
            {
                var t = reader.TokenType;
                if (t is System.Text.Json.JsonTokenType.StartObject or System.Text.Json.JsonTokenType.StartArray) depth = Math.Max(depth, reader.CurrentDepth + 1);
                if (pending == "id")
                {
                    id = t switch
                    {
                        System.Text.Json.JsonTokenType.Number when reader.ValueSpan.Length <= MaxIdBytes => reader.ValueSpan.ToArray(),
                        System.Text.Json.JsonTokenType.String when !reader.HasValueSequence && reader.ValueSpan.Length <= MaxIdBytes => [(byte)'"', .. reader.ValueSpan, (byte)'"'],
                        _ => null,
                    };
                }
                else if (pending == "params") paramsKind = t;
                pending = t == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1
                    ? reader.ValueTextEquals("id") ? "id" : reader.ValueTextEquals("params") ? "params" : null
                    : null;
            }
            if (buffer.Length > MaxBytes) return ($"The request is larger than {Math.Max(1, MaxBytes / (1024 * 1024))} MB.", InvalidRequest, id);
            if (depth > MaxDepth) return ($"The request nests more than {MaxDepth} levels of objects and arrays.", InvalidRequest, id);
            if (paramsKind is not (null or System.Text.Json.JsonTokenType.StartObject or System.Text.Json.JsonTokenType.StartArray or System.Text.Json.JsonTokenType.Null))
                return ("A request's params must be an object.", InvalidRequest, id);
            return (null, 0, id);
        }
        catch (System.Text.Json.JsonException)
        {
            return ("The message is not valid JSON.", ParseError, id);
        }
    }

    /// <summary>{"jsonrpc":"2.0","id":&lt;the caller's&gt;,"method":"rpc.malformed","params":{"code":…,"message":…}}; no id: a notification, answered by no one.</summary>
    static byte[] Substitute(byte[]? id, int code, string message)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            if (id != null)
            {
                w.WritePropertyName("id");
                w.WriteRawValue(id, skipInputValidation: false);
            }
            w.WriteString("method", MalformedMethod);
            w.WriteStartObject("params");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
