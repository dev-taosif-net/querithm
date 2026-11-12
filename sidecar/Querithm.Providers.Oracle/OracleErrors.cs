using System.Globalization;
using System.Security.Authentication;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// ORA- errors as Querithm shows them (D4 S03): the code in <see cref="ProviderMessage.Number"/>, the server's own text
/// (without the "Help: https://…" line 23ai appends), the parse position as a line in the command text plus its column, and
/// the cases a user meets on connect (sign-in, listener, service, TLS) said in Querithm's terms.
/// </summary>
internal static class OracleErrors
{
    public static IReadOnlyList<ProviderMessage> Translate(OracleException ex, string? commandText)
    {
        var list = new List<ProviderMessage>();
        if (Reason(ex) is { } reason)
        {
            list.Add(new ProviderMessage(Severity.Error, reason, Number: ex.Number));
            return list;
        }
        var errors = ex.Errors.Cast<OracleError>().Where(e => e.Number != 0).ToList();
        if (errors.Count == 0)
        {
            list.Add(new ProviderMessage(Severity.Error, Clean(ex.Message), Number: ex.Number == 0 ? null : ex.Number));
            return list;
        }
        foreach (var e in errors)
        {
            var (line, column) = Position(commandText, e.ParseErrorOffset);
            var text = Clean(e.Message);
            if (column is { } col) text += string.Create(CultureInfo.InvariantCulture, $" (line {line}, column {col})");
            list.Add(new ProviderMessage(Severity.Error, text, Line: line, Procedure: string.IsNullOrEmpty(e.Procedure) ? null : e.Procedure, Number: e.Number, Column: column));
        }
        return list;
    }

    /// <summary>
    /// The 1-based line and column of a 0-based character offset into <paramref name="text"/> (the driver's ParseErrorOffset);
    /// none when the server named no position (0 is also "no position" when the statement itself is not at fault).
    /// </summary>
    internal static (int? Line, int? Column) Position(string? text, int offset)
    {
        if (text == null || offset <= 0 || offset > text.Length) return (null, null);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        return (line, offset - lineStart + 1);
    }

