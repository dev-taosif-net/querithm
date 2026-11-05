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

    /// <summary>
    /// The key type an algorithm name signs with: one RSA key is offered as ssh-rsa, rsa-sha2-256 or rsa-sha2-512 (the
    /// signature hash is negotiated, the key and its fingerprint are the same), so a line written under any of them
    /// matches the others. Lines keep the name they were written with.
    /// </summary>
    internal static string KeyType(string algorithm) => algorithm switch
    {
        "ssh-rsa" or "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
        "ssh-rsa-cert-v01@openssh.com" or "rsa-sha2-256-cert-v01@openssh.com" or "rsa-sha2-512-cert-v01@openssh.com" => "ssh-rsa-cert-v01@openssh.com",
        _ => algorithm,
    };

    /// <summary>Trusted when this exact key is known; Changed when another key is known for the host (any algorithm the same).</summary>
    public (HostKeyVerdict Verdict, string? Previous) Check(string host, int port, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            var type = KeyType(algorithm);
            var known = Load().Where(e => Same(e.Host, host) && e.Port == port).ToList();
            if (known.Any(e => KeyType(e.Algorithm) == type && e.Fingerprint == fingerprint)) return (HostKeyVerdict.Trusted, null);
            var sameAlgorithm = known.FirstOrDefault(e => KeyType(e.Algorithm) == type);
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
