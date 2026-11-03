namespace Querithm.Contract;

public sealed record SessionOpenParams
{
    public required string ProfileId { get; init; }
    public string? Database { get; init; }
    /// <summary>Injected by Rust from the keyring (§8 Secrets). Never logged.</summary>
    public string? Secret { get; init; }
    /// <summary>SSH password or key passphrase, injected by Rust from the keyring ("&lt;profileId&gt;:ssh"). Never logged.</summary>
    public string? SshSecret { get; init; }
    /// <summary>A file profile's grant, injected by the shell from its registry (Phase D0c); one the webview sent is dropped by the shell.</summary>
    public FileGrant? FileGrant { get; init; }
}

/// <summary>How a granted database file may be opened (Phase D0c): read-only unless the user allowed writes in the shell's own dialog.</summary>
public enum FileAccessMode { ReadOnly, ReadWrite }

/// <summary>
/// A database file the shell lets the sidecar open (Phase D0c, §5 injection list): the path the shell's dialog picked
/// (canonical), the access mode and the file's identity at pick time. Injected into session.open / profile.test only;
/// the path stays in sidecar memory, is never stored or logged, and is re-checked against the identity around every open.
/// </summary>
public sealed record FileGrant
{
    public required string GrantId { get; init; }
    public required string Path { get; init; }
    public FileAccessMode Mode { get; init; }
    /// <summary>"u:&lt;dev&gt;:&lt;ino&gt;" (Unix) or "w:&lt;volume serial&gt;:&lt;file index&gt;" (Windows).</summary>
    public required string Identity { get; init; }
}

public sealed record GrantRevokeParams
{
    public required string ProfileId { get; init; }
}

public sealed record GrantRevokeResult
{
    /// <summary>Sessions of the profile that were closed.</summary>
    public int ClosedSessions { get; init; }
}

public sealed record SessionOpenResult
{
    public required string SessionId { get; init; }
    public string? Database { get; init; }
    /// <summary>Null until the session first connects (session.open is lazy).</summary>
    public string? ServerVersion { get; init; }
}

public sealed record SessionRef
{
    public required string SessionId { get; init; }
}

public sealed record SessionCloseParams
{
    public required string SessionId { get; init; }
    public bool Force { get; init; }
}

public sealed record ChangeDatabaseParams
{
    public required string SessionId { get; init; }
    public required string Database { get; init; }
}

public sealed record ChangeDatabaseResult
{
    public required string Database { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed record SessionState
{
    public required string SessionId { get; init; }
    public required string ProfileId { get; init; }
    public string? Database { get; init; }
    public TxState TxState { get; init; }
    public long? TxStartedAt { get; init; }
    public bool Busy { get; init; }
    public int? Spid { get; init; }
    public bool Connected { get; init; }
    public bool Pinned { get; init; }
    public string? ServerVersion { get; init; }
    /// <summary>The product the session is connected to, as detected (null until it connects).</summary>
    public ProductInfo? Product { get; init; }
    public string? ActiveExecutionId { get; init; }
}

/// <summary>
/// The database product as detected on a connection (Phase D0a): engines that share a provider family (MySQL and MariaDB)
/// differ here, so product-specific behaviour is data, not inference from the engine.
/// </summary>
public sealed record ProductInfo
{
    /// <summary>"SQL Server", "Azure SQL Database", "PostgreSQL", ….</summary>
    public required string Name { get; init; }
    /// <summary>The server version string as the driver reports it.</summary>
    public string? Version { get; init; }
}

/// <summary>How an engine groups objects inside a database (Phase D0a). Explorer namespaces follow this, never whether the engine has CREATE SCHEMA.</summary>
public enum NamespaceModel
{
    Schemas,
    /// <summary>SQLite: the attached databases of one connection ("main", "temp") act as schemas.</summary>
    Attached,
    /// <summary>MySQL / MariaDB: a database is the schema (one level below the server).</summary>
    Databases,
}

public sealed record CapabilityInfo
{
    public bool Available { get; init; }
    public string? Reason { get; init; }

    public static CapabilityInfo Yes { get; } = new() { Available = true };
    public static CapabilityInfo No(string reason) => new() { Available = false, Reason = reason };
    /// <summary>The default of a feature entry nobody filled: unavailable, so a forgotten entry never advertises a feature.</summary>
    public static CapabilityInfo NotReported { get; } = new() { Available = false, Reason = "This connection did not report the feature." };
}

/// <summary>
/// What a session can do, with the reason for everything it cannot. Depends on the engine, product, server version,
/// permissions, profile access mode and session state; cached per (profile, database, credential epoch) in the sidecar.
/// The feature entries below Trace…ActualPlan are filled by Core from the provider's <c>FeatureRefusal</c> (Phase D0a), and
/// every RPC that needs one refuses with the same reason.
/// </summary>
public sealed record Capabilities
{
    public required CapabilityInfo Trace { get; init; }
    public required CapabilityInfo Debugger { get; init; }
    public required CapabilityInfo EstimatedPlan { get; init; }
    public required CapabilityInfo ActualPlan { get; init; }
    /// <summary>session.changeDatabase.</summary>
    public CapabilityInfo ChangeDatabase { get; init; } = CapabilityInfo.NotReported;
    /// <summary>tx.begin / commit / rollback.</summary>
    public CapabilityInfo Transactions { get; init; } = CapabilityInfo.NotReported;
    /// <summary>Grid edits written back (data.applyPatch).</summary>
    public CapabilityInfo DataEdit { get; init; } = CapabilityInfo.NotReported;
    public CapabilityInfo Import { get; init; } = CapabilityInfo.NotReported;
    /// <summary>Reading both sides and diffing.</summary>
    public CapabilityInfo SchemaCompare { get; init; } = CapabilityInfo.NotReported;
    /// <summary>A migration script for this engine.</summary>
    public CapabilityInfo SchemaCompareApply { get; init; } = CapabilityInfo.NotReported;
    public CapabilityInfo DataCompare { get; init; } = CapabilityInfo.NotReported;
    /// <summary>A sync script for this engine.</summary>
    public CapabilityInfo DataCompareApply { get; init; } = CapabilityInfo.NotReported;
    /// <summary>Find usages and rename.</summary>
    public CapabilityInfo References { get; init; } = CapabilityInfo.NotReported;
    /// <summary>The server activity monitor (Phase B).</summary>
    public CapabilityInfo Monitor { get; init; } = CapabilityInfo.NotReported;
    public NamespaceModel Namespaces { get; init; }
    public ProductInfo? Product { get; init; }
    public bool CreateOrAlter { get; init; }
    public bool EncryptStrict { get; init; }
    public int? MajorVersion { get; init; }
    public string? EngineEdition { get; init; }
    /// <summary>"tier1+tier2" or "tier1".</summary>
    public required string CompletionTier { get; init; }
}
