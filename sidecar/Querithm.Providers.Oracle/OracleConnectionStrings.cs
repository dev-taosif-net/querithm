using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Querithm.Contract;
using Querithm.Contract.Providers;

namespace Querithm.Providers.Oracle;

/// <summary>
/// How an Oracle profile becomes a connection string (D4 S03). Querithm writes the whole connect descriptor itself — one
/// address, the service (or, only through <see cref="SidOption"/>, a SID), TCPS with the certificate and server name
/// checked unless the profile downgrades it — and names an empty Querithm-owned directory as TNS_ADMIN, so no
/// <c>sqlnet.ora</c> / <c>tnsnames.ora</c> on this computer applies (owner decision O1, ARCHITECTURE §16). Nothing from a
/// pasted string's extras reaches the built string: wallets, OS / token sign-in, proxy users, DBA privileges and TNS_ADMIN
/// are named as not applied.
/// </summary>
internal static class OracleConnectionStrings
{
    public const int TcpPort = 1521, TcpsPort = 2484;

    /// <summary>Profile option: "true" makes the profile's service name a SID (<c>CONNECT_DATA=(SID=…)</c>), for servers without a service.</summary>
    public const string SidOption = "oracleSid";

    public static bool UsesSid(Profile p) => string.Equals(p.Options?.GetValueOrDefault(SidOption), "true", StringComparison.OrdinalIgnoreCase);

    public enum Transport { Tcps, TcpsNoNameCheck, Tcp }

    /// <summary>
    /// The profile's TLS mode for Oracle, or the reason it is refused. ODP.NET always checks the certificate chain over TCPS
    /// (there is no "encrypt without checking"), and Oracle has no opportunistic TLS, so the modes that mean either refuse.
    /// </summary>
    public static (Transport Transport, string? Refusal) TransportFor(Profile p)
    {
        if (p.TrustCert) return (Transport.Tcps, "Oracle's driver always checks the server's certificate chain, so \"trust the server certificate\" cannot be turned on. Give the CA file of the server's certificate instead.");
        return p.TlsMode switch
        {
            TlsMode.Default or TlsMode.VerifyFull or TlsMode.Strict => (Transport.Tcps, null),
            TlsMode.VerifyCa => (Transport.TcpsNoNameCheck, null),
            TlsMode.Disable => (Transport.Tcp, null),
            TlsMode.Require or TlsMode.Mandatory => (Transport.Tcps, "Oracle's driver always checks the server's certificate chain over TCPS: choose Verify CA (chain only) or Verify full (chain and server name)."),
            _ => (Transport.Tcps, "Oracle has no opportunistic TLS: choose TCPS (Verify full or Verify CA) or, as an explicit downgrade, Disable."),
        };
    }

