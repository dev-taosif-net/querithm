using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Core.Sessions;

/// <summary>One query tab's session: bound to (profile, database), borrows connections per execution (§6).</summary>
public sealed class Session(string id, Profile profile, IDbProvider provider, string? database)
{
    public string Id { get; } = id;
    public Profile Profile { get; } = profile;
    public IDbProvider Provider { get; } = provider;
    public string? Database { get; set; } = database;
    public TxState TxState { get; set; } = TxState.None;
    public long? TxStartedAt { get; set; }
    /// <summary>Held while a transaction is open or a Trace is running. Never shared.</summary>
    public PhysicalConnection? Pinned { get; set; }
    public bool TracePinned { get; set; }
    public bool Connected { get; set; }
    public string? ServerVersion { get; set; }
    public ProductInfo? Product { get; set; }
    public int? Spid { get; set; }
    public string? ActiveExecutionId { get; set; }
    public bool Closed { get; set; }

    /// <summary>
    /// Phase D0d: the session opens one connection of its own on first use and keeps it (pinned) until it closes — file
    /// and memory databases. Nothing else borrows it; helpers take <see cref="ExecLock"/> to use it.
    /// </summary>
    public bool SessionOwned { get; init; }
    /// <summary>An in-memory database: its data and objects exist only on this session's connection.</summary>
    public bool InMemory { get; init; }
    /// <summary>The in-memory database went with its connection; a fresh, empty one is never opened in its place.</summary>
    public bool MemoryLost { get; set; }
    /// <summary>
    /// A COMMIT on this session was stopped or lost its connection, so whether it committed is unknown: the next COMMIT that
    /// finds no transaction says so instead of reporting success. Cleared by BEGIN and by a COMMIT / ROLLBACK that ran.
    /// </summary>
    public bool CommitOutcomeUnknown { get; set; }
    /// <summary>A metadata refresh was asked for while this session's transaction was open: it runs once the transaction ends.</summary>
    public bool RefreshDeferred { get; set; }

    /// <summary>One execution at a time per session; later ones queue (§7).</summary>
    public SemaphoreSlim ExecLock { get; } = new(1, 1);
    /// <summary>Cancelled when the session starts closing: helpers waiting for or holding its lease stop (Phase D0d).</summary>
    internal CancellationTokenSource Closing { get; } = new();

    public SessionState State() => new()
    {
        SessionId = Id,
        ProfileId = Profile.Id,
        Database = Database,
        TxState = TxState,
        TxStartedAt = TxStartedAt,
        Busy = ActiveExecutionId != null,
        Spid = Spid,
        Connected = Connected,
        Pinned = Pinned != null,
        ServerVersion = ServerVersion,
        Product = Product,
        ActiveExecutionId = ActiveExecutionId,
    };
}
