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

    /// <summary>
    /// Read and written under the same lock as the deferred refresh (<see cref="DeferIfInTransaction"/>): a transaction
    /// that ends while DDL defers its read either sees the deferral or is seen by it (G#5, completion audit follow-up).
    /// </summary>
    public TxState TxState
    {
        get { lock (_deferredDatabases) return _txState; }
        set { lock (_deferredDatabases) _txState = value; }
    }

    TxState _txState = TxState.None;
    public long? TxStartedAt { get; set; }
    /// <summary>Held while a transaction is open or a Trace is running. Never shared.</summary>
    public PhysicalConnection? Pinned { get; set; }
    public bool TracePinned { get; set; }
    public bool Connected { get; set; }
    public string? ServerVersion { get; set; }
    public ProductInfo? Product { get; set; }
    public int? Spid { get; set; }
    public string? ActiveExecutionId { get; set; }
    /// <summary>Under the deferral lock like <see cref="TxState"/>: <see cref="DeferIfInTransaction"/> reads it there (Gemini review 2026-11-26).</summary>
    public bool Closed
    {
        get { lock (_deferredDatabases) return _closed; }
        set { lock (_deferredDatabases) _closed = value; }
    }

    bool _closed;
    /// <summary>
    /// Phase D4: server output (Oracle DBMS_OUTPUT) is read back after each batch. Set by the run option or a script's
    /// <c>SET SERVEROUTPUT</c>; applied to whichever connection the next run borrows.
    /// </summary>
    public bool ServerOutput { get; set; }

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
    public bool RefreshDeferred
    {
        get { lock (_deferredDatabases) return _refreshDeferred; }
    }

    bool _refreshDeferred;

    /// <summary>Also the lock of <see cref="TxState"/> and <see cref="RefreshDeferred"/>; nothing else is taken inside it.</summary>
    readonly HashSet<string> _deferredDatabases = new(StringComparer.Ordinal);

    /// <summary>
    /// Defers a metadata refresh of <paramref name="databases"/> (besides <see cref="Database"/>, read when the transaction
    /// ends) while a transaction is open, in one step with the transaction-state check: false when none is open (or the
    /// session closed) — the caller reads now. Before (G#5), the check, the deferral and the end's look at the flag were
    /// separate unlocked reads and writes: a COMMIT landing between them could leave the refresh deferred for good.
    /// </summary>
    public bool DeferIfInTransaction(IEnumerable<string> databases)
    {
        lock (_deferredDatabases)
        {
            if (_txState == TxState.None || _closed) return false;
            foreach (var db in databases) _deferredDatabases.Add(db);
            _refreshDeferred = true;
            return true;
        }
    }

    /// <summary>
    /// The deferred refresh, taken at once (the transaction ended): the databases it covers besides <see cref="Database"/>,
    /// or null when nothing was deferred.
    /// </summary>
    public string[]? TakeDeferral()
    {
        lock (_deferredDatabases)
        {
            if (!_refreshDeferred) return null;
            _refreshDeferred = false;
            var list = _deferredDatabases.ToArray();
            _deferredDatabases.Clear();
            return list;
        }
    }

    /// <summary>A refresh of <paramref name="database"/> is deferred until this session's transaction ends (G#6: after a USE, not only its current database).</summary>
    public bool DefersRefreshOf(string database)
    {
        lock (_deferredDatabases) return _refreshDeferred && (_deferredDatabases.Contains(database) || string.Equals(Database, database, StringComparison.Ordinal));
    }

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
