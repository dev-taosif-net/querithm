using System.ComponentModel;
using System.Security.Authentication;

namespace Querithm.Contract.Providers;

/// <summary>
/// Inspects driver exception chains for TLS certificate validation failures (Linux/macOS managed SslStream
/// <see cref="AuthenticationException"/> and Windows Schannel/SNI codes) while ignoring non-certificate TLS failures
/// (such as protocol or cipher mismatches).
/// </summary>
public static class TlsCertificateClassifier
{
    public readonly record struct Match(TlsCertReason Reason, string Detail);

    // Windows Schannel / CryptoAPI HRESULTs raised by native SNI / Win32Exception on certificate validation errors.
    const int SecEUntrustedRoot = unchecked((int)0x80090325);
    const int SecEWrongPrincipal = unchecked((int)0x80090322);
    const int SecECertExpired = unchecked((int)0x80090328);
    const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    const int CertEChaining = unchecked((int)0x800B010A);
    const int CertECnNoMatch = unchecked((int)0x800B010F);
    const int CertEExpired = unchecked((int)0x800B0101);

    /// <summary>
    /// Returns the certificate failure reason and concise technical detail when <paramref name="ex"/> failed because the
    /// server's TLS certificate was rejected; null for unreachable hosts, auth failures, or TLS protocol/cipher errors.
    /// </summary>
    public static Match? TryClassify(Exception ex)
    {
        AuthenticationException? authEx = null;
        Match? found = null;

        foreach (var e in Flatten(ex))
        {
            if (e is AuthenticationException ae && authEx == null) authEx = ae;
            if (e is Win32Exception w32 && ClassifyCode(w32.NativeErrorCode) is { } fromWin32)
                Record(ref found, fromWin32, CleanDetail(w32.Message));
            if (ClassifyCode(e.HResult) is { } fromHr)
                Record(ref found, fromHr, CleanDetail(e.Message));
            if (ClassifyMessage(e.Message) is { } fromMsg)
                Record(ref found, fromMsg, CleanDetail(e.Message));
        }

        if (found != null)
        {
            var detail = authEx != null && !string.IsNullOrWhiteSpace(authEx.Message)
                ? CleanDetail(authEx.Message)
                : found.Value.Detail;
            return found.Value with { Detail = detail };
        }
        return null;
    }

    // Specific certificate reasons (UntrustedRoot, NameMismatch, Expired) always take precedence over generic Other.
    static void Record(ref Match? found, TlsCertReason reason, string detail)
    {
        if (found == null || (found.Value.Reason == TlsCertReason.Other && reason != TlsCertReason.Other))
            found = new(reason, detail);
    }

    /// <summary>Deepest non-empty inner exception message (used when managed SNI wraps a failure as generic error 35).</summary>
    public static string? DeepestInnerMessage(Exception ex)
    {
        string? best = null;
        var first = true;
        foreach (var e in Flatten(ex))
        {
            if (first) { first = false; continue; }
            if (!string.IsNullOrWhiteSpace(e.Message)) best = CleanDetail(e.Message);
        }
        return best;
    }

    /// <summary>Short plain-English phrase for <paramref name="reason"/>.</summary>
    public static string ReasonPhrase(TlsCertReason reason) => reason switch
    {
        TlsCertReason.UntrustedRoot => "the certificate is self-signed or issued by an untrusted authority",
        TlsCertReason.NameMismatch => "the certificate name does not match the server address",
        TlsCertReason.Expired => "the certificate has expired or is not yet valid",
        _ => "the server's certificate failed validation",
    };

    /// <summary>Formats host[:port] for user-facing TLS messages.</summary>
    public static string FormatEndpoint(string? host, int? port, int? defaultPort)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim();
        if (h.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) h = h[4..].Trim();
        if (string.IsNullOrEmpty(h)) h = "localhost";
        if (h.Contains('\\') || h.Contains(',')) return h;
        var p = port is > 0 ? port : defaultPort;
        if (h.StartsWith('['))
            return h.EndsWith(']') && p is > 0 ? $"{h}:{p}" : h;
        if (System.Net.IPAddress.TryParse(h, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return p is > 0 ? $"[{h}]:{p}" : h;
        if (h.Contains(':')) return h; // already host:port
        return p is > 0 ? $"{h}:{p}" : h;
    }

    static TlsCertReason? ClassifyCode(int code) => code switch
    {
        SecEUntrustedRoot or CertEUntrustedRoot or CertEChaining => TlsCertReason.UntrustedRoot,
        SecEWrongPrincipal or CertECnNoMatch => TlsCertReason.NameMismatch,
        SecECertExpired or CertEExpired => TlsCertReason.Expired,
        _ => null,
    };

    static TlsCertReason? ClassifyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        // Name / CN / SAN mismatch
        if (Has(message, "RemoteCertificateNameMismatch")
            || Has(message, "target principal name is incorrect")
            || Has(message, "certificate name does not match")
            || Has(message, "hostname mismatch")
            || Has(message, "host name mismatch")
            || Has(message, "not issued for this host")
            || Has(message, "CERT_E_CN_NO_MATCH"))
            return TlsCertReason.NameMismatch;

        // Expired / not yet valid
        if (Has(message, "NotTimeValid")
            || Has(message, "certificate has expired")
            || Has(message, "certificate is expired")
            || Has(message, "certificate is not yet valid")
            || Has(message, "CERT_E_EXPIRED"))
            return TlsCertReason.Expired;

        // Untrusted root / self-signed / chain error
        if (Has(message, "UntrustedRoot")
            || Has(message, "PartialChain")
            || Has(message, "authority that is not trusted")
            || Has(message, "self-signed certificate")
            || Has(message, "self signed certificate")
            || Has(message, "unable to get local issuer certificate")
            || Has(message, "unable to verify the first certificate")
            || Has(message, "CERT_E_UNTRUSTEDROOT")
            || Has(message, "RemoteCertificateChainErrors"))
            return TlsCertReason.UntrustedRoot;

        // Generic SslStream / driver certificate validation failure (never matches protocol/cipher errors)
        if (Has(message, "remote certificate is invalid")
            || Has(message, "remote certificate was rejected")
            || Has(message, "CERTIFICATE_VERIFY_FAILED")
            || Has(message, "certificate could not be verified")
            || Has(message, "RemoteCertificateNotAvailable")
            || Has(message, "ORA-29024")
            || Has(message, "ORA-29003")
            || Has(message, "ORA-29002"))
            return TlsCertReason.Other;

        return null;
    }

    static bool Has(string text, string needle) => text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the first line of <paramref name="text"/>, capped at 240 characters.</summary>
    public static string CleanDetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0].Trim();
        return line.Length > 240 ? line[..240] + "…" : line;
    }

    static IEnumerable<Exception> Flatten(Exception root)
    {
        var stack = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            yield return cur;
            if (cur is AggregateException agg)
            {
                for (var i = agg.InnerExceptions.Count - 1; i >= 0; i--)
                    if (agg.InnerExceptions[i] != null) stack.Push(agg.InnerExceptions[i]);
            }
            else if (cur.InnerException != null)
            {
                stack.Push(cur.InnerException);
            }
        }
    }
}
