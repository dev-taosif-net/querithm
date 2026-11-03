using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Querithm.Contract;
using Querithm.Core.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Querithm.Core.Ssh;

/// <summary>
/// SSH tunnels to database servers behind a bastion. One SSH connection per (profile, bastion, target),
/// forwarding a local port on 127.0.0.1 — the drivers can only dial TCP. That listener is the single exception to
/// "the sidecar binds no TCP port" (§16): it is loopback-only, ephemeral, and every accepted connection is checked
/// before a channel opens — its client socket must belong to this process (Linux, Windows), else (where the OS cannot
/// tell) it must arrive inside a connect window that one of our own driver opens holds. Everything else is dropped.
/// </summary>
public sealed class SshTunnelManager(KnownHosts known, ILogger log) : IAsyncDisposable
{
    public sealed class Tunnel : IDisposable
    {
        internal SshClient Client { get; init; } = null!;
        internal ForwardedPortLocal Forward { get; init; } = null!;
        public required string Key { get; init; }
        public required string ProfileId { get; init; }
        readonly List<Ticket> _tickets = [];
        public long Refused;
        public volatile bool Dead;

        public IPEndPoint Local => new(IPAddress.Loopback, (int)Forward.BoundPort);
        public bool Alive => !Dead && Client.IsConnected && Forward.IsStarted;

        internal sealed class Ticket { public bool Used; }

        /// <summary>A connect window for one driver open: one connection may come in while it is held.</summary>
        public IDisposable Expect()
        {
            var t = new Ticket();
            lock (_tickets) _tickets.Add(t);
            return new Releaser(() => { lock (_tickets) _tickets.Remove(t); });
        }

        internal bool TakeTicket()
        {
            lock (_tickets)
            {
                var t = _tickets.FirstOrDefault(x => !x.Used);
                if (t == null) return false;
                t.Used = true;
                return true;
            }
        }

        public void Dispose()
        {
            Dead = true;
            try { Forward.Stop(); } catch { /* already stopped */ }
            try { Client.Dispose(); } catch { /* already gone */ }
        }
    }

