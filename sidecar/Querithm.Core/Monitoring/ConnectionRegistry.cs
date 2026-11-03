using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Querithm.Contract;
using Querithm.Core.Sessions;

namespace Querithm.Core.Monitoring;

/// <summary>A registry hit at a given moment: which kind of Querithm connection a server session is.</summary>
public sealed record RegistryHit(string Kind, string? LeasedBy, string? Purpose, bool Idle);

/// <summary>
/// SessionKey → Querithm's own physical connection (B1b), filled on every physical open before the connection is
/// handed to anyone. Read at kill time (never from a snapshot) so a connection registered after the dialog opened is
/// still refused. Holds weak references only; entries of closed or collected connections are pruned on lookup and sweep.
/// </summary>
public sealed class ConnectionRegistry
{
    /// <summary>pool | dedicated | monitor | control</summary>
    public static class Kinds
    {
        public const string Pool = "pool";
        public const string Dedicated = "dedicated";
        public const string Monitor = "monitor";
        public const string Control = "control";
    }

    sealed class Entry(DbConnection connection, string kind, string token, Func<DbConnection, string>? currentToken)
    {
        public WeakReference<DbConnection> Connection { get; } = new(connection);
        public string Kind { get; set; } = kind;
        public string Token { get; } = token;
        /// <summary>The provider's token reader (no I/O): a different value means the driver reconnected underneath.</summary>
        public Func<DbConnection, string>? CurrentToken { get; } = currentToken;
        public WeakReference<PhysicalConnection>? Physical { get; set; }
    }

    sealed class KeyBox(SessionKey key)
    {
        public SessionKey Key { get; set; } = key;
    }

    /// <summary>A connection whose server refused the identity read: it serves queries, but cannot be recognised.</summary>
    sealed class Unidentified(string kind, string token, string reason)
    {
        public string Kind { get; } = kind;
        public string Token { get; } = token;
        public string Reason { get; } = reason;
    }

    readonly ConcurrentDictionary<SessionKey, Entry> _byKey = new();
    readonly ConditionalWeakTable<DbConnection, KeyBox> _keyOf = new();
    readonly ConditionalWeakTable<DbConnection, Unidentified> _unidentified = new();

    public int Count => _byKey.Count;

    /// <summary>Records the identity read right after open (or after the driver reconnected underneath).</summary>
    public void Register(DbConnection connection, SessionKey key, string kind, string token, Func<DbConnection, string>? currentToken = null)
    {
        _unidentified.Remove(connection);
        if (_keyOf.TryGetValue(connection, out var old))
        {
            // A reopen on the same object: the old key maps to nothing from now on (O17).
            if (_byKey.TryGetValue(old.Key, out var prev) && prev.Connection.TryGetTarget(out var c) && ReferenceEquals(c, connection))
            {
                _byKey.TryRemove(old.Key, out _);
                kind = prev.Kind;
                var entry = new Entry(connection, kind, token, currentToken ?? prev.CurrentToken) { Physical = prev.Physical };
                _byKey[key] = entry;
                old.Key = key;
                return;
            }
            old.Key = key;
        }
        else _keyOf.AddOrUpdate(connection, new KeyBox(key));
        _byKey[key] = new Entry(connection, kind, token, currentToken);
    }

    /// <summary>
    /// Records a connection whose server refused the identity read on a working connection (IdentityUnavailableException):
    /// it serves queries, but its server session cannot be told from a foreign one, so <see cref="UnidentifiedReason"/>
    /// refuses every kill while it is open (ARCHITECTURE §6 rule 7).
    /// </summary>
    public void RegisterUnidentified(DbConnection connection, string kind, string token, string reason)
    {
        // A reopen whose new identity cannot be read: the old key maps to nothing from now on (O17).
        if (_keyOf.TryGetValue(connection, out var old))
        {
            if (_byKey.TryGetValue(old.Key, out var prev) && prev.Connection.TryGetTarget(out var c) && ReferenceEquals(c, connection))
                _byKey.TryRemove(old.Key, out _);
            _keyOf.Remove(connection);
        }
        _unidentified.AddOrUpdate(connection, new Unidentified(kind, token, reason));
    }

