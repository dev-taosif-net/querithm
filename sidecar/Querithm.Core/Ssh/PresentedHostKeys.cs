using Querithm.Contract;

namespace Querithm.Core.Ssh;

/// <summary>
/// The untrusted bastion host keys this sidecar was actually shown (profile.test, session.open, a tunnel reopen), so
/// ssh.trustHostKey can trust only one of them (M2): a key the webview made up, or one for a host that never presented
/// it, is refused. Bounded (the oldest entry goes first) and expiring: the user compares the fingerprint while the
/// dialog is open, so an entry lives <see cref="Lifetime"/>; trusting a key uses its entry up.
/// </summary>
public sealed class PresentedHostKeys(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int Capacity = 32;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly object _gate = new();
    readonly List<(SshHostKey Key, DateTimeOffset At)> _keys = [];

    static bool Same(SshHostKey a, SshHostKey b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port
        && string.Equals(a.Algorithm, b.Algorithm, StringComparison.Ordinal) && string.Equals(a.Fingerprint, b.Fingerprint, StringComparison.Ordinal);

    void DropExpired(DateTimeOffset now) => _keys.RemoveAll(e => now - e.At >= Lifetime);

    /// <summary>A key the server presented and the known_hosts file did not trust.</summary>
    public void Record(SshHostKey key)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            DropExpired(now);
            _keys.RemoveAll(e => Same(e.Key, key));
            if (_keys.Count >= Capacity) _keys.RemoveAt(0);
            _keys.Add((key, now));
        }
    }

    /// <summary>Takes the entry of exactly this key (host, port, algorithm, fingerprint); false when none is live.</summary>
    public bool Take(SshHostKey key)
    {
        lock (_gate)
        {
            DropExpired(_clock.GetUtcNow());
            return _keys.RemoveAll(e => Same(e.Key, key)) > 0;
        }
    }
}