    /// <summary>A host name, IPv4 or IPv6 literal: letters, digits, '.', '-', '_' and ':' only, so nothing can close a descriptor clause.</summary>
    internal static bool ValidHost(string? host) =>
        !string.IsNullOrEmpty(host) && host.Length <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':');

    /// <summary>An Oracle service name or SID: letters, digits and _ . $ # -.</summary>
    internal static bool ValidService(string? service) =>
        !string.IsNullOrEmpty(service) && service.Length <= 128 && service.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '$' or '#' or '-');

    /// <summary>
    /// The sign-in name: OS authentication ("/"), a proxy user ("a[b]") and anything that could reach the descriptor are
    /// refused; the driver quotes the value itself.
    /// </summary>
    internal static bool ValidUser(string? user) =>
        !string.IsNullOrWhiteSpace(user) && user.Length <= 128 && user != "/" && !user.Any(c => c is '[' or ']' or '/' or '@' or '\0' || char.IsControl(c));

    /// <summary>Null when the profile can open, else why not (the RPC boundary's check; <see cref="Build"/> repeats it).</summary>
    public static string? Validate(Profile p)
    {
        if ((p.Location ?? LocationKind.Server) != LocationKind.Server) return "An Oracle connection is a server: host, port and service name.";
        if (p.Auth is not (AuthKind.Password or AuthKind.Sql))
            return "Oracle connections sign in with a user name and password in this build of Querithm; wallet, OS and token sign-in are not supported yet.";
        var host = (p.Host ?? "").Trim();
        if (host.Length > 0 && !ValidHost(host)) return "The host must be a server name or an IP address (letters, digits, '.', '-', ':').";
        if (p.Port is { } port && port is < 1 or > 65535) return "The port must be between 1 and 65535.";
        var service = (p.DatabaseDefault ?? "").Trim();
        if (service.Length > 0 && !ValidService(service)) return "The service name may hold letters, digits and _ . $ # - only.";
        if (!string.IsNullOrEmpty(p.Username) && !ValidUser(p.Username))
            return "The user name cannot be \"/\" (OS sign-in) or a proxy user (name[client]); sign in as the database user itself.";
        return TransportFor(p).Refusal;
    }

    /// <summary>
    /// Set by <see cref="OracleProvider.RouteThrough"/> only: the server's own host and port as the SSH bastion reaches them.
    /// Read only for a loopback host (<see cref="TunnelOf"/>), so a stored option cannot retarget a direct connection;
    /// <see cref="OracleProvider.ValidateProfile"/> refuses a profile that names them itself.
    /// </summary>
    public const string TunnelHostOption = "tunnelHost", TunnelPortOption = "tunnelPort";

    /// <summary>The real server behind the SSH forward this profile dials (a loopback host), or null for a direct connection.</summary>
    public static (string Host, int Port)? TunnelOf(Profile p) =>
        p.Options?.GetValueOrDefault(TunnelHostOption) is { Length: > 0 } host && ValidHost(host)
            && int.TryParse(p.Options.GetValueOrDefault(TunnelPortOption), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            && System.Net.IPAddress.TryParse(p.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip)
            ? (host, port) : null;

    /// <summary>
    /// A certificate subject the descriptor can carry inside its quotes (<c>SSL_SERVER_CERT_DN="…"</c>): printable ASCII
    /// without a quote, a parenthesis or a backslash, at most 1,024 characters.
    /// </summary>
    internal static bool ValidCertDn(string? dn) =>
        !string.IsNullOrEmpty(dn) && dn.Length <= 1024 && dn.All(c => c is >= ' ' and <= '~' and not ('"' or '(' or ')' or '\\'));

    /// <param name="certDn">
    /// Through an SSH forward with Verify full: the subject of the certificate already checked against the real server name
    /// (<see cref="OracleTunnelTls"/>). The driver then checks the chain and that exact subject, since the address it dials
    /// (127.0.0.1) is not the name the certificate was issued for.
    /// </param>
    public static string Descriptor(Profile p, string? certDn = null)
    {
        var host = (p.Host ?? "").Trim();
        if (host.Length == 0) host = "localhost";
        var service = (p.DatabaseDefault ?? "").Trim();
        if (!ValidHost(host) || !ValidService(service))
            throw new ArgumentException(service.Length == 0 ? "An Oracle profile needs a service name (for example FREEPDB1 or ORCLPDB1)." : Validate(p) ?? "The host or service name is not valid.");
        var (transport, refusal) = TransportFor(p);
        if (refusal != null) throw new ArgumentException(refusal);
        var tcps = transport != Transport.Tcp;
        var port = p.Port ?? (tcps ? TcpsPort : TcpPort);
        var timeout = p.TimeoutS is { } t && t > 0 ? t : 15;
        var sb = new StringBuilder("(DESCRIPTION=");
        // One address, no retries: a timeout means what it says. Timeouts in seconds.
        sb.Append(CultureInfo.InvariantCulture, $"(CONNECT_TIMEOUT={timeout})(TRANSPORT_CONNECT_TIMEOUT={timeout})(RETRY_COUNT=0)");
        sb.Append(CultureInfo.InvariantCulture, $"(ADDRESS=(PROTOCOL={(tcps ? "TCPS" : "TCP")})(HOST={host})(PORT={port}))");
        // Through a tunnel, a dedicated server: a shared-server dispatcher may answer with a redirect to its own port, which
        // the forward does not reach.
        var tunnelled = TunnelOf(p) != null;
        sb.Append("(CONNECT_DATA=").Append(UsesSid(p) ? "(SID=" : "(SERVICE_NAME=").Append(service).Append(')');
        if (tunnelled) sb.Append("(SERVER=DEDICATED)");
        sb.Append(')');
        if (tcps)
        {
            sb.Append("(SECURITY=(SSL_SERVER_DN_MATCH=").Append(transport == Transport.Tcps ? "TRUE" : "FALSE").Append(')');
            if (certDn != null)
            {
                if (transport != Transport.Tcps || !tunnelled || !ValidCertDn(certDn)) throw new ArgumentException("The server certificate's subject cannot be used to check this connection.");
                sb.Append("(SSL_SERVER_CERT_DN=\"").Append(certDn).Append("\")");
            }
            sb.Append(')');
        }
        else if (certDn != null) throw new ArgumentException("A plain TCP connection has no certificate to check.");
        return sb.Append(')').ToString();
    }

    /// <summary>Pooling off (Core pools); TNS_ADMIN is the empty <paramref name="tnsAdmin"/> directory; the trust wallet when the profile names a CA file.</summary>
    public static string Build(Profile p, string? secret, string tnsAdmin, string? walletDir, string? certDn = null)
    {
        if (Validate(p) is { } why) throw new ArgumentException(why);
        var b = new OracleConnectionStringBuilder
        {
            DataSource = Descriptor(p, certDn),
            UserID = p.Username ?? "",
            Password = secret ?? "",
            Pooling = false,
            // Core reads the connection's identity once; a transparent replay would move it to another server session.
            ["Application Continuity"] = "false",
            ConnectionTimeout = p.TimeoutS is { } t && t > 0 ? t : 15,
            PersistSecurityInfo = false,
            TnsAdmin = tnsAdmin,
            HAEvents = false,
            LoadBalancing = false,
        };
        if (walletDir != null) b.WalletLocation = walletDir;
        return b.ConnectionString;
    }

    // Keywords ParseConnectionString maps onto the profile. Everything else the driver knows is reported as not applied;
    // these ones with the reason (they would bypass the sign-in or the descriptor Querithm writes).
    static readonly HashSet<string> Mapped = new(StringComparer.OrdinalIgnoreCase) { "Data Source", "User Id", "Password" };

    static readonly Dictionary<string, string> RefusedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TNS_ADMIN"] = "TNS_ADMIN is not used: Querithm does not read sqlnet.ora or tnsnames.ora, so a connection works the same on every computer.",
        ["WALLET_LOCATION"] = "WALLET_LOCATION is not used: wallet sign-in is not supported yet; give the CA file of the server's certificate for TCPS.",
        ["WALLET_PASSWORD"] = "WALLET_PASSWORD is not used: wallet sign-in is not supported yet.",
        ["TOKEN_AUTH"] = "TOKEN_AUTH is not used: token sign-in is not supported yet; sign in with a user name and password.",
        ["TOKEN_LOCATION"] = "TOKEN_LOCATION is not used: token sign-in is not supported yet.",
        ["Proxy User Id"] = "Proxy User Id is not used: proxy sign-in is not supported; sign in as the database user itself.",
        ["Proxy Password"] = "Proxy Password is not used: proxy sign-in is not supported; it is not kept.",
        ["DBA Privilege"] = "DBA Privilege (SYSDBA / SYSOPER) is not used: Querithm signs in as an ordinary database user.",
    };

    static bool Knows(string keyword) => new OracleConnectionStringBuilder().ContainsKey(keyword);

    static readonly HashSet<string> Conventional = new(StringComparer.OrdinalIgnoreCase) { "Data Source", "User Id", "Password" };

    /// <summary>A descriptor or an Easy Connect "//host:port/service" in Data Source is Oracle's alone.</summary>
    public static ConnectionStringAffinity Affinity(IReadOnlyDictionary<string, string> keywords)
    {
        var distinctive = keywords.Keys.Count(k => RefusedKeywords.ContainsKey(k));
        if (keywords.TryGetValue("Data Source", out var ds) && (ds.TrimStart().StartsWith('(') || ds.Contains('/'))) distinctive++;
        return new(distinctive, ConnectionStringKeywords.CountKeys(keywords, Conventional));
    }

    public static ParseConnectionStringResult Parse(string connectionString, Profile basis)
    {
        var k = ConnectionStringKeywords.Apply(connectionString, () => new OracleConnectionStringBuilder(), Knows);
        var b = k.Builder;
        var notApplied = new List<string>();
        var notes = new List<string>();
        foreach (var n in k.Named.Where(n => !Mapped.Contains(n)))
        {
            notApplied.Add(n);
            if (RefusedKeywords.TryGetValue(n, out var why)) notes.Add(why);
        }
        var p = basis with { Engine = Engine.Oracle, Auth = AuthKind.Password };
        if (k.Has("Data Source") && !string.IsNullOrWhiteSpace(b.DataSource))
        {
            var target = ParseDataSource(b.DataSource);
            var options = new Dictionary<string, string>(basis.Options ?? []);
            if (target.Sid) options[SidOption] = "true"; else options.Remove(SidOption);
            // A string that does not name the protocol keeps the editor's TLS mode (TCPS by default), as for the other engines.
            var tls = target.Tcps switch { true => basis.TlsMode == TlsMode.Disable ? TlsMode.Default : basis.TlsMode, false => TlsMode.Disable, null => basis.TlsMode };
            if (target.Tcps == false) notes.Add(WeakTls.NotEncrypted("PROTOCOL=TCP"));
            var tcps = tls != TlsMode.Disable;
            if (target.NoNameCheck && tcps)
            {
                tls = TlsMode.VerifyCa;
                notes.Add(WeakTls.HostNotChecked("SSL_SERVER_DN_MATCH=FALSE"));
            }
            notes.AddRange(target.Notes);
            p = p with
            {
                Host = target.Host, Port = target.Port == (tcps ? TcpsPort : TcpPort) ? null : target.Port,
                DatabaseDefault = target.Service, TlsMode = tls, Options = options.Count == 0 ? null : options,
            };
        }
        if (k.Has("User Id") && !string.IsNullOrEmpty(b.UserID))
        {
            if (!ValidUser(b.UserID)) throw new ArgumentException("User Id cannot be \"/\" (OS sign-in) or a proxy user (name[client]); give the database user itself.");
            p = p with { Username = b.UserID };
        }
        return new ParseConnectionStringResult
        {
            Profile = p, Secret = k.Has("Password") && !string.IsNullOrEmpty(b.Password) ? b.Password : null,
            Unknown = [.. k.Unknown], NotApplied = [.. notApplied], Notes = [.. notes],
        };
    }

    /// <param name="Tcps">True / false when the string names the protocol (tcps:// or tcp://, PROTOCOL=…); null when it does not.</param>
    internal sealed record Target(string Host, int? Port, string Service, bool Sid, bool? Tcps, bool NoNameCheck, IReadOnlyList<string> Notes);

    /// <summary>
    /// Easy Connect (<c>[tcps://]host[:port]/service</c>, with or without a leading <c>//</c>) or a connect descriptor with one
    /// address. A bare name is a tnsnames.ora alias, which Querithm does not read (O1): refused with that reason.
    /// </summary>
    internal static Target ParseDataSource(string dataSource)
    {
        var ds = dataSource.Trim();
        return ds.StartsWith('(') ? ParseDescriptor(ds) : ParseEasyConnect(ds);
    }

    static Target ParseEasyConnect(string ds)
    {
        bool? tcps = null;
        var notes = new List<string>();
        var at = ds.IndexOf("://", StringComparison.Ordinal);
        if (at >= 0)
        {
            var scheme = ds[..at];
            tcps = scheme.Equals("tcps", StringComparison.OrdinalIgnoreCase);
            if (tcps == false && !scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"The protocol {scheme} is not supported; use tcps:// or tcp://.");
            ds = ds[(at + 3)..];
        }
        else if (ds.StartsWith("//", StringComparison.Ordinal)) ds = ds[2..];
        var q = ds.IndexOf('?');
        if (q >= 0)
        {
            notes.Add("The Easy Connect parameters after \"?\" are not used; Querithm sets the connection's options itself.");
            ds = ds[..q];
        }
        var slash = ds.IndexOf('/');
        if (slash < 0)
            throw new ArgumentException($"\"{ds}\" looks like a tnsnames.ora alias. Querithm does not read tnsnames.ora: give host, port and service (host:1521/SERVICE).");
        var hostPort = ds[..slash];
        var rest = ds[(slash + 1)..];
        // service[:server][/instance]: the server type and instance are not kept.
        var colon = rest.IndexOf(':');
        var instance = rest.IndexOf('/');
        var cut = new[] { colon, instance }.Where(i => i >= 0).DefaultIfEmpty(rest.Length).Min();
        if (cut < rest.Length) notes.Add("The server type or instance name after the service is not used.");
        var service = rest[..cut];
        string host;
        int? port = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0) throw new ArgumentException("The IPv6 address is missing its closing \"]\".");
            host = hostPort[1..close];
            if (close + 1 < hostPort.Length && hostPort[close + 1] == ':') port = ParsePort(hostPort[(close + 2)..]);
        }
        else
        {
            var c = hostPort.LastIndexOf(':');
            host = c >= 0 ? hostPort[..c] : hostPort;
            if (c >= 0) port = ParsePort(hostPort[(c + 1)..]);
        }
        if (host.Contains(',')) throw new ArgumentException("This connection string names several hosts; Querithm connects to one server: keep the one you mean.");
        Check(host, service);
        return new Target(host, port, service, false, tcps, false, notes);
    }

    static int ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 65535 ? n : throw new ArgumentException($"\"{text}\" is not a port number.");

    static void Check(string host, string service)
    {
        if (!ValidHost(host)) throw new ArgumentException("The host must be a server name or an IP address.");
        if (!ValidService(service)) throw new ArgumentException("The service name may hold letters, digits and _ . $ # - only.");
    }

    /// <summary>A parsed descriptor clause: its name and either a value or child clauses.</summary>
    sealed record Node(string Name, string? Value, List<Node> Children);

    /// <summary>
    /// A connect descriptor, read as nested (NAME=value) clauses (not SQL: the descriptor grammar). Exactly one ADDRESS;
    /// the protocol, host, port, SERVICE_NAME or SID and SSL_SERVER_DN_MATCH are kept, every other clause is named in a note.
    /// </summary>
    static Target ParseDescriptor(string ds)
    {
        var pos = 0;
        var root = ReadNode(ds, ref pos);
        SkipSpace(ds, ref pos);
        if (pos != ds.Length) throw new ArgumentException("The connect descriptor has text after its closing parenthesis.");
        if (!root.Name.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(root.Name.Equals("DESCRIPTION_LIST", StringComparison.OrdinalIgnoreCase)
                ? "This descriptor lists several descriptions; Querithm connects to one server: keep the one you mean."
                : "A connect descriptor starts with (DESCRIPTION=…).");
        var addresses = All(root, "ADDRESS").ToList();
        if (addresses.Count != 1)
            throw new ArgumentException(addresses.Count == 0 ? "The connect descriptor has no ADDRESS." : $"The connect descriptor names {addresses.Count} addresses; Querithm connects to one server: keep the one you mean.");
        var a = addresses[0];
        var protocol = Child(a, "PROTOCOL");
        bool? tcps = protocol == null ? null : protocol.Equals("TCPS", StringComparison.OrdinalIgnoreCase);
        if (tcps == false && !protocol!.Equals("TCP", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"The protocol {protocol} is not supported; use TCPS or TCP.");
        var host = Child(a, "HOST") ?? throw new ArgumentException("The ADDRESS has no HOST.");
        int? port = Child(a, "PORT") is { } pt ? ParsePort(pt) : null;
        var data = root.Children.FirstOrDefault(c => c.Name.Equals("CONNECT_DATA", StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("The connect descriptor has no CONNECT_DATA.");
        var serviceName = Child(data, "SERVICE_NAME");
        var sid = Child(data, "SID");
        var service = serviceName ?? sid ?? throw new ArgumentException("CONNECT_DATA names neither SERVICE_NAME nor SID.");
        var noNameCheck = All(root, "SSL_SERVER_DN_MATCH").Any(n => n.Value is { } v && (v.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || v.Equals("OFF", StringComparison.OrdinalIgnoreCase) || v.Equals("NO", StringComparison.OrdinalIgnoreCase)));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESCRIPTION", "ADDRESS", "ADDRESS_LIST", "PROTOCOL", "HOST", "PORT", "CONNECT_DATA", "SERVICE_NAME", "SID", "SECURITY", "SSL_SERVER_DN_MATCH" };
        var others = Flatten(root).Select(n => n.Name.ToUpperInvariant()).Where(n => !known.Contains(n)).Distinct().ToList();
        var notes = new List<string>();
        if (others.Count > 0) notes.Add($"These descriptor settings are not used (Querithm writes the descriptor itself): {string.Join(", ", others)}.");
        Check(host, service);
        return new Target(host, port, service, serviceName == null, tcps, noNameCheck, notes);
    }

    static string? Child(Node n, string name) => n.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    static IEnumerable<Node> Flatten(Node n) => n.Children.SelectMany(Flatten).Prepend(n);

    static IEnumerable<Node> All(Node n, string name) => Flatten(n).Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    const int MaxDepth = 16;

    static Node ReadNode(string s, ref int pos, int depth = 0)
    {
        if (depth > MaxDepth) throw new ArgumentException("The connect descriptor is nested too deeply.");
        SkipSpace(s, ref pos);
        if (pos >= s.Length || s[pos] != '(') throw new ArgumentException("The connect descriptor is malformed (a clause must start with \"(\").");
        pos++;
        var start = pos;
        while (pos < s.Length && s[pos] is not ('=' or '(' or ')')) pos++;
        if (pos >= s.Length || s[pos] != '=') throw new ArgumentException("The connect descriptor is malformed (a clause must be NAME=value).");
        var name = s[start..pos].Trim();
        pos++;
        SkipSpace(s, ref pos);
        var children = new List<Node>();
        string? value = null;
        if (pos < s.Length && s[pos] == '(')
        {
            while (true)
            {
                SkipSpace(s, ref pos);
                if (pos < s.Length && s[pos] == '(') { children.Add(ReadNode(s, ref pos, depth + 1)); continue; }
                break;
            }
        }
        else
        {
            var v = pos;
            if (pos < s.Length && s[pos] == '"')
            {
                pos++;
                while (pos < s.Length && s[pos] != '"') pos++;
                if (pos >= s.Length) throw new ArgumentException("The connect descriptor has an unclosed quote.");
                value = s[(v + 1)..pos];
                pos++;
            }
            else
            {
                while (pos < s.Length && s[pos] is not ('(' or ')')) pos++;
                value = s[v..pos].Trim();
            }
        }
        SkipSpace(s, ref pos);
        if (pos >= s.Length || s[pos] != ')') throw new ArgumentException("The connect descriptor is malformed (a clause is not closed).");
        pos++;
        return new Node(name, value, children);
    }
}
