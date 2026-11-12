using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Monitoring;

/// <summary>Rollback-risk thresholds (B1c). Fixed constants with tests; changing them is a deliberate change.</summary>
public static class KillRisk
{
    public const long HighLogBytes = 100L * 1024 * 1024;
    public const long HighTxMs = 10 * 60_000;

    /// <summary>
    /// none = no open transaction; high = ≥ 100 MB of log or ≥ 10 min old; low otherwise (the plan's 1–10 min gap counts
    /// as low); unknown = the provider could not read it (treated as high for confirmation — P6). A PostgreSQL
    /// transaction without an xid wrote nothing, so it is low whatever its age (P8).
    /// </summary>
    public static RollbackRisk Classify(KillPreview p)
    {
        if (p.SessionEnded) return RollbackRisk.None; // nothing left to roll back (K5)
        if (p.Risk == RollbackRisk.Unknown) return RollbackRisk.Unknown;
        var inTx = (p.TxCount ?? 0) > 0 || p.TxElapsedMs != null;
        if (!inTx) return RollbackRisk.None;
        if (p.HasWrites == false) return RollbackRisk.Low;
        if ((p.LogBytesUsed ?? 0) >= HighLogBytes || (p.TxElapsedMs ?? 0) >= HighTxMs) return RollbackRisk.High;
        return RollbackRisk.Low;
    }

    public static bool NeedsLongRollbackConfirm(RollbackRisk r) => r is RollbackRisk.High or RollbackRisk.Unknown;
}

/// <summary>
/// The sidecar-enforced kill guard (B1c steps 1–6), evaluated without I/O before anything is sent. Step 7 (rollback
/// risk, recomputed on the server at kill time) runs in the monitor right after, and the identity is re-checked on the
/// server in the kill statement itself.
/// </summary>
public static class KillGuard
{
    public sealed record Context(
        MonitorKillParams Params,
        EnvironmentKind Environment,
        bool Connected,
        IReadOnlyDictionary<SessionKey, ActivityRow>? Latest,
        SessionKey? Self,
        ConnectionRegistry Registry,
        MonitorCapabilities? Capabilities,
        IReadOnlySet<SessionKey> RollingBack);

    static KillResult Refuse(string code, string message, GuardReason? guard = null) =>
        new() { Outcome = KillOutcome.Refused, Code = code, Message = message, Guard = guard };

    public const string QuerithmConnectionMessage = "This is a Querithm connection — cancel or disconnect it from its tab.";

    /// <summary>Null = allowed so far; else the refusal (nothing is sent).</summary>
    public static KillResult? Check(Context c)
    {
        var p = c.Params;
        var target = p.Target;
        if (!c.Connected) return Refuse("disconnected", "The monitor is disconnected; nothing was sent. Try again once it reconnects.");
        if (!p.ConfirmedKill) return Refuse("notConfirmed", "Confirm the kill first.");
        if (c.Environment == EnvironmentKind.Prod && !p.ConfirmedProd)
            return Refuse("prod", "This is a production server: confirm the kill for production.", GuardReason.ProdWrite);
        if (target.Id < 0) return Refuse("notASession", "Not a session: SQL Server reports a transaction or latch, not a session, as the blocker.");
        if (string.IsNullOrEmpty(target.Instance)) return Refuse("identityHidden", "The session's start time is hidden, so its identity cannot be re-checked on the server.");
        if (c.Latest == null || !c.Latest.TryGetValue(target, out var row))
            return Refuse("notInSnapshot", "The session ended or its ID was reused; nothing was sent.");
        if (c.Self is { } self && self == target) return Refuse("ownMonitor", "This is the monitor's own connection.");
        if (row.IdentityHidden != null) return Refuse("identityHidden", $"The session's identity is hidden ({row.IdentityHidden}); it cannot be cancelled or terminated from here.");
        if (!row.IsUserSession) return Refuse("systemSession", "This is a system session; it cannot be cancelled or terminated.");
        // Read at kill time, never from the snapshot: a connection registered after the dialog opened is still refused (K8).
        if (c.Registry.Find(target) is { } hit)
            return Refuse("querithmConnection", hit.Kind is ConnectionRegistry.Kinds.Monitor ? "This is the monitor's own connection." : QuerithmConnectionMessage);
        if (c.RollingBack.Contains(target) || row.RollingBack)
            return Refuse("rollingBack", "The session is already rolling back; another kill would change nothing. Its progress is shown in the grid.");
        if (p.Mode == KillMode.CancelRequest)
        {
            if (c.Capabilities is { CanCancelRequest: false } caps) return Refuse("unsupported", caps.CancelReason ?? "Cancelling a single request is not supported.");
            if (!IsRunning(row)) return new KillResult { Outcome = KillOutcome.NoRunningQuery, Code = "noRunningQuery", Message = "The session is not running a query; nothing was sent." };
            if (p.ExpectedStatementStart != row.StatementStart)
                return new KillResult { Outcome = KillOutcome.StatementFinished, Code = "statementFinished", Message = "The statement already finished (a newer one is running); nothing was sent." };
        }
        else if (c.Capabilities is { CanTerminate: false } caps)
            return Refuse("unsupported", caps.TerminateReason ?? "Terminating sessions is not allowed for this login.");
        if (c.Capabilities?.OtherLoginsReason is { } other && row.Login != null && c.Capabilities.Login != null && row.Login != c.Capabilities.Login)
            return Refuse("permission", other);
        return null;
    }

    static bool IsRunning(ActivityRow row) => row.StatementStart != null || row.Status is "active" or "running" or "runnable" or "suspended";

    /// <summary>
    /// RPC boundary (K12): ids, identity and statement start are validated before any SQL is built.
    /// Returns the rejection message, or null.
    /// </summary>
    public static string? ValidateShape(MonitorKillParams p, IActivityProvider activity, Engine engine)
    {
        if (p.Target == null) return "A kill needs a target session.";
        if (p.Target.Id < 0) return null; // refused later with "not a session" (K14)
        var rules = MonitorRules.For(engine);
        var max = rules.MaxSessionId;
        if (p.Target.Id < 1 || p.Target.Id > max) return $"Session id must be 1–{max}.";
        if (p.Target.Instance == null || p.Target.Instance.Length > 64) return "Invalid session identity.";
        if (p.Target.Instance.Length > 0 && !activity.IsValidInstance(p.Target.Instance)) return "Invalid session identity.";
        if (p.ExpectedStatementStart != null && (p.ExpectedStatementStart.Length is 0 or > 64 || !activity.IsValidStatementStart(p.ExpectedStatementStart)))
            return "Invalid statement start.";
        if (p.Mode == KillMode.CancelRequest && rules.CancelNeedsStatementStart && p.ExpectedStatementStart == null)
            return $"Cancelling on {Sessions.ProfileRules.EngineName(engine)} needs the statement start the dialog showed.";
        if (!Enum.IsDefined(p.Mode)) return "Unknown kill mode.";
        return null;
    }
}
