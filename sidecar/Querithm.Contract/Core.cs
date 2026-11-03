namespace Querithm.Contract;

/// <summary>Contract version; system.ping returns it and the webview refuses to run on a mismatch (§5).</summary>
public static class ContractInfo
{
    public const string Version = "1.18.0";
}

/// <summary>Members are appended as providers land (Phase D); existing spellings never change ("mssql", "pg").</summary>
public enum Engine { Mssql, Pg, Sqlite, MySql, DuckDb }

/// <summary>Every engine has a dialect of its own (Phase D0a): language calls route by dialect to exactly one provider.</summary>
public enum Dialect { Tsql, Pg, Sqlite, MySql, DuckDb }

public enum EnvironmentKind { Dev, Staging, Prod, Local, Test }

/// <summary>
/// How a profile signs in. <see cref="None"/> is for engines that take no credentials (file and memory databases): it is
/// never a fallback, so <see cref="Sql"/> stays the enum default (Phase D0a).
/// </summary>
public enum AuthKind { Sql, Windows, Kerberos, EntraInteractive, EntraDeviceCode, EntraToken, Password, None }

/// <summary>
/// Where a profile's database lives (Phase D0a). Server: <see cref="Profile.Host"/>/<see cref="Profile.Port"/> with every
/// encoding the driver understands (PostgreSQL socket directories and failover lists, SQL Server tcp:/np:/lpc:/instance).
/// File: a database file reached only through a shell-granted <see cref="Profile.File"/>. Memory: a database that lives
/// as long as its session.
/// </summary>
public enum LocationKind { Server, File, Memory }

/// <summary>How the SSH tunnel signs in to the bastion. Agent is listed so profiles can say so; it is refused with a reason.</summary>
public enum SshAuthKind { Password, PrivateKey, Agent }

/// <summary>Mssql: Strict | Mandatory | Optional. Pg: Require | VerifyCa | VerifyFull | Prefer | Disable.</summary>
public enum TlsMode { Default, Strict, Mandatory, Optional, Require, VerifyCa, VerifyFull, Prefer, Disable }

public enum TxState { None, Open, Unknown }

public enum ExecState { Queued, Running, Streaming, Done, Failed, Cancelled }

public enum Severity { Info, Warning, Error }

/// <summary>Trigger: a DML trigger on a table or view (its parent is <see cref="SnapObject.ParentObjectId"/>).</summary>
public enum ObjectKind { Table, View, Proc, Func, Tvf, Type, Sequence, Synonym, Trigger }

public enum SnapshotStatus { Empty, Fresh, Stale, Refreshing, Error }

public sealed record Empty;

public sealed record PingResult
{
    public required string Version { get; init; }
    public int Pid { get; init; }
    public long UptimeMs { get; init; }
    public long WorkingSetBytes { get; init; }
}

/// <summary>
/// Something the sidecar met that the user must hear about once (M22): <c>storeReset</c> — store.db was damaged and set
/// aside as <see cref="File"/> (a name in the data folder, never a path), a fresh store was made; <c>storeRefused</c> — a
/// store this build must not open was left as it is (8a review); <c>historyPruned</c> — history retention deleted entries
/// for the first time (announced with system.noticeAdded, since it runs after start).
/// </summary>
public sealed record SystemNotice
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? File { get; init; }
}

/// <summary>
/// A webview render error for the sidecar log (M17). Only what locates the fault: the error's type and message, the
/// script stack and React's component stack, the panel it hit. Never cell values or SQL text; the sidecar caps every field
/// and scrubs the line like any other.
/// </summary>
public sealed record ErrorReport
{
    /// <summary>Where it was caught: <c>root</c> or a panel name ("Results", "Query tab", …).</summary>
    public required string Where { get; init; }
    public string? Name { get; init; }
    public string? Message { get; init; }
    public string? Stack { get; init; }
    public string? ComponentStack { get; init; }
}

public sealed record LogTailParams
{
    /// <summary>How many of the newest log lines (1–500, default 200).</summary>
    public int? Lines { get; init; }
}

/// <summary>Settings → About "Copy diagnostics" (M21): the newest lines of Querithm's own logs, scrubbed again on read.</summary>
public sealed record LogTailResult
{
    public required string LogDir { get; init; }
    public required string[] Lines { get; init; }
}

/// <summary>
/// An engine this build knows (Phase D0g): what the profile editor offers and its defaults, as data. A hidden engine's
/// provider ships behind the dev setting (Phase D merge model): it is not offered, not used to read connection strings, and
/// its profiles do not open until the setting exposes it.
/// </summary>
public sealed record EngineInfo
{
    public required Engine Engine { get; init; }
    public required Dialect Dialect { get; init; }
    public required string Name { get; init; }
    public bool Exposed { get; init; }
    public int? DefaultPort { get; init; }
    public string? DefaultDatabase { get; init; }
    public string? DefaultSchema { get; init; }
    public AuthKind DefaultAuth { get; init; }
    public required AuthKind[] AuthKinds { get; init; }
    public required LocationKind[] Locations { get; init; }
}

public sealed record SettingsGetParams
{
    public string[]? Keys { get; init; }
}

public sealed record SettingEntry
{
    public required string Key { get; init; }
    /// <summary>JSON-encoded value.</summary>
    public required string ValueJson { get; init; }
}

public sealed record SyntheticParams
{
    public int? Rows { get; init; }
    public int? Columns { get; init; }
}
