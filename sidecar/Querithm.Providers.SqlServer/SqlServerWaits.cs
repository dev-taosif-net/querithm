namespace Querithm.Providers.SqlServer;

/// <summary>
/// Wait-type categories and the fixed list of benign (idle / background) waits excluded from the waits pane (B3).
/// Names are server-returned identifiers compared exactly or by prefix, never SQL text.
/// </summary>
public static class SqlServerWaits
{
    public const string Cpu = "CPU", Lock = "Lock", Latch = "Latch", BufferIo = "Buffer IO", LogIo = "Log IO", Network = "Network",
        Memory = "Memory", Parallelism = "Parallelism", Other = "Other";

    public static readonly string[] Categories = [Cpu, Lock, Latch, BufferIo, LogIo, Network, Memory, Parallelism, Other];

    /// <summary>Idle and background waits: they accumulate on an idle server and say nothing about user work.</summary>
    public static readonly IReadOnlySet<string> Benign = new HashSet<string>(StringComparer.Ordinal)
    {
        "BROKER_EVENTHANDLER", "BROKER_RECEIVE_WAITFOR", "BROKER_TASK_STOP", "BROKER_TO_FLUSH", "BROKER_TRANSMITTER",
        "CHECKPOINT_QUEUE", "CHKPT", "CLR_AUTO_EVENT", "CLR_MANUAL_EVENT", "CLR_SEMAPHORE",
        "DBMIRROR_DBM_EVENT", "DBMIRROR_EVENTS_QUEUE", "DBMIRROR_WORKER_QUEUE", "DBMIRRORING_CMD",
        "DIRTY_PAGE_POLL", "DISPATCHER_QUEUE_SEMAPHORE", "EXECSYNC", "FSAGENT", "FT_IFTS_SCHEDULER_IDLE_WAIT", "FT_IFTSHC_MUTEX",
        "HADR_CLUSAPI_CALL", "HADR_FILESTREAM_IOMGR_IOCOMPLETION", "HADR_LOGCAPTURE_WAIT", "HADR_NOTIFICATION_DEQUEUE",
        "HADR_TIMER_TASK", "HADR_WORK_QUEUE", "KSOURCE_WAKEUP", "LAZYWRITER_SLEEP", "LOGMGR_QUEUE",
        "MEMORY_ALLOCATION_EXT", "ONDEMAND_TASK_QUEUE",
        "PARALLEL_REDO_DRAIN_WORKER", "PARALLEL_REDO_LOG_CACHE", "PARALLEL_REDO_TRAN_LIST", "PARALLEL_REDO_WORKER_SYNC",
        "PARALLEL_REDO_WORKER_WAIT_WORK", "PREEMPTIVE_OS_FLUSHFILEBUFFERS", "PREEMPTIVE_XE_GETTARGETSTATE",
        "PVS_PREALLOCATE", "PWAIT_ALL_COMPONENTS_INITIALIZED", "PWAIT_DIRECTLOGCONSUMER_GETNEXT", "PWAIT_EXTENSIBILITY_CLEANUP_TASK",
        "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP", "QDS_ASYNC_QUEUE", "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP", "QDS_SHUTDOWN_QUEUE",
        "REDO_THREAD_PENDING_WORK", "REQUEST_FOR_DEADLOCK_SEARCH", "RESOURCE_QUEUE", "SERVER_IDLE_CHECK",
        "SLEEP_BPOOL_FLUSH", "SLEEP_DBSTARTUP", "SLEEP_DCOMSTARTUP", "SLEEP_MASTERDBREADY", "SLEEP_MASTERMDREADY",
        "SLEEP_MASTERUPGRADED", "SLEEP_MSDBSTARTUP", "SLEEP_SYSTEMTASK", "SLEEP_TASK", "SLEEP_TEMPDBSTARTUP",
        "SNI_HTTP_ACCEPT", "SOS_WORK_DISPATCHER", "SP_SERVER_DIAGNOSTICS_SLEEP", "SQLTRACE_BUFFER_FLUSH",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES", "STARTUP_DEPENDENCY_MANAGER", "VDI_CLIENT_OTHER",
        "WAIT_FOR_RESULTS", "WAITFOR", "WAITFOR_TASKSHUTDOWN", "WAIT_XTP_RECOVERY", "WAIT_XTP_HOST_WAIT",
        "WAIT_XTP_OFFLINE_CKPT_NEW_LOG", "WAIT_XTP_CKPT_CLOSE", "XE_DISPATCHER_JOIN", "XE_DISPATCHER_WAIT",
        "XE_TIMER_EVENT", "XE_LIVE_TARGET_TVF",
    };

    /// <summary>Waits the dictionary must explain beyond the prefix families (W8 checks coverage).</summary>
    public static readonly string[] Notable =
    [
        "SOS_SCHEDULER_YIELD", "THREADPOOL", "CXPACKET", "CXCONSUMER", "CXSYNC_PORT", "CXSYNC_CONSUMER",
        "WRITELOG", "LOGBUFFER", "LOG_RATE_GOVERNOR", "HADR_SYNC_COMMIT", "ASYNC_NETWORK_IO", "NET_WAITFOR_PACKET",
        "RESOURCE_SEMAPHORE", "RESOURCE_SEMAPHORE_QUERY_COMPILE", "CMEMTHREAD", "IO_COMPLETION", "ASYNC_IO_COMPLETION",
        "PAGEIOLATCH_SH", "PAGEIOLATCH_EX", "PAGEIOLATCH_UP", "PAGELATCH_SH", "PAGELATCH_EX", "PAGELATCH_UP",
        "LATCH_EX", "LATCH_SH", "LCK_M_S", "LCK_M_X", "LCK_M_U", "LCK_M_IS", "LCK_M_IX", "LCK_M_SCH_S", "LCK_M_SCH_M",
        "LCK_M_RS_S", "LCK_M_RX_X", "OLEDB", "PREEMPTIVE_OS_WRITEFILEGATHER", "BACKUPIO", "BACKUPBUFFER", "DTC",
        "SOS_WORKER_MIGRATION", "HADR_SYNCHRONIZING_THROTTLE",
    ];

    public static string Category(string waitType)
    {
        if (waitType.StartsWith("LCK_M_", StringComparison.Ordinal)) return Lock;
        if (waitType.StartsWith("PAGEIOLATCH_", StringComparison.Ordinal) || waitType is "IO_COMPLETION" or "ASYNC_IO_COMPLETION" or "BACKUPIO") return BufferIo;
        if (waitType.StartsWith("PAGELATCH_", StringComparison.Ordinal) || waitType.StartsWith("LATCH_", StringComparison.Ordinal)) return Latch;
        if (waitType is "WRITELOG" or "LOGBUFFER" or "LOG_RATE_GOVERNOR" or "HADR_SYNC_COMMIT" or "HADR_SYNCHRONIZING_THROTTLE") return LogIo;
        if (waitType is "ASYNC_NETWORK_IO" or "NET_WAITFOR_PACKET" or "OLEDB") return Network;
        if (waitType is "RESOURCE_SEMAPHORE" or "RESOURCE_SEMAPHORE_QUERY_COMPILE" or "CMEMTHREAD") return Memory;
        if (waitType.StartsWith("CX", StringComparison.Ordinal) || waitType == "EXCHANGE") return Parallelism;
        if (waitType is "SOS_SCHEDULER_YIELD" or "THREADPOOL" or "SOS_WORKER_MIGRATION") return Cpu;
        return Other;
    }
}
