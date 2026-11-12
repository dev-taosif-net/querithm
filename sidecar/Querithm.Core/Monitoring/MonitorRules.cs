using Querithm.Contract;

namespace Querithm.Core.Monitoring;

/// <summary>
/// The activity monitor's engine rules (Phase D0e) in one exhaustive table instead of SQL Server / else-PostgreSQL
/// branches: an engine that gains an activity provider must state its own rules here, never inherit one of these.
/// </summary>
public sealed record MonitorRules(
    /// <summary>Largest session id (SQL Server spids fit a smallint).</summary>
    int MaxSessionId,
    /// <summary>Cancelling a request needs the statement start the dialog showed (PostgreSQL cancels whatever runs now).</summary>
    bool CancelNeedsStatementStart,
    /// <summary>A wait type with this prefix is a lock wait when the dictionary does not know it.</summary>
    string LockWaitPrefix,
    /// <summary>Waits come from sampling each session's state per tick rather than server-wide counters.</summary>
    bool WaitsSampledPerTick,
    /// <summary>Lock object ids are resolved to names by a later catalog read.</summary>
    bool ResolvesLockObjectNames,
    /// <summary>A terminated session can linger in the session list (rolling back) and is remembered as terminated.</summary>
    bool RemembersTerminated,
    /// <summary>Wait types read "Class:Event" (PostgreSQL wait_event_type:wait_event), so the class drives the diagnosis.</summary>
    bool WaitTypeHasClass)
{
    public static MonitorRules For(Engine engine) => engine switch
    {
        Engine.Mssql => new(MaxSessionId: 32_767, CancelNeedsStatementStart: false, LockWaitPrefix: "LCK_M_", WaitsSampledPerTick: false,
            ResolvesLockObjectNames: true, RemembersTerminated: true, WaitTypeHasClass: false),
        Engine.Pg => new(MaxSessionId: int.MaxValue, CancelNeedsStatementStart: true, LockWaitPrefix: "Lock:", WaitsSampledPerTick: true,
            ResolvesLockObjectNames: false, RemembersTerminated: false, WaitTypeHasClass: true),
        // MySQL / MariaDB: connection ids are 32-bit; KILL QUERY stops whatever runs now, so a cancel names the statement
        // (performance_schema EVENT_ID); InnoDB row waits are "wait/lock/…" instruments.
        Engine.MySql => new(MaxSessionId: int.MaxValue, CancelNeedsStatementStart: true, LockWaitPrefix: "wait/lock/", WaitsSampledPerTick: false,
            ResolvesLockObjectNames: false, RemembersTerminated: false, WaitTypeHasClass: false),
        // Oracle (D4 S12): SIDs are positive NUMBERs; ALTER SYSTEM CANCEL SQL stops the named SQL_ID of whatever execution runs
        // now, so a cancel names the execution (SQL_ID:SQL_EXEC_ID); row and table locks are "enq: …" events; a killed session
        // stays in V$SESSION as KILLED, which its row reports itself.
        Engine.Oracle => new(MaxSessionId: int.MaxValue, CancelNeedsStatementStart: true, LockWaitPrefix: "enq: ", WaitsSampledPerTick: false,
            ResolvesLockObjectNames: false, RemembersTerminated: false, WaitTypeHasClass: false),
        _ => throw new QuerithmException("unsupported", $"The activity monitor has no rules for {Sessions.ProfileRules.EngineName(engine)}."),
    };
}