    /// <summary>Why kills are refused right now: the reason of an open connection the server would not identify; null when none. No I/O.</summary>
    public string? UnidentifiedReason()
    {
        foreach (var (c, u) in _unidentified)
            if (!IsGone(c)) return u.Reason;
        return null;
    }

    public bool IsUnidentified(DbConnection connection) => _unidentified.TryGetValue(connection, out _) && !IsGone(connection);

    /// <summary>The monitor labels its own connections monitor / control.</summary>
    public void Relabel(DbConnection connection, string kind)
    {
        if (_keyOf.TryGetValue(connection, out var box) && _byKey.TryGetValue(box.Key, out var e)) e.Kind = kind;
    }

    /// <summary>Links the pool's wrapper so a snapshot can show who leases the connection.</summary>
    public void Attach(PhysicalConnection physical)
    {
        if (_keyOf.TryGetValue(physical.Db, out var box) && _byKey.TryGetValue(box.Key, out var e)) e.Physical = new WeakReference<PhysicalConnection>(physical);
    }

    public SessionKey? KeyOf(DbConnection connection) => _keyOf.TryGetValue(connection, out var box) ? box.Key : null;

    /// <summary>The token recorded with the connection's current identity, or with its refused read (null when never registered).</summary>
    public string? TokenOf(DbConnection connection) =>
        _keyOf.TryGetValue(connection, out var box) && _byKey.TryGetValue(box.Key, out var e) ? e.Token
        : _unidentified.TryGetValue(connection, out var u) ? u.Token : null;

    /// <summary>Is this server session one of Querithm's connections right now? Prunes the entry when its connection is gone.</summary>
    public RegistryHit? Find(SessionKey key)
    {
        if (!_byKey.TryGetValue(key, out var e)) return null;
        if (!e.Connection.TryGetTarget(out var c) || IsGone(c))
        {
            _byKey.TryRemove(new KeyValuePair<SessionKey, Entry>(key, e));
            return null;
        }
        PhysicalConnection? p = null;
        e.Physical?.TryGetTarget(out p);
        if (p?.State == ConnLifecycle.Discarded)
        {
            _byKey.TryRemove(new KeyValuePair<SessionKey, Entry>(key, e));
            return null;
        }
        return new RegistryHit(e.Kind, p?.LeasedBy, p?.Purpose, p?.State == ConnLifecycle.Idle);
    }

    /// <summary>
    /// True while one of Querithm's live connections was reconnected by its driver (connection resiliency) and its new
    /// server identity is not recorded yet: that server session would look foreign to the monitor. No I/O.
    /// </summary>
    public bool HasReconnected()
    {
        foreach (var (_, e) in _byKey)
        {
            if (e.CurrentToken is not { } read || !e.Connection.TryGetTarget(out var c) || IsGone(c)) continue;
            string now;
            try { now = read(c); } catch (InvalidOperationException) { continue; } // closing right now
            if (now != e.Token) return true;
        }
        return false;
    }

    /// <summary>Removes entries whose connection closed or was collected (pool idle sweep, monitor ticks).</summary>
    public void Prune()
    {
        foreach (var (key, e) in _byKey)
        {
            PhysicalConnection? p = null;
            e.Physical?.TryGetTarget(out p);
            if (!e.Connection.TryGetTarget(out var c) || IsGone(c) || p?.State == ConnLifecycle.Discarded)
                _byKey.TryRemove(new KeyValuePair<SessionKey, Entry>(key, e));
        }
    }

    static bool IsGone(DbConnection c) => c.State is ConnectionState.Closed or ConnectionState.Broken;
}
