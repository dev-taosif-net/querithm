using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Querithm.Providers.Oracle;

/// <summary>
/// Verify full through an SSH forward (D4 S07). ODP.NET checks the server name against the address it dials, which through
/// a tunnel is 127.0.0.1, and takes no certificate callback. So before the driver opens, one TLS handshake of Querithm's
/// own goes through the same forward: the certificate must chain to the profile's CA file (or the system's roots) for
/// server authentication and be issued for the real server name. Its exact subject is then handed to the driver
/// (<c>SSL_SERVER_CERT_DN</c>), which checks the chain again and that subject on its own handshake — a certificate for
/// another name cannot pass the second check without passing the first (ARCHITECTURE §16).
/// </summary>
internal static class OracleTunnelTls
{
    /// <summary>A TLS failure said in Querithm's terms; the driver is not involved yet.</summary>
    internal sealed class CertificateRefusedException(string message, Exception? inner = null) : AuthenticationException(message, inner);

    /// <summary>The checked certificate's subject (<see cref="X509Certificate.Subject"/>: the form the driver compares).</summary>
    public static async Task<string> ServerSubjectAsync(string forwardHost, int forwardPort, string serverName, string? caPath, int timeoutS, CancellationToken ct)
    {
        var roots = ReadRoots(caPath);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutS > 0 ? timeoutS : 15));
        using var tcp = new TcpClient();
        string? refusal = null;
        string? subject = null;
        try
        {
            await tcp.ConnectAsync(forwardHost, forwardPort, limit.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, chain, errors) =>
            {
                refusal = Check(cert, chain, errors, serverName, roots, out subject);
                return refusal == null;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The server did not finish the TLS handshake through the SSH tunnel within {(timeoutS > 0 ? timeoutS : 15)} s.");
        }
        catch (Exception ex) when (refusal != null && ex is AuthenticationException or IOException)
        {
            throw new CertificateRefusedException(refusal, ex);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
            throw new CertificateRefusedException(
                $"The TLS (TCPS) handshake with {serverName} through the SSH tunnel failed: {ex.Message} Check that the profile's port is the server's TCPS listener (usually 2484).", ex);
        }
        return subject ?? throw new CertificateRefusedException($"{serverName} presented no certificate.");
    }

    /// <summary>Null when the certificate passes; else why not, naming the server.</summary>
    internal static string? Check(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, string serverName, X509Certificate2Collection? roots, out string? subject)
    {
        subject = null;
        if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return $"{serverName} presented no certificate.";
        using var leaf = new X509Certificate2(certificate);
        bool trusted;
        if (roots == null) trusted = (errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0;
        else
        {
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(roots);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // server authentication
            if (chain != null)
                foreach (var e in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(e.Certificate);
            trusted = custom.Build(leaf);
        }
        if (!trusted)
            return "The TLS (TCPS) connection was refused because the server's certificate could not be verified: it is not signed by a CA this computer trusts. " +
                   "Give the CA file of the server's certificate in the profile.";
        if (!leaf.MatchesHostname(serverName))
            return $"The TLS (TCPS) connection was refused because the server's certificate was not issued for {serverName} (it names {leaf.GetNameInfo(X509NameType.DnsName, false)}). " +
                   "Check the host name, or choose Verify CA to check the certificate chain only.";
        if (!OracleConnectionStrings.ValidCertDn(leaf.Subject))
            return "The server's certificate passed, but its subject holds characters Oracle's driver cannot be given to check it again. Choose Verify CA, or connect without the SSH tunnel.";
        subject = leaf.Subject;
        return null;
    }

    static X509Certificate2Collection? ReadRoots(string? caPath)
    {
        if (string.IsNullOrEmpty(caPath)) return null;
        X509Certificate2Collection roots = [];
        try
        {
            roots.ImportFromPemFile(caPath);
            if (roots.Count == 0) roots.Add(X509CertificateLoader.LoadCertificateFromFile(caPath)); // DER
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException($"The CA file {Path.GetFileName(caPath)} could not be read as a certificate: {ex.Message}", ex);
        }
        return roots;
    }
}