    sealed class Releaser(Action release) : IDisposable
    {
        int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) release(); }
    }

    readonly ConcurrentDictionary<string, Tunnel> _tunnels = new();
    readonly SemaphoreSlim _gate = new(1, 1);

    public KnownHosts KnownHosts => known;

    /// <summary>The untrusted keys bastions presented to this sidecar; ssh.trustHostKey trusts only one of them (M2).</summary>
    public PresentedHostKeys Presented { get; } = new();

    /// <summary>
    /// Records a key the user trusted in the UI. Only a key a bastion presented to this sidecar in the last
    /// <see cref="PresentedHostKeys.Lifetime"/> (exactly: host, port, algorithm, fingerprint) is accepted; the webview
    /// cannot pre-trust a key it chose.
    /// </summary>
    public void Trust(SshHostKey key)
    {
        if (!Presented.Take(key))
            throw new QuerithmException("sshHostKeyNotPresented",
                $"The SSH server {key.Host}:{key.Port} did not present that key to Querithm (or it was shown more than {PresentedHostKeys.Lifetime.TotalMinutes:0} minutes ago). Connect again and compare the fingerprint it shows.");
        known.Trust(key);
    }

    static string KeyOf(Profile p, string targetHost, int targetPort)
    {
        var s = p.Ssh!;
        return string.Join('|', p.Id, s.Host, s.Port ?? 22, s.Username, s.Auth, s.KeyPath, targetHost, targetPort);
    }

    /// <summary>The live tunnel for the profile's target, opening (or re-opening a dropped) one when needed.</summary>
    public async Task<Tunnel> EnsureAsync(Profile p, string? sshSecret, string targetHost, int targetPort, CancellationToken ct)
    {
        var key = KeyOf(p, targetHost, targetPort);
        if (_tunnels.TryGetValue(key, out var t) && t.Alive) return t;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tunnels.TryGetValue(key, out t) && t.Alive) return t;
            if (t != null)
            {
                log.LogInformation("SSH tunnel for {Profile} was closed; reconnecting", p.Id);
                t.Dispose();
            }
            t = await OpenAsync(p, sshSecret, targetHost, targetPort, key, ct).ConfigureAwait(false);
            _tunnels[key] = t;
            return t;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A tunnel of its own (profile tests): not shared, closed by the caller.</summary>
    public Task<Tunnel> OpenTemporaryAsync(Profile p, string? sshSecret, string targetHost, int targetPort, CancellationToken ct) =>
        OpenAsync(p, sshSecret, targetHost, targetPort, KeyOf(p, targetHost, targetPort) + "|temp", ct);

    async Task<Tunnel> OpenAsync(Profile p, string? sshSecret, string targetHost, int targetPort, string key, CancellationToken ct)
    {
        var s = p.Ssh ?? throw QuerithmException.Invalid("The profile has no SSH settings.");
        var port = s.Port is > 0 ? s.Port.Value : 22;
        LogScrubber.RegisterSecret(sshSecret);
        var info = new ConnectionInfo(s.Host, port, s.Username, Authentication(s, sshSecret)) { Timeout = TimeSpan.FromSeconds(Math.Max(1, p.TimeoutS ?? 15)) };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        SshHostKey? untrusted = null;
        client.HostKeyReceived += (_, e) =>
        {
            var (verdict, previous) = known.Check(s.Host, port, e.HostKeyName, e.FingerPrintSHA256);
            if (verdict == HostKeyVerdict.Trusted)
            {
                e.CanTrust = true;
                return;
            }
            untrusted = new SshHostKey { Host = s.Host, Port = port, Algorithm = e.HostKeyName, Fingerprint = e.FingerPrintSHA256, PreviousFingerprint = previous };
            Presented.Record(untrusted);
            e.CanTrust = false;
        };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (untrusted != null)
        {
            client.Dispose();
            log.LogInformation("SSH host key for {Host}:{Port} not trusted ({Message})", s.Host, port, ex.Message);
            throw untrusted.PreviousFingerprint == null
                ? new QuerithmException("sshHostKeyUnknown",
                    $"The SSH server {s.Host}:{port} is not trusted yet. Its {untrusted.Algorithm} key fingerprint is SHA256:{untrusted.Fingerprint}. Compare it with what the server's administrator gives you before trusting it.", untrusted)
                : new QuerithmException("sshHostKeyChanged",
                    $"The SSH server {s.Host}:{port} presented a different key than the one you trusted (trusted SHA256:{untrusted.PreviousFingerprint}, now SHA256:{untrusted.Fingerprint}). The server may have been reinstalled — or the connection is being intercepted. Nothing was sent.", untrusted);
        }
        catch (Exception ex) when (ex is SshAuthenticationException or SshPassPhraseNullOrEmptyException)
        {
            client.Dispose();
            throw new QuerithmException("sshAuth", $"SSH sign-in to {s.Username}@{s.Host}:{port} failed: {LogScrubber.Scrub(ex.Message)}");
        }
        catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException or ProxyException or TimeoutException)
        {
            client.Dispose();
            throw new QuerithmException("sshConnect", $"Could not reach the SSH server {s.Host}:{port}: {LogScrubber.Scrub(ex.Message)}");
        }

        var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
        var tunnel = new Tunnel { Client = client, Forward = forward, Key = key, ProfileId = p.Id };
        // SSH.NET raises this before it opens the channel; throwing makes it close the accepted socket (nothing is forwarded).
        forward.RequestReceived += (_, e) => Admit(tunnel, e.OriginatorHost, e.OriginatorPort);
        forward.Exception += (_, e) => log.LogDebug("SSH forward {Key}: {Message}", key, LogScrubber.Scrub(e.Exception.Message));
        client.ErrorOccurred += (_, e) =>
        {
            tunnel.Dead = true;
            log.LogWarning("SSH tunnel to {Host} closed: {Message}", s.Host, LogScrubber.Scrub(e.Exception.Message));
        };
        try
        {
            client.AddForwardedPort(forward);
            forward.Start();
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
        log.LogInformation("SSH tunnel {User}@{Host}:{Port} → {Target}:{TargetPort} on {Local}", s.Username, s.Host, port, targetHost, targetPort, tunnel.Local);
        return tunnel;
    }

    static AuthenticationMethod[] Authentication(SshSettings s, string? secret)
    {
        switch (s.Auth)
        {
            case SshAuthKind.Password:
            {
                if (string.IsNullOrEmpty(secret)) throw new QuerithmException("sshNeedsSecret", "The SSH password is not loaded. Enter it to connect.");
                var interactive = new KeyboardInteractiveAuthenticationMethod(s.Username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (var prompt in e.Prompts) if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase)) prompt.Response = secret;
                };
                return [new PasswordAuthenticationMethod(s.Username, secret), interactive];
            }
            case SshAuthKind.PrivateKey:
                return [new PrivateKeyAuthenticationMethod(s.Username, LoadKey(s.KeyPath, secret))];
            default:
                throw new QuerithmException("sshAgent", "Signing in through an SSH agent is not supported yet. Choose a key file or a password for the tunnel.");
        }
    }

    /// <summary>The user-picked key file: an absolute path to a small regular file. Its content is never echoed.</summary>
    static PrivateKeyFile LoadKey(string? path, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw QuerithmException.Invalid("Choose the SSH key file (an absolute path).");
        var info = new FileInfo(path);
        if (!info.Exists) throw new QuerithmException("sshKey", $"The SSH key file {Path.GetFileName(path)} does not exist.");
        if (info.Length > 256 * 1024) throw new QuerithmException("sshKey", $"{Path.GetFileName(path)} is too large to be an SSH private key.");
        try
        {
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            throw new QuerithmException("sshNeedsSecret", $"The SSH key {Path.GetFileName(path)} is protected by a passphrase. Enter it to connect.");
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or FormatException or NotSupportedException or IOException)
        {
            // Wrong passphrase, unsupported format: the message names the problem, never the key.
            throw new QuerithmException("sshKey", $"The SSH key {Path.GetFileName(path)} could not be used: {LogScrubber.Scrub(ex.Message)}");
        }
    }

    void Admit(Tunnel t, string host, uint port)
    {
        var client = PeerOwnership.Endpoint(host, port);
        var allowed = client != null && IPAddress.IsLoopback(client.Address) && (PeerOwnership.IsOwnConnection(client, t.Local) ?? t.TakeTicket());
        if (allowed) return;
        Interlocked.Increment(ref t.Refused);
        log.LogWarning("SSH forward {Local}: refused a connection from {Host}:{Port} that did not come from Querithm", t.Local, host, port);
        throw new SocketException((int)SocketError.ConnectionRefused);
    }

    /// <summary>Closes the profile's tunnels (profile changed or deleted, or disconnected).</summary>
    public void Close(string profileId)
    {
        foreach (var (key, t) in _tunnels.Where(kv => kv.Value.ProfileId == profileId).ToList())
        {
            _tunnels.TryRemove(key, out _);
            t.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var t in _tunnels.Values) t.Dispose();
        _tunnels.Clear();
        return ValueTask.CompletedTask;
    }
}
