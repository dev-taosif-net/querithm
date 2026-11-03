using Querithm.Contract;

namespace Querithm.Core.Ssh;

public enum HostKeyVerdict { Trusted, Unknown, Changed }

/// <summary>
/// The bastion host keys the user trusted, one line per (host, port, algorithm): "host port algorithm
/// SHA256-fingerprint" in the app data directory. Never auto-accepts: an unknown key waits for the user, a changed one
/// is refused until the user trusts the new key on purpose.
/// </summary>
public sealed class KnownHosts(string path)
{
    readonly object _gate = new();

    sealed record Entry(string Host, int Port, string Algorithm, string Fingerprint);

    List<Entry> Load()
    {
        if (!File.Exists(path)) return [];
        var list = new List<Entry>();
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && int.TryParse(parts[1], out var port)) list.Add(new Entry(parts[0], port, parts[2], parts[3]));
        }
        return list;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Trusted when this exact key is known; Changed when another key is known for the host (any algorithm the same).</summary>
    public (HostKeyVerdict Verdict, string? Previous) Check(string host, int port, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            var known = Load().Where(e => Same(e.Host, host) && e.Port == port).ToList();
            if (known.Any(e => e.Algorithm == algorithm && e.Fingerprint == fingerprint)) return (HostKeyVerdict.Trusted, null);
            var sameAlgorithm = known.FirstOrDefault(e => e.Algorithm == algorithm);
            // A server may offer another key type later (negotiation changes); a different key of a known type is a change.
            return sameAlgorithm != null ? (HostKeyVerdict.Changed, sameAlgorithm.Fingerprint) : known.Count > 0 ? (HostKeyVerdict.Changed, known[0].Fingerprint) : (HostKeyVerdict.Unknown, null);
        }
    }

    /// <summary>Records the key the user trusted, replacing older keys of the host.</summary>
    public void Trust(SshHostKey key)
    {
        if (key.Host.Any(char.IsWhiteSpace) || key.Algorithm.Any(char.IsWhiteSpace) || key.Fingerprint.Any(char.IsWhiteSpace))
            throw QuerithmException.Invalid("Malformed host key.");
        lock (_gate)
        {
            var list = Load().Where(e => !(Same(e.Host, key.Host) && e.Port == key.Port)).ToList();
            list.Add(new Entry(key.Host, key.Port, key.Algorithm, key.Fingerprint));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllLines(path, list.Select(e => $"{e.Host} {e.Port} {e.Algorithm} {e.Fingerprint}"));
        }
    }
}