    /// <summary>The server's text without the trailing "Help: https://docs.oracle.com/…" line.</summary>
    internal static string Clean(string message)
    {
        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.TrimStart().StartsWith("Help: http", StringComparison.OrdinalIgnoreCase)).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines).Trim();
    }

    /// <summary>A connect-time failure in Querithm's words; null for any other error (shown as the server words it).</summary>
    internal static string? Reason(OracleException ex)
    {
        if (TlsFailure(ex) is { } tls) return tls;
        return Code(ex) switch
        {
            1017 => "The server rejected the user name or password (ORA-01017). Check both; the password is case-sensitive.",
            1005 => "No password was given (ORA-01005). Enter the account's password.",
            28000 => "The account is locked (ORA-28000). An administrator can unlock it (ALTER USER … ACCOUNT UNLOCK).",
            28001 => "The account's password has expired (ORA-28001). Change it with SQL*Plus or ask an administrator, then sign in with the new one.",
            12514 => "The listener does not know the service named in this profile (ORA-12514). Check the service name (for example FREEPDB1 or ORCLPDB1); the listener's services are listed by lsnrctl status.",
            12505 => "The listener does not know the SID named in this profile (ORA-12505). Check the SID, or connect by service name instead.",
            12541 => "Nothing is listening at this host and port (ORA-12541). Check the port: 2484 is the usual TCPS port, 1521 the plain TCP one.",
            // Linux and macOS: ODP.NET reports a refused (or unreachable) address as ORA-50232 where Windows says ORA-12541 (D4 S13).
            50232 => "Nothing answered at this host and port (ORA-50232: the connection was refused or the address cannot be reached). Check the host and the port: 2484 is the usual TCPS port, 1521 the plain TCP one.",
            12545 => "The host could not be found or reached (ORA-12545). Check the host name.",
            12170 or 12535 => "The connection timed out before the server answered (ORA-" + Code(ex).ToString("D5", CultureInfo.InvariantCulture) + "). Check the host, the port and any firewall in between.",
            1013 => "The statement was cancelled (ORA-01013).",
            28 => "The server ended this session (ORA-00028: it was killed, for example by ALTER SYSTEM KILL SESSION). " + LostTail,
            1012 => "This session is no longer signed in on the server (ORA-01012). " + LostTail,
            2396 => "The server ended this session after it was idle for too long (ORA-02396). " + LostTail,
            3113 or 3114 or 3135 or 12537 or 12547 or 12570 or 12571 =>
                "The connection to the server was lost (ORA-" + Code(ex).ToString("D5", CultureInfo.InvariantCulture) + "): the server or the network closed it. " + LostTail,
            _ => null,
        };
    }

    /// <summary>What a lost connection means for the tab (Core discards the connection; the next run opens another).</summary>
    internal const string LostTail = "Run again to reconnect; whatever this session had not committed was rolled back by the server.";

    /// <summary>Codes after which the driver connection is gone (it reports itself closed; Core discards it).</summary>
    internal static bool IsConnectionLost(int code) => code is 28 or 1012 or 2396 or 3113 or 3114 or 3135 or 12537 or 12547 or 12570 or 12571;

    /// <summary>
    /// A connect through an SSH forward that failed because the driver dialled another address: the listener answered with
    /// a redirect (a RAC SCAN listener handing the client to a node, a shared-server dispatcher, a REDIRECT) and the driver
    /// followed it, outside the tunnel. The forward itself is a live loopback listener, so a host that does not resolve
    /// (ORA-12545) or "no listener" at an address other than the forward can only be such a redirect. Null for any other
    /// failure. Reads the driver's messages, not SQL.
    /// </summary>
    internal static string? RedirectRefusal(Exception ex, int forwardPort, string serverHost, int serverPort)
    {
        string? target = null;
        var redirected = false;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain or System.Net.Sockets.SocketError.NoData })
                redirected = true;
            switch (LeadingCode(e.Message))
            {
                case 12545:
                    redirected = true;
                    break;
                case 12541 or 50232 when NoListenerAt(e.Message) is { } at && !(IsLoopback(at.Host) && at.Port == forwardPort):
                    redirected = true;
                    target = string.Create(CultureInfo.InvariantCulture, $"{at.Host}:{at.Port}");
                    break;
            }
        }
        if (!redirected) return null;
        var where = target == null ? "another address" : target;
        return $"The Oracle listener at {serverHost}:{serverPort} sent the connection on to {where} (a RAC SCAN listener, a shared-server dispatcher or a REDIRECT), " +
               "and the SSH tunnel reaches only the listener itself. Connect to a node's own listener and service (not the SCAN name), or connect without the SSH tunnel.";
    }

    static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    /// <summary>
    /// "ORA-12541: Cannot connect. No listener at host H port P. …" (Windows) or "ORA-50232: Network Transport: TCP transport
    /// address connect failure for host H port P. …" (Linux, macOS): the host and port the driver dialled.
    /// </summary>
    internal static (string Host, int Port)? NoListenerAt(string message)
    {
        string? marker = null;
        var i = -1;
        foreach (var m in (string[])["No listener at", "connect failure for"])
            if ((i = message.IndexOf(m, StringComparison.OrdinalIgnoreCase)) >= 0) { marker = m; break; }
        if (marker == null) return null;
        var words = message[(i + marker.Length)..].Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var h = Array.FindIndex(words, w => w.Equals("host", StringComparison.OrdinalIgnoreCase));
        if (h < 0 || h + 3 >= words.Length || !words[h + 2].Equals("port", StringComparison.OrdinalIgnoreCase)) return null;
        var host = words[h + 1];
        var portText = words[h + 3].TrimEnd('.', ',', ';');
        if (!OracleConnectionStrings.ValidHost(host) || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) return null;
        return (host, port);
    }

    /// <summary>
    /// The code that says what happened. A connect failure arrives as ORA-50201 ("failed to connect"), with the listener's
    /// own error (ORA-12514 unknown service, ORA-12541 no listener …) only in the innermost driver exception's text: the
    /// deepest "ORA-nnnnn" at the start of a message in the chain wins. This reads the driver's message, not SQL.
    /// </summary>
    internal static int Code(OracleException ex)
    {
        var code = ex.Number;
        for (Exception? e = ex.InnerException; e != null; e = e.InnerException)
            if (LeadingCode(e.Message) is { } inner and not 50201) code = inner;
        return code;
    }

    internal static int? LeadingCode(string? message)
    {
        var m = (message ?? "").TrimStart();
        if (m.Length < 9 || !m.StartsWith("ORA-", StringComparison.Ordinal)) return null;
        for (var i = 4; i < 9; i++) if (!char.IsAsciiDigit(m[i])) return null;
        return int.Parse(m.AsSpan(4, 5), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A TCPS handshake that failed on the certificate (ODP.NET reports it as a TNS error with the TLS failure inside, or as
    /// ORA-29024 / ORA-29003): the certificate chain or the server name did not check.
    /// </summary>
    internal static string? TlsFailure(OracleException ex)
    {
        var tlsInside = false;
        for (Exception? e = ex.InnerException; e != null; e = e.InnerException)
            if (e is AuthenticationException) tlsInside = true;
        if (!tlsInside && ex.Number is not (29024 or 29003 or 29002 or 29106) && !ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)) return null;
        return "The TLS (TCPS) connection was refused because the server's certificate could not be verified: it is not signed by a CA this computer trusts, " +
               "or it was not issued for this host name. Give the CA file of the server's certificate in the profile, or check the host name. " +
               $"(Driver: {Clean(ex.Message)})";
    }
}
