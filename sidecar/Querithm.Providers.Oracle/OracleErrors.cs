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
            12545 => "The host could not be found or reached (ORA-12545). Check the host name.",
            12170 or 12535 => "The connection timed out before the server answered (ORA-" + Code(ex).ToString("D5", CultureInfo.InvariantCulture) + "). Check the host, the port and any firewall in between.",
            1013 => "The statement was cancelled (ORA-01013).",
            3113 or 3114 or 3135 => "The connection to the server was lost (ORA-" + Code(ex).ToString("D5", CultureInfo.InvariantCulture) + ").",
            _ => null,
        };
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
