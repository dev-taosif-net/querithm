namespace Querithm.Providers.Postgres;

/// <summary>
/// PostgreSQL wait_event_type / wait_event → the monitor's categories (shared with SQL Server for one UI), and the
/// benign idle waits excluded from the waits pane. Names are server-returned identifiers, never SQL text. Spellings
/// differ by version (PG 12 "WALWriteLock" / "wal_insert" / "Hash/Batch/…", PG 17 "WalWrite" / "BgworkerStartup"),
/// so prefixes are compared case-insensitively.
/// </summary>
public static class PostgresWaits
{
    public const string Cpu = "CPU", Lock = "Lock", Latch = "Latch", BufferIo = "Buffer IO", LogIo = "Log IO", Network = "Network",
        Memory = "Memory", Parallelism = "Parallelism", Other = "Other";

    /// <summary>"Type:Event"; pg_wait_sampling's NULL event (sampled on CPU) is "CPU".</summary>
    public static string Name(string? type, string? ev) => type == null ? "CPU" : ev == null ? type : $"{type}:{ev}";

    /// <summary>Background main loops (Activity) and idle client connections: they say nothing about user work.</summary>
    public static bool IsBenign(string? type, string? ev) => type == "Activity" || (type == "Client" && ev is "ClientRead");

    static bool Starts(string? ev, string prefix) => ev != null && ev.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    static bool IsWal(string? ev) => Starts(ev, "WAL") || Starts(ev, "wal_");

    public static string Category(string? type, string? ev) => type switch
    {
        null => Cpu,
        "Lock" => Lock,
        "LWLock" or "BufferPin" => type == "LWLock" && IsWal(ev) ? LogIo : Latch,
        "IO" => IsWal(ev) || Starts(ev, "LogicalRewrite") ? LogIo : BufferIo,
        "Client" => Network,
        "IPC" => Starts(ev, "Parallel") || Starts(ev, "ExecuteGather") || Starts(ev, "Bgworker") || Starts(ev, "Hash")
                 || ev is "MessageQueueReceive" or "MessageQueueSend" ? Parallelism : Other,
        _ => Other,
    };

    /// <summary>The (type, event) of a "Type:Event" wait type as stored in rows and counters.</summary>
    public static (string? Type, string? Event) Split(string waitType)
    {
        if (waitType == "CPU") return (null, null);
        var i = waitType.IndexOf(':');
        return i < 0 ? (waitType, null) : (waitType[..i], waitType[(i + 1)..]);
    }
}
